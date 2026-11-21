using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicMochi.Services;

/// <summary>
/// v1.7.1 Liquid Glass backdrop: copies the screen area behind the island into a small bitmap
/// (a third of the size, halftone-filtered, which is already a soft first blur pass).
/// The island window is excluded from screen capture, so the copy shows what is behind it, not the island itself.
/// v1.7.0 asked Windows to blur instead (accent policy + window region); Windows ignored the region on this
/// kind of window and painted a big dark rectangle, so that approach is gone.
/// </summary>
public sealed class Glass : IDisposable
{
    private const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const int HALFTONE = 4;
    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dest, int dx, int dy, int dw, int dh, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();

    private IntPtr _dc, _dib, _old, _bits;
    private int _w, _h;

    /// <summary>The latest copy of what is behind the island (Bgr32), or null before the first capture.</summary>
    public WriteableBitmap? Bitmap { get; private set; }

    /// <summary>Hides the island from every screen capture (and so from our own); also from screenshots and recordings.</summary>
    public static bool ExcludeFromCapture(IntPtr hwnd, bool on)
    {
        bool ok = hwnd != IntPtr.Zero && SetWindowDisplayAffinity(hwnd, on ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
        Log.Write($"glass: exclude from capture {(on ? "on" : "off")} {(ok ? "ok" : "failed " + Marshal.GetLastWin32Error())}");
        return ok;
    }

    /// <summary>Copies the screen rectangle (physical pixels) at 1/<paramref name="shrink"/> size.</summary>
    public bool Grab(Int32Rect screen, int shrink)
    {
        if (screen.Width < 4 || screen.Height < 4) return false;
        int w = Math.Max(2, (screen.Width + shrink - 1) / shrink), h = Math.Max(2, (screen.Height + shrink - 1) / shrink);
        try
        {
            if (w != _w || h != _h || _dc == IntPtr.Zero) Allocate(w, h);
            IntPtr src = GetDC(IntPtr.Zero);
            try
            {
                SetStretchBltMode(_dc, HALFTONE);
                SetBrushOrgEx(_dc, 0, 0, IntPtr.Zero);
                if (!StretchBlt(_dc, 0, 0, w, h, src, screen.X, screen.Y, screen.Width, screen.Height, SRCCOPY | CAPTUREBLT)) return false;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, src);
            }
            GdiFlush();
            Bitmap!.WritePixels(new Int32Rect(0, 0, w, h), _bits, w * h * 4, w * 4);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("glass capture: " + ex.Message);
            return false;
        }
    }

    private void Allocate(int w, int h)
    {
        Free();
        var bmi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h,   // negative = top-down rows
            biPlanes = 1, biBitCount = 32,
        };
        _dc = CreateCompatibleDC(IntPtr.Zero);
        _dib = CreateDIBSection(_dc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        _old = SelectObject(_dc, _dib);
        _w = w;
        _h = h;
        Bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
    }

    private void Free()
    {
        if (_dc != IntPtr.Zero)
        {
            SelectObject(_dc, _old);
            DeleteObject(_dib);
            DeleteDC(_dc);
        }
        _dc = _dib = _old = _bits = IntPtr.Zero;
        _w = _h = 0;
    }

    public void Dispose()
    {
        Free();
        Bitmap = null;
    }
}
