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
        // v1.8.2: in the notch the reverse corners are glass too (filled from the same screen copy in UpdateGlassViews),
        // so the sides flow into the top of the screen, collapsed and open alike
        EarLeft.Visibility = EarRight.Visibility = _settings.Shape == 1 ? Visibility.Visible : Visibility.Collapsed;
        _earBmp = null;
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
        // v1.8.1: while the capsule moves, only the crop into the last copy follows it, every frame (cheap);
        // the screen itself is still copied ~30 times a second by the timer
        if (_glassFollowing) return;
        _glassFollowing = true;
        CompositionTarget.Rendering += GlassFollow;
    }

    private bool _glassFollowing;
    private Int32Rect _glassArea;   // the screen area of the last copy: the whole island window
    private System.Windows.Media.Imaging.BitmapSource? _earBmp;
    private DrawingBrush? _earLeftFill, _earRightFill;

    /// <summary>A notch corner made of glass: the screen copy under a dark tint (the shader is too heavy for 16 px).</summary>
    private static DrawingBrush EarGlass(System.Windows.Media.Imaging.BitmapSource bmp)
    {
        var r = new Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight);
        var g = new DrawingGroup();
        g.Children.Add(new ImageDrawing(bmp, r));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(0x70, 10, 10, 14)), null, new RectangleGeometry(r)));
        return new DrawingBrush(g) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox };
    }

    private void GlassFollow(object? sender, EventArgs e)
    {
        UpdateGlassViews();
        if (_glassOn && DateTime.UtcNow <= _glassFastUntil) return;
        CompositionTarget.Rendering -= GlassFollow;
        _glassFollowing = false;
    }

    private void GlassTimer_Tick(object? sender, EventArgs e) => GlassTick();

    /// <summary>
    /// Copies what is behind the whole island window. v1.8.1: the copy used to cover only the capsule; while the
    /// capsule shrank between two copies, the old picture was squeezed into the new size, so everything behind the
    /// glass seemed to jump up. Now the copy is a fixed area and only the crop follows the capsule.
    /// </summary>
    private void GlassTick()
    {
        if (!_glassOn || _glass == null || _hidden || !IsVisible) return;
        if (!TryScreenRect(WindowRoot, out var win)) return;
        var px = new Int32Rect((int)Math.Floor(win.X), (int)Math.Floor(win.Y), (int)Math.Ceiling(win.Width), (int)Math.Ceiling(win.Height));
        if (!_glass.Grab(px, 2) || _glass.Bitmap is not { } bmp) return;   // half size = soft first blur pass
        _glassArea = px;
        if (_capsuleBack!.ImageSource != bmp) _capsuleBack.ImageSource = bmp;
        if (_stripBack!.ImageSource != bmp) _stripBack.ImageSource = bmp;
        UpdateGlassViews();
    }

    /// <summary>Points the capsule and the strip at their part of the last copy (and sets the glass shape).</summary>
    private void UpdateGlassViews()
    {
        if (!_glassOn || _capsuleBack == null || _stripBack == null || _glassArea.Width < 4) return;
        if (TryScreenRect(GlassBackdrop, out var cap))
        {
            _capsuleBack.Viewbox = Relative(cap, _glassArea);
            if (_capsuleFx is LiquidGlassEffect cf)
                cf.Shape(GlassBackdrop.ActualWidth, GlassBackdrop.ActualHeight, Capsule.CornerRadius.BottomLeft, _settings.Shape == 1);
        }
        if (_settings.Shape == 1 && _glass?.Bitmap is { } bmp)
        {
            if (!ReferenceEquals(bmp, _earBmp))
            {
                _earBmp = bmp;
                _earLeftFill = EarGlass(bmp);
                _earRightFill = EarGlass(bmp);
                EarLeft.Fill = _earLeftFill;
                EarRight.Fill = _earRightFill;
            }
            if (TryScreenRect(EarLeft, out var el)) _earLeftFill!.Viewbox = Relative(el, _glassArea);
            if (TryScreenRect(EarRight, out var er)) _earRightFill!.Viewbox = Relative(er, _glassArea);
        }
        if (LyricsStrip.Visibility == Visibility.Visible && LyricsStrip.Opacity > 0.02 && TryScreenRect(StripShadowShape, out var str))
        {
            _stripBack.Viewbox = Relative(str, _glassArea);
            if (_stripFx is LiquidGlassEffect sf)
                sf.Shape(StripShadowShape.ActualWidth, StripShadowShape.ActualHeight, LyricsStrip.CornerRadius.TopLeft, false);
        }
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
