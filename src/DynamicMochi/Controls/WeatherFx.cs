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

    /// <summary>"Спокойный" live background: no cloud shadows, no wind lines.</summary>
    public bool Calm { get; set; }

    /// <summary>Partly cloudy: the sun sometimes peeks out between the clouds.</summary>
    public bool Partly { get; set; }

    /// <summary>Night: clouds lit from below by cold moonlight, stars in the gaps.</summary>
    public bool Night { get; set; }

    /// <summary>Real wind (m/s) and the direction it comes from (degrees): speed and direction of clouds and wind lines.</summary>
    public double WindMs { get; set; } = 3;
    public double WindFromDeg { get; set; } = 270;

    // clouds: 3 parallax layers, shadows, the sun peeking, wind lines
    private sealed class Cloud { public double X, Y, Scale, Seed; public int Layer; }
    private readonly List<Cloud> _clouds = new();
    private readonly List<P> _cloudStars = new();
    private double _nextShadow = 6, _shadowAt = -100, _shadowDur = 5, _shadowY;
    private double _nextPeek = 4, _peekAt = -100, _peekDur = 4;
    private readonly List<(double X, double Y, double Born, double Len)> _gusts = new();
    private double _nextGust = 1;

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
            Fx.Clouds => 0,
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

        _clouds.Clear();
        _cloudStars.Clear();
        _gusts.Clear();
        if (_kind == Fx.Clouds)
        {
            // far → near: smaller, paler and slower at the back
            for (int layer = 0; layer < 3; layer++)
                for (int i = 0; i < 3; i++)
                    _clouds.Add(new Cloud
                    {
                        Layer = layer,
                        X = (i + _rng.NextDouble() * 0.6) / 3 * 1.4 - 0.2,
                        Y = 0.08 + layer * 0.22 + _rng.NextDouble() * 0.18,
                        Scale = (0.6 + layer * 0.35) * (0.8 + _rng.NextDouble() * 0.4),
                        Seed = _rng.NextDouble() * 1000,
                    });
            for (int i = 0; i < 24; i++)
                _cloudStars.Add(new P { X = _rng.NextDouble(), Y = _rng.NextDouble() * 0.8, Size = _rng.NextDouble(), Phase = _rng.NextDouble() * 6.28, Speed = 0.6 + _rng.NextDouble() });
        }
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
                    break;                                   // drawn from _clouds
                case Fx.Fog:
                    p.X += dt * (p.Phase > Math.PI ? 1 : -1) * (5 + 6 * p.Speed) / w;
                    break;
            }
            if (p.Y > 1.05) { p.Y -= 1.1; p.X = _rng.NextDouble(); }
            if (p.X > 1.4) p.X -= 1.8;
            if (p.X < -0.4) p.X += 1.8;
        }

        if (_kind == Fx.Clouds) StepClouds(dt, w, h);

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
                DrawClouds(dc, w, h, m, ink);
                break;
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

    // ================= Clouds

    /// <summary>Unit vector of where the wind blows to (screen x), at least a slow drift.</summary>
    private double WindDirX => Math.Sin((WindFromDeg + 180) * Math.PI / 180) is var x && Math.Abs(x) < 0.25 ? (x < 0 ? -0.25 : 0.25) : Math.Sin((WindFromDeg + 180) * Math.PI / 180);

    private void StepClouds(double dt, double w, double h)
    {
        double speed = Math.Clamp(5 + WindMs * 2.2, 5, 55);           // px/s for the nearest layer
        foreach (var c in _clouds)
        {
            double k = c.Layer switch { 0 => 0.35, 1 => 0.6, _ => 1.0 };
            c.X += WindDirX * speed * k * dt / w;
            if (c.X > 1.35) { c.X -= 1.7; c.Y = 0.08 + c.Layer * 0.22 + _rng.NextDouble() * 0.18; }
            if (c.X < -0.35) { c.X += 1.7; c.Y = 0.08 + c.Layer * 0.22 + _rng.NextDouble() * 0.18; }
        }
        if (!Calm && _t >= _nextShadow)
        {
            _shadowAt = _t;
            _shadowDur = 4 + _rng.NextDouble() * 2;
            _shadowY = 0.3 + _rng.NextDouble() * 0.5;
            _nextShadow = _t + 15 + _rng.NextDouble() * 10;
        }
        if (Partly && !Night && _t >= _nextPeek)
        {
            _peekAt = _t;
            _peekDur = 3 + _rng.NextDouble() * 2;
            _nextPeek = _t + _peekDur + 6 + _rng.NextDouble() * 8;
        }
        // strong wind: a couple of thin fast lines
        _gusts.RemoveAll(g => _t - g.Born > 0.9);
        if (!Calm && WindMs >= 8 && _t >= _nextGust && _gusts.Count < 2)
        {
            _gusts.Add((_rng.NextDouble(), 0.15 + _rng.NextDouble() * 0.7, _t, 40 + _rng.NextDouble() * 50));
            _nextGust = _t + 0.5 + _rng.NextDouble() * 1.2;
        }
    }

    private static readonly Dictionary<long, RadialGradientBrush> PuffCache = new();

    /// <summary>Soft round puff: colour in the centre fading to transparent (cached per colour and alpha).</summary>
    private static RadialGradientBrush Puff(Color c, double alpha)
    {
        byte a = (byte)Math.Clamp(Math.Round(alpha * 255 / 4) * 4, 0, 255);
        long key = ((long)a << 24) | ((long)c.R << 16) | ((long)c.G << 8) | c.B;
        if (!PuffCache.TryGetValue(key, out var b))
        {
            b = new RadialGradientBrush();
            b.GradientStops.Add(new GradientStop(Color.FromArgb(a, c.R, c.G, c.B), 0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(a * 0.55), c.R, c.G, c.B), 0.55));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
            b.Freeze();
            PuffCache[key] = b;
        }
        return b;
    }

    private void DrawClouds(DrawingContext dc, double w, double h, double m, Color ink)
    {
        // the sun peeking between the clouds: halo grows for a few seconds, then hides again
        double peek = Envelope(_t - _peekAt, _peekDur);
        if (Partly && peek > 0.01)
        {
            var sun = Tint ?? Color.FromRgb(0xFF, 0xD6, 0x0A);
            double r = Math.Min(w, h) * (0.45 + 0.15 * peek);
            dc.DrawEllipse(Puff(sun, 0.42 * peek * m), null, new Point(w * 0.82, h * 0.08), r, r);
        }

        // night: stars twinkle in the gaps (drawn first, clouds cover them partly)
        if (Night)
        {
            var star = Tint ?? ink;
            foreach (var s in _cloudStars)
            {
                double tw = 0.2 + 0.8 * (0.5 + 0.5 * Math.Sin(_t * s.Speed + s.Phase));
                double size = 0.6 + s.Size * 0.5;
                dc.DrawEllipse(Br(star, 0.7 * tw * m), null, new Point(s.X * w, s.Y * h), size, size);
            }
        }

        var moon = Color.FromRgb(0xA9, 0xC4, 0xFF);
        foreach (var c in _clouds.OrderBy(c => c.Layer))
        {
            double alpha = c.Layer switch { 0 => 0.10, 1 => 0.15, _ => 0.22 };
            double cw = w * 0.22 * c.Scale, ch = h * 0.13 * c.Scale;
            double cx = c.X * w, cy = c.Y * h;
            // a cloud = several puffs: a wide base and rounded tops, slowly breathing
            for (int i = 0; i < 5; i++)
            {
                double f = i / 4.0;
                double ox = (f - 0.5) * cw * 1.5;
                double bump = Math.Sin(f * Math.PI);
                double breathe = 1 + 0.05 * Math.Sin(_t * 0.4 + c.Seed + i);
                double rx = cw * (0.42 + 0.25 * bump) * breathe, ry = ch * (0.55 + 0.45 * bump) * breathe;
                double oy = -bump * ch * 0.35;
                dc.DrawEllipse(Puff(ink, alpha * m), null, new Point(cx + ox, cy + oy), rx, ry);
            }
            if (Night && c.Layer > 0)
                dc.DrawEllipse(Puff(moon, alpha * 0.8 * m), null, new Point(cx, cy + ch * 0.45), cw * 0.9, ch * 0.35);   // cold light from below
        }

        // cloud shadow passing over the content
        double sh = Envelope(_t - _shadowAt, _shadowDur);
        if (!Calm && sh > 0.01)
        {
            double k = (_t - _shadowAt) / _shadowDur;
            double x = WindDirX >= 0 ? -0.3 + k * 1.6 : 1.3 - k * 1.6;
            dc.DrawEllipse(Puff(Colors.Black, 0.16 * sh * m), null, new Point(x * w, _shadowY * h), w * 0.45, h * 0.5);
        }

        foreach (var g in _gusts)
        {
            double k = (_t - g.Born) / 0.9;
            double x = (WindDirX >= 0 ? g.X - 0.2 + k * 0.7 : g.X + 0.2 - k * 0.7) * w;
            double y = g.Y * h;
            double a = Math.Sin(k * Math.PI) * 0.3 * m;
            double len = g.Len * (WindDirX >= 0 ? -1 : 1);
            dc.DrawLine(new Pen(Br(ink, a), 1), new Point(x, y), new Point(x + len, y + 2));
        }
    }

    /// <summary>0 → 1 → 0 over the duration (smooth in and out), 0 outside.</summary>
    private static double Envelope(double t, double dur)
    {
        if (t < 0 || t > dur) return 0;
        double k = t / dur;
        return Math.Sin(k * Math.PI) is var s && s > 0 ? Math.Min(1, s * 1.6) : 0;
    }

    /// <summary>Flash envelope: 120 ms at full, then a fast fade.</summary>
    private static double Flash(double dt)
    {
        if (dt < 0) return 0;
        if (dt < 0.12) return 1;
        return Math.Max(0, 1 - (dt - 0.12) / 0.25);
    }
}
