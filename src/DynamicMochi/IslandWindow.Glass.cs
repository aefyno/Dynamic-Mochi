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
        // v1.8: no faster capture while animating — copying the screen every frame made the motion stutter
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

    // ================= Motion
    // v1.8: smooth, no wobble. The v1.7 springs (overshoot + settle) read as jerky on the tester's PC:
    // every overshoot frame re-lays out the capsule. Now one calm ease-out per change, no second axis lag.

    private static readonly IEasingFunction Smooth = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction SmoothInOut = new CubicEase { EasingMode = EasingMode.EaseInOut };

    /// <summary>Size change of the capsule: one smooth ease, width and height together.</summary>
    private void SpringSize(double w, double h, int ms, bool heightFirst)
    {
        // expand 340 ms, collapse 300 ms, page switch 270 ms (the v1.7 callers pass the old, longer spring times)
        int dur = Math.Clamp((int)(ms * 0.62), 220, 360);
        AnimateSize(w, h, dur, heightFirst ? SmoothInOut : Smooth);
    }

    /// <summary>Track change: a short, gentle pulse (render transform only, no relayout).</summary>
    private void Jelly()
    {
        var a = new DoubleAnimation(1.025, 1, Dur(320)) { EasingFunction = Smooth };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.985, 1, Dur(320)) { EasingFunction = Smooth });
        PumpGlass(320);
    }

    /// <summary>Hover anticipation: the capsule grows a little.</summary>
    private void Swell(double to, int ms)
    {
        var a = new DoubleAnimation(to, Dur(Math.Min(ms, 200))) { EasingFunction = Smooth };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        PumpGlass(200);
    }

    /// <summary>The lyric strip slides out from under the capsule and back in (no neck, no wobble).</summary>
    private void GooStrip(bool show)
    {
        double up = -(StripHost.Margin.Top + Math.Max(12, LyricsStrip.ActualHeight * 0.4));
        Neck.BeginAnimation(WidthProperty, null);
        Neck.Width = 0;
        if (show)
        {
            var d = Dur(300);
            StripScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.85, 1, d) { EasingFunction = Smooth });
            StripScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, 1, d) { EasingFunction = Smooth });
            StripShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(up, 0, d) { EasingFunction = Smooth });
            LyricsStrip.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Dur(220)) { EasingFunction = Smooth });
            PumpGlass(300);
        }
        else
        {
            var d = Dur(220);
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            StripScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.85, d) { EasingFunction = ease });
            StripScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, d) { EasingFunction = ease });
            StripShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(up, d) { EasingFunction = ease });
            var fade = new DoubleAnimation(0, d) { EasingFunction = ease };
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
            PumpGlass(220);
        }
    }
}
