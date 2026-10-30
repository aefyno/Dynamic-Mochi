using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicMochi.Controls;
using DynamicMochi.Services;

namespace DynamicMochi;

public partial class IslandWindow : Window
{
    // Capsule sizes
    private const double CollapsedW = 320, IdleW = 200, CollapsedH = 44;
    private const double ExpandedW = 560;
    private const double HomeH = 210, MusicH = 236, MusicWithLyricsH = 290, SettingsH = 320;

    private readonly Settings _settings = Settings.Load();
    private readonly MediaService _media = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _expandTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _eqTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private static readonly CultureInfo Ru = new("ru-RU");
    private static readonly Random Rng = new();

    // Theme brushes (unfrozen so colours can animate)
    private readonly Dictionary<string, SolidColorBrush> _brushes = new();
    private Theme _theme = Themes.All[0];
    private readonly List<(Button Card, Border Check, Theme Theme)> _themeCards = new();

    private TrackSnapshot? _track;
    private string _shownKey = "";
    private bool _hasArt;
    private bool _expanded;
    private bool _polling;
    private bool _loading = true;
    private string _page = "home";
    private bool _showTotal;
    private bool _dragging;
    private double _dragFraction;
    private readonly double[] _eqTarget = { 4, 4, 4, 4 };
    private readonly double[] _eqNow = { 4, 4, 4, 4 };

    // Lyrics
    private string _lyrKey = "";
    private LyricsResult? _lyrics;
    private int _lyrAttempts;
    private DateTime? _lyrRetryAt;
    private bool _lyrBusy;
    private DateTime? _noDurationSince;
    private int _shownIndex = int.MinValue;
    private bool _shownDots;
    private int _previewIndex = int.MinValue;

