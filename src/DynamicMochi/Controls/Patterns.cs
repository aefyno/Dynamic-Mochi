using System.Windows;
using System.Windows.Media;
using DynamicMochi.Services;

namespace DynamicMochi.Controls;

/// <summary>
/// v1.7.4: a subtle repeating pattern for each colour theme (drawn at ~7 % white over the capsule,
/// fading out towards the bottom). White, OLED and Liquid Glass stay plain.
/// </summary>
public static class Patterns
{
    /// <summary>The pattern of a theme, or null for the plain ones.</summary>
    public static string? For(Theme t) => t.Name switch
    {
        "Красная" => "diagonal",
        "Синяя" => "waves",
        "Зелёная" => "plus",
        "Оранжевая" => "dots",
        "Фиолетовая" => "sparkles",
        Themes.GradientName => "rings",
        _ => null,
    };

    public static Brush Make(string id, Color ink)
    {
        var fill = new SolidColorBrush(ink);
        var pen = new Pen(fill, 1.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        double size;
        Drawing drawing;
        switch (id)
        {
            case "diagonal":
                size = 14;
                drawing = new GeometryDrawing(null, pen, Geometry.Parse("M0,14 L14,0 M-3,3 L3,-3 M11,17 L17,11"));
                break;
            case "waves":
                size = 24;
                drawing = new GeometryDrawing(null, pen, Geometry.Parse("M0,8 C4,4 8,4 12,8 C16,12 20,12 24,8 M0,20 C4,16 8,16 12,20 C16,24 20,24 24,20"));
                break;
            case "plus":
                size = 22;
                drawing = new GeometryDrawing(null, pen, Geometry.Parse("M11,7 L11,15 M7,11 L15,11"));
                break;
            case "dots":
                size = 14;
                drawing = new GeometryDrawing(fill, null, new GeometryGroup
                {
                    Children = { new EllipseGeometry(new Point(3.5, 3.5), 1.6, 1.6), new EllipseGeometry(new Point(10.5, 10.5), 1.6, 1.6) },
                });
                break;
            case "sparkles":
                size = 34;
                drawing = new GeometryDrawing(fill, null, Geometry.Parse(
                    "M10,4 Q10,10 16,10 Q10,10 10,16 Q10,10 4,10 Q10,10 10,4 Z " +
                    "M25,21 Q25,24.5 28.5,24.5 Q25,24.5 25,28 Q25,24.5 21.5,24.5 Q25,24.5 25,21 Z " +
                    "M27,6 A1.2,1.2 0 1 1 26.99,6 Z M6,27 A1,1 0 1 1 5.99,27 Z"));
                break;
            default: // rings
                size = 26;
                drawing = new GeometryDrawing(null, pen, Geometry.Parse("M13,4 A9,9 0 1 1 12.99,4 Z"));
                break;
        }
        var b = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            Viewport = new Rect(0, 0, size, size),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, size, size),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        b.Freeze();
        return b;
    }
}
