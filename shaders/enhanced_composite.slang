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
[[vk::binding(5, 0)]] Sampler2D bloom;                 // enhanced_bloom.slang's, at a quarter of the view's size

struct CompositeParams
{
    float4 frameRect; // the 2D's rectangle of the window: x, y, width, height
    float4 viewRect;  // the 3D view's
    int2 frameSize;   // the frame's pixels
    int2 tileSize;    // the backtile's (0: none)
    int widened;      // the view goes on beside the 2D
    int inLevel;      // the border's tile beside the 2D
    float2 pixelScale; // the window's pixels per pixel of the frame
    float warpPhase;  // D_WarpScreen's: cl.time * 20 (stepped in the software's look)
    int warp;         // under water (r_dowarp): 1 smooth, 2 in the software's steps
    float bloomStrength; // design.md §11.9's post effects on the view: the bloom added (0 none),
    float vignette;   // the corners darkened by (0 none),
    int fxaa;         // 1: FXAA over the view
    int pad;
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

// d_scan.c: D_WarpScreen: under water each pixel of the view is read from one moved along the other axis's sine (3 +
// 3 sin, a cycle of 128 of the frame's pixels), the view shrunk by 6 pixels to make room
float sineWarp(float i) { return 3.0 + (3.0 * sin(i * 3.14159 * 2.0 / 128.0)); }

float2 viewUv(float2 p)
{
    float2 uv = (p - params.viewRect.xy) / params.viewRect.zw;
    if (params.warp == 0)
    {
        return uv;
    }

    float2 size = params.viewRect.zw / params.pixelScale; // the view in the frame's pixels
    float2 f = uv * size;
    float2 moved;
    if (params.warp == 2)
    {
        f = floor(f);
        moved = float2(f.x + floor(sineWarp(params.warpPhase + f.y)), f.y + floor(sineWarp(params.warpPhase + f.x)));
    }
    else
    {
        moved = float2(f.x + sineWarp(params.warpPhase + f.y), f.y + sineWarp(params.warpPhase + f.x));
    }

    return moved / (size + 6.0);
}

float luma(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }

// FXAA (Lottes's, its first version): where the view's brightness changes across an edge, its colours blended along it
float3 fxaa(float2 uv)
{
    uint w, h;
    view.GetDimensions(w, h);
    float2 rcp = 1.0 / float2(w, h);
    float3 m = view.Sample(uv).rgb;
    float nw = luma(view.Sample(uv + (float2(-1, -1) * rcp)).rgb);
    float ne = luma(view.Sample(uv + (float2(1, -1) * rcp)).rgb);
    float sw = luma(view.Sample(uv + (float2(-1, 1) * rcp)).rgb);
    float se = luma(view.Sample(uv + (float2(1, 1) * rcp)).rgb);
    float lm = luma(m);
    float least = min(lm, min(min(nw, ne), min(sw, se)));
    float most = max(lm, max(max(nw, ne), max(sw, se)));
    float2 dir = float2(-((nw + ne) - (sw + se)), (nw + sw) - (ne + se));
    float reduce = max((nw + ne + sw + se) * (0.25 / 8.0), 1.0 / 128.0);
    float scale = 1.0 / (min(abs(dir.x), abs(dir.y)) + reduce);
    dir = clamp(dir * scale, -8.0, 8.0) * rcp;
    float3 a = 0.5 * (view.Sample(uv + (dir * ((1.0 / 3.0) - 0.5))).rgb + view.Sample(uv + (dir * ((2.0 / 3.0) - 0.5))).rgb);
    float3 b = (a * 0.5) + (0.25 * (view.Sample(uv - (dir * 0.5)).rgb + view.Sample(uv + (dir * 0.5)).rgb));
    float lb = luma(b);
    return lb < least || lb > most ? a : b;
}

// the view's colour at uv with the post effects: FXAA, the bloom added, the vignette darkening towards the corners
// (at its rectangle's point at, 0..1)
float3 viewEffects(float2 uv, float2 at)
{
    float3 c = params.fxaa != 0 ? fxaa(uv) : view.Sample(uv).rgb;
    if (params.bloomStrength > 0)
    {
        c += bloom.Sample(uv).rgb * params.bloomStrength;
    }

    if (params.vignette > 0)
    {
        float2 d = (at - 0.5) * 2.0;
        c *= 1.0 - (params.vignette * smoothstep(0.4, 1.5, dot(d, d)));
    }

    return c;
}
[shader("fragment")]
float4 fragmentMain(FullscreenVertex input) : SV_Target
{
    float2 p = input.position.xy;
    float2 fp = (p - params.frameRect.xy) / params.frameRect.zw;
    bool inViewRect = all(p >= params.viewRect.xy) && all(p < params.viewRect.xy + params.viewRect.zw);
    float3 viewColor = inViewRect ? viewEffects(viewUv(p), (p - params.viewRect.xy) / params.viewRect.zw) : float3(0, 0, 0);

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
        float2 size = float2(params.tileSize);
        int2 t = int2(framePixel - (size * floor(framePixel / size)));
        return float4(paletteColor(backtile.Load(int3(t, 0))), 1);
    }

    return float4(0, 0, 0, 1);
}
