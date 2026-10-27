using System.IO;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows;
using Microsoft.Win32;
using Pulse.Helpers;
using PulseLocalization = Pulse.Services.Localization;

namespace Pulse.Views;

public partial class LicenseView : UserControl
{
    private CancellationTokenSource? _errorTransition;

    public LicenseView()
    {
        InitializeComponent();
        Unloaded += (_, _) => _errorTransition?.Cancel();
    }

    private async void ErrorMessage_OnTargetUpdated(object sender, DataTransferEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ErrorMessage.Text))
        {
            return;
        }

        _errorTransition?.Cancel();
        _errorTransition?.Dispose();
        var transition = new CancellationTokenSource();
        _errorTransition = transition;
        try
        {
            await Motion.FadeSlideAsync(ErrorMessage, true, true, transition.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_errorTransition, transition))
            {
                _errorTransition = null;
                transition.Dispose();
            }
        }
    }

    private void KeyInput_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox)
        {
            return;
        }

        e.Handled = true;
        if (DataContext is ViewModels.LicenseViewModel viewModel
            && viewModel.ActivateCommand.CanExecute(null))
        {
            viewModel.ActivateCommand.Execute(null);
        }
    }

    private async void ImportLicenseFile_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter = "PULSE license (*.pulse-license)|*.pulse-license|Text file (*.txt)|*.txt",
            Title = PulseLocalization.Get("ImportLicenseFile")
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var file = new FileInfo(dialog.FileName);
            if (file.Length is <= 0 or > 1024)
            {
                throw new InvalidDataException("The license file must contain a single PULSE license code.");
            }

            var licenseCode = (await File.ReadAllTextAsync(dialog.FileName)).Trim();
            if (licenseCode.Length is <= 20 or > 1024
                || !licenseCode.StartsWith("PULSE1.", StringComparison.Ordinal)
                || licenseCode.Count(character => character == '.') != 2)
            {
                throw new InvalidDataException("The selected file does not contain a valid PULSE license code.");
            }

            if (DataContext is ViewModels.LicenseViewModel viewModel)
            {
                viewModel.LicenseKey = licenseCode;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (DataContext is ViewModels.LicenseViewModel viewModel)
            {
                viewModel.SetError($"{PulseLocalization.Get("LicenseFileReadFailed")}: {exception.Message}");
            }
        }
    }
}
