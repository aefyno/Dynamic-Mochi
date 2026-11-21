// Liquid Glass — WPF pixel shader (ps_3_0), compiled in CI with fxc:
//   fxc /T ps_3_0 /E main /O3 /Fo LiquidGlass.ps LiquidGlass.fx
// Input: the screen area behind the capsule at half size (the downscale is the first blur pass).
// v1.7.3: lightly blurred (the background stays recognisable), depth from a round rim that bends the
// background like a lens, light only on a thin rim (no glossy stripe: that read as Frutiger Aero),
// a faint colour split at the very edge and a soft shade on the far side. A light tint keeps white text readable.

sampler2D Input : register(s0);

float2 Size       : register(c0);   // element size, DIP
float  Radius     : register(c1);   // corner radius, DIP
float  Refraction : register(c2);   // how far the rim pulls the background inward, DIP
float  Blur       : register(c3);   // softening radius, DIP (small: clear glass)
float4 Tint       : register(c4);   // glass colour; alpha = base strength
float  Bevel      : register(c5);   // width of the curved rim, DIP
float  FlatTop    : register(c6);   // 1 = notch shape (no rim along the top)
float2 Light      : register(c7);   // direction towards the light, screen space

float sdBox(float2 p, float2 b, float r)
{
    float2 q = abs(p) - b + r;
    return length(max(q, 0)) + min(max(q.x, q.y), 0) - r;
}

float shape(float2 p)
{
    float2 hs = Size * 0.5;
    float r = min(Radius, min(hs.x, hs.y));
    if (FlatTop > 0.5)
    {
        // a taller box whose top edge sits above the element: the visible top is flat and has no rim
        return sdBox(p + float2(0, r + 2), hs + float2(0, r + 2), r);
    }
    return sdBox(p, hs, r);
}

float4 blurred(float2 uv, float2 px)
{
    // 17 taps in two rings: a smooth, round blur
    float2 o = px * Blur;
    float2 o2 = o * 2;
    float4 c = tex2D(Input, uv) * 4;
    c += (tex2D(Input, uv + float2(o.x, 0)) + tex2D(Input, uv - float2(o.x, 0))
        + tex2D(Input, uv + float2(0, o.y)) + tex2D(Input, uv - float2(0, o.y))) * 2;
    c += tex2D(Input, uv + o * 0.7) + tex2D(Input, uv - o * 0.7)
       + tex2D(Input, uv + float2(o.x, -o.y) * 0.7) + tex2D(Input, uv + float2(-o.x, o.y) * 0.7);
    c += tex2D(Input, uv + float2(o2.x, 0)) + tex2D(Input, uv - float2(o2.x, 0))
       + tex2D(Input, uv + float2(0, o2.y)) + tex2D(Input, uv - float2(0, o2.y));
    return c / 20;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 px = 1.0 / Size;
    float2 p = (uv - 0.5) * Size;
    float d = shape(p);

    // outward normal of the rounded shape
    float e = 0.75;
    float2 n = float2(shape(p + float2(e, 0)) - shape(p - float2(e, 0)),
                      shape(p + float2(0, e)) - shape(p - float2(0, e)));
    n = n / max(length(n), 0.0001);

    // 0 in the flat middle → 1 at the very edge
    float t = saturate(1 + d / Bevel);
    // round rim: the surface gets steeper towards the edge, so the bending does too
    float slope = t / sqrt(max(1 - t * t * 0.9, 0.1));

    // the rim bends the background inward like a lens; the middle stays true
    float2 suv = clamp(uv - n * slope * Refraction * px, px, 1 - px);
    float4 c = blurred(suv, px);

    // a faint colour split, only right at the rim
    float2 ca = n * slope * 0.9 * px;
    float split = t * t;
    c.r = lerp(c.r, tex2D(Input, clamp(suv - ca, px, 1 - px)).r, split);
    c.b = lerp(c.b, tex2D(Input, clamp(suv + ca, px, 1 - px)).b, split);

    // light tint, stronger only over bright backgrounds so white text stays readable
    float l = dot(c.rgb, float3(0.299, 0.587, 0.114));
    c.rgb = lerp(c.rgb, Tint.rgb, saturate(Tint.a + 0.28 * l));

    // light lives only on the thin rim (no gloss stripe): brighter where it faces the light, a faint bounce opposite
    float key = saturate(dot(n, Light));
    float bounce = saturate(dot(n, -Light));
    // v1.7.4: a wide soft glow on the rim, no constant edge and no hairline (they read as an outline)
    float edge = smoothstep(0.5, 1.0, t);
    c.rgb += edge * edge * (0.20 * key * key + 0.07 * bounce * bounce);

    // a soft shade just inside the rim, away from the light: thickness
    c.rgb *= 1 - 0.10 * smoothstep(0.35, 0.9, t) * (1 - key);

    float a = saturate(0.5 - d);
    return float4(saturate(c.rgb) * a, a);
}
