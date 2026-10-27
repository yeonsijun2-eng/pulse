using System.Collections.ObjectModel;
using System.Diagnostics;
using Pulse.Helpers;
using Pulse.Models;
using Pulse.Services;

namespace Pulse.ViewModels;

public sealed class InjectorViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;
    private CancellationTokenSource? _run;
    private bool _busy;
    private double _progress;
    private string _status = "IdleSimulation";
    private int _generation;

    public InjectorViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Logs = [];
        ClearLogsCommand = new RelayCommand(_ => ClearLogs());
    }

    public ObservableCollection<InjectorLogEntry> Logs { get; }
    public RelayCommand ClearLogsCommand { get; }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                _shell.InjectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string Status
    {
        get => Localization.Get(_status);
        private set => SetProperty(ref _status, value);
    }

    public async Task RunSimulationAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _run = run;
        var generation = ++_generation;
        var ct = run.Token;
        IsBusy = true;
        Logs.Clear();
        Progress = 0;
        Status = "Preparing";
        var phases = new (TimeSpan At, string MessageKey)[]
        {
            (TimeSpan.Zero, "LogInitializing"),
            (TimeSpan.FromSeconds(1), "LogLoadingConfiguration"),
            (TimeSpan.FromSeconds(2), "LogPreparingModules"),
            (TimeSpan.FromSeconds(3), "LogProcessingProgress"),
            (TimeSpan.FromSeconds(4), "LogFinalizingSequence")
        };

        try
        {
            var duration = TimeSpan.FromSeconds(5);
            var stopwatch = Stopwatch.StartNew();
            var phaseIndex = 0;
            while (stopwatch.Elapsed < duration)
            {
                ct.ThrowIfCancellationRequested();

                Progress = Math.Min(1, stopwatch.Elapsed.TotalMilliseconds / duration.TotalMilliseconds);
                Status = Progress < 0.2 ? "Preparing" : "Processing";

                while (phaseIndex < phases.Length && stopwatch.Elapsed >= phases[phaseIndex].At)
                {
                    Append(InjectorLogLevel.Info, phases[phaseIndex].MessageKey);
                    phaseIndex++;
                }

                var remaining = duration - stopwatch.Elapsed;
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(16)
                    ? remaining
                    : TimeSpan.FromMilliseconds(16), ct);
            }

            Progress = 1;
            Status = "Injecting";
            Append(InjectorLogLevel.Success, "LogSequenceComplete");
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation)
            {
                Status = "SimulationCancelled";
                Append(InjectorLogLevel.Warning, "LogSequenceCancelled");
            }
        }
        catch (Exception ex)
        {
            if (generation == _generation)
            {
                Status = "SimulationFailed";
                Append(InjectorLogLevel.Error, "LogSequenceFailed", ex.Message);
            }
        }
        finally
        {
            if (generation == _generation)
            {
                _run = null;
                IsBusy = false;
            }
        }
    }

    public void Reset()
    {
        Cancel();
        ClearLogs();
        Progress = 0;
        Status = "IdleSimulation";
    }

    public void RefreshLanguage()
    {
        Raise(nameof(Status));
        foreach (var log in Logs)
        {
            log.RefreshLanguage();
        }
    }

    private void ClearLogs() => Logs.Clear();

    public void Cancel()
    {
        var run = _run;
        if (run is null)
        {
            return;
        }

        _run = null;
        _generation++;
        run.Cancel();
        IsBusy = false;
        Status = "SimulationCancelled";
        Append(InjectorLogLevel.Warning, "LogSequenceCancelled");
    }

    private void Append(InjectorLogLevel level, string messageKey, string? detail = null)
    {
        Logs.Add(new InjectorLogEntry(DateTimeOffset.Now, level, messageKey));
        if (!string.IsNullOrWhiteSpace(detail))
        {
            Logs.Add(new InjectorLogEntry(DateTimeOffset.Now, InjectorLogLevel.Error, string.Empty, detail));
        }
    }
}
