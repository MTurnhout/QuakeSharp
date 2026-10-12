// First pass of the presentation (design.md §6.4): the 8-bit frame through its palette, each pixel repeated scale x
// scale times (nearest neighbour), into an image an integer multiple of the frame's size.
import fullscreen;

[[vk::binding(0, 0)]] Texture2D<uint> frameIndices;
[[vk::binding(1, 0)]] StructuredBuffer<uint> palette; // 0x00BBGGRR

struct PaletteParams
{
    int scale;
};

[[vk::push_constant]] ConstantBuffer<PaletteParams> params;

[shader("vertex")]
FullscreenVertex vertexMain(uint id : SV_VertexID)
{
    return fullscreenVertex(id);
}

[shader("fragment")]
float4 fragmentMain(FullscreenVertex input) : SV_Target
{
    int2 pixel = int2(input.position.xy) / params.scale;
    uint color = palette[frameIndices.Load(int3(pixel, 0))];
    return float4(color & 0xff, (color >> 8) & 0xff, (color >> 16) & 0xff, 255) / 255.0;
}
