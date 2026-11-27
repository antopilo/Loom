// A light that travels around a rounded rectangle's edge with a fading tail, over a faint steady glow along the whole edge.
// The element is the box grown by the glow's reach on every side, so the light spills a little outside the box it follows.
// Parameter 0: x, y = element size in layout units, z = corner radius of the box, w = reach (the margin around the box).
// Parameter 1: the light's color, premultiplied by nothing (straight alpha).
// Parameter 2: x = seconds per lap, y = tail length as a share of the lap, z = steady glow (0 to 1), w = overall strength (0 to 1).
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
    float2 size = max(box.xy, float2(1.0, 1.0));
    float reach = max(box.w, 0.5);
    float2 p = uv * size - size * 0.5;
    float2 half = size * 0.5 - reach;
    float distance = roundedBox(p, half, min(box.z, min(half.x, half.y)));

    // Bright on the edge itself, fading over the reach outside and a little less inside.
    float edge = exp(-abs(distance) / (distance > 0.0 ? reach * 0.45 : reach * 0.25));

    // The head runs clockwise; points behind it light up less the further back they are.
    float angle = atan2(p.y * size.x / size.y, p.x) / 6.2831853 + 0.5;
    float head = frac(gooElapsedSeconds() / max(motion.x, 0.1));
    float behind = frac(head - angle);
    float tail = exp(-behind / max(motion.y, 0.01));

    float alpha = edge * lerp(saturate(motion.z), 1.0, tail) * saturate(motion.w) * color.a;
    return float4(color.rgb * alpha, alpha);
}
