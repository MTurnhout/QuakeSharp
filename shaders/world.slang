// The enhanced mode's brush models (design.md §11.6): the world and the brush entities, each texel lit as r_surf.c
// lights the software's surfaces. R_BuildLightMap's light at the four luxels around the texel (the ambient light, each
// lightmap times its style's value, R_AddDynamicLights' lights; bound, inverted and shifted), blended as
// R_DrawSurfaceBlock8 blends across its blocks; then either the colormap's row of that light looked up for the texel
// (the software's look) or the texel's colour times the light, up to twice as bright (true colour), through the
// palette's shifts and gamma. With a pack's material (design.md §11.7, decision 71), each dynamic light's part of the
// light shaped by the material's normal per pixel, and its gloss as a highlight.
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
[[vk::binding(2, 2)]] Sampler2D textureMaterial;      // a pack's: the tangent space normal in RGB, the gloss in alpha

struct DrawParams
{
    float4 model0; // the entity's transform: rows of rotation and origin
    float4 model1;
    float4 model2;
    float2 textureSize;
    int flags;     // 4: the colours are a pack's (DRAW_REPLACED); 8: and a material of its (DRAW_MATERIAL)
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
    float3 world : TEXCOORD2;
    float3 local : TEXCOORD3; // in the model's own space, as the surfaces' planes and axes are
};

static const int SURF_PLANEBACK = 2;
static const float BACKFACE_EPSILON = 0.01;

// r_bsp.c: a surface is drawn only from the side it faces (R_RecursiveWorldNode, R_DrawSolidClippedSubmodelPolygons:
// the eye, in the model's space, more than BACKFACE_EPSILON in front of the plane, or behind it for SURF_PLANEBACK)
bool facesEye(Surface surf)
{
    float d = dot(modelEye(), surf.plane.xyz) - surf.plane.w;
    return (surf.light.z & SURF_PLANEBACK) != 0 ? d < -BACKFACE_EPSILON : d > BACKFACE_EPSILON;
}

float3 modelOrigin() { return float3(draw.model0.w, draw.model1.w, draw.model2.w); }

// the eye in the model's own space
float3 modelEye()
{
    float3 eye = viewOrigin() - modelOrigin();
    return (eye.x * draw.model0.xyz) + (eye.y * draw.model1.xyz) + (eye.z * draw.model2.xyz);
}

[shader("vertex")]
VertexOutput vertexMain(VertexInput input)
{
    float4 p = float4(input.position, 1);
    float3 world = float3(dot(draw.model0, p), dot(draw.model1, p), dot(draw.model2, p));
    VertexOutput o;
    // a surface facing away: all its vertices outside the clip volume, so nothing of it is drawn
    o.position = facesEye(surfaces[input.surface]) ? project(world) : float4(0, 0, -2, 1);
    o.st = input.st;
    o.surface = input.surface;
    o.world = world;
    o.local = input.position;
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

    uint block = staticBlock(surf, s, t, smax, size);
    for (int l = 0; l < dlightCount(); l++)
    {
        float add;
        if (dlightReaches(surf, l, s, t, add))
        {
            block = uint(int(block + add));
        }
    }

    return lightFromBlock(block);
}

// r_surf.c: R_BuildLightMap's block for one luxel before the dynamic lights: the ambient light and each lightmap times
// its style's value
uint staticBlock(Surface surf, int s, int t, int smax, int size)
{
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

    return block;
}

