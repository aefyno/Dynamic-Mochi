using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicMochi.Services;

/// <summary>Dominant colour of an artwork: average of an 8×8 downscale, ignoring near-black and near-white pixels.</summary>
public static class ArtColor
{
    public static Color? Dominant(byte[]? data)
    {
        if (data == null || data.Length == 0) return null;
        try
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(data);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 8;
            bmp.DecodePixelHeight = 8;
            bmp.StreamSource = ms;
            bmp.EndInit();

            var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);

            double r = 0, g = 0, b = 0, weight = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double bb = px[i], gg = px[i + 1], rr = px[i + 2];
                double max = Math.Max(rr, Math.Max(gg, bb)), min = Math.Min(rr, Math.Min(gg, bb));
                if (max < 40 || min > 225) continue;              // near black / near white
                double sat = max > 0 ? (max - min) / max : 0;
                double wgt = 0.2 + sat;                            // saturated pixels count more
                r += rr * wgt; g += gg * wgt; b += bb * wgt; weight += wgt;
            }
            if (weight <= 0) return null;
            var c = Color.FromRgb((byte)(r / weight), (byte)(g / weight), (byte)(b / weight));
            return Boost(c);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Make the colour vivid enough to read as a tint.</summary>
    private static Color Boost(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2;
        double s = max == min ? 0 : (l < 0.5 ? (max - min) / (max + min) : (max - min) / (2 - max - min));
        double hh = 0;
        if (max != min)
        {
            double d = max - min;
            if (max == r) hh = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) hh = (b - r) / d + 2;
            else hh = (r - g) / d + 4;
            hh /= 6;
        }
        s = Math.Clamp(Math.Max(s, 0.55), 0, 0.9);
        l = Math.Clamp(Math.Max(l, 0.5), 0.45, 0.65);
        return FromHsl(hh, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        double R = Hue(p, q, h + 1.0 / 3), G = Hue(p, q, h), B = Hue(p, q, h - 1.0 / 3);
        return Color.FromRgb((byte)(R * 255), (byte)(G * 255), (byte)(B * 255));
    }

    private static double Hue(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}
