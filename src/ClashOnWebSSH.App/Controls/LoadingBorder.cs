using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClashOnWebSSH.App.Controls;

/// <summary>A moving segment that follows the rounded outline of an active operation card.</summary>
public sealed class LoadingBorder : FrameworkElement
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(LoadingBorder), new PropertyMetadata(false, OnActiveChanged));
    private static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(
        "Phase", typeof(double), typeof(LoadingBorder), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    public LoadingBorder()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => BeginAnimation(PhaseProperty, null);
    }

    private static void OnActiveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((LoadingBorder)sender).UpdateAnimation();

    private void UpdateAnimation()
    {
        BeginAnimation(PhaseProperty, null);
        if (IsActive && IsLoaded && SystemParameters.ClientAreaAnimation)
            BeginAnimation(PhaseProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(2.4)) { RepeatBehavior = RepeatBehavior.Forever });
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (!IsActive || ActualWidth < 8 || ActualHeight < 8) return;
        const double thickness = 2.5;
        var bounds = new Rect(thickness / 2, thickness / 2, ActualWidth - thickness, ActualHeight - thickness);
        var radius = Math.Min(11, Math.Min(bounds.Width, bounds.Height) / 2);
        var perimeter = 2 * (bounds.Width + bounds.Height - 4 * radius) + 2 * Math.PI * radius;
        var segment = Math.Min(140, perimeter * 0.18);
        context.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0xD7, 0xD3, 0xFF)), thickness), bounds, radius, radius);
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x65, 0x5D, 0xEF)), thickness)
        {
            DashStyle = new DashStyle(new[] { segment / thickness, (perimeter - segment) / thickness }, -(double)GetValue(PhaseProperty) * perimeter / thickness),
            DashCap = PenLineCap.Round
        };
        context.DrawRoundedRectangle(null, pen, bounds, radius, radius);
    }
}
