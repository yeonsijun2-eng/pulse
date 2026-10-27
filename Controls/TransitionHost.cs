using System.Windows;
using System.Windows.Controls;
using Pulse.Helpers;

namespace Pulse.Controls;

public sealed class TransitionHost : ContentControl
{
    public static readonly DependencyProperty AnimationsEnabledProperty =
        DependencyProperty.Register(
            nameof(AnimationsEnabled),
            typeof(bool),
            typeof(TransitionHost),
            new PropertyMetadata(true));

    private CancellationTokenSource? _transition;

    static TransitionHost()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(TransitionHost),
            new FrameworkPropertyMetadata(typeof(TransitionHost)));
    }

    public TransitionHost()
    {
        ClipToBounds = true;
    }

    public bool AnimationsEnabled
    {
        get => (bool)GetValue(AnimationsEnabledProperty);
        set => SetValue(AnimationsEnabledProperty, value);
    }

    protected override async void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        _transition?.Cancel();
        _transition?.Dispose();
        var transition = new CancellationTokenSource();
        _transition = transition;

        try
        {
            await Motion.FadeSlideAsync(this, enter: true, AnimationsEnabled, transition.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_transition, transition))
            {
                _transition = null;
                transition.Dispose();
            }
        }
    }
}
