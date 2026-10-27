using System.Security.Cryptography;
using System.Text;
using Pulse.LicensingCore;

const string IssuerFolderName = "PULSE-License-Issuer";
var folder = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    IssuerFolderName);
var privateKeyPath = Path.Combine(folder, "signing-key.dpapi");
var publicKeyPath = Path.Combine(folder, "public-key.pem");
var entropy = Encoding.UTF8.GetBytes("PULSE.LicenseIssuer.SigningKey.v1");

if (args is ["--generate-keypair"])
{
    Directory.CreateDirectory(folder);
    if (File.Exists(privateKeyPath))
    {
        return Fail($"A signing key already exists at {privateKeyPath}. It will not be overwritten.");
    }

    using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var privateBytes = ecdsa.ExportPkcs8PrivateKey();
    try
    {
        var protectedBytes = ProtectedData.Protect(
            privateBytes,
            entropy,
            DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(privateKeyPath, protectedBytes);
        await File.WriteAllTextAsync(publicKeyPath, ecdsa.ExportSubjectPublicKeyInfoPem());
    }
    finally
    {
        CryptographicOperations.ZeroMemory(privateBytes);
    }

    Console.WriteLine($"Signing key created for this Windows user: {privateKeyPath}");
    Console.WriteLine($"Public key: {publicKeyPath}");
    Console.WriteLine("Copy public-key.pem into LicensingCore\\pulse-license-public-key.pem, then rebuild PULSE.");
    Console.WriteLine("Back up the DPAPI-protected signing key securely. Do not share it or put it in the app.");
    return 0;
}

if (args is ["--self-test"])
{
    if (!File.Exists(privateKeyPath))
    {
        return Fail($"Signing key not found at {privateKeyPath}. Run --generate-keypair first.");
    }

    var protectedBytes = await File.ReadAllBytesAsync(privateKeyPath);
    byte[] privateBytes;
    try
    {
        privateBytes = ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser);
    }
    catch (CryptographicException)
    {
        return Fail("The signing key cannot be opened by this Windows user.");
    }

    try
    {
        using var signer = ECDsa.Create();
        signer.ImportPkcs8PrivateKey(privateBytes, out _);
        using var publicKeyStream = typeof(OfflineLicenseCodec).Assembly
            .GetManifestResourceStream("Pulse.LicensingCore.PublicKey.pem")
            ?? throw new InvalidOperationException("The application public key is missing.");
        using var publicKeyReader = new StreamReader(publicKeyStream);
        using var verifier = ECDsa.Create();
        verifier.ImportFromPem(await publicKeyReader.ReadToEndAsync());

        const string testDeviceId = "0123456789ABCDEF0123456789ABCDEF";
        const string otherDeviceId = "FEDCBA9876543210FEDCBA9876543210";
        var issuedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var sevenDayKey = OfflineLicenseCodec.Create(signer, testDeviceId, "seven_day", issuedAt);
        var permanentKey = OfflineLicenseCodec.Create(signer, testDeviceId, "permanent", issuedAt);
        var thirtyDayKey = OfflineLicenseCodec.Create(signer, testDeviceId, "thirty_day", issuedAt);
        var valid = OfflineLicenseCodec.Validate(
            verifier, sevenDayKey, testDeviceId, issuedAt.AddDays(1), out var payload);
        var permanent = OfflineLicenseCodec.Validate(
            verifier, permanentKey, testDeviceId, issuedAt.AddYears(10), out var permanentPayload);
        var thirtyDay = OfflineLicenseCodec.Validate(
            verifier, thirtyDayKey, testDeviceId, issuedAt.AddDays(29), out var thirtyDayPayload);
        var thirtyDayExpired = OfflineLicenseCodec.Validate(
            verifier, thirtyDayKey, testDeviceId, issuedAt.AddDays(30), out _);
        var wrongDevice = OfflineLicenseCodec.Validate(
            verifier, sevenDayKey, otherDeviceId, issuedAt.AddDays(1), out _);
        var expired = OfflineLicenseCodec.Validate(
            verifier, sevenDayKey, testDeviceId, issuedAt.AddDays(7), out _);
        var signatureStart = sevenDayKey.LastIndexOf('.') + 1;
        var tamperedKey = sevenDayKey[..signatureStart]
            + (sevenDayKey[signatureStart] == 'A' ? 'B' : 'A')
            + sevenDayKey[(signatureStart + 1)..];
        var tampered = OfflineLicenseCodec.Validate(
            verifier, tamperedKey, testDeviceId, issuedAt.AddDays(1), out _);
        if (valid != OfflineLicenseFailure.None
            || payload?.Type != "seven_day"
            || permanent != OfflineLicenseFailure.None
            || permanentPayload?.Type != "permanent"
            || thirtyDay != OfflineLicenseFailure.None
            || thirtyDayPayload?.Type != "thirty_day"
            || thirtyDayExpired != OfflineLicenseFailure.Expired
            || wrongDevice != OfflineLicenseFailure.InvalidKey
            || expired != OfflineLicenseFailure.Expired
            || tampered != OfflineLicenseFailure.InvalidKey)
        {
            return Fail("Offline license self-test failed. Confirm the issuer private key matches the PULSE public key.");
        }

        Console.WriteLine("Issuer signature, app public-key match, device binding, expiry, and tamper checks passed.");
        return 0;
    }
    finally
    {
        CryptographicOperations.ZeroMemory(privateBytes);
        CryptographicOperations.ZeroMemory(protectedBytes);
    }
}

if (args.Length == 3 && args[0] == "--issue")
{
    var deviceId = args[1].ToUpperInvariant();
    var kind = args[2] switch
    {
        "7-day" => "seven_day",
        "30-day" => "thirty_day",
        "permanent" => "permanent",
        _ => null
    };
    if (!OfflineLicenseCodec.IsValidDeviceId(deviceId) || kind is null)
    {
        return Fail("Usage: PulseLicenseIssuer.exe --issue <DEVICE-ID> <7-day|30-day|permanent>");
    }

    if (!File.Exists(privateKeyPath))
    {
        return Fail($"Signing key not found at {privateKeyPath}. Run --generate-keypair first.");
    }

    var protectedBytes = await File.ReadAllBytesAsync(privateKeyPath);
    byte[] privateBytes;
    try
    {
        privateBytes = ProtectedData.Unprotect(
            protectedBytes,
            entropy,
            DataProtectionScope.CurrentUser);
    }
    catch (CryptographicException)
    {
        return Fail("The signing key cannot be opened by this Windows user. Run the issuer on the account where it was created.");
    }

    try
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(privateBytes, out _);
        Console.WriteLine(OfflineLicenseCodec.Create(ecdsa, deviceId, kind, DateTimeOffset.UtcNow));
        return 0;
    }
    finally
    {
        CryptographicOperations.ZeroMemory(privateBytes);
        CryptographicOperations.ZeroMemory(protectedBytes);
    }
}

Console.WriteLine("PULSE offline license issuer");
Console.WriteLine("  PulseLicenseIssuer.exe --generate-keypair");
Console.WriteLine("  PulseLicenseIssuer.exe --self-test");
Console.WriteLine("  PulseLicenseIssuer.exe --issue <DEVICE-ID> <7-day|30-day|permanent>");
return 2;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
