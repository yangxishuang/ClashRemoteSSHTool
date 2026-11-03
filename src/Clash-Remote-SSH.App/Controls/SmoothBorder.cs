using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClashRemoteSsh.App.Controls;

public class SmoothBorder : Border
{
    private Rect _bounds;
    private double _radius;
    private Geometry? _geometry;

    public static Geometry Outline(Rect bounds, double radius)
    {
        var r = Math.Clamp(radius, 0, Math.Min(bounds.Width, bounds.Height) / 2);
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new(bounds.Left + r, bounds.Top), true, true);
            var corners = new[]
            {
                (bounds.Right - r, bounds.Top + r, -Math.PI / 2),
                (bounds.Right - r, bounds.Bottom - r, 0d),
                (bounds.Left + r, bounds.Bottom - r, Math.PI / 2),
                (bounds.Left + r, bounds.Top + r, Math.PI)
            };
            foreach (var (x, y, start) in corners)
                for (var sample = 0; sample <= 18; sample++)
                {
                    var angle = start + sample * Math.PI / 36;
                    var cosine = Math.Cos(angle); var sine = Math.Sin(angle);
                    path.LineTo(new(x + r * Math.Sign(cosine) * Math.Pow(Math.Abs(cosine), 0.6),
                        y + r * Math.Sign(sine) * Math.Pow(Math.Abs(sine), 0.6)), true, false);
                }
        }
        geometry.Freeze(); return geometry;
    }

    protected override void OnRender(DrawingContext context)
    {
        var thickness = BorderThickness.Left;
        var bounds = new Rect(thickness / 2, thickness / 2, Math.Max(0, ActualWidth - thickness), Math.Max(0, ActualHeight - thickness));
        if (bounds.IsEmpty || bounds.Width == 0 || bounds.Height == 0) return;
        var radius = Math.Max(0, CornerRadius.TopLeft - thickness / 2);
        if (_geometry is null || bounds != _bounds || radius != _radius)
        { _bounds = bounds; _radius = radius; _geometry = Outline(bounds, radius); }
        context.DrawGeometry(Background, thickness > 0 && BorderBrush != null ? new Pen(BorderBrush, thickness) : null, _geometry);
    }
}
