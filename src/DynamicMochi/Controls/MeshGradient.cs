using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DynamicMochi.Controls;

/// <summary>
/// v1.7.5: live gradient for the colour themes. Three soft blobs in shades of the theme colour drift slowly
/// over the dark background and flow into each other (a "mesh" look). Positions are relative, so it fits
/// the small capsule and the open island alike. Runs at 24 fps to keep the layered window cheap.
/// </summary>
public sealed class MeshGradient : Grid
{
    private static readonly (Point A, Point B, double Seconds, double Rx, double Ry)[] Paths =
    {
        (new Point(0.12, 0.15), new Point(0.42, 0.95), 11, 0.55, 1.15),
        (new Point(0.88, 0.05), new Point(0.62, 0.85), 14, 0.50, 1.05),
        (new Point(0.55, 1.05), new Point(0.95, 0.45), 17, 0.60, 1.20),
    };

    private readonly RadialGradientBrush[] _blobs = new RadialGradientBrush[Paths.Length];
    private bool _running;

    public MeshGradient()
    {
        IsHitTestVisible = false;
        for (int i = 0; i < Paths.Length; i++)
        {
            var p = Paths[i];
            var b = new RadialGradientBrush { RadiusX = p.Rx, RadiusY = p.Ry, Center = p.A, GradientOrigin = p.A };
            b.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            b.GradientStops.Add(new GradientStop(Colors.Transparent, 0.55));
            b.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            _blobs[i] = b;
            Children.Add(new Rectangle { Fill = b });
        }
    }

    /// <summary>Blob colours (alpha = strength).</summary>
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
            var p = Paths[i];
            var anim = new PointAnimation(p.A, p.B, TimeSpan.FromSeconds(p.Seconds))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Timeline.SetDesiredFrameRate(anim, 24);
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
