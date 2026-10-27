using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Pulse.LicensingCore;

var configuration = ServerConfiguration.Load();
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
builder.Services.AddSingleton(configuration);
builder.Services.AddSingleton(new LicenseRequestStore(configuration.DataFile));
builder.Services.AddSingleton<AdminSessionStore>();
builder.Services.AddHostedService<LicenseRequestRetentionService>();
builder.Services.AddCors(options => options.AddPolicy("website", policy =>
    policy.WithOrigins(configuration.AllowedOrigin)
        .WithMethods("GET", "POST", "DELETE")
        .WithHeaders("Content-Type", "Authorization", "X-Request-Token")));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("public-request", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(30),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("admin-login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(15),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

var app = builder.Build();
app.UseCors("website");
app.UseRateLimiter();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/license-requests", async (
    CreateLicenseRequestInput input,
    LicenseRequestStore store,
    CancellationToken cancellationToken) =>
{
    if (!OfflineLicenseCodec.IsValidDeviceId(input.DeviceId))
    {
        return Results.BadRequest(new { error = "Enter the 32-character device ID shown in PULSE." });
    }

    var receipt = await store.CreateAsync(input.DeviceId!.ToUpperInvariant(), cancellationToken);
    return Results.Created($"/api/license-requests/{receipt.RequestId}", receipt);
}).RequireRateLimiting("public-request");

app.MapGet("/api/license-requests/{requestId}", async (
    string requestId,
    HttpContext context,
    LicenseRequestStore store,
    CancellationToken cancellationToken) =>
{
    var requestToken = context.Request.Headers["X-Request-Token"].ToString();
    var request = await store.GetForUserAsync(requestId, requestToken, cancellationToken);
    return request is null
        ? Results.NotFound(new { error = "The request receipt was not found." })
        : Results.Ok(new UserRequestStatus(request.Status, request.CreatedAt, request.LicenseType));
});

app.MapGet("/api/license-requests/{requestId}/download", async (
    string requestId,
    HttpContext context,
    LicenseRequestStore store,
    CancellationToken cancellationToken) =>
{
    var requestToken = context.Request.Headers["X-Request-Token"].ToString();
    var request = await store.GetForUserAsync(requestId, requestToken, cancellationToken);
    if (request is null)
    {
        return Results.NotFound(new { error = "The request receipt was not found." });
    }

    if (request.Status != LicenseRequestStates.Issued || request.LicenseCode is null)
    {
        return Results.Conflict(new { error = "This license is not ready to download yet." });
    }

    var fileName = $"PULSE-{request.Id[..8]}.pulse-license";
    return Results.File(
        Encoding.UTF8.GetBytes(request.LicenseCode + Environment.NewLine),
        "application/vnd.pulse.license",
        fileName);
});

app.MapPost("/api/admin/login", (
    AdminLoginInput input,
    HttpContext context,
    ServerConfiguration settings,
    AdminSessionStore sessions) =>
{
    var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(input.Password ?? string.Empty));
    var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(settings.AdminPassword));
    if (!CryptographicOperations.FixedTimeEquals(suppliedHash, configuredHash))
    {
        return Results.Unauthorized();
    }

    var token = sessions.Create();
    return Results.Ok(new { token, expiresInSeconds = (int)AdminSessionStore.Lifetime.TotalSeconds });
}).RequireRateLimiting("admin-login");

app.MapGet("/api/admin/requests", async (
    HttpContext context,
    LicenseRequestStore store,
    AdminSessionStore sessions,
    CancellationToken cancellationToken) =>
{
    if (!sessions.IsAuthorized(context))
    {
        return Results.Unauthorized();
    }

    var requests = await store.ListAsync(cancellationToken);
    return Results.Ok(requests.Select(LicenseRequestSummary.From));
});

app.MapPost("/api/admin/requests/{requestId}/prepare", async (
    string requestId,
    PrepareLicenseInput input,
    HttpContext context,
    LicenseRequestStore store,
    AdminSessionStore sessions,
    CancellationToken cancellationToken) =>
{
    if (!sessions.IsAuthorized(context))
    {
        return Results.Unauthorized();
    }

    if (!Guid.TryParseExact(requestId, "N", out _)
        || input.LicenseType is not ("seven_day" or "thirty_day" or "permanent"))
    {
        return Results.BadRequest(new { error = "Choose a supported license term." });
    }

    var prepared = await store.PrepareAsync(requestId, input.LicenseType, cancellationToken);
    return prepared is null
        ? Results.NotFound(new { error = "The request was not found or was already issued." })
        : Results.Ok(LicenseRequestSummary.From(prepared));
});

