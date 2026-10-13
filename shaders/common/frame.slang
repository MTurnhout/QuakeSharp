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
static const int COLORMAP = 960;        // 64 x 256 bytes

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
