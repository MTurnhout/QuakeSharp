// The enhanced mode's alias models (design.md §11.6, step 5): each vertex of the pose placed as r_alias.c places it
// (scale * v + scale_origin, then the entity's forward, -right and up from its angles with the pitch turned) and lit
// as R_AliasTransformFinalVert lights it (r_ambientlight, darkened by r_shadelight where the vertex's normal faces away
// from r_plightvec), the light blended across the triangle as D_PolysetDraw blends it; the skin's texel then lit as
// the world's (the colormap's row, or the colour times the light), a player's colours moved as CL_NewTranslation moves
// them. Two poses can be blended (alias poses' interpolation, decision 64: off by default).
import frame;

[[vk::binding(0, 1)]] StructuredBuffer<uint> poses;    // x | y << 8 | z << 16 | normal << 24
[[vk::binding(1, 1)]] StructuredBuffer<float> normals; // r_avertexnormals: 162 x 3

[[vk::binding(0, 2)]] Texture2D<uint> skinIndices;
[[vk::binding(1, 2)]] Sampler2D skinColors;           // RGB, fullbright in alpha

struct AliasParams
{
    float4 model0;      // world = row . (x, y, z, 1) of the model's point
    float4 model1;
    float4 model2;
    float4 lightVector; // r_plightvec, w: r_shadelight
    float4 scale;       // xyz scale, w: the second pose's blend
    float4 scaleOrigin; // xyz, w: unused
    int pose;           // the pose's first word
    int pose2;          // the pose blended to
    int ambient;        // r_ambientlight
    int skinWidth;
    int skinHeight;
    int top;            // a player's colours (-1 none)
    int bottom;
    int flags;          // 1 the view model, 2 the skin's colours a pack's (the player's colours in them)
};

[[vk::push_constant]] ConstantBuffer<AliasParams> alias;

struct VertexInput
{
    int vertex : POSITION;
    float2 st : TEXCOORD0;
};

struct VertexOutput
{
    float4 position : SV_Position;
    float2 st : TEXCOORD0;
    float light : TEXCOORD1;
};

float3 unpackPoint(uint word) { return float3(word & 0xff, (word >> 8) & 0xff, (word >> 16) & 0xff); }

float3 normalOf(uint word)
{
    uint n = (word >> 24) * 3;
    return float3(normals[n], normals[n + 1], normals[n + 2]);
}

// r_alias.c: R_AliasTransformFinalVert's lighting
float vertexLight(float3 normal)
{
    float lightcos = dot(normal, alias.lightVector.xyz);
    int temp = alias.ambient;
    if (lightcos < 0)
    {
        temp += int(alias.lightVector.w * lightcos);
        if (temp < 0)
        {
            temp = 0;
        }
    }

    return float(temp);
}

[shader("vertex")]
VertexOutput vertexMain(VertexInput input)
{
    uint a = poses[alias.pose + input.vertex];
    uint b = poses[alias.pose2 + input.vertex];
    float blend = alias.scale.w;
    float3 v = lerp(unpackPoint(a), unpackPoint(b), blend);
    float3 local = (alias.scale.xyz * v) + alias.scaleOrigin.xyz;
    float4 p = float4(local, 1);
    float3 world = float3(dot(alias.model0, p), dot(alias.model1, p), dot(alias.model2, p));

    VertexOutput o;
    o.position = project(world);
    o.st = input.st;
    o.light = lerp(vertexLight(normalOf(a)), vertexLight(normalOf(b)), blend);
    return o;
}

// cl_parse.c: CL_NewTranslation: the skin's top and bottom ranges moved to the player's colours (reversed for the
// colours from 128 up)
uint translate(uint index)
{
    if (alias.top >= 0 && index >= 16 && index < 32)
    {
        uint j = index - 16;
        return alias.top < 128 ? alias.top + j : alias.top + 15 - j;
    }

    if (alias.bottom >= 0 && index >= 96 && index < 112)
    {
        uint j = index - 96;
        return alias.bottom < 128 ? alias.bottom + j : alias.bottom + 15 - j;
    }

    return index;
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target
{
    int2 size = int2(alias.skinWidth, alias.skinHeight);
    float2 at = floor(input.st);
    int2 texel = int2(at - (float2(size) * floor(at / float2(size))));
    uint index = translate(skinIndices.Load(int3(texel, 0)));

    if (colormapLighting())
    {
        uint row = uint(input.light) >> 8;
        return float4(paletteColor(colormapIndex(row, index)), 1);
    }

    // true colour: the base palette's colour of the translated index, lit, the fullbright colours unlit; a player's
    // translated texels read their own colour, the rest the filtered skin (a pack's skin has the player's colours)
    float3 color;
    float fullbright;
    if ((alias.flags & 2) == 0 && index != skinIndices.Load(int3(texel, 0)))
    {
        color = baseColor(index);
        fullbright = index >= 224 ? 1.0 : 0.0;
    }
    else
    {
        float4 texelColor = skinColors.Sample(input.st / float2(size));
        color = texelColor.rgb;
        fullbright = texelColor.a;
    }

    float scale = ((255.0 * 256.0) - (4.0 * input.light)) / 32768.0;
    return float4(shiftColor(color * lerp(scale, 1.0, fullbright)), 1);
}
