using System.Runtime.InteropServices;
using System.Windows;

namespace DynamicMochi.Services;

/// <summary>One shape of the island that shows real blur behind it, in device pixels of the window.</summary>
public readonly record struct GlassShape(Rect Bounds, double RadiusX, double RadiusY, bool FlatTop = false, Point[]? Polygon = null);

/// <summary>
/// v1.7 Liquid Glass: Windows blurs whatever is behind the island window (DWM blur-behind through the accent
/// policy), and the window region cuts that blur to the shapes of the capsule and the lyric line.
/// The region is a pixel mask, so its curved edges are not anti-aliased; the glass rim drawn in WPF covers them.
/// </summary>
public static class Glass
{
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_DISABLED = 0, ACCENT_ENABLE_BLURBEHIND = 3;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    private const int RGN_OR = 2, RGN_AND = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);
    [DllImport("gdi32.dll")] private static extern IntPtr CreatePolygonRgn(POINT[] points, int count, int mode);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Turns the blur on or off. Off also removes the region, so the window is whole again.</summary>
    public static bool SetBlur(IntPtr hwnd, bool on)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            if (on)
            {
                // no system border or rounding on top of our own shape
                int round = DWMWCP_DONOTROUND, none = DWMWA_COLOR_NONE;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));
            }
            var policy = new AccentPolicy { AccentState = on ? ACCENT_ENABLE_BLURBEHIND : ACCENT_DISABLED };
            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
                bool ok = SetWindowCompositionAttribute(hwnd, ref data) != 0;
                if (!on) SetWindowRgn(hwnd, IntPtr.Zero, true);
                Log.Write($"glass: blur {(on ? "on" : "off")} {(ok ? "ok" : "refused by Windows")}");
                return ok;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch (Exception ex)
        {
            Log.Write("glass: " + ex.Message);
            return false;
        }
    }

    /// <summary>Cuts the window (and the blur) to these shapes. An empty list hides the window completely.</summary>
    public static void SetRegion(IntPtr hwnd, IReadOnlyList<GlassShape> shapes)
    {
        if (hwnd == IntPtr.Zero) return;
        IntPtr all = CreateRectRgn(0, 0, 0, 0);
        try
        {
            foreach (var s in shapes)
            {
                IntPtr part = Build(s);
                if (part == IntPtr.Zero) continue;
                CombineRgn(all, all, part, RGN_OR);
                DeleteObject(part);
            }
            // on success Windows owns the region and frees it
            if (SetWindowRgn(hwnd, all, true) != 0) all = IntPtr.Zero;
        }
        catch (Exception ex)
        {
            Log.Write("glass region: " + ex.Message);
        }
        finally
        {
            if (all != IntPtr.Zero) DeleteObject(all);
        }
    }

    private static IntPtr Build(GlassShape s)
    {
        if (s.Polygon is { Length: >= 3 } poly)
        {
            var pts = poly.Select(p => new POINT { X = (int)Math.Round(p.X), Y = (int)Math.Round(p.Y) }).ToArray();
            return CreatePolygonRgn(pts, pts.Length, 2 /* WINDING */);
        }
        var b = s.Bounds;
        if (b.IsEmpty || b.Width < 1 || b.Height < 1) return IntPtr.Zero;
        int l = (int)Math.Round(b.Left), t = (int)Math.Round(b.Top), r = (int)Math.Round(b.Right), btm = (int)Math.Round(b.Bottom);
        int ex = Math.Max(0, (int)Math.Round(s.RadiusX * 2)), ey = Math.Max(0, (int)Math.Round(s.RadiusY * 2));
        if (!s.FlatTop) return CreateRoundRectRgn(l, t, r + 1, btm + 1, ex, ey);
        // notch: round only at the bottom — a taller rounded rect, cut flat at the top
        IntPtr round = CreateRoundRectRgn(l, t - ey, r + 1, btm + 1, ex, ey);
        IntPtr box = CreateRectRgn(l, t, r + 1, btm + 1);
        CombineRgn(round, round, box, RGN_AND);
        DeleteObject(box);
        return round;
    }
}
