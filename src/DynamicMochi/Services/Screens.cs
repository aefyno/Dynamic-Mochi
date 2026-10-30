using System.Runtime.InteropServices;
using System.Text;

namespace DynamicMochi.Services;

/// <summary>Monitors (physical pixels), placing the island on one of them, and full-screen detection.</summary>
public static class Screens
{
    public sealed record Monitor(IntPtr Handle, int Left, int Top, int Width, int Height, bool Primary, double Scale);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hMonitor, int type, out uint dpiX, out uint dpiY);

    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    public static List<Monitor> All()
    {
        var list = new List<Monitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref info))
            {
                double scale = 1;
                try { if (GetDpiForMonitor(h, 0, out uint dx, out _) == 0) scale = dx / 96.0; } catch { }
                var r = info.rcMonitor;
                list.Add(new Monitor(h, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, (info.dwFlags & 1) != 0, scale));
            }
            return true;
        }, IntPtr.Zero);
        // primary first, then left to right
        return list.OrderByDescending(m => m.Primary).ThenBy(m => m.Left).ToList();
    }

    /// <summary>Centre the window horizontally at the top edge of the chosen monitor (index 0 = primary).</summary>
    public static Monitor? Place(IntPtr hwnd, int index, double widthDip)
    {
        var all = All();
        if (all.Count == 0) return null;
        var m = all[Math.Clamp(index, 0, all.Count - 1)];
        int width = (int)Math.Round(widthDip * m.Scale);
        int x = m.Left + (m.Width - width) / 2;
        int y = m.Top;
        if (GetWindowRect(hwnd, out var cur) && Math.Abs(cur.Left - x) <= 1 && cur.Top == y) return m;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        return m;
    }

    /// <summary>True when the foreground window covers the whole monitor the island is on (games, video, slides).</summary>
    public static bool FullScreenAppOn(IntPtr island)
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == island || !IsWindowVisible(fg)) return false;
        var cls = new StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        string c = cls.ToString();
        if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;   // desktop / taskbar
        var mon = MonitorFromWindow(island, 2);
        if (MonitorFromWindow(fg, 2) != mon) return false;
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(mon, ref info) || !GetWindowRect(fg, out var r)) return false;
        var m = info.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }
}
