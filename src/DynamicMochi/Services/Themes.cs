using System.Windows.Media;

namespace DynamicMochi.Services;

/// <summary>Island color theme. To add one, add a line to All.</summary>
public sealed record Theme(string Name, Color Background, Color Foreground, Color Secondary, Color Accent, Color Hover, Color Swatch);

public static class Themes
{
    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static readonly IReadOnlyList<Theme> All = new List<Theme>
    {
        new("Чёрная",       C("#FF000000"), C("#FFFFFFFF"), C("#99FFFFFF"), C("#FFFFFFFF"), C("#22FFFFFF"), C("#FF000000")),
        new("Белая",        C("#FFF7F7F9"), C("#FF111114"), C("#99111114"), C("#FF2F80FF"), C("#14000000"), C("#FFF7F7F9")),
        new("Красно-чёрная", C("#FF0B0B0D"), C("#FFFFFFFF"), C("#99FFFFFF"), C("#FFFF3B47"), C("#26FF3B47"), C("#FFFF3B47")),
        new("Бело-чёрная",  C("#FFFFFFFF"), C("#FF000000"), C("#8C000000"), C("#FF000000"), C("#14000000"), C("#FFFFFFFF")),
    };

    public static Theme Find(string name) => All.FirstOrDefault(t => t.Name == name) ?? All[0];
}
