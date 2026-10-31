using System.Runtime.InteropServices;

namespace DynamicMochi.Services;

/// <summary>Non-activating tool window (hidden from Alt+Tab) that can temporarily take the keyboard.</summary>
public static class Native
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, int modifiers, int vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const int WM_HOTKEY = 0x0312;
    public const int MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;

    /// <summary>"Win+Alt+I" style label for a hotkey.</summary>
    public static string HotkeyLabel(int modifiers, int vk)
    {
        var parts = new List<string>();
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_WIN) != 0) parts.Add("Win");
        if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey(vk);
        string k = key.ToString();
        if (k.Length == 2 && k[0] == 'D' && char.IsDigit(k[1])) k = k[1].ToString();
        parts.Add(k);
        return string.Join("+", parts);
    }

    // ----- Mica / dark title bar (Windows 11)

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Dark or light title bar and the Mica backdrop. Returns false when Mica is not available.</summary>
    public static bool ApplyBackdrop(IntPtr hwnd, bool dark, bool mica = true)
    {
        int d = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref d, sizeof(int));          // DWMWA_USE_IMMERSIVE_DARK_MODE
        if (!IsWindows11) return false;
        // no Mica: the window paints its own (theme gradient) background
        var m = mica ? new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 } : new MARGINS();
        DwmExtendFrameIntoClientArea(hwnd, ref m);
        int type = mica ? 2 : 1;                                       // DWMSBT_MAINWINDOW / DWMSBT_NONE
        return DwmSetWindowAttribute(hwnd, 38, ref type, sizeof(int)) == 0 && mica;   // DWMWA_SYSTEMBACKDROP_TYPE
    }

    public static void MakeToolWindow(IntPtr hwnd)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    /// <summary>Allow or forbid activation (needed only while the user types into a field).</summary>
    public static void SetNoActivate(IntPtr hwnd, bool noActivate)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style = noActivate ? style | WS_EX_NOACTIVATE : style & ~WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }
}