app.MapPost("/api/admin/requests/{requestId}/license", async (
    string requestId,
    UploadLicenseInput input,
    HttpContext context,
    LicenseRequestStore store,
    AdminSessionStore sessions,
    ServerConfiguration settings,
    CancellationToken cancellationToken) =>
{
    if (!sessions.IsAuthorized(context))
    {
        return Results.Unauthorized();
    }

    if (!Guid.TryParseExact(requestId, "N", out _)
        || string.IsNullOrWhiteSpace(input.LicenseCode)
        || input.LicenseCode.Length > 1024)
    {
        return Results.BadRequest(new { error = "Provide a valid signed PULSE license code." });
    }

    var request = await store.GetForAdminAsync(requestId, cancellationToken);
    if (request is null || request.Status != LicenseRequestStates.AwaitingSignature)
    {
        return Results.Conflict(new { error = "Prepare this request before uploading a license." });
    }

    using var verifier = ECDsa.Create();
    verifier.ImportFromPem(settings.PublicKeyPem);
    var failure = OfflineLicenseCodec.Validate(
        verifier,
        input.LicenseCode.Trim(),
        request.DeviceId,
        DateTimeOffset.UtcNow,
        out var payload);
    if (failure != OfflineLicenseFailure.None || payload is null)
    {
        return Results.BadRequest(new
        {
            error = failure == OfflineLicenseFailure.Expired
                ? "The signed license has already expired."
                : "The license signature or device ID is invalid."
        });
    }

    if (payload.Type != request.LicenseType)
    {
        return Results.BadRequest(new { error = "The signed license term does not match the selected term." });
    }

    var issued = await store.IssueAsync(requestId, payload.DeviceId, payload.Type, input.LicenseCode.Trim(), cancellationToken);
    return issued
        ? Results.Ok(new { status = LicenseRequestStates.Issued })
        : Results.Conflict(new { error = "The request changed before the license could be saved. Refresh and retry." });
});

app.MapDelete("/api/admin/requests/{requestId}", async (
    string requestId,
    HttpContext context,
    LicenseRequestStore store,
    AdminSessionStore sessions,
    CancellationToken cancellationToken) =>
{
    if (!sessions.IsAuthorized(context))
    {
        return Results.Unauthorized();
    }

    if (!Guid.TryParseExact(requestId, "N", out _))
    {
        return Results.NotFound();
    }

    var deleted = await store.DeleteAsync(requestId, cancellationToken);
    return deleted ? Results.NoContent() : Results.NotFound();
});

app.Run();

internal sealed record CreateLicenseRequestInput(string? DeviceId);
internal sealed record AdminLoginInput(string? Password);
internal sealed record PrepareLicenseInput(string? LicenseType);
internal sealed record UploadLicenseInput(string? LicenseCode);
internal sealed record UserRequestStatus(string Status, DateTimeOffset CreatedAt, string? LicenseType);

internal sealed record LicenseRequestSummary(
    string Id,
    string DeviceId,
    string Status,
    string? LicenseType,
    DateTimeOffset CreatedAt,
    DateTimeOffset? IssuedAt)
{
    public static LicenseRequestSummary From(LicenseRequest request) =>
        new(request.Id, request.DeviceId, request.Status, request.LicenseType, request.CreatedAt, request.IssuedAt);
}

internal static class LicenseRequestStates
{
    public const string Pending = "pending";
    public const string AwaitingSignature = "awaiting_signature";
    public const string Issued = "issued";
}

internal sealed record LicenseRequest(
    string Id,
    string DeviceId,
    string RequestTokenHash,
    string Status,
    string? LicenseType,
    string? LicenseCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? IssuedAt);

internal sealed record LicenseRequestReceipt(string RequestId, string RequestToken, string Status, DateTimeOffset CreatedAt);

internal sealed class ServerConfiguration
{
    public required string AdminPassword { get; init; }
    public required string AllowedOrigin { get; init; }
    public required string DataFile { get; init; }
    public required string PublicKeyPem { get; init; }

    public static ServerConfiguration Load()
    {
        var password = Environment.GetEnvironmentVariable("PULSE_ADMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(password) || password.Length < 24)
        {
            throw new InvalidOperationException("Set PULSE_ADMIN_PASSWORD to a unique secret of at least 24 characters.");
        }

        var origin = Environment.GetEnvironmentVariable("PULSE_ALLOWED_ORIGIN");
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var allowedOrigin)
            || (allowedOrigin.Scheme != Uri.UriSchemeHttps && !allowedOrigin.IsLoopback))
        {
            throw new InvalidOperationException("Set PULSE_ALLOWED_ORIGIN to the HTTPS website origin.");
        }

        var dataDirectory = Environment.GetEnvironmentVariable("PULSE_LICENSE_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new InvalidOperationException("Set PULSE_LICENSE_DATA_DIR to persistent storage.");
        }

        Directory.CreateDirectory(dataDirectory);
        var publicKeyPath = Environment.GetEnvironmentVariable("PULSE_LICENSE_PUBLIC_KEY_PATH");
        if (string.IsNullOrWhiteSpace(publicKeyPath) || !File.Exists(publicKeyPath))
        {
            throw new InvalidOperationException("The PULSE public-key PEM file is missing.");
        }

        return new ServerConfiguration
        {
            AdminPassword = password,
            AllowedOrigin = allowedOrigin.GetLeftPart(UriPartial.Authority),
            DataFile = Path.Combine(dataDirectory, "license-requests.json"),
            PublicKeyPem = File.ReadAllText(publicKeyPath)
        };
    }
}

