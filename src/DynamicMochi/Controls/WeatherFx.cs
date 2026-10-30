using System.Windows;
using System.Windows.Media;
using DynamicMochi.Services;

namespace DynamicMochi.Controls;

/// <summary>
/// Live weather background for the weather tab. One element drawn in OnRender at up to 30 fps,
/// at most ~150 particles, fully stopped when not visible (Stop()).
/// </summary>
public sealed class WeatherFx : FrameworkElement
{
    public enum Fx { None, Sun, Stars, Clouds, Fog, Drizzle, Rain, Snow, Thunder }

    private sealed class P   // particle in normalised coordinates
    {
        public double X, Y, Size, Speed, Phase, Alpha;
    }

    private readonly Random _rng = new(7);
    private readonly List<P> _ps = new();
    private Fx _kind = Fx.None, _pending = Fx.None;
    private int _rainCount = 80;
    private double _mix = 1;                 // crossfade between kinds: 1 → 0 → switch → 1 (800 ms)
    private bool _switching;
    private bool _running;
    private TimeSpan _last = TimeSpan.Zero;
    private double _t;                       // seconds of animation time
    private double _nextFlash = 8, _flashAt = -10, _secondFlashAt = -10;
    private double _nextShoot = 12, _shootAt = -10;
    private double _shootX, _shootY;

    /// <summary>Particle colour (white in dark themes, #5E7A99 in the white theme).</summary>
    public Color Ink { get; set; } = Colors.White;

    /// <summary>Tint for the sun and stars (accent of coloured themes); null = natural colours.</summary>
    public Color? Tint { get; set; }

    public Fx Kind => _kind;

    public static Fx ForCode(int wmo, bool isDay) => wmo switch
    {
        0 or 1 => isDay ? Fx.Sun : Fx.Stars,
        2 or 3 => Fx.Clouds,
        45 or 48 => Fx.Fog,
        >= 51 and <= 57 => Fx.Drizzle,
        (>= 61 and <= 67) or (>= 80 and <= 82) => Fx.Rain,
        (>= 71 and <= 77) or 85 or 86 => Fx.Snow,
        >= 95 => Fx.Thunder,
        _ => Fx.Clouds,
    };

    /// <summary>Rain amount by WMO code: light ≈ 40, moderate ≈ 80, heavy ≈ 140 drops.</summary>
    public static int RainAmount(int wmo) => wmo switch
    {
        61 or 66 or 80 => 40,
        65 or 67 or 82 or >= 95 => 140,
        _ => 80,
    };

    public WeatherFx()
    {
        IsHitTestVisible = false;
        SizeChanged += (_, _) => Seed();
    }

    public void SetKind(Fx kind, int rain = 80, bool animate = true)
    {
        if (kind == _kind && rain == _rainCount && !_switching) return;
        _rainCount = Math.Clamp(rain, 20, 140);
        if (!animate || _kind == Fx.None)
        {
            _kind = kind;
            _switching = false;
            _mix = 1;
            Seed();
            InvalidateVisual();
            return;
        }
        _pending = kind;
        _switching = true;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _last = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs re) return;
        if (_last == TimeSpan.Zero) { _last = re.RenderingTime; return; }
        double dt = (re.RenderingTime - _last).TotalSeconds;
        if (dt < 1.0 / 31) return;            // 30 fps cap
        _last = re.RenderingTime;
        dt = Math.Min(dt, 0.1);
        _t += dt;

        if (_switching)
        {
            _mix -= dt / 0.4;
            if (_mix <= 0)
            {
                _mix = 0;
                _kind = _pending;
                _switching = false;
                Seed();
            }
        }
        else if (_mix < 1) _mix = Math.Min(1, _mix + dt / 0.4);

