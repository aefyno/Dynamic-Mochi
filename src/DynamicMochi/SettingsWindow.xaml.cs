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
/// Painted in the island theme (gradient, accent). Every change is applied and saved right away.
/// A setting that does not apply in the current mode is greyed out with the reason (SPEC-v1.4 §14).
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>Disabled: returns the reason when the row does not apply right now, otherwise null.</summary>
    private sealed record Row(string Title, string Description, Func<FrameworkElement> Control, bool Wide = false, Func<string?>? Disabled = null);
    private sealed record Group(string? Title, List<Row> Rows);
    private sealed record Section(string Key, string Title, string Glyph, List<Group> Groups);

    private static Settings S => App.Settings;
    private static CultureInfo Ru => Loc.Culture;

    private string _section = "general";
    private readonly Dictionary<string, Button> _nav = new();
    private readonly Dictionary<ScrollViewer, DispatcherTimer> _scrollHide = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
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
        Width = Math.Max(MinWidth, double.IsNaN(b.Width) ? 900 : b.Width);
        Height = Math.Max(MinHeight, double.IsNaN(b.Height) ? 640 : b.Height);
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

        SourceInitialized += (_, _) => { _hwnd = new WindowInteropHelper(this).Handle; ApplyBackdrop(); };
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        ContentScroll.PreviewMouseWheel += (_, e) => ThinScrollBars.Wheel(ContentScroll, e);
        _placeDebounce.Tick += async (_, _) => { _placeDebounce.Stop(); await SearchPlacesAsync(); };
        Loc.Changed += OnLanguage;
        Closed += (_, _) => Loc.Changed -= OnLanguage;

        ApplyTexts();
        BuildNav();
        ShowSection("general");
    }

    private void OnLanguage()
    {
        ApplyTexts();
        BuildNav();
        ShowSection(_section);
    }

    private void ApplyTexts()
    {
        Title = Loc.T("set.window");
        VersionLabel.Text = "Dynamic Mochi " + App.Version;
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
        _saveTimer.Stop();
        S.Save();
        App.Island?.PreviewWeather(null);
    }

    /// <summary>
    /// The window wears the island theme: coloured themes get their gradient (colour flowing into black),
    /// OLED and White keep Mica when available (a gradient over Mica looks muddy).
    /// </summary>
    private void ApplyBackdrop()
    {
        var theme = Themes.Find(S.Theme);
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            _mica = !theme.IsGradient && Native.ApplyBackdrop(_hwnd, dark: !theme.IsLight);
            if (theme.IsGradient) Native.ApplyBackdrop(_hwnd, dark: true, mica: false);
            if (_mica)
            {
                Background = Brushes.Transparent;
                if (HwndSource.FromHwnd(_hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
            }
            else
            {
                if (HwndSource.FromHwnd(_hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Black;
                SetResourceReference(BackgroundProperty, "WindowBrush");
            }
        }
        catch (Exception ex)
        {
            Log.Write("backdrop failed: " + ex.Message);
            SetResourceReference(BackgroundProperty, "WindowBrush");
        }
    }

    /// <summary>Apply to the island and save now (sliders: a short debounce while dragging).</summary>
    private void Changed(bool everything = true, bool debounce = false)
    {
        if (everything) App.Island?.ApplySettings();
        if (debounce) { _saveTimer.Stop(); _saveTimer.Start(); }
        else { _saveTimer.Stop(); S.Save(); }
    }

    /// <summary>The island changed something (theme card, lyrics toggle): redraw.</summary>
    public void RefreshFromSettings()
    {
        ApplyBackdrop();
        Render();
        HighlightNav();
    }

    // ================= Navigation: icons in accent-coloured circles

    private static readonly (string Key, string Title, string Glyph)[] NavItems =
    {
        ("general", "set.general", ""),
        ("appearance", "set.appearance", ""),
        ("music", "set.music", ""),
        ("weather", "set.weather", ""),
        ("calendar", "set.calendar", ""),
        ("notifications", "set.notifications", ""),
        ("about", "set.about", ""),
    };

    private readonly Dictionary<string, (Border Circle, TextBlock Icon)> _navIcons = new();

    private void BuildNav()
    {
        NavPanel.Children.Clear();
        _nav.Clear();
        _navIcons.Clear();
        foreach (var (key, title, glyph) in NavItems)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
            var circle = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 12, 0) };
            var icon = new TextBlock { Text = glyph, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
            circle.Child = icon;
            var text = new TextBlock { Text = Loc.T(title), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            row.Children.Add(circle);
            row.Children.Add(text);
            var b = new Button { Content = row, Height = 42 };
            b.SetResourceReference(StyleProperty, "NavItem");
            string k = key;
            b.Click += (_, _) => { if (SearchBox.Text.Length > 0) SearchBox.Text = ""; ShowSection(k); };
            NavPanel.Children.Add(b);
            _nav[key] = b;
            _navIcons[key] = (circle, icon);
        }
        SearchBox.Tag = Loc.T("set.search");
        SavedHint.Text = Loc.T("set.saved");
        QuitText.Text = Loc.T("set.quit");
    }

    private void HighlightNav()
    {
        foreach (var (k, b) in _nav)
        {
            bool on = k == _section && SearchBox.Text.Trim().Length == 0;
            if (on) b.SetResourceReference(BackgroundProperty, "SurfaceBrush");
            else b.Background = Brushes.Transparent;
            var (circle, icon) = _navIcons[k];
            if (on)
            {
                circle.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentFgBrush");
            }
            else
            {
                var accent = Themes.Find(S.Theme).Accent;
                circle.Background = new SolidColorBrush(Color.FromArgb(46, accent.R, accent.G, accent.B));
                icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            }
        }
    }

    public void ShowSection(string key)
    {
        if (NavItems.All(n => n.Key != key)) key = "general";
        _section = key;
        HighlightNav();
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
            var none = new TextBlock { Text = Loc.T("set.nothing"), FontSize = 13, Margin = new Thickness(4, 16, 0, 0) };
            none.SetResourceReference(TextBlock.ForegroundProperty, "SubBrush");
            ContentPanel.Children.Add(none);
        }
    }

    private static TextBlock Header(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 4, 0, 20) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        return t;
    }

    private void AddGroup(string? title, List<Row> rows)
    {
        if (title != null)
        {
            var h = new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 4, 0, 10) };
            h.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            ContentPanel.Children.Add(h);
        }
        var card = new Border { CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 0, 24) };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        var stack = new StackPanel();
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0)
            {
                var sep = new Border { Height = 1, Opacity = 0.15, Margin = new Thickness(20, 0, 20, 0) };
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
        string? reason = r.Disabled?.Invoke();
        var grid = new Grid { MinHeight = 56, Margin = new Thickness(20, 14, 20, 14) };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (r.Title.Length > 0)
        {
            var t = new TextBlock { Text = r.Title, FontSize = 14, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            text.Children.Add(t);
        }
        if (r.Description.Length > 0)
        {
            var d = new TextBlock { Text = r.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            d.SetResourceReference(TextBlock.ForegroundProperty, "SubBrush");
            text.Children.Add(d);
        }
        if (reason != null)
        {
            var why = new TextBlock { Text = reason, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), FontStyle = FontStyles.Italic };
            why.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            text.Children.Add(why);
        }
        var control = r.Control();
        if (reason != null)
        {
            control.IsEnabled = false;
            control.IsHitTestVisible = false;
            control.Opacity = 0.4;
        }
        if (r.Wide)
        {
            var stack = new StackPanel();
            if (text.Children.Count > 0) { text.Margin = new Thickness(0, 0, 0, 12); stack.Children.Add(text); }
            stack.Children.Add(control);
            grid.Children.Add(stack);
            return grid;
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        text.Margin = new Thickness(0, 0, 20, 0);
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    // ----- control factories

    private Toggle MakeToggle(bool value, Action<bool> set, bool rerender = false)
    {
        var t = new Toggle(value);
        t.Toggled += on => { set(on); Changed(); if (rerender) Render(); };
        return t;
    }

    private Segmented MakeSegmented(string[] options, int selected, Action<int> set, bool rerender = false)
    {
        var s = new Segmented(options, selected);
        s.Changed += i => { set(i); Changed(); if (rerender) Render(); };
        return s;
    }

    private static string[] L(params string[] keys) => keys.Select(Loc.T).ToArray();

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
        text.Width = 52;
        text.TextAlignment = TextAlignment.Right;
        slider.ValueChanged += v =>
        {
            set(v);
            text.Text = label(v);
            if (layoutOnly) { App.Island?.ApplyLayoutOnly(); Changed(everything: false, debounce: true); }
            else Changed(debounce: true);
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
        var b = new Button { Content = content, Height = 34 };
        b.SetResourceReference(StyleProperty, accent ? "AccentButton" : "PlateButton");
        if (danger || !accent) b.SetResourceReference(BackgroundProperty, "SurfaceHoverBrush");
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Choice cards with a preview drawn in the option itself (fonts, shape).</summary>
    private FrameworkElement Choices(IReadOnlyList<(string Key, Func<FrameworkElement> Preview, string Caption)> items, string selected, Action<string> pick, double width = 112, double height = 72)
    {
        var panel = new WrapPanel();
        foreach (var (key, preview, caption) in items)
        {
            bool on = key == selected;
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var p = preview();
            p.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(p);
            var cap = new TextBlock { Text = caption, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            cap.SetResourceReference(TextBlock.ForegroundProperty, on ? "AccentBrush" : "SubBrush");
            stack.Children.Add(cap);
            var b = new Button { Content = stack, Width = width, Height = height, Margin = new Thickness(0, 0, 10, 10), Tag = key };
            b.SetResourceReference(StyleProperty, "ThemeCard");
            b.SetResourceReference(Control.BackgroundProperty, "SurfaceHoverBrush");
            if (on) b.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
            string k = key;
            b.Click += (_, _) => { pick(k); Changed(); Render(); };
            panel.Children.Add(b);
        }
        return panel;
    }

    private static TextBlock FontSample(string text, FontFamily family, FontWeight weight, double size)
    {
        var t = new TextBlock { Text = text, FontFamily = family, FontWeight = weight, FontSize = size };
        Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        return t;
    }

    /// <summary>Mini picture of the island or the notch at the top of a screen.</summary>
    private static FrameworkElement ShapePreview(bool notch)
    {
        var screen = new Grid { Width = 92, Height = 40, ClipToBounds = true };
        var bezel = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1.5) };
        bezel.SetResourceReference(Border.BorderBrushProperty, "FaintBrush");
        screen.Children.Add(bezel);
        var shape = new Border
        {
            Width = 44, Height = 12, Background = Brushes.Black,
            CornerRadius = notch ? new CornerRadius(0, 0, 6, 6) : new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, notch ? 1.5 : 6, 0, 0),
        };
        shape.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        shape.BorderThickness = new Thickness(notch ? 0 : 1);
        screen.Children.Add(shape);
        return screen;
    }

    // ================= Sections

    private List<Section> BuildSections()
    {
        var monitors = SafeMonitors();
        var theme = Themes.Find(S.Theme);
        return new List<Section>
        {
            new("general", Loc.T("set.general"), "", new List<Group>
            {
                new(null, new List<Row>
                {
                    new(Loc.T("set.size"), Loc.T("set.size.d"), () =>
                    {
                        var stack = new StackPanel();
                        var presets = new[] { 0.9, 1.0, 1.15 };
                        int sel = Array.FindIndex(presets, p => Math.Abs(p - S.Scale) < 0.001);
                        var seg = new Segmented(L("set.size.compact", "set.size.normal", "set.size.large"), sel < 0 ? 1 : sel);
                        stack.Children.Add(seg);
                        var slider = MakeSlider(80, 130, Math.Round(S.Scale * 100), 1, v => $"{v:0}%", v => S.Scale = v / 100, layoutOnly: true);
                        slider.Margin = new Thickness(0, 10, 0, 0);
                        stack.Children.Add(slider);
                        seg.Changed += i => { S.Scale = presets[i]; App.Island?.ApplyLayoutOnly(); Changed(everything: false); Render(); };
                        return stack;
                    }),
                    new(Loc.T("set.shape"), Loc.T("set.shape.d"), () => Choices(new (string, Func<FrameworkElement>, string)[]
                    {
                        ("0", () => ShapePreview(false), Loc.T("set.shape.island")),
                        ("1", () => ShapePreview(true), Loc.T("set.shape.notch")),
                    }, S.Shape.ToString(CultureInfo.InvariantCulture), k => S.Shape = k == "1" ? 1 : 0, width: 120, height: 78)),
                    new(Loc.T("set.offset"), Loc.T("set.offset.d"), () =>
                        MakeSlider(0, 24, S.TopOffset, 1, v => $"{v:0} px", v => S.TopOffset = v, layoutOnly: true),
                        Disabled: () => S.Shape == 1 ? Loc.T("set.offset.notch") : null),
                    new(Loc.T("set.monitor"), Loc.T(monitors.Count > 1 ? "set.monitor.d" : "set.monitor.one"), () =>
                    {
                        if (monitors.Count <= 1) return Small(Loc.T("set.monitor.main"));
                        var names = monitors.Select((m, i) => i == 0 ? Loc.T("set.monitor.main") : Loc.F("set.monitor.n", i + 1)).ToArray();
                        return MakeSegmented(names, Math.Clamp(S.Monitor, 0, names.Length - 1), i => S.Monitor = i);
                    }),
                }),
                new(Loc.T("set.idle"), new List<Row>
                {
                    new(Loc.T("set.idle.view"), Loc.T("set.idle.view.d"), () =>
                        MakeSegmented(L("set.idle.detailed", "set.idle.minimal"), S.MinimalIdle ? 1 : 0, i => S.MinimalIdle = i == 1, rerender: true)),
                    new(Loc.T("set.clockmusic"), Loc.T("set.clockmusic.d"), () =>
                        MakeToggle(S.ClockWithMusic, v => S.ClockWithMusic = v)),
                    new(Loc.T("set.greetings"), Loc.T("set.greetings.d"), () =>
                        MakeToggle(S.Greetings, v => S.Greetings = v),
                        Disabled: () => S.MinimalIdle ? Loc.T("set.onlydetailed") : null),
                    new(Loc.T("set.clock24"), Loc.T("set.clock24.d"), () =>
                        MakeSegmented(new[] { "24", "12" }, S.Clock24 ? 0 : 1, i => S.Clock24 = i == 0)),
                    new(Loc.T("set.fullscreen"), Loc.T("set.fullscreen.d"), () =>
                        MakeToggle(S.HideInFullscreen, v => S.HideInFullscreen = v)),
                }),
                new(Loc.T("set.system"), new List<Row>
                {
                    new(Loc.T("set.autostart"), Loc.T("set.autostart.d"), () =>
                        MakeToggle(AutoStart.IsEnabled, v => AutoStart.Set(v))),
                    new(Loc.T("set.hotkey"), Loc.T("set.hotkey.d"), HotkeyControl),
                    new(Loc.T("set.language"), Loc.T("set.language.d"), () =>
                        MakeSegmented(new[] { Loc.T("set.language.system"), "Русский", "English" },
                            S.Language switch { "ru" => 1, "en" => 2, _ => 0 }, i =>
                            {
                                S.Language = i switch { 1 => "ru", 2 => "en", _ => "system" };
                                S.Save();
                                Loc.Apply(S.Language);   // applies at once, no restart
                            })),
                }),
            }),

            new("appearance", Loc.T("set.appearance"), "", new List<Group>
            {
                new(Loc.T("set.theme"), new List<Row> { new("", "", ThemeGrid, Wide: true) }),
                new(Loc.T("set.fonts"), new List<Row>
                {
                    new(Loc.T("set.font"), Loc.T("set.font.d"), () => Choices(
                        Settings.Fonts.Select(f => (f, (Func<FrameworkElement>)(() => FontSample("Аа Bb", App.TextFace(f), FontWeights.SemiBold, 20)),
                            f == "System" ? Loc.T("set.font.system") : f)).ToList(),
                        S.Font, k => { S.Font = k; App.ApplyFont(k); }), Wide: true),
                    new(Loc.T("set.clockfont"), Loc.T("set.clockfont.d"), () => Choices(
                        Settings.ClockFonts.Select(f => (f, (Func<FrameworkElement>)(() =>
                        {
                            var (family, weight) = App.ClockFace(f);
                            return FontSample("12:34", family, weight, 20);
                        }), f)).ToList(),
                        S.ClockFont, k => { S.ClockFont = k; App.ApplyClockFont(k); }), Wide: true),
                }),
                new(null, new List<Row>
                {
                    new(Loc.T("set.tint"), Loc.T("set.tint.d"), () =>
                        MakeToggle(S.ArtworkTint, v => S.ArtworkTint = v),
                        Disabled: () => theme.IsGradient ? Loc.T("set.tint.colored") : null),
                    new(Loc.T("set.anim"), Loc.T("set.anim.d"), () =>
                        MakeSegmented(L("set.anim.normal", "set.anim.fast", "set.anim.off"),
                            S.AnimationSpeed >= 1 ? 0 : S.AnimationSpeed > 0 ? 1 : 2, i => S.AnimationSpeed = i switch { 0 => 1, 1 => 0.6, _ => 0 }, rerender: true)),
                }),
            }),

            new("music", Loc.T("set.music"), "", new List<Group>
            {
                new(Loc.T("set.lyrics"), new List<Row>
                {
                    new(Loc.T("set.lyrics.place"), Loc.T("set.lyrics.place.d"), () =>
                        MakeSegmented(L("set.lyrics.in", "set.lyrics.under", "set.lyrics.none"), S.LyricsPlace, i => S.LyricsPlace = i, rerender: true)),
                    new(Loc.T("set.lyrics.size"), Loc.T("set.lyrics.size.d"), () =>
                        MakeSlider(12, 18, S.LyricsSize, 1, v => $"{v:0} px", v => S.LyricsSize = v, layoutOnly: false),
                        Disabled: () => S.LyricsPlace == 2 ? Loc.T("set.lyrics.off") : null),
                    new(Loc.T("set.lyrics.backing"), Loc.T("set.lyrics.backing.d"), () =>
                        MakeToggle(S.LyricsBacking, v => S.LyricsBacking = v),
                        Disabled: () => S.LyricsPlace == 2 ? Loc.T("set.lyrics.off") : null),
                    new(Loc.T("set.lyrics.artist"), Loc.T("set.lyrics.artist.d"), () =>
                        MakeToggle(S.LyricsArtistFallback, v => S.LyricsArtistFallback = v),
                        Disabled: () => S.LyricsPlace != 0 ? Loc.T("set.lyrics.onlyin") : S.MinimalIdle ? Loc.T("set.onlydetailed") : null),
                }),
                new(null, new List<Row>
                {
                    new(Loc.T("set.eq"), Loc.T("set.eq.d"), () =>
                        MakeToggle(S.ShowEqualizer, v => S.ShowEqualizer = v)),
                    new(Loc.T("set.volume"), Loc.T("set.volume.d"), () =>
                        MakeSegmented(L("set.volume.player", "set.volume.system"), S.SystemVolume ? 1 : 0, i => S.SystemVolume = i == 1)),
                    new(Loc.T("set.click"), Loc.T("set.click.d"), () =>
                        MakeToggle(S.ClickControl, v => S.ClickControl = v)),
                }),
            }),

            new("weather", Loc.T("set.weather"), "", new List<Group>
            {
                new(Loc.T("set.places"), new List<Row> { new("", "", PlacesControl, Wide: true) }),
                new(null, new List<Row>
                {
                    new(Loc.T("set.temp"), Loc.T("set.temp.d"), () =>
                        MakeSegmented(new[] { "°C", "°F" }, S.Fahrenheit ? 1 : 0, i => S.Fahrenheit = i == 1)),
                    new(Loc.T("set.wind"), Loc.T("set.wind.d"), () =>
                        MakeSegmented(L("unit.ms", "unit.kmh", "unit.mph", "unit.kn"), S.WindUnit, i => S.WindUnit = i)),
                    new(Loc.T("set.wcapsule"), Loc.T("set.wcapsule.d"), () =>
                        MakeToggle(S.WeatherInCapsule, v => S.WeatherInCapsule = v),
                        Disabled: () => S.MinimalIdle ? Loc.T("set.onlydetailed") : null),
                    new(Loc.T("set.live"), Loc.T("set.live.d"), () =>
                        MakeSegmented(L("set.live.off", "set.live.calm", "set.live.full"), S.LiveWeather, i => S.LiveWeather = i),
                        Disabled: () => S.AnimationSpeed <= 0 ? Loc.T("set.live.noanim") : null),
                }),
            }),

            new("calendar", Loc.T("set.calendar"), "", new List<Group>
            {
                new(null, new List<Row>
                {
                    new(Loc.T("set.week"), Loc.T("set.week.d"), () =>
                        MakeSegmented(L("set.week.mon", "set.week.sun"), S.WeekStartsMonday ? 0 : 1, i => S.WeekStartsMonday = i == 0)),
                    new(Loc.T("set.nextevent"), Loc.T("set.nextevent.d"), () =>
                        MakeToggle(S.ShowNextEvent, v => S.ShowNextEvent = v)),
                    new(Loc.T("set.reminders"), Loc.T("set.reminders.d"), () =>
                        MakeToggle(S.Reminders, v => S.Reminders = v, rerender: true)),
                    new(Loc.T("set.remlen"), Loc.T("set.remlen.d"), () =>
                        MakeSegmented(new[] { Loc.F("set.sec", 5), Loc.F("set.sec", 8), Loc.F("set.sec", 15) }, S.ReminderSeconds switch { 5 => 0, 15 => 2, _ => 1 },
                            i => S.ReminderSeconds = i switch { 0 => 5, 2 => 15, _ => 8 }),
                        Disabled: () => !S.Reminders ? Loc.T("set.remlen.off") : null),
                }),
            }),

            new("notifications", Loc.T("set.notifications"), "", NotificationGroups()),

            new("about", Loc.T("set.about"), "", AboutGroups()),
        };
    }

    private List<Group> NotificationGroups()
    {
        string status = App.Island?.NotificationStatus ?? "identity";
        string? chatOff = !S.Notifications ? Loc.T("set.notify.off") : null;
        var chats = new List<Row>
        {
            new(Loc.T("set.notify"), Loc.T("set.notify.d"), () => MakeToggle(S.Notifications, v => S.Notifications = v, rerender: true)),
            new("Telegram", Loc.T("set.notify.tg"), () => MakeToggle(S.NotifyTelegram, v => S.NotifyTelegram = v),
                Disabled: () => chatOff ?? (status == "identity" ? Loc.T("set.notify.identity.short") : null)),
            new("Discord", Loc.T("set.notify.dc"), () => MakeToggle(S.NotifyDiscord, v => S.NotifyDiscord = v),
                Disabled: () => chatOff ?? (status == "identity" ? Loc.T("set.notify.identity.short") : null)),
            new(Loc.T("set.notify.access"), status switch
            {
                "" => Loc.T("set.notify.access.ok"),
                "denied" => Loc.T("set.notify.access.denied"),
                "identity" => Loc.T("set.notify.access.identity"),
                _ => Loc.T("set.notify.access.ask"),
            }, () =>
            {
                if (status == "") return Small("✓", "AccentBrush");
                if (status == "identity") return Small("—");
                return MakeButton(Loc.T(status == "denied" ? "set.notify.open" : "set.notify.allow"), "", async () =>
                {
                    if (status == "denied") { Open("ms-settings:privacy-notifications"); return; }
                    if (App.Island != null) await App.Island.RequestNotificationAccessAsync();
                    Render();
                }, accent: true);
            }),
        };
        var shots = new List<Row>
        {
            new(Loc.T("set.shots"), Loc.T("set.shots.d"), () => MakeToggle(S.Screenshots, v => S.Screenshots = v), Disabled: () => chatOff),
        };
        return new List<Group> { new(Loc.T("set.notify.chats"), chats), new(Loc.T("set.shots.group"), shots) };
    }

    private List<Group> AboutGroups()
    {
        var rows = new List<Row>
        {
            new(Loc.T("set.version"), "Dynamic Mochi " + App.Version, () => Small("")),
            new("GitHub", Loc.T("set.github.d"), () =>
                MakeButton(Loc.T("set.open"), "", () => Open("https://github.com/aefyno/Dynamic-Mochi"))),
            new(Loc.T("set.logs"), Loc.T("set.logs.d"), () =>
                MakeButton(Loc.T("set.logs.open"), "", () => { Directory.CreateDirectory(Log.Folder); Open(Log.Folder); })),
            new(Loc.T("set.reset"), Loc.T("set.reset.d"), () =>
                MakeButton(Loc.T(_resetArmed ? "set.reset.sure" : "set.reset.do"), "", () =>
                {
                    if (!_resetArmed) { _resetArmed = true; Render(); return; }
                    _resetArmed = false;
                    ResetAll();
                }, danger: true)),
        };
        var groups = new List<Group> { new(null, rows) };
        if (_previewUnlocked)
        {
            groups.Add(new(Loc.T("set.tests"), new List<Row>
            {
                new(Loc.T("set.wpreview"), Loc.T("set.wpreview.d"), () =>
                {
                    var names = L("set.wp.off", "set.wp.sun", "set.wp.night", "set.wp.clouds", "set.wp.fog", "set.wp.drizzle", "set.wp.rain", "set.wp.snow", "set.wp.storm");
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
        fresh.SettingsVersion = Settings.CurrentVersion;
        S.CopyFrom(fresh);
        S.Save();
        App.ApplyFont(S.Font);
        App.ApplyClockFont(S.ClockFont);
        Loc.Apply(S.Language);
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
                HighlightNav();
                Render();
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
            SetHotkeyLabel(Loc.T("set.hotkey.press"));
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
        _hotkeyStatus.Text = taken ? Loc.T("set.hotkey.taken") : "";
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
        if (mods == 0) { SetHotkeyLabel(Loc.T("set.hotkey.need")); _capturingHotkey = true; return; }
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
            var empty = Small(Loc.T("set.places.empty"));
            empty.Margin = new Thickness(4, 0, 0, 8);
            stack.Children.Add(empty);
        }
        bool full = S.Places.Count >= Settings.MaxPlaces;
        var add = MakeButton(Loc.T(full ? "set.places.full" : "set.places.add"), "", OpenPlaceDialog);
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
        DialogTitle.Text = Loc.T(district ? "dlg.district" : "set.places.add");
        DialogSubtitle.Text = district && _pendingCity != null ? Loc.F("dlg.district.sub", _pendingCity.Name) : "";
        DialogSubtitle.Visibility = district ? Visibility.Visible : Visibility.Collapsed;
        PlaceBox.Tag = Loc.T(district ? "dlg.district.box" : "dlg.city.box");
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
            if (hits.Count == 0) ShowPlaceHint(Loc.T(_pendingCity != null ? "dlg.none.city" : "dlg.none"));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write("place search failed: " + ex.Message);
            if (!cts.IsCancellationRequested) ShowPlaceHint(Loc.T("dlg.neterror"));
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
        ShowPlaceHint(Loc.T("dlg.locating"));
        OpenLocationSettings.Visibility = Visibility.Collapsed;
        try
        {
            var (result, lat, lon) = await PlaceService.LocateAsync();
            if (result == PlaceService.LocateResult.Denied)
            {
                ShowPlaceHint(Loc.T("dlg.loc.denied"));
                OpenLocationSettings.Visibility = Visibility.Visible;
                return;
            }
            if (result != PlaceService.LocateResult.Ok)
            {
                ShowPlaceHint(Loc.T("dlg.loc.failed"));
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
