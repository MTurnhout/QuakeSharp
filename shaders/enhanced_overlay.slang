// The enhanced mode's 2D (design.md §11.6): the 8-bit frame through its palette where the 2D wrote it last (its mask),
// each pixel repeated scale x scale times as the faithful presentation's first pass. Two outputs: the 2D's colour
// premultiplied by its coverage, and how much of the 3D view shows (1, a quarter where Draw_FadeScreen darkened it, 0
// under the 2D); the composite pass filters both into the window. With enhanced_2d (design.md §11.9) the 2D's staircase
// edges are cut along their diagonals as Scale2x (EPX) decides them, at the scale's own resolution: a pixel's corner
// takes the colour of the two neighbours meeting there where they agree and the pixel's other two do not, inside the
// triangle from the middle of one side to the middle of the other.
import fullscreen;

[[vk::binding(0, 0)]] Texture2D<uint> frameIndices;
[[vk::binding(1, 0)]] Texture2D<uint> frameMask; // 0 the 2D, 1 the view; + 2 faded
[[vk::binding(2, 0)]] StructuredBuffer<uint> palette; // 0x00BBGGRR

struct OverlayParams
{
    int scale;
    int viewDrawn; // 0: no view this frame, every pixel is the frame's own
    int smooth;    // 1: the 2D's edges smoothed (enhanced_2d)
    int pad;
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

bool isView(int2 pixel)
{
    return params.viewDrawn != 0 && (frameMask.Load(int3(pixel, 0)) & 1) != 0;
}

// a pixel's palette index, or 256 for the view's and outside the frame (equal to no 2D pixel)
uint index2D(int2 pixel)
{
    uint width, height;
    frameIndices.GetDimensions(width, height);
    if (any(pixel < 0) || pixel.x >= int(width) || pixel.y >= int(height) || isView(pixel))
    {
        return 256;
    }

    return frameIndices.Load(int3(pixel, 0));
}

// Scale2x's corner: the neighbours a and b meeting there agree, and the pixel's other two neighbours c and d do not
bool corner(uint a, uint b, uint c, uint d)
{
    return a == b && a != 256 && a != c && b != d;
}

[shader("fragment")]
OverlayOutput fragmentMain(FullscreenVertex input)
{
    int2 pixel = int2(input.position.xy) / params.scale;
    OverlayOutput o;
    if (isView(pixel))
    {
        o.overlay = float4(0, 0, 0, 0);
        o.viewWeight = (frameMask.Load(int3(pixel, 0)) & 2) != 0 ? 0.25 : 1.0;
        return o;
    }

    uint index = frameIndices.Load(int3(pixel, 0));
    if (params.smooth != 0 && params.scale > 1)
    {
        // where in the pixel: 0..1 across, from its top left
        float2 f = (input.position.xy - (float2(pixel) * params.scale)) / params.scale;
        uint up = index2D(pixel + int2(0, -1));
        uint down = index2D(pixel + int2(0, 1));
        uint left = index2D(pixel + int2(-1, 0));
        uint right = index2D(pixel + int2(1, 0));
        if (f.x + f.y < 0.5 && corner(up, left, right, down))
        {
            index = up;
        }
        else if ((1 - f.x) + f.y < 0.5 && corner(up, right, left, down))
        {
            index = up;
        }
        else if (f.x + (1 - f.y) < 0.5 && corner(down, left, right, up))
        {
            index = down;
        }
        else if ((1 - f.x) + (1 - f.y) < 0.5 && corner(down, right, left, up))
        {
            index = down;
        }
    }

    uint color = palette[index];
    o.overlay = float4(color & 0xff, (color >> 8) & 0xff, (color >> 16) & 0xff, 255) / 255.0;
    o.viewWeight = 0;
    return o;
}
