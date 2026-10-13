// The enhanced mode's sprites (design.md §11.6, step 5): r_sprite.c's quad, the frame's up, down, left and right
// along the axes the sprite's type gives (R_DrawSprite), its texels unlit (D_SpriteDrawSpans writes them as they are),
// 255 clear.
import frame;

[[vk::binding(0, 2)]] Texture2D<uint> pictureIndices;
[[vk::binding(1, 2)]] Sampler2D pictureColors; // RGB, alpha 0 where clear

struct SpriteParams
{
    float4 origin;    // xyz
    float4 upAxis;    // xyz, w: the frame's up
    float4 rightAxis; // xyz, w: the frame's down
    float4 extent;    // the frame's left, right, width, height
};

[[vk::push_constant]] ConstantBuffer<SpriteParams> sprite;

struct VertexOutput
{
    float4 position : SV_Position;
    float2 st : TEXCOORD0;
};

[shader("vertex")]
VertexOutput vertexMain(uint id : SV_VertexID)
{
    // two triangles: (up, left) (up, right) (down, right), (up, left) (down, right) (down, left)
    static const int2 corners[6] = { int2(0, 0), int2(1, 0), int2(1, 1), int2(0, 0), int2(1, 1), int2(0, 1) };
    int2 c = corners[id];
    float u = c.x == 0 ? sprite.extent.x : sprite.extent.y;
    float v = c.y == 0 ? sprite.upAxis.w : sprite.rightAxis.w;
    float3 world = sprite.origin.xyz + (sprite.upAxis.xyz * v) + (sprite.rightAxis.xyz * u);
    VertexOutput o;
    o.position = project(world);
    o.st = float2(c) * sprite.extent.zw;
    return o;
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target
{
    int2 size = int2(sprite.extent.zw);
    int2 texel = clamp(int2(floor(input.st)), int2(0, 0), size - 1);
    if (colormapLighting())
    {
        uint index = pictureIndices.Load(int3(texel, 0));
        if (index == 255)
        {
            discard;
        }

        return float4(paletteColor(index), 1);
    }

    float4 color = pictureColors.Sample(input.st / float2(size));
    if (color.a < 0.5)
    {
        discard;
    }

    return float4(shiftColor(color.rgb), 1);
}
