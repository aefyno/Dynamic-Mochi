using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicMochi.Controls;

/// <summary>
/// Single-line text that scrolls when it does not fit: pause 1.5 s → slide at 30 px/s → pause 1.5 s → ease back.
/// Edges fade over 12 px while scrolling.
/// </summary>
public sealed class MarqueeText : Grid
{
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TranslateTransform _shift = new();
    private Storyboard? _story;

    public MarqueeText()
    {
        ClipToBounds = true;
        _text.RenderTransform = _shift;
        Children.Add(_text);
        SizeChanged += (_, _) => Restart();
        _text.SizeChanged += (_, _) => Restart();
        Unloaded += (_, _) => Stop();
    }

    public string Text
    {
        get => _text.Text;
        set { if (_text.Text != value) { _text.Text = value; Restart(); } }
    }

    public double FontSize { get => _text.FontSize; set => _text.FontSize = value; }
    public FontWeight FontWeight { get => _text.FontWeight; set => _text.FontWeight = value; }
    public FontFamily FontFamily { get => _text.FontFamily; set => _text.FontFamily = value; }
    public TextAlignment TextAlignment { get => _text.TextAlignment; set => _text.TextAlignment = value; }

    public Brush Foreground
    {
        get => _text.Foreground;
        set => _text.Foreground = value;
    }

    /// <summary>Bind the text colour to a theme brush so theme changes animate through.</summary>
    public void SetForegroundResource(object key) => _text.SetResourceReference(TextBlock.ForegroundProperty, key);

    private void Stop()
    {
        _story?.Stop();
        _story = null;
        _shift.BeginAnimation(TranslateTransform.XProperty, null);
        _shift.X = 0;
        OpacityMask = null;
    }

    private void Restart()
    {
        Stop();
        double overflow = _text.ActualWidth - ActualWidth;
        if (overflow <= 2 || ActualWidth <= 0) return;

        double fade = Math.Min(12 / ActualWidth, 0.25);
        OpacityMask = new LinearGradientBrush(new GradientStopCollection
        {
            new(Colors.Transparent, 0), new(Colors.Black, fade), new(Colors.Black, 1 - fade), new(Colors.Transparent, 1),
        }, new Point(0, 0), new Point(1, 0));

        var slide = TimeSpan.FromSeconds(overflow / 30.0);
        var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, TimeSpan.Zero));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.FromSeconds(1.5)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, TimeSpan.FromSeconds(1.5) + slide));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, TimeSpan.FromSeconds(3.0) + slide));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, TimeSpan.FromSeconds(3.4) + slide, new CubicEase { EasingMode = EasingMode.EaseInOut }));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.FromSeconds(4.0) + slide));

        _story = new Storyboard();
        Storyboard.SetTarget(anim, _shift);
        Storyboard.SetTargetProperty(anim, new PropertyPath(TranslateTransform.XProperty));
        _story.Children.Add(anim);
        _story.Begin();
    }
}
