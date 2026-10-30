using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicMochi.Controls;
using DynamicMochi.Services;

namespace DynamicMochi;

/// <summary>
/// "Dynamic Mochi — настройки": side menu, search across all settings, rows with a description each.
/// Every change is applied and saved right away.
/// </summary>
public partial class SettingsWindow : Window
{
    private sealed record Row(string Title, string Description, Func<FrameworkElement> Control, bool Wide = false);
    private sealed record Group(string? Title, List<Row> Rows);
    private sealed record Section(string Key, string Title, string Glyph, List<Group> Groups);

    private static Settings S => App.Settings;
    private static readonly CultureInfo Ru = new("ru-RU");

    private string _section = "general";
    private readonly Dictionary<string, Button> _nav = new();
    private readonly Dictionary<ScrollViewer, DispatcherTimer> _scrollHide = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private IntPtr _hwnd;
    private bool _mica;
    private bool _previewUnlocked;
    private bool _resetArmed;
    private bool _capturingHotkey;
    private Button? _hotkeyButton;
    private TextBlock? _hotkeyStatus;

    public SettingsWindow()
    {
        InitializeComponent();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); S.Save(); };

        var b = S.SettingsWindow;
        Width = Math.Max(MinWidth, b.Width);
        Height = Math.Max(MinHeight, b.Height);
        if (!double.IsNaN(b.Left) && !double.IsNaN(b.Top) && OnSomeScreen(b.Left, b.Top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = b.Left;
            Top = b.Top;
        }
        try
        {
            string ico = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "mochi.ico");
            if (File.Exists(ico)) Icon = BitmapFrame.Create(new Uri(ico));
        }
        catch { }

        VersionLabel.Text = "Dynamic Mochi " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "");
        SourceInitialized += (_, _) => { _hwnd = new WindowInteropHelper(this).Handle; ApplyBackdrop(); };
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        _placeDebounce.Tick += async (_, _) => { _placeDebounce.Stop(); await SearchPlacesAsync(); };

        BuildNav();
        ShowSection("general");
    }

    private static bool OnSomeScreen(double left, double top) =>
        left > SystemParameters.VirtualScreenLeft - 50 && top > SystemParameters.VirtualScreenTop - 50 &&
        left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
        top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            S.SettingsWindow.Left = Left;
            S.SettingsWindow.Top = Top;
            S.SettingsWindow.Width = Width;
            S.SettingsWindow.Height = Height;
        }
        S.Save();
        App.Island?.PreviewWeather(null);
    }

    /// <summary>Mica backdrop and a title bar that follows the island theme (falls back to a solid colour).</summary>
    private void ApplyBackdrop()
    {
        if (_hwnd == IntPtr.Zero) return;
        var theme = Themes.Find(S.Theme);
        try
        {
            _mica = Native.ApplyBackdrop(_hwnd, dark: !theme.IsLight);
            if (_mica)
            {
                Background = Brushes.Transparent;
                if (HwndSource.FromHwnd(_hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
            }
            else SetResourceReference(BackgroundProperty, "WindowBrush");
        }
        catch (Exception ex)
        {
            Log.Write("backdrop failed: " + ex.Message);
            SetResourceReference(BackgroundProperty, "WindowBrush");
        }
    }

    /// <summary>Apply to the island and save (debounced while a slider is dragged).</summary>
    private void Changed(bool everything = true)
    {
        if (everything) App.Island?.ApplySettings();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>The island changed something (theme card, lyrics toggle): redraw.</summary>
    public void RefreshFromSettings()
    {
        ApplyBackdrop();
        Render();
    }

    // ================= Navigation

    private static readonly (string Key, string Title, string Glyph)[] NavItems =
    {
        ("general", "Общее", ""),
        ("appearance", "Внешний вид", ""),
        ("music", "Музыка", ""),
        ("weather", "Погода", ""),
        ("calendar", "Календарь", ""),
        ("about", "О программе", ""),
    };

    private void BuildNav()
    {
        NavPanel.Children.Clear();
        _nav.Clear();
        foreach (var (key, title, glyph) in NavItems)
        {
            var grid = new Grid();
            var bar = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Hidden };
            bar.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            grid.Children.Add(bar);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0) };
            var icon = new TextBlock { Text = glyph, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
            icon.SetResourceReference(TextBlock.ForegroundProperty, "SubBrush");
            var text = new TextBlock { Text = title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            row.Children.Add(icon);
            row.Children.Add(text);
            grid.Children.Add(row);
            var b = new Button { Content = grid, Tag = bar };
            b.SetResourceReference(StyleProperty, "NavItem");
            string k = key;
            b.Click += (_, _) => { if (SearchBox.Text.Length > 0) SearchBox.Text = ""; ShowSection(k); };
            NavPanel.Children.Add(b);
            _nav[key] = b;
        }
    }

    public void ShowSection(string key)
    {
        if (NavItems.All(n => n.Key != key)) key = "general";
        _section = key;
        foreach (var (k, b) in _nav)
        {
            bool on = k == key && SearchBox.Text.Trim().Length == 0;
            if (on) b.SetResourceReference(BackgroundProperty, "SurfaceBrush");
            else b.Background = Brushes.Transparent;
            if (b.Tag is Border bar) bar.Visibility = on ? Visibility.Visible : Visibility.Hidden;
        }
        Render();
        ContentScroll.ScrollToTop();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => ShowSection(_section);

    private void ContentScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0) ThinScrollBars.Flash(ContentScroll, _scrollHide);
    }

    // ================= Rendering

    private void Render()
    {
        ContentPanel.Children.Clear();
        _hotkeyButton = null;
        _hotkeyStatus = null;
        var sections = BuildSections();
        string q = SearchBox.Text.Trim();

        if (q.Length == 0)
        {
            var s = sections.First(x => x.Key == _section);
            ContentPanel.Children.Add(Header(s.Title));
            foreach (var g in s.Groups) AddGroup(g.Title, g.Rows);
            return;
        }

        bool any = false;
        foreach (var s in sections)
        {
            var rows = s.Groups.SelectMany(g => g.Rows)
                .Where(r => r.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || r.Description.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
            if (rows.Count == 0) continue;
            any = true;
            AddGroup(s.Title, rows);
        }
        if (!any)
        {
            var none = new TextBlock { Text = "Ничего не нашлось", FontSize = 13, Margin = new Thickness(4, 16, 0, 0) };
            none.SetResourceReference(TextBlock.ForegroundProperty, "SubBrush");
            ContentPanel.Children.Add(none);
        }
    }

    private static TextBlock Header(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 4, 0, 16) };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        return t;
    }

    private void AddGroup(string? title, List<Row> rows)
    {
        if (title != null)
        {
            var h = new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 6, 0, 8) };
            h.SetResourceReference(TextBlock.ForegroundProperty, "SubBrush");
            ContentPanel.Children.Add(h);
        }
        var card = new Border { CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 16) };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        var stack = new StackPanel();
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0)
            {
                var sep = new Border { Height = 1, Opacity = 0.15, Margin = new Thickness(16, 0, 16, 0) };
                sep.SetResourceReference(Border.BackgroundProperty, "FaintBrush");
                stack.Children.Add(sep);
            }
            stack.Children.Add(BuildRow(rows[i]));
        }
        card.Child = stack;
        ContentPanel.Children.Add(card);
    }

    private static FrameworkElement BuildRow(Row r)
    {
        var grid = new Grid { MinHeight = 52, Margin = new Thickness(16, 10, 16, 10) };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (r.Title.Length > 0)
        {
            var t = new TextBlock { Text = r.Title, FontSize = 14, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            text.Children.Add(t);
        }
        if (r.Description.Length > 0)
        {
            var d = new TextBlock { Text = r.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            d.SetResourceReference(TextBlock.ForegroundProperty, "SubBrush");
            text.Children.Add(d);
        }
        var control = r.Control();
        if (r.Wide)
        {
            var stack = new StackPanel();
            if (text.Children.Count > 0) { text.Margin = new Thickness(0, 0, 0, 10); stack.Children.Add(text); }
            stack.Children.Add(control);
            grid.Children.Add(stack);
            return grid;
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        text.Margin = new Thickness(0, 0, 16, 0);
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    // ----- control factories

    private Toggle MakeToggle(bool value, Action<bool> set)
    {
        var t = new Toggle(value);
        t.Toggled += on => { set(on); Changed(); };
        return t;
    }

    private Segmented MakeSegmented(string[] options, int selected, Action<int> set)
    {
        var s = new Segmented(options, selected);
        s.Changed += i => { set(i); Changed(); };
        return s;
    }

    private static TextBlock Small(string text, string brush = "SubBrush")
    {
        var t = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    private FrameworkElement MakeSlider(double min, double max, double value, double step, Func<double, string> label, Action<double> set, bool layoutOnly)
    {
        var slider = new Slider2(min, max, value, step) { Width = 220 };
        var text = Small(label(value));
        text.Width = 48;
        text.TextAlignment = TextAlignment.Right;
        slider.ValueChanged += v =>
        {
            set(v);
            text.Text = label(v);
            if (layoutOnly) { App.Island?.ApplyLayoutOnly(); Changed(everything: false); }
            else Changed();
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(slider);
        row.Children.Add(text);
        return row;
    }

    private static Button MakeButton(string text, string? glyph, Action click, bool accent = false, bool danger = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph != null)
        {
            var g = new TextBlock { Text = glyph, FontSize = 13, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            g.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
            if (danger) g.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
            else g.SetResourceReference(TextBlock.ForegroundProperty, accent ? "AccentFgBrush" : "FgBrush");
            content.Children.Add(g);
        }
        var t = new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        if (danger) t.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
        else t.SetResourceReference(TextBlock.ForegroundProperty, accent ? "AccentFgBrush" : "FgBrush");
        content.Children.Add(t);
        var b = new Button { Content = content, Height = 32 };
        b.SetResourceReference(StyleProperty, accent ? "AccentButton" : "PlateButton");
        if (danger) b.SetResourceReference(BackgroundProperty, "SurfaceHoverBrush");
        b.Click += (_, _) => click();
        return b;
    }

    // ================= Sections

    private List<Section> BuildSections()
    {
        var monitors = SafeMonitors();
        return new List<Section>
        {
            new("general", "Общее", "", new List<Group>
            {
                new(null, new List<Row>
                {
                    new("Размер острова", "Компактный, обычный или крупный; ползунок для точной настройки. Остров меняется сразу.", () =>
                    {
                        var stack = new StackPanel();
                        var presets = new[] { 0.9, 1.0, 1.15 };
                        int sel = Array.FindIndex(presets, p => Math.Abs(p - S.Scale) < 0.001);
                        var seg = new Segmented(new[] { "Компактный", "Обычный", "Крупный" }, sel < 0 ? 1 : sel);
                        stack.Children.Add(seg);
                        var slider = MakeSlider(80, 130, Math.Round(S.Scale * 100), 1, v => $"{v:0}%", v => S.Scale = v / 100, layoutOnly: true);
                        slider.Margin = new Thickness(0, 10, 0, 0);
                        stack.Children.Add(slider);
                        seg.Changed += i => { S.Scale = presets[i]; App.Island?.ApplyLayoutOnly(); Changed(everything: false); Render(); };
                        return stack;
                    }),
                    new("Отступ сверху", "Расстояние от верхнего края экрана до острова.", () =>
                        MakeSlider(0, 24, S.TopOffset, 1, v => $"{v:0} px", v => S.TopOffset = v, layoutOnly: true)),
                    new("Монитор", monitors.Count > 1 ? "На каком экране показывать остров." : "Подключён один монитор.", () =>
                    {
                        if (monitors.Count <= 1) return Small("Основной");
                        var names = monitors.Select((m, i) => i == 0 ? "Основной" : $"Монитор {i + 1}").ToArray();
                        return MakeSegmented(names, Math.Clamp(S.Monitor, 0, names.Length - 1), i => S.Monitor = i);
                    }),
                }),
                new("Вид в покое", new List<Row>
                {
                    new("Вид в покое", "Минимальный: только часы или обложка с эквалайзером; подробности при наведении. Подробный: время, дата, погода и название трека всегда.", () =>
                        MakeSegmented(new[] { "Минимальный", "Подробный" }, S.MinimalIdle ? 0 : 1, i => S.MinimalIdle = i == 0)),
                    new("Часы при музыке", "В минимальном виде показывать время между обложкой и эквалайзером.", () =>
                        MakeToggle(S.ClockWithMusic, v => S.ClockWithMusic = v)),
                    new("Прятать в полноэкранных приложениях", "Игры, видео на весь экран, презентации: остров уезжает вверх и возвращается сам.", () =>
                        MakeToggle(S.HideInFullscreen, v => S.HideInFullscreen = v)),
                }),
                new("Система", new List<Row>
                {
                    new("Запускать вместе с Windows", "Остров появится сразу после входа в систему.", () =>
                        MakeToggle(AutoStart.IsEnabled, v => AutoStart.Set(v))),
                    new("Горячая клавиша", "Раскрыть или свернуть остров с клавиатуры. Нажми на сочетание, чтобы задать своё.", HotkeyControl),
                    new("Язык", "Пока интерфейс только на русском, английский появится позже.", () =>
                        MakeSegmented(new[] { "Как в Windows", "Русский", "English" },
                            S.Language switch { "ru" => 1, "en" => 2, _ => 0 }, i => S.Language = i switch { 1 => "ru", 2 => "en", _ => "system" })),
                }),
            }),

            new("appearance", "Внешний вид", "", new List<Group>
            {
                new("Тема", new List<Row> { new("", "", ThemeGrid, Wide: true) }),
                new(null, new List<Row>
                {
                    new("Шрифт", "Шрифт всего острова. Меняется сразу.", () =>
                        MakeSegmented(Settings.Fonts, Math.Max(0, Array.IndexOf(Settings.Fonts, S.Font)), i =>
                        {
                            S.Font = Settings.Fonts[i];
                            App.ApplyFont(S.Font);
                        })),
                    new("Цветное пятно от обложки", "Мягкий отсвет цвета обложки в углу острова (в темах OLED и Белая).", () =>
                        MakeToggle(S.ArtworkTint, v => S.ArtworkTint = v)),
                    new("Анимации", "Скорость всех анимаций. «Без анимаций» также выключает живой фон погоды.", () =>
                        MakeSegmented(new[] { "Обычные", "Быстрые", "Без анимаций" },
                            S.AnimationSpeed >= 1 ? 0 : S.AnimationSpeed > 0 ? 1 : 2, i => S.AnimationSpeed = i switch { 0 => 1, 1 => 0.6, _ => 0 })),
                }),
            }),

            new("music", "Музыка", "", new List<Group>
            {
                new(null, new List<Row>
                {
                    new("Текст песни под островом", "Строка текста с lrclib.net под капсулой, пока играет музыка.", () =>
                        MakeToggle(S.ShowLyrics, v => S.ShowLyrics = v)),
                    new("Размер текста песни", "Размер шрифта строки под островом.", () =>
                        MakeSlider(13, 18, S.LyricsSize, 1, v => $"{v:0} px", v => S.LyricsSize = v, layoutOnly: false)),
                    new("Показывать эквалайзер", "Прыгающие полоски рядом с треком.", () =>
                        MakeToggle(S.ShowEqualizer, v => S.ShowEqualizer = v)),
                    new("Громкость", "«Только плеера» меняет громкость того приложения, где играет музыка. «Системная» меняет общую громкость Windows.", () =>
                        MakeSegmented(new[] { "Только плеера", "Системная" }, S.SystemVolume ? 1 : 0, i => S.SystemVolume = i == 1)),
                    new("Управление кликом", "Клик по свёрнутому острову ставит на паузу, двойной клик включает следующий трек. Колёсико меняет громкость всегда.", () =>
                        MakeToggle(S.ClickControl, v => S.ClickControl = v)),
                }),
            }),

            new("weather", "Погода", "", new List<Group>
            {
                new("Места", new List<Row> { new("", "", PlacesControl, Wide: true) }),
                new(null, new List<Row>
                {
                    new("Температура", "Единицы температуры.", () =>
                        MakeSegmented(new[] { "°C", "°F" }, S.Fahrenheit ? 1 : 0, i => S.Fahrenheit = i == 1)),
                    new("Ветер", "Единицы скорости ветра.", () =>
                        MakeSegmented(new[] { "м/с", "км/ч" }, S.WindKmh ? 1 : 0, i => S.WindKmh = i == 1)),
                    new("Погода в свёрнутом острове", "Иконка и температура рядом с часами в подробном виде.", () =>
                        MakeToggle(S.WeatherInCapsule, v => S.WeatherInCapsule = v)),
                }),
            }),

            new("calendar", "Календарь", "", new List<Group>
            {
                new(null, new List<Row>
                {
                    new("Неделя начинается с", "Первый день недели в сетке месяца.", () =>
                        MakeSegmented(new[] { "Пн", "Вс" }, S.WeekStartsMonday ? 0 : 1, i => S.WeekStartsMonday = i == 0)),
                    new("Ближайшее событие на Главной", "Под часами: «14:30 · Созвон».", () =>
                        MakeToggle(S.ShowNextEvent, v => S.ShowNextEvent = v)),
                    new("Напоминания в острове", "В момент напоминания остров показывает событие.", () =>
                        MakeToggle(S.Reminders, v => S.Reminders = v)),
                    new("Длительность напоминания", "Сколько секунд событие держится в острове.", () =>
                        MakeSegmented(new[] { "5 с", "8 с", "15 с" }, S.ReminderSeconds switch { 5 => 0, 15 => 2, _ => 1 },
                            i => S.ReminderSeconds = i switch { 0 => 5, 2 => 15, _ => 8 })),
                }),
            }),

            new("about", "О программе", "", AboutGroups()),
        };
    }

    private List<Group> AboutGroups()
    {
        var rows = new List<Row>
        {
            new("Версия", "Dynamic Mochi " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? ""), () => Small("")),
            new("GitHub", "Исходный код, сборки и баг-репорты.", () =>
                MakeButton("Открыть", "", () => Open("https://github.com/aefyno/Dynamic-Mochi"))),
            new("Папка с логами", "log.txt, настройки, события календаря и кеш погоды.", () =>
                MakeButton("Открыть папку", "", () => { Directory.CreateDirectory(Log.Folder); Open(Log.Folder); })),
            new("Сбросить все настройки", "Вернуть всё как после установки. События календаря и сохранённые места тоже сбросятся.", () =>
            {
                Button? b = null;
                b = MakeButton(_resetArmed ? "Точно сбросить?" : "Сбросить", "", () =>
                {
                    if (!_resetArmed) { _resetArmed = true; Render(); return; }
                    _resetArmed = false;
                    ResetAll();
                }, danger: true);
                return b;
            }),
        };
        var groups = new List<Group> { new(null, rows) };
        if (_previewUnlocked)
        {
            groups.Add(new("Для тестов", new List<Row>
            {
                new("Предпросмотр погоды", "Показать живой фон без реальной погоды. Открой вкладку Погода в острове.", () =>
                {
                    var names = new[] { "Выкл", "Ясно", "Ночь", "Облака", "Туман", "Морось", "Дождь", "Снег", "Гроза" };
                    var seg = new Segmented(names, 0);
                    seg.Changed += i => App.Island?.PreviewWeather(i == 0 ? null : (WeatherFx.Fx)(i switch
                    {
                        1 => (int)WeatherFx.Fx.Sun, 2 => (int)WeatherFx.Fx.Stars, 3 => (int)WeatherFx.Fx.Clouds, 4 => (int)WeatherFx.Fx.Fog,
                        5 => (int)WeatherFx.Fx.Drizzle, 6 => (int)WeatherFx.Fx.Rain, 7 => (int)WeatherFx.Fx.Snow, _ => (int)WeatherFx.Fx.Thunder,
                    }));
                    return seg;
                }, Wide: true),
            }));
        }
        return groups;
    }

    private static List<Screens.Monitor> SafeMonitors()
    {
        try { return Screens.All(); }
        catch { return new List<Screens.Monitor>(); }
    }

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("open failed: " + ex.Message); }
    }

    private void ResetAll()
    {
        var fresh = new Settings();
        fresh.SettingsWindow = S.SettingsWindow;
        fresh.FirstRunDone = true;
        S.CopyFrom(fresh);
        S.Save();
        App.ApplyFont(S.Font);
        App.Island?.ApplySettings();
        App.Island?.PlacesChanged();
        ApplyBackdrop();
        Render();
    }

    private void Version_Click(object sender, MouseButtonEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;
        _previewUnlocked = !_previewUnlocked;
        ShowSection("about");
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => App.Quit();

    // ----- themes

    private FrameworkElement ThemeGrid()
    {
        var panel = new WrapPanel();
        var cards = new List<(Button, Border, Theme)>();
        foreach (var theme in Themes.All)
        {
            var t = theme;
            var (card, check) = ThemeCards.Create(theme, width: 180, height: 96, miniWidth: 96, () =>
            {
                S.Theme = t.Name;
                Changed();
                foreach (var (c, k, th) in cards) ThemeCards.SetSelected(c, k, th.Name == S.Theme);
                ApplyBackdrop();
            });
            card.Margin = new Thickness(0, 0, 10, 10);
            cards.Add((card, check, theme));
            panel.Children.Add(card);
        }
        foreach (var (c, k, th) in cards) ThemeCards.SetSelected(c, k, th.Name == S.Theme);
        return panel;
    }

    // ----- hotkey

    private FrameworkElement HotkeyControl()
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        _hotkeyStatus = Small("", "SubBrush");
        _hotkeyStatus.Margin = new Thickness(0, 0, 10, 0);
        _hotkeyButton = MakeButton(Native.HotkeyLabel(S.HotkeyModifiers, S.HotkeyKey), null, () =>
        {
            _capturingHotkey = true;
            SetHotkeyLabel("Нажми сочетание…");
        });
        var toggle = MakeToggle(S.HotkeyEnabled, v => S.HotkeyEnabled = v);
        toggle.Margin = new Thickness(10, 0, 0, 0);
        stack.Children.Add(_hotkeyStatus);
        stack.Children.Add(_hotkeyButton);
        stack.Children.Add(toggle);
        UpdateHotkeyStatus();
        return stack;
    }

    private void SetHotkeyLabel(string text)
    {
        if (_hotkeyButton?.Content is StackPanel sp && sp.Children.OfType<TextBlock>().LastOrDefault() is { } t) t.Text = text;
    }

    private void UpdateHotkeyStatus()
    {
        if (_hotkeyStatus == null) return;
        bool taken = S.HotkeyEnabled && App.Island != null && !App.Island.HotkeyRegistered;
        _hotkeyStatus.Text = taken ? "занято другой программой" : "";
        _hotkeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DialogLayer.Visibility == Visibility.Visible && e.Key == Key.Escape) { ClosePlaceDialog(); e.Handled = true; return; }
        if (!_capturingHotkey) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }
        e.Handled = true;
        _capturingHotkey = false;
        if (key == Key.Escape) { SetHotkeyLabel(Native.HotkeyLabel(S.HotkeyModifiers, S.HotkeyKey)); return; }
        int mods = 0;
        var m = Keyboard.Modifiers;
        if ((m & ModifierKeys.Control) != 0) mods |= Native.MOD_CONTROL;
        if ((m & ModifierKeys.Alt) != 0) mods |= Native.MOD_ALT;
        if ((m & ModifierKeys.Shift) != 0) mods |= Native.MOD_SHIFT;
        if ((m & ModifierKeys.Windows) != 0 || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= Native.MOD_WIN;
        if (mods == 0) { SetHotkeyLabel("Нужен Ctrl, Alt или Win"); _capturingHotkey = true; return; }
        S.HotkeyModifiers = mods;
        S.HotkeyKey = KeyInterop.VirtualKeyFromKey(key);
        S.HotkeyEnabled = true;
        App.Island?.RegisterHotkey();
        S.Save();
        SetHotkeyLabel(Native.HotkeyLabel(S.HotkeyModifiers, S.HotkeyKey));
        UpdateHotkeyStatus();
    }

    // ================= Places

    private FrameworkElement PlacesControl()
    {
        var stack = new StackPanel();
        for (int i = 0; i < S.Places.Count; i++)
        {
            int index = i;
            var p = S.Places[i];
            bool current = i == S.PlaceIndex;
            var row = new Grid { Height = 40, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var plate = new Border { CornerRadius = new CornerRadius(10) };
            row.Children.Add(plate);
            var check = new TextBlock { Text = "", FontSize = 13, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = current ? Visibility.Visible : Visibility.Hidden };
            check.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
            check.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            row.Children.Add(check);
            var name = new TextBlock { Text = p.Name, FontSize = 14, Margin = new Thickness(38, 0, 44, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            if (current) name.FontWeight = FontWeights.SemiBold;
            row.Children.Add(name);
            var remove = new Button { Content = "", Width = 28, Height = 28, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 6, 0), Opacity = 0 };
            remove.SetResourceReference(StyleProperty, "IconButton");
            remove.SetResourceReference(ForegroundProperty, "SubBrush");
            remove.Click += (_, e) => { e.Handled = true; RemovePlace(index); };
            row.Children.Add(remove);
            row.MouseEnter += (_, _) => { plate.SetResourceReference(Border.BackgroundProperty, "SurfaceHoverBrush"); remove.Opacity = 1; };
            row.MouseLeave += (_, _) => { plate.Background = null; remove.Opacity = 0; };
            row.MouseLeftButtonUp += (_, _) =>
            {
                S.PlaceIndex = index;
                S.Save();
                App.Island?.PlacesChanged();
                Render();
            };
            stack.Children.Add(row);
        }
        if (S.Places.Count == 0)
        {
            var empty = Small("Пока ни одного места. Добавь город, и погода появится в острове.");
            empty.Margin = new Thickness(4, 0, 0, 8);
            stack.Children.Add(empty);
        }
        bool full = S.Places.Count >= Settings.MaxPlaces;
        var add = MakeButton(full ? "Можно сохранить до 3 мест" : "Добавить место", "", OpenPlaceDialog);
        add.IsEnabled = !full;
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(add);
        return stack;
    }

    private void RemovePlace(int index)
    {
        if (index < 0 || index >= S.Places.Count) return;
        S.Places.RemoveAt(index);
        if (S.PlaceIndex >= S.Places.Count) S.PlaceIndex = Math.Max(0, S.Places.Count - 1);
        S.Save();
        App.Island?.PlacesChanged();
        Render();
    }

    private void AddPlace(Place p)
    {
        if (S.Places.Count >= Settings.MaxPlaces) return;
        S.Places.Add(p);
        S.PlaceIndex = S.Places.Count - 1;
        S.Save();
        App.Island?.PlacesChanged();
        if (_section == "weather") Render();
    }

    // ----- add place dialog: city (Open-Meteo) + district/street (Nominatim), auto-detect

    private readonly DispatcherTimer _placeDebounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private CancellationTokenSource? _placeCts;
    private List<PlaceHit> _placeHits = new();
    private List<bool> _hitIsCity = new();
    private int _placeIndex = -1;
    private Place? _pendingCity;          // step 2: the chosen city, waiting for an optional district
    private PlaceHit? _pendingDistrict;
    private bool _suppressPlaceSearch;

    public void OpenPlaceDialog()
    {
        ShowSection("weather");
        if (S.Places.Count >= Settings.MaxPlaces) return;
        _pendingCity = null;
        _pendingDistrict = null;
        SetDialogStep(district: false);
        DialogLayer.Visibility = Visibility.Visible;
        DialogLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160 * Math.Max(0.01, S.AnimationSpeed))));
        var pop = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(200 * Math.Max(0.01, S.AnimationSpeed))) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } };
        DialogScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        DialogScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        Activate();
        Dispatcher.InvokeAsync(() => { PlaceBox.Focus(); Keyboard.Focus(PlaceBox); }, DispatcherPriority.Input);
    }

    private void ClosePlaceDialog()
    {
        _placeCts?.Cancel();
        _placeDebounce.Stop();
        SetSpinner(false);
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140 * Math.Max(0.01, S.AnimationSpeed)));
        fade.Completed += (_, _) => DialogLayer.Visibility = Visibility.Collapsed;
        DialogLayer.BeginAnimation(OpacityProperty, fade);
    }

    private void SetDialogStep(bool district)
    {
        _suppressPlaceSearch = true;
        PlaceBox.Text = "";
        _suppressPlaceSearch = false;
        ShowPlaceHits(new List<PlaceHit>(), new List<bool>());
        ShowPlaceHint(null);
        OpenLocationSettings.Visibility = Visibility.Collapsed;
        DialogTitle.Text = district ? "Уточнить район" : "Добавить место";
        DialogSubtitle.Text = district && _pendingCity != null ? $"Необязательно. Погода будет точнее для твоего места в городе {_pendingCity.Name}." : "";
        DialogSubtitle.Visibility = district ? Visibility.Visible : Visibility.Collapsed;
        PlaceBox.Tag = district ? "Район или улица" : "Город, район или улица";
        LocateRow.Visibility = district ? Visibility.Collapsed : Visibility.Visible;
        DistrictButtons.Visibility = district ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DialogDim_Click(object sender, MouseButtonEventArgs e) => ClosePlaceDialog();
    private void DialogClose_Click(object sender, RoutedEventArgs e) => ClosePlaceDialog();

    private void Dialog_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { ClosePlaceDialog(); e.Handled = true; }
    }

    private void PlaceBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressPlaceSearch) return;
        _placeDebounce.Stop();
        _placeDebounce.Start();
    }

    private async Task SearchPlacesAsync()
    {
        string q = PlaceBox.Text.Trim();
        _placeCts?.Cancel();
        if (q.Length < 2) { ShowPlaceHits(new List<PlaceHit>(), new List<bool>()); return; }
        var cts = new CancellationTokenSource();
        _placeCts = cts;
        SetSpinner(true);
        ShowPlaceHint(null);
        try
        {
            var hits = new List<PlaceHit>();
            var isCity = new List<bool>();
            if (_pendingCity != null)
            {
                foreach (var h in await PlaceService.SearchDistrictAsync(q, _pendingCity, cts.Token)) { hits.Add(h); isCity.Add(false); }
            }
            else
            {
                foreach (var h in (await PlaceService.SearchCityAsync(q, cts.Token)).Take(6)) { hits.Add(h); isCity.Add(true); }
                if (cts.IsCancellationRequested) return;
                ShowPlaceHits(hits, isCity);   // cities first, streets and districts follow
                if (q.Length >= 3)
                {
                    try
                    {
                        foreach (var h in (await PlaceService.SearchAnyAsync(q, cts.Token)).Take(4))
                            if (!hits.Any(x => Math.Abs(x.Latitude - h.Latitude) < 0.02 && Math.Abs(x.Longitude - h.Longitude) < 0.02)) { hits.Add(h); isCity.Add(false); }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Log.Write("street search failed: " + ex.Message); }
                }
            }
            if (cts.IsCancellationRequested) return;
            ShowPlaceHits(hits, isCity);
            if (hits.Count == 0) ShowPlaceHint(_pendingCity != null ? "Ничего не нашлось в этом городе" : "Ничего не нашлось");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write("place search failed: " + ex.Message);
            if (!cts.IsCancellationRequested) ShowPlaceHint("Не получилось найти. Проверь интернет.");
        }
        finally
        {
            if (_placeCts == cts) SetSpinner(false);
        }
    }

    private void SetSpinner(bool on)
    {
        PlaceSpinner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        PlaceSpin.BeginAnimation(RotateTransform.AngleProperty, on
            ? new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    private void ShowPlaceHint(string? text)
    {
        PlaceHint.Text = text ?? "";
        PlaceHint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowPlaceHits(List<PlaceHit> hits, List<bool> isCity)
    {
        _placeHits = hits;
        _hitIsCity = isCity;
        _placeIndex = hits.Count > 0 ? 0 : -1;
        PlaceResults.Children.Clear();
        for (int i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            int index = i;
            var stack = new StackPanel { Margin = new Thickness(0, 5, 0, 5) };
            var title = new TextBlock { Text = hit.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            title.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            stack.Children.Add(title);
            if (hit.Subtitle.Length > 0)
            {
                var sub = new TextBlock { Text = hit.Subtitle, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
                sub.SetResourceReference(TextBlock.ForegroundProperty, "SubBrush");
                stack.Children.Add(sub);
            }
            var b = new Button { Content = stack, Padding = new Thickness(10, 0, 10, 0) };
            b.SetResourceReference(StyleProperty, "FlatButton");
            b.Click += (_, _) => ChooseHit(index);
            PlaceResults.Children.Add(b);
        }
        HighlightHit();
    }

    private void HighlightHit()
    {
        for (int i = 0; i < PlaceResults.Children.Count; i++)
        {
            if (PlaceResults.Children[i] is not Button b) continue;
            bool on = i == _placeIndex || (_pendingDistrict != null && _placeHits.Count > i && _placeHits[i] == _pendingDistrict);
            if (on) b.SetResourceReference(BackgroundProperty, "SurfaceHoverBrush");
            else b.Background = Brushes.Transparent;
        }
    }

    private void PlaceBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when _placeHits.Count > 0:
                _placeIndex = Math.Min(_placeHits.Count - 1, _placeIndex + 1); HighlightHit(); e.Handled = true; break;
            case Key.Up when _placeHits.Count > 0:
                _placeIndex = Math.Max(0, _placeIndex - 1); HighlightHit(); e.Handled = true; break;
            case Key.Enter:
                if (_placeIndex >= 0 && _placeIndex < _placeHits.Count) ChooseHit(_placeIndex);
                else if (_pendingCity != null) DistrictDone_Click(sender, e);
                e.Handled = true;
                break;
        }
    }

    private void ChooseHit(int index)
    {
        if (index < 0 || index >= _placeHits.Count) return;
        var hit = _placeHits[index];
        if (_pendingCity == null)
        {
            if (_hitIsCity[index])
            {
                // step 2: optional district
                _pendingCity = new Place { Name = hit.Title, Latitude = hit.Latitude, Longitude = hit.Longitude, Timezone = hit.Timezone, Country = hit.Country };
                _pendingDistrict = null;
                SetDialogStep(district: true);
                Dispatcher.InvokeAsync(() => { PlaceBox.Focus(); Keyboard.Focus(PlaceBox); }, DispatcherPriority.Input);
            }
            else
            {
                // a district or street found directly: save it right away
                string city = hit.Subtitle.Split(',').Select(x => x.Trim()).Skip(1).FirstOrDefault() ?? "";
                AddPlace(new Place
                {
                    Name = city.Length > 0 && city != hit.Title ? $"{hit.Title}, {city}" : hit.Title,
                    Latitude = hit.Latitude, Longitude = hit.Longitude, Timezone = "auto", Country = hit.Country,
                });
                ClosePlaceDialog();
            }
        }
        else
        {
            _pendingDistrict = hit;
            _placeIndex = index;
            HighlightHit();
        }
    }

    private void DistrictSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingCity != null) AddPlace(_pendingCity);
        ClosePlaceDialog();
    }

    private void DistrictDone_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingCity == null) return;
        var place = _pendingCity;
        if (_pendingDistrict is { } d)
        {
            place.Name = $"{d.Title}, {_pendingCity.Name}";
            place.Latitude = d.Latitude;
            place.Longitude = d.Longitude;
        }
        AddPlace(place);
        ClosePlaceDialog();
    }

    private async void Locate_Click(object sender, RoutedEventArgs e)
    {
        LocateRow.IsEnabled = false;
        ShowPlaceHint("Определяю место…");
        OpenLocationSettings.Visibility = Visibility.Collapsed;
        try
        {
            var (result, lat, lon) = await PlaceService.LocateAsync();
            if (result == PlaceService.LocateResult.Denied)
            {
                ShowPlaceHint("Разреши доступ к местоположению в Параметрах Windows (Конфиденциальность → Расположение).");
                OpenLocationSettings.Visibility = Visibility.Visible;
                return;
            }
            if (result != PlaceService.LocateResult.Ok)
            {
                ShowPlaceHint("Не получилось определить место. Найди город вручную.");
                return;
            }
            string name;
            string country = "";
            try { (name, country) = await PlaceService.ReverseAsync(lat, lon, CancellationToken.None); }
            catch (Exception ex)
            {
                Log.Write("reverse geocoding failed: " + ex.Message);
                name = string.Format(CultureInfo.InvariantCulture, "{0:F2}, {1:F2}", lat, lon);
            }
            AddPlace(new Place { Name = name, Latitude = lat, Longitude = lon, Timezone = "auto", Country = country });
            ClosePlaceDialog();
        }
        finally
        {
            LocateRow.IsEnabled = true;
        }
    }

    private void LocationSettings_Click(object sender, RoutedEventArgs e) => Open("ms-settings:privacy-location");
}
