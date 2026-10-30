using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using DynamicMochi.Services;

namespace DynamicMochi.Controls;

/// <summary>
/// Vector weather icons drawn in code (Segoe Fluent has no full weather set).
/// Colour mode: sun / bolt yellow, drops blue, clouds and moon in the text colour.
/// Mono mode: everything in the inherited text colour (used on the tab bar).
/// </summary>
public sealed class WeatherIcon : FrameworkElement
{
    public enum Glyph { Sun, Moon, PartlySun, PartlyMoon, Cloud, Fog, Drizzle, Rain, Snow, Thunder, Wind, Drop, Thermo }

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(Glyph), typeof(WeatherIcon), new FrameworkPropertyMetadata(Glyph.Sun, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MonoProperty = DependencyProperty.Register(
        nameof(Mono), typeof(bool), typeof(WeatherIcon), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(WeatherIcon), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public Glyph Icon { get => (Glyph)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public bool Mono { get => (bool)GetValue(MonoProperty); set => SetValue(MonoProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public WeatherIcon()
    {
        Width = 24; Height = 24;
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
    }

    public static Glyph For(int wmoCode, bool isDay) => WeatherService.Kind(wmoCode) switch
    {
        WeatherKind.Clear => isDay ? Glyph.Sun : Glyph.Moon,
        WeatherKind.PartlyCloudy => isDay ? Glyph.PartlySun : Glyph.PartlyMoon,
        WeatherKind.Fog => Glyph.Fog,
        WeatherKind.Drizzle => Glyph.Drizzle,
        WeatherKind.Rain => Glyph.Rain,
        WeatherKind.Snow => Glyph.Snow,
        WeatherKind.Thunder => Glyph.Thunder,
        _ => Glyph.Cloud,
    };

    private static readonly Brush Yellow = Frozen("#FFD60A");
    private static readonly Brush Blue = Frozen("#64D2FF");

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var fg = Foreground ?? Brushes.White;
        var sun = Mono ? fg : Yellow;
        var water = Mono ? fg : Blue;

        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(new ScaleTransform(size / 24, size / 24));

        switch (Icon)
        {
            case Glyph.Sun:
                DrawSun(dc, sun, 12, 12, 4.6, 7.2, 9.6);
                break;
            case Glyph.Moon:
                dc.DrawGeometry(fg, null, Moon(12, 12, 7.5));
                break;
            case Glyph.PartlySun:
                DrawSun(dc, sun, 8.5, 8.5, 3.3, 5.3, 7.2);
                DrawCloud(dc, fg, 2.5, 2.5, 0.85);
                break;
            case Glyph.PartlyMoon:
                dc.DrawGeometry(fg, null, Moon(8.5, 8.5, 5.2));
                DrawCloud(dc, fg, 2.5, 2.5, 0.85);
                break;
            case Glyph.Cloud:
                DrawCloud(dc, fg, 0, 0, 1);
                break;
            case Glyph.Fog:
                DrawCloud(dc, fg, 0, -3.5, 1);
                var fogPen = Pen(fg, 1.8);
                dc.DrawLine(fogPen, new Point(5, 19), new Point(19, 19));
                dc.DrawLine(fogPen, new Point(7, 22), new Point(17, 22));
                break;
            case Glyph.Drizzle:
                DrawCloud(dc, fg, 0, -3.5, 1);
                var dp = Pen(water, 1.8);
                dc.DrawLine(dp, new Point(10, 18.5), new Point(9.3, 20.5));
                dc.DrawLine(dp, new Point(15, 18.5), new Point(14.3, 20.5));
                break;
            case Glyph.Rain:
                DrawCloud(dc, fg, 0, -3.5, 1);
                var rp = Pen(water, 1.8);
                dc.DrawLine(rp, new Point(8.5, 18.5), new Point(7.3, 22));
                dc.DrawLine(rp, new Point(12.5, 18.5), new Point(11.3, 22));
                dc.DrawLine(rp, new Point(16.5, 18.5), new Point(15.3, 22));
                break;
            case Glyph.Snow:
                DrawCloud(dc, fg, 0, -3.5, 1);
                foreach (var x in new[] { 8.0, 12.0, 16.0 })
                    dc.DrawEllipse(Mono ? fg : fg, null, new Point(x, 20.5), 1.3, 1.3);
                break;
            case Glyph.Thunder:
                DrawCloud(dc, fg, 0, -3.5, 1);
                dc.DrawGeometry(sun, null, Geometry.Parse("M13.2,15.5 L9.5,20.5 L12,20.5 L10.8,24 L15.3,18.6 L12.8,18.6 L14.6,15.5 Z"));
                break;
            case Glyph.Wind:
                var wp = Pen(fg, 1.8);
                dc.DrawGeometry(null, wp, Geometry.Parse("M3,9 H14 A3,3 0 1 0 11,6"));
                dc.DrawGeometry(null, wp, Geometry.Parse("M3,14 H18 A3,3 0 1 1 15,17"));
                dc.DrawGeometry(null, wp, Geometry.Parse("M3,19 H9"));
                break;
            case Glyph.Drop:
                dc.DrawGeometry(water, null, Geometry.Parse("M12,3 C12,3 5.5,10.5 5.5,14.5 A6.5,6.5 0 0 0 18.5,14.5 C18.5,10.5 12,3 12,3 Z"));
                break;
            case Glyph.Thermo:
                var tp = Pen(fg, 1.8);
                dc.DrawGeometry(null, tp, Geometry.Parse("M10,14 V5 A2,2 0 0 1 14,5 V14 A4,4 0 1 1 10,14 Z"));
                dc.DrawEllipse(fg, null, new Point(12, 17), 2, 2);
                break;
        }
        dc.Pop();
        dc.Pop();
    }

    private static Pen Pen(Brush b, double w) => new(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

    private static void DrawSun(DrawingContext dc, Brush b, double cx, double cy, double r, double r1, double r2)
    {
        dc.DrawEllipse(b, null, new Point(cx, cy), r, r);
        var pen = Pen(b, 1.8);
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            dc.DrawLine(pen, new Point(cx + Math.Cos(a) * r1, cy + Math.Sin(a) * r1), new Point(cx + Math.Cos(a) * r2, cy + Math.Sin(a) * r2));
        }
    }

    private static Geometry Moon(double cx, double cy, double r) => new CombinedGeometry(GeometryCombineMode.Exclude,
        new EllipseGeometry(new Point(cx, cy), r, r),
        new EllipseGeometry(new Point(cx + r * 0.55, cy - r * 0.4), r * 0.82, r * 0.82));

    /// <summary>Cloud in a 24-unit box: bottom edge at y 19 before offset.</summary>
    private static void DrawCloud(DrawingContext dc, Brush b, double dx, double dy, double scale)
    {
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(new EllipseGeometry(new Point(7.5, 15), 4.2, 4));
        g.Children.Add(new EllipseGeometry(new Point(12.5, 11.5), 5.5, 5.5));
        g.Children.Add(new EllipseGeometry(new Point(17.3, 14.8), 4.2, 4.2));
        g.Children.Add(new RectangleGeometry(new Rect(7.5, 13, 9.8, 6), 0, 0));
        var t = new TransformGroup();
        t.Children.Add(new ScaleTransform(scale, scale, 12, 12));
        t.Children.Add(new TranslateTransform(dx, dy));
        g.Transform = t;
        dc.DrawGeometry(b, null, g);
    }
}
