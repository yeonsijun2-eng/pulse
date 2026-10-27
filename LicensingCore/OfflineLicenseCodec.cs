using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pulse.LicensingCore;

public enum OfflineLicenseFailure
{
    None,
    InvalidKey,
    Expired
}

public sealed record OfflineLicensePayload(
    int Version,
    string DeviceId,
    string Type,
    long IssuedAt,
    long ExpiresAt);

public static class OfflineLicenseCodec
{
    private const string Prefix = "PULSE1.";
    private const int MaximumKeyLength = 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Create(
        ECDsa privateKey,
        string deviceId,
        string type,
        DateTimeOffset issuedAt)
    {
        if (!IsValidDeviceId(deviceId))
        {
            throw new ArgumentException("The device ID is invalid.", nameof(deviceId));
        }

        if (type is not ("seven_day" or "thirty_day" or "permanent"))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        var issuedAtSeconds = issuedAt.ToUniversalTime().ToUnixTimeSeconds();
        var duration = type switch
        {
            "seven_day" => TimeSpan.FromDays(7),
            "thirty_day" => TimeSpan.FromDays(30),
            _ => TimeSpan.Zero
        };
        var expiresAtSeconds = duration == TimeSpan.Zero
            ? 0
            : issuedAt.ToUniversalTime().Add(duration).ToUnixTimeSeconds();
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new OfflineLicensePayload(1, deviceId, type, issuedAtSeconds, expiresAtSeconds),
            JsonOptions);
        var signature = privateKey.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{Prefix}{Base64UrlEncode(payload)}.{Base64UrlEncode(signature)}";
    }

    public static OfflineLicenseFailure Validate(
        ECDsa publicKey,
        string licenseCode,
        string expectedDeviceId,
        DateTimeOffset now,
        out OfflineLicensePayload? payload)
    {
        payload = null;
        if (licenseCode.Length > MaximumKeyLength)
        {
            return OfflineLicenseFailure.InvalidKey;
        }

        var parts = licenseCode.Split('.');
        if (parts.Length != 3
            || parts[0] != "PULSE1"
            || !TryBase64UrlDecode(parts[1], out var payloadBytes)
            || !TryBase64UrlDecode(parts[2], out var signature)
            || signature.Length != 64
            || !publicKey.VerifyData(
                payloadBytes,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            return OfflineLicenseFailure.InvalidKey;
        }

        try
        {
            payload = JsonSerializer.Deserialize<OfflineLicensePayload>(payloadBytes, JsonOptions);
        }
        catch (JsonException)
        {
            return OfflineLicenseFailure.InvalidKey;
        }

        if (payload is null
            || payload.Version != 1
            || !IsValidDeviceId(payload.DeviceId)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(payload.DeviceId),
                Encoding.ASCII.GetBytes(expectedDeviceId))
            || payload.Type is not ("seven_day" or "thirty_day" or "permanent")
            || payload.IssuedAt <= 0
            || payload.IssuedAt > now.ToUnixTimeSeconds() + 300
            || (payload.Type == "permanent" && payload.ExpiresAt != 0)
            || (payload.Type != "permanent"
                && payload.ExpiresAt != DateTimeOffset.FromUnixTimeSeconds(payload.IssuedAt)
                    .Add(payload.Type == "seven_day" ? TimeSpan.FromDays(7) : TimeSpan.FromDays(30))
                    .ToUnixTimeSeconds()))
        {
            payload = null;
            return OfflineLicenseFailure.InvalidKey;
        }

        if (payload.Type != "permanent" && now.ToUnixTimeSeconds() >= payload.ExpiresAt)
        {
            return OfflineLicenseFailure.Expired;
        }

        return OfflineLicenseFailure.None;
    }

    public static bool IsValidDeviceId(string? deviceId) =>
        deviceId is { Length: 32 }
        && deviceId.All(character =>
            character is >= '0' and <= '9'
            or >= 'A' and <= 'F');

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool TryBase64UrlDecode(string value, out byte[] decoded)
    {
        decoded = [];
        if (value.Length == 0 || value.Any(character =>
                !(character is >= 'A' and <= 'Z'
                    or >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '-'
                    or '_')))
        {
            return false;
        }

        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        try
        {
            decoded = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
