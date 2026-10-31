using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicMochi.Controls;
using DynamicMochi.Services;

namespace DynamicMochi;

/// <summary>
/// The island. Partial files: this one (window, layout, theme, shape, states, tabs, keyboard, quick settings),
/// IslandWindow.Music.cs, IslandWindow.Calendar.cs, IslandWindow.Weather.cs. Full settings live in SettingsWindow.
/// </summary>
public partial class IslandWindow : Window
{
    // Capsule sizes before the user scale (Settings.Scale is applied as a LayoutTransform)
    private const double PlayingW = 420, IdleW = 300, DetailH = 58;     // collapsed view (v1.4 default, SPEC-v1.4 §4)
    private const double MinPlayW = 240, MinIdleW = 200, MinH = 36;     // minimal collapsed view (option)
    private const double ExpandedW = 600;
    private const double HomeH = 230, MusicH = 252, MusicWithLyricsH = 304, CalendarH = 330, WeatherH = 436, SettingsH = 350;
    private const double BaseWindowW = 700, BaseWindowH = 500;

    private Settings _settings => App.Settings;
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _expandTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _unpeekTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _clickTimer = new() { Interval = TimeSpan.FromMilliseconds(260) };
    private readonly DispatcherTimer _osdTimer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private static CultureInfo Ru => Loc.Culture;   // date culture follows the interface language

    private Theme _theme = Themes.Find(null);
    private readonly List<(Button Card, Border Check, Theme Theme)> _themeCards = new();

    private IntPtr _hwnd;
    private bool _expanded;
    private bool _peek;            // minimal view shown at the detailed size (first-run hint only)
    private bool _hotkeyOpen;      // opened with the hotkey: stays until the hotkey again or mouse in-and-out
    private bool _hidden;          // slid away for a full-screen app
    private bool _loading = true;
    private string _page = "home";
    private int _lastSecond = -1;
    private WeatherFx.Fx? _previewFx;

    // keyboard: the window never takes focus, except while the user types into a field
    private bool _keyboard;
    private IntPtr _previousForeground;

    /// <summary>Animation durations follow Settings.AnimationSpeed (1 / 0.6 / 0).</summary>
    private static TimeSpan Dur(double ms) => TimeSpan.FromMilliseconds(ms * App.Settings.AnimationSpeed);

    public bool HotkeyRegistered { get; private set; }

