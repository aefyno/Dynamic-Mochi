using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicMochi.Services;

namespace DynamicMochi;

public partial class App : Application
{
    private Mutex? _single;
    private TrayIcon? _tray;
    private static SettingsWindow? _settingsWindow;

    /// <summary>The one settings instance shared by the island and the settings window.</summary>
    public static Settings Settings { get; } = Settings.Load();

    public static IslandWindow? Island { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // single instance
        _single = new Mutex(true, "DynamicMochi.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);

        Log.Write($"start: Dynamic Mochi {Version}");
        if (Settings.StartWithWindows) AutoStart.Refresh();
        Loc.Apply(Settings.Language);
        ApplyFont(Settings.Font);
        ApplyClockFont(Settings.ClockFont);
        var window = new IslandWindow();
        Island = window;
        MainWindow = window;
        window.Show();

        try { _tray = new TrayIcon(() => ShowSettings(), ShowTrayMenu); }
        catch (Exception ex) { Log.Write("tray icon failed: " + ex); }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("unhandled: " + e.Exception);
        e.Handled = true;
    }

    /// <summary>"1.4.0".</summary>
    public static string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";

    protected override void OnExit(ExitEventArgs e)
    {
        Settings.Save();
        _tray?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }

    // ================= Settings window

    /// <summary>Open (or bring forward) the settings window, optionally on a section and with the "add place" dialog.</summary>
    public static void ShowSettings(string? section = null, bool addPlace = false)
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
        if (section != null) _settingsWindow.ShowSection(section);
        if (addPlace) _settingsWindow.OpenPlaceDialog();
    }

    /// <summary>The island changed a setting (theme card, lyrics toggle, place dots): refresh the open settings window.</summary>
    public static void SettingsChangedFromIsland() => _settingsWindow?.RefreshFromSettings();

    public static void Quit()
    {
        _settingsWindow?.Close();
        if (Island != null) Island.QuitAnimated();
        else Current.Shutdown();
    }

    // ================= Menus (right click on the capsule, tray)

    public static ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item(Loc.T("qs.all"), "\uE713", () => ShowSettings()));
        var quit = Item(Loc.T("common.quit"), "\uE711", Quit);
        quit.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
        menu.Items.Add(quit);
        return menu;
    }

    private static MenuItem Item(string text, string glyph, Action action)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 13 };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
        var item = new MenuItem { Header = text, Icon = icon };
        item.Click += (_, _) => action();
        return item;
    }

    private void ShowTrayMenu()
    {
        var menu = BuildMenu();
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    // ================= Fonts (Assets/Fonts, embedded as resources)

    private static readonly Uri FontBase = new("pack://application:,,,/");

    /// <summary>Interface text font: Manrope (default), Onest, Inter, Golos Text or the system font.</summary>
    public static void ApplyFont(string name) => Current.Resources["UiFont"] = TextFace(name);

    /// <summary>Clock font (clocks, big temperature, timer): Unbounded SemiBold (default), Manrope ExtraBold, Onest Bold, JetBrains Mono, Inter Display.</summary>
    public static void ApplyClockFont(string name)
    {
        var (family, weight) = ClockFace(name);
        Current.Resources["DisplayFont"] = family;
        Current.Resources["DisplayWeight"] = weight;
    }

    public static (FontFamily Family, FontWeight Weight) ClockFace(string name) => name switch
    {
        "Manrope" => (new FontFamily(FontBase, "./Assets/Fonts/#Manrope, Segoe UI"), FontWeights.ExtraBold),
        "Onest" => (new FontFamily(FontBase, "./Assets/Fonts/#Onest, Segoe UI"), FontWeights.Bold),
        "JetBrains Mono" => (new FontFamily(FontBase, "./Assets/Fonts/#JetBrains Mono, Consolas"), FontWeights.Bold),
        "Inter Display" => (new FontFamily(FontBase, "./Assets/Fonts/#Inter Display, ./Assets/Fonts/#Inter, Segoe UI"), FontWeights.SemiBold),
        _ => (new FontFamily(FontBase, "./Assets/Fonts/#Unbounded, Segoe UI"), FontWeights.SemiBold),
    };

    /// <summary>Interface font for an option (also the preview in the settings window).</summary>
    public static FontFamily TextFace(string name) => name switch
    {
        "System" => new FontFamily("Segoe UI Variable Text, Segoe UI"),
        "Inter" or "Onest" or "Golos Text" => new FontFamily(FontBase, $"./Assets/Fonts/#{name}, Segoe UI"),
        _ => new FontFamily(FontBase, "./Assets/Fonts/#Manrope, Segoe UI"),
    };

    // ================= Theme brushes (shared by both windows)

    /// <summary>
    /// Theme brushes are replaced, never mutated: WPF freezes a brush resource once a style or template resolves it,
    /// and recolouring a frozen brush throws. A new brush gets its colour animation started before anyone sees it.
    /// </summary>
    public static void ApplyThemeBrushes(Theme t, bool animate)
    {
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
        // opaque plates: menus and the settings window fallback (no Mica)
        var menu = t.IsLight ? Colors.White : t.IsGradient ? Blend(t.GradMid, Color.FromRgb(0x1C, 0x1C, 0x1E), 0.55) : Color.FromRgb(0x1C, 0x1C, 0x1E);
        SetBrush("MenuBrush", menu, animate);
        // settings window: the same gradient as the island in coloured themes (colour flowing into black)
        if (t.IsGradient) SetWindowGradient(t);
        else SetBrush("WindowBrush", t.IsLight ? Color.FromRgb(0xF3, 0xF3, 0xF5) : Color.FromRgb(0x10, 0x10, 0x12), animate);
    }

    private static Color? _gradientBottom;

    /// <summary>
    /// v1.6.1: the island tells us the Gradient theme's real bottom colour (the song colour), so the settings
    /// window matches it instead of the fixed purple.
    /// </summary>
    public static void SetGradientBottom(Color c)
    {
        if (_gradientBottom == c) return;
        _gradientBottom = c;
        var t = Themes.Find(Settings.Theme);
        if (t.FollowsArt) SetWindowGradient(t);
    }

    private static void SetWindowGradient(Theme t)
    {
        double k = Settings.GlowStrength;
        var g = t.Vertical
            ? new LinearGradientBrush { StartPoint = new System.Windows.Point(0.5, 0), EndPoint = new System.Windows.Point(0.5, 1) }
            : new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 1) };
        g.GradientStops.Add(new GradientStop(Themes.Boost(t.GradTop, k), 0));
        g.GradientStops.Add(new GradientStop(Themes.Boost(t.GradMid, k), t.Vertical ? 0.45 : 0.55));
        g.GradientStops.Add(new GradientStop(t.Vertical ? (t.FollowsArt && _gradientBottom is Color b ? b : Themes.Boost(t.GradEnd, k)) : Colors.Black, 1));
        g.Freeze();
        Current.Resources["WindowBrush"] = g;
    }

    private static void SetBrush(string key, Color to, bool animate)
    {
        Color from = Current.Resources[key] is SolidColorBrush old ? old.Color : to;   // a gradient before: no animation
        var b = new SolidColorBrush(animate ? from : to);
        if (animate && from != to)
            b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(from, to, TimeSpan.FromMilliseconds(250 * Math.Max(0.01, Settings.AnimationSpeed))));
        Current.Resources[key] = b;
    }

    private static Color Blend(Color a, Color b, double k) => Color.FromRgb(
        (byte)(a.R * (1 - k) + b.R * k), (byte)(a.G * (1 - k) + b.G * k), (byte)(a.B * (1 - k) + b.B * k));
}
