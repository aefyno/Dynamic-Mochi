using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicMochi.Services;

namespace DynamicMochi.Controls;

/// <summary>Thin scroll bars (style ThinScroll): show while scrolling, hide 900 ms later.</summary>
public static class ThinScrollBars
{
    public static void Flash(ScrollViewer sv, Dictionary<ScrollViewer, DispatcherTimer> timers)
    {
        if (sv.Template?.FindName("PART_VerticalScrollBar", sv) is not ScrollBar bar) return;
        bar.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        if (!timers.TryGetValue(sv, out var timer))
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                bar.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
            };
            timers[sv] = timer;
        }
        timer.Stop();
        timer.Start();
    }

    /// <summary>
    /// Mouse wheel handled by hand and clamped to [0, end]: at the end of a list nothing happens any more
    /// (no jump to the top, the wheel never leaks to the capsule). Sliders inside keep their own wheel.
    /// </summary>
    public static void Wheel(ScrollViewer sv, MouseWheelEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null && d != sv; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is Slider2) return;
        e.Handled = true;
        if (sv.ScrollableHeight <= 0) return;
        double to = Math.Clamp(sv.VerticalOffset - e.Delta * 0.5, 0, sv.ScrollableHeight);
        if (Math.Abs(to - sv.VerticalOffset) > 0.1) sv.ScrollToVerticalOffset(to);
    }
}

/// <summary>Theme preview cards (island quick settings and the settings window).</summary>
public static class ThemeCards
{
    public static (Button Card, Border Check) Create(Theme theme, double width, double height, double miniWidth, Action onClick)
    {
        double miniHeight = Math.Round(miniWidth * 0.31);
        var bg = theme.Vertical
            ? new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) }
            : new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        bg.GradientStops.Add(new GradientStop(theme.GradTop, 0));
        bg.GradientStops.Add(new GradientStop(theme.GradMid, theme.Vertical ? 0.3 : 0.55));
        bg.GradientStops.Add(new GradientStop(theme.GradEnd, 1));
        var mini = new Border
        {
            Width = miniWidth, Height = miniHeight, CornerRadius = new CornerRadius(miniHeight / 2),
            Background = bg,
            BorderBrush = new SolidColorBrush(theme.Border.A > 0 ? theme.Border : (theme.IsLight ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(40, 255, 255, 255))),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, height > 80 ? 18 : 10, 0, height > 80 ? 10 : 6),
            Effect = new DropShadowEffect
            {
                BlurRadius = theme.HasGlow ? 10 : 6, ShadowDepth = theme.HasGlow ? 0 : 1,
                Color = theme.HasGlow ? Color.FromRgb(theme.Glow.R, theme.Glow.G, theme.Glow.B) : Colors.Black,
                Opacity = theme.HasGlow ? Math.Min(1, theme.Glow.A / 255.0 * 2) : 0.3,
            },
        };
        double dot = Math.Max(6, miniHeight * 0.4);
        var inner = new Grid();
        inner.Children.Add(new Ellipse { Width = dot, Height = dot, Fill = new SolidColorBrush(theme.Accent), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(miniHeight * 0.3, 0, 0, 0) });
        var bars = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, miniHeight * 0.35, 0) };
        foreach (var h in new[] { 0.25, 0.45, 0.3 })
            bars.Children.Add(new Rectangle { Width = 2, Height = Math.Round(miniHeight * h * 1.6), RadiusX = 1, RadiusY = 1, Margin = new Thickness(1, 0, 1, 0), Fill = new SolidColorBrush(theme.Equalizer), VerticalAlignment = VerticalAlignment.Center });
        inner.Children.Add(bars);
        mini.Child = inner;

        var label = new TextBlock { Text = Themes.Title(theme), FontSize = height > 80 ? 13 : 12, HorizontalAlignment = HorizontalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");

        var check = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 5, 0), Visibility = Visibility.Collapsed,
        };
        check.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        var tick = new TextBlock { Text = "", FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        tick.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
        tick.SetResourceReference(TextBlock.ForegroundProperty, "AccentFgBrush");
        check.Child = tick;

        var column = new StackPanel();
        column.Children.Add(mini);
        column.Children.Add(label);
        var content = new Grid();
        content.Children.Add(column);
        content.Children.Add(check);

        var card = new Button { Content = content, Tag = theme.Name, Width = width, Height = height };
        card.SetResourceReference(FrameworkElement.StyleProperty, "ThemeCard");
        card.Click += (_, _) => onClick();
        return (card, check);
    }

    public static void SetSelected(Button card, Border check, bool selected)
    {
        check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        if (selected) card.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
        else card.BorderBrush = Brushes.Transparent;
    }
}

