using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicMochi.Services;

namespace DynamicMochi;

/// <summary>
/// v1.6: power tab (sleep, lock, shut down, restart, sign out, hibernate) and the Home quick buttons
/// (screenshot, do not disturb, lock, power). Destructive actions ask once more on the same tile (SPEC-v1.6 §4).
/// </summary>
public partial class IslandWindow
{
    private sealed record PowerAction(string Key, string Glyph, bool Confirm, Action Run);

    private readonly List<(Button Tile, TextBlock Label, PowerAction Action)> _powerTiles = new();
    private Button? _armedTile;
    private readonly DispatcherTimer _confirmTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    private PowerAction[] PowerActions => new[]
    {
        new PowerAction("sleep", "", false, () => Suspend(hibernate: false)),
        new PowerAction("lock", "", false, Lock),
        new PowerAction("hibernate", "", false, () => Suspend(hibernate: true)),
        new PowerAction("restart", "", true, () => RunShutdown("/r /t 0")),
        new PowerAction("signout", "", true, () => RunShutdown("/l")),
        new PowerAction("shutdown", "", true, () => RunShutdown("/s /t 0")),
    };

    private void BuildPowerTiles()
    {
        _confirmTimer.Tick -= ConfirmTimer_Tick;
        _confirmTimer.Tick += ConfirmTimer_Tick;
        PowerGrid.Children.Clear();
        _powerTiles.Clear();
        foreach (var action in PowerActions)
        {
            var glyph = new TextBlock { Text = action.Glyph, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center };
            glyph.SetResourceReference(StyleProperty, "Glyph");
            glyph.SetResourceReference(TextBlock.ForegroundProperty, action.Confirm && action.Key == "shutdown" ? "AccentBrush" : "FgBrush");
            var label = new TextBlock { Text = Loc.T("power." + action.Key), FontSize = 13, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(glyph);
            stack.Children.Add(label);
            var tile = new Button { Content = stack, Height = 68 };
            tile.SetResourceReference(StyleProperty, "PowerTile");
            var a = action;
            tile.Click += (_, _) => PowerTile_Click(tile, label, a);
            PowerGrid.Children.Add(tile);
            _powerTiles.Add((tile, label, action));
        }
    }

    private void PowerTile_Click(Button tile, TextBlock label, PowerAction action)
    {
        if (action.Confirm && _armedTile != tile)
        {
            // first click arms the tile: red plate + "click again" for 5 s
            ResetPowerConfirm();
            _armedTile = tile;
            tile.Background = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0x45, 0x3A));
            label.Text = Loc.T("power.confirm");
            label.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
            _confirmTimer.Stop();
            _confirmTimer.Start();
            return;
        }
        ResetPowerConfirm();
        Log.Write($"power: {action.Key}");
        if (_expanded) Collapse();
        // let the island collapse before the screen goes away
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            try { action.Run(); }
            catch (Exception ex) { Log.Write($"power {action.Key} failed: {ex.Message}"); }
        });
    }

    private void ConfirmTimer_Tick(object? sender, EventArgs e) => ResetPowerConfirm();

    private void ResetPowerConfirm()
    {
        _confirmTimer.Stop();
        _armedTile = null;
        foreach (var (tile, label, action) in _powerTiles)
        {
            tile.SetResourceReference(BackgroundProperty, "SurfaceBrush");
            label.Text = Loc.T("power." + action.Key);
            label.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        }
    }

    private static void Lock()
    {
        if (!LockWorkStation()) Log.Write("power lock failed: " + Marshal.GetLastWin32Error());
    }

    private void Suspend(bool hibernate)
    {
        // P/Invoke instead of "rundll32 powrprof.dll,SetSuspendState": rundll32 passes wrong arguments
        // and hibernates instead of sleeping on PCs with hibernation enabled.
        if (!SetSuspendState(hibernate, false, false))
        {
            int err = Marshal.GetLastWin32Error();
            Log.Write($"power {(hibernate ? "hibernate" : "sleep")} failed: {err}");
            if (hibernate)
                ShowNotice(new Notice { Kind = "power", Title = Loc.T("power.nohibernate"), Glyph = "", Duration = TimeSpan.FromSeconds(5) });
        }
    }

    private static void RunShutdown(string args) =>
        Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true });

    // ----- Home quick buttons

    private void QuickScreenshot_Click(object sender, RoutedEventArgs e)
    {
        if (_expanded) Collapse();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            try { Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("screenshot launch failed: " + ex.Message); }
        });
    }

    private void QuickDnd_Click(object sender, RoutedEventArgs e)
    {
        _settings.DoNotDisturb = !_settings.DoNotDisturb;
        _settings.Save();
        RefreshToggles();
        App.SettingsChangedFromIsland();
    }

    private void QuickLock_Click(object sender, RoutedEventArgs e)
    {
        if (_expanded) Collapse();
        Lock();
    }

    private void QuickPower_Click(object sender, RoutedEventArgs e) => SelectTab("power", animate: true);
}