    public IslandWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Deactivated += OnDeactivated;
        _uiTimer.Tick += (_, _) => UiTick();
        // one continuous movement from the collapsed view to the expanded one, no stop in between (SPEC-v1.4 §3)
        _expandTimer.Tick += (_, _) =>
        {
            _expandTimer.Stop();
            if (Capsule.IsMouseOver && !_expanded && !HoldsHover) Expand();
        };
        _unpeekTimer.Tick += (_, _) =>
        {
            _unpeekTimer.Stop();
            if (!Capsule.IsMouseOver && !_expanded && !_firstRun) SetPeek(false);
        };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!Capsule.IsMouseOver && _expanded && !_keyboard) Collapse();
        };
        _clickTimer.Tick += async (_, _) => { _clickTimer.Stop(); await TogglePlayAsync(); };
        _osdTimer.Tick += (_, _) => { _osdTimer.Stop(); Fade(VolumeOsd, 0, 250, 0); };

        foreach (var box in new[] { FormTitle, FormTime, TaskTitle, TaskTime, TimerInput })
            box.PreviewMouseLeftButtonDown += (_, _) => EnableKeyboard();
        foreach (var sv in new[] { SettingsPage, DaysScroll, DayScroll, EventForm, TasksScroll })
        {
            sv.ScrollChanged += ThinScroll_ScrollChanged;
            sv.PreviewMouseWheel += ThinScroll_PreviewMouseWheel;
        }
        Capsule.ContextMenu = App.BuildMenu();
        Loc.Changed += () => Step("language", OnLanguageChanged);
    }

    // ================= Window, layout, hotkey

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.MakeToolWindow(_hwnd);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        ApplyLayout();
        RegisterHotkey();
        _clipboardHooked = AddClipboardFormatListener(_hwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE) OnClipboardUpdate();
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == 1)
        {
            handled = true;
            if (_expanded) Collapse();
            else if (!_hidden) { _hotkeyOpen = true; Expand(); }
        }
        return IntPtr.Zero;
    }

    /// <summary>Registers the show/hide hotkey; returns false when the combination is taken by another app.</summary>
    public bool RegisterHotkey()
    {
        if (_hwnd == IntPtr.Zero) return false;
        Native.UnregisterHotKey(_hwnd, 1);
        HotkeyRegistered = false;
        if (!_settings.HotkeyEnabled) return true;
        HotkeyRegistered = Native.RegisterHotKey(_hwnd, 1, _settings.HotkeyModifiers | Native.MOD_NOREPEAT, _settings.HotkeyKey);
        if (!HotkeyRegistered) Log.Write("hotkey is taken: " + Native.HotkeyLabel(_settings.HotkeyModifiers, _settings.HotkeyKey));
        return HotkeyRegistered;
    }

    /// <summary>Live preview while a size / offset slider in the settings window moves.</summary>
    public void ApplyLayoutOnly() => ApplyLayout();

    /// <summary>Scale, top offset, window size and monitor.</summary>
    private void ApplyLayout()
    {
        double s = _settings.Scale;
        IslandScale.ScaleX = s;
        IslandScale.ScaleY = s;
        bool notch = _settings.Shape == 1;
        IslandStack.Margin = new Thickness(0, notch ? 0 : _settings.TopOffset, 0, 0);
        EarLeft.Visibility = EarRight.Visibility = notch ? Visibility.Visible : Visibility.Collapsed;
        if (!_loading) { ApplyBackground(Dur(0)); SetGradientShape(_expanded, 0); }
        Capsule_SizeChanged(Capsule, null);
        Width = BaseWindowW * s;
        Height = BaseWindowH * s;
        PlaceOnScreen();
    }

    private void PlaceOnScreen()
    {
        if (_hwnd == IntPtr.Zero) return;
        try { Screens.Place(_hwnd, _settings.Monitor, Width); }
        catch (Exception ex) { Log.Write("place on screen failed: " + ex.Message); }
    }

    /// <summary>Slide up behind the screen edge while a full-screen app is in front (250 ms), come back after.</summary>
    private void UpdateFullScreen()
    {
        bool hide = _settings.HideInFullscreen && Screens.FullScreenAppOn(_hwnd);
        if (hide == _hidden) return;
        _hidden = hide;
        if (hide && _expanded) Collapse();
        double to = hide ? -(IslandStack.ActualHeight * _settings.Scale + IslandStack.Margin.Top + 30) : 0;
        HideShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(to, Dur(250))
        {
            EasingFunction = new CubicEase { EasingMode = hide ? EasingMode.EaseIn : EasingMode.EaseOut },
        });
        IsHitTestVisible = !hide;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Each step is isolated: one failing step must not hide the island or the rest of the UI.
        Step("theme", () => ApplyTheme(_theme = Themes.Find(_settings.Theme), animate: false));
        Step("theme cards", BuildThemeCards);
        Step("toggles", RefreshToggles);
        Step("music settings", ApplyMusicSettings);
        Step("shape", () => SetGradientShape(expanded: false, ms: 0));
        Step("track", () => ShowTrack(null));
        Step("calendar", InitCalendar);
        Step("tasks", InitTasks);
        Step("timer", InitTimer);
        Step("screenshots", InitScreenshots);
        Step("notifications", () => _ = InitNotificationsAsync());
        Step("weather", InitWeather);
        Step("tabs", () => SelectTab("home", animate: false));
        Step("version", () => VersionText.Text = "Dynamic Mochi " + App.Version);
        Step("layout", ApplyLayout);
        _loading = false;

        // appear from a point; always runs, otherwise the island stays invisible
        var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        var grow = new DoubleAnimation(0, 1, Dur(450)) { EasingFunction = spring };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        _uiTimer.Start();
        if (!_settings.FirstRunDone) Step("first run", ShowFirstRunHint);
        await StartMediaAsync();
    }

    private static void Step(string name, Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Write($"step '{name}' failed: {ex}"); }
    }

    /// <summary>Called by the settings window after any change.</summary>
    public void ApplySettings()
    {
        Step("theme", () =>
        {
            var t = Themes.Find(_settings.Theme);
            if (t != _theme) ApplyTheme(t, animate: true);
            else { UpdateTint(300); RefreshThemeCards(); }
        });
        Step("layout", ApplyLayout);
        Step("hotkey", () => RegisterHotkey());
        Step("toggles", RefreshToggles);
        Step("music", ApplyMusicSettings);
        Step("collapsed", () => { if (!_settings.MinimalIdle) _peek = false; UpdateClocks(force: true); UpdateCollapsedRow(); });
        Step("notifications", ApplyNotificationSettings);
        Step("calendar", () => { RenderCalendar(0); UpdateHomeEvent(); });
        Step("weather", WeatherSettingsChanged);
        Step("fx", UpdateWeatherFx);
    }

    // ================= Theme

    private void ApplyTheme(Theme t, bool animate)
    {
        _theme = t;
        var dur = animate ? Dur(250) : TimeSpan.Zero;
        App.ApplyThemeBrushes(t, animate);

        ApplyBackground(dur);
        AnimateColor(StripTopStop, GradientStop.ColorProperty, WithAlpha(t.GradTop, 0.9), dur);
        AnimateColor(StripEndStop, GradientStop.ColorProperty, WithAlpha(t.GradEnd, 0.9), dur);

        double border = t.Border.A > 0 ? 1 : 0;
        Capsule.BorderThickness = new Thickness(border);
        LyricsStrip.BorderThickness = new Thickness(border);

        // one effect only: coloured glow for coloured themes, otherwise a soft black shadow
        var glowColor = t.HasGlow ? Color.FromRgb(t.Glow.R, t.Glow.G, t.Glow.B) : Colors.Black;
        double glowOpacity = t.HasGlow ? t.Glow.A / 255.0 : t.ShadowOpacity;
        GlowEffect.ShadowDepth = t.HasGlow ? 0 : 6;
        GlowEffect.BlurRadius = t.HasGlow ? 22 : 24;
        AnimateColor(GlowEffect, DropShadowEffect.ColorProperty, glowColor, dur);
        AnimateDouble(GlowEffect, DropShadowEffect.OpacityProperty, glowOpacity, dur);
        StripShadow.Color = glowColor;
        StripShadow.ShadowDepth = t.HasGlow ? 0 : 4;
        StripShadow.Opacity = t.HasGlow ? glowOpacity * 0.8 : 0.4;

        UpdateTint(animate ? 250 : 0);
        _shownIndex = int.MinValue;   // redraw the lyric line
        RefreshThemeCards();
        RefreshToggles();
        RefreshFormChips();
        UpdateWeatherFx();
        Capsule_SizeChanged(Capsule, null);
        foreach (var ring in new[] { MiniTimerRing, IdleTimerRing, TimerRing }) ring.InvalidateVisual();
    }

    /// <summary>
    /// Background colours. Island: GradTop → GradMid → black. Notch: black at the top edge (it merges with the
    /// screen bezel) flowing into the theme colour towards the bottom (SPEC-v1.4 §17).
    /// </summary>
    private void ApplyBackground(TimeSpan dur)
    {
        var t = _theme;
        bool notch = _settings.Shape == 1 && !t.IsLight;
        AnimateColor(BgTopStop, GradientStop.ColorProperty, notch ? Colors.Black : t.GradTop, dur);
        AnimateColor(BgMidStop, GradientStop.ColorProperty, t.GradMid, dur);
        AnimateColor(BgEndStop, GradientStop.ColorProperty, notch ? t.GradTop : t.GradEnd, dur);
        var ear = new SolidColorBrush(notch ? Colors.Black : t.GradTop);
        ear.Freeze();
        EarLeft.Fill = ear;
        EarRight.Fill = ear;
    }

    private static void AnimateColor(Animatable target, DependencyProperty prop, Color to, TimeSpan dur)
    {
        if (dur > TimeSpan.Zero) target.BeginAnimation(prop, new ColorAnimation(to, dur));
        else { target.BeginAnimation(prop, null); target.SetValue(prop, to); }
    }

    private static void AnimateDouble(Animatable target, DependencyProperty prop, double to, TimeSpan dur)
    {
        if (dur > TimeSpan.Zero) target.BeginAnimation(prop, new DoubleAnimation(to, dur));
        else { target.BeginAnimation(prop, null); target.SetValue(prop, to); }
    }

    private static Color WithAlpha(Color c, double alpha)
    {
        c.A = (byte)Math.Round(c.A * alpha);
        return c;
    }

    private static Color Hex(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Colors.DodgerBlue; }
    }

    /// <summary>
    /// Island collapsed: horizontal, GradTop 0 % → GradMid 55 % → black 100 % (black only at the very end).
    /// Island expanded: diagonal from the top-left corner with the same stops.
    /// Notch: vertical, black at the top edge.
    /// </summary>
    private void SetGradientShape(bool expanded, int ms)
    {
        var dur = Dur(ms);
        bool animate = dur > TimeSpan.Zero;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        void P(DependencyProperty prop, Point to)
        {
            if (animate) BgBrush.BeginAnimation(prop, new PointAnimation(to, dur) { EasingFunction = ease });
            else { BgBrush.BeginAnimation(prop, null); BgBrush.SetValue(prop, to); }
        }
        void O(GradientStop s, double to)
        {
            if (animate) s.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(to, dur) { EasingFunction = ease });
            else { s.BeginAnimation(GradientStop.OffsetProperty, null); s.Offset = to; }
        }
        if (_settings.Shape == 1)
        {
            P(LinearGradientBrush.StartPointProperty, new Point(0.5, 0));
            P(LinearGradientBrush.EndPointProperty, new Point(0.5, 1));
            O(BgMidStop, 0.55);
            O(BgEndStop, 1.0);
            return;
        }
        P(LinearGradientBrush.StartPointProperty, expanded ? new Point(0, 0) : new Point(0, 0.5));
        P(LinearGradientBrush.EndPointProperty, expanded ? new Point(1, 1) : new Point(1, 0.5));
        O(BgMidStop, 0.55);
        O(BgEndStop, 1.0);
    }

    /// <summary>Colour blob in the top-right corner: weather on the weather tab, otherwise the artwork (OLED and White only).</summary>
    private void UpdateTint(int ms)
    {
        Color c = Colors.Transparent;
        bool artTint = _theme.ArtworkTint && _settings.ArtworkTint;
        if (_expanded && _page == "weather" && WeatherTint() is Color w)
            c = w;
        else if (artTint && _artColor is Color art && _track != null)
            c = WithAlpha(art, _theme.TintOpacity);
        else if (artTint)
            c = WithAlpha(_theme.Accent, _theme.IsLight ? 0.06 : 0.08);

        AnimateColor(TintStop, GradientStop.ColorProperty, c, Dur(ms));
    }

    // ================= Shape: everything inside is clipped to the rounded capsule

    private void Capsule_SizeChanged(object sender, SizeChangedEventArgs? e)
    {
        double w = Capsule.ActualWidth, h = Capsule.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double bt = Capsule.BorderThickness.Left;
        double outer = Math.Min(h / 2, 28);
        double r = Math.Max(0, outer - bt);
        double iw = Math.Max(0, w - 2 * bt), ih = Math.Max(0, h - 2 * bt);
        if (_settings.Shape == 1)
        {
            // notch: flat top glued to the screen edge, only the bottom corners are round
            Capsule.CornerRadius = new CornerRadius(0, 0, outer, outer);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(0, 0), true, true);
                g.LineTo(new Point(iw, 0), false, false);
                g.LineTo(new Point(iw, Math.Max(0, ih - r)), false, false);
                g.ArcTo(new Point(Math.Max(0, iw - r), ih), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
                g.LineTo(new Point(Math.Min(iw, r), ih), false, false);
                g.ArcTo(new Point(0, Math.Max(0, ih - r)), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            }
            geo.Freeze();
            CapsuleContent.Clip = geo;
        }
        else
        {
            Capsule.CornerRadius = new CornerRadius(outer);
            CapsuleContent.Clip = new RectangleGeometry(new Rect(0, 0, iw, ih), r, r);
        }
        DropRing.CornerRadius = Capsule.CornerRadius;
        if (CapsuleProgress.Visibility == Visibility.Visible) UpdateCapsuleProgress();
    }

    /// <summary>After a size animation: repaint the whole layered window so no pixels of the larger size remain.</summary>
    private void Repaint()
    {
        WindowRoot.Opacity = 0.999;
        WindowRoot.InvalidateVisual();
        Dispatcher.InvokeAsync(() => WindowRoot.Opacity = 1, DispatcherPriority.Render);
    }

    // ================= Hover → expanded (one movement)

    private void Capsule_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        _unpeekTimer.Stop();
        if (_notice != null && _notice.HoverOpens) { var n = _notice; DismissNotice(); n.Click?.Invoke(); }
        if (_expanded || HoldsHover) return;
        _expandTimer.Start();
        Scale(RootScale, 1.02, 150);   // anticipation
    }

    private void Capsule_MouseLeave(object sender, MouseEventArgs e)
    {
        _expandTimer.Stop();
        if (!_expanded)
        {
            Scale(RootScale, 1, 150);
            if (_peek) _unpeekTimer.Start();
            return;
        }
        if (!_keyboard) _collapseTimer.Start();
    }

    private void SetPeek(bool on)
    {
        if (_peek == on) return;
        _peek = on;
        UpdateCollapsedRow();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // clicked into another app while typing: give the keyboard back and let the island close
        if (!_keyboard) return;
        _keyboard = false;
        Keyboard.ClearFocus();
        Native.SetNoActivate(_hwnd, true);
        if (_expanded && !Capsule.IsMouseOver) _collapseTimer.Start();
    }

    private static void Scale(ScaleTransform st, double to, int ms)
    {
        var a = new DoubleAnimation(to, Dur(ms)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private double PageHeight(string page) => page switch
    {
        "music" => HasLyricsForTrack ? MusicWithLyricsH : MusicH,
        "calendar" => CalendarH,
        "weather" => WeatherH,
        "settings" => SettingsH,
        _ => HomeH,
    };

    private bool DetailedCollapsed => !_settings.MinimalIdle || _peek;

    /// <summary>A notification in the capsule keeps it from opening on hover; it is clicked instead.</summary>
    private bool HoldsHover => _notice != null && !_notice.HoverOpens;

    private (double W, double H) CollapsedSize =>
        _notice != null ? (_notice.Width, DetailH)
        : DetailedCollapsed ? (_track != null ? PlayingW : IdleW, DetailH)
        : (_track != null ? MinPlayW : MinIdleW, MinH);

    private double CollapsedWidth => CollapsedSize.W;

    private void Expand()
    {
        _expanded = true;
        _expandTimer.Stop();
        HideFirstRunHint();
        Scale(RootScale, 1, 120);
        var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
        AnimateSize(ExpandedW, PageHeight(_page), 380, spring);
        SetGradientShape(expanded: true, ms: 380);
        Fade(CollapsedView, 0, 120, 0);
        ExpandedView.Visibility = Visibility.Visible;
        Fade(ExpandedView, 1, 180, 120);
        Slide(ExpandedShift, 6, 0, 180, 120);
        UpdateStrip();
        OnPageShown(_page);
    }

    private void Collapse()
    {
        _expanded = false;
        _hotkeyOpen = false;
        _peek = false;
        DisableKeyboard();
        if (_dragging) { _dragging = false; ProgressTrack.ReleaseMouseCapture(); }
        if (_volDragging) { _volDragging = false; VolumeTrack.ReleaseMouseCapture(); }
        CloseEventForm(animate: false);
        _highlightId = null;
        var (w, h) = CollapsedSize;
        AnimateSize(w, h, 260, new CubicEase { EasingMode = EasingMode.EaseInOut });
        SetGradientShape(expanded: false, ms: 260);
        var fade = new DoubleAnimation(0, Dur(120));
        fade.Completed += (_, _) => { if (!_expanded) ExpandedView.Visibility = Visibility.Collapsed; };
        ExpandedView.BeginAnimation(OpacityProperty, fade);
        Fade(CollapsedView, 1, 200, 120);
        UpdateCollapsedRow();
        UpdateStrip();
        UpdateTint(300);
        UpdateWeatherFx();
        ShowQueuedNotice();
    }

    private void AnimateSize(double w, double h, int ms, IEasingFunction ease)
    {
        var dur = Dur(ms);
        var wa = new DoubleAnimation(w, dur) { EasingFunction = ease };
        wa.Completed += (_, _) => Repaint();
        Capsule.BeginAnimation(FrameworkElement.WidthProperty, wa);
        Capsule.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(h, dur) { EasingFunction = ease });
    }

    private static void Fade(UIElement el, double to, int ms, int delayMs)
    {
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, Dur(ms))
        {
            BeginTime = Dur(delayMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private static void Slide(TranslateTransform tt, double fromY, double toY, int ms, int delayMs)
    {
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, toY, Dur(ms))
        {
            BeginTime = Dur(delayMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    /// <summary>Collapsed content: reminder activity › playing › idle, minimal or detailed. Animates the size.</summary>
    private void UpdateCollapsedRow()
    {
        FrameworkElement want = _notice != null ? ActivityRow
            : DetailedCollapsed ? (_track != null ? PlayingRow : IdleRow)
            : (_track != null ? MinPlayRow : MinClock);
        foreach (var row in new FrameworkElement[] { ActivityRow, PlayingRow, IdleRow, MinPlayRow, MinClock })
        {
            if (row == want)
            {
                if (row.Visibility != Visibility.Visible)
                {
                    row.Visibility = Visibility.Visible;
                    row.Opacity = 0;
                    Fade(row, 1, 200, 80);
                }
            }
            else if (row.Visibility == Visibility.Visible)
            {
                row.BeginAnimation(OpacityProperty, null);
                row.Opacity = 0;
                row.Visibility = Visibility.Collapsed;
            }
        }
        UpdateCapsuleLine();
        if (_expanded) return;
        var (w, h) = CollapsedSize;
        if (Math.Abs(Capsule.Width - w) > 0.5 || Math.Abs(Capsule.Height - h) > 0.5)
            AnimateSize(w, h, _loading ? 0 : 300, new CubicEase { EasingMode = EasingMode.EaseInOut });
    }

    // ================= Collapsed capsule: click = play/pause, double click = next, wheel = volume

    private void Capsule_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_expanded) return;
        if (_notice?.Click is { } click && !_notice.HoverOpens)
        {
            DismissNotice();
            click();
            e.Handled = true;
            return;
        }
        if (_timerRunning && (_track == null || !_settings.ClickControl))
        {
            TimerTogglePause();   // click on a running timer = pause / resume
            e.Handled = true;
            return;
        }
        if (!_settings.ClickControl || _track == null) return;
        // a click is not a hover: do not open the island because of it
        _expandTimer.Stop();
        if (e.ClickCount >= 2)
        {
            _clickTimer.Stop();
            _ = _media.NextAsync();
        }
        else _clickTimer.Start();
        e.Handled = true;
    }

    private async Task TogglePlayAsync()
    {
        if (_track != null)
        {
            var now = DateTime.UtcNow;
            _track = _track with { IsPlaying = !_track.IsPlaying, PositionSec = _track.PositionAt(now), PositionStampUtc = now };
            ShowTrack(_track);
        }
        await _media.PlayPauseAsync();
    }

    private void Capsule_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_expanded) return;
        RefreshVolume(force: false);
        SetVolume(_volFraction + Math.Sign(e.Delta) * 0.05);
        double w = Math.Max(0, Capsule.ActualWidth - 40);
        VolumeOsdFill.Width = w * _volFraction;
        VolumeOsd.BeginAnimation(OpacityProperty, null);
        VolumeOsd.Opacity = 1;
        _osdTimer.Stop();
        _osdTimer.Start();
        // scrolling is not hovering: restart the hover delay
        if (_expandTimer.IsEnabled) { _expandTimer.Stop(); _expandTimer.Start(); }
        e.Handled = true;
    }

    // ================= First run

    private bool _firstRun;

    private void ShowFirstRunHint()
    {
        _firstRun = true;
        SetPeek(true);
        FirstRunHint.Visibility = Visibility.Visible;
        Fade(FirstRunHint, 1, 250, 400);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            HideFirstRunHint();
            if (!Capsule.IsMouseOver && !_expanded) SetPeek(false);
        };
        timer.Start();
        _settings.FirstRunDone = true;
        _settings.Save();
    }

    private void HideFirstRunHint()
    {
        if (!_firstRun) return;
        _firstRun = false;
        var fade = new DoubleAnimation(0, Dur(200));
        fade.Completed += (_, _) => { FirstRunHint.Visibility = Visibility.Collapsed; Repaint(); };
        FirstRunHint.BeginAnimation(OpacityProperty, fade);
    }

    // ================= Tabs

    private static readonly string[] Pages = { "home", "music", "calendar", "weather", "settings" };

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string page && page != _page) SelectTab(page, animate: true);
    }

    private FrameworkElement PageElement(string page) => page switch
    {
        "music" => MusicPage,
        "calendar" => CalendarPage,
        "weather" => WeatherPage,
        "settings" => SettingsPage,
        _ => HomePage,
    };

    private void SelectTab(string page, bool animate)
    {
        string old = _page;
        _page = page;
        int dir = Math.Sign(Array.IndexOf(Pages, page) - Array.IndexOf(Pages, old));
        if (old == "calendar" && page != "calendar") CloseEventForm(animate: false);
        if (page != old) DisableKeyboard();

        double x = Array.IndexOf(Pages, page) * 40;
        TabPillShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, Dur(animate ? 220 : 0))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });
        foreach (var (tab, name) in new[] { (TabHome, "home"), (TabMusic, "music"), (TabCalendar, "calendar"), (TabWeather, "weather"), (TabSettings, "settings") })
            tab.SetResourceReference(ForegroundProperty, page == name ? "AccentBrush" : "SubBrush");
        SmallClock.Visibility = page == "home" ? Visibility.Collapsed : Visibility.Visible;

        var incoming = PageElement(page);
        var ms = Dur(animate ? 200 : 0);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        if (animate && old != page)
        {
            var outgoing = PageElement(old);
            var outTt = (TranslateTransform)outgoing.RenderTransform;
            outTt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -8 * dir, ms) { EasingFunction = ease });
            var fade = new DoubleAnimation(0, ms);
            fade.Completed += (_, _) => { if (_page != old) outgoing.Visibility = Visibility.Collapsed; };
            outgoing.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            foreach (var p in Pages.Where(p => p != page))
            {
                var el = PageElement(p);
                el.BeginAnimation(OpacityProperty, null);
                el.Opacity = 0;
                el.Visibility = Visibility.Collapsed;
            }
        }

        incoming.Visibility = Visibility.Visible;
        var inTt = (TranslateTransform)incoming.RenderTransform;
        inTt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(8 * dir, 0, ms) { EasingFunction = ease });
        incoming.BeginAnimation(OpacityProperty, new DoubleAnimation(1, ms) { EasingFunction = ease });

        if (_expanded)
        {
            AnimateSize(ExpandedW, PageHeight(page), animate ? 220 : 0, new CubicEase { EasingMode = EasingMode.EaseInOut });
            OnPageShown(page);
        }
        UpdateTint(animate ? 300 : 0);
        UpdateWeatherFx();
    }

    private void OnPageShown(string page)
    {
        if (page == "weather") RefreshWeatherIfStale(TimeSpan.FromMinutes(10));
        if (page == "calendar") RenderCalendar(animateDir: 0);
        if (page == "music") RefreshVolume(force: true);
        UpdateWeatherFx();
    }

    // ================= 50 ms tick

    private string _clockShown = "";

    private void UiTick()
    {
        var local = DateTime.Now;
        UpdateClocks(force: false);
        MusicTick(local);

        if (local.Second != _lastSecond)
        {
            _lastSecond = local.Second;
            CalendarSecondTick(local);
            TasksSecondTick(local);
            TimerSecondTick();
            NoticeSecondTick(local);
            WeatherSecondTick();
        }
    }

    /// <summary>Clocks, dates and the idle capsule's second line (greeting, weather or the next event).</summary>
    private void UpdateClocks(bool force)
    {
        var local = DateTime.Now;
        string hm = Loc.Clock(local, _settings.Clock24);
        if (!force && _clockShown == hm + Loc.Culture.Name) return;
        _clockShown = hm + Loc.Culture.Name;
        BigClock.Text = hm; SmallClock.Text = hm; MinClock.Text = hm; MinMusicClock.Text = hm;
        if (!_timerRunning) { MiniClock.Text = hm; IdleClock.Text = hm; }
        DateText.Text = Capital(local.ToString(Loc.T("fmt.date.long"), Ru));
        IdleDate.Text = Greeting(local) ?? Capital(local.ToString(Loc.T("fmt.date.long"), Ru));
        UpdateIdleLine();
    }

    /// <summary>"Доброй ночи" 00–06, "Доброе утро" 06–10, otherwise null (the date is shown).</summary>
    private string? Greeting(DateTime t)
    {
        if (!_settings.Greetings) return null;
        if (t.Hour < 6) return Loc.T("greet.night");
        if (t.Hour < 10) return Loc.T("greet.morning");
        return null;
    }

    private static string Capital(string s) => Loc.Cap(s);

    // ================= Keyboard (calendar form fields)

    private void EnableKeyboard()
    {
        if (_keyboard || _hwnd == IntPtr.Zero) return;
        _keyboard = true;
        _collapseTimer.Stop();
        var fg = Native.GetForegroundWindow();
        _previousForeground = fg != _hwnd ? fg : IntPtr.Zero;
        Native.SetNoActivate(_hwnd, false);
        Activate();
    }

    private void DisableKeyboard()
    {
        if (!_keyboard) return;
        _keyboard = false;
        Keyboard.ClearFocus();
        Native.SetNoActivate(_hwnd, true);
        if (Native.GetForegroundWindow() == _hwnd && _previousForeground != IntPtr.Zero && Native.IsWindow(_previousForeground))
            Native.SetForegroundWindow(_previousForeground);
        _previousForeground = IntPtr.Zero;
    }

    private void FocusField(TextBox box)
    {
        EnableKeyboard();
        Dispatcher.InvokeAsync(() =>
        {
            box.Focus();
            Keyboard.Focus(box);
            box.CaretIndex = box.Text.Length;
        }, DispatcherPriority.Input);
    }

    // ================= Thin scroll bars: visible only while scrolling

    private readonly Dictionary<ScrollViewer, DispatcherTimer> _scrollHide = new();

    private void ThinScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 || sender is not ScrollViewer sv) return;
        ThinScrollBars.Flash(sv, _scrollHide);
    }

    /// <summary>
    /// Wheel scrolling done by hand and clamped: at the end of a list nothing moves any more and the event never
    /// reaches anything else (it used to make the content jump to the top, SPEC-v1.4 §8).
    /// </summary>
    private void ThinScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
        ThinScrollBars.Wheel((ScrollViewer)sender, e);

    // ================= Quick settings tab: theme cards, lyrics toggle, "all settings"

    private void BuildThemeCards()
    {
        ThemePanel.Children.Clear();
        _themeCards.Clear();
        foreach (var theme in Themes.All)
        {
            try
            {
                var (card, check) = ThemeCards.Create(theme, width: 100, height: 64, miniWidth: 64, () =>
                {
                    if (_settings.Theme == theme.Name) return;
                    _settings.Theme = theme.Name;
                    _settings.Save();
                    ApplyTheme(theme, animate: true);
                    App.SettingsChangedFromIsland();
                });
                ThemePanel.Children.Add(card);
                _themeCards.Add((card, check, theme));
            }
            catch (Exception ex) { Log.Write($"theme card '{theme.Name}' failed: {ex}"); }
        }
        RefreshThemeCards();
    }

    /// <summary>Selection follows Settings.Theme only.</summary>
    private void RefreshThemeCards()
    {
        foreach (var (card, check, theme) in _themeCards)
            ThemeCards.SetSelected(card, check, theme.Name == _settings.Theme);
    }

    private void RefreshToggles()
    {
        SetToggle(LyricsToggle, LyricsKnob, _settings.ShowLyrics, animate: !_loading);
    }

    private static void SetToggle(Border track, Ellipse knob, bool on, bool animate)
    {
        var dur = Dur(animate ? 160 : 0);
        var tt = (TranslateTransform)knob.RenderTransform;
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(on ? 18 : 0, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        track.SetResourceReference(Border.BackgroundProperty, on ? "AccentBrush" : "ToggleOffBrush");
        if (on) knob.SetResourceReference(Shape.FillProperty, "AccentFgBrush");
        else knob.Fill = Brushes.White;
    }

    /// <summary>Selected chip = accent plate, others = surface plate.</summary>
    private static void SetChip(Button chip, bool selected)
    {
        chip.SetResourceReference(BackgroundProperty, selected ? "AccentBrush" : "SurfaceBrush");
        chip.SetResourceReference(ForegroundProperty, selected ? "AccentFgBrush" : "FgBrush");
    }

    private void LyricsRow_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.ShowLyrics = !_settings.ShowLyrics;
        _shownIndex = int.MinValue;
        _settings.Save();
        RefreshToggles();
        if (_settings.ShowLyrics && _track != null) UpdateLyricsSource(_track);
        UpdateStrip();
        App.SettingsChangedFromIsland();
    }

    private void AllSettings_Click(object sender, RoutedEventArgs e) => App.ShowSettings();

    private void Quit_Click(object sender, RoutedEventArgs e) => App.Quit();

    /// <summary>Shrink to a point, then exit the app.</summary>
    public void QuitAnimated()
    {
        _uiTimer.Stop();
        StopMedia();
        WeatherFx.Stop();
        DisableKeyboard();
        if (_hwnd != IntPtr.Zero) Native.UnregisterHotKey(_hwnd, 1);
        var shrink = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        shrink.Completed += (_, _) => Application.Current.Shutdown();
        LyricsStrip.Visibility = Visibility.Collapsed;
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    // ================= Language: everything built from code is rebuilt (XAML follows by itself)

    private void OnLanguageChanged()
    {
        if (_loading) return;
        _shownKey = "";              // retitle the track texts
        ShowTrack(_track);
        UpdateClocks(force: true);
        BuildThemeCards();
        BuildFormReminders();
        RefreshFormChips();
        RenderCalendar(0);
        RenderTasks();
        InitTimer();
        ShowWeather();
        Capsule.ContextMenu = App.BuildMenu();
        UpdateHomeEvent();
    }

    // ================= Live weather background

    /// <summary>Settings → About (Shift+click on the version): show a given animation regardless of the real weather.</summary>
    public void PreviewWeather(WeatherFx.Fx? fx)
    {
        _previewFx = fx;
        UpdateWeatherFx();
    }

    private void UpdateWeatherFx()
    {
        WeatherFx.Ink = _theme.IsLight ? Color.FromRgb(0x5E, 0x7A, 0x99) : Colors.White;
        WeatherFx.Tint = _theme.IsGradient ? _theme.Accent : null;
        WeatherFx.Calm = _settings.LiveWeather == 1;

        var w = CurrentWeather;
        WeatherFx.Fx kind = _previewFx ?? (w != null ? WeatherFx.ForCode(w.Code, w.IsDay) : WeatherFx.Fx.None);
        int rain = _previewFx != null ? 80 : w != null ? WeatherFx.RainAmount(w.Code) : 80;
        WeatherFx.Partly = _previewFx == null && w != null && w.Code is 1 or 2;
        WeatherFx.Night = _previewFx == null && w != null && !w.IsDay;
        WeatherFx.WindMs = w != null ? WeatherService.ToMs(w.Wind, w.WindUnit) : 3;
        WeatherFx.WindFromDeg = w?.WindDir ?? 270;
        bool show = _expanded && _page == "weather" && _settings.AnimationSpeed > 0 && _settings.LiveWeather > 0 && kind != WeatherFx.Fx.None
                    && (_previewFx != null || _settings.CurrentPlace != null);

        if (show)
        {
            WeatherFx.SetKind(kind, rain, animate: WeatherFx.Visibility == Visibility.Visible);
            if (WeatherFx.Visibility != Visibility.Visible)
            {
                WeatherFx.Visibility = Visibility.Visible;
                WeatherFx.Start();
            }
            WeatherFx.BeginAnimation(OpacityProperty, new DoubleAnimation(0.5, TimeSpan.FromMilliseconds(400)));
        }
        else if (WeatherFx.Visibility == Visibility.Visible)
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
            fade.Completed += (_, _) =>
            {
                if (WeatherFx.Opacity > 0.01) return;
                WeatherFx.Stop();
                WeatherFx.Visibility = Visibility.Collapsed;
            };
            WeatherFx.BeginAnimation(OpacityProperty, fade);
        }
    }
}
