// The enhanced mode's brush models (design.md §11.6): the world and the brush entities, each texel lit as r_surf.c
// lights the software's surfaces. R_BuildLightMap's light at the four luxels around the texel (the ambient light, each
// lightmap times its style's value, R_AddDynamicLights' lights; bound, inverted and shifted), blended as
// R_DrawSurfaceBlock8 blends across its blocks; then either the colormap's row of that light looked up for the texel
// (the software's look) or the texel's colour times the light, up to twice as bright (true colour), through the
// palette's shifts and gamma.
import frame;

struct Surface
{
    float4 plane;  // normal, dist
    float4 vecs0;  // the texture's s axis and offset
    float4 vecs1;  // t
    int4 extent;   // texturemins s, t; extents s, t
    int4 light;    // light data offset (-1 none), styles (4 bytes), flags, texture
};

[[vk::binding(0, 1)]] StructuredBuffer<Surface> surfaces;
[[vk::binding(1, 1)]] ByteAddressBuffer lightData;

[[vk::binding(0, 2)]] Texture2D<uint> textureIndices; // the BSP's four mip levels
[[vk::binding(1, 2)]] Sampler2D textureColors;        // RGB, fullbright in alpha

struct DrawParams
{
    float4 model0; // the entity's transform: rows of rotation and origin
    float4 model1;
    float4 model2;
    float2 textureSize;
    int flags;     // 1 sky, 2 turbulent
    int pad;
};

[[vk::push_constant]] ConstantBuffer<DrawParams> draw;

static const int SURF_DRAWSKY = 4;
static const int SURF_DRAWTURB = 0x10;

struct VertexInput
{
    float3 position : POSITION;
    float2 st : TEXCOORD0;
    int surface : TEXCOORD1;
};

struct VertexOutput
{
    float4 position : SV_Position;
    float2 st : TEXCOORD0;
    nointerpolation int surface : TEXCOORD1;
};

[shader("vertex")]
VertexOutput vertexMain(VertexInput input)
{
    float4 p = float4(input.position, 1);
    float3 world = float3(dot(draw.model0, p), dot(draw.model1, p), dot(draw.model2, p));
    VertexOutput o;
    o.position = project(world);
    o.st = input.st;
    o.surface = input.surface;
    return o;
}

uint lightSample(int offset)
{
    uint word = lightData.Load((offset >> 2) << 2);
    return (word >> ((offset & 3) * 8)) & 0xff;
}

// r_surf.c: R_BuildLightMap for one luxel (s, t) of the surface, with R_AddDynamicLights: bound, inverted, shifted
int luxelLight(Surface surf, int s, int t, int smax, int size)
{
    if (frameFullbright())
    {
        return 0; // r_fullbright, or a world without light data: the blocks stay 0, the brightest
    }

    uint block = ambientLight() << 8;
    int offset = surf.light.x;
    for (int map = 0; map < 4 && offset >= 0; map++)
    {
        uint style = (uint(surf.light.y) >> (map * 8)) & 0xff;
        if (style == 255)
        {
            break;
        }

        block += lightSample(offset + (t * smax) + s) * uint(lightStyle(style));
        offset += size;
    }

    // the dynamic lights, in the entity's own space (the software moves them by the entity's origin)
    for (int l = 0; l < dlightCount(); l++)
    {
        float4 light = dlight(l);
        float3 origin = light.xyz - float3(draw.model0.w, draw.model1.w, draw.model2.w);
        float dist = dot(origin, surf.plane.xyz) - surf.plane.w;
        float rad = light.w - abs(dist);
        float minlight = dlightMinlight(l);
        if (rad < minlight)
        {
            continue;
        }

        minlight = rad - minlight;
        float3 impact = origin - (surf.plane.xyz * dist);
        float localS = dot(impact, surf.vecs0.xyz) + surf.vecs0.w - surf.extent.x;
        float localT = dot(impact, surf.vecs1.xyz) + surf.vecs1.w - surf.extent.y;
        int sd = abs(int(localS - (s * 16)));
        int td = abs(int(localT - (t * 16)));
        float d = sd > td ? sd + (td >> 1) : td + (sd >> 1);
        if (d < minlight)
        {
            block = uint(int(block + ((rad - d) * 256)));
        }
    }

    return lightFromBlock(block);
}

int lightFromBlock(uint block)
{
    int t = ((255 * 256) - int(block)) >> 2;
    return t < (1 << 6) ? (1 << 6) : t;
}

// the light at texture coordinates st: the four luxels around it, blended as the software blends across a block
float surfaceLight(Surface surf, float2 st)
{
    int smax = (surf.extent.z >> 4) + 1;
    int tmax = (surf.extent.w >> 4) + 1;
    float2 l = clamp((st - float2(surf.extent.xy)) / 16.0, float2(0, 0), float2(smax - 1, tmax - 1));
    int2 l0 = int2(floor(l));
    int2 l1 = min(l0 + 1, int2(smax - 1, tmax - 1));
    float2 f = l - float2(l0);
    int size = smax * tmax;
    float a = luxelLight(surf, l0.x, l0.y, smax, size);
    float b = luxelLight(surf, l1.x, l0.y, smax, size);
    float c = luxelLight(surf, l0.x, l1.y, smax, size);
    float d = luxelLight(surf, l1.x, l1.y, smax, size);
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

// the mip level the hardware would pick, for the palette indices (which cannot be filtered)
int mipLevel(float2 st)
{
    float2 dx = ddx(st), dy = ddy(st);
    float rho = max(length(dx), length(dy));
    return clamp(int(floor(log2(max(rho, 1e-6)) + 0.5)), 0, 3);
}

uint textureIndex(float2 st)
{
    int mip = mipLevel(st);
    int2 size = max(int2(draw.textureSize) >> mip, int2(1, 1));
    int2 texel = int2(floor(st / float(1 << mip)));
    texel = ((texel % size) + size) % size;
    return textureIndices.Load(int3(texel, mip));
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target
{
    Surface surf = surfaces[input.surface];
    int flags = surf.light.z;

    // sky and water: not lit (their own drawing comes with step 4)
    if ((flags & (SURF_DRAWSKY | SURF_DRAWTURB)) != 0)
    {
        return float4(paletteColor(textureIndex(input.st)), 1);
    }

    float light = surfaceLight(surf, input.st);
    if (colormapLighting())
    {
        uint row = uint(light) >> 8;
        return float4(paletteColor(colormapIndex(row, textureIndex(input.st))), 1);
    }

    float4 texel = textureColors.Sample(input.st / draw.textureSize);
    float scale = ((255.0 * 256.0) - (4.0 * light)) / 32768.0;
    float3 lit = texel.rgb * lerp(scale, 1.0, texel.a);
    return float4(shiftColor(lit), 1);
}
