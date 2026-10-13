// The enhanced mode's particles (design.md §11.6, step 5): each one as d_part.c's D_DrawParticle draws it, a square of
// (32768 / z) >> d_pix_shift pixels of the frame (from d_pix_min to d_pix_max, twice as tall where d_y_aspect_shift
// says), its top left corner where r_part.c's shrunk projection puts it, scaled to the window; or a round dot of that
// size (enhanced_particles). Its colour unlit, through the palette shown.
import frame;

[[vk::binding(0, 1)]] StructuredBuffer<float4> particles; // x, y, z, colour

struct ParticleParams
{
    float2 framePixel; // one pixel of the frame in clip space (x, y)
    float shrink;      // xscaleshrink / xscale: (vrect width - 6) / vrect width
    int pixShift;      // d_pix_shift
    int pixMin;        // d_pix_min
    int pixMax;        // d_pix_max
    int aspectShift;   // d_y_aspect_shift
    int round;         // 1: round dots
};

[[vk::push_constant]] ConstantBuffer<ParticleParams> params;

static const float PARTICLE_Z_CLIP = 8.0;

struct VertexOutput
{
    float4 position : SV_Position;
    float2 corner : TEXCOORD0; // 0..1 across the square
    nointerpolation uint color : TEXCOORD1;
};

[shader("vertex")]
VertexOutput vertexMain(uint id : SV_VertexID, uint instance : SV_InstanceID)
{
    static const float2 corners[6] = { float2(0, 0), float2(1, 0), float2(1, 1), float2(0, 0), float2(1, 1), float2(0, 1) };
    float4 p = particles[instance];
    float4 clip = project(p.xyz);
    VertexOutput o;
    o.corner = corners[id];
    o.color = uint(p.w);
    if (clip.w < PARTICLE_Z_CLIP)
    {
        o.position = float4(0, 0, -2, 1); // too near: not drawn
        return o;
    }

    float pix;
    if (stepped())
    {
        int izi = int((1.0 / clip.w) * 32768.0);
        pix = float(clamp(izi >> params.pixShift, params.pixMin, params.pixMax));
    }
    else
    {
        pix = clamp((32768.0 / clip.w) / float(1 << params.pixShift), float(params.pixMin), float(params.pixMax));
    }

    float2 size = float2(pix, pix * float(1 << params.aspectShift));
    clip.xy *= params.shrink;
    clip.xy += corners[id] * size * params.framePixel * clip.w;
    o.position = clip;
    return o;
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target
{
    if (params.round != 0 && length(input.corner - 0.5) > 0.5)
    {
        discard;
    }

    return float4(paletteColor(input.color), 1);
}
