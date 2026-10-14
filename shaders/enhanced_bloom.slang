// The enhanced mode's bloom (design.md §11.9, enhanced_bloom): the 3D view's bright parts at a quarter of its size,
// blurred, for the composite pass to add over the view. Three passes of this shader: the view's colours above a
// brightness taken down to the quarter size (a 4x4 box of the view per texel, the threshold's excess kept), then a
// Gaussian across, then down (σ 2 of the quarter's texels, so 8 of the view's).
import fullscreen;

[[vk::binding(0, 0)]] Sampler2D source;

struct BloomParams
{
    float2 texel;     // the source's texel size, 0..1 units
    int pass;         // 0 down with the threshold, 1 across, 2 down the image
    float threshold;  // the brightness (0..1, the brightest channel) where the bloom starts
};

[[vk::push_constant]] ConstantBuffer<BloomParams> params;

[shader("vertex")]
FullscreenVertex vertexMain(uint id : SV_VertexID)
{
    return fullscreenVertex(id);
}

[shader("fragment")]
float4 fragmentMain(FullscreenVertex input) : SV_Target
{
    float2 uv = input.uv;
    if (params.pass == 0)
    {
        // four linear taps cover the 4x4 texels of the view under this texel
        float3 c = (source.Sample(uv + (params.texel * float2(-1, -1))).rgb + source.Sample(uv + (params.texel * float2(1, -1))).rgb
            + source.Sample(uv + (params.texel * float2(-1, 1))).rgb + source.Sample(uv + (params.texel * float2(1, 1))).rgb) * 0.25;
        float bright = max(c.r, max(c.g, c.b));
        float excess = max(bright - params.threshold, 0.0) / max(1.0 - params.threshold, 1e-3);
        return float4(c * excess, 1);
    }

    float2 axis = params.pass == 1 ? float2(params.texel.x, 0) : float2(0, params.texel.y);
    static const float weights[5] = { 0.2042, 0.1802, 0.1238, 0.0663, 0.0276 };
    float3 sum = source.Sample(uv).rgb * weights[0];
    for (int i = 1; i < 5; i++)
    {
        sum += (source.Sample(uv + (axis * i)).rgb + source.Sample(uv - (axis * i)).rgb) * weights[i];
    }

    return float4(sum, 1);
}
