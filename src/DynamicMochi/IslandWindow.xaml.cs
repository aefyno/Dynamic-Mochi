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
using DynamicMochi.Services;

namespace DynamicMochi;

/// <summary>
/// The island. Split into partial files: this one (window, theme, shape, tabs, keyboard, settings rows),
/// IslandWindow.Music.cs, IslandWindow.Calendar.cs, IslandWindow.Weather.cs.
/// </summary>
public partial class IslandWindow : Window
{
    // Capsule sizes (DESIGN v1.2, A2)
    private const double PlayingW = 340, IdleW = 260, ActivityW = 380, CollapsedH = 52;
    private const double ExpandedW = 600;
    private const double PageChrome = 12 + 30 + 12 + 16;   // top margin, tab bar, gap, bottom margin
    private const double HomeH = 230, MusicH = 252, MusicWithLyricsH = 304, CalendarH = 292, WeatherH = 400, SettingsH = 380;

    private readonly Settings _settings = Settings.Load();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _expandTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private static readonly CultureInfo Ru = new("ru-RU");

    private static readonly string[] ThemeKeys =
    {
        "SurfaceBrush", "SurfaceHoverBrush", "FgBrush", "SubBrush", "FaintBrush", "AccentBrush",
        "AccentFgBrush", "BorderBrush", "ToggleOffBrush", "EqBrush",
    };

    private Theme _theme = Themes.Find(null);
    private readonly List<(Button Card, Border Check, Theme Theme)> _themeCards = new();

    private IntPtr _hwnd;
    private bool _expanded;
    private bool _loading = true;
    private string _page = "home";
    private int _lastSecond = -1;

    // keyboard: the window never takes focus, except while the user types into a field
    private bool _keyboard;
    private IntPtr _previousForeground;

