// The enhanced frame (design.md §11.6), drawn over the whole window: the 2D (the overlay pass's, filtered linearly as
// the faithful presentation's second pass) over the 3D view where the mask lets it show. A widened view goes on beside
// the 2D's 4:3 rectangle in its rows; the other rows beside it show the border's tile in a level (Draw_TileClear's
// pattern, as if the frame went on), black elsewhere.
import fullscreen;

[[vk::binding(0, 0)]] Sampler2D overlay;
[[vk::binding(1, 0)]] Sampler2D viewWeight;
[[vk::binding(2, 0)]] Sampler2D view;
[[vk::binding(3, 0)]] Texture2D<uint> backtile;
[[vk::binding(4, 0)]] StructuredBuffer<uint> palette; // 0x00BBGGRR

struct CompositeParams
{
    float4 frameRect; // the 2D's rectangle of the window: x, y, width, height
    float4 viewRect;  // the 3D view's
    int2 frameSize;   // the frame's pixels
    int2 tileSize;    // the backtile's (0: none)
    int widened;      // the view goes on beside the 2D
    int inLevel;      // the border's tile beside the 2D
};

[[vk::push_constant]] ConstantBuffer<CompositeParams> params;

[shader("vertex")]
FullscreenVertex vertexMain(uint id : SV_VertexID)
{
    return fullscreenVertex(id);
}

float3 paletteColor(uint index)
{
    uint color = palette[index];
    return float3(color & 0xff, (color >> 8) & 0xff, (color >> 16) & 0xff) / 255.0;
}

[shader("fragment")]
float4 fragmentMain(FullscreenVertex input) : SV_Target
{
    float2 p = input.position.xy;
    float2 fp = (p - params.frameRect.xy) / params.frameRect.zw;
    bool inViewRect = all(p >= params.viewRect.xy) && all(p < params.viewRect.xy + params.viewRect.zw);
    float3 viewColor = inViewRect ? view.Sample((p - params.viewRect.xy) / params.viewRect.zw).rgb : float3(0, 0, 0);

    if (all(fp >= 0) && all(fp < 1))
    {
        float4 o = overlay.Sample(fp);
        return float4(o.rgb + (viewColor * viewWeight.Sample(fp).r), 1);
    }

    if (params.widened != 0 && inViewRect)
    {
        return float4(viewColor, 1);
    }

    // beside the frame, in its rows: the border's tile, its pixels as the frame's
    float2 framePixel = floor(fp * float2(params.frameSize));
    if (params.inLevel != 0 && params.tileSize.x > 0 && fp.y >= 0 && fp.y < 1)
    {
        int2 t = int2(framePixel) % params.tileSize;
        t = (t + params.tileSize) % params.tileSize;
        return float4(paletteColor(backtile.Load(int3(t, 0))), 1);
    }

    return float4(0, 0, 0, 1);
}
