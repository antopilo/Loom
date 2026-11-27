// Fades the element's own pixels out toward its right edge, so a title too long for its row ends softly instead of with an ellipsis.
// The fade spans a multiple of the element's height, measured from the pixel derivatives, so it keeps its length at any size and scale.
// Parameter 0: x = fade length as a multiple of the element's height.
#include "goo_effect.slang"

float4 gooEffect(float2 uv, float4 source, float4 backdrop)
{
    // One pixel's step in uv across and down: the element is 1 / ddx wide and 1 / ddy tall in pixels.
    float across = max(abs(ddx(uv.x)), 1e-6);
    float down = max(abs(ddy(uv.y)), 1e-6);
    float length = max(gooParameter(0).x, 0.1) * across / down;
    float fade = saturate((1.0 - uv.x) / length);
    fade = fade * fade * (3.0 - 2.0 * fade);
    // Colors are premultiplied, so the whole pixel scales.
    return source * fade;
}
