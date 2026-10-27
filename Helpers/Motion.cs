using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Pulse.Helpers;

public static class Motion
{
    public static async Task FadeSlideAsync(
        FrameworkElement element,
        bool enter,
        bool enabled,
        CancellationToken cancellationToken)
    {
        Stop(element);
        if (element.RenderTransform is not TranslateTransform)
        {
            element.RenderTransform = new TranslateTransform();
            element.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        if (!enabled)
        {
            element.Opacity = enter ? 1 : 0;
            ((TranslateTransform)element.RenderTransform).X = 0;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(enter ? 420 : 280);
        var opacityFrom = enter ? 0 : 1;
        var opacityTo = enter ? 1 : 0;
        var xFrom = enter ? 28 : 0;
        var xTo = enter ? 0 : -18;

        element.Opacity = opacityFrom;
        ((TranslateTransform)element.RenderTransform).X = xFrom;

        var opacity = new DoubleAnimation(opacityFrom, opacityTo, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var slide = new DoubleAnimation(xFrom, xTo, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        opacity.Completed += (_, _) => tcs.TrySetResult();
        using var reg = cancellationToken.Register(() =>
        {
            Stop(element);
            tcs.TrySetCanceled(cancellationToken);
        });

        element.BeginAnimation(UIElement.OpacityProperty, opacity);
        element.RenderTransform.BeginAnimation(TranslateTransform.XProperty, slide);
        await tcs.Task;
    }

    public static async Task BlurInAsync(FrameworkElement element, bool enabled, CancellationToken cancellationToken)
    {
        Stop(element);
        element.Effect ??= new BlurEffect();
        var blur = (BlurEffect)element.Effect;

        if (!enabled)
        {
            blur.Radius = 0;
            element.Opacity = 1;
            return;
        }

        blur.Radius = 28;
        element.Opacity = 0;
        var duration = TimeSpan.FromMilliseconds(1100);
        var radius = new DoubleAnimation(28, 0, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        var opacity = new DoubleAnimation(0, 1, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        opacity.Completed += (_, _) => tcs.TrySetResult();
        using var reg = cancellationToken.Register(() =>
        {
            Stop(element);
            blur.BeginAnimation(BlurEffect.RadiusProperty, null);
            tcs.TrySetCanceled(cancellationToken);
        });

        blur.BeginAnimation(BlurEffect.RadiusProperty, radius);
        element.BeginAnimation(UIElement.OpacityProperty, opacity);
        await tcs.Task;
        element.Effect = null;
    }

    public static void Stop(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (element.RenderTransform is TranslateTransform transform)
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
        }

        if (element.Effect is BlurEffect blur)
        {
            blur.BeginAnimation(BlurEffect.RadiusProperty, null);
        }
    }
}
