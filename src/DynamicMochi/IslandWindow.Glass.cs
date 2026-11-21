using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using DynamicMochi.Controls;
using DynamicMochi.Services;

namespace DynamicMochi;

/// <summary>
/// v1.7 Liquid Glass and gooey motion.
/// Glass (v1.7.1): the screen area behind the island is copied ~30 times a second (the island itself is excluded
/// from capture) and drawn inside the capsule and the lyric strip through the LiquidGlass pixel shader:
/// blur, lens bending at the rim, colour split, adaptive tint and rim light.
/// </summary>
public partial class IslandWindow
{
    private bool _glassOn;          // the glass theme is active
    private bool _glassShader;      // the liquid shader loaded (otherwise a plain blur)
    private bool _stripShown;       // the lyric strip is out (or on its way out) of the capsule
    private Glass? _glass;
    private DispatcherTimer? _glassTimer;
    private Effect? _capsuleFx, _stripFx;
    private ImageBrush? _capsuleBack, _stripBack;
    private DateTime _glassFastUntil;


    /// <summary>Glass theme on or off.</summary>
    private void ApplyGlass()
    {
        bool want = _theme?.Glass == true && _hwnd != IntPtr.Zero;
        if (want != _glassOn)
        {
            _glassOn = want;
            if (want) StartGlass();
            else StopGlass();
        }
        UpdateNeckFill();
        if (!want)
        {
            EarLeft.Visibility = EarRight.Visibility = _settings.Shape == 1 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        // the notch's reverse corners are solid black, which reads wrong next to glass
        EarLeft.Visibility = EarRight.Visibility = Visibility.Collapsed;
        // the strip is see-through: the glass behind it does the work
        StripTopStop.BeginAnimation(GradientStop.ColorProperty, null);
        StripEndStop.BeginAnimation(GradientStop.ColorProperty, null);
        StripTopStop.Color = StripEndStop.Color = _glassShader ? Colors.Transparent : WithAlpha(_theme!.GradTop, 0.9);
        GlassTick();
    }

    private void StartGlass()
    {
        Glass.ExcludeFromCapture(_hwnd, true);
        _glass = new Glass();
        try
        {
            _capsuleFx = new LiquidGlassEffect();
            _stripFx = new LiquidGlassEffect { Refraction = 9 };
            _glassShader = true;
        }
        catch (Exception ex)
        {
            // a build without the compiled shader: frosted blur only
            Log.Write("glass shader: " + ex.Message);
            _capsuleFx = new BlurEffect { Radius = 10, KernelType = KernelType.Gaussian };
            _stripFx = new BlurEffect { Radius = 10, KernelType = KernelType.Gaussian };
            _glassShader = false;
        }
        _capsuleBack = MakeBackBrush();
        _stripBack = MakeBackBrush();
        GlassBackdrop.Fill = _capsuleBack;
        GlassBackdrop.Effect = _capsuleFx;
        GlassBackdrop.Opacity = _glassShader ? 1 : 0.55;   // without the shader the theme tint underneath darkens it
        GlassBackdrop.Visibility = Visibility.Visible;
        GlassRim.Visibility = _glassShader ? Visibility.Collapsed : Visibility.Visible;   // the shader lights its own rim
        StripShadowShape.Background = _stripBack;
        StripShadowShape.Effect = _stripFx;

        _glassTimer ??= new DispatcherTimer(DispatcherPriority.Render, Dispatcher);
        _glassTimer.Interval = TimeSpan.FromMilliseconds(33);
        _glassTimer.Tick -= GlassTimer_Tick;
        _glassTimer.Tick += GlassTimer_Tick;
        _glassTimer.Start();
        Log.Write($"glass: on, shader {(_glassShader ? "liquid" : "blur fallback")}");
    }

    private void StopGlass()
    {
        _glassTimer?.Stop();
        Glass.ExcludeFromCapture(_hwnd, false);
        _glass?.Dispose();
        _glass = null;
        GlassBackdrop.Visibility = Visibility.Collapsed;
        GlassBackdrop.Fill = null;
        GlassBackdrop.Effect = null;
        GlassRim.Visibility = Visibility.Collapsed;
        StripShadowShape.Background = Brushes.Black;
        StripShadowShape.Effect = StripShadow;
        _capsuleBack = _stripBack = null;
        _capsuleFx = _stripFx = null;
    }

    private static ImageBrush MakeBackBrush()
    {
        var b = new ImageBrush { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox };
        RenderOptions.SetBitmapScalingMode(b, BitmapScalingMode.Linear);   // smooth upscale = part of the blur
        return b;
    }

    /// <summary>The neck is painted like the top of the strip, so the drop and the strip read as one liquid.</summary>
    private void UpdateNeckFill()
    {
        // the theme's own colour, not StripTopStop.Color: that one may still be mid-animation from the old theme
        var c = _theme?.Glass == true ? Color.FromArgb(150, 24, 24, 30) : WithAlpha(_theme!.GradTop, 0.9);
        var b = new SolidColorBrush(c);
        b.Freeze();
        Neck.Fill = b;
    }

    /// <summary>Capture every frame (instead of ~30 fps) for this long, while the island moves.</summary>
    private void PumpGlass(double ms)
    {
        if (!_glassOn || _glassTimer == null) return;
        var until = DateTime.UtcNow.AddMilliseconds(ms * Math.Max(1, _settings.AnimationSpeed) + 120);
        if (until > _glassFastUntil) _glassFastUntil = until;
        _glassTimer.Interval = TimeSpan.FromMilliseconds(15);
    }

    private void GlassTimer_Tick(object? sender, EventArgs e)
    {
        GlassTick();
        if (_glassTimer != null && DateTime.UtcNow > _glassFastUntil && _glassTimer.Interval.TotalMilliseconds < 30)
            _glassTimer.Interval = TimeSpan.FromMilliseconds(33);
    }

    /// <summary>Copies what is behind the capsule (and the strip) and feeds it to the glass.</summary>
    private void GlassTick()
    {
        if (!_glassOn || _glass == null || _hidden || !IsVisible) return;
        if (!TryScreenRect(GlassBackdrop, out var cap)) return;
        bool strip = LyricsStrip.Visibility == Visibility.Visible && LyricsStrip.Opacity > 0.02
                     && TryScreenRect(StripShadowShape, out _);
        TryScreenRect(StripShadowShape, out var str);
        var all = strip ? Rect.Union(cap, str) : cap;

        var px = new Int32Rect((int)Math.Floor(all.X), (int)Math.Floor(all.Y), (int)Math.Ceiling(all.Width), (int)Math.Ceiling(all.Height));
        if (!_glass.Grab(px, 2) || _glass.Bitmap is not { } bmp) return;   // v1.7.3: half size = soft first blur pass

        if (_capsuleBack!.ImageSource != bmp) _capsuleBack.ImageSource = bmp;
        if (_stripBack!.ImageSource != bmp) _stripBack.ImageSource = bmp;
        _capsuleBack.Viewbox = Relative(cap, px);
        if (strip) _stripBack.Viewbox = Relative(str, px);

        if (_capsuleFx is LiquidGlassEffect cf)
            cf.Shape(GlassBackdrop.ActualWidth, GlassBackdrop.ActualHeight, Capsule.CornerRadius.BottomLeft, _settings.Shape == 1);
        if (strip && _stripFx is LiquidGlassEffect sf)
            sf.Shape(StripShadowShape.ActualWidth, StripShadowShape.ActualHeight, LyricsStrip.CornerRadius.TopLeft, false);
    }

    private static Rect Relative(Rect r, Int32Rect all) =>
        new((r.X - all.X) / all.Width, (r.Y - all.Y) / all.Height, r.Width / all.Width, r.Height / all.Height);

    /// <summary>Element bounds on the screen in physical pixels, render transforms (scale, hide slide) included.</summary>
    private static bool TryScreenRect(FrameworkElement el, out Rect r)
    {
        r = Rect.Empty;
        if (!el.IsVisible || el.ActualWidth < 1 || el.ActualHeight < 1 || PresentationSource.FromVisual(el) == null) return false;
        try
        {
            var a = el.PointToScreen(new Point(0, 0));
            var b = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
            r = new Rect(a, b);
            return r.Width >= 4 && r.Height >= 4;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
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
            };
            LyricsStrip.BeginAnimation(OpacityProperty, fade);
            PumpGlass(300);
        }
    }
}