        Step(dt);
        InvalidateVisual();
    }

    private void Seed()
    {
        _ps.Clear();
        int n = _kind switch
        {
            Fx.Stars => 34,
            Fx.Clouds => 4,
            Fx.Fog => 3,
            Fx.Drizzle => 26,
            Fx.Rain or Fx.Thunder => _rainCount,
            Fx.Snow => 60,
            _ => 0,
        };
        for (int i = 0; i < n; i++)
        {
            _ps.Add(new P
            {
                X = _rng.NextDouble(),
                Y = _rng.NextDouble(),
                Size = _rng.NextDouble(),
                Speed = 0.6 + _rng.NextDouble() * 0.8,
                Phase = _rng.NextDouble() * Math.PI * 2,
                Alpha = _rng.NextDouble(),
            });
        }
        if (_kind == Fx.Clouds || _kind == Fx.Fog)
            for (int i = 0; i < _ps.Count; i++) _ps[i].Y = (i + 0.5) / _ps.Count;
    }

    private void Step(double dt)
    {
        double w = Math.Max(1, ActualWidth), h = Math.Max(1, ActualHeight);
        foreach (var p in _ps)
        {
            switch (_kind)
            {
                case Fx.Rain:
                case Fx.Thunder:
                    p.Y += dt * (520 + 240 * p.Speed) / h;
                    p.X += dt * 120 / w;
                    break;
                case Fx.Drizzle:
                    p.Y += dt * (200 + 80 * p.Speed) / h;
                    p.X += dt * 30 / w;
                    break;
                case Fx.Snow:
                    p.Y += dt * (18 + 30 * p.Speed) / h;
                    break;
                case Fx.Clouds:
                    p.X += dt * (6 + 10 * p.Speed) / w;     // parallax: different speeds
                    break;
                case Fx.Fog:
                    p.X += dt * (p.Phase > Math.PI ? 1 : -1) * (5 + 6 * p.Speed) / w;
                    break;
            }
            if (p.Y > 1.05) { p.Y -= 1.1; p.X = _rng.NextDouble(); }
            if (p.X > 1.4) p.X -= 1.8;
            if (p.X < -0.4) p.X += 1.8;
        }

        if (_kind == Fx.Thunder && _t >= _nextFlash)
        {
            _flashAt = _t;
            _secondFlashAt = _rng.NextDouble() < 0.35 ? _t + 0.25 : -10;
            _nextFlash = _t + 6 + _rng.NextDouble() * 8;
        }
        if (_kind == Fx.Stars && _t >= _nextShoot)
        {
            _shootAt = _t;
            _shootX = 0.2 + _rng.NextDouble() * 0.5;
            _shootY = _rng.NextDouble() * 0.3;
            _nextShoot = _t + 16 + _rng.NextDouble() * 8;
        }
    }

    private static readonly Dictionary<int, SolidColorBrush> BrushCache = new();

    private static SolidColorBrush Br(Color c, double alpha)
    {
        byte a = (byte)Math.Clamp(Math.Round(alpha * 255 / 8) * 8, 0, 255);   // quantised to keep the cache small
        int key = (a << 24) | (c.R << 16) | (c.G << 8) | c.B;
        if (!BrushCache.TryGetValue(key, out var b))
        {
            b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
            b.Freeze();
            BrushCache[key] = b;
        }
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0 || _kind == Fx.None) return;
        double m = _mix;
        var ink = Ink;

        switch (_kind)
        {
            case Fx.Sun:
            {
                var sun = Tint ?? Color.FromRgb(0xFF, 0xD6, 0x0A);
                var c = new Point(w * 0.88, h * 0.06);
                double breathe = 1 + 0.06 * (0.5 + 0.5 * Math.Sin(_t * 2 * Math.PI / 6));
                double r = Math.Min(w, h) * 0.55 * breathe;
                var halo = new RadialGradientBrush(Color.FromArgb((byte)(110 * m), sun.R, sun.G, sun.B), Color.FromArgb(0, sun.R, sun.G, sun.B));
                dc.DrawEllipse(halo, null, c, r, r);
                // slow rays
                for (int i = 0; i < 4; i++)
                {
                    double a = _t * 0.06 + i * Math.PI / 2 + 0.4;
                    var geo = new StreamGeometry();
                    using (var g = geo.Open())
                    {
                        double len = r * 2.2, spread = 0.07;
                        g.BeginFigure(c, true, true);
                        g.LineTo(new Point(c.X + Math.Cos(a - spread) * len, c.Y + Math.Sin(a - spread) * len), false, false);
                        g.LineTo(new Point(c.X + Math.Cos(a + spread) * len, c.Y + Math.Sin(a + spread) * len), false, false);
                    }
                    var ray = new RadialGradientBrush(Color.FromArgb((byte)(40 * m), sun.R, sun.G, sun.B), Color.FromArgb(0, sun.R, sun.G, sun.B))
                    {
                        MappingMode = BrushMappingMode.Absolute, Center = c, GradientOrigin = c, RadiusX = r * 2.2, RadiusY = r * 2.2,
                    };
                    dc.DrawGeometry(ray, null, geo);
                }
                break;
            }
            case Fx.Stars:
            {
                var star = Tint ?? ink;
                foreach (var p in _ps)
                {
                    double tw = 0.25 + 0.75 * (0.5 + 0.5 * Math.Sin(_t * (0.8 + p.Speed) + p.Phase));
                    double size = 0.6 + p.Size * 0.6;
                    dc.DrawEllipse(Br(star, 0.9 * tw * m), null, new Point(p.X * w, p.Y * h * 0.85), size, size);
                }
                double k = (_t - _shootAt) / 0.8;
                if (k >= 0 && k <= 1)
                {
                    double x = (_shootX + k * 0.35) * w, y = (_shootY + k * 0.25) * h;
                    var pen = new Pen(new LinearGradientBrush(Color.FromArgb(0, star.R, star.G, star.B), Color.FromArgb((byte)(200 * (1 - k) * m), star.R, star.G, star.B), 0), 1.2);
                    dc.DrawLine(pen, new Point(x - 40, y - 28), new Point(x, y));
                }
                break;
            }
            case Fx.Clouds:
            {
                foreach (var p in _ps)
                {
                    double cw = w * (0.35 + p.Size * 0.25), ch = h * (0.18 + p.Size * 0.12);
                    var brush = new RadialGradientBrush(Color.FromArgb((byte)(48 * m), ink.R, ink.G, ink.B), Color.FromArgb(0, ink.R, ink.G, ink.B));
                    dc.DrawEllipse(brush, null, new Point(p.X * w, (0.08 + p.Y * 0.7) * h), cw, ch);
                }
                break;
            }
            case Fx.Fog:
            {
                foreach (var p in _ps)
                {
                    var brush = new RadialGradientBrush(Color.FromArgb((byte)(42 * m), ink.R, ink.G, ink.B), Color.FromArgb(0, ink.R, ink.G, ink.B));
                    dc.DrawEllipse(brush, null, new Point(p.X * w, (0.2 + p.Y * 0.7) * h), w * 0.8, h * 0.09);
                }
                break;
            }
            case Fx.Drizzle:
            case Fx.Rain:
            case Fx.Thunder:
            {
                bool drizzle = _kind == Fx.Drizzle;
                foreach (var p in _ps)
                {
                    double len = drizzle ? 6 : 10 + p.Size * 6;
                    double x = p.X * w, y = p.Y * h;
                    double fade = Math.Clamp((h - y) / 30, 0, 1);            // drops vanish at the bottom, no splashes
                    double alpha = (drizzle ? 0.18 : 0.25 + p.Alpha * 0.15) * fade * m;
                    if (alpha <= 0.01) continue;
                    dc.DrawLine(new Pen(Br(ink, alpha), 1), new Point(x, y), new Point(x - len * 0.22, y - len));
                }
                if (_kind == Fx.Thunder)
                {
                    double f = Flash(_t - _flashAt) + Flash(_t - _secondFlashAt) * 0.7;
                    if (f > 0.001) dc.DrawRectangle(Br(Colors.White, 0.15 * Math.Min(1, f) * m), null, new Rect(0, 0, w, h));
                }
                break;
            }
            case Fx.Snow:
            {
                foreach (var p in _ps)
                {
                    double x = p.X * w + Math.Sin(_t * (0.6 + p.Speed * 0.5) + p.Phase) * 8;
                    double y = p.Y * h;
                    double r = 1 + p.Size * 1.1;                              // 2–4 px flakes
                    double fade = Math.Clamp((h - y) / 30, 0, 1);
                    dc.DrawEllipse(Br(ink, (0.35 + 0.4 * p.Speed / 1.4) * fade * m), null, new Point(x, y), r, r);
                }
                break;
            }
        }
    }

    /// <summary>Flash envelope: 120 ms at full, then a fast fade.</summary>
    private static double Flash(double dt)
    {
        if (dt < 0) return 0;
        if (dt < 0.12) return 1;
        return Math.Max(0, 1 - (dt - 0.12) / 0.25);
    }
}
