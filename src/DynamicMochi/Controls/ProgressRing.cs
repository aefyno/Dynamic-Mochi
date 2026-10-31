using System.Windows;
using System.Windows.Media;

namespace DynamicMochi.Controls;

/// <summary>Circular progress: a faint track and an accent arc from 12 o'clock, clockwise.</summary>
public sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(2.5, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0…1.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    /// <summary>Arc colour instead of the theme accent (e.g. green for a pomodoro break).</summary>
    public Color? ArcColor { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        double t = Thickness;
        double r = (size - t) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);

        var faint = TryFindResource("FaintBrush") as Brush ?? Brushes.Gray;
        dc.PushOpacity(0.35);
        dc.DrawEllipse(null, new Pen(faint, t), c, r, r);
        dc.Pop();

        double v = Math.Clamp(Value, 0, 1);
        if (v <= 0.001) return;
        Brush arc = ArcColor is Color col ? new SolidColorBrush(col) : TryFindResource("AccentBrush") as Brush ?? Brushes.White;
        var pen = new Pen(arc, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (v >= 0.999) { dc.DrawEllipse(null, pen, c, r, r); return; }
        double a = v * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(start, false, false);
            g.ArcTo(end, new Size(r, r), 0, v > 0.5, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
