using System.Windows.Media;

namespace DynamicMochi.Services;

/// <summary>
/// Theme tokens. Every theme sets all of them; adding a theme is one entry in <see cref="Themes.All"/>.
/// Coloured themes flow from <see cref="GradTop"/> through <see cref="GradMid"/> into pure black.
/// </summary>
public sealed record Theme(
    string Name,
    Color GradTop,          // capsule background: start of the gradient (whole fill for a solid theme)
    Color GradMid,          // gradient middle (35 % when expanded)
    Color GradEnd,          // gradient end: #000000 for coloured themes, same as GradTop for solid ones
    Color Surface,          // cards and pills inside the capsule
    Color SurfaceHover,
    Color Fg,               // primary text
    Color Sub,              // secondary text
    Color Faint,            // tertiary text: timecodes, captions
    Color Accent,           // active elements, progress, enabled toggle, today
    Color AccentFg,         // icon / text on top of Accent
    Color Border,           // capsule outline (transparent allowed)
    Color Glow,             // outer glow colour; alpha = opacity (transparent = plain shadow)
    double ShadowOpacity,   // black drop shadow when there is no glow
    Color ToggleOff,        // disabled toggle track, neutral
    Color Equalizer,
    bool ArtworkTint,       // paint a blob of the artwork colour on the background
    double TintOpacity,
    bool IsLight,
    bool Vertical = false)  // v1.6: black at the top flowing into a colour at the bottom (Gradient Black)
{
    public bool IsGradient => GradTop != GradEnd;
    public bool HasGlow => Glow.A > 0;
    /// <summary>Gradient Black: the bottom colour can follow the artwork (Settings.GradientFromArt).</summary>
    public bool FollowsArt => Name == Themes.GradientName;
}

public static class Themes
{
    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static Color A(string hex, double alpha)
    {
        var c = C(hex);
        c.A = (byte)Math.Round(255 * alpha);
        return c;
    }

    private static readonly Color Black = C("#000000");

    /// <summary>Dark coloured theme: colour flows into black, soft glow of the same colour.</summary>
    private static Theme Colored(string name, string top, string mid, string accent, string accentFg, double glow) => new(
        name,
        GradTop: C(top), GradMid: C(mid), GradEnd: Black,
        Surface: A("#FFFFFF", 0.08), SurfaceHover: A("#FFFFFF", 0.12),
        Fg: C("#FFFFFF"), Sub: A("#FFFFFF", 0.62), Faint: A("#FFFFFF", 0.38),
        Accent: C(accent), AccentFg: C(accentFg),
        Border: A(accent, 0.55), Glow: A(accent, glow), ShadowOpacity: 0.45,
        ToggleOff: C("#3A3A3C"), Equalizer: C(accent),
        ArtworkTint: false, TintOpacity: 0, IsLight: false);

    /// <summary>Order = card grid, 4 columns: White, OLED, Gradient, Purple / Red, Blue, Green, Orange.</summary>
    public static readonly IReadOnlyList<Theme> All = new List<Theme>
    {
        new("Белая",
            GradTop: C("#F5F5F7"), GradMid: C("#F5F5F7"), GradEnd: C("#F5F5F7"),
            Surface: C("#FFFFFF"), SurfaceHover: C("#EBEBF0"),
            Fg: C("#111114"), Sub: A("#111114", 0.60), Faint: A("#111114", 0.40),
            Accent: C("#0A84FF"), AccentFg: C("#FFFFFF"),
            Border: A("#000000", 0.08), Glow: Colors.Transparent, ShadowOpacity: 0.25,
            ToggleOff: C("#D1D1D6"), Equalizer: C("#0A84FF"),
            ArtworkTint: true, TintOpacity: 0.18, IsLight: true),

        // v1.6: OLED Black is total black — no gradient, no glow, no artwork blob (SPEC-v1.6 §2)
        new("OLED",
            GradTop: Black, GradMid: Black, GradEnd: Black,
            Surface: C("#121214"), SurfaceHover: C("#1E1E22"),
            Fg: C("#FFFFFF"), Sub: A("#FFFFFF", 0.62), Faint: A("#FFFFFF", 0.38),
            Accent: C("#FFFFFF"), AccentFg: C("#000000"),
            Border: Colors.Transparent, Glow: Colors.Transparent, ShadowOpacity: 0.45,
            ToggleOff: C("#3A3A3C"), Equalizer: C("#30D158"),
            ArtworkTint: false, TintOpacity: 0, IsLight: false),

        // v1.6: Gradient Black — pitch black on top, a restrained colour at the bottom
        // (the artwork colour or a fixed purple, Settings.GradientFromArt)
        new(GradientName,
            GradTop: Black, GradMid: Black, GradEnd: C("#2B1050"),
            Surface: A("#FFFFFF", 0.07), SurfaceHover: A("#FFFFFF", 0.11),
            Fg: C("#FFFFFF"), Sub: A("#FFFFFF", 0.62), Faint: A("#FFFFFF", 0.38),
            Accent: C("#FFFFFF"), AccentFg: C("#000000"),
            Border: A("#FFFFFF", 0.10), Glow: A("#8E5BFF", 0.30), ShadowOpacity: 0.45,
            ToggleOff: C("#3A3A3C"), Equalizer: C("#C9A6FF"),
            ArtworkTint: false, TintOpacity: 0, IsLight: false, Vertical: true),

        // v1.4: brighter, the colour reads at first sight (SPEC-v1.4 §15); v1.6: + purple
        Colored("Фиолетовая", "#5B1A9E", "#26093F", "#B45CFF", "#FFFFFF", 0.5),
        Colored("Красная", "#8E0F1E", "#3B0710", "#FF3B4A", "#FFFFFF", 0.5),
        Colored("Синяя", "#0F3FA6", "#0A1A46", "#3D8BFF", "#FFFFFF", 0.5),
        Colored("Зелёная", "#0E7036", "#062A18", "#32D74B", "#000000", 0.5),
        Colored("Оранжевая", "#9C4806", "#3E1C03", "#FF9F0A", "#000000", 0.5),
    };

    public const string GradientName = "Градиент";

    public const string Default = "OLED";

    /// <summary>Unknown or removed theme names (e.g. "Чёрная", "Красно-чёрная") silently fall back to OLED.</summary>
    public static Theme Find(string? name) =>
        All.FirstOrDefault(t => t.Name == name) ?? All.First(t => t.Name == Default);

    /// <summary>Localised theme name (the stored key stays Russian for compatibility).</summary>
    public static string Title(Theme t) => Loc.T("theme." + t.Name switch
    {
        "Белая" => "white", "OLED" => "oled", GradientName => "gradient", "Фиолетовая" => "purple",
        "Красная" => "red", "Синяя" => "blue", "Зелёная" => "green", _ => "orange",
    });
}
