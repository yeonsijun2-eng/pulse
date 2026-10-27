using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Pulse.Helpers;
using Pulse.ViewModels;

namespace Pulse.Views;

public partial class IntroView : UserControl
{
    private CancellationTokenSource? _play;
    private bool _hasPlayed;

    public IntroView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _play?.Cancel();
            Motion.Stop(LogoText);
            Motion.Stop(SubtitleText);
            LightSweep.BeginAnimation(OpacityProperty, null);
            LightSweep.RenderTransform.BeginAnimation(TranslateTransform.XProperty, null);
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hasPlayed)
        {
            return;
        }

        _hasPlayed = true;
        await PlayAsync();
    }

    private async Task PlayAsync()
    {
        if (DataContext is not IntroViewModel intro)
        {
            return;
        }

        _play?.Cancel();
        var play = new CancellationTokenSource();
        _play = play;
        var cancellationToken = play.Token;
        try
        {
            if (!intro.AnimationsEnabled)
            {
                await Motion.BlurInAsync(LogoText, false, cancellationToken);
                await Motion.FadeSlideAsync(SubtitleText, true, false, cancellationToken);
                return;
            }

            await Task.WhenAll(
                Motion.BlurInAsync(LogoText, true, cancellationToken),
                SweepAsync(cancellationToken));
            await Task.Delay(140, cancellationToken);
            await Motion.FadeSlideAsync(SubtitleText, true, true, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Motion.Stop(LogoText);
            Motion.Stop(SubtitleText);
            LightSweep.BeginAnimation(OpacityProperty, null);
            LightSweep.RenderTransform.BeginAnimation(TranslateTransform.XProperty, null);
        }
        finally
        {
            if (ReferenceEquals(_play, play))
            {
                _play = null;
                play.Dispose();
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        var transform = (TranslateTransform)LightSweep.RenderTransform;
        transform.X = -(ActualWidth + 220) / 2;
        var fadeIn = new DoubleAnimation(0, 0.75, TimeSpan.FromMilliseconds(140));
        var sweep = new DoubleAnimation(
            transform.X,
            (ActualWidth + 220) / 2,
            TimeSpan.FromMilliseconds(900))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sweep.Completed += (_, _) => completion.TrySetResult();
        using var registration = cancellationToken.Register(() =>
        {
            LightSweep.BeginAnimation(OpacityProperty, null);
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            completion.TrySetCanceled(cancellationToken);
        });

        LightSweep.BeginAnimation(OpacityProperty, fadeIn);
        transform.BeginAnimation(TranslateTransform.XProperty, sweep);
        await Task.Delay(720, cancellationToken);
        LightSweep.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.75, 0, TimeSpan.FromMilliseconds(180)));
        await completion.Task;
    }
}
