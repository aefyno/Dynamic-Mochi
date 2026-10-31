using System.Globalization;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DynamicMochi.Services;

namespace DynamicMochi;

/// <summary>Calendar tab, "Таймер" mode: quick 1/5/10/25 min, custom time; ring + time in the collapsed capsule.</summary>
public partial class IslandWindow
{
    private bool _timerRunning;            // started and not finished (may be paused)
    private bool _timerPaused;
    private DateTime _timerEnd;
    private TimeSpan _timerLeft;           // while paused
    private TimeSpan _timerTotal = TimeSpan.FromMinutes(5);
    private string _calMode = "calendar";

    private static readonly int[] QuickMinutes = { 1, 5, 10, 25 };

    private void InitTimer()
    {
        TimerQuick.Children.Clear();
        foreach (int m in QuickMinutes)
        {
            var chip = new Button { Style = (Style)FindResource("Chip"), Content = Loc.F("timer.min", m), Height = 30, Padding = new Thickness(12, 0, 12, 0) };
            chip.SetResourceReference(BackgroundProperty, "SurfaceBrush");
            int minutes = m;
            chip.Click += (_, _) => StartTimer(TimeSpan.FromMinutes(minutes));
            TimerQuick.Children.Add(chip);
        }
        SetCalMode(_calMode);
        RenderTimer();
    }

    // ----- mode switch: Календарь / Задачи / Таймер

    private void CalMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string mode) SetCalMode(mode);
    }

    private void SetCalMode(string mode)
    {
        if (mode != "calendar") CloseEventForm(animate: false);
        _calMode = mode;
        CalendarView.Visibility = mode == "calendar" ? Visibility.Visible : Visibility.Collapsed;
        TasksView.Visibility = mode == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        TimerView.Visibility = mode == "timer" ? Visibility.Visible : Visibility.Collapsed;
        SetChip(ModeCalendar, mode == "calendar");
        SetChip(ModeTasks, mode == "tasks");
        SetChip(ModeTimer, mode == "timer");
        if (mode == "tasks") RenderTasks();
        if (mode == "timer") RenderTimer();
        DisableKeyboard();
    }

    // ----- timer

    private TimeSpan TimerLeft => !_timerRunning ? _timerTotal : _timerPaused ? _timerLeft : (_timerEnd - DateTime.Now) is var l && l > TimeSpan.Zero ? l : TimeSpan.Zero;

    private void StartTimer(TimeSpan total)
    {
        if (total <= TimeSpan.Zero) return;
        _timerTotal = total;
        _timerEnd = DateTime.Now + total;
        _timerRunning = true;
        _timerPaused = false;
        Log.Write("timer: started " + total);
        DisableKeyboard();
        RenderTimer();
        ApplyMusicSettings();   // the clock column shows the timer even when "clock with music" is off
    }

    private void TimerTogglePause()
    {
        if (!_timerRunning) { StartTimer(_timerTotal); return; }
        if (_timerPaused)
        {
            _timerEnd = DateTime.Now + _timerLeft;
            _timerPaused = false;
        }
        else
        {
            _timerLeft = TimerLeft;
            _timerPaused = true;
        }
        RenderTimer();
    }

    private void StopTimer()
    {
        _timerRunning = false;
        _timerPaused = false;
        RenderTimer();
        UpdateClocks(force: true);
        ApplyMusicSettings();
    }

    private void TimerFace_Click(object sender, MouseButtonEventArgs e) => TimerTogglePause();
    private void TimerPause_Click(object sender, RoutedEventArgs e) => TimerTogglePause();
    private void TimerStop_Click(object sender, RoutedEventArgs e) => StopTimer();

    private void TimerStartCustom_Click(object sender, RoutedEventArgs e)
    {
        if (TryParseDuration(TimerInput.Text, out var d)) { StartTimer(d); TimerInput.Text = ""; }
        else { Shake(TimerInput); FocusField(TimerInput); }
    }

    private void TimerInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { TimerStartCustom_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { DisableKeyboard(); e.Handled = true; }
    }

    /// <summary>"7" = 7 min, "7:30" = 7 min 30 s, "1:05:00" = 1 h 5 min.</summary>
    private static bool TryParseDuration(string text, out TimeSpan d)
    {
        d = TimeSpan.Zero;
        var parts = text.Trim().Replace('.', ':').Replace(',', ':').Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 3) return false;
        var nums = new List<int>();
        foreach (var p in parts)
        {
            if (!int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0) return false;
            nums.Add(n);
        }
        d = nums.Count switch
        {
            1 => TimeSpan.FromMinutes(nums[0]),
            2 => new TimeSpan(0, nums[0], nums[1]),
            _ => new TimeSpan(nums[0], nums[1], nums[2]),
        };
        return d > TimeSpan.Zero && d < TimeSpan.FromHours(24);
    }

    private static string FmtLeft(TimeSpan t)
    {
        int s = (int)Math.Ceiling(t.TotalSeconds);
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60:00}:{s % 60:00}";
    }

    private void RenderTimer()
    {
        var left = TimerLeft;
        double frac = _timerTotal.TotalSeconds > 0 ? left.TotalSeconds / _timerTotal.TotalSeconds : 0;
        TimerText.Text = FmtLeft(left);
        TimerRing.Value = _timerRunning ? frac : 1;
        TimerState.Text = !_timerRunning ? Loc.T("timer.ready") : _timerPaused ? Loc.T("timer.paused") : Loc.T("timer.click");
        TimerControls.Visibility = _timerRunning ? Visibility.Visible : Visibility.Collapsed;
        TimerPauseButton.Content = Loc.T(_timerPaused ? "timer.resume" : "timer.pause");

        // collapsed capsule: ring + time instead of the clock
        var ring = _timerRunning ? Visibility.Visible : Visibility.Collapsed;
        MiniTimerRing.Visibility = ring;
        IdleTimerRing.Visibility = ring;
        if (_timerRunning)
        {
            string text = FmtLeft(left);
            MiniClock.Text = text;
            IdleClock.Text = text;
            MinClock.Text = text;
            MiniTimerRing.Value = frac;
            IdleTimerRing.Value = frac;
            double dim = _timerPaused ? 0.55 : 1;
            MiniClock.Opacity = IdleClock.Opacity = dim;
        }
        else MiniClock.Opacity = IdleClock.Opacity = 1;
    }

    private void TimerSecondTick()
    {
        if (!_timerRunning) return;
        if (!_timerPaused && DateTime.Now >= _timerEnd)
        {
            var total = _timerTotal;
            StopTimer();
            Log.Write("timer: done " + total);
            try { SystemSounds.Exclamation.Play(); } catch { }
            string label = total.TotalMinutes >= 1 && total.Seconds == 0 ? Loc.F("timer.min", (int)total.TotalMinutes) : FmtLeft(total);
            ShowNotice(new Notice
            {
                Kind = "timer",
                Title = Loc.F("timer.done", label),
                Glyph = "",
                Width = 380,
                Duration = TimeSpan.FromSeconds(8),
                Blink = true,
                Click = () => { SetCalMode("timer"); SelectTab("calendar", animate: false); },
            });
            return;
        }
        RenderTimer();
    }
}
