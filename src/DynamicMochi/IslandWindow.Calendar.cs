using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicMochi.Controls;
using DynamicMochi.Services;

namespace DynamicMochi;

/// <summary>Calendar tab: month grid, day list, event form, reminders as a live activity in the capsule.</summary>
public partial class IslandWindow
{
    private static readonly string[] EventColors = { "#FF453A", "#FF9F0A", "#FFD60A", "#30D158", "#0A84FF", "#BF5AF2" };
    private static readonly (int? Minutes, string Label)[] ReminderOptions =
    {
        (null, "rem.none"), (0, "rem.at"), (5, "rem.5"), (15, "rem.15"), (60, "rem.60"),
    };

    private readonly ICalendarSource _calendar = new LocalCalendar();
    private DateTime _calMonth;        // first day of the shown month
    private DateTime _calSelected;     // selected day
    private DateTime _calToday;
    private Grid? _monthGrid;
    private string? _highlightId;

    // form
    private bool _formOpen;
    private CalendarEvent? _editing;
    private bool _isNew;
    private DateTime _formDate;
    private bool _formAllDay;
    private string _formColor = EventColors[4];
    private int? _formReminder = 15;
    private bool _deleteArmed;
    private readonly List<(Grid Swatch, Ellipse Ring, string Color)> _colorSwatches = new();
    private readonly List<(Button Chip, int? Minutes)> _reminderChips = new();

    // reminders
    private readonly HashSet<string> _fired = new();

    private void InitCalendar()
    {
        _calToday = DateTime.Today;
        _calSelected = _calToday;
        _calMonth = new DateTime(_calToday.Year, _calToday.Month, 1);

        // reminders that were due before this launch are not shown again
        var now = DateTime.Now;
        foreach (var e in _calendar.Events.Where(e => e.ReminderMinutes != null && e.ReminderAt < now.AddSeconds(-60)))
            _fired.Add(ReminderKey(e));

        BuildFormColors();
        BuildFormReminders();
        RenderCalendar(animateDir: 0);
        UpdateHomeEvent();
    }

    // ================= Month

    private bool MondayFirst => _settings.WeekStartsMonday;