internal sealed class AdminSessionStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);

    public string Create()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var session in _sessions.Where(session => session.Value <= now))
        {
            _sessions.TryRemove(session.Key, out _);
        }

        var token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        _sessions[Hash(token)] = now.Add(Lifetime);
        return token;
    }

    public bool IsAuthorized(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return false;
        }

        var hash = Hash(authorization["Bearer ".Length..]);
        if (!_sessions.TryGetValue(hash, out var expiresAt))
        {
            return false;
        }

        if (expiresAt > DateTimeOffset.UtcNow)
        {
            return true;
        }

        _sessions.TryRemove(hash, out _);
        return false;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed class LicenseRequestStore
{
    private const int DatabaseVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(180);
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<LicenseRequest> _requests;

    public LicenseRequestStore(string filePath)
    {
        _filePath = filePath;
        _requests = Load();
    }

    public async Task<LicenseRequestReceipt> CreateAsync(string deviceId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _requests.RemoveAll(request => request.CreatedAt <= DateTimeOffset.UtcNow - Retention);
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var now = DateTimeOffset.UtcNow;
            var request = new LicenseRequest(
                Guid.NewGuid().ToString("N"),
                deviceId,
                HashToken(token),
                LicenseRequestStates.Pending,
                null,
                null,
                now,
                null);
            _requests.Add(request);
            if (_requests.Count > 5000)
            {
                _requests = _requests.OrderByDescending(item => item.CreatedAt).Take(5000).ToList();
            }

            await SaveAsync(cancellationToken);
            return new LicenseRequestReceipt(request.Id, token, request.Status, now);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LicenseRequest?> GetForUserAsync(string requestId, string token, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(requestId, "N", out _) || token.Length > 128)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var request = _requests.FirstOrDefault(item => item.Id == requestId);
            if (request is null)
            {
                return null;
            }

            var actualHash = Encoding.ASCII.GetBytes(HashToken(token));
            var expectedHash = Encoding.ASCII.GetBytes(request.RequestTokenHash);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash) ? request : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LicenseRequest>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _requests.OrderByDescending(item => item.CreatedAt).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LicenseRequest?> GetForAdminAsync(string requestId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _requests.FirstOrDefault(item => item.Id == requestId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LicenseRequest?> PrepareAsync(
        string requestId,
        string licenseType,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var index = _requests.FindIndex(item => item.Id == requestId && item.Status != LicenseRequestStates.Issued);
            if (index < 0)
            {
                return null;
            }

            _requests[index] = _requests[index] with
            {
                Status = LicenseRequestStates.AwaitingSignature,
                LicenseType = licenseType
            };
            await SaveAsync(cancellationToken);
            return _requests[index];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IssueAsync(
        string requestId,
        string deviceId,
        string licenseType,
        string licenseCode,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var index = _requests.FindIndex(item =>
                item.Id == requestId
                && item.Status == LicenseRequestStates.AwaitingSignature
                && item.DeviceId == deviceId
                && item.LicenseType == licenseType);
            if (index < 0)
            {
                return false;
            }

            _requests[index] = _requests[index] with
            {
                Status = LicenseRequestStates.Issued,
                LicenseCode = licenseCode,
                IssuedAt = DateTimeOffset.UtcNow
            };
            await SaveAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string requestId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var removed = _requests.RemoveAll(item => item.Id == requestId) > 0;
            if (removed)
            {
                await SaveAsync(cancellationToken);
            }

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveExpiredAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_requests.RemoveAll(request => request.CreatedAt <= DateTimeOffset.UtcNow - Retention) > 0)
            {
                await SaveAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<LicenseRequest> Load()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        var database = JsonSerializer.Deserialize<LicenseRequestDatabase>(File.ReadAllBytes(_filePath), JsonOptions)
            ?? throw new InvalidDataException("The license request database is empty or invalid.");
        if (database.Version != DatabaseVersion || database.Requests.Count > 5000
            || database.Requests.Any(request => !OfflineLicenseCodec.IsValidDeviceId(request.DeviceId)
                || !Guid.TryParseExact(request.Id, "N", out _)
                || request.RequestTokenHash.Length != 64
                || request.RequestTokenHash.Any(character => !Uri.IsHexDigit(character))
                || request.Status is not (LicenseRequestStates.Pending or LicenseRequestStates.AwaitingSignature or LicenseRequestStates.Issued)
                || (request.LicenseType is not null && request.LicenseType is not ("seven_day" or "thirty_day" or "permanent"))
                || (request.Status == LicenseRequestStates.Issued && request.LicenseCode is null)))
        {
            throw new InvalidDataException("The license request database failed its integrity checks.");
        }

        return database.Requests;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var temporaryPath = _filePath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new LicenseRequestDatabase(DatabaseVersion, _requests),
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

internal sealed record LicenseRequestDatabase(int Version, List<LicenseRequest> Requests);

internal sealed class LicenseRequestRetentionService(LicenseRequestStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await store.RemoveExpiredAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}