    public IslandWindow()
    {
        InitializeComponent();
        CreateBrushes();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        _uiTimer.Tick += (_, _) => UiTick();
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _eqTimer.Tick += (_, _) => RetargetEqualizer();
        _expandTimer.Tick += (_, _) =>
        {
            _expandTimer.Stop();
            if (Capsule.IsMouseOver && !_expanded) Expand();
        };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!Capsule.IsMouseOver && _expanded) Collapse();
        };
    }

    // ================= Window

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.MakeToolWindow(hwnd);
        PlaceOnScreen();
    }

    private void PlaceOnScreen()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _theme = Themes.Find(_settings.Theme);
            ApplyTheme(_theme, animate: false);
            BuildThemeCards();
            SetToggle(LyricsToggle, LyricsKnob, _settings.ShowLyrics, animate: false);
            SetToggle(AutoStartToggle, AutoStartKnob, AutoStart.IsEnabled, animate: false);
            ShowTrack(null);
            SelectTab("home", animate: false);
        }
        catch (Exception ex)
        {
            Log.Write("startup: " + ex);
        }
        finally
        {
            _loading = false;

            // appear from a point; must run even if setup failed, otherwise the island stays invisible
            var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
            var grow = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { EasingFunction = spring };
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }

        await _media.InitAsync();
        _uiTimer.Start();
        _pollTimer.Start();
        _eqTimer.Start();
        await PollAsync();
    }

    // ================= Theme

    private void CreateBrushes()
    {
        foreach (var key in new[] { "SurfaceBrush", "SurfaceHoverBrush", "FgBrush", "SubBrush", "FaintBrush", "AccentBrush",
                                    "AccentFgBrush", "BorderBrush", "ToggleOffBrush", "EqBrush", "TabPillBrush", "TabActiveBrush",
                                    "CardFillBrush", "CardStrokeBrush" })
        {
            var b = new SolidColorBrush(((SolidColorBrush)Resources[key]).Color);
            _brushes[key] = b;
            Resources[key] = b;
        }
        MiniTitle.SetForegroundResource("FgBrush");
        HomeTitle.SetForegroundResource("FgBrush");
        MusicTitle.SetForegroundResource("FgBrush");
    }

    private void ApplyTheme(Theme t, bool animate)
    {
        _theme = t;
        var dur = TimeSpan.FromMilliseconds(animate ? 250 : 0);

        void Set(string key, Color c)
        {
            var b = _brushes[key];
            if (b.IsFrozen)
            {
                // WPF freezes brushes that end up in styles/templates; swap in a fresh one
                b = new SolidColorBrush(b.Color);
                _brushes[key] = b;
                Resources[key] = b;
            }
            if (animate) b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(c, dur));
            else { b.BeginAnimation(SolidColorBrush.ColorProperty, null); b.Color = c; }
        }
        void SetStop(GradientStop s, Color c)
        {
            if (animate) s.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(c, dur));
            else { s.BeginAnimation(GradientStop.ColorProperty, null); s.Color = c; }
        }

        Set("SurfaceBrush", t.Surface);
        Set("SurfaceHoverBrush", t.SurfaceHover);
        Set("FgBrush", t.Fg);
        Set("SubBrush", t.Sub);
        Set("FaintBrush", t.Faint);
        Set("AccentBrush", t.Accent);
        Set("AccentFgBrush", t.AccentFg);
        Set("BorderBrush", t.Border);
        Set("ToggleOffBrush", t.ToggleOff);
        Set("EqBrush", t.Equalizer);
        // monochrome: active tab is an inverted pill; cards are outlines instead of fills
        Set("TabPillBrush", t.OutlineCards ? t.Accent : t.Surface);
        Set("TabActiveBrush", t.OutlineCards ? t.AccentFg : t.Accent);
        Set("CardFillBrush", t.OutlineCards ? Colors.Transparent : t.Surface);
        Set("CardStrokeBrush", t.OutlineCards ? t.Border : WithAlpha(t.Border, 0.5));

        SetStop(BgTopStop, t.BgTop);
        SetStop(BgBottomStop, t.BgBottom);
        SetStop(StripTopStop, WithAlpha(t.BgTop, 0.88));
        SetStop(StripBottomStop, WithAlpha(t.BgBottom, 0.88));

        Capsule.BorderThickness = new Thickness(t.BorderThickness);
        LyricsStrip.BorderThickness = new Thickness(t.BorderThickness);
        ShadowEffect.Opacity = t.IsLight ? 0.25 : 0.45;

        if (animate)
        {
            GlowEffect.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation(t.Glow, dur));
            GlowEffect.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(t.Glow.A / 255.0, dur));
        }
        else
        {
            GlowEffect.Color = t.Glow;
            GlowEffect.Opacity = t.Glow.A / 255.0;
        }

        UpdateTint(animate ? 250 : 0);
        _shownIndex = int.MinValue;   // redraw the lyric line with the new colours
        RefreshThemeCards();
        SetToggle(LyricsToggle, LyricsKnob, _settings.ShowLyrics, animate);
        SetToggle(AutoStartToggle, AutoStartKnob, AutoStart.IsEnabled, animate);
    }

    private static Color WithAlpha(Color c, double alpha)
    {
        c.A = (byte)Math.Round(c.A * alpha);
        return c;
    }

    /// <summary>Colour blob in the top-right corner: artwork colour, or the accent when idle.</summary>
    private void UpdateTint(int ms)
    {
        Color c;
        if (!_theme.ArtworkTint)
            c = Colors.Transparent;
        else if (_artColor is Color art && _track != null)
            c = WithAlpha(art, _theme.TintOpacity);
        else
            c = WithAlpha(_theme.Accent, _theme.IsLight ? 0.06 : 0.10);

        if (ms > 0) TintStop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(c, TimeSpan.FromMilliseconds(ms)));
        else { TintStop.BeginAnimation(GradientStop.ColorProperty, null); TintStop.Color = c; }
    }

    private Color? _artColor;

    // ================= Expand / collapse

    private void Capsule_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        if (_expanded) return;
        _expandTimer.Start();
        // anticipation: grow a little before opening
        Scale(RootScale, 1.02, 150);
    }

    private void Capsule_MouseLeave(object sender, MouseEventArgs e)
    {
        _expandTimer.Stop();
        if (!_expanded) { Scale(RootScale, 1, 150); return; }
        _collapseTimer.Start();
    }

    private static void Scale(ScaleTransform st, double to, int ms)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private double PageHeight(string page) => page switch
    {
        "music" => _lyrics != null && _track != null && _track.Key == _lyrKey ? MusicWithLyricsH : MusicH,
        "settings" => SettingsH,
        _ => HomeH,
    };

    private double CollapsedWidth => _track != null ? CollapsedW : IdleW;

    private void Expand()
    {
        _expanded = true;
        Scale(RootScale, 1, 120);
        var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
        AnimateSize(ExpandedW, PageHeight(_page), 380, spring);
        Fade(CollapsedView, 0, 120, 0);
        ExpandedView.Visibility = Visibility.Visible;
        Fade(ExpandedView, 1, 180, 120);
        Slide(ExpandedShift, 6, 0, 180, 120);
        UpdateStrip();
        _uiTimer.Interval = TimeSpan.FromMilliseconds(50);
    }

    private void Collapse()
    {
        _expanded = false;
        if (_dragging) { _dragging = false; ProgressTrack.ReleaseMouseCapture(); }
        AnimateSize(CollapsedWidth, CollapsedH, 260, new CubicEase { EasingMode = EasingMode.EaseInOut });
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) => { if (!_expanded) ExpandedView.Visibility = Visibility.Collapsed; };
        ExpandedView.BeginAnimation(OpacityProperty, fade);
        Fade(CollapsedView, 1, 200, 120);
        UpdateStrip();
    }

    private void AnimateSize(double w, double h, int ms, IEasingFunction ease)
    {
        var dur = TimeSpan.FromMilliseconds(ms);
        Capsule.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(w, dur) { EasingFunction = ease });
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

    // ================= Tabs

    private static readonly string[] Pages = { "home", "music", "settings" };

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string page && page != _page) SelectTab(page, animate: true);
    }

    private FrameworkElement PageElement(string page) => page switch
    {
        "music" => MusicPage,
        "settings" => SettingsPage,
        _ => HomePage,
    };

    private void SelectTab(string page, bool animate)
    {
        string old = _page;
        _page = page;
        int dir = Math.Sign(Array.IndexOf(Pages, page) - Array.IndexOf(Pages, old));

        // pill slides to the active tab
        double x = Array.IndexOf(Pages, page) * 40;
        TabPillShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(animate ? 220 : 0))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });
        TabHome.SetResourceReference(ForegroundProperty, page == "home" ? "TabActiveBrush" : "SubBrush");
        TabMusic.SetResourceReference(ForegroundProperty, page == "music" ? "TabActiveBrush" : "SubBrush");
        TabSettings.SetResourceReference(ForegroundProperty, page == "settings" ? "TabActiveBrush" : "SubBrush");
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

        if (_expanded) AnimateSize(ExpandedW, PageHeight(page), animate ? 220 : 0, new CubicEase { EasingMode = EasingMode.EaseInOut });
    }

    // ================= Media

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            PlaceOnScreen();
            var t = await _media.ReadAsync();
            if (!_dragging) _track = t;
            else if (t != null && _track != null) _track = t with { PositionSec = _track.PositionSec, PositionStampUtc = _track.PositionStampUtc };
            ShowTrack(_track);
            if (_track != null) UpdateLyricsSource(_track);
        }
        catch (Exception ex)
        {
            Log.Write("media poll failed: " + ex.Message);
        }
        finally
        {
            _polling = false;
        }
    }

    private void ShowTrack(TrackSnapshot? t)
    {
        bool has = t != null;
        bool playing = t?.IsPlaying == true;

        // collapsed: playing row vs idle row
        if (has && PlayingRow.Visibility != Visibility.Visible)
        {
            IdleRow.Visibility = Visibility.Collapsed;
            PlayingRow.Visibility = Visibility.Visible;
            Fade(PlayingRow, 1, 200, 0);
            if (!_expanded) AnimateSize(CollapsedW, CollapsedH, 300, new CubicEase { EasingMode = EasingMode.EaseInOut });
        }
        else if (!has && IdleRow.Visibility != Visibility.Visible)
        {
            PlayingRow.Visibility = Visibility.Collapsed;
            IdleRow.Visibility = Visibility.Visible;
            Fade(IdleRow, 1, 200, 0);
            if (!_expanded) AnimateSize(IdleW, CollapsedH, 300, new CubicEase { EasingMode = EasingMode.EaseInOut });
        }

        // paused look: dim title and art
        MiniTitle.SetForegroundResource(playing ? "FgBrush" : "SubBrush");
        MiniArtBox.Opacity = playing ? 1 : 0.7;

        string playIcon = playing ? "" : "";
        string playTip = playing ? "Пауза" : "Играть";
        if ((string)MusicPlay.Content != playIcon)
        {
            SwapIcon(MusicPlay, playIcon);
            SwapIcon(HomePlay, playIcon);
            MusicPlay.ToolTip = playTip;
            HomePlay.ToolTip = playTip;
        }

        HomeMusic.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        HomeEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        MusicButtons.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        ProgressTrack.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        SourceRow.Visibility = has && !string.IsNullOrEmpty(t!.Source) ? Visibility.Visible : Visibility.Collapsed;

        HintText.Text = !has ? "Тишина" : playing ? (string.IsNullOrEmpty(t!.Source) ? "Играет" : "Играет в " + t.Source) : "На паузе";

        if (t == null)
        {
            if (_shownKey != "")
            {
                _shownKey = "";
                _hasArt = false;
                _artColor = null;
                MusicTitle.Text = "Ничего не играет";
                MusicArtist.Text = "Включи музыку в любом плеере";
                TimeLeft.Text = "";
                TimeRight.Text = "";
                SetArt(null, animate: true);
                UpdateTint(600);
            }
            UpdateStrip();
            return;
        }

        bool newTrack = t.Key != _shownKey;
        if (newTrack || (!_hasArt && t.Thumbnail != null))
        {
            _shownKey = t.Key;
            MiniTitle.Text = t.Title;
            MusicTitle.Text = t.Title;
            HomeTitle.Text = t.Title;
            MusicArtist.Text = t.Artist;
            HomeArtist.Text = t.Artist;
            SourceText.Text = t.Source;
            var img = ToImage(t.Thumbnail);
            _hasArt = img != null;
            _artColor = ArtColor.Dominant(t.Thumbnail);
            SetArt(img, animate: !_loading);
            UpdateTint(600);
            if (newTrack && !_loading) Squish();
        }
        UpdateStrip();
    }

    /// <summary>Crossfade the artwork into all three slots; the big one "presses in" 0.96 → 1.</summary>
    private void SetArt(BitmapImage? img, bool animate)
    {
        foreach (var image in new[] { MiniArt, HomeArt, BigArt })
        {
            if (!animate) { image.Source = img; continue; }
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
            var target = image;
            fadeOut.Completed += (_, _) =>
            {
                target.Source = img;
                target.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(110)));
            };
            image.BeginAnimation(OpacityProperty, fadeOut);
        }
        if (animate)
        {
            var press = new DoubleAnimationUsingKeyFrames();
            press.KeyFrames.Add(new LinearDoubleKeyFrame(0.96, TimeSpan.FromMilliseconds(110)));
            press.KeyFrames.Add(new EasingDoubleKeyFrame(1, TimeSpan.FromMilliseconds(220), new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }));
            BigArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, press);
            BigArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, press);
        }
    }

    /// <summary>Light width "squish" of the capsule when the track changes.</summary>
    private void Squish()
    {
        double w = _expanded ? ExpandedW : CollapsedWidth;
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new EasingDoubleKeyFrame(w * 1.03, TimeSpan.FromMilliseconds(110), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(w, TimeSpan.FromMilliseconds(220), new QuadraticEase { EasingMode = EasingMode.EaseInOut }));
        Capsule.BeginAnimation(FrameworkElement.WidthProperty, a);
    }

    /// <summary>Play ↔ pause icon change: rotate + fade.</summary>
    private static void SwapIcon(Button b, string icon)
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(75));
        fade.Completed += (_, _) =>
        {
            b.Content = icon;
            b.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(75)));
        };
        b.BeginAnimation(OpacityProperty, fade);
    }

    private static BitmapImage? ToImage(byte[]? data)
    {
        if (data == null || data.Length == 0) return null;
        try
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(data);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 240;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_track != null)
        {
            var now = DateTime.UtcNow;
            _track = _track with { IsPlaying = !_track.IsPlaying, PositionSec = _track.PositionAt(now), PositionStampUtc = now };
            ShowTrack(_track);
        }
        await _media.PlayPauseAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        // like most players: restart the track unless we are in its first 3 seconds
        if (_track != null && _track.PositionAt(DateTime.UtcNow) > 3)
        {
            _track = _track with { PositionSec = 0, PositionStampUtc = DateTime.UtcNow };
            await _media.SeekAsync(0);
            return;
        }
        await _media.PreviousAsync();
    }

    private void HomeArt_Click(object sender, MouseButtonEventArgs e) => SelectTab("music", animate: true);

    // ----- progress bar: hover grows, click and drag seek with a preview

    private void Progress_MouseEnter(object sender, MouseEventArgs e)
    {
        AnimateHeight(ProgressBg, 6); AnimateHeight(ProgressFill, 6);
        ProgressThumb.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
    }

    private void Progress_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_dragging) return;
        AnimateHeight(ProgressBg, 4); AnimateHeight(ProgressFill, 4);
        ProgressThumb.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
    }

    private static void AnimateHeight(FrameworkElement el, double to) =>
        el.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(120)));

    private void Progress_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_track == null || _track.DurationSec <= 0) return;
        _dragging = true;
        _dragFraction = Fraction(e.GetPosition(ProgressTrack).X);
        ProgressTrack.CaptureMouse();
    }

    private void Progress_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragFraction = Fraction(e.GetPosition(ProgressTrack).X);
    }

    private async void Progress_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ProgressTrack.ReleaseMouseCapture();
        _dragFraction = Fraction(e.GetPosition(ProgressTrack).X);
        if (_track == null || _track.DurationSec <= 0) return;
        double target = _dragFraction * _track.DurationSec;
        _track = _track with { PositionSec = target, PositionStampUtc = DateTime.UtcNow };
        if (!ProgressTrack.IsMouseOver) Progress_MouseLeave(sender, e);
        await _media.SeekAsync(target);
    }

    private double Fraction(double x) => ProgressTrack.ActualWidth <= 0 ? 0 : Math.Clamp(x / ProgressTrack.ActualWidth, 0, 1);

    private void TimeRight_Click(object sender, MouseButtonEventArgs e) => _showTotal = !_showTotal;

    // ================= 50 ms tick: clocks, equalizer, progress, lyric line

    private void UiTick()
    {
        var local = DateTime.Now;
        string hm = local.ToString("HH:mm", Ru);
        if (MiniClock.Text != hm)
        {
            MiniClock.Text = hm; IdleClock.Text = hm; BigClock.Text = hm; SmallClock.Text = hm;
            string date = local.ToString("dddd, d MMMM", Ru);
            DateText.Text = Capital(date);
            IdleDate.Text = Capital(local.ToString("ddd, d MMM", Ru).Replace(".", ""));
        }

        // equalizer: ease each bar toward its target
        var bars = new[] { Bar0, Bar1, Bar2, Bar3 };
        for (int i = 0; i < bars.Length; i++)
        {
            _eqNow[i] += (_eqTarget[i] - _eqNow[i]) * 0.35;
            bars[i].Height = Math.Round(_eqNow[i], 1);
        }

        var track = _track;
        double pos = track?.PositionAt(DateTime.UtcNow) ?? 0;
        if (track != null && _expanded)
        {
            double dur = track.DurationSec;
            double frac = _dragging ? _dragFraction : (dur > 0 ? Math.Clamp(pos / dur, 0, 1) : 0);
            if (_page == "music")
            {
                double w = ProgressTrack.ActualWidth;
                ProgressFill.Width = w * frac;
                ProgressThumb.Margin = new Thickness(w * frac - 6, 0, 0, 0);
                double shown = _dragging ? frac * dur : pos;
                TimeLeft.Text = Fmt(shown);
                TimeRight.Text = dur > 0 ? (_showTotal ? Fmt(dur) : "-" + Fmt(Math.Max(0, dur - shown))) : "";
                UpdateLyricsPreview(pos);
            }
            else if (_page == "home")
            {
                HomeProgress.Width = Math.Max(0, HomeProgressTrack.ActualWidth * frac);
            }
        }

        UpdateLyricLine(local.TimeOfDay.TotalSeconds, pos);
    }

    private void RetargetEqualizer()
    {
        bool playing = _track?.IsPlaying == true;
        for (int i = 0; i < _eqTarget.Length; i++)
            _eqTarget[i] = playing ? 4 + Rng.NextDouble() * 12 : 3;
    }

    private static string Capital(string s) => s.Length > 0 ? char.ToUpper(s[0], Ru) + s.Substring(1) : s;

    private static string Fmt(double sec)
    {
        int s = (int)Math.Max(0, sec);
        return $"{s / 60}:{s % 60:00}";
    }

    // ================= Lyrics (matching rules live in LyricsService, ported from the Mac version)

    private void UpdateLyricsSource(TrackSnapshot t)
    {
        if (!_settings.ShowLyrics) return;
        var now = DateTime.UtcNow;

        if (t.Key != _lyrKey)
        {
            _lyrKey = t.Key;
            _lyrics = null;
            _lyrAttempts = 0;
            _lyrRetryAt = now;
            _noDurationSince = null;
            _shownIndex = int.MinValue;
            _previewIndex = int.MinValue;
            UpdateStrip();
        }

        // Wait for the duration so we do not match the wrong song; after 5 s search without it.
        double duration = t.DurationSec;
        if (duration <= 0)
        {
            _noDurationSince ??= now;
            if (now - _noDurationSince.Value < TimeSpan.FromSeconds(5)) return;
        }
        else _noDurationSince = null;

        bool due = _lyrics == null && _lyrRetryAt.HasValue && now >= _lyrRetryAt.Value;
        if (!due) return;
        if (_lyrBusy)
        {
            _lyrRetryAt = now;   // search again as soon as the running search ends
            return;
        }

        _lyrAttempts++;
        _lyrRetryAt = null;
        _lyrBusy = true;
        string key = t.Key, title = t.Title, artist = t.Artist;
        _ = Task.Run(async () =>
        {
            LyricsResult? result = null;
            bool failed = false;
            try { result = await LyricsService.FindAsync(title, artist, duration); }
            catch { failed = true; }
            await Dispatcher.InvokeAsync(() =>
            {
                _lyrBusy = false;
                if (key != _lyrKey) { _lyrRetryAt = DateTime.UtcNow; return; }
                if (result != null)
                {
                    _lyrics = result;
                    _shownIndex = int.MinValue;
                    _previewIndex = int.MinValue;
                    if (_expanded && _page == "music")
                        AnimateSize(ExpandedW, PageHeight("music"), 220, new CubicEase { EasingMode = EasingMode.EaseInOut });
                }
                else if (failed && _lyrAttempts < 4)
                {
                    _lyrRetryAt = DateTime.UtcNow.AddSeconds(15);
                }
                UpdateStrip();
            });
        });
    }

    private void UpdateStrip()
    {
        bool show = _settings.ShowLyrics && !_expanded && _track?.IsPlaying == true && _lyrics != null && _track.Key == _lyrKey;
        if (show && LyricsStrip.Visibility != Visibility.Visible)
        {
            LyricsStrip.Visibility = Visibility.Visible;
            Fade(LyricsStrip, 1, 150, 0);
        }
        else if (!show && LyricsStrip.Visibility == Visibility.Visible)
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
            fade.Completed += (_, _) =>
            {
                if (LyricsStrip.Opacity < 0.01) LyricsStrip.Visibility = Visibility.Collapsed;
            };
            LyricsStrip.BeginAnimation(OpacityProperty, fade);
        }
        LyricsPreview.Visibility = _lyrics != null && _track != null && _track.Key == _lyrKey ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Current line index at a position; -1 before the first line. Also reports long instrumental gaps.</summary>
    private int LineAt(double pos, out bool longBreak)
    {
        longBreak = false;
        var lyr = _lyrics!;
        int count = lyr.Lines.Count;
        if (lyr.Times == null || lyr.Times.Count != count)
            return _track != null && _track.DurationSec > 0 ? Math.Min(count - 1, (int)(pos / _track.DurationSec * count)) : 0;

        int index = -1;
        for (int i = 0; i < count; i++)
        {
            if (lyr.Times[i] <= pos + 0.15) index = i;
            else break;
        }
        if (index >= 0)
        {
            double start = lyr.Times[index];
            double length = Math.Max(0.5, (index + 1 < count ? lyr.Times[index + 1] : start + 5) - start);
            double elapsed = pos - start;
            double sing = Math.Min(length * 0.95, Math.Max(1.2, lyr.Lines[index].Length * 0.075));
            longBreak = length > 7 && elapsed > sing + 1.5 && length - elapsed > 2;
        }
        return index;
    }

    private void UpdateLyricLine(double t, double pos)
    {
        if (_lyrics == null || _track == null || LyricsStrip.Visibility != Visibility.Visible) return;
        int index = LineAt(pos, out bool longBreak);
        bool dots = index < 0 || longBreak;

        if (dots)
        {
            if (!_shownDots)
            {
                _shownDots = true;
                LyricText.Visibility = Visibility.Collapsed;
                Dots.Visibility = Visibility.Visible;
            }
            var all = new[] { Dot0, Dot1, Dot2 };
            for (int i = 0; i < all.Length; i++)
                all[i].Opacity = 0.3 + 0.7 * (0.5 + 0.5 * Math.Sin(t * (2 * Math.PI / 1.2) - i * 1.05));
            return;
        }

        if (_shownDots)
        {
            _shownDots = false;
            Dots.Visibility = Visibility.Collapsed;
            LyricText.Visibility = Visibility.Visible;
            _shownIndex = int.MinValue;
        }
        if (index == _shownIndex) return;
        _shownIndex = index;
        RenderLine(_lyrics.Lines[index]);
    }

    /// <summary>Lead vocal bold; backing vocals in parentheses smaller, italic and dimmer.</summary>
    private void RenderLine(string line)
    {
        LyricText.Inlines.Clear();
        bool first = true;
        foreach (var (text, adlib) in SplitParts(line))
        {
            if (adlib)
            {
                var run = new Run((first ? "" : " ") + "(" + text.Trim() + ")") { FontSize = 13, FontStyle = FontStyles.Italic, FontWeight = FontWeights.Normal };
                run.SetResourceReference(TextElement.ForegroundProperty, "SubBrush");
                LyricText.Inlines.Add(run);
            }
            else
            {
                LyricText.Inlines.Add(new Run(first ? text.TrimStart() : text));
            }
            first = false;
        }
        var dur = TimeSpan.FromMilliseconds(220);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        LyricText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        LyricShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, dur) { EasingFunction = ease });
    }

    private void UpdateLyricsPreview(double pos)
    {
        if (_lyrics == null || LyricsPreview.Visibility != Visibility.Visible) return;
        int index = LineAt(pos, out _);
        if (index == _previewIndex) return;
        _previewIndex = index;
        var lines = _lyrics.Lines;
        PreviewCurrent.Text = index >= 0 ? lines[index] : "…";
        PreviewNext.Text = index + 1 < lines.Count ? lines[index + 1] : "";
        var dur = TimeSpan.FromMilliseconds(220);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        PreviewCurrent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        PreviewShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, dur) { EasingFunction = ease });
    }

    private static List<(string Text, bool Adlib)> SplitParts(string line)
    {
        var parts = new List<(string Text, bool Adlib)>();
        var cur = new System.Text.StringBuilder();
        int depth = 0;
        foreach (char ch in line)
        {
            if (ch == '(')
            {
                if (depth == 0 && cur.Length > 0) { parts.Add((cur.ToString(), false)); cur.Clear(); }
                depth++;
            }
            else if (ch == ')' && depth > 0)
            {
                depth--;
                if (depth == 0) { parts.Add((cur.ToString(), true)); cur.Clear(); }
            }
            else cur.Append(ch);
        }
        if (cur.Length > 0) parts.Add((cur.ToString(), depth > 0));
        return parts.Where(p => p.Text.Trim().Length > 0).ToList();
    }

    // ================= Settings

    private void BuildThemeCards()
    {
        ThemePanel.Children.Clear();
        _themeCards.Clear();
        foreach (var theme in Themes.All)
        {
            // mini capsule in the theme's own colours
            var mini = new Border
            {
                Width = 60, Height = 16, CornerRadius = new CornerRadius(8),
                Background = new LinearGradientBrush(theme.BgTop, theme.BgBottom, 90),
                BorderBrush = new SolidColorBrush(theme.Border),
                BorderThickness = new Thickness(Math.Min(theme.BorderThickness, 1)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 6),
            };
            if (theme.Glow.A > 0)
                mini.Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 0, Color = theme.Glow, Opacity = theme.Glow.A / 255.0 };
            var inner = new Grid();
            inner.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(theme.Accent), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(5, 0, 0, 0) });
            var bars = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 6, 0) };
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

            var content = new Grid();
            var column = new StackPanel();
            column.Children.Add(mini);
            column.Children.Add(label);
            content.Children.Add(column);
            content.Children.Add(check);

            var card = new Button { Content = content, Style = (Style)FindResource("ThemeCard"), Tag = theme.Name, ToolTip = theme.Name };
            card.Click += (_, _) =>
            {
                _settings.Theme = theme.Name;
                _settings.Save();
                ApplyTheme(theme, animate: true);
            };
            ThemePanel.Children.Add(card);
            _themeCards.Add((card, check, theme));
        }
        RefreshThemeCards();
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

    private void SetToggle(Border track, Ellipse knob, bool on, bool animate)
    {
        var dur = TimeSpan.FromMilliseconds(animate ? 160 : 0);
        var tt = (TranslateTransform)knob.RenderTransform;
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(on ? 18 : 0, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        track.SetResourceReference(Border.BackgroundProperty, on ? "AccentBrush" : "ToggleOffBrush");
        knob.SetResourceReference(Shape.FillProperty, on ? "AccentFgBrush" : "FgBrush");
        if (!on) knob.Fill = Brushes.White;
    }

    private void LyricsRow_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.ShowLyrics = !_settings.ShowLyrics;
        _settings.Save();
        SetToggle(LyricsToggle, LyricsKnob, _settings.ShowLyrics, animate: true);
        if (_settings.ShowLyrics && _track != null) UpdateLyricsSource(_track);
        UpdateStrip();
    }

    private void AutoStartRow_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool on = !AutoStart.IsEnabled;
        AutoStart.Set(on);
        SetToggle(AutoStartToggle, AutoStartKnob, AutoStart.IsEnabled, animate: true);
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        // shrink to a point, then exit
        _uiTimer.Stop(); _pollTimer.Stop(); _eqTimer.Stop();
        var shrink = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        shrink.Completed += (_, _) => Application.Current.Shutdown();
        LyricsStrip.Visibility = Visibility.Collapsed;
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }
}
