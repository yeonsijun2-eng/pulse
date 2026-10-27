using Pulse.Helpers;
using Pulse.Services;

namespace Pulse.Models;

public enum InjectorLogLevel
{
    Info,
    Success,
    Warning,
    Error
}

public sealed class InjectorLogEntry : ViewModelBase
{
    private readonly string _messageKey;

    public InjectorLogEntry(DateTimeOffset timestamp, InjectorLogLevel level, string messageKey, string? message = null)
    {
        Timestamp = timestamp;
        Level = level;
        _messageKey = messageKey;
        MessageOverride = message;
    }

    private string? MessageOverride { get; }
    public DateTimeOffset Timestamp { get; }
    public string TimestampText => Timestamp.ToString("HH:mm:ss");
    public InjectorLogLevel Level { get; }
    public string LevelLabel => Level.ToString().ToUpperInvariant();
    public string Message => MessageOverride ?? Localization.Get(_messageKey);

    public void RefreshLanguage() => Raise(nameof(Message));
}
