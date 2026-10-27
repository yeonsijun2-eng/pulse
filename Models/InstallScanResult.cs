namespace Pulse.Models;

public enum InstallStatus
{
    Scanning,
    Found,
    NotFound,
    Invalid,
    Inaccessible,
    Running
}

public sealed record InstallScanResult
{
    public InstallStatus Status { get; init; }
    public string? ExecutablePath { get; init; }
    public string? RiotClientPath { get; init; }
    public string Message { get; init; } = string.Empty;

    public bool CanLaunch =>
        Status is InstallStatus.Found or InstallStatus.Running
        && !string.IsNullOrWhiteSpace(ExecutablePath);

    public bool IsInstalled => CanLaunch;

    public static InstallScanResult Scanning() => new()
    {
        Status = InstallStatus.Scanning,
        Message = "Scanning VALORANT installation…"
    };

    public static InstallScanResult NotFound(string? message = null) => new()
    {
        Status = InstallStatus.NotFound,
        Message = message ?? "VALORANT was not found. Install it, then retry scan or browse to the executable."
    };

    public static InstallScanResult Invalid(string message) => new()
    {
        Status = InstallStatus.Invalid,
        Message = message
    };

    public static InstallScanResult Inaccessible(string path) => new()
    {
        Status = InstallStatus.Inaccessible,
        ExecutablePath = path,
        Message = "The selected path exists but cannot be accessed."
    };

    public static InstallScanResult Found(string executablePath, string? riotClientPath, bool running) => new()
    {
        Status = running ? InstallStatus.Running : InstallStatus.Found,
        ExecutablePath = executablePath,
        RiotClientPath = riotClientPath,
        Message = running
            ? "VALORANT process is running."
            : "VALORANT installation verified."
    };
}