    private void RenderCalendar(int animateDir)
    {
        if (_calMonth == default) return;
        MonthTitle.Text = Capital(_calMonth.ToString("MMMM yyyy", Ru));
        bool currentMonth = _calMonth.Year == _calToday.Year && _calMonth.Month == _calToday.Month;
        TodayButton.Visibility = currentMonth ? Visibility.Collapsed : Visibility.Visible;

        WeekdaysRow.Children.Clear();
        var all = Loc.T("weekdays.short").Split(',');   // Sunday first
        string[] names = MondayFirst ? all.Skip(1).Append(all[0]).ToArray() : all;
        foreach (var n in names)
        {
            var t = new TextBlock { Text = n, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FaintBrush");
            WeekdaysRow.Children.Add(t);
        }

        var grid = BuildMonthGrid(_calMonth);
        var old = _monthGrid;
        _monthGrid = grid;
        MonthHost.Children.Add(grid);

        if (old != null)
        {
            if (animateDir == 0)
            {
                MonthHost.Children.Remove(old);
            }
            else
            {
                // old grid leaves in the direction of travel, new one comes from the other side (220 ms)
                var dur = Dur(220);
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var oldShift = new TranslateTransform();
                old.RenderTransform = oldShift;
                var outAnim = new DoubleAnimation(0, -40 * animateDir, dur) { EasingFunction = ease };
                outAnim.Completed += (_, _) => MonthHost.Children.Remove(old);
                oldShift.BeginAnimation(TranslateTransform.XProperty, outAnim);
                old.BeginAnimation(OpacityProperty, new DoubleAnimation(0, dur));

                var newShift = new TranslateTransform();
                grid.RenderTransform = newShift;
                newShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40 * animateDir, 0, dur) { EasingFunction = ease });
                grid.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur));
            }
        }
        RenderDay();
    }

    private Grid BuildMonthGrid(DateTime month)
    {
        var grid = new Grid { Width = 224, Height = 168 };
        for (int i = 0; i < 7; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        for (int i = 0; i < 6; i++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });

        int first = MondayFirst ? 1 : 0;
        int offset = ((int)month.DayOfWeek - first + 7) % 7;
        var start = month.AddDays(-offset);
        var byDay = _calendar.Events
            .Where(e => e.Start.Date >= start && e.Start.Date < start.AddDays(42))
            .GroupBy(e => e.Start.Date)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Start).Select(e => e.Color).Distinct().Take(3).ToList());

        for (int i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);
            var cell = BuildDayCell(day, day.Month == month.Month, byDay.TryGetValue(day, out var c) ? c : null);
            Grid.SetRow(cell, i / 7);
            Grid.SetColumn(cell, i % 7);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private FrameworkElement BuildDayCell(DateTime day, bool inMonth, List<string>? dotColors)
    {
        bool today = day == _calToday;
        bool selected = day == _calSelected;
        bool weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        var cell = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var plate = new Border { CornerRadius = new CornerRadius(8), Margin = new Thickness(1, 0, 1, 0) };
        cell.Children.Add(plate);

        if (today)
        {
            var dot = new Ellipse { Width = 26, Height = 26 };
            dot.SetResourceReference(Shape.FillProperty, "AccentBrush");
            cell.Children.Add(dot);
        }
        else if (selected)
        {
            var ring = new Ellipse { Width = 26, Height = 26, StrokeThickness = 1.5 };
            ring.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            cell.Children.Add(ring);
        }

        var number = new TextBlock
        {
            Text = day.Day.ToString(CultureInfo.InvariantCulture),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = today ? FontWeights.SemiBold : FontWeights.Normal,
            Margin = new Thickness(0, 0, 0, dotColors != null ? 3 : 0),
        };
        Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
        number.SetResourceReference(TextBlock.ForegroundProperty,
            today ? "AccentFgBrush" : !inMonth ? "FaintBrush" : weekend ? "SubBrush" : "FgBrush");
        cell.Children.Add(number);

        if (dotColors != null)
        {
            var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 3) };
            foreach (var color in dotColors)
                dots.Children.Add(new Ellipse { Width = 4, Height = 4, Margin = new Thickness(1, 0, 1, 0), Fill = new SolidColorBrush(Hex(color)), Opacity = inMonth ? 1 : 0.5 });
            cell.Children.Add(dots);
        }

        cell.MouseEnter += (_, _) => { if (!today) plate.SetResourceReference(Border.BackgroundProperty, "SurfaceHoverBrush"); };
        cell.MouseLeave += (_, _) => plate.Background = null;
        cell.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2) { OpenEventForm(null, day); e.Handled = true; return; }
            SelectDay(day);
        };
        return cell;
    }

    private void SelectDay(DateTime day)
    {
        _calSelected = day.Date;
        var month = new DateTime(day.Year, day.Month, 1);
        if (month != _calMonth)
        {
            int dir = month > _calMonth ? 1 : -1;
            _calMonth = month;
            RenderCalendar(dir);
        }
        else RenderCalendar(0);
        if (_formOpen && _isNew) { _formDate = _calSelected; UpdateFormDate(); }
    }

    private void ShiftMonth(int dir)
    {
        _calMonth = _calMonth.AddMonths(dir);
        RenderCalendar(dir);
    }

    private void PrevMonth_Click(object sender, RoutedEventArgs e) => ShiftMonth(-1);
    private void NextMonth_Click(object sender, RoutedEventArgs e) => ShiftMonth(1);
    private void Today_Click(object sender, RoutedEventArgs e) => SelectDay(_calToday);

    private void Month_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ShiftMonth(e.Delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    // ================= Day list

    private string DayName(DateTime day)
    {
        if (day == _calToday) return Loc.T("day.today");
        if (day == _calToday.AddDays(1)) return Loc.T("day.tomorrow");
        if (day == _calToday.AddDays(-1)) return Loc.T("day.yesterday");
        return Capital(day.ToString(Loc.T("fmt.date.long"), Ru));
    }

    private void RenderDay()
    {
        DayTitle.Text = DayName(_calSelected);
        DayEvents.Children.Clear();
        var events = _calendar.On(_calSelected).ToList();
        DayEmpty.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var now = DateTime.Now;
        foreach (var ev in events)
        {
            bool past = ev.AllDay ? ev.Start.Date < _calToday : ev.Start < now;
            DayEvents.Children.Add(BuildEventCard(ev, past));
        }
    }

    private FrameworkElement BuildEventCard(CalendarEvent ev, bool past)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.Hand,
            Opacity = past ? 0.5 : 1,
            BorderThickness = new Thickness(ev.Id == _highlightId ? 1.5 : 0),
        };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        if (ev.Id == _highlightId) card.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(8, 8, 0, 8), Background = new SolidColorBrush(Hex(ev.Color)) });

        var text = new StackPanel { Margin = new Thickness(10, 6, 10, 7) };
        var time = new TextBlock { Text = ev.AllDay ? Loc.T("cal.allday") : Loc.Clock(ev.Start, _settings.Clock24), FontSize = 12 };
        Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
        time.SetResourceReference(TextBlock.ForegroundProperty, "SubBrush");
        var title = new MarqueeText { Text = ev.Title, FontSize = 13, Height = 18, AutoPlay = false };
        title.SetForegroundResource("FgBrush");
        text.Children.Add(time);
        text.Children.Add(title);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        card.Child = grid;

        card.MouseEnter += (_, _) => { card.SetResourceReference(Border.BackgroundProperty, "SurfaceHoverBrush"); title.Play(); };
        card.MouseLeave += (_, _) => { card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush"); title.Pause(); };
        card.MouseLeftButtonUp += (_, _) => OpenEventForm(ev, ev.Start.Date);
        return card;
    }

    // ================= Event form

    private void AddEvent_Click(object sender, RoutedEventArgs e) => OpenEventForm(null, _calSelected);

    private void OpenEventForm(CalendarEvent? existing, DateTime day)
    {
        _isNew = existing == null;
        if (existing != null)
        {
            _editing = existing.Copy();
        }
        else
        {
            var now = DateTime.Now;
            var start = day.Date == _calToday
                ? now.Date.AddHours(Math.Min(23, now.Hour + 1))
                : day.Date.AddHours(10);
            _editing = new CalendarEvent { Start = start, Color = _formColor, ReminderMinutes = 15 };
        }

        _formDate = _editing.Start.Date;
        _formAllDay = _editing.AllDay;
        _formColor = _editing.Color;
        _formReminder = _editing.ReminderMinutes;
        _deleteArmed = false;
        FormDelete.Content = Loc.T("common.delete");
        FormDelete.Visibility = _isNew ? Visibility.Collapsed : Visibility.Visible;
        FormTitle.Text = _editing.Title;
        FormTime.Text = _editing.Start.ToString("HH:mm", CultureInfo.InvariantCulture);
        UpdateFormDate();
        RefreshFormChips();

        if (!_formOpen)
        {
            _formOpen = true;
            SwapDayAndForm(showForm: true, animate: true);
        }
        FocusField(FormTitle);
    }

    private void CloseEventForm(bool animate)
    {
        if (!_formOpen) return;
        _formOpen = false;
        _editing = null;
        DisableKeyboard();
        SwapDayAndForm(showForm: false, animate: animate);
    }

    private void SwapDayAndForm(bool showForm, bool animate)
    {
        var dur = Dur(animate ? 220 : 0);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        FrameworkElement incoming = showForm ? EventForm : DayListView;
        FrameworkElement outgoing = showForm ? DayListView : EventForm;
        var inShift = showForm ? EventFormShift : DayListShift;
        var outShift = showForm ? DayListShift : EventFormShift;
        int dir = showForm ? 1 : -1;

        incoming.Visibility = Visibility.Visible;
        inShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40 * dir, 0, dur) { EasingFunction = ease });
        incoming.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur));

        outShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -40 * dir, dur) { EasingFunction = ease });
        var fade = new DoubleAnimation(0, dur);
        fade.Completed += (_, _) => { if (_formOpen != (outgoing == EventForm)) outgoing.Visibility = Visibility.Collapsed; };
        outgoing.BeginAnimation(OpacityProperty, fade);
        if (!animate) outgoing.Visibility = Visibility.Collapsed;
    }

    private void UpdateFormDate()
    {
        FormDate.Text = Capital(_formDate.ToString(Loc.T("fmt.date.short"), Ru).Replace(".", ""));
        FormTime.Visibility = _formAllDay ? Visibility.Hidden : Visibility.Visible;
    }

    private void FormDatePrev_Click(object sender, RoutedEventArgs e) { _formDate = _formDate.AddDays(-1); UpdateFormDate(); }
    private void FormDateNext_Click(object sender, RoutedEventArgs e) { _formDate = _formDate.AddDays(1); UpdateFormDate(); }

    private void FormAllDay_Click(object sender, RoutedEventArgs e)
    {
        _formAllDay = !_formAllDay;
        UpdateFormDate();
        RefreshFormChips();
    }

    private void BuildFormColors()
    {
        FormColors.Children.Clear();
        _colorSwatches.Clear();
        foreach (var color in EventColors)
        {
            var swatch = new Grid { Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            var ring = new Ellipse { Width = 22, Height = 22, StrokeThickness = 1.5, Visibility = Visibility.Hidden };
            ring.SetResourceReference(Shape.StrokeProperty, "FgBrush");
            swatch.Children.Add(ring);
            swatch.Children.Add(new Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush(Hex(color)) });
            string c = color;
            swatch.MouseLeftButtonUp += (_, _) => { _formColor = c; RefreshFormChips(); };
            FormColors.Children.Add(swatch);
            _colorSwatches.Add((swatch, ring, color));
        }
    }

    private void BuildFormReminders()
    {
        FormReminders.Children.Clear();
        _reminderChips.Clear();
        foreach (var (minutes, label) in ReminderOptions)
        {
            var chip = new Button { Style = (Style)FindResource("Chip"), Content = Loc.T(label) };
            int? m = minutes;
            chip.Click += (_, _) => { _formReminder = m; RefreshFormChips(); };
            FormReminders.Children.Add(chip);
            _reminderChips.Add((chip, minutes));
        }
    }

    private void RefreshFormChips()
    {
        foreach (var (_, ring, color) in _colorSwatches)
            ring.Visibility = string.Equals(color, _formColor, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Hidden;
        foreach (var (chip, minutes) in _reminderChips)
            SetChip(chip, minutes == _formReminder);
        SetChip(FormAllDay, _formAllDay);
    }

    private void EventForm_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseEventForm(animate: true); e.Handled = true; }
        else if (e.Key == Key.Enter) { FormSave_Click(sender, e); e.Handled = true; }
    }

    private void FormCancel_Click(object sender, RoutedEventArgs e) => CloseEventForm(animate: true);

    private void FormSave_Click(object sender, RoutedEventArgs e)
    {
        if (_editing == null) return;
        string title = FormTitle.Text.Trim();
        if (title.Length == 0) { Shake(FormTitle); FocusField(FormTitle); return; }

        DateTime start = _formDate.Date;
        if (!_formAllDay)
        {
            if (!TryParseTime(FormTime.Text, out var time)) { Shake(FormTime); FocusField(FormTime); return; }
            start = start.Add(time);
        }

        _editing.Title = title;
        _editing.Start = start;
        _editing.AllDay = _formAllDay;
        _editing.Color = _formColor;
        _editing.ReminderMinutes = _formReminder;
        _calendar.Save(_editing);
        // a reminder moved into the past is not fired retroactively
        if (_editing.ReminderMinutes != null && _editing.ReminderAt < DateTime.Now.AddSeconds(-60)) _fired.Add(ReminderKey(_editing));

        var day = start.Date;
        CloseEventForm(animate: true);
        SelectDay(day);
        UpdateHomeEvent();
    }

    private void FormDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_editing == null || _isNew) return;
        if (!_deleteArmed)
        {
            _deleteArmed = true;
            FormDelete.Content = Loc.T("common.delete.sure");
            return;
        }
        _calendar.Delete(_editing.Id);
        CloseEventForm(animate: true);
        RenderCalendar(0);
        UpdateHomeEvent();
    }

    private static bool TryParseTime(string text, out TimeSpan time)
    {
        time = default;
        string t = text.Trim().Replace('.', ':').Replace(',', ':').Replace(' ', ':');
        if (t.Length == 0) return false;
        int h, m = 0;
        if (t.Contains(':'))
        {
            var parts = t.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !int.TryParse(parts[0], out h)) return false;
            if (parts.Length > 1 && !int.TryParse(parts[1], out m)) return false;
        }
        else
        {
            if (!int.TryParse(t, out int n)) return false;
            if (t.Length <= 2) h = n;
            else { h = n / 100; m = n % 100; }
        }
        if (h < 0 || h > 23 || m < 0 || m > 59) return false;
        time = new TimeSpan(h, m, 0);
        return true;
    }

    private static void Shake(FrameworkElement el)
    {
        var tt = el.RenderTransform as TranslateTransform ?? new TranslateTransform();
        el.RenderTransform = tt;
        var a = new DoubleAnimationUsingKeyFrames();
        int i = 0;
        foreach (var x in new[] { -6.0, 6, -4, 4, -2, 0 })
            a.KeyFrames.Add(new LinearDoubleKeyFrame(x, Dur(40 * ++i)));
        tt.BeginAnimation(TranslateTransform.XProperty, a);
    }

    // ================= Home line

    private void UpdateHomeEvent()
    {
        if (!_settings.ShowNextEvent) { HomeEvent.Visibility = Visibility.Collapsed; return; }
        var now = DateTime.Now;
        var today = _calendar.On(now.Date).ToList();
        var next = today.FirstOrDefault(e => !e.AllDay && e.Start >= now.AddMinutes(-5)) ?? today.FirstOrDefault(e => e.AllDay);
        if (next == null) { HomeEvent.Visibility = Visibility.Collapsed; return; }
        HomeEventDot.Fill = new SolidColorBrush(Hex(next.Color));
        HomeEventText.Text = (next.AllDay ? Loc.T("cal.allday") : Loc.Clock(next.Start, _settings.Clock24)) + " · " + next.Title;
        HomeEvent.Visibility = Visibility.Visible;
    }

    private void HomeEvent_Click(object sender, RoutedEventArgs e)
    {
        SelectDay(DateTime.Today);
        SelectTab("calendar", animate: true);
    }

    // ================= Reminders (live activity)

    private static string ReminderKey(CalendarEvent e) => e.Id + "@" + e.ReminderAt.Ticks;

    private void CalendarSecondTick(DateTime now)
    {
        // a new day: move "today"
        if (now.Date != _calToday)
        {
            bool followToday = _calSelected == _calToday;
            _calToday = now.Date;
            if (followToday) _calSelected = _calToday;
            RenderCalendar(0);
        }
        if (now.Second == 0) UpdateHomeEvent();

        foreach (var ev in _calendar.Events)
        {
            if (ev.ReminderMinutes == null) continue;
            var at = ev.ReminderAt;
            if (now < at || now > at.AddSeconds(90)) continue;
            if (!_fired.Add(ReminderKey(ev))) continue;
            Log.Write($"reminder: {ev.Title} at {ev.Start:HH:mm}");
            if (!_settings.Reminders) continue;
            ShowActivity(ev);
        }
    }

    private static string WhenText(CalendarEvent ev, DateTime now) => WhenText(ev.AllDay, ev.Start, now);

    private static string WhenText(bool allDay, DateTime start, DateTime now)
    {
        if (allDay) return Loc.T("when.today");
        var left = start - now;
        if (left.TotalSeconds <= 30) return Loc.T("when.now");
        int minutes = (int)Math.Ceiling(left.TotalMinutes);
        if (minutes < 60) return Loc.F("when.min", minutes);
        int hours = (int)Math.Round(left.TotalHours);
        return Loc.F("when.hours", hours);
    }

    /// <summary>Reminder as a live activity; hovering opens the calendar on that event.</summary>
    private void ShowActivity(CalendarEvent ev)
    {
        var id = ev.Id;
        ShowNotice(new Notice
        {
            Kind = "reminder",
            Title = ev.Title,
            Dot = Hex(ev.Color),
            Right = () => WhenText(ev, DateTime.Now),
            Width = 380,
            Duration = TimeSpan.FromSeconds(_settings.ReminderSeconds),
            HoverOpens = true,
            Click = () =>
            {
                _highlightId = id;
                _calSelected = ev.Start.Date;
                _calMonth = new DateTime(ev.Start.Year, ev.Start.Month, 1);
                SetCalMode("calendar");
                RenderCalendar(0);
                SelectTab("calendar", animate: false);
            },
        });
    }
}
