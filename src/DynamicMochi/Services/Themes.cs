using System.Windows.Media;

namespace DynamicMochi.Services;

/// <summary>
/// Theme tokens. Every theme must set all of them; adding a theme is one entry in <see cref="Themes.All"/>.
/// </summary>
public sealed record Theme(
    string Name,
    Color BgTop,            // capsule background, top of the gradient
    Color BgBottom,         // capsule background, bottom of the gradient (same as BgTop for a flat theme)
    Color Surface,          // cards and pills inside the capsule
    Color SurfaceHover,
    Color Fg,               // primary text
    Color Sub,              // secondary text
    Color Faint,            // tertiary text: timecodes, captions
    Color Accent,           // active tab, progress, equalizer, enabled toggle
    Color AccentFg,         // icon / text on top of Accent
    Color Border,           // capsule outline (transparent allowed)
    double BorderThickness,
    Color Glow,             // outer glow (transparent allowed)
    Color ToggleOff,        // disabled toggle track, always a neutral gray
    Color Equalizer,        // collapsed equalizer bars
    bool ArtworkTint,       // paint a blob of the artwork colour on the background
    double TintOpacity,
    bool OutlineCards,      // cards drawn as outlines instead of fills (monochrome)
    bool IsLight);

public static class Themes
{
    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static Color A(string hex, double alpha)
    {
        var c = C(hex);
        c.A = (byte)Math.Round(255 * alpha);
        return c;
    }

    public static readonly IReadOnlyList<Theme> All = new List<Theme>
    {
        new("Чёрная",
            BgTop: C("#000000"), BgBottom: C("#000000"),
            Surface: C("#16161A"), SurfaceHover: C("#22222A"),
            Fg: C("#FFFFFF"), Sub: A("#FFFFFF", 0.60), Faint: A("#FFFFFF", 0.38),
            Accent: C("#FFFFFF"), AccentFg: C("#000000"),
            Border: Colors.Transparent, BorderThickness: 0, Glow: Colors.Transparent,
            ToggleOff: C("#3A3A3C"), Equalizer: C("#30D158"),
            ArtworkTint: true, TintOpacity: 0.28, OutlineCards: false, IsLight: false),

        new("Белая",
            BgTop: C("#F5F5F7"), BgBottom: C("#F5F5F7"),
            Surface: C("#FFFFFF"), SurfaceHover: C("#EBEBF0"),
            Fg: C("#111114"), Sub: A("#111114", 0.60), Faint: A("#111114", 0.40),
            Accent: C("#0A84FF"), AccentFg: C("#FFFFFF"),
            Border: A("#000000", 0.08), BorderThickness: 1, Glow: Colors.Transparent,
            ToggleOff: C("#D1D1D6"), Equalizer: C("#0A84FF"),
            ArtworkTint: true, TintOpacity: 0.18, OutlineCards: false, IsLight: true),

        new("Красно-чёрная",
            BgTop: C("#2A0508"), BgBottom: C("#0A0001"),
            Surface: C("#3A0A0E"), SurfaceHover: C("#4D0F15"),
            Fg: C("#FFFFFF"), Sub: A("#FFD6D9", 0.70), Faint: A("#FFB3B8", 0.45),
            Accent: C("#FF2D3D"), AccentFg: C("#FFFFFF"),
            Border: A("#FF2D3D", 0.45), BorderThickness: 1, Glow: A("#FF2D3D", 0.35),
            ToggleOff: C("#3A3A3C"), Equalizer: C("#FF2D3D"),
            ArtworkTint: true, TintOpacity: 0.22, OutlineCards: false, IsLight: false),

        new("Бело-чёрная",
            BgTop: C("#000000"), BgBottom: C("#000000"),
            Surface: C("#000000"), SurfaceHover: C("#1A1A1A"),
            Fg: C("#FFFFFF"), Sub: A("#FFFFFF", 0.65), Faint: A("#FFFFFF", 0.40),
            Accent: C("#FFFFFF"), AccentFg: C("#000000"),
            Border: C("#FFFFFF"), BorderThickness: 1.5, Glow: Colors.Transparent,
            ToggleOff: C("#3A3A3C"), Equalizer: C("#FFFFFF"),
            ArtworkTint: false, TintOpacity: 0, OutlineCards: true, IsLight: false),
    };

    public static Theme Find(string name) => All.FirstOrDefault(t => t.Name == name) ?? All[0];
}
