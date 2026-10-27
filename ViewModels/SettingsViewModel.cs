using Pulse.Helpers;

namespace Pulse.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    public SettingsViewModel(ShellViewModel shell)
    {
        Shell = shell;
    }

    public ShellViewModel Shell { get; }

    public void Notify() => Raise(nameof(Shell));
}
