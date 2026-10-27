using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Pulse.LicensingCore;

public static class DeviceIdentity
{
    private const string RegistryPath = @"SOFTWARE\Microsoft\Cryptography";
    private const string RegistryValue = "MachineGuid";

    public static string GetDeviceId()
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(RegistryPath, writable: false);
        var machineGuid = key?.GetValue(RegistryValue) as string;
        if (string.IsNullOrWhiteSpace(machineGuid))
        {
            throw new InvalidOperationException("This Windows device does not provide a machine identifier.");
        }

        var source = Encoding.UTF8.GetBytes($"PULSE-OFFLINE-LICENSE-V1:{machineGuid.Trim().ToUpperInvariant()}");
        var hash = SHA256.HashData(source);
        try
        {
            return Convert.ToHexString(hash.AsSpan(0, 16));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(source);
            CryptographicOperations.ZeroMemory(hash);
        }
    }
}
