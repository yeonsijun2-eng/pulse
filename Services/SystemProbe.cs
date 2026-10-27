using System.Runtime.InteropServices;

namespace Pulse.Services;

public sealed class SystemSnapshot
{
    public string OperatingSystem { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public int ProcessorCount { get; init; }
    public string Framework { get; init; } = string.Empty;
    public double DpiScale { get; init; }
}

public static class SystemProbe
{
    public static Task<SystemSnapshot> CollectAsync(double dpiScale, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SystemSnapshot
            {
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.OSArchitecture.ToString(),
                ProcessorCount = Environment.ProcessorCount,
                Framework = RuntimeInformation.FrameworkDescription,
                DpiScale = dpiScale
            };
        }, cancellationToken);
    }
}
