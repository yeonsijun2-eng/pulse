namespace Pulse.Models;

public sealed class AppSettings
{
    public string? LastExecutablePath { get; set; }
    public string? LastRiotClientPath { get; set; }
    public string Language { get; set; } = "en";
    public bool AnimationsEnabled { get; set; } = true;
}
