using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Pulse.Models;

namespace Pulse.Services;

public sealed class ValorantPathService
{
    public const string OfficialDownloadUrl = "https://playvalorant.com/ko-kr/";

    private static readonly string[] AcceptedFileNames =
    [
        "VALORANT.exe",
        "VALORANT-Win64-Shipping.exe",
        "RiotClientServices.exe"
    ];

    private static readonly string[] GameProcessNames =
    [
        "VALORANT-Win64-Shipping",
        "VALORANT"
    ];

    public Task<InstallScanResult> ScanAsync(string? savedPath, CancellationToken cancellationToken) =>
        Task.Run(() => Scan(savedPath, cancellationToken), cancellationToken);

    public Task<InstallScanResult> ValidatePathAsync(string? path, CancellationToken cancellationToken) =>
        Task.Run(() => ValidatePath(path), cancellationToken);

    public Task<bool> IsValorantRunningAsync(CancellationToken cancellationToken) =>
        Task.Run(IsValorantRunning, cancellationToken);

    public InstallScanResult ValidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return InstallScanResult.Invalid("Path is empty. Browse to VALORANT.exe or VALORANT-Win64-Shipping.exe.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch
        {
            return InstallScanResult.Invalid("The path format is not valid.");
        }

        if (!Path.IsPathRooted(fullPath))
        {
            return InstallScanResult.Invalid("An absolute path is required.");
        }

        if (Directory.Exists(fullPath))
        {
            var nested = FindExecutableUnder(fullPath);
            if (nested is null)
            {
                return InstallScanResult.Invalid("No VALORANT executable was found in that folder.");
            }

            fullPath = nested;
        }

        if (!File.Exists(fullPath))
        {
            return InstallScanResult.Invalid("The file does not exist.");
        }

        var isRiotClient = IsRiotClient(fullPath);
        var isGame = IsGameExecutable(fullPath);
        if (!isRiotClient && !isGame)
        {
            return InstallScanResult.Invalid("That file is not a VALORANT or Riot Client executable.");
        }

        if (!CanAccessFile(fullPath))
        {
            return InstallScanResult.Inaccessible(fullPath);
        }

        var gamePath = isGame ? fullPath : RelatedValorantPaths(fullPath)
            .FirstOrDefault(candidate => IsGameExecutable(candidate) && File.Exists(candidate) && CanAccessFile(candidate));

        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return InstallScanResult.NotFound(
                "Riot Client was found, but VALORANT game files are missing. Install VALORANT, then retry scan.");
        }

        if (!CanAccessFile(gamePath))
        {
            return InstallScanResult.Inaccessible(gamePath);
        }

