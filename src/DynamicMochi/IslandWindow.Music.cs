using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicMochi.Services;

namespace DynamicMochi;

/// <summary>Now playing, progress, volume and the lyric line.</summary>
public partial class IslandWindow
{
    private readonly MediaService _media = new();
    private readonly VolumeService _volume = new();
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _eqTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private static readonly Random Rng = new();

    private TrackSnapshot? _track;
    private Color? _artColor;
    private string _shownKey = "";
    private bool _hasArt;
    private bool _polling;
    private bool _showTotal;
    private bool _dragging;
    private double _dragFraction;
    private bool _volDragging;
    private double _volFraction;
    private DateTime _volumeReadAt = DateTime.MinValue;
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

    private bool HasLyricsForTrack => _lyrics != null && _track != null && _track.Key == _lyrKey;

    private async Task StartMediaAsync()
    {
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _eqTimer.Tick += (_, _) => RetargetEqualizer();
        await _media.InitAsync();
        _pollTimer.Start();
        _eqTimer.Start();
        await PollAsync();
    }

    private void StopMedia()
    {
        _pollTimer.Stop();
        _eqTimer.Stop();
    }

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
            if (_expanded && _page == "music") RefreshVolume(force: false);
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

        UpdateCollapsedRow();

        // paused look: dim title and art
        MiniTitle.SetForegroundResource(playing ? "FgBrush" : "SubBrush");
        MiniArtBox.Opacity = playing ? 1 : 0.7;

        string playIcon = playing ? "" : "";
        string playTip = playing ? "Пауза" : "Играть";
        if (MusicPlay.Content as string != playIcon)
        {
            SwapIcon(MusicPlay, playIcon, !_loading);
            SwapIcon(HomePlay, playIcon, !_loading);
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
            if (_shownKey != "" || _loading)
            {
                _shownKey = "";
                _hasArt = false;
                _artColor = null;
                MiniTitle.Text = "";
                MusicTitle.Text = "Ничего не играет";
                MusicArtist.Text = "Включи музыку в любом плеере";
                TimeLeft.Text = "";
                TimeRight.Text = "";
                SetArt(null, animate: !_loading);
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
            if (newTrack) RefreshVolume(force: true);
        }
        UpdateStrip();
    }

