using System.Windows;
using System.Windows.Media.Animation;

namespace DynamicMochi.Controls;

/// <summary>
/// v1.7: damped spring, the "gooey" motion. Overshoots the target, wobbles back and settles:
/// 1 − e^(−Damping·t) · cos(Frequency·t). The first overshoot is about e^(−π·Damping/Frequency).
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    public static readonly DependencyProperty DampingProperty =
        DependencyProperty.Register(nameof(Damping), typeof(double), typeof(SpringEase), new PropertyMetadata(6.0));

    public static readonly DependencyProperty FrequencyProperty =
        DependencyProperty.Register(nameof(Frequency), typeof(double), typeof(SpringEase), new PropertyMetadata(10.0));

    public SpringEase() => EasingMode = EasingMode.EaseIn;   // EaseIn = the curve exactly as written below

    public SpringEase(double damping, double frequency) : this()
    {
        Damping = damping;
        Frequency = frequency;
    }

    public double Damping
    {
        get => (double)GetValue(DampingProperty);
        set => SetValue(DampingProperty, value);
    }

    public double Frequency
    {
        get => (double)GetValue(FrequencyProperty);
        set => SetValue(FrequencyProperty, value);
    }

    /// <summary>A spring whose first overshoot is <paramref name="overshoot"/> of the distance (0.02 … 0.3).</summary>
    public static SpringEase WithOvershoot(double overshoot, double frequency = 10)
    {
        double p = Math.Clamp(overshoot, 0.01, 0.3);
        return new SpringEase(-Math.Log(p) * frequency / Math.PI, frequency);
    }

    protected override double EaseInCore(double t)
    {
        if (t >= 1) return 1;
        // fade the remaining wobble out over the last 15 %, so the end lands exactly on the target
        double v = 1 - Math.Exp(-Damping * t) * Math.Cos(Frequency * t);
        double tail = Math.Clamp((t - 0.85) / 0.15, 0, 1);
        return v + (1 - v) * tail * tail;
    }

    protected override Freezable CreateInstanceCore() => new SpringEase();
}
