using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DynamicMochi.Controls;
using DynamicMochi.Services;

namespace DynamicMochi;

/// <summary>Calendar tab, "Задачи" mode: to-do list with optional reminder times (shown like event reminders).</summary>
public partial class IslandWindow
{
    private readonly TaskStore _tasks = new();
    private readonly HashSet<string> _taskFired = new();
    private bool _showDone;

    private void InitTasks()
    {
        var now = DateTime.Now;
        foreach (var t in _tasks.Items.Where(t => t.RemindAt != null && t.RemindAt < now.AddSeconds(-60)))
            _taskFired.Add(t.Id + "@" + t.RemindAt!.Value.Ticks);
        RenderTasks();
    }

    /// <summary>Done more than a day ago: moves into the folded "Выполненные" group.</summary>
    private static bool Archived(TaskItem t) => t.Done && t.DoneAt is { } d && DateTime.Now - d > TimeSpan.FromDays(1);

    private void RenderTasks()
    {
        TasksPanel.Children.Clear();
        var active = _tasks.Items.Where(t => !Archived(t))
            .OrderBy(t => t.Done)
            .ThenBy(t => t.RemindAt ?? DateTime.MaxValue)
            .ThenBy(t => t.Created)
            .ToList();
        var archived = _tasks.Items.Where(Archived).OrderByDescending(t => t.DoneAt).ToList();
        foreach (var t in active) TasksPanel.Children.Add(BuildTaskRow(t));

        if (archived.Count > 0)
        {
            var header = new Button { Style = (Style)FindResource("FlatButton"), Height = 28, Margin = new Thickness(0, 4, 0, 2), HorizontalContentAlignment = HorizontalAlignment.Left };
            var hs = new StackPanel { Orientation = Orientation.Horizontal };
            var chevron = new TextBlock { Text = _showDone ? "" : "", Style = (Style)FindResource("Glyph"), FontSize = 10, Margin = new Thickness(0, 0, 8, 0) };
            chevron.SetResourceReference(TextBlock.ForegroundProperty, "SubBrush");
            var label = new TextBlock { Text = Loc.F("tasks.done", archived.Count), FontSize = 12 };
            label.SetResourceReference(TextBlock.ForegroundProperty, "SubBrush");
            hs.Children.Add(chevron);
            hs.Children.Add(label);
            header.Content = hs;
            header.Click += (_, _) => { _showDone = !_showDone; RenderTasks(); };
            TasksPanel.Children.Add(header);
            if (_showDone) foreach (var t in archived) TasksPanel.Children.Add(BuildTaskRow(t));
        }
        TasksEmpty.Visibility = _tasks.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateIdleLine();
    }

