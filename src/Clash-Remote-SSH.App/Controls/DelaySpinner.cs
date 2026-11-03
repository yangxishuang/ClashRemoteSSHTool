using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ClashRemoteSsh.App.Controls;

public sealed class DelaySpinner : Shape
{
    private static readonly Geometry Arc = Geometry.Parse("M 7,1 A 6,6 0 1 1 1,7");
    private readonly RotateTransform _rotation = new();

    static DelaySpinner() => Arc.Freeze();

    public DelaySpinner()
    {
        IsHitTestVisible = false;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _rotation;
        Loaded += (_, _) => _rotation.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.8)) { RepeatBehavior = RepeatBehavior.Forever });
        Unloaded += (_, _) => _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    protected override Geometry DefiningGeometry => Arc;
}
