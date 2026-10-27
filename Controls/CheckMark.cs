using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Pulse.Controls;

public sealed class CheckMark : Control
{
    public static readonly DependencyProperty IsCompleteProperty = DependencyProperty.Register(
        nameof(IsComplete), typeof(bool), typeof(CheckMark),
        new PropertyMetadata(false, OnCompleteChanged));

    public static readonly DependencyProperty AnimateProperty = DependencyProperty.Register(
        nameof(Animate), typeof(bool), typeof(CheckMark), new PropertyMetadata(true));

    private Path? _path;
    private Path? _notFoundPath;
    private ScaleTransform? _scale;
    private ScaleTransform? _notFoundScale;

    static CheckMark()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(CheckMark), new FrameworkPropertyMetadata(typeof(CheckMark)));
    }

    public bool IsComplete
    {
        get => (bool)GetValue(IsCompleteProperty);
        set => SetValue(IsCompleteProperty, value);
    }

    public static readonly DependencyProperty IsNotFoundProperty = DependencyProperty.Register(
        nameof(IsNotFound), typeof(bool), typeof(CheckMark),
        new PropertyMetadata(false, OnCompleteChanged));

    public bool IsNotFound
    {
        get => (bool)GetValue(IsNotFoundProperty);
        set => SetValue(IsNotFoundProperty, value);
    }

    public bool Animate
    {
        get => (bool)GetValue(AnimateProperty);
        set => SetValue(AnimateProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _path = GetTemplateChild("PART_Path") as Path;
        _notFoundPath = GetTemplateChild("PART_NotFoundPath") as Path;
        if (_path is not null)
        {
            _scale = new ScaleTransform(0.7, 0.7);
            _path.RenderTransform = _scale;
            _path.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        if (_notFoundPath is not null)
        {
            _notFoundScale = new ScaleTransform(0.7, 0.7);
            _notFoundPath.RenderTransform = _notFoundScale;
            _notFoundPath.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        ApplyState(true);
    }

    private static void OnCompleteChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((CheckMark)d).ApplyState(false);
    }

    private void ApplyState(bool instant)
    {
        if (_path is null || _scale is null || _notFoundPath is null || _notFoundScale is null)
        {
            return;
        }

        _path.BeginAnimation(UIElement.OpacityProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _notFoundPath.BeginAnimation(UIElement.OpacityProperty, null);
        _notFoundScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _notFoundScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        _path.Opacity = 0;
        _scale.ScaleX = 0.7;
        _scale.ScaleY = 0.7;
        _notFoundPath.Opacity = 0;
        _notFoundScale.ScaleX = 0.7;
        _notFoundScale.ScaleY = 0.7;

        if (!IsComplete && !IsNotFound)
        {
            return;
        }

        if (!Animate || instant)
        {
            var mark = IsNotFound ? _notFoundPath : _path;
            var scale = IsNotFound ? _notFoundScale : _scale;
            mark.Opacity = 1;
            scale.ScaleX = 1;
            scale.ScaleY = 1;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(420);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var visiblePath = IsNotFound ? _notFoundPath : _path;
        var visibleScale = IsNotFound ? _notFoundScale : _scale;
        visiblePath.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        visibleScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.7, 1, duration) { EasingFunction = ease });
        visibleScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.7, 1, duration) { EasingFunction = ease });
    }
}
