using QuakeSharp.Core.Common;

namespace QuakeSharp.Core.Render;

/// <summary>d_iface.h: ptype_t</summary>
public enum ptype_t
{
    pt_static,
    pt_grav,
    pt_slowgrav,
    pt_fire,
    pt_explode,
    pt_explode2,
    pt_blob,
    pt_blob2,
}

/// <summary>d_iface.h: particle_t</summary>
public sealed class Particle
{
    public Vec3 org;
    public float color;
    public Particle? next;
    public Vec3 vel;
    public float ramp;
    public float die;
    public ptype_t type;
}

/// <summary>vid.h: vrect_t (without pnext, a list only the video drivers' updates use)</summary>
public struct VRect
{
    public int x, y, width, height;
}

/// <summary>render.h: refdef_t</summary>
public sealed class RefDef
{
    /// <summary>subwindow in video for refresh</summary>
    public VRect vrect;

    /// <summary>scaled Alias version</summary>
    public VRect aliasvrect;

    /// <summary>right &amp; bottom screen coords</summary>
    public int vrectright, vrectbottom;

    /// <summary>scaled Alias versions</summary>
    public int aliasvrectright, aliasvrectbottom;

    /// <summary>rightmost right edge we care about, for use in edge list</summary>
    public float vrectrightedge;

    /// <summary>for floating-point compares</summary>
    public float fvrectx, fvrecty;

    /// <summary>left and top edges, for clamping</summary>
    public float fvrectx_adj, fvrecty_adj;

    /// <summary>(vrect.x + 0.5 - epsilon) &lt;&lt; 20</summary>
    public int vrect_x_adj_shift20;

    /// <summary>(vrectright + 0.5 - epsilon) &lt;&lt; 20</summary>
    public int vrectright_adj_shift20;

    /// <summary>right and bottom edges, for clamping</summary>
    public float fvrectright_adj, fvrectbottom_adj;

    /// <summary>rightmost edge, for Alias clamping</summary>
    public float fvrectright;

    /// <summary>bottommost edge, for Alias clamping</summary>
    public float fvrectbottom;

    /// <summary>at Z = 1.0, this many X is visible; 2.0 = 90 degrees</summary>
    public float horizontalFieldOfView;

    /// <summary>should probably allways be 0.5</summary>
    public float xOrigin;

    /// <summary>between be around 0.3 to 0.5</summary>
    public float yOrigin;

    public Vec3 vieworg;
    public Vec3 viewangles;

    public float fov_x, fov_y;

    public int ambientlight;
}
