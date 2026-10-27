using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pulse.LicensingCore;

namespace Pulse.LicenseIssuer.Desktop;

public partial class MainWindow : Window
{
    private const string IssuerFolderName = "PULSE-License-Issuer";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PULSE.LicenseIssuer.SigningKey.v1");
    private static string PrivateKeyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        IssuerFolderName,
        "signing-key.dpapi");

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_OnLoaded;
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        IssueButton.IsEnabled = false;
        SetKeyStatus("VERIFYING ISSUER KEY", "Checking local signing key against the PULSE app.", "#9EA3AD");
        try
        {
            var isValid = await Task.Run(VerifyIssuerKey);
            if (isValid)
            {
                SetKeyStatus("SIGNING KEY READY", "Issuer key matches the public key embedded in PULSE.", "#F4F5F7");
            }
            else
            {
                SetKeyStatus("SIGNING KEY UNAVAILABLE", "The signing key is missing or does not match this PULSE build.", "#E7A6A6");
            }
        }
        catch (Exception exception)
        {
            SetKeyStatus("SIGNING KEY ERROR", exception.Message, "#E7A6A6");
        }

        UpdateIssueButton();
    }

    private void DeviceIdInput_OnTextChanged(object sender, TextChangedEventArgs e) =>
        UpdateIssueButton();

    private async void IssueButton_OnClick(object sender, RoutedEventArgs e)
    {
        var deviceId = DeviceIdInput.Text.Trim().ToUpperInvariant();
        if (!OfflineLicenseCodec.IsValidDeviceId(deviceId))
        {
            FeedbackText.Text = "Enter a valid 32-character hexadecimal device ID.";
            return;
        }

        IssueButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        GeneratedKeyOutput.Clear();
        FeedbackText.Text = "Signing a device-bound license...";
        var licenseType = LicenseTypeInput.SelectedIndex switch
        {
            1 => "thirty_day",
            2 => "permanent",
            _ => "seven_day"
        };

        try
        {
            var licenseKey = await Task.Run(() => CreateLicense(deviceId, licenseType));
            GeneratedKeyOutput.Text = licenseKey;
            CopyButton.IsEnabled = true;
            FeedbackText.Text = licenseType switch
            {
                "thirty_day" => "30-day license generated for this device.",
                "permanent" => "Permanent license generated for this device.",
                _ => "7-day license generated for this device."
            };
        }
        catch (Exception exception)
        {
            FeedbackText.Text = $"License generation failed: {exception.Message}";
        }
        finally
        {
            UpdateIssueButton();
        }
    }

    private void CopyButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GeneratedKeyOutput.Text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(GeneratedKeyOutput.Text);
            FeedbackText.Text = "License key copied to clipboard.";
        }
        catch (System.Runtime.InteropServices.ExternalException exception)
        {
            FeedbackText.Text = $"Could not access the clipboard: {exception.Message}";
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void UpdateIssueButton()
    {
        IssueButton.IsEnabled = OfflineLicenseCodec.IsValidDeviceId(DeviceIdInput.Text.Trim())
            && KeyStatusTitle.Text == "SIGNING KEY READY";
    }

    private void SetKeyStatus(string title, string description, string color)
    {
        KeyStatusTitle.Text = title;
        KeyStatusDescription.Text = description;
        KeyStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        UpdateIssueButton();
    }

    private static bool VerifyIssuerKey()
    {
        if (!File.Exists(PrivateKeyPath))
        {
            return false;
        }

        var protectedBytes = File.ReadAllBytes(PrivateKeyPath);
        byte[] privateBytes;
        try
        {
            privateBytes = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            return false;
        }

        try
        {
            using var signer = ECDsa.Create();
            signer.ImportPkcs8PrivateKey(privateBytes, out _);
            using var verifier = CreateAppPublicKey();
            const string deviceId = "0123456789ABCDEF0123456789ABCDEF";
            var now = DateTimeOffset.UtcNow;
            var sampleKey = OfflineLicenseCodec.Create(signer, deviceId, "seven_day", now);
            return OfflineLicenseCodec.Validate(
                verifier,
                sampleKey,
                deviceId,
                now.AddMinutes(1),
                out _) == OfflineLicenseFailure.None;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateBytes);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private static string CreateLicense(string deviceId, string type)
    {
        if (!File.Exists(PrivateKeyPath))
        {
            throw new FileNotFoundException("The DPAPI-protected signing key was not found for this Windows account.");
        }

        var protectedBytes = File.ReadAllBytes(PrivateKeyPath);
        byte[] privateBytes;
        try
        {
            privateBytes = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException exception)
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            throw new InvalidOperationException("This Windows account cannot access the issuer signing key.", exception);
        }

        try
        {
            using var signer = ECDsa.Create();
            signer.ImportPkcs8PrivateKey(privateBytes, out _);
            using var verifier = CreateAppPublicKey();
            var now = DateTimeOffset.UtcNow;
            var licenseKey = OfflineLicenseCodec.Create(signer, deviceId, type, now);
            if (OfflineLicenseCodec.Validate(
                    verifier,
                    licenseKey,
                    deviceId,
                    now,
                    out _) != OfflineLicenseFailure.None)
            {
                throw new InvalidOperationException("The signing key does not match the public key embedded in PULSE.");
            }

            return licenseKey;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateBytes);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private static ECDsa CreateAppPublicKey()
    {
        using var stream = typeof(OfflineLicenseCodec).Assembly
            .GetManifestResourceStream("Pulse.LicensingCore.PublicKey.pem")
            ?? throw new InvalidOperationException("The PULSE app public key is missing.");
        using var reader = new StreamReader(stream);
        var verifier = ECDsa.Create();
        verifier.ImportFromPem(reader.ReadToEnd());
        return verifier;
    }
}
