using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DynamicMochi.Controls;

/// <summary>
/// v1.7.5: live gradient for the colour themes. Soft blobs in shades of the theme colour drift slowly
/// over the dark background and flow into each other (a "mesh" look). Positions are relative, so it fits
/// the small capsule and the open island alike. v1.8: slower drift (16/21/26 s) at 30 fps: smooth and cheap.
/// </summary>
public sealed class MeshGradient : Grid
{
    // v1.8.2: four blobs that circle on their own slow orbits (not back and forth along a line),
    // so the colour wanders around the island instead of sliding from corner to corner.
    private static readonly (Point Center, double Ox, double Oy, double Seconds, bool Clockwise, double Rx, double Ry)[] Orbits =
    {
        (new Point(0.25, 0.35), 0.20, 0.30, 24, true, 0.55, 1.10),
        (new Point(0.78, 0.30), 0.18, 0.34, 31, false, 0.50, 1.00),
        (new Point(0.50, 0.90), 0.30, 0.18, 27, true, 0.60, 1.10),
        (new Point(0.62, 0.55), 0.24, 0.24, 37, false, 0.34, 0.70),
    };

    private readonly RadialGradientBrush[] _blobs = new RadialGradientBrush[Orbits.Length];
    private bool _running;

    public MeshGradient()
    {
        IsHitTestVisible = false;
        for (int i = 0; i < Orbits.Length; i++)
        {
            var o = Orbits[i];
            var start = new Point(o.Center.X + o.Ox, o.Center.Y);
            var b = new RadialGradientBrush { RadiusX = o.Rx, RadiusY = o.Ry, Center = start, GradientOrigin = start };
            b.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            b.GradientStops.Add(new GradientStop(Colors.Transparent, 0.55));
            b.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            _blobs[i] = b;
            Children.Add(new Rectangle { Fill = b });
        }
    }

    /// <summary>Blob colours (alpha = strength); fewer colours than blobs repeat the last one.</summary>
    public void SetColors(params Color[] colors)
    {
        for (int i = 0; i < _blobs.Length; i++)
        {
            var c = colors[Math.Min(i, colors.Length - 1)];
            var stops = _blobs[i].GradientStops;
            stops[0].Color = c;
            stops[1].Color = Color.FromArgb((byte)(c.A * 0.45), c.R, c.G, c.B);
            stops[2].Color = Color.FromArgb(0, c.R, c.G, c.B);   // same RGB at zero alpha: no grey fringe
        }
    }

    /// <summary>Starts or stops the drift; calling it again with the same value changes nothing.</summary>
    public void Run(bool on)
    {
        if (on == _running) return;
        _running = on;
        for (int i = 0; i < _blobs.Length; i++)
        {
            var b = _blobs[i];
            if (!on)
            {
                b.BeginAnimation(RadialGradientBrush.CenterProperty, null);
                b.BeginAnimation(RadialGradientBrush.GradientOriginProperty, null);
                continue;
            }
            var o = Orbits[i];
            var path = PathGeometry.CreateFromGeometry(new EllipseGeometry(o.Center, o.Ox, o.Oy));
            if (!o.Clockwise) path.Transform = new ScaleTransform(-1, 1, o.Center.X, o.Center.Y);
            path.Freeze();
            var anim = new PointAnimationUsingPath
            {
                PathGeometry = path,
                Duration = TimeSpan.FromSeconds(o.Seconds),
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Timeline.SetDesiredFrameRate(anim, 30);
            b.BeginAnimation(RadialGradientBrush.CenterProperty, anim);
            b.BeginAnimation(RadialGradientBrush.GradientOriginProperty, anim);
        }
    }

    /// <summary>Rotates the hue by <paramref name="degrees"/> and mixes towards white by <paramref name="light"/> (0…1).</summary>
    public static Color Shift(Color c, double degrees, double light, double alpha)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 1e-6)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        double s = max <= 0 ? 0 : d / max, v = max;
        h = ((h + degrees) % 360 + 360) % 360;
        double cc = v * s, x = cc * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - cc;
        (double R, double G, double B) = h switch
        {
            < 60 => (cc, x, 0.0), < 120 => (x, cc, 0.0), < 180 => (0.0, cc, x),
            < 240 => (0.0, x, cc), < 300 => (x, 0.0, cc), _ => (cc, 0.0, x),
        };
        byte To(double u) => (byte)Math.Round(Math.Clamp((u + m) + (1 - (u + m)) * light, 0, 1) * 255);
        return Color.FromArgb((byte)Math.Round(alpha * 255), To(R), To(G), To(B));
    }
}
