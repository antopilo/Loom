// A thin light that travels along a rounded rectangle's border with a fading tail. The element is the box itself, and the light stays
// inside it, so nothing around the box clips it and it never draws over neighboring controls.
// Parameter 0: x, y = element size in layout units, z = corner radius, w = distance of the border line from the element's edge.
// Parameter 1: the light's color (straight alpha).
// Parameter 2: x = seconds per lap, y = tail length as a share of the lap, z = steady glow (0 to 1), w = overall strength (0 to 1).
// Parameter 3: x = how far the light fades inward from the border, in layout units.
#include "goo_effect.slang"

float roundedBox(float2 p, float2 half, float radius)
{
    float2 q = abs(p) - half + radius;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
}

float4 gooEffect(float2 uv, float4 source, float4 backdrop)
{
    float4 box = gooParameter(0);
    float4 color = gooParameter(1);
    float4 motion = gooParameter(2);
    float width = max(gooParameter(3).x, 0.5);
    float2 size = max(box.xy, float2(1.0, 1.0));
    float2 p = uv * size - size * 0.5;
    float2 half = size * 0.5 - box.w;
    float distance = roundedBox(p, half, min(box.z, min(half.x, half.y)));

    // Full on the border line, fading inward; nothing outside it.
    float edge = distance > 0.5 ? 0.0 : exp(-max(-distance, 0.0) / width);

    // Where the point is along the border, as a share of the way round, clockwise from the top left corner: by distance, not angle, so the
    // light keeps one speed and one length on the long sides and the short ones.
    float w = half.x * 2.0;
    float h = half.y * 2.0;
    float along;
    if (half.y - abs(p.y) < half.x - abs(p.x))
        along = p.y < 0.0 ? p.x + half.x : w + h + (half.x - p.x);
    else
        along = p.x > 0.0 ? w + (p.y + half.y) : 2.0 * w + h + (half.y - p.y);
    float angle = along / (2.0 * (w + h));
    // The head runs clockwise; points behind it light up less the further back they are.
    float head = frac(gooElapsedSeconds() / max(motion.x, 0.1));
    float behind = frac(head - angle);
    // A short fade ahead of the head, so the light has no hard front edge.
    float tail = exp(-behind / max(motion.y, 0.01)) * smoothstep(0.0, 0.015, behind) + exp(-(1.0 - behind) / 0.008);

    float alpha = edge * lerp(saturate(motion.z), 1.0, tail) * saturate(motion.w) * color.a;
    return float4(color.rgb * alpha, alpha);
}
