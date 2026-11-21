// Liquid Glass — WPF pixel shader (ps_3_0), compiled in CI with fxc:
//   fxc /T ps_3_0 /E main /O3 /Fo LiquidGlass.ps LiquidGlass.fx
// Input: the screen area behind the capsule, full size.
// v1.7.2: clear glass, not frosted. The background stays sharp; the depth comes from a thick rounded rim
// that bends the background like a lens, a colour split along that rim, a glossy reflection across the top,
// sharp highlights on the lit side and a shade on the far side. A light tint keeps white text readable.

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

float4 soft(float2 uv, float2 px)
{
    float2 o = px * Blur;
    float4 c = tex2D(Input, uv) * 4;
    c += tex2D(Input, uv + float2(o.x, 0)) + tex2D(Input, uv - float2(o.x, 0))
       + tex2D(Input, uv + float2(0, o.y)) + tex2D(Input, uv - float2(0, o.y));
    return c / 8;
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
    // a round, thick rim: the surface gets steeper towards the edge, so the bending does too
    float slope = t / sqrt(max(1 - t * t * 0.94, 0.06));

    // the whole piece magnifies a touch, the rim bends the background inward like a lens
    float2 cuv = 0.5 + (uv - 0.5) * 0.985;
    float2 suv = clamp(cuv - n * slope * Refraction * px, px, 1 - px);
    float4 c = soft(suv, px);

    // colour split along the rim
    float2 ca = n * slope * 1.8 * px;
    float split = saturate(t * 1.4);
    c.r = lerp(c.r, tex2D(Input, clamp(suv - ca, px, 1 - px)).r, split);
    c.b = lerp(c.b, tex2D(Input, clamp(suv + ca, px, 1 - px)).b, split);

    // a light tint, stronger only over bright backgrounds so white text stays readable
    float l = dot(c.rgb, float3(0.299, 0.587, 0.114));
    c.rgb = lerp(c.rgb, Tint.rgb, saturate(Tint.a + 0.30 * l));

    float key = saturate(dot(n, Light));
    float bounce = saturate(dot(n, -Light));

    // glossy reflection across the upper part, fading out before the rim
    float gloss = (1 - smoothstep(0, 0.55, uv.y)) * (1 - t);
    c.rgb += gloss * 0.09;

    // the rim reflects more than the middle (Fresnel)
    c.rgb += pow(t, 3) * 0.08;

    // sharp highlights on the rim: strong where it faces the light, a softer one on the opposite side
    float band = smoothstep(0.45, 0.9, t);
    c.rgb += band * (0.85 * pow(key, 4) + 0.35 * pow(bounce, 5));

    // a thin bright edge all round
    c.rgb += pow(t, 22) * 0.35;

    // thickness: the rim away from the light is in shade
    c.rgb *= 1 - 0.28 * smoothstep(0.15, 0.85, t) * (1 - key) * (1 - 0.6 * bounce);

    // lit from the top, a little deeper at the bottom
    c.rgb *= 1.04 - 0.10 * uv.y;

    float a = saturate(0.5 - d);
    return float4(saturate(c.rgb) * a, a);
}
