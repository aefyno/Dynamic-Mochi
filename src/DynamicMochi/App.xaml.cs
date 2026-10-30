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

        ApplyFont(Settings.Font);
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

    protected override void OnExit(ExitEventArgs e)
    {
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
        menu.Items.Add(Item("Все настройки", "", () => ShowSettings()));
        var quit = Item("Выйти", "", Quit);
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

    public static void ApplyFont(string name)
    {
        FontFamily ui, display;
        switch (name)
        {
            case "Segoe UI Variable":
                ui = new FontFamily("Segoe UI Variable Text, Segoe UI");
                display = new FontFamily("Segoe UI Variable Display, Segoe UI");
                break;
            case "Manrope":
            case "Onest":
                ui = new FontFamily(FontBase, $"./Assets/Fonts/#{name}, Segoe UI");
                display = ui;
                break;
            default:
                ui = new FontFamily(FontBase, "./Assets/Fonts/#Inter, Segoe UI");
                display = new FontFamily(FontBase, "./Assets/Fonts/#Inter Display, ./Assets/Fonts/#Inter, Segoe UI");
                break;
        }
        Current.Resources["UiFont"] = ui;
        Current.Resources["DisplayFont"] = display;
    }

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
        var window = t.IsLight ? Color.FromRgb(0xF3, 0xF3, 0xF5) : t.IsGradient ? Blend(t.GradMid, Colors.Black, 0.5) : Color.FromRgb(0x10, 0x10, 0x12);
        SetBrush("WindowBrush", window, animate);
    }

    private static void SetBrush(string key, Color to, bool animate)
    {
        Color from = Current.Resources[key] is SolidColorBrush old ? old.Color : to;
        var b = new SolidColorBrush(animate ? from : to);
        if (animate && from != to)
            b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(from, to, TimeSpan.FromMilliseconds(250 * Math.Max(0.01, Settings.AnimationSpeed))));
        Current.Resources[key] = b;
    }

    private static Color Blend(Color a, Color b, double k) => Color.FromRgb(
        (byte)(a.R * (1 - k) + b.R * k), (byte)(a.G * (1 - k) + b.G * k), (byte)(a.B * (1 - k) + b.B * k));
}
