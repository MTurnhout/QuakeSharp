// A triangle that covers the viewport, with uv 0..1 over it (0,0 at the top left).
module fullscreen;

public struct FullscreenVertex
{
    public float4 position : SV_Position;
    public float2 uv : TEXCOORD0;
};

public FullscreenVertex fullscreenVertex(uint id)
{
    FullscreenVertex o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.uv = uv;
    o.position = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    return o;
}