    private FrameworkElement BuildTaskRow(TaskItem t)
    {
        var row = new Grid { MinHeight = 34, Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 2) };
        var plate = new Border { CornerRadius = new CornerRadius(10) };
        row.Children.Add(plate);
        var grid = new Grid { Margin = new Thickness(8, 4, 6, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // round checkbox
        var check = new Grid { Width = 20, Height = 20, Cursor = Cursors.Hand, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
        var ring = new Ellipse { StrokeThickness = 1.5 };
        ring.SetResourceReference(Shape.StrokeProperty, t.Done ? "AccentBrush" : "SubBrush");
        if (t.Done) ring.SetResourceReference(Shape.FillProperty, "AccentBrush");
        check.Children.Add(ring);
        if (t.Done)
        {
            var tick = new TextBlock { Text = "", Style = (Style)FindResource("Glyph"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center };
            tick.SetResourceReference(TextBlock.ForegroundProperty, "AccentFgBrush");
            check.Children.Add(tick);
        }
        check.MouseLeftButtonUp += (_, e) =>
        {
            t.Done = !t.Done;
            t.DoneAt = t.Done ? DateTime.Now : null;
            _tasks.Save(t);
            RenderTasks();
            e.Handled = true;
        };
        grid.Children.Add(check);

        var title = new TextBlock { Text = t.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
        title.SetResourceReference(TextBlock.ForegroundProperty, t.Done ? "FaintBrush" : "FgBrush");
        if (t.Done) title.TextDecorations = TextDecorations.Strikethrough;
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        if (t.RemindAt is { } at)
        {
            var when = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var bell = new TextBlock { Text = "", Style = (Style)FindResource("Glyph"), FontSize = 11, Margin = new Thickness(0, 0, 4, 0) };
            bool overdue = !t.Done && at < DateTime.Now;
            bell.SetResourceReference(TextBlock.ForegroundProperty, overdue ? "AccentBrush" : "FaintBrush");
            var time = new TextBlock
            {
                Text = (at.Date == DateTime.Today ? "" : Capital(at.ToString(Loc.T("fmt.date.short"), Ru).Replace(".", "")) + " ") + Loc.Clock(at, _settings.Clock24),
                FontSize = 11,
            };
            time.SetResourceReference(TextBlock.ForegroundProperty, overdue ? "AccentBrush" : "SubBrush");
            when.Children.Add(bell);
            when.Children.Add(time);
            Grid.SetColumn(when, 2);
            grid.Children.Add(when);
        }

        var remove = new Button { Content = "", Width = 24, Height = 24, FontSize = 9, Opacity = 0, Margin = new Thickness(4, 0, 0, 0), ToolTip = Loc.T("tasks.delete") };
        remove.SetResourceReference(StyleProperty, "IconButton");
        remove.SetResourceReference(ForegroundProperty, "SubBrush");
        remove.Click += (_, _) => { _tasks.Delete(t.Id); RenderTasks(); };
        Grid.SetColumn(remove, 3);
        grid.Children.Add(remove);
        row.Children.Add(grid);

        row.MouseEnter += (_, _) => { plate.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush"); remove.Opacity = 1; };
        row.MouseLeave += (_, _) => { plate.Background = null; remove.Opacity = 0; };
        return row;
    }

    private void AddTask_Click(object sender, RoutedEventArgs e) => AddTask();

    private void TaskInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AddTask(); e.Handled = true; }
        else if (e.Key == Key.Escape) { DisableKeyboard(); e.Handled = true; }
    }

    private void AddTask()
    {
        string title = TaskTitle.Text.Trim();
        if (title.Length == 0) { Shake(TaskTitle); FocusField(TaskTitle); return; }
        DateTime? at = null;
        if (TaskTime.Text.Trim().Length > 0)
        {
            if (!TryParseTime(TaskTime.Text, out var time)) { Shake(TaskTime); FocusField(TaskTime); return; }
            var when = DateTime.Today.Add(time);
            if (when < DateTime.Now) when = when.AddDays(1);   // a time that has passed today means tomorrow
            at = when;
        }
        _tasks.Save(new TaskItem { Title = title, RemindAt = at });
        TaskTitle.Text = "";
        TaskTime.Text = "";
        RenderTasks();
        FocusField(TaskTitle);
    }

    private void TasksSecondTick(DateTime now)
    {
        foreach (var t in _tasks.Items)
        {
            if (t.Done || t.RemindAt is not { } at) continue;
            if (now < at || now > at.AddSeconds(90)) continue;
            if (!_taskFired.Add(t.Id + "@" + at.Ticks)) continue;
            Log.Write($"task reminder: {t.Title}");
            if (!_settings.Reminders) continue;
            ShowNotice(new Notice
            {
                Kind = "task",
                Title = t.Title,
                Sub = Loc.T("tasks.remind"),
                Glyph = "",
                Right = () => Loc.Clock(at, _settings.Clock24),
                Width = 380,
                Duration = TimeSpan.FromSeconds(_settings.ReminderSeconds),
                HoverOpens = true,
                Click = () => { SetCalMode("tasks"); SelectTab("calendar", animate: false); },
            });
            RenderTasks();
        }
        if (now.Second == 0 && _calMode == "tasks" && _expanded) RenderTasks();
    }

    /// <summary>
    /// The idle capsule's second line: the next event or task today (coloured dot, "14:30 · Созвон"),
    /// otherwise the weather ("Облачно · +16°").
    /// </summary>
    private void UpdateIdleLine()
    {
        var now = DateTime.Now;
        (DateTime At, string Title, Color Dot, bool AllDay)? next = null;
        if (_settings.ShowNextEvent)
        {
            var ev = _calendar.On(now.Date).Where(e => e.AllDay || e.Start >= now.AddMinutes(-5)).OrderBy(e => e.AllDay).ThenBy(e => e.Start).FirstOrDefault();
            if (ev != null) next = (ev.Start, ev.Title, Hex(ev.Color), ev.AllDay);
            var task = _tasks.Items.Where(t => !t.Done && t.RemindAt is { } a && a.Date == now.Date && a >= now.AddMinutes(-5)).OrderBy(t => t.RemindAt).FirstOrDefault();
            if (task != null && (next == null || next.Value.AllDay || task.RemindAt!.Value < next.Value.At))
                next = (task.RemindAt!.Value, task.Title, _theme.Accent, false);
        }
        if (next is { } n)
        {
            IdleEventDot.Fill = new SolidColorBrush(n.Dot);
            IdleEventText.Text = (n.AllDay ? Loc.T("cal.allday") : Loc.Clock(n.At, _settings.Clock24)) + " · " + n.Title;
            IdleEvent.Visibility = Visibility.Visible;
            IdleWeather.Visibility = Visibility.Collapsed;
        }
        else
        {
            IdleEvent.Visibility = Visibility.Collapsed;
            IdleWeather.Visibility = _idleWeatherReady && _settings.WeatherInCapsule ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
