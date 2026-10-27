using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Pulse.Models;
using Pulse.LicensingCore;

namespace Pulse.Services;

public interface ILicenseAuthenticationService
{
    Task<LicenseAuthResult> ActivateAsync(string licenseKey, CancellationToken cancellationToken);
    Task<LicenseAuthResult> ValidateSessionAsync(string sessionToken, CancellationToken cancellationToken);
    Task RevokeSessionAsync(string sessionToken, CancellationToken cancellationToken);
}

public sealed class OfflineLicenseAuthenticationService : ILicenseAuthenticationService
{
    private static readonly string PublicKeyPem = LoadPublicKey();
    private readonly string _deviceId = DeviceIdentity.GetDeviceId();

    public string DeviceId => _deviceId;

    public Task<LicenseAuthResult> ActivateAsync(string licenseKey, CancellationToken cancellationToken) =>
        ValidateLicenseAsync(licenseKey, cancellationToken);

    public Task<LicenseAuthResult> ValidateSessionAsync(
        string sessionToken,
        CancellationToken cancellationToken) =>
        ValidateLicenseAsync(sessionToken, cancellationToken);

    public Task RevokeSessionAsync(string sessionToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private Task<LicenseAuthResult> ValidateLicenseAsync(
        string licenseCode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var verifier = ECDsa.Create();
        verifier.ImportFromPem(PublicKeyPem);
        var now = DateTimeOffset.UtcNow;
        var failure = OfflineLicenseCodec.Validate(
            verifier,
            licenseCode,
            _deviceId,
            now,
            out var payload);
        if (failure != OfflineLicenseFailure.None || payload is null)
        {
            var authFailure = failure == OfflineLicenseFailure.Expired
                ? LicenseAuthFailure.Expired
                : LicenseAuthFailure.InvalidKey;
            return Task.FromResult(new LicenseAuthResult(null, authFailure));
        }

        var kind = payload.Type switch
        {
            "seven_day" => LicenseKind.SevenDay,
            "thirty_day" => LicenseKind.ThirtyDay,
            "permanent" => LicenseKind.Permanent,
            _ => throw new InvalidOperationException("The license type is not supported.")
        };
        DateTimeOffset? expiry = kind == LicenseKind.Permanent
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt);
        var session = new AuthenticatedLicense(
            licenseCode,
            expiry ?? DateTimeOffset.MaxValue,
            new LicenseDetails(kind, LicenseState.Active, expiry, now));
        return Task.FromResult(new LicenseAuthResult(session, LicenseAuthFailure.None));
    }

    private static string LoadPublicKey()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(PulseLicensePublicKey.ResourceName)
            ?? throw new InvalidOperationException("The offline license public key is missing from the application.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed class LicenseSessionStore
{
    private static readonly byte[] Entropy = "PULSE.LicenseSession.v1"u8.ToArray();
    private static string SessionFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pulse",
        "license.session");

    public async Task<string?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(SessionFile))
        {
            return null;
        }

        try
        {
            var protectedToken = await File.ReadAllBytesAsync(SessionFile, cancellationToken);
            var token = ProtectedData.Unprotect(
                protectedToken,
                Entropy,
                DataProtectionScope.CurrentUser);
            try
            {
                return System.Text.Encoding.UTF8.GetString(token);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(token);
            }
        }
        catch (CryptographicException)
        {
            await DeleteAsync(cancellationToken);
            return null;
        }
    }

    public async Task SaveAsync(string sessionToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);
        var folder = Path.GetDirectoryName(SessionFile)
                     ?? throw new InvalidOperationException("The session folder could not be resolved.");
        Directory.CreateDirectory(folder);
        var tempFile = SessionFile + ".tmp";
        try
        {
            var clearToken = System.Text.Encoding.UTF8.GetBytes(sessionToken);
            try
            {
                var protectedToken = ProtectedData.Protect(
                    clearToken,
                    Entropy,
                    DataProtectionScope.CurrentUser);
                await File.WriteAllBytesAsync(tempFile, protectedToken, cancellationToken);
                File.Move(tempFile, SessionFile, overwrite: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clearToken);
            }
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(SessionFile))
        {
            File.Delete(SessionFile);
        }

        return Task.CompletedTask;
    }
}
