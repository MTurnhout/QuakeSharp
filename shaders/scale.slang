// Second pass of the presentation (design.md §6.4): the integer-scaled frame filtered linearly into its rectangle of
// the window (the frame's display aspect, letterboxed).
import fullscreen;

[[vk::binding(0, 0)]] Sampler2D upscaled;

[shader("vertex")]
FullscreenVertex vertexMain(uint id : SV_VertexID)
{
    return fullscreenVertex(id);
}

[shader("fragment")]
float4 fragmentMain(FullscreenVertex input) : SV_Target
{
    return upscaled.Sample(input.uv);
}