    public IslandWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Deactivated += OnDeactivated;
        _uiTimer.Tick += (_, _) => UiTick();
        _expandTimer.Tick += (_, _) =>
        {
            _expandTimer.Stop();
            if (Capsule.IsMouseOver && !_expanded) Expand();
        };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!Capsule.IsMouseOver && _expanded && !_keyboard) Collapse();
        };

        foreach (var box in new[] { FormTitle, FormTime, CityBox, DistrictBox })
            box.PreviewMouseLeftButtonDown += (_, _) => EnableKeyboard();
        foreach (var sv in new[] { SettingsPage, DaysScroll, DayScroll, EventForm })
            sv.ScrollChanged += ThinScroll_ScrollChanged;
    }

    // ================= Window

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.MakeToolWindow(_hwnd);
        PlaceOnScreen();
    }

    private void PlaceOnScreen()
    {
        double left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        if (Math.Abs(Left - left) > 0.5) Left = left;
        if (Top != 0) Top = 0;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Each step is isolated: one failing step must not hide the island or the rest of the UI.
        Step("theme", () => ApplyTheme(_theme = Themes.Find(_settings.Theme), animate: false));
        Step("theme cards", BuildThemeCards);
        Step("toggles", RefreshToggles);
        Step("shape", () => SetGradientShape(expanded: false, ms: 0));
        Step("track", () => ShowTrack(null));
        Step("calendar", InitCalendar);
        Step("weather", InitWeather);
        Step("tabs", () => SelectTab("home", animate: false));
        Step("version", () => VersionText.Text = "Dynamic Mochi " + (typeof(IslandWindow).Assembly.GetName().Version?.ToString(3) ?? ""));
        _loading = false;

        // appear from a point; always runs, otherwise the island stays invisible
        var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        var grow = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { EasingFunction = spring };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        _uiTimer.Start();
        await StartMediaAsync();
    }

    private static void Step(string name, Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Write($"startup step '{name}' failed: {ex}"); }
    }

    // ================= Theme

    /// <summary>
    /// Theme brushes are replaced, never mutated. WPF freezes a brush resource once a style or template
    /// resolves it (unless an animation is running on it); recolouring a frozen brush throws. So every
    /// change builds a new brush (its colour animation already running) and swaps it into Resources;
    /// DynamicResource / SetResourceReference users pick up the new instance.
    /// </summary>
    private void ApplyTheme(Theme t, bool animate)
    {
        _theme = t;
        var dur = TimeSpan.FromMilliseconds(animate ? 250 : 0);

        SetBrush("SurfaceBrush", t.Surface, animate);
        SetBrush("SurfaceHoverBrush", t.SurfaceHover, animate);
        SetBrush("FgBrush", t.Fg, animate);
        SetBrush("SubBrush", t.Sub, animate);
        SetBrush("FaintBrush", t.Faint, animate);
        SetBrush("AccentBrush", t.Accent, animate);
        SetBrush("AccentFgBrush", t.AccentFg, animate);
        SetBrush("BorderBrush", t.Border, animate);
        SetBrush("ToggleOffBrush", t.ToggleOff, animate);
        SetBrush("EqBrush", t.Equalizer, animate);

        AnimateColor(BgTopStop, GradientStop.ColorProperty, t.GradTop, dur);
        AnimateColor(BgMidStop, GradientStop.ColorProperty, t.GradMid, dur);
        AnimateColor(BgEndStop, GradientStop.ColorProperty, t.GradEnd, dur);
        AnimateColor(StripTopStop, GradientStop.ColorProperty, WithAlpha(t.GradTop, 0.9), dur);
        AnimateColor(StripEndStop, GradientStop.ColorProperty, WithAlpha(t.GradEnd, 0.9), dur);

        double border = t.Border.A > 0 ? 1 : 0;
        Capsule.BorderThickness = new Thickness(border);
        LyricsStrip.BorderThickness = new Thickness(border);

        // one effect only: coloured glow for coloured themes, otherwise a soft black shadow
        var glowColor = t.HasGlow ? Color.FromRgb(t.Glow.R, t.Glow.G, t.Glow.B) : Colors.Black;
        double glowOpacity = t.HasGlow ? t.Glow.A / 255.0 : t.ShadowOpacity;
        GlowEffect.ShadowDepth = t.HasGlow ? 0 : 6;
        GlowEffect.BlurRadius = t.HasGlow ? 26 : 24;
        AnimateColor(GlowEffect, DropShadowEffect.ColorProperty, glowColor, dur);
        AnimateDouble(GlowEffect, DropShadowEffect.OpacityProperty, glowOpacity, dur);
        StripShadow.Color = glowColor;
        StripShadow.ShadowDepth = t.HasGlow ? 0 : 4;
        StripShadow.Opacity = t.HasGlow ? glowOpacity * 0.8 : 0.4;

        UpdateTint(animate ? 250 : 0);
        _shownIndex = int.MinValue;   // redraw the lyric line
        RefreshThemeCards();
        RefreshToggles();
        RefreshChips();
        Capsule_SizeChanged(Capsule, null);
    }

    private void SetBrush(string key, Color to, bool animate)
    {
        Color from = Resources[key] is SolidColorBrush old ? old.Color : to;
        var b = new SolidColorBrush(animate ? from : to);
        if (animate && from != to)
            b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(from, to, TimeSpan.FromMilliseconds(250)));
        Resources[key] = b;
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
    /// Collapsed: horizontal, colour on the left flowing into black on the right.
    /// Expanded: diagonal from the top-left corner: GradTop 0 % → GradMid 35 % → black 75 %.
    /// </summary>
    private void SetGradientShape(bool expanded, int ms)
    {
        var dur = TimeSpan.FromMilliseconds(ms);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        void P(DependencyProperty prop, Point to)
        {
            if (ms > 0) BgBrush.BeginAnimation(prop, new PointAnimation(to, dur) { EasingFunction = ease });
            else { BgBrush.BeginAnimation(prop, null); BgBrush.SetValue(prop, to); }
        }
        void O(GradientStop s, double to)
        {
            if (ms > 0) s.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(to, dur) { EasingFunction = ease });
            else { s.BeginAnimation(GradientStop.OffsetProperty, null); s.Offset = to; }
        }
        P(LinearGradientBrush.StartPointProperty, expanded ? new Point(0, 0) : new Point(0, 0.5));
        P(LinearGradientBrush.EndPointProperty, expanded ? new Point(1, 1) : new Point(1, 0.5));
        O(BgMidStop, expanded ? 0.35 : 0.5);
        O(BgEndStop, expanded ? 0.75 : 1.0);
    }

    /// <summary>Colour blob in the top-right corner: weather on the weather tab, otherwise the artwork (OLED and White only).</summary>
    private void UpdateTint(int ms)
    {
        Color c = Colors.Transparent;
        if (_expanded && _page == "weather" && WeatherTint() is Color w)
            c = w;
        else if (_theme.ArtworkTint && _artColor is Color art && _track != null)
            c = WithAlpha(art, _theme.TintOpacity);
        else if (_theme.ArtworkTint)
            c = WithAlpha(_theme.Accent, _theme.IsLight ? 0.06 : 0.08);

        AnimateColor(TintStop, GradientStop.ColorProperty, c, TimeSpan.FromMilliseconds(ms));
    }

    // ================= Shape: everything inside is clipped to the rounded capsule

    private void Capsule_SizeChanged(object sender, SizeChangedEventArgs? e)
    {
        double w = Capsule.ActualWidth, h = Capsule.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double bt = Capsule.BorderThickness.Left;
        double r = Math.Max(0, Math.Min(h / 2, 28) - bt);
        CapsuleContent.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, w - 2 * bt), Math.Max(0, h - 2 * bt)), r, r);
    }

    /// <summary>After a size animation: repaint the whole layered window so no pixels of the larger size remain.</summary>
    private void Repaint()
    {
        WindowRoot.Opacity = 0.999;
        WindowRoot.InvalidateVisual();
        Dispatcher.InvokeAsync(() => WindowRoot.Opacity = 1, DispatcherPriority.Render);
    }

    // ================= Expand / collapse

    private void Capsule_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        if (_activity != null) DismissActivity(openCalendar: true);
        if (_expanded) return;
        _expandTimer.Start();
        Scale(RootScale, 1.02, 150);   // anticipation
    }

    private void Capsule_MouseLeave(object sender, MouseEventArgs e)
    {
        _expandTimer.Stop();
        if (!_expanded) { Scale(RootScale, 1, 150); return; }
        if (!_keyboard) _collapseTimer.Start();
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
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
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

    private double CollapsedWidth => _activity != null ? ActivityW : _track != null ? PlayingW : IdleW;

    private void Expand()
    {
        _expanded = true;
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
        DisableKeyboard();
        if (_dragging) { _dragging = false; ProgressTrack.ReleaseMouseCapture(); }
        if (_volDragging) { _volDragging = false; VolumeTrack.ReleaseMouseCapture(); }
        CloseEventForm(animate: false);
        ClearPlaceSearch();
        _highlightId = null;
        AnimateSize(CollapsedWidth, CollapsedH, 260, new CubicEase { EasingMode = EasingMode.EaseInOut });
        SetGradientShape(expanded: false, ms: 260);
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) => { if (!_expanded) ExpandedView.Visibility = Visibility.Collapsed; };
        ExpandedView.BeginAnimation(OpacityProperty, fade);
        Fade(CollapsedView, 1, 200, 120);
        UpdateStrip();
        UpdateTint(300);
        ShowPendingReminder();
    }

    private void AnimateSize(double w, double h, int ms, IEasingFunction ease)
    {
        var dur = TimeSpan.FromMilliseconds(ms);
        var wa = new DoubleAnimation(w, dur) { EasingFunction = ease };
        wa.Completed += (_, _) => Repaint();
        Capsule.BeginAnimation(FrameworkElement.WidthProperty, wa);
        Capsule.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(h, dur) { EasingFunction = ease });
    }

    private static void Fade(UIElement el, double to, int ms, int delayMs)
    {
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private static void Slide(TranslateTransform tt, double fromY, double toY, int ms, int delayMs)
    {
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, toY, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    /// <summary>Collapsed content: reminder activity, else the playing row, else the idle clock. Animates the width.</summary>
    private void UpdateCollapsedRow()
    {
        FrameworkElement want = _activity != null ? ActivityRow : _track != null ? PlayingRow : IdleRow;
        foreach (var row in new FrameworkElement[] { ActivityRow, PlayingRow, IdleRow })
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
        if (!_expanded && Math.Abs(Capsule.Width - CollapsedWidth) > 0.5)
            AnimateSize(CollapsedWidth, CollapsedH, _loading ? 0 : 300, new CubicEase { EasingMode = EasingMode.EaseInOut });
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
        if (old == "settings" && page != "settings") ClearPlaceSearch();
        if (page != old) DisableKeyboard();

        double x = Array.IndexOf(Pages, page) * 40;
        TabPillShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(animate ? 220 : 0))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });
        foreach (var (tab, name) in new[] { (TabHome, "home"), (TabMusic, "music"), (TabCalendar, "calendar"), (TabWeather, "weather"), (TabSettings, "settings") })
            tab.SetResourceReference(ForegroundProperty, page == name ? "FgBrush" : "SubBrush");
        SmallClock.Visibility = page == "home" ? Visibility.Collapsed : Visibility.Visible;

        var incoming = PageElement(page);
        var ms = TimeSpan.FromMilliseconds(animate ? 200 : 0);
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
    }

    private void OnPageShown(string page)
    {
        if (page == "weather") RefreshWeatherIfStale(TimeSpan.FromMinutes(10));
        if (page == "calendar") RenderCalendar(animateDir: 0);
        if (page == "music") RefreshVolume(force: true);
    }

    // ================= 50 ms tick

    private void UiTick()
    {
        var local = DateTime.Now;
        string hm = local.ToString("HH:mm", Ru);
        if (MiniClock.Text != hm)
        {
            MiniClock.Text = hm; IdleClock.Text = hm; BigClock.Text = hm; SmallClock.Text = hm;
            DateText.Text = Capital(local.ToString("dddd, d MMMM", Ru));
            IdleDate.Text = Capital(local.ToString("ddd, d MMM", Ru).Replace(".", ""));
        }

        MusicTick(local);

        if (local.Second != _lastSecond)
        {
            _lastSecond = local.Second;
            CalendarSecondTick(local);
            WeatherSecondTick();
        }
    }

    private static string Capital(string s) => s.Length > 0 ? char.ToUpper(s[0], Ru) + s.Substring(1) : s;

    // ================= Keyboard (fields)

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
        if (sv.Template?.FindName("PART_VerticalScrollBar", sv) is not System.Windows.Controls.Primitives.ScrollBar bar) return;
        bar.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        if (!_scrollHide.TryGetValue(sv, out var timer))
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                bar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
            };
            _scrollHide[sv] = timer;
        }
        timer.Stop();
        timer.Start();
    }

    // ================= Settings: theme cards

    private void BuildThemeCards()
    {
        ThemePanel.Children.Clear();
        _themeCards.Clear();
        foreach (var theme in Themes.All)
        {
            try { AddThemeCard(theme); }
            catch (Exception ex) { Log.Write($"theme card '{theme.Name}' failed: {ex}"); }
        }
        RefreshThemeCards();
    }

    private void AddThemeCard(Theme theme)
    {
        // mini capsule with the theme's real gradient and glow
        var bg = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        bg.GradientStops.Add(new GradientStop(theme.GradTop, 0));
        bg.GradientStops.Add(new GradientStop(theme.GradMid, 0.5));
        bg.GradientStops.Add(new GradientStop(theme.GradEnd, 1));
        var mini = new Border
        {
            Width = 64, Height = 20, CornerRadius = new CornerRadius(10),
            Background = bg,
            BorderBrush = new SolidColorBrush(theme.Border.A > 0 ? theme.Border : (theme.IsLight ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(40, 255, 255, 255))),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 6),
            Effect = new DropShadowEffect
            {
                BlurRadius = theme.HasGlow ? 10 : 6, ShadowDepth = theme.HasGlow ? 0 : 1,
                Color = theme.HasGlow ? Color.FromRgb(theme.Glow.R, theme.Glow.G, theme.Glow.B) : Colors.Black,
                Opacity = theme.HasGlow ? Math.Min(1, theme.Glow.A / 255.0 * 2) : 0.3,
            },
        };
        var inner = new Grid();
        inner.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(theme.Accent), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6, 0, 0, 0) });
        var bars = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 7, 0) };
        foreach (var h in new[] { 5, 9, 6 })
            bars.Children.Add(new Rectangle { Width = 2, Height = h, RadiusX = 1, RadiusY = 1, Margin = new Thickness(1, 0, 1, 0), Fill = new SolidColorBrush(theme.Equalizer), VerticalAlignment = VerticalAlignment.Center });
        inner.Children.Add(bars);
        mini.Child = inner;

        var label = new TextBlock { Text = theme.Name, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");

        var check = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 4, 0), Visibility = Visibility.Collapsed,
        };
        check.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        var tick = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        tick.SetResourceReference(TextBlock.ForegroundProperty, "AccentFgBrush");
        check.Child = tick;

        var column = new StackPanel();
        column.Children.Add(mini);
        column.Children.Add(label);
        var content = new Grid();
        content.Children.Add(column);
        content.Children.Add(check);

        var card = new Button { Content = content, Style = (Style)FindResource("ThemeCard"), Tag = theme.Name };
        card.Click += (_, _) =>
        {
            if (_settings.Theme == theme.Name) return;
            _settings.Theme = theme.Name;
            _settings.Save();
            ApplyTheme(theme, animate: true);
        };
        ThemePanel.Children.Add(card);
        _themeCards.Add((card, check, theme));
    }

    /// <summary>Selection follows Settings.Theme only.</summary>
    private void RefreshThemeCards()
    {
        foreach (var (card, check, theme) in _themeCards)
        {
            bool selected = theme.Name == _settings.Theme;
            check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            if (selected) card.SetResourceReference(BorderBrushProperty, "AccentBrush");
            else card.BorderBrush = Brushes.Transparent;
        }
    }

    // ================= Settings: toggles and chips

    private void RefreshToggles()
    {
        SetToggle(LyricsToggle, LyricsKnob, _settings.ShowLyrics, animate: !_loading);
        SetToggle(AutoStartToggle, AutoStartKnob, AutoStart.IsEnabled, animate: !_loading);
        SetToggle(NextEventToggle, NextEventKnob, _settings.ShowNextEvent, animate: !_loading);
    }

    private static void SetToggle(Border track, Ellipse knob, bool on, bool animate)
    {
        var dur = TimeSpan.FromMilliseconds(animate ? 160 : 0);
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

    private void RefreshChips()
    {
        SetChip(UnitC, !_settings.Fahrenheit);
        SetChip(UnitF, _settings.Fahrenheit);
        SetChip(WeekMon, _settings.WeekStartsMonday);
        SetChip(WeekSun, !_settings.WeekStartsMonday);
        RefreshFormChips();
    }

    private void LyricsRow_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.ShowLyrics = !_settings.ShowLyrics;
        _settings.Save();
        RefreshToggles();
        if (_settings.ShowLyrics && _track != null) UpdateLyricsSource(_track);
        UpdateStrip();
    }

    private void AutoStartRow_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AutoStart.Set(!AutoStart.IsEnabled);
        RefreshToggles();
    }

    private void NextEventRow_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.ShowNextEvent = !_settings.ShowNextEvent;
        _settings.Save();
        RefreshToggles();
        UpdateHomeEvent();
    }

    private void WeekMon_Click(object sender, RoutedEventArgs e) => SetWeekStart(true);
    private void WeekSun_Click(object sender, RoutedEventArgs e) => SetWeekStart(false);

    private void SetWeekStart(bool monday)
    {
        if (_settings.WeekStartsMonday == monday) return;
        _settings.WeekStartsMonday = monday;
        _settings.Save();
        RefreshChips();
        RenderCalendar(animateDir: 0);
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        _uiTimer.Stop();
        StopMedia();
        DisableKeyboard();
        var shrink = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        shrink.Completed += (_, _) => Application.Current.Shutdown();
        LyricsStrip.Visibility = Visibility.Collapsed;
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }
}
