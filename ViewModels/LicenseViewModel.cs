using Pulse.Helpers;
using Pulse.Models;
using Pulse.Services;

namespace Pulse.ViewModels;

public sealed class LicenseViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;
    private string _licenseKey = string.Empty;
    private string _error = string.Empty;
    private bool _rememberMe;
    private bool _isBusy;
    private int _generation;

    public LicenseViewModel(ShellViewModel shell)
    {
        _shell = shell;
        ActivateCommand = new AsyncRelayCommand(
            ActivateAsync,
            _ => !IsBusy && IsValidLicenseKey(LicenseKey.Trim()));
    }

    public string DeviceId => _shell.DeviceId;

    public string LicenseKey
    {
        get => _licenseKey;
        set
        {
            if (SetProperty(ref _licenseKey, value))
            {
                ActivateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public void SetError(string message) => Error = message;

    public bool RememberMe
    {
        get => _rememberMe;
        set => SetProperty(ref _rememberMe, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                ActivateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncRelayCommand ActivateCommand { get; }

    private async Task ActivateAsync(object? _, CancellationToken cancellationToken)
    {
        if (!IsValidLicenseKey(LicenseKey.Trim()))
        {
            Error = Localization.Get("InvalidKeyFormat");
            return;
        }

        IsBusy = true;
        Error = string.Empty;
        var generation = ++_generation;
        try
        {
            var result = await _shell.AuthenticateLicenseKeyAsync(
                LicenseKey.Trim(),
                cancellationToken);

            if (generation != _generation)
            {
                return;
            }

            if (result.IsSuccess)
            {
                await _shell.CompleteLicenseLoginAsync(result.Session!, RememberMe, cancellationToken);
                LicenseKey = string.Empty;
                return;
            }

            Error = _shell.LicenseFailureText(result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _shell.SetLicenseError($"{Localization.Get("SessionStorageFailed")}: {ex.Message}");
        }
        finally
        {
            if (generation == _generation)
            {
                IsBusy = false;
            }
        }
    }

    private static bool IsValidLicenseKey(string key) =>
        key.Length is > 20 and <= 1024
        && key.StartsWith("PULSE1.", StringComparison.Ordinal)
        && key.Count(character => character == '.') == 2;
}
