using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClashRemoteSsh.App.Controls;

/// <summary>A moving segment that follows the rounded outline of an active operation card.</summary>
public sealed class LoadingBorder : FrameworkElement
{
    private Geometry? _outline;
    private Rect _bounds;
    private double _perimeter;
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
        if (_outline is null || bounds != _bounds)
        {
            _bounds = bounds;
            _outline = SmoothBorder.Outline(bounds, 23);
            _perimeter = 0;
            foreach (var figure in _outline.GetFlattenedPathGeometry().Figures)
            {
                var previous = figure.StartPoint;
                foreach (var segment in figure.Segments)
                {
                    var points = segment is PolyLineSegment polyline ? polyline.Points : segment is LineSegment line ? new PointCollection { line.Point } : new PointCollection();
                    foreach (var point in points) { _perimeter += (point - previous).Length; previous = point; }
                }
                if (figure.IsClosed) _perimeter += (figure.StartPoint - previous).Length;
            }
        }
        var perimeter = _perimeter;
        var segmentLength = Math.Min(140, perimeter * 0.18);
        var accent = TryFindResource("Accent") as Brush ?? Brushes.DodgerBlue;
        var track = accent.CloneCurrentValue(); track.Opacity = 0.22;
        context.DrawGeometry(null, new Pen(track, thickness), _outline);
        var pen = new Pen(accent, thickness)
        {
            DashStyle = new DashStyle(new[] { segmentLength / thickness, (perimeter - segmentLength) / thickness }, -(double)GetValue(PhaseProperty) * perimeter / thickness),
            DashCap = PenLineCap.Round
        };
        context.DrawGeometry(null, pen, _outline);
    }
}
