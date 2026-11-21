// v1.7.1 Liquid Glass — WPF pixel shader (ps_3_0), compiled in CI with fxc:
//   fxc /T ps_3_0 /E main /O3 /Fo LiquidGlass.ps LiquidGlass.fx
// Input: the screen area behind the capsule (captured at 1/3 size, stretched back up = first blur pass).
// The shader adds a soft blur, bends the background like a convex lens near the rim, splits colour at the rim,
// darkens bright backgrounds so white text stays readable, and lights the edge from one side for depth.

sampler2D Input : register(s0);

float2 Size       : register(c0);   // element size, DIP
float  Radius     : register(c1);   // corner radius, DIP
float  Refraction : register(c2);   // how far the rim pulls the background inward, DIP
float  Blur       : register(c3);   // extra blur radius, DIP
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
    float2 o = px * Blur;
    float4 c = tex2D(Input, uv) * 4;
    c += (tex2D(Input, uv + float2(o.x, 0)) + tex2D(Input, uv - float2(o.x, 0))
        + tex2D(Input, uv + float2(0, o.y)) + tex2D(Input, uv - float2(0, o.y))) * 2;
    c += tex2D(Input, uv + o) + tex2D(Input, uv - o)
       + tex2D(Input, uv + float2(o.x, -o.y)) + tex2D(Input, uv + float2(-o.x, o.y));
    return c / 16;
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

    // 0 in the flat middle → 1 at the very edge: the curved rim of the glass
    float t = saturate(1 + d / Bevel);
    float lens = t * t;

    // convex lens: near the rim the background is sampled from further inside, so it bends and stretches
    float2 suv = clamp(uv - n * lens * Refraction * px, px, 1 - px);
    float4 c = blurred(suv, px);

    // colour split along the rim, like light through a thick edge
    float2 ca = n * lens * 2.0 * px;
    c.r = lerp(c.r, tex2D(Input, clamp(suv - ca, px, 1 - px)).r, lens);
    c.b = lerp(c.b, tex2D(Input, clamp(suv + ca, px, 1 - px)).b, lens);

    // a little more colour, then the glass tint — stronger over bright backgrounds so white text stays readable
    float l = dot(c.rgb, float3(0.299, 0.587, 0.114));
    c.rgb = lerp(l.xxx, c.rgb, 1.2);
    c.rgb = lerp(c.rgb, Tint.rgb, saturate(Tint.a + 0.42 * l));

    // volume: lit from the top, deeper at the bottom
    c.rgb *= 1.07 - 0.14 * uv.y;

    // light on the rim: strong on the side facing the light, a weaker bounce opposite, a thin bright edge all round
    float key = saturate(dot(n, Light));
    float bounce = saturate(dot(n, -Light));
    float rim = pow(t, 5);
    c.rgb += rim * (0.5 * pow(key, 1.5) + 0.2 * pow(bounce, 2)) + pow(t, 18) * 0.25;

    // thickness: a soft shade just inside the rim, away from the light
    c.rgb *= 1 - 0.16 * pow(t, 3) * (1 - key);

    float a = saturate(0.5 - d);
    return float4(saturate(c.rgb) * a, a);
}