/// <summary>Small reusable controls for the settings window, built from code in the shared theme.</summary>
public sealed class Toggle : Border
{
    private readonly Ellipse _knob = new() { Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0) };
    private readonly TranslateTransform _shift = new();
    private bool _on;

    public event Action<bool>? Toggled;

    public Toggle(bool on)
    {
        Width = 40; Height = 22; CornerRadius = new CornerRadius(11);
        Cursor = Cursors.Hand;
        _knob.RenderTransform = _shift;
        Child = _knob;
        Set(on, animate: false);
        MouseLeftButtonUp += (_, e) => { Set(!_on, animate: true); Toggled?.Invoke(_on); e.Handled = true; };
    }

    public bool IsOn => _on;

    public void Set(bool on, bool animate)
    {
        _on = on;
        _shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(on ? 18 : 0, TimeSpan.FromMilliseconds(animate ? 160 * App.Settings.AnimationSpeed : 0))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });
        SetResourceReference(BackgroundProperty, on ? "AccentBrush" : "ToggleOffBrush");
        if (on) _knob.SetResourceReference(Shape.FillProperty, "AccentFgBrush");
        else _knob.Fill = Brushes.White;
    }
}

/// <summary>Segmented control: a few options, the selected one is an accent plate.</summary>
public sealed class Segmented : Border
{
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };
    private readonly List<Button> _buttons = new();
    private int _index;

    public event Action<int>? Changed;

    public Segmented(IReadOnlyList<string> options, int selected)
    {
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(3);
        SetResourceReference(BackgroundProperty, "SurfaceHoverBrush");
        Child = _row;
        for (int i = 0; i < options.Count; i++)
        {
            int index = i;
            var b = new Button { Content = options[i], Height = 28, Margin = new Thickness(0), Padding = new Thickness(12, 0, 12, 0) };
            b.SetResourceReference(StyleProperty, "Chip");
            b.Click += (_, _) => { Select(index); Changed?.Invoke(index); };
            _buttons.Add(b);
            _row.Children.Add(b);
        }
        Select(selected);
    }

    public void Select(int index)
    {
        _index = Math.Clamp(index, 0, _buttons.Count - 1);
        for (int i = 0; i < _buttons.Count; i++)
        {
            bool on = i == _index;
            if (on) _buttons[i].SetResourceReference(BackgroundProperty, "AccentBrush");
            else _buttons[i].Background = Brushes.Transparent;
            _buttons[i].SetResourceReference(Control.ForegroundProperty, on ? "AccentFgBrush" : "FgBrush");
        }
    }
}

/// <summary>Slider with a 6 px inner inset so the 12 px thumb never gets cut at 0 % or 100 %.</summary>
public sealed class Slider2 : Grid
{
    private readonly Grid _inner = new() { Margin = new Thickness(6, 0, 6, 0) };
    private readonly Border _fill = new() { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _thumb = new() { Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly double _min, _max, _step;
    private double _value;
    private bool _drag;

    public event Action<double>? ValueChanged;

    public Slider2(double min, double max, double value, double step)
    {
        _min = min; _max = max; _step = step;
        Height = 24; Width = 200;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.35 };
        track.SetResourceReference(Border.BackgroundProperty, "FaintBrush");
        _fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        _thumb.SetResourceReference(Shape.FillProperty, "FgBrush");
        _inner.Children.Add(track);
        _inner.Children.Add(_fill);
        _inner.Children.Add(_thumb);
        Children.Add(_inner);
        _value = value;
        SizeChanged += (_, _) => Render();
        MouseLeftButtonDown += (_, e) => { _drag = true; CaptureMouse(); Pick(e.GetPosition(_inner).X); e.Handled = true; };
        MouseMove += (_, e) => { if (_drag) Pick(e.GetPosition(_inner).X); };
        MouseLeftButtonUp += (_, _) => { _drag = false; ReleaseMouseCapture(); };
        MouseWheel += (_, e) => { SetSliderValue(_value + Math.Sign(e.Delta) * _step, notify: true); e.Handled = true; };
    }

    public double Value => _value;

    private void Pick(double x)
    {
        double w = Math.Max(1, _inner.ActualWidth);
        SetSliderValue(_min + Math.Clamp(x / w, 0, 1) * (_max - _min), notify: true);
    }

    public void SetSliderValue(double v, bool notify)
    {
        v = Math.Round(Math.Clamp(v, _min, _max) / _step) * _step;
        bool changed = Math.Abs(v - _value) > 1e-9;
        _value = v;
        Render();
        if (notify && changed) ValueChanged?.Invoke(v);
    }

    private void Render()
    {
        double w = _inner.ActualWidth;
        double f = (_value - _min) / Math.Max(1e-9, _max - _min);
        _fill.Width = Math.Max(0, w * f);
        _thumb.Margin = new Thickness(w * f - 7, 0, 0, 0);
    }
}
