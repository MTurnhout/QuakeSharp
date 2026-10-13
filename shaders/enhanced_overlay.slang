// The enhanced mode's 2D (design.md §11.6): the 8-bit frame through its palette where the 2D wrote it last (its mask),
// each pixel repeated scale x scale times as the faithful presentation's first pass. Two outputs: the 2D's colour
// premultiplied by its coverage, and how much of the 3D view shows (1, a quarter where Draw_FadeScreen darkened it, 0
// under the 2D); the composite pass filters both into the window.
import fullscreen;

[[vk::binding(0, 0)]] Texture2D<uint> frameIndices;
[[vk::binding(1, 0)]] Texture2D<uint> frameMask; // 0 the 2D, 1 the view; + 2 faded
[[vk::binding(2, 0)]] StructuredBuffer<uint> palette; // 0x00BBGGRR

struct OverlayParams
{
    int scale;
    int viewDrawn; // 0: no view this frame, every pixel is the frame's own
};

[[vk::push_constant]] ConstantBuffer<OverlayParams> params;

struct OverlayOutput
{
    float4 overlay : SV_Target0;
    float viewWeight : SV_Target1;
};

[shader("vertex")]
FullscreenVertex vertexMain(uint id : SV_VertexID)
{
    return fullscreenVertex(id);
}

[shader("fragment")]
OverlayOutput fragmentMain(FullscreenVertex input)
{
    int2 pixel = int2(input.position.xy) / params.scale;
    uint mask = frameMask.Load(int3(pixel, 0));
    OverlayOutput o;
    if (params.viewDrawn != 0 && (mask & 1) != 0)
    {
        o.overlay = float4(0, 0, 0, 0);
        o.viewWeight = (mask & 2) != 0 ? 0.25 : 1.0;
    }
    else
    {
        uint color = palette[frameIndices.Load(int3(pixel, 0))];
        o.overlay = float4(color & 0xff, (color >> 8) & 0xff, (color >> 16) & 0xff, 255) / 255.0;
        o.viewWeight = 0;
    }

    return o;
}