// r_surf.c: R_AddDynamicLights for one light and luxel, in the entity's own space (the software moves the lights by
// the entity's origin): whether it reaches the luxel, and what it adds to its block
bool dlightReaches(Surface surf, int l, int s, int t, out float add)
{
    add = 0;
    float4 light = dlight(l);
    float3 origin = light.xyz - modelOrigin();
    float dist = dot(origin, surf.plane.xyz) - surf.plane.w;
    float rad = light.w - abs(dist);
    float minlight = dlightMinlight(l);
    if (rad < minlight)
    {
        return false;
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
        add = (rad - d) * 256;
        return true;
    }

    return false;
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

// The light at st with a pack's material (decision 71): surfaceLight's, but each dynamic light's part of each luxel
// times how much more the pixel's normal faces the light than the surface itself does (a flat normal changes nothing;
// the lightmaps' light, which has no direction, stays as it is); and each light's highlight on the gloss (Blinn-Phong,
// exponent 32), as much of a block as the light's part at the pixel, in highlight
float materialLight(Surface surf, float2 st, float3 local, float4 material, out float highlight)
{
    highlight = 0;
    if (frameFullbright())
    {
        return 0;
    }

    int smax = (surf.extent.z >> 4) + 1;
    int tmax = (surf.extent.w >> 4) + 1;
    float2 l = clamp((st - float2(surf.extent.xy)) / 16.0, float2(0, 0), float2(smax - 1, tmax - 1));
    int2 l0 = int2(floor(l));
    int2 l1 = min(l0 + 1, int2(smax - 1, tmax - 1));
    float2 f = l - float2(l0);
    int size = smax * tmax;
    int2 corners[4] = { l0, int2(l1.x, l0.y), int2(l0.x, l1.y), l1 };
    float weights[4] = { (1 - f.x) * (1 - f.y), f.x * (1 - f.y), (1 - f.x) * f.y, f.x * f.y };
    uint blocks[4];
    for (int c = 0; c < 4; c++)
    {
        blocks[c] = staticBlock(surf, corners[c].x, corners[c].y, smax, size);
    }

    // the tangent frame: the texture's s axis (the image's right), its t axis (the image's down), the side drawn
    float3 n = (surf.light.z & SURF_PLANEBACK) != 0 ? -surf.plane.xyz : surf.plane.xyz;
    float3 sAxis = normalize(surf.vecs0.xyz - (n * dot(surf.vecs0.xyz, n)));
    float3 tAxis = normalize(surf.vecs1.xyz - (n * dot(surf.vecs1.xyz, n)));
    float3 tangent = (material.xyz * 2.0) - 1.0;
    float3 normal = normalize((sAxis * tangent.x) - (tAxis * tangent.y) + (n * tangent.z));
    float3 toEye = normalize(modelEye() - local);
    for (int i = 0; i < dlightCount(); i++)
    {
        float3 toLight = normalize((dlight(i).xyz - modelOrigin()) - local);
        float facing = max(0.0, 1.0 + dot(normal, toLight) - dot(n, toLight));
        float part = 0;
        for (int c = 0; c < 4; c++)
        {
            float add;
            if (dlightReaches(surf, i, corners[c].x, corners[c].y, add))
            {
                blocks[c] = uint(int(blocks[c] + (add * facing)));
                part += add * weights[c];
            }
        }

        if (dot(n, toLight) > 0 && dot(normal, toLight) > 0)
        {
            highlight += part * material.w * pow(max(dot(normal, normalize(toLight + toEye)), 0.0), 32.0);
        }
    }

    float la = lightFromBlock(blocks[0]);
    float lb = lightFromBlock(blocks[1]);
    float lc = lightFromBlock(blocks[2]);
    float ld = lightFromBlock(blocks[3]);
    return lerp(lerp(la, lb, f.x), lerp(lc, ld, f.x), f.y);
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
    float2 size = float2(max(int2(draw.textureSize) >> mip, int2(1, 1)));
    // wrapped by floor, not %: the shader's % on negative numbers is not C's (r_surf.c adds tmax << 16 first)
    float2 at = floor(st / float(1 << mip));
    int2 texel = int2(at - (size * floor(at / size)));
    return textureIndices.Load(int3(texel, mip));
}

// r_sky.c and d_sky.c: the sky's two layers, the texture's right half behind and its left half in front (index 0
// clear), seen along the view's ray flattened three times (D_Sky_uv_To_st's end[2] *= 3), the back scrolling 8 texels a
// second and the front, moved again by R_MakeSky's shift, twice as fast; skytime is cl.time modulo 512
uint skyIndex(float3 world, float2 targetPixel)
{
    float3 dir = stepped() ? softwareSkyRay(targetPixel) : world - viewOrigin();
    dir.z *= 3;
    dir = normalize(dir);
    float time = frameTime();
    float skytime = time - (floor(time / 512.0) * 512.0);
    float scroll = skytime * 8.0;
    float s = scroll + (6.0 * 63.0 * dir.x);
    float t = scroll + (6.0 * 63.0 * dir.y);
    float shift = stepped() ? floor(scroll) : scroll;
    int2 back = int2(int(floor(s)) & 127, int(floor(t)) & 127);
    int2 front = int2(int(floor(s + shift)) & 127, int(floor(t + shift)) & 127);
    uint f = textureIndices.Load(int3(front, 0));
    return f != 0 ? f : textureIndices.Load(int3(back.x + 128, back.y, 0));
}

static const int DRAW_REPLACED = 4;
static const int DRAW_MATERIAL = 8;

// a texel of a pack's sky with its colour times its alpha (so the clear texels' colour does not darken the edges)
float4 premultiplied(int x, int y)
{
    float4 t = textureColors.Load(int3(x, y, 0));
    return float4(t.rgb * t.a, t.a);
}

// a texel of one layer of a pack's sky, between texels, wrapping within the layer (x0: the layer's first column),
// premultiplied by its alpha
float4 skyLayer(float2 p, int x0, int width, int height)
{
    p -= 0.5;
    int2 i = int2(floor(p));
    float2 f = p - floor(p);
    int xa = ((i.x % width) + width) % width, xb = (xa + 1) % width;
    int ya = ((i.y % height) + height) % height, yb = (ya + 1) % height;
    float4 a = premultiplied(x0 + xa, ya), b = premultiplied(x0 + xb, ya);
    float4 c = premultiplied(x0 + xa, yb), d = premultiplied(x0 + xb, yb);
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

// a pack's sky (design.md §11.7): skyIndex's two layers and their scrolling, the colours read between the pack's
// texels, the front layer over the back by its alpha
float3 replacedSky(float3 world, float2 targetPixel)
{
    float3 dir = stepped() ? softwareSkyRay(targetPixel) : world - viewOrigin();
    dir.z *= 3;
    dir = normalize(dir);
    float time = frameTime();
    float skytime = time - (floor(time / 512.0) * 512.0);
    float scroll = skytime * 8.0;
    float s = scroll + (6.0 * 63.0 * dir.x);
    float t = scroll + (6.0 * 63.0 * dir.y);
    float shift = stepped() ? floor(scroll) : scroll;
    uint w, h, levels;
    textureColors.GetDimensions(0, w, h, levels);
    float k = float(w) / draw.textureSize.x;
    int layer = int(w) / 2;
    float4 front = skyLayer(float2(s + shift, t + shift) * k, 0, layer, int(h));
    float4 back = skyLayer(float2(s, t) * k, layer, layer, int(h));
    return (back.rgb * (1.0 - front.a)) + front.rgb;
}

// d_scan.c: D_DrawTurbulent8Span: each texel moved along the other axis's sine (r_turb_turb: 8 + 8 sin, a cycle of
// 128 texels, 20 a second), from the surface's texturemins, in a 64x64 texture
float sineTurb(float i) { return 8.0 + (8.0 * sin(i * 3.14159 * 2.0 / 128.0)); }

float2 turbulent(Surface surf, float2 st)
{
    float2 rel = clamp(st - float2(surf.extent.xy), float2(0, 0), float2(surf.extent.zw) - (1.0 / 65536.0));
    float phase = frameTime() * 20.0;
    if (stepped())
    {
        phase = floor(phase);
        return float2(rel.x + floor(sineTurb(phase + floor(rel.y))), rel.y + floor(sineTurb(phase + floor(rel.x))));
    }

    return float2(rel.x + sineTurb(phase + rel.y), rel.y + sineTurb(phase + rel.x));
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target
{
    Surface surf = surfaces[input.surface];
    int flags = surf.light.z;
    if (debugSurfaces())
    {
        return float4(float(input.surface & 0xff) / 255.0, float((input.surface >> 8) & 0xff) / 255.0, 1, 1);
    }

    // the sky and water are not lit: their texels as the palette shows them
    if ((flags & SURF_DRAWSKY) != 0)
    {
        if ((draw.flags & DRAW_REPLACED) != 0 && !colormapLighting())
        {
            return float4(shiftColor(replacedSky(input.world, input.position.xy)), 1);
        }

        return float4(paletteColor(skyIndex(input.world, input.position.xy)), 1);
    }

    if ((flags & SURF_DRAWTURB) != 0)
    {
        float2 warped = turbulent(surf, input.st);
        if (colormapLighting())
        {
            int2 texel = int2(floor(warped)) & 63;
            return float4(paletteColor(textureIndices.Load(int3(texel, 0))), 1);
        }

        return float4(shiftColor(textureColors.Sample(warped / 64.0).rgb), 1);
    }

    if ((draw.flags & DRAW_MATERIAL) != 0 && !colormapLighting())
    {
        float2 uv = input.st / draw.textureSize;
        float highlight;
        float light = materialLight(surf, input.st, input.local, textureMaterial.Sample(uv), highlight);
        float4 texel = textureColors.Sample(uv);
        float scale = ((255.0 * 256.0) - (4.0 * light)) / 32768.0;
        float3 lit = (texel.rgb * lerp(scale, 1.0, texel.a)) + ((highlight / 32768.0) * (1.0 - texel.a));
        return float4(shiftColor(lit), 1);
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
