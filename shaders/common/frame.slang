// What every enhanced draw reads of the frame (design.md §11.6): one buffer of words, at the offsets
// VulkanPresenter.Enhanced.cs writes them (FrameWords): the view's projection, the light styles, the dynamic lights,
// the palettes, the colour shifts' tables, the colormap and the frame's switches.
module frame;

[[vk::binding(0, 0)]] StructuredBuffer<uint> frameWords;

static const int VIEW_PROJECTION = 0;   // 16 floats: four rows
static const int LIGHT_STYLES = 16;     // 64 ints (d_lightstylevalue)
static const int DLIGHTS = 80;          // 32 x (x, y, z, radius)
static const int DLIGHT_MINLIGHTS = 208;// 32 floats
static const int PALETTE = 240;         // 256 x 0x00BBGGRR, the palette shown
static const int BASE_PALETTE = 496;    // 256 x 0x00BBGGRR, host_basepal
static const int CHANNEL_TABLES = 752;  // 768 bytes: red, green, blue
static const int AMBIENT = 944;
static const int DLIGHT_COUNT = 945;
static const int LIGHTING = 946;        // 0 true colour, 1 the colormap
static const int FULLBRIGHT = 947;
static const int TIME = 948;            // cl.time as a float
static const int VIEW_ORIGIN = 949;     // r_refdef.vieworg: 3 floats
static const int STEPPED = 952;         // 1: the sky and water move in the software's steps (its look)
static const int VIEW_AXES = 953;       // vpn, vright, vup: 9 floats
static const int SKY_MAP = 962;         // the frame's pixel of the view target's: u = x * a + b, v = y * c + d
static const int SKY_SIZES = 966;       // max(vrect width, height), vid.width / 2, vid.height / 2
static const int LINEAR_SKY = 1022;     // 1: the game's sky read between its texels (enhanced_filter 1 in true colour)
static const int DEBUG_SURFACES = 1023; // 1: each pixel the number of its surface (the tools' debugging)
static const int COLORMAP = 1024;       // 64 x 256 bytes

public float4 project(float3 world)
{
    float4 p = float4(world, 1);
    return float4(
        dot(row(0), p),
        dot(row(1), p),
        dot(row(2), p),
        dot(row(3), p));
}

float4 row(int r)
{
    int b = VIEW_PROJECTION + (r * 4);
    return float4(asfloat(frameWords[b]), asfloat(frameWords[b + 1]), asfloat(frameWords[b + 2]), asfloat(frameWords[b + 3]));
}

public int lightStyle(uint style) { return int(frameWords[LIGHT_STYLES + style]); }

public int dlightCount() { return int(frameWords[DLIGHT_COUNT]); }

public float4 dlight(int i)
{
    int b = DLIGHTS + (i * 4);
    return float4(asfloat(frameWords[b]), asfloat(frameWords[b + 1]), asfloat(frameWords[b + 2]), asfloat(frameWords[b + 3]));
}

public float dlightMinlight(int i) { return asfloat(frameWords[DLIGHT_MINLIGHTS + i]); }

public uint ambientLight() { return frameWords[AMBIENT]; }

public bool frameFullbright() { return frameWords[FULLBRIGHT] != 0; }

public bool colormapLighting() { return frameWords[LIGHTING] != 0; }

public float frameTime() { return asfloat(frameWords[TIME]); }

public float3 viewOrigin() { return float3(asfloat(frameWords[VIEW_ORIGIN]), asfloat(frameWords[VIEW_ORIGIN + 1]), asfloat(frameWords[VIEW_ORIGIN + 2])); }

public bool stepped() { return frameWords[STEPPED] != 0; }

public bool linearSky() { return frameWords[LINEAR_SKY] != 0; }

public bool debugSurfaces() { return frameWords[DEBUG_SURFACES] != 0; }

float word(int i) { return asfloat(frameWords[i]); }

public float3 viewAxis(int axis) { int b = VIEW_AXES + (axis * 3); return float3(word(b), word(b + 1), word(b + 2)); }

// d_sky.c: D_Sky_uv_To_st's ray for a pixel of the view target: the frame's pixel it shows, from the frame's centre,
// 8192 / max(vrect's width, height) per pixel against 4096 ahead (not the view's own projection)
public float3 softwareSkyRay(float2 targetPixel)
{
    float u = (targetPixel.x * word(SKY_MAP)) + word(SKY_MAP + 1);
    float v = (targetPixel.y * word(SKY_MAP + 2)) + word(SKY_MAP + 3);
    float temp = word(SKY_SIZES);
    float wu = 8192.0 * (u - word(SKY_SIZES + 1)) / temp;
    float wv = 8192.0 * (word(SKY_SIZES + 2) - v) / temp;
    return (4096.0 * viewAxis(0)) + (wu * viewAxis(1)) + (wv * viewAxis(2));
}

float3 unpack(uint color) { return float3(color & 0xff, (color >> 8) & 0xff, (color >> 16) & 0xff) / 255.0; }

// a palette index as shown (the palette with its shifts and gamma)
public float3 paletteColor(uint index) { return unpack(frameWords[PALETTE + index]); }

// a palette index before the shifts and gamma
public float3 baseColor(uint index) { return unpack(frameWords[BASE_PALETTE + index]); }

// colormap[row * 256 + index]
public uint colormapIndex(uint row, uint index)
{
    uint at = (row * 256) + index;
    return (frameWords[COLORMAP + (at >> 2)] >> ((at & 3) * 8)) & 0xff;
}

uint channelTable(int channel, int value)
{
    uint at = (channel * 256) + value;
    return (frameWords[CHANNEL_TABLES + (at >> 2)] >> ((at & 3) * 8)) & 0xff;
}

// a colour (0-1, of the base palette's channels) through V_UpdatePalette's tables: the shifts, then gamma; between
// two of the tables' entries, part way
public float3 shiftColor(float3 color)
{
    float3 v = clamp(color, 0.0, 1.0) * 255.0;
    float3 result;
    for (int c = 0; c < 3; c++)
    {
        int lo = int(floor(v[c]));
        int hi = min(lo + 1, 255);
        result[c] = lerp(float(channelTable(c, lo)), float(channelTable(c, hi)), v[c] - lo) / 255.0;
    }

    return result;
}
