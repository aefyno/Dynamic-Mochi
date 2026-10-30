using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicMochi.Services;

namespace DynamicMochi;

public partial class IslandWindow : Window
{
    // Capsule sizes
    private const double CollapsedW = 230, CollapsedH = 38;
    private const double ExpandedW = 520, ExpandedH = 250;

    private readonly Settings _settings = Settings.Load();
    private readonly MediaService _media = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private static readonly CultureInfo Ru = new("ru-RU");

    private TrackSnapshot? _track;
    private string _shownKey = "";
    private bool _expanded;
    private bool _polling;
    private bool _loading = true;
    private string _page = "home";

    // Lyrics
    private string _lyrKey = "";
    private LyricsResult? _lyrics;
    private int _lyrAttempts;
    private DateTime? _lyrRetryAt;
    private bool _lyrBusy;
    private int _shownIndex = int.MinValue;
    private bool _shownDots;

    public IslandWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        _uiTimer.Tick += (_, _) => UiTick();
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!Capsule.IsMouseOver) Collapse();
        };
    }

    // Window

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
        ApplyTheme(Themes.Find(_settings.Theme));
        BuildThemeButtons();
        LyricsSwitch.IsChecked = _settings.ShowLyrics;
        AutoStartSwitch.IsChecked = AutoStart.IsEnabled;
        SelectTab("home");
        _loading = false;

        await _media.InitAsync();
        _uiTimer.Start();
        _pollTimer.Start();
        await PollAsync();
    }

    // Expand / collapse

    private void Capsule_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        if (!_expanded) Expand();
    }

    private void Capsule_MouseLeave(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void Expand()
    {
        _expanded = true;
        AnimateSize(ExpandedW, ExpandedH, 460);
        Fade(CollapsedView, 0, 120, 0);
        ExpandedView.Visibility = Visibility.Visible;
        Fade(ExpandedView, 1, 260, 110);
        UpdateStrip();
    }

    private void Collapse()
    {
        _expanded = false;
        AnimateSize(CollapsedW, CollapsedH, 380);
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) =>
        {
            if (!_expanded) ExpandedView.Visibility = Visibility.Collapsed;
        };
        ExpandedView.BeginAnimation(OpacityProperty, fade);
        Fade(CollapsedView, 1, 220, 140);
        UpdateStrip();
    }

    private void AnimateSize(double w, double h, int ms)
    {
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
        var dur = TimeSpan.FromMilliseconds(ms);
        Capsule.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(w, dur) { EasingFunction = ease });
        Capsule.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(h, dur) { EasingFunction = ease });
    }

    private static void Fade(UIElement el, double to, int ms, int delayMs)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        el.BeginAnimation(OpacityProperty, a);
    }

    // Tabs

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string page) SelectTab(page);
    }

    private void SelectTab(string page)
    {
        _page = page;
        HomePage.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        MusicPage.Visibility = page == "music" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        TabHome.SetResourceReference(ForegroundProperty, page == "home" ? "FgBrush" : "SubBrush");
        TabMusic.SetResourceReference(ForegroundProperty, page == "music" ? "FgBrush" : "SubBrush");
        TabSettings.SetResourceReference(ForegroundProperty, page == "settings" ? "FgBrush" : "SubBrush");
    }

    // Media

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            PlaceOnScreen();
            var t = await _media.ReadAsync();
            _track = t;
            ShowTrack(t);
            if (t != null) UpdateLyricsSource(t);
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
        MiniArtBox.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        MiniTitle.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        EqBars.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        MiniClock.Visibility = has ? Visibility.Collapsed : Visibility.Visible;

        string play = t?.IsPlaying == true ? "" : "";
        MusicPlay.Content = play;
        HomePlay.Content = play;
        HomePlay.Visibility = has ? Visibility.Visible : Visibility.Collapsed;

        if (t == null)
        {
            if (_shownKey != "")
            {
                _shownKey = "";
                MusicTitle.Text = "Ничего не играет";
                MusicArtist.Text = "Включи музыку в любом плеере";
                HomeTitle.Text = "";
                HomeArtist.Text = "";
                MiniArt.Source = null;
                BigArt.Source = null;
            }
            UpdateStrip();
            return;
        }

        if (t.Key != _shownKey || (MiniArt.Source == null && t.Thumbnail != null))
        {
            _shownKey = t.Key;
            MiniTitle.Text = t.Title;
            MusicTitle.Text = t.Title;
            MusicArtist.Text = t.Artist;
            HomeTitle.Text = t.Title;
            HomeArtist.Text = t.Artist;
            var img = ToImage(t.Thumbnail);
            MiniArt.Source = img;
            BigArt.Source = img;
        }
        UpdateStrip();
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
            bmp.DecodePixelWidth = 200;
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

    private async void Previous_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

    private async void Progress_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_track == null || _track.DurationSec <= 0 || ProgressTrack.ActualWidth <= 0) return;
        double frac = Math.Clamp(e.GetPosition(ProgressTrack).X / ProgressTrack.ActualWidth, 0, 1);
        double target = frac * _track.DurationSec;
        _track = _track with { PositionSec = target, PositionStampUtc = DateTime.UtcNow };
        await _media.SeekAsync(target);
    }

    // 50 ms tick: clock, equalizer, progress, lyric line

    private void UiTick()
    {
        var local = DateTime.Now;
        MiniClock.Text = local.ToString("HH:mm", Ru);
        BigClock.Text = local.ToString("HH:mm", Ru);
        string date = local.ToString("dddd, d MMMM", Ru);
        DateText.Text = date.Length > 0 ? char.ToUpper(date[0], Ru) + date.Substring(1) : date;

        double t = local.TimeOfDay.TotalSeconds;
        bool playing = _track?.IsPlaying == true;
        var bars = new[] { Bar0, Bar1, Bar2, Bar3 };
        for (int i = 0; i < bars.Length; i++)
            bars[i].Height = playing ? 4 + 10 * Math.Abs(Math.Sin(t * (3.2 + i * 0.9) + i)) : 4;

        var track = _track;
        if (track != null && _expanded && _page == "music")
        {
            double pos = track.PositionAt(DateTime.UtcNow);
            double dur = track.DurationSec;
            ProgressFill.Width = dur > 0 ? ProgressTrack.ActualWidth * Math.Clamp(pos / dur, 0, 1) : 0;
            TimeLeft.Text = Fmt(pos);
            TimeRight.Text = dur > 0 ? "-" + Fmt(Math.Max(0, dur - pos)) : "";
        }

        UpdateLyricLine(t);
    }

    private static string Fmt(double sec)
    {
        int s = (int)Math.Max(0, sec);
        return $"{s / 60}:{s % 60:00}";
    }

    // Lyrics

    private void UpdateLyricsSource(TrackSnapshot t)
    {
        if (!_settings.ShowLyrics) return;
        if (t.DurationSec <= 0) return;   // wait for duration, otherwise we may match the wrong song

        bool newTrack = t.Key != _lyrKey;
        bool retryDue = !newTrack && _lyrics == null && _lyrRetryAt.HasValue && DateTime.UtcNow >= _lyrRetryAt.Value;
        if (!newTrack && !retryDue) return;

        if (newTrack)
        {
            _lyrKey = t.Key;
            _lyrics = null;
            _lyrAttempts = 0;
            _shownIndex = int.MinValue;
            UpdateStrip();
        }
        if (_lyrBusy)
        {
            _lyrRetryAt = DateTime.UtcNow;
            return;
        }

        _lyrAttempts++;
        _lyrRetryAt = null;
        _lyrBusy = true;
        string key = t.Key, title = t.Title, artist = t.Artist;
        double duration = t.DurationSec;
        _ = Task.Run(async () =>
        {
            LyricsResult? result = null;
            try
            {
                result = await LyricsService.FindAsync(title, artist, duration);
            }
            catch
            {
                // offline, retry later
            }
            await Dispatcher.InvokeAsync(() =>
            {
                _lyrBusy = false;
                if (key != _lyrKey)
                {
                    _lyrRetryAt = DateTime.UtcNow;
                    return;
                }
                if (result != null)
                {
                    _lyrics = result;
                    _shownIndex = int.MinValue;
                }
                else if (_lyrAttempts < 4)
                {
                    _lyrRetryAt = DateTime.UtcNow.AddSeconds(15);
                }
                UpdateStrip();
            });
        });
    }

    private void UpdateStrip()
    {
        bool show = _settings.ShowLyrics && !_expanded && _track?.IsPlaying == true && _lyrics != null
                    && _track.Key == _lyrKey;
        LyricsStrip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateLyricLine(double t)
    {
        var lyr = _lyrics;
        var track = _track;
        if (lyr == null || track == null || LyricsStrip.Visibility != Visibility.Visible) return;

        double pos = track.PositionAt(DateTime.UtcNow);
        int count = lyr.Lines.Count;
        int index;
        bool longBreak = false;

        if (lyr.Times != null && lyr.Times.Count == count)
        {
            index = -1;
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
        }
        else
        {
            index = track.DurationSec > 0 ? Math.Min(count - 1, (int)(pos / track.DurationSec * count)) : 0;
        }

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
            {
                double wave = 0.5 + 0.5 * Math.Sin(t * 3 - i * 0.7);
                all[i].Opacity = 0.3 + 0.7 * wave;
            }
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
        RenderLine(lyr.Lines[index]);
    }

    /// <summary>Lead vocal in bold; backing vocals (in parentheses) smaller, italic and dimmed.</summary>
    private void RenderLine(string line)
    {
        LyricText.Inlines.Clear();
        var sub = (Brush)FindResource("SubBrush");
        bool first = true;
        foreach (var (text, adlib) in SplitParts(line))
        {
            if (adlib)
            {
                LyricText.Inlines.Add(new Run((first ? "" : " ") + "(" + text.Trim() + ")")
                {
                    FontSize = 11.5,
                    FontStyle = FontStyles.Italic,
                    Foreground = sub,
                });
            }
            else
            {
                LyricText.Inlines.Add(new Run(first ? text.TrimStart() : text)
                {
                    FontSize = 13.5,
                    FontWeight = FontWeights.SemiBold,
                });
            }
            first = false;
        }

        // slide the new line in from below
        var dur = TimeSpan.FromMilliseconds(280);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        LyricText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        LyricShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, dur) { EasingFunction = ease });
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
            else
            {
                cur.Append(ch);
            }
        }
        if (cur.Length > 0) parts.Add((cur.ToString(), depth > 0));
        return parts.Where(p => p.Text.Trim().Length > 0).ToList();
    }

    // Settings

    private void ApplyTheme(Theme theme)
    {
        Resources["BgBrush"] = Frozen(theme.Background);
        Resources["FgBrush"] = Frozen(theme.Foreground);
        Resources["SubBrush"] = Frozen(theme.Secondary);
        Resources["AccentBrush"] = Frozen(theme.Accent);
        Resources["HoverBrush"] = Frozen(theme.Hover);
        _shownIndex = int.MinValue;   // force redraw with new theme colors
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void BuildThemeButtons()
    {
        ThemePanel.Children.Clear();
        foreach (var theme in Themes.All)
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 16,
                Height = 16,
                Fill = new SolidColorBrush(theme.Swatch),
                Stroke = new SolidColorBrush(theme.Accent),
                StrokeThickness = 2,
                Margin = new Thickness(0, 0, 6, 0),
            };
            var label = new TextBlock { Text = theme.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, theme.Name == _settings.Theme ? "FgBrush" : "SubBrush");
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(dot);
            content.Children.Add(label);

            var button = new Button
            {
                Content = content,
                Style = (Style)FindResource("TextButton"),
                Margin = new Thickness(0, 0, 6, 6),
                Tag = theme.Name,
            };
            button.Click += (_, _) =>
            {
                _settings.Theme = theme.Name;
                _settings.Save();
                ApplyTheme(theme);
                BuildThemeButtons();
            };
            ThemePanel.Children.Add(button);
        }
    }

    private void LyricsSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.ShowLyrics = LyricsSwitch.IsChecked == true;
        _settings.Save();
        if (_settings.ShowLyrics && _track != null) UpdateLyricsSource(_track);
        UpdateStrip();
    }

    private void AutoStartSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AutoStart.Set(AutoStartSwitch.IsChecked == true);
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
