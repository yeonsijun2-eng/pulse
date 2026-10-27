using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using Pulse.Helpers;
using Pulse.Models;
using Pulse.Services;

namespace Pulse.ViewModels;

public enum AppScreen
{
    Intro,
    License,
    Setup,
    Shell
}

public enum ShellPage
{
    Home,
    Injector,
    Settings
}

public sealed class ShellViewModel : ViewModelBase, IDisposable
{
    private readonly ValorantPathService _valorant = new();
    private readonly ILicenseAuthenticationService _licenseAuthentication;
    private readonly LicenseSessionStore _licenseSessionStore = new();
    private readonly DispatcherTimer _processTimer;
    private readonly DispatcherTimer _licenseRefreshTimer;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly SemaphoreSlim _licenseRefreshGate = new(1, 1);
    private CancellationTokenSource _lifetime = new();
    private AppScreen _screen = AppScreen.Intro;
    private ShellPage _page = ShellPage.Home;
    private object? _content;
    private InstallScanResult _install = InstallScanResult.Scanning();
    private AppSettings _settings = new();
    private string? _error;
    private bool _busy;
    private bool _showInstallNotice;
    private bool _isValorantRunning;
    private bool _licenseStatusUnavailable;
    private AuthenticatedLicense? _licenseSession;
    private Stopwatch? _licenseClock;
    private SystemSnapshot? _system;
    private int _transitionSerial;

    public ShellViewModel(ILicenseAuthenticationService? licenseAuthentication = null)
    {
        _licenseAuthentication = licenseAuthentication ?? new OfflineLicenseAuthenticationService();
        Intro = new IntroViewModel();
        LicensePage = new LicenseViewModel(this);
        Setup = new SetupViewModel(this);
        Home = new HomeViewModel(this);
        Injector = new InjectorViewModel(this);
        SettingsPage = new SettingsViewModel(this);

        NavigateCommand = new RelayCommand(p =>
        {
            if (p is string name && Enum.TryParse<ShellPage>(name, out var page))
            {
                _ = NavigateAsync(page);
            }
        });

        RetrySetupCommand = new AsyncRelayCommand((_, _) => Setup.RunAsync(_lifetime.Token));
        ScanCommand = new AsyncRelayCommand((_, ct) => ScanAsync(ct));
        BrowseCommand = new AsyncRelayCommand((_, ct) => BrowseAsync(ct));
        LaunchCommand = new AsyncRelayCommand((_, ct) => LaunchAsync(ct), _ => IsLicenseAuthorized && Install.CanLaunch && !Busy);
        DownloadCommand = new RelayCommand(_ => Download());
        SaveSettingsCommand = new AsyncRelayCommand((_, ct) => SaveSettingsAsync(ct));
        ResetSettingsCommand = new AsyncRelayCommand((_, ct) => ResetAsync(ct));
        InjectCommand = new AsyncRelayCommand(
            (_, ct) => RunInjectorDemoAsync(ct),
            _ => IsLicenseAuthorized && IsValorantRunning && !Injector.IsBusy);
        DismissInstallNoticeCommand = new RelayCommand(_ => ShowInstallNotice = false);
        SetLanguageCommand = new RelayCommand(p =>
        {
            if (p is string language)
            {
                Language = language;
            }
        });
        LogoutCommand = new AsyncRelayCommand((_, ct) => LogoutAsync(ct));

        _processTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _processTimer.Tick += async (_, _) => await RefreshProcessAsync();
        _licenseRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _licenseRefreshTimer.Tick += async (_, _) => await RefreshLicenseAsync();
        Content = Intro;
    }

    public IntroViewModel Intro { get; }
    public LicenseViewModel LicensePage { get; }
    public SetupViewModel Setup { get; }
    public HomeViewModel Home { get; }
    public InjectorViewModel Injector { get; }
    public SettingsViewModel SettingsPage { get; }
    public ValorantPathService Valorant => _valorant;
    public IUserDialogs? Dialogs { get; set; }

