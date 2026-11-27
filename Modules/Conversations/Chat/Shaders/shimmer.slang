// A soft band of light that sweeps across the element's own pixels, left to right, then waits before the next sweep.
// Parameter 0: x = band half-width as a share of the width, y = seconds per sweep and pause, z = brightness the band adds (0 to 1).
#include "goo_effect.slang"

float4 gooEffect(float2 uv, float4 source, float4 backdrop)
{
    float4 setup = gooParameter(0);
    float halfWidth = max(setup.x, 0.01);
    // The band crosses during the first 60% of each period and rests off the right edge for the rest.
    float phase = frac(gooElapsedSeconds() / max(setup.y, 0.1)) / 0.6;
    float center = lerp(-halfWidth, 1.0 + halfWidth, saturate(phase));
    float band = saturate(1.0 - abs(uv.x - center) / halfWidth);
    band = band * band * (3.0 - 2.0 * band);
    // Colors are premultiplied, so lighting toward white means toward the pixel's own alpha.
    float3 lit = lerp(source.rgb, float3(source.a, source.a, source.a), band * saturate(setup.z));
    return float4(lit, source.a);
}