    /// <summary>Crossfade the artwork into all three slots; the big one "presses in" 0.96 → 1.</summary>
    private void SetArt(BitmapImage? img, bool animate)
    {
        foreach (var image in new[] { MiniArt, MinArt, HomeArt, BigArt })
        {
            if (!animate) { image.Source = img; continue; }
            var fadeOut = new DoubleAnimation(0, Dur(110));
            var target = image;
            fadeOut.Completed += (_, _) =>
            {
                target.Source = img;
                target.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Dur(110)));
            };
            image.BeginAnimation(OpacityProperty, fadeOut);
        }
        if (animate)
        {
            var press = new DoubleAnimationUsingKeyFrames();
            press.KeyFrames.Add(new LinearDoubleKeyFrame(0.96, Dur(110)));
            press.KeyFrames.Add(new EasingDoubleKeyFrame(1, Dur(220), new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }));
            BigArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, press);
            BigArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, press);
        }
    }

    /// <summary>Light width "squish" of the capsule when the track changes.</summary>
    private void Squish()
    {
        double w = _expanded ? ExpandedW : CollapsedWidth;
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new EasingDoubleKeyFrame(w * 1.03, Dur(110), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(w, Dur(220), new QuadraticEase { EasingMode = EasingMode.EaseInOut }));
        a.Completed += (_, _) => Repaint();
        Capsule.BeginAnimation(FrameworkElement.WidthProperty, a);
    }

    /// <summary>Play ↔ pause icon change: fade out, swap, fade in.</summary>
    private static void SwapIcon(Button b, string icon, bool animate)
    {
        if (!animate) { b.Content = icon; return; }
        var fade = new DoubleAnimation(0, Dur(75));
        fade.Completed += (_, _) =>
        {
            b.Content = icon;
            b.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Dur(75)));
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
            bmp.DecodePixelWidth = 272;
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
        ProgressThumb.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Dur(120)));
    }

    private void Progress_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_dragging) return;
        AnimateHeight(ProgressBg, 4); AnimateHeight(ProgressFill, 4);
        ProgressThumb.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Dur(120)));
    }

    private static void AnimateHeight(FrameworkElement el, double to) =>
        el.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(to, Dur(120)));

    private void Progress_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_track == null || _track.DurationSec <= 0) return;
        _dragging = true;
        _dragFraction = Fraction(ProgressInner, e.GetPosition(ProgressInner).X);
        ProgressTrack.CaptureMouse();
    }

    private void Progress_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragFraction = Fraction(ProgressInner, e.GetPosition(ProgressInner).X);
    }

    private async void Progress_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ProgressTrack.ReleaseMouseCapture();
        _dragFraction = Fraction(ProgressInner, e.GetPosition(ProgressInner).X);
        if (_track == null || _track.DurationSec <= 0) return;
        double target = _dragFraction * _track.DurationSec;
        _track = _track with { PositionSec = target, PositionStampUtc = DateTime.UtcNow };
        if (!ProgressTrack.IsMouseOver) Progress_MouseLeave(sender, e);
        await _media.SeekAsync(target);
    }

    private static double Fraction(FrameworkElement track, double x) =>
        track.ActualWidth <= 0 ? 0 : Math.Clamp(x / track.ActualWidth, 0, 1);

    private void TimeRight_Click(object sender, MouseButtonEventArgs e) => _showTotal = !_showTotal;

    // ----- volume: the player's own volume, or the system volume as a fallback

    private void RefreshVolume(bool force)
    {
        if (_volDragging) return;
        if (!force && DateTime.UtcNow - _volumeReadAt < TimeSpan.FromSeconds(2)) return;
        _volumeReadAt = DateTime.UtcNow;
        try
        {
            _volume.Track(_track?.AppId ?? "", force);
            _volFraction = _volume.Volume;
            ShowVolume(_volume.Muted);
        }
        catch (Exception ex)
        {
            Log.Write("volume refresh failed: " + ex.Message);
        }
    }

    private void ShowVolume(bool muted)
    {
        double f = muted ? 0 : _volFraction;
        double w = VolumeInner.ActualWidth;
        VolumeFill.Width = Math.Max(0, w * f);
        VolumeThumb.Margin = new Thickness(w * f - 6, 0, 0, 0);
        VolumeText.Text = muted ? "—" : $"{Math.Round(_volFraction * 100)}";
        MuteButton.Content = muted || _volFraction < 0.005 ? "" : "";
    }

    private void SetVolume(double f)
    {
        _volFraction = Math.Clamp(f, 0, 1);
        _volume.Volume = _volFraction;
        if (_volFraction > 0 && _volume.Muted) _volume.Muted = false;
        ShowVolume(false);
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        bool muted = !_volume.Muted;
        _volume.Muted = muted;
        ShowVolume(muted);
    }

    private void Volume_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _volDragging = true;
        VolumeTrack.CaptureMouse();
        SetVolume(Fraction(VolumeInner, e.GetPosition(VolumeInner).X));
    }

    private void Volume_MouseMove(object sender, MouseEventArgs e)
    {
        if (_volDragging) SetVolume(Fraction(VolumeInner, e.GetPosition(VolumeInner).X));
    }

    private void Volume_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_volDragging) return;
        _volDragging = false;
        VolumeTrack.ReleaseMouseCapture();
        if (!VolumeTrack.IsMouseOver) Volume_MouseLeave(sender, e);
    }

    private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        SetVolume(_volFraction + Math.Sign(e.Delta) * 0.05);
        e.Handled = true;
    }

    private void Volume_MouseEnter(object sender, MouseEventArgs e)
    {
        AnimateHeight(VolumeBg, 6); AnimateHeight(VolumeFill, 6);
        VolumeThumb.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Dur(120)));
    }

    private void Volume_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_volDragging) return;
        AnimateHeight(VolumeBg, 4); AnimateHeight(VolumeFill, 4);
        VolumeThumb.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Dur(120)));
    }

    // ----- 50 ms: equalizer, progress, lyrics

    private void MusicTick(DateTime local)
    {
        var bars = new[] { Bar0, Bar1, Bar2, Bar3 };
        var minBars = new[] { MinBar0, MinBar1, MinBar2, MinBar3 };
        for (int i = 0; i < bars.Length; i++)
        {
            _eqNow[i] += (_eqTarget[i] - _eqNow[i]) * 0.35;
            bars[i].Height = Math.Round(_eqNow[i], 1);
            minBars[i].Height = Math.Round(Math.Max(3, _eqNow[i] * 0.8), 1);
        }

        var track = _track;
        double pos = track?.PositionAt(DateTime.UtcNow) ?? 0;
        if (track != null && _expanded)
        {
            double dur = track.DurationSec;
            double frac = _dragging ? _dragFraction : (dur > 0 ? Math.Clamp(pos / dur, 0, 1) : 0);
            if (_page == "music")
            {
                double w = ProgressInner.ActualWidth;
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
        if (_expanded && _page == "music" && !_volDragging && VolumeFill.Width == 0 && _volFraction > 0)
            ShowVolume(_volume.Muted);   // first layout pass: track width is known only now

        UpdateLyricLine(local.TimeOfDay.TotalSeconds, pos);
    }

    /// <summary>Settings: system or player volume, equalizer, lyric size, clock next to the music.</summary>
    private void ApplyMusicSettings()
    {
        _volume.PreferSystem = _settings.SystemVolume;
        _volumeReadAt = DateTime.MinValue;
        var eq = _settings.ShowEqualizer ? Visibility.Visible : Visibility.Collapsed;
        EqBars.Visibility = eq;
        MinEqBars.Visibility = eq;
        MinMusicClock.Visibility = _settings.ClockWithMusic ? Visibility.Visible : Visibility.Collapsed;
        double size = _settings.LyricsSize;
        LyricText.FontSize = size;
        LyricText.LineHeight = size + 5;
        LyricText.MaxHeight = 2 * (size + 5);
        _shownIndex = int.MinValue;
        if (_settings.ShowLyrics && _track != null) UpdateLyricsSource(_track);
        UpdateStrip();
        if (_expanded && _page == "music") RefreshVolume(force: true);
    }

    private void RetargetEqualizer()
    {
        bool playing = _track?.IsPlaying == true;
        for (int i = 0; i < _eqTarget.Length; i++)
            _eqTarget[i] = playing ? 4 + Rng.NextDouble() * 13 : 3;
    }

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
            _lyrRetryAt = now;
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
        bool show = _settings.ShowLyrics && !_expanded && _activity == null && _track?.IsPlaying == true && HasLyricsForTrack;
        if (show && LyricsStrip.Visibility != Visibility.Visible)
        {
            LyricsStrip.Visibility = Visibility.Visible;
            Fade(LyricsStrip, 1, 150, 0);
        }
        else if (!show && LyricsStrip.Visibility == Visibility.Visible)
        {
            var fade = new DoubleAnimation(0, Dur(150));
            fade.Completed += (_, _) =>
            {
                if (LyricsStrip.Opacity < 0.01) LyricsStrip.Visibility = Visibility.Collapsed;
                Repaint();
            };
            LyricsStrip.BeginAnimation(OpacityProperty, fade);
        }
        LyricsPreview.Visibility = HasLyricsForTrack ? Visibility.Visible : Visibility.Collapsed;
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
        var dur = Dur(220);
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
        var dur = Dur(220);
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
}
