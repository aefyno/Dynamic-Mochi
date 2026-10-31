using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicMochi.Services;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicMochi;

/// <summary>
/// Notifications in the capsule: Telegram / Discord (Windows notification listener) and screenshots
/// (clipboard after Win+Shift+S / PrtScn, and new files in Pictures\Screenshots).
/// </summary>
public partial class IslandWindow
{
    // ================= Screenshots

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private FileSystemWatcher? _shotWatcher;
    private DateTime _lastShot = DateTime.MinValue;
    private bool _clipboardHooked;

    private void InitScreenshots()
    {
        if (_hwnd != IntPtr.Zero && !_clipboardHooked) _clipboardHooked = AddClipboardFormatListener(_hwnd);
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            if (Directory.Exists(folder))
            {
                _shotWatcher = new FileSystemWatcher(folder, "*.png") { EnableRaisingEvents = true, NotifyFilter = NotifyFilters.FileName };
                _shotWatcher.Created += (_, e) => Dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(400);   // let the file finish writing
                    ShowScreenshot(LoadFile(e.FullPath), e.FullPath);
                });
            }
        }
        catch (Exception ex) { Log.Write("screenshots folder watch failed: " + ex.Message); }
    }

    /// <summary>WM_CLIPBOARDUPDATE: an image without HTML / files = a screen snip (browsers copy images with HTML).</summary>
    private void OnClipboardUpdate()
    {
        if (!_settings.Notifications || !_settings.Screenshots) return;
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(150);
            try
            {
                if (!Clipboard.ContainsImage() || Clipboard.ContainsData(DataFormats.Html) || Clipboard.ContainsFileDropList()) return;
                var img = Clipboard.GetImage();
                if (img == null) return;
                img.Freeze();
                ShowScreenshot(img, null);
            }
            catch (Exception ex) { Log.Write("clipboard read failed: " + ex.Message); }
        }, DispatcherPriority.Background);
    }

    private static BitmapSource? LoadFile(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelHeight = 96;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private void ShowScreenshot(BitmapSource? img, string? file)
    {
        if (img == null || !_settings.Notifications || !_settings.Screenshots || DndActive) return;
        // Win+PrtScn copies and saves: one notice for both
        if (DateTime.Now - _lastShot < TimeSpan.FromSeconds(2)) return;
        _lastShot = DateTime.Now;
        double aspect = img.PixelHeight > 0 ? img.PixelWidth / (double)img.PixelHeight : 1.6;
        double h = 44, w = Math.Clamp(h * aspect, 30, 96);
        ShowNotice(new Notice
        {
            Kind = "screenshot",
            Title = Loc.T("shot.title"),
            Sub = Loc.T("shot.sub"),
            Picture = img,
            RoundPicture = false,
            PictureWidth = w,
            PictureHeight = h,
            Width = 360 + Math.Max(0, w - 44),
            Duration = TimeSpan.FromSeconds(4),
            Click = () => OpenScreenshot(img, file),
        });
    }

    /// <summary>Open in Snipping Tool (falls back to the default image editor).</summary>
    private static void OpenScreenshot(BitmapSource img, string? file)
    {
        try
        {
            if (file == null)
            {
                file = Path.Combine(Path.GetTempPath(), $"DynamicMochi-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                using var fs = File.Create(file);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(Clipboard.ContainsImage() ? Clipboard.GetImage() : img));
                enc.Save(fs);
            }
            try { Process.Start(new ProcessStartInfo("SnippingTool.exe", $"\"{file}\"") { UseShellExecute = true }); }
            catch
            {
                try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, Verb = "edit" }); }
                catch { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
            }
        }
        catch (Exception ex) { Log.Write("open screenshot failed: " + ex.Message); }
    }

    // ================= Telegram / Discord (UserNotificationListener)

    /// <summary>Why the listener does not work, for the settings window ("" when it works).</summary>
    public string NotificationStatus { get; private set; } = "";
    private UserNotificationListener? _listener;
    private readonly HashSet<uint> _seenToasts = new();
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _toastsPrimed;

    private static readonly (string Match, string Name, string Badge, Color Color)[] ChatApps =
    {
        ("telegram", "Telegram", "T", Color.FromRgb(0x22, 0x9E, 0xD9)),
        ("discord", "Discord", "D", Color.FromRgb(0x58, 0x65, 0xF2)),
    };

    /// <summary>
    /// Needs package identity (MSIX / sparse package). Without it Windows refuses access; the island keeps
    /// working and the settings window explains what is missing. Never asks for permission by itself:
    /// <see cref="RequestNotificationAccessAsync"/> runs from the button in the settings window.
    /// </summary>
    private async Task InitNotificationsAsync()
    {
        _toastTimer.Tick += async (_, _) => await PollToastsAsync();
        try
        {
            _listener = UserNotificationListener.Current;
            var status = _listener.GetAccessStatus();
            NotificationStatus = status == UserNotificationListenerAccessStatus.Allowed ? "" : status == UserNotificationListenerAccessStatus.Denied ? "denied" : "ask";
        }
        catch (Exception ex)
        {
            _listener = null;
            NotificationStatus = "identity";
            Log.Write("notification listener unavailable (no package identity): " + ex.Message);
        }
        ApplyNotificationSettings();
        await Task.CompletedTask;
    }

    public async Task<string> RequestNotificationAccessAsync()
    {
        if (_listener == null) return NotificationStatus = "identity";
        try
        {
            var status = await _listener.RequestAccessAsync();
            NotificationStatus = status == UserNotificationListenerAccessStatus.Allowed ? "" : "denied";
            Log.Write("notification access: " + status);
        }
        catch (Exception ex)
        {
            NotificationStatus = "identity";
            Log.Write("notification access request failed: " + ex.Message);
        }
        ApplyNotificationSettings();
        return NotificationStatus;
    }

    private void ApplyNotificationSettings()
    {
        bool on = _settings.Notifications && (_settings.NotifyTelegram || _settings.NotifyDiscord) && _listener != null && NotificationStatus == "";
        if (on && !_toastTimer.IsEnabled) { _toastsPrimed = false; _toastTimer.Start(); }
        else if (!on) _toastTimer.Stop();
    }

    private async Task PollToastsAsync()
    {
        if (_listener == null) return;
        IReadOnlyList<UserNotification> list;
        try { list = await _listener.GetNotificationsAsync(NotificationKinds.Toast); }
        catch (Exception ex) { Log.Write("notifications read failed: " + ex.Message); return; }

        foreach (var n in list)
        {
            if (!_seenToasts.Add(n.Id) || !_toastsPrimed) continue;   // the first pass only remembers what is already there
            string app = "";
            try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? ""; } catch { }
            var chat = ChatApps.FirstOrDefault(c => app.Contains(c.Match, StringComparison.OrdinalIgnoreCase));
            if (chat.Name == null) continue;
            if (chat.Name == "Telegram" && !_settings.NotifyTelegram) continue;
            if (chat.Name == "Discord" && !_settings.NotifyDiscord) continue;
            if (DndActive) continue;

            var texts = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements()?.Select(t => t.Text).ToList() ?? new List<string>();
            string sender = texts.Count > 0 ? texts[0] : chat.Name;
            string body = texts.Count > 1 ? string.Join(" ", texts.Skip(1)) : "";
            ImageSource? logo = await AppLogoAsync(n);
            string appId = "";
            try { appId = n.AppInfo?.AppUserModelId ?? ""; } catch { }
            ShowNotice(new Notice
            {
                Kind = "chat",
                Title = sender,
                Sub = body.Length > 90 ? body[..90] + "…" : body,
                Picture = logo ?? Initials(sender, chat.Color),
                BadgeText = chat.Badge,
                BadgeColor = chat.Color,
                Width = 440,
                Duration = TimeSpan.FromSeconds(5),
                Click = () => OpenApp(chat.Name, appId),
            });
        }
        if (_seenToasts.Count > 500) _seenToasts.IntersectWith(list.Select(x => x.Id));
        _toastsPrimed = true;
    }

    private static async Task<ImageSource?> AppLogoAsync(UserNotification n)
    {
        try
        {
            var reference = n.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64));
            using var stream = await reference.OpenReadAsync();
            using var net = stream.AsStreamForRead();
            using var ms = new MemoryStream();
            await net.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>Round avatar with the sender's first letter when there is no picture.</summary>
    private static ImageSource Initials(string name, Color color)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawEllipse(new SolidColorBrush(color), null, new Point(32, 32), 32, 32);
            string letter = name.Trim().Length > 0 ? name.Trim()[0].ToString().ToUpperInvariant() : "?";
            var ft = new FormattedText(letter, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"), 30, Brushes.White, 1.0);
            dc.DrawText(ft, new Point(32 - ft.Width / 2, 32 - ft.Height / 2));
        }
        var rtb = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static void OpenApp(string name, string appId)
    {
        try
        {
            if (appId.Length > 0) Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + appId) { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(name.ToLowerInvariant() + ":") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("open app failed: " + ex.Message); }
    }

    /// <summary>"Не беспокоить" arrives in v1.5; until then nothing is muted.</summary>
    private bool DndActive => _settings.DoNotDisturb;   // v1.6: quick button on Home
}
