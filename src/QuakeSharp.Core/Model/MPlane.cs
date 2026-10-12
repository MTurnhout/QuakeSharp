using QuakeSharp.Core.Common;

namespace QuakeSharp.Core.Model;

/// <summary>model.h: mplane_t. Planes are shared by reference, as C shares their pointers.</summary>
public sealed class MPlane
{
    public Vec3 normal;

    public float dist;

    /// <summary>for texture axis selection and fast side tests</summary>
    public byte type;

    /// <summary>signx + signy&lt;&lt;1 + signz&lt;&lt;2</summary>
    public byte signbits;
}