    public RelayCommand NavigateCommand { get; }
    public AsyncRelayCommand RetrySetupCommand { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand BrowseCommand { get; }
    public AsyncRelayCommand LaunchCommand { get; }
    public RelayCommand DownloadCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand ResetSettingsCommand { get; }
    public AsyncRelayCommand InjectCommand { get; }
    public RelayCommand DismissInstallNoticeCommand { get; }
    public RelayCommand SetLanguageCommand { get; }
    public AsyncRelayCommand LogoutCommand { get; }

    public AppScreen Screen
    {
        get => _screen;
        private set
        {
            if (SetProperty(ref _screen, value))
            {
                Raise(nameof(IsIntro));
                Raise(nameof(IsLicense));
                Raise(nameof(IsSetup));
                Raise(nameof(IsShell));
            }
        }
    }

    public ShellPage Page
    {
        get => _page;
        private set
        {
            if (SetProperty(ref _page, value))
            {
                Raise(nameof(IsHome));
                Raise(nameof(IsInjector));
                Raise(nameof(IsSettings));
            }
        }
    }

    public object? Content
    {
        get => _content;
        private set => SetProperty(ref _content, value);
    }

    public InstallScanResult Install
    {
        get => _install;
        private set
        {
            if (SetProperty(ref _install, value))
            {
                SetValorantRunning(value.Status == InstallStatus.Running);
                Raise(nameof(StatusHeadline));
                Raise(nameof(InstallPathText));
                Raise(nameof(InstallMessage));
                Raise(nameof(InjectorAvailabilityText));
                Raise(nameof(ShowDownload));
                Raise(nameof(ShowRetryScan));
                Raise(nameof(CanLaunch));
                LaunchCommand.RaiseCanExecuteChanged();
                InjectCommand.RaiseCanExecuteChanged();
                Home.Notify();
                SettingsPage.Notify();
            }
        }
    }

    public AppSettings Settings
    {
        get => _settings;
        private set
        {
            if (SetProperty(ref _settings, value))
            {
                Raise(nameof(Language));
                Raise(nameof(AnimationsEnabled));
            }
        }
    }

    public bool AnimationsEnabled
    {
        get => Settings.AnimationsEnabled;
        set
        {
            if (Settings.AnimationsEnabled == value)
            {
                return;
            }

            Settings.AnimationsEnabled = value;
            Raise();
            _ = SaveSettingsAsync(_lifetime.Token);
        }
    }

    public string Language
    {
        get => Settings.Language;
        set
        {
            if (Settings.Language == value || value is not "en" and not "ko")
            {
                return;
            }

            Settings.Language = value;
            Raise();
            Localization.Apply(value);
            Raise(nameof(InjectorAvailabilityText));
            Setup.RefreshLanguage();
            Injector.RefreshLanguage();
            Home.Notify();
            SettingsPage.Notify();
            _ = SaveSettingsAsync(_lifetime.Token);
        }
    }

    public bool ShowInstallNotice
    {
        get => _showInstallNotice;
        private set => SetProperty(ref _showInstallNotice, value);
    }

    public string? Error
    {
        get => _error;
        set => SetProperty(ref _error, value);
    }

    public string InstallMessage => Install.Status switch
    {
        InstallStatus.Found => Localization.Get("VerifiedMessage"),
        InstallStatus.Running => Localization.Get("RunningMessage"),
        InstallStatus.NotFound => Localization.Get("MissingMessage"),
        _ => Install.Message
    };

    public bool HasLicenseDetails => _licenseSession is not null;
    public bool ShowLicenseCard => HasLicenseDetails || LicenseStatusUnavailable;
    public bool LicenseStatusUnavailable
    {
        get => _licenseStatusUnavailable;
        private set
        {
            if (SetProperty(ref _licenseStatusUnavailable, value))
            {
                Raise(nameof(ShowLicenseCard));
                Raise(nameof(LicenseTypeText));
                Raise(nameof(LicenseStateText));
                Raise(nameof(LicenseExpiryText));
                Raise(nameof(LicenseRemainingText));
                Raise(nameof(IsLicenseAuthorized));
                Raise(nameof(InjectorAvailabilityText));
                CanLaunchCommandChanged();
                InjectCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public string LicenseTypeText => LicenseStatusUnavailable
        ? Localization.Get("StatusUnavailable")
        : _licenseSession?.License.Kind switch
    {
        LicenseKind.SevenDay => Localization.Get("LicenseType7Day"),
        LicenseKind.ThirtyDay => Localization.Get("LicenseType30Day"),
        LicenseKind.Permanent => Localization.Get("LicenseTypePermanent"),
        _ => Localization.Get("StatusUnavailable")
    };
    public string LicenseStateText => LicenseStatusUnavailable
        ? Localization.Get("StatusUnavailable")
        : _licenseSession?.License.State switch
    {
        LicenseState.Active when _licenseSession.License.Kind is LicenseKind.SevenDay or LicenseKind.ThirtyDay
                                 && _licenseSession.License.ExpiresAt is { } expiresAt
                                 && GetTrustedServerTime() is { } trustedNow
                                 && expiresAt <= trustedNow => Localization.Get("LicenseExpired"),
        LicenseState.Active => Localization.Get("LicenseActive"),
        LicenseState.Expired => Localization.Get("LicenseExpired"),
        LicenseState.Revoked => Localization.Get("LicenseRevoked"),
        _ => Localization.Get("StatusUnavailable")
    };
    public string LicenseExpiryText
    {
        get
        {
            if (LicenseStatusUnavailable || _licenseSession is null)
            {
                return Localization.Get("StatusUnavailable");
            }

            var license = _licenseSession.License;
            return license.Kind == LicenseKind.Permanent
                ? Localization.Get("NeverExpires")
                : license.ExpiresAt is { } expiresAt
                    ? string.Format(
                        CultureInfo.CurrentCulture,
                        Localization.Get("ExpiryDate"),
                        expiresAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.CurrentCulture))
                    : Localization.Get("StatusUnavailable");
        }
    }
    public string LicenseRemainingText
    {
        get
        {
            if (LicenseStatusUnavailable || _licenseSession is null)
            {
                return Localization.Get("StatusUnavailable");
            }

            var license = _licenseSession.License;
            if (license.Kind == LicenseKind.Permanent)
            {
                return Localization.Get("NeverExpires");
            }

            if (license.State != LicenseState.Active || license.ExpiresAt is not { } expiresAt)
            {
                return LicenseStateText;
            }

            var trustedNow = license.ServerTime + (_licenseClock?.Elapsed ?? TimeSpan.Zero);
            var remaining = expiresAt - trustedNow;
            if (remaining <= TimeSpan.Zero)
            {
                return Localization.Get("LicenseExpired");
            }

            if (remaining.TotalDays >= 1)
            {
                return string.Format(CultureInfo.CurrentCulture, Localization.Get("ExpiresInDays"), Math.Ceiling(remaining.TotalDays));
            }

            if (remaining.TotalHours >= 1)
            {
                return string.Format(CultureInfo.CurrentCulture, Localization.Get("ExpiresInHours"), Math.Ceiling(remaining.TotalHours));
            }

            return string.Format(CultureInfo.CurrentCulture, Localization.Get("ExpiresInMinutes"), Math.Max(1, Math.Ceiling(remaining.TotalMinutes)));
        }
    }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                LaunchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public SystemSnapshot? SystemInfo
    {
        get => _system;
        private set => SetProperty(ref _system, value);
    }

    public bool IsIntro => Screen == AppScreen.Intro;
    public bool IsLicense => Screen == AppScreen.License;
    public bool IsSetup => Screen == AppScreen.Setup;
    public bool IsShell => Screen == AppScreen.Shell;
    public bool IsHome => Page == ShellPage.Home;
    public bool IsInjector => Page == ShellPage.Injector;
    public bool IsSettings => Page == ShellPage.Settings;
    public bool CanLaunch => IsLicenseAuthorized && Install.CanLaunch && !Busy;
    public string DeviceId => _licenseAuthentication is OfflineLicenseAuthenticationService offline
        ? offline.DeviceId
        : string.Empty;
    public bool IsLicenseAuthorized =>
        !LicenseStatusUnavailable
        && _licenseSession is { } session
        && GetTrustedServerTime() is { } trustedNow
        && session.SessionExpiresAt > trustedNow
        && session.License.State == LicenseState.Active
        && (session.License.Kind == LicenseKind.Permanent
            || session.License.ExpiresAt is { } expiresAt
            && expiresAt > trustedNow);
    public bool ShowDownload => Install.Status is InstallStatus.NotFound;
    public bool ShowRetryScan => Install.Status is InstallStatus.NotFound or InstallStatus.Invalid or InstallStatus.Inaccessible;
    public bool IsValorantRunning => _isValorantRunning;
    public string InjectorAvailabilityText => !IsLicenseAuthorized
        ? Localization.Get("LicenseRequired")
        : Localization.Get(IsValorantRunning ? "DemoGameDetected" : "DemoWaitingForGame");
    public string StatusHeadline => Install.Status switch
    {
        InstallStatus.Found => Localization.Get("ValorantReady"),
        InstallStatus.Running => Localization.Get("ValorantRunning"),
        InstallStatus.NotFound => Localization.Get("NotFoundHeadline"),
        InstallStatus.Invalid or InstallStatus.Inaccessible => Localization.Get("PathInvalid"),
        _ => Localization.Get("Scanning")
    };
    public string InstallPathText => string.IsNullOrWhiteSpace(Install.ExecutablePath)
        ? Localization.Get("NoVerifiedPath")
        : Install.ExecutablePath!;

    public async Task StartAsync(double dpiScale)
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _lifetime = new CancellationTokenSource();
        var ct = _lifetime.Token;
        try
        {
            await ReloadSettingsAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Settings = new AppSettings();
            Localization.Apply(Settings.Language);
            LicensePage.SetError($"{Localization.Get("SettingsLoadFailed")}: {ex.Message}");
        }

        Screen = AppScreen.Intro;
        Localization.Apply(Settings.Language);
        Intro.Prepare(Settings.AnimationsEnabled);
        Content = Intro;
        await Intro.PlayAsync(Settings.AnimationsEnabled, ct);

        string? token;
        try
        {
            token = await _licenseSessionStore.LoadAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LicensePage.SetError($"{Localization.Get("SessionStorageFailed")}: {ex.Message}");
            token = null;
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            LicenseAuthResult result;
            try
            {
                result = await _licenseAuthentication.ValidateSessionAsync(token, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new LicenseAuthResult(null, LicenseAuthFailure.ConnectionFailed, ex.Message);
            }

            if (result.IsSuccess)
            {
                if (IsValidLicenseResponse(result.Session!))
                {
                    SetLicenseSession(result.Session!);
                    await StartSetupAsync(dpiScale);
                    return;
                }

                result = new LicenseAuthResult(null, LicenseAuthFailure.ConnectionFailed, Localization.Get("InvalidServerResponse"));
            }

            LicensePage.SetError(LicenseFailureText(result));
            if (result.Failure != LicenseAuthFailure.ConnectionFailed)
            {
                await _licenseSessionStore.DeleteAsync(ct);
            }
        }

        ShowLicensePage();
    }

    public async Task<LicenseAuthResult> AuthenticateLicenseKeyAsync(
        string licenseKey,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        LicenseAuthResult result;
        try
        {
            result = await _licenseAuthentication.ActivateAsync(licenseKey, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = new LicenseAuthResult(null, LicenseAuthFailure.ConnectionFailed, ex.Message);
        }

        if (!result.IsSuccess || result.Session is null)
        {
            _licenseSession = null;
            _licenseClock = null;
            LicenseStatusUnavailable = true;
            RaiseLicenseDetails();
            return result;
        }

        if (!IsValidLicenseResponse(result.Session))
        {
            _licenseSession = null;
            _licenseClock = null;
            LicenseStatusUnavailable = true;
            RaiseLicenseDetails();
            return new LicenseAuthResult(null, LicenseAuthFailure.ConnectionFailed, Localization.Get("InvalidServerResponse"));
        }

        return result;
    }

    public async Task CompleteLicenseLoginAsync(
        AuthenticatedLicense session,
        bool rememberMe,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        var ct = linked.Token;
        if (rememberMe)
        {
            await _licenseSessionStore.SaveAsync(session.SessionToken, ct);
        }
        else
        {
            await _licenseSessionStore.DeleteAsync(ct);
        }

        ct.ThrowIfCancellationRequested();
        SetLicenseSession(session);
        LicensePage.SetError(string.Empty);
        await StartSetupAsync();
    }

    public string LicenseFailureText(LicenseAuthResult result) => result.Failure switch
    {
        LicenseAuthFailure.InvalidKey => Localization.Get("InvalidLicenseKey"),
        LicenseAuthFailure.Expired => Localization.Get("LicenseExpiredError"),
        LicenseAuthFailure.Revoked => Localization.Get("LicenseRevokedError"),
        LicenseAuthFailure.None when result.Session?.License.State == LicenseState.Expired =>
            Localization.Get("LicenseExpiredError"),
        LicenseAuthFailure.None when result.Session?.License.State == LicenseState.Revoked =>
            Localization.Get("LicenseRevokedError"),
        LicenseAuthFailure.ConnectionFailed => result.Message is null
            ? Localization.Get("ConnectionFailed")
            : $"{Localization.Get("ConnectionFailed")}: {result.Message}",
        _ => Localization.Get("ConnectionFailed")
    };

    public void SetLicenseError(string message) => LicensePage.SetError(message);

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        var ct = linked.Token;
        var token = _licenseSession?.SessionToken;
        Injector.Cancel();
        _licenseRefreshTimer.Stop();
        _licenseSession = null;
        _licenseClock = null;
        LicenseStatusUnavailable = true;
        RaiseLicenseDetails();
        CanLaunchCommandChanged();
        InjectCommand.RaiseCanExecuteChanged();
        try
        {
            await _licenseSessionStore.DeleteAsync(ct);
            if (!string.IsNullOrWhiteSpace(token))
            {
                await _licenseAuthentication.RevokeSessionAsync(token, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LicensePage.SetError($"{Localization.Get("LogoutFailed")}: {ex.Message}");
        }

        ShowLicensePage();
    }

    private void ShowLicensePage()
    {
        Screen = AppScreen.License;
        Content = LicensePage;
    }

    private async Task StartSetupAsync(double? dpiScale = null)
    {
        if (!IsLicenseAuthorized)
        {
            ShowLicensePage();
            return;
        }

        Screen = AppScreen.Setup;
        Content = Setup;
        await Setup.RunAsync(_lifetime.Token, dpiScale);
    }

    private void SetLicenseSession(AuthenticatedLicense session)
    {
        _licenseSession = session;
        _licenseClock = Stopwatch.StartNew();
        LicenseStatusUnavailable = false;
        RaiseLicenseDetails();
        CanLaunchCommandChanged();
    }

    private void RaiseLicenseDetails()
    {
        Raise(nameof(HasLicenseDetails));
        Raise(nameof(ShowLicenseCard));
        Raise(nameof(LicenseTypeText));
        Raise(nameof(LicenseStateText));
        Raise(nameof(LicenseExpiryText));
        Raise(nameof(LicenseRemainingText));
        Raise(nameof(IsLicenseAuthorized));
        Raise(nameof(InjectorAvailabilityText));
    }

    private DateTimeOffset? GetTrustedServerTime() =>
        _licenseSession is null
            ? null
            : _licenseSession.License.ServerTime + (_licenseClock?.Elapsed ?? TimeSpan.Zero);

    private static bool IsValidLicenseResponse(AuthenticatedLicense session)
    {
        var license = session.License;
        return !string.IsNullOrWhiteSpace(session.SessionToken)
               && Enum.IsDefined(license.Kind)
               && Enum.IsDefined(license.State)
               && license.ServerTime != default
               && (license.Kind == LicenseKind.Permanent
                   ? license.ExpiresAt is null
                   : license.ExpiresAt is { } expiresAt
                     && (license.State != LicenseState.Active || expiresAt > license.ServerTime))
               && session.SessionExpiresAt > license.ServerTime;
    }

    private void CanLaunchCommandChanged() => LaunchCommand.RaiseCanExecuteChanged();

    private async Task RefreshLicenseAsync()
    {
        if (_licenseSession is null || !await _licenseRefreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var result = await _licenseAuthentication.ValidateSessionAsync(
                _licenseSession.SessionToken,
                _lifetime.Token);
            if (result.IsSuccess && result.Session is { } session && IsValidLicenseResponse(session))
            {
                SetLicenseSession(session);
                return;
            }

            LicenseStatusUnavailable = true;
            RaiseLicenseDetails();
            CanLaunchCommandChanged();
            InjectCommand.RaiseCanExecuteChanged();

            if (result.Failure is LicenseAuthFailure.Expired or LicenseAuthFailure.Revoked
                or LicenseAuthFailure.InvalidKey
                || result.Session is { License.State: LicenseState.Expired or LicenseState.Revoked }
                || result.Session is not null && !IsValidLicenseResponse(result.Session))
            {
                var failure = result.Failure == LicenseAuthFailure.None
                    ? new LicenseAuthResult(null, LicenseAuthFailure.ConnectionFailed, Localization.Get("InvalidServerResponse"))
                    : result;
                _licenseSession = null;
                _licenseClock = null;
                _licenseRefreshTimer.Stop();
                await _licenseSessionStore.DeleteAsync(_lifetime.Token);
                LicensePage.SetError(LicenseFailureText(failure));
                ShowLicensePage();
                RaiseLicenseDetails();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            LicenseStatusUnavailable = true;
            RaiseLicenseDetails();
            CanLaunchCommandChanged();
            InjectCommand.RaiseCanExecuteChanged();
        }
        finally
        {
            _licenseRefreshGate.Release();
        }
    }

    public async Task CompleteSetupAsync()
    {
        if (!IsLicenseAuthorized)
        {
            ShowLicensePage();
            return;
        }

        Error = null;
        ShowInstallNotice = Install.Status == InstallStatus.NotFound;
        Screen = AppScreen.Shell;
        Page = ShellPage.Home;
        Content = Home;
        _processTimer.Start();
        _licenseRefreshTimer.Start();
        await RefreshProcessAsync();
    }

    public async Task NavigateAsync(ShellPage page)
    {
        if (Screen != AppScreen.Shell || Page == page)
        {
            return;
        }

        if (Page == ShellPage.Injector)
        {
            Injector.Cancel();
        }

        var serial = ++_transitionSerial;
        Page = page;
        object next = page switch
        {
            ShellPage.Injector => Injector,
            ShellPage.Settings => SettingsPage,
            _ => Home
        };

        if (serial == _transitionSerial)
        {
            Content = next;
        }

        if (page == ShellPage.Injector)
        {
            await RefreshProcessAsync();
        }
    }

    public async Task<InstallScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        if (!await _scanGate.WaitAsync(0, cancellationToken))
        {
            return Install;
        }

        Busy = true;
        Error = null;
        try
        {
            Install = InstallScanResult.Scanning();
            var result = await _valorant.ScanAsync(Settings.LastExecutablePath, cancellationToken);
            ApplyInstall(result);
            if (result.CanLaunch)
            {
                try
                {
                    await PersistAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    Error = $"Installation was verified, but settings could not be saved: {ex.Message}";
                }
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Install = InstallScanResult.Invalid($"Scan failed: {ex.Message}");
            return Install;
        }
        finally
        {
            Busy = false;
            _scanGate.Release();
        }
    }

    public async Task BrowseAsync(CancellationToken cancellationToken)
    {
        var path = Dialogs?.PickExecutable();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Busy = true;
        try
        {
            var result = await _valorant.ValidatePathAsync(path, cancellationToken);
            ApplyInstall(result);
            if (result.CanLaunch)
            {
                try
                {
                    await PersistAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    Error = $"Installation was verified, but settings could not be saved: {ex.Message}";
                }
            }
            else
            {
                Error = result.Message;
            }
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task LaunchAsync(CancellationToken cancellationToken)
    {
        if (!IsLicenseAuthorized)
        {
            Error = Localization.Get("LicenseRequired");
            return;
        }

        if (!Install.CanLaunch)
        {
            Error = "Launch is disabled until a valid VALORANT path is verified.";
            return;
        }

        Busy = true;
        try
        {
            var verified = await _valorant.ValidatePathAsync(Install.ExecutablePath, cancellationToken);
            if (!verified.CanLaunch)
            {
                ApplyInstall(verified);
                Error = verified.Message;
                return;
            }

            var launched = await Task.Run(() => _valorant.Launch(verified), cancellationToken);
            ApplyInstall(launched);
            if (!launched.CanLaunch)
            {
                Error = launched.Message;
            }
        }
        catch (Exception ex)
        {
            Error = $"Launch failed: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    public void Download()
    {
        try
        {
            _valorant.OpenOfficialDownloadPage();
            Error = null;
        }
        catch (Exception ex)
        {
            Error = $"Could not open the official site: {ex.Message}";
        }
    }

    public async Task PersistAsync(CancellationToken cancellationToken)
    {
        Settings.LastExecutablePath = Install.CanLaunch ? Install.ExecutablePath : Settings.LastExecutablePath;
        Settings.LastRiotClientPath = Install.RiotClientPath ?? Settings.LastRiotClientPath;
        await SettingsStore.SaveAsync(Settings, cancellationToken);
    }

    public async Task ReloadSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await SettingsStore.LoadAsync(cancellationToken);
        settings.Language = settings.Language is "en" or "ko" ? settings.Language : "en";
        Settings = settings;
        Localization.Apply(settings.Language);
        Raise(nameof(AnimationsEnabled));
    }

    private async Task SaveSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PersistAsync(cancellationToken);
            Error = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Error = $"Could not save settings: {ex.Message}";
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        Injector.Reset();
        Settings = new AppSettings();
        Localization.Apply(Settings.Language);
        Setup.RefreshLanguage();
        Injector.RefreshLanguage();
        Home.Notify();
        Raise(nameof(Language));
        ApplyInstall(InstallScanResult.NotFound());
        Raise(nameof(AnimationsEnabled));
        try
        {
            await SettingsStore.SaveAsync(Settings, cancellationToken);
        }
        catch (Exception ex)
        {
            Error = $"Could not reset settings: {ex.Message}";
        }
    }

    public void ApplyInstall(InstallScanResult result)
    {
        Install = result;
        if (result.CanLaunch)
        {
            ShowInstallNotice = false;
        }
        if (result.Status is not InstallStatus.Invalid and not InstallStatus.Inaccessible)
        {
            Error = null;
        }
    }

    public async Task<SystemSnapshot> ProbeSystemAsync(double dpiScale, CancellationToken cancellationToken)
    {
        var snapshot = await SystemProbe.CollectAsync(dpiScale, cancellationToken);
        SystemInfo = snapshot;
        return snapshot;
    }

    private async Task RunInjectorDemoAsync(CancellationToken cancellationToken)
    {
        if (!IsLicenseAuthorized || Injector.IsBusy)
        {
            return;
        }

        var running = await _valorant.IsValorantRunningAsync(cancellationToken);
        SetValorantRunning(running);
        if (!running)
        {
            return;
        }

        if (!IsLicenseAuthorized || Injector.IsBusy)
        {
            return;
        }

        await Injector.RunSimulationAsync(cancellationToken);
    }

    private async Task RefreshProcessAsync()
    {
        Raise(nameof(LicenseRemainingText));
        Raise(nameof(IsLicenseAuthorized));
        Raise(nameof(InjectorAvailabilityText));
        CanLaunchCommandChanged();
        InjectCommand.RaiseCanExecuteChanged();
        if (Screen != AppScreen.Shell)
        {
            return;
        }

        try
        {
            var running = await _valorant.IsValorantRunningAsync(_lifetime.Token);
            SetValorantRunning(running);
            if (Install.IsInstalled && running != (Install.Status == InstallStatus.Running))
            {
                ApplyInstall(Install with
                {
                    Status = running ? InstallStatus.Running : InstallStatus.Found,
                    Message = running
                        ? "VALORANT process is running."
                        : "VALORANT installation verified."
                });
            }
        }
        catch (OperationCanceledException)
        {
            // App closing.
        }
    }

    private void SetValorantRunning(bool running)
    {
        if (_isValorantRunning == running)
        {
            return;
        }

        _isValorantRunning = running;
        Raise(nameof(IsValorantRunning));
        Raise(nameof(InjectorAvailabilityText));
        InjectCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _processTimer.Stop();
        _licenseRefreshTimer.Stop();
        _lifetime.Cancel();
        _lifetime.Dispose();
        Injector.Cancel();
    }
}
