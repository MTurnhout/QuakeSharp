using QuakeSharp.Core.Common;
using QuakeSharp.Core.Progs;

namespace QuakeSharp.Core.Server;

/// <summary>world.h: plane_t</summary>
public struct Plane
{
    public Vec3 normal;
    public float dist;
}

/// <summary>world.h: trace_t (a value, as the C returns and copies it)</summary>
public struct Trace
{
    /// <summary>if true, plane is not valid</summary>
    public bool allsolid;

    /// <summary>if true, the initial point was in a solid area</summary>
    public bool startsolid;

    public bool inopen, inwater;

    /// <summary>time completed, 1.0 = didn't hit anything</summary>
    public float fraction;

    /// <summary>final position</summary>
    public Vec3 endpos;

    /// <summary>surface normal at impact</summary>
    public Plane plane;

    /// <summary>entity the surface is on</summary>
    public Edict? ent;
}