        var riotClient = isRiotClient ? fullPath : ResolveRiotClientPath(gamePath);
        return InstallScanResult.Found(gamePath, riotClient, IsValorantRunning());
    }

    public void OpenOfficialDownloadPage()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = OfficialDownloadUrl,
            UseShellExecute = true
        });
    }

    public InstallScanResult Launch(InstallScanResult current)
    {
        if (!current.CanLaunch || string.IsNullOrWhiteSpace(current.ExecutablePath))
        {
            return InstallScanResult.Invalid("A verified VALORANT executable is required before launch.");
        }

        var verified = ValidatePath(current.ExecutablePath);
        if (!verified.CanLaunch)
        {
            return verified;
        }

        var target = verified.RiotClientPath;
        var args = string.Empty;
        if (!string.IsNullOrWhiteSpace(target) && File.Exists(target) && CanAccessFile(target))
        {
            args = "--launch-product=valorant --launch-patchline=live";
        }
        else
        {
            target = verified.ExecutablePath!;
        }

        if (!CanAccessFile(target))
        {
            return InstallScanResult.Inaccessible(target);
        }

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = target,
                Arguments = args,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(target) ?? string.Empty
            };
            var process = Process.Start(info);
            if (process is null)
            {
                return InstallScanResult.Invalid("The process could not be started.");
            }
        }
        catch (Exception ex)
        {
            return InstallScanResult.Invalid($"Launch failed: {ex.Message}");
        }

        return verified with { Status = InstallStatus.Found, Message = "Launch requested." };
    }

    private InstallScanResult Scan(string? savedPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(savedPath))
        {
            var saved = ValidatePath(savedPath);
            if (saved.CanLaunch)
            {
                return saved;
            }
        }

        foreach (var candidate in EnumerateCandidates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = ValidatePath(candidate);
            if (result.CanLaunch)
            {
                return result;
            }
        }

        return InstallScanResult.NotFound();
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        foreach (var path in ReadRiotClientInstalls())
        {
            yield return path;
        }

        foreach (var path in ReadProductMetadata())
        {
            yield return path;
        }

        foreach (var path in ReadUninstallRegistry())
        {
            yield return path;
        }

        foreach (var drive in SafeGetDrives())
        {
            yield return Path.Combine(drive, "Riot Games", "VALORANT", "live", "VALORANT.exe");
            yield return Path.Combine(drive, "Riot Games", "VALORANT", "live", "ShooterGame", "Binaries", "Win64", "VALORANT-Win64-Shipping.exe");
            yield return Path.Combine(drive, "Riot Games", "Riot Client", "RiotClientServices.exe");
            yield return Path.Combine(drive, "Program Files", "Riot Games", "VALORANT", "live", "VALORANT.exe");
            yield return Path.Combine(drive, "Program Files (x86)", "Riot Games", "VALORANT", "live", "VALORANT.exe");
        }
    }

    private static IEnumerable<string> ReadRiotClientInstalls()
    {
        var jsonPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Riot Games",
            "RiotClientInstalls.json");

        if (!File.Exists(jsonPath) || !CanAccessFile(jsonPath))
        {
            yield break;
        }

        string json;
        try
        {
            json = File.ReadAllText(jsonPath);
        }
        catch
        {
            yield break;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch
        {
            yield break;
        }

        using (document)
        {
            var root = document.RootElement;
            foreach (var key in new[] { "rc_default", "rc_live", "rc_beta" })
            {
                if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var path = value.GetString();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        yield return path;
                        foreach (var related in RelatedValorantPaths(path))
                        {
                            yield return related;
                        }
                    }
                }
            }

            if (root.TryGetProperty("associated_client", out var associated) && associated.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in associated.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var related in RelatedValorantPaths(property.Name))
                    {
                        yield return related;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> ReadProductMetadata()
    {
        var metadataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Riot Games",
            "Metadata",
            "valorant.live");

        if (!Directory.Exists(metadataDir))
        {
            yield break;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(metadataDir, "*.yaml");
        }
        catch
        {
            yield break;
        }

        var pathRegex = new Regex(
            @"product_install(?:_full)?_path\s*:\s*[""']?([^""'\r\n]+)[""']?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            var match = pathRegex.Match(text);
            if (!match.Success)
            {
                continue;
            }

            var installPath = match.Groups[1].Value.Trim().Replace('/', Path.DirectorySeparatorChar);
            yield return Path.Combine(installPath, "VALORANT.exe");
            yield return Path.Combine(installPath, "ShooterGame", "Binaries", "Win64", "VALORANT-Win64-Shipping.exe");
            yield return installPath;
        }
    }

    private static IEnumerable<string> ReadUninstallRegistry()
    {
        string[] roots =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        ];

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var root in roots)
            {
                RegistryKey? key;
                try
                {
                    key = hive.OpenSubKey(root);
                }
                catch
                {
                    continue;
                }

                if (key is null)
                {
                    continue;
                }

                using (key)
                {
                    string[] names;
                    try
                    {
                        names = key.GetSubKeyNames();
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var subName in names)
                    {
                        if (!subName.Contains("valorant", StringComparison.OrdinalIgnoreCase)
                            && !subName.Contains("riot", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        using var sub = key.OpenSubKey(subName);
                        var location = sub?.GetValue("InstallLocation") as string
                                       ?? sub?.GetValue("DisplayIcon") as string;
                        if (!string.IsNullOrWhiteSpace(location))
                        {
                            yield return location.Trim('"');
                        }
                    }
                }
            }
        }
    }

    private static IEnumerable<string> RelatedValorantPaths(string riotClientPath)
    {
        var directory = Path.GetDirectoryName(riotClientPath);
        var riotGamesRoot = directory is null ? null : Directory.GetParent(directory)?.FullName;
        if (string.IsNullOrWhiteSpace(riotGamesRoot))
        {
            yield break;
        }

        yield return Path.Combine(riotGamesRoot, "VALORANT", "live", "VALORANT.exe");
        yield return Path.Combine(riotGamesRoot, "VALORANT", "live", "ShooterGame", "Binaries", "Win64", "VALORANT-Win64-Shipping.exe");
    }

    private static string? FindExecutableUnder(string directory)
    {
        var preferred = new[]
        {
            Path.Combine(directory, "VALORANT.exe"),
            Path.Combine(directory, "live", "VALORANT.exe"),
            Path.Combine(directory, "ShooterGame", "Binaries", "Win64", "VALORANT-Win64-Shipping.exe"),
            Path.Combine(directory, "RiotClientServices.exe")
        };

        foreach (var path in preferred)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        try
        {
            return Directory
                .EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(IsAcceptedExecutable);
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveRiotClientPath(string executablePath)
    {
        foreach (var candidate in ReadRiotClientInstalls())
        {
            if (IsRiotClient(candidate) && File.Exists(candidate) && CanAccessFile(candidate))
            {
                return candidate;
            }
        }

        var parent = Directory.GetParent(Path.GetDirectoryName(executablePath) ?? string.Empty);
        while (parent is not null)
        {
            var candidate = Path.Combine(parent.FullName, "Riot Client", "RiotClientServices.exe");
            if (File.Exists(candidate) && CanAccessFile(candidate))
            {
                return candidate;
            }

            parent = parent.Parent;
        }

        return null;
    }

    private static bool IsAcceptedExecutable(string path)
    {
        var name = Path.GetFileName(path);
        return AcceptedFileNames.Any(accepted => accepted.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGameExecutable(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("VALORANT.exe", StringComparison.OrdinalIgnoreCase)
               || name.Equals("VALORANT-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRiotClient(string path) =>
        Path.GetFileName(path).Equals("RiotClientServices.exe", StringComparison.OrdinalIgnoreCase);

    private static bool CanAccessFile(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.CanRead;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValorantRunning()
    {
        foreach (var name in GameProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                continue;
            }

            if (processes.Length > 0)
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }

                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> SafeGetDrives()
    {
        var roots = new List<string>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            return [Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\"];
        }

        foreach (var drive in drives)
        {
            try
            {
                if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
                {
                    roots.Add(drive.RootDirectory.FullName);
                }
            }
            catch
            {
                // Skip drives that throw on access.
            }
        }

        return roots;
    }
}
