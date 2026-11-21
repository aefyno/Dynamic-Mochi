using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicMochi.Controls;
using DynamicMochi.Services;

namespace DynamicMochi;

/// <summary>
/// v1.7 Liquid Glass and gooey motion.
/// Glass: Windows blurs the desktop behind the window; the window region keeps that blur inside the capsule,
/// the lyric strip and the neck between them. The region follows every animation frame while something moves.
/// </summary>
public partial class IslandWindow
{
    private bool _glassOn;
    private string _glassKey = "";
    private DateTime _glassPumpUntil;
    private bool _glassPumping;
    private bool _stripShown;     // the lyric strip is out (or on its way out) of the capsule

    private static readonly Brush StripRimBrush = MakeStripRim();

    private static Brush MakeStripRim()
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0.2, 0), EndPoint = new Point(0.8, 1) };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x88, 255, 255, 255), 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x1C, 255, 255, 255), 0.5));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x48, 255, 255, 255), 1));
        b.Freeze();
        return b;
    }

    /// <summary>Glass theme on or off: blur, region, rim, and no dark shapes behind the see-through capsule.</summary>
    private void ApplyGlass()
    {
        bool want = _theme?.Glass == true && _hwnd != IntPtr.Zero;
        GlassRim.Visibility = want ? Visibility.Visible : Visibility.Collapsed;
        GlowShape.Visibility = want ? Visibility.Collapsed : Visibility.Visible;
        StripShadowShape.Background = want ? Brushes.Transparent : Brushes.Black;
        if (want)
        {
            LyricsStrip.BorderBrush = StripRimBrush;
            LyricsStrip.BorderThickness = new Thickness(1);
            // the reverse corners of the notch sit outside the blur region, so they would only be cut
            EarLeft.Visibility = EarRight.Visibility = Visibility.Collapsed;
        }
        else
        {
            LyricsStrip.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "BorderBrush");
            EarLeft.Visibility = EarRight.Visibility = _settings.Shape == 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateNeckFill();

        if (want != _glassOn)
        {
            _glassOn = want;
            _glassKey = "";
            Glass.SetBlur(_hwnd, want);
            Repaint();
        }
        if (want) UpdateGlassRegion();
    }

    /// <summary>The neck is painted like the top of the strip, so the drop and the strip read as one liquid.</summary>
    private void UpdateNeckFill()
    {
        // the theme's own colour, not StripTopStop.Color: that one may still be mid-animation from the old theme
        var b = new SolidColorBrush(WithAlpha(_theme.GradTop, 0.9));
        b.Freeze();
        Neck.Fill = b;
    }

    /// <summary>Keep the blur region in step with moving shapes for this long (every rendered frame).</summary>
    private void PumpGlass(double ms)
    {
        if (!_glassOn) return;
        var until = DateTime.UtcNow.AddMilliseconds(ms * Math.Max(1, _settings.AnimationSpeed) + 120);
        if (until > _glassPumpUntil) _glassPumpUntil = until;
        if (_glassPumping) return;
        _glassPumping = true;
        CompositionTarget.Rendering += GlassFrame;
    }

    private void GlassFrame(object? sender, EventArgs e)
    {
        UpdateGlassRegion();
        if (_glassOn && DateTime.UtcNow <= _glassPumpUntil) return;
        CompositionTarget.Rendering -= GlassFrame;
        _glassPumping = false;
    }

    /// <summary>Recomputes the blur shapes; does nothing when they have not moved since the last frame.</summary>
    private void UpdateGlassRegion()
    {
        if (!_glassOn || _hwnd == IntPtr.Zero) return;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return;
        var device = source.CompositionTarget.TransformToDevice;

        var shapes = new List<GlassShape>(4);
        bool notch = _settings.Shape == 1;
        AddRounded(shapes, Capsule, Capsule.CornerRadius.BottomLeft, device, notch);
        if (LyricsStrip.Visibility == Visibility.Visible && LyricsStrip.Opacity > 0.03)
            AddRounded(shapes, LyricsStrip, LyricsStrip.CornerRadius.TopLeft, device, false);
        if (FirstRunHint.Visibility == Visibility.Visible && FirstRunHint.Opacity > 0.03)
            AddRounded(shapes, FirstRunHint, FirstRunHint.CornerRadius.TopLeft, device, false);
        if (Neck.ActualWidth > 0.5) AddOutline(shapes, Neck, device);

        string key = string.Join(";", shapes.Select(s => s.Polygon != null
            ? string.Join(",", s.Polygon.Select(p => $"{p.X:0},{p.Y:0}"))
            : $"{s.Bounds.X:0},{s.Bounds.Y:0},{s.Bounds.Width:0},{s.Bounds.Height:0},{s.RadiusX:0},{s.RadiusY:0}"));
        if (key == _glassKey) return;
        _glassKey = key;
        Glass.SetRegion(_hwnd, shapes);
    }

    private void AddRounded(List<GlassShape> list, FrameworkElement el, double radius, Matrix device, bool flatTop)
    {
        if (!el.IsVisible || el.ActualWidth <= 0 || el.ActualHeight <= 0) return;
        Rect r;
        try { r = el.TransformToAncestor(WindowRoot).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight)); }
        catch (InvalidOperationException) { return; }
        var dev = new Rect(r.X * device.M11, r.Y * device.M22, r.Width * device.M11, r.Height * device.M22);
        double sx = dev.Width / el.ActualWidth, sy = dev.Height / el.ActualHeight;
        list.Add(new GlassShape(dev, radius * sx, radius * sy, flatTop));
    }

    private void AddOutline(List<GlassShape> list, System.Windows.Shapes.Shape shape, Matrix device)
    {
        if (!shape.IsVisible) return;
        GeneralTransform tr;
        try { tr = shape.TransformToAncestor(WindowRoot); }
        catch (InvalidOperationException) { return; }
        var flat = shape.RenderedGeometry.GetFlattenedPathGeometry(0.5, ToleranceType.Absolute);
        var points = new List<Point>();
        foreach (var fig in flat.Figures)
        {
            points.Add(fig.StartPoint);
            foreach (var seg in fig.Segments)
            {
                if (seg is PolyLineSegment pl) points.AddRange(pl.Points);
                else if (seg is LineSegment ln) points.Add(ln.Point);
            }
        }
        if (points.Count < 3) return;
        var dev = points.Select(p => { var q = tr.Transform(p); return new Point(q.X * device.M11, q.Y * device.M22); }).ToArray();
        list.Add(new GlassShape(Rect.Empty, 0, 0, false, dev));
    }

    // ================= Gooey motion

    /// <summary>
    /// Springy size change: one axis leads, the other follows a beat later, both overshoot and settle,
    /// so the capsule stretches like a drop instead of scaling like a box. The overshoot is limited by the
    /// room left in the window, so a tall page never gets cut at the bottom.
    /// </summary>
    private void SpringSize(double w, double h, int ms, bool heightFirst)
    {
        double fromW = Capsule.ActualWidth > 0 ? Capsule.ActualWidth : w;
        double fromH = Capsule.ActualHeight > 0 ? Capsule.ActualHeight : h;
        double room = BaseWindowH - IslandStack.Margin.Top - h - 6;
        double dh = Math.Abs(h - fromH);
        double hOver = dh < 1 ? 0.1 : Math.Clamp(Math.Min(0.16, room / dh), 0.02, 0.16);
        double wOver = Math.Clamp(Math.Min(0.11, (BaseWindowW - w - 8) / Math.Max(1, Math.Abs(w - fromW))), 0.02, 0.11);

        var dur = Dur(ms);
        var lag = Dur(45);
        var wa = new DoubleAnimation(w, dur) { EasingFunction = SpringEase.WithOvershoot(wOver, 10), BeginTime = heightFirst ? lag : TimeSpan.Zero };
        var ha = new DoubleAnimation(h, dur) { EasingFunction = SpringEase.WithOvershoot(hOver, 11), BeginTime = heightFirst ? TimeSpan.Zero : lag };
        (heightFirst ? wa : ha).Completed += (_, _) => Repaint();
        Capsule.BeginAnimation(FrameworkElement.WidthProperty, wa);
        Capsule.BeginAnimation(FrameworkElement.HeightProperty, ha);
        PumpGlass(ms + 60);
    }

    /// <summary>Jelly wobble on a track change: wider and flatter for a moment, then it shivers back.</summary>
    private void Jelly()
    {
        double w = _expanded ? ExpandedW : CollapsedWidth;
        double h = _expanded ? PageHeight(_page) : CollapsedSize.H;
        var spring = new SpringEase(5.5, 19);
        var wa = new DoubleAnimation(w * 1.06, w, Dur(620)) { EasingFunction = spring };
        var ha = new DoubleAnimation(h * 0.9, h, Dur(620)) { EasingFunction = spring };
        wa.Completed += (_, _) => Repaint();
        Capsule.BeginAnimation(FrameworkElement.WidthProperty, wa);
        Capsule.BeginAnimation(FrameworkElement.HeightProperty, ha);
        PumpGlass(640);
    }

    /// <summary>Hover anticipation: the capsule swells a little, like a drop about to split.</summary>
    private void Swell(double to, int ms)
    {
        var a = new DoubleAnimation(to, Dur(ms)) { EasingFunction = new SpringEase(7, 14) };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to + (to - 1) * 0.6, Dur(ms)) { EasingFunction = new SpringEase(6, 14) });
        PumpGlass(ms);
    }

    /// <summary>
    /// The lyric strip leaves the capsule like a drop: it starts squeezed inside the capsule's bottom edge,
    /// stretches down on a thinning neck, the neck snaps and the strip settles with a wobble.
    /// Hiding runs it backwards: the strip is sucked back into the capsule.
    /// </summary>
    private void GooStrip(bool show)
    {
        double up = -(StripHost.Margin.Top + Math.Max(18, LyricsStrip.ActualHeight * 0.6));
        if (show)
        {
            StripScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.32, 1, Dur(640)) { EasingFunction = new SpringEase(5.5, 12) });
            StripScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.4, 1, Dur(640)) { EasingFunction = new SpringEase(5, 13), BeginTime = Dur(30) });
            StripShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(up, 0, Dur(600)) { EasingFunction = new SpringEase(6, 11) });
            LyricsStrip.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Dur(160)) { BeginTime = Dur(40) });

            var neck = new DoubleAnimationUsingKeyFrames();
            neck.KeyFrames.Add(new EasingDoubleKeyFrame(0, Dur(0)));
            neck.KeyFrames.Add(new EasingDoubleKeyFrame(Math.Min(84, Capsule.ActualWidth * 0.3), Dur(150), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            neck.KeyFrames.Add(new EasingDoubleKeyFrame(0, Dur(380), new CubicEase { EasingMode = EasingMode.EaseIn }));
            Neck.BeginAnimation(WidthProperty, neck);
            PumpGlass(680);
        }
        else
        {
            var ease = new BackEase { EasingMode = EasingMode.EaseIn, Amplitude = 0.35 };
            StripScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.3, Dur(260)) { EasingFunction = ease });
            StripScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.4, Dur(260)) { EasingFunction = ease });
            StripShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(up, Dur(260)) { EasingFunction = ease });

            var neck = new DoubleAnimationUsingKeyFrames();
            neck.KeyFrames.Add(new EasingDoubleKeyFrame(Math.Min(70, Capsule.ActualWidth * 0.25), Dur(140), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            neck.KeyFrames.Add(new EasingDoubleKeyFrame(0, Dur(260)));
            Neck.BeginAnimation(WidthProperty, neck);

            var fade = new DoubleAnimation(0, Dur(110)) { BeginTime = Dur(150) };
            fade.Completed += (_, _) =>
            {
                if (LyricsStrip.Opacity < 0.01)
                {
                    LyricsStrip.Visibility = Visibility.Collapsed;
                    StripScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    StripScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    StripShift.BeginAnimation(TranslateTransform.YProperty, null);
                }
                Repaint();
                UpdateGlassRegion();
            };
            LyricsStrip.BeginAnimation(OpacityProperty, fade);
            PumpGlass(300);
        }
    }
}
