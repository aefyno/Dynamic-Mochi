using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DynamicMochi.Controls;

/// <summary>
/// v1.7.1: the Liquid Glass pixel shader (Shaders/LiquidGlass.fx, compiled to LiquidGlass.ps in CI).
/// Throws when the compiled shader is missing (a local build without fxc); the caller falls back to a plain blur.
/// </summary>
public sealed class LiquidGlassEffect : ShaderEffect
{
    private static PixelShader? _shader;

    private static PixelShader Shader
    {
        get
        {
            if (_shader != null) return _shader;
            var ps = new PixelShader();
            ps.SetStreamSource(Application.GetResourceStream(new Uri("pack://application:,,,/Shaders/LiquidGlass.ps"))!.Stream);
            _shader = ps;
            return ps;
        }
    }

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(LiquidGlassEffect), 0);

    public static readonly DependencyProperty SizeProperty = Register("Size", typeof(Point), new Point(300, 40), 0);
    public static readonly DependencyProperty RadiusProperty = Register("Radius", typeof(double), 20.0, 1);
    public static readonly DependencyProperty RefractionProperty = Register("Refraction", typeof(double), 14.0, 2);
    public static readonly DependencyProperty BlurProperty = Register("Blur", typeof(double), 2.4, 3);
    public static readonly DependencyProperty TintProperty = Register("Tint", typeof(Color), Color.FromArgb(41, 10, 10, 14), 4);
    public static readonly DependencyProperty BevelProperty = Register("Bevel", typeof(double), 16.0, 5);
    public static readonly DependencyProperty FlatTopProperty = Register("FlatTop", typeof(double), 0.0, 6);
    public static readonly DependencyProperty LightProperty = Register("Light", typeof(Point), new Point(-0.55, -0.83), 7);

    private static DependencyProperty Register(string name, Type type, object value, int register) =>
        DependencyProperty.Register(name, type, typeof(LiquidGlassEffect), new UIPropertyMetadata(value, PixelShaderConstantCallback(register)));

    public LiquidGlassEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        foreach (var p in new[] { SizeProperty, RadiusProperty, RefractionProperty, BlurProperty, TintProperty, BevelProperty, FlatTopProperty, LightProperty })
            UpdateShaderValue(p);
    }

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Point Size { get => (Point)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }
    public double Refraction { get => (double)GetValue(RefractionProperty); set => SetValue(RefractionProperty, value); }
    public double Blur { get => (double)GetValue(BlurProperty); set => SetValue(BlurProperty, value); }
    public Color Tint { get => (Color)GetValue(TintProperty); set => SetValue(TintProperty, value); }
    public double Bevel { get => (double)GetValue(BevelProperty); set => SetValue(BevelProperty, value); }
    public double FlatTop { get => (double)GetValue(FlatTopProperty); set => SetValue(FlatTopProperty, value); }
    public Point Light { get => (Point)GetValue(LightProperty); set => SetValue(LightProperty, value); }

    /// <summary>Sets the shape; only touches the GPU constants when something changed.</summary>
    public void Shape(double w, double h, double radius, bool flatTop)
    {
        if (Math.Abs(Size.X - w) > 0.25 || Math.Abs(Size.Y - h) > 0.25) Size = new Point(Math.Max(1, w), Math.Max(1, h));
        if (Math.Abs(Radius - radius) > 0.25) Radius = radius;
        double bevel = Math.Clamp(Math.Min(w, h) * 0.34, 7, 18);
        if (Math.Abs(Bevel - bevel) > 0.25) Bevel = bevel;
        double flat = flatTop ? 1 : 0;
        if (FlatTop != flat) FlatTop = flat;
    }
}
