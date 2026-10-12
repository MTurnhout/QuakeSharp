using System.Runtime.CompilerServices;

namespace QuakeSharp.Core.Common;

/// <summary>
/// mathlib.h: vec3_t, three floats indexed as in C (v[0]). Passed by ref where C passes the pointer, so outputs that
/// are also inputs alias as they do in C.
/// </summary>
[InlineArray(3)]
public struct Vec3
{
    private float element0;

    public Vec3(float x, float y, float z)
    {
        this[0] = x;
        this[1] = y;
        this[2] = z;
    }
}

/// <summary>Four floats (a row of float[3][4]).</summary>
[InlineArray(4)]
public struct Vec4
{
    private float element0;
}

/// <summary>float[3][3], rows of three, indexed m[i][j] as in C.</summary>
[InlineArray(3)]
public struct Mat3
{
    private Vec3 row0;
}

/// <summary>float[3][4], rows of four, indexed m[i][j] as in C.</summary>
[InlineArray(3)]
public struct Mat3x4
{
    private Vec4 row0;
}
