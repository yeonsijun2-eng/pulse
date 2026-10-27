using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Pulse.Helpers;
using Pulse.Models;
using Pulse.Services;

namespace Pulse.ViewModels;

public sealed class SetupViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;
    private bool _running;
    private bool _hasError;
    private string _progressLabel = "Waiting";
    private double _progress;
    private int _failedIndex = -1;

    public SetupViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Steps =
        [
            new SetupStep(1, "StepInterface"),
            new SetupStep(2, "StepSystem"),
            new SetupStep(3, "StepValorant"),
            new SetupStep(4, "StepFinalize")
        ];
    }

    public ObservableCollection<SetupStep> Steps { get; }

    public bool IsRunning
    {
        get => _running;
        private set => SetProperty(ref _running, value);
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public string ProgressLabel
    {
        get => _progressLabel;
        private set => SetProperty(ref _progressLabel, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public async Task RunAsync(CancellationToken cancellationToken, double? dpiScale = null)
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        HasError = false;
        _shell.Error = null;
        var start = _failedIndex >= 0 ? _failedIndex : 0;
        try
        {
            for (var i = start; i < Steps.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = Steps[i];
                step.State = StepState.Active;
                step.Detail = Localization.Get("StepWorking");
                ProgressLabel = step.Title;
                Progress = i / (double)Steps.Count;

                var watch = Stopwatch.StartNew();
                var operation = ExecuteStepAsync(i, dpiScale ?? 1, cancellationToken);
                try
                {
                    while (!operation.IsCompleted)
                    {
                        var fraction = Math.Min(0.92, watch.Elapsed.TotalMilliseconds / 3000d * 0.92);
                        Progress = (i + fraction) / Steps.Count;
                        await Task.Delay(8, cancellationToken);
                    }

                    await operation;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Fail(i, ex.Message);
                    return;
                }

                while (watch.ElapsedMilliseconds < 3000)
                {
                    var fraction = Math.Min(0.92, watch.Elapsed.TotalMilliseconds / 3000d * 0.92);
                    Progress = (i + fraction) / Steps.Count;
                    var remaining = 3000 - (int)watch.ElapsedMilliseconds;
                    await Task.Delay(Math.Min(8, remaining), cancellationToken);
                }

                step.State = i == 2 && !_shell.Install.CanLaunch
                    ? StepState.NotFound
                    : StepState.Complete;
                Progress = (i + 1) / (double)Steps.Count;
                _failedIndex = -1;
            }

            await _shell.CompleteSetupAsync();
        }
        catch (OperationCanceledException)
        {
            ProgressLabel = Localization.Get("Cancelled");
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task ExecuteStepAsync(int index, double dpiScale, CancellationToken cancellationToken)
    {
        var step = Steps[index];
        switch (index)
        {
            case 0:
                await _shell.ReloadSettingsAsync(cancellationToken);
                step.Detail = Localization.Get("StepReady");
                break;
            case 1:
                var snapshot = await _shell.ProbeSystemAsync(dpiScale, cancellationToken);
                step.Detail = string.Format(
                    CultureInfo.CurrentCulture,
                    Localization.Get("SystemDetails"),
                    snapshot.OperatingSystem,
                    snapshot.Architecture,
                    snapshot.ProcessorCount);
                break;
            case 2:
                var result = await _shell.ScanAsync(cancellationToken);
                if (result.Status is InstallStatus.Invalid or InstallStatus.Inaccessible)
                {
                    throw new InvalidOperationException(result.Message);
                }

                step.Detail = result.CanLaunch
                    ? result.ExecutablePath ?? Localization.Get("StepInstalled")
                    : Localization.Get("StepValorantNotFoundDetail");
                break;
            case 3:
                await _shell.PersistAsync(cancellationToken);
                step.Detail = Localization.Get("StepSaved");
                break;
            default:
                throw new InvalidOperationException("Unknown setup step.");
        }
    }

    private void Fail(int index, string message)
    {
        _failedIndex = index;
        HasError = true;
        _shell.Error = message;
        var step = Steps[index];
        step.State = StepState.Error;
        step.Detail = message;
        ProgressLabel = Localization.Get("SetupPaused");
        for (var i = index + 1; i < Steps.Count; i++)
        {
            Steps[i].State = StepState.Pending;
            Steps[i].Detail = string.Empty;
        }
    }

    public void RefreshLanguage()
    {
        foreach (var step in Steps)
        {
            step.RefreshLanguage();
        }

        if (_running)
        {
            var active = Steps.FirstOrDefault(step => step.IsActive);
            if (active is not null)
            {
                ProgressLabel = active.Title;
            }
        }
    }
}
