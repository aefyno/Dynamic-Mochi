using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DynamicMochi.Services;

/// <summary>Notification-area icon (Shell_NotifyIcon). Left click → onClick, right click → onMenu.</summary>
public sealed class TrayIcon : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr hinst, string name, int type, int cx, int cy, int flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);

    private const int NIM_ADD = 0, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const int WM_APP = 0x8000, CallbackMessage = WM_APP + 77;
    private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;

    private readonly HwndSource _source;
    private readonly IntPtr _icon;
    private readonly Action _onClick;
    private readonly Action _onMenu;
    private readonly int _taskbarCreated;
    private bool _added;

    public TrayIcon(Action onClick, Action onMenu)
    {
        _onClick = onClick;
        _onMenu = onMenu;
        _source = new HwndSource(new HwndSourceParameters("DynamicMochiTray") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(Hook);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "mochi.ico");
        int size = GetSystemMetrics(49);   // SM_CXSMICON
        _icon = File.Exists(path) ? LoadImage(IntPtr.Zero, path, 1, size, size, 0x10) : IntPtr.Zero;
        Add();
    }

    private NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _source.Handle,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = "Dynamic Mochi",
        szInfo = "",
        szInfoTitle = "",
    };

    private void Add()
    {
        var d = Data();
        _added = Shell_NotifyIcon(NIM_ADD, ref d);
        if (!_added) Log.Write("tray icon: add failed");
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            int ev = lParam.ToInt32() & 0xFFFF;
            if (ev == WM_LBUTTONUP) _onClick();
            else if (ev == WM_RBUTTONUP) _onMenu();
            handled = true;
        }
        else if (msg == _taskbarCreated)
        {
            Add();   // explorer restarted
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_added)
        {
            var d = Data();
            Shell_NotifyIcon(NIM_DELETE, ref d);
            _added = false;
        }
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _source.RemoveHook(Hook);
        _source.Dispose();
    }
}
