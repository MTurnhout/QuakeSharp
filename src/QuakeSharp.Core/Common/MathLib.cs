using QuakeSharp.Core.Model;
using QuakeSharp.Core.Numerics;

namespace QuakeSharp.Core.Common;

/// <summary>
/// mathlib.c and mathlib.h's macros. Every expression keeps the C's types and order: float arithmetic in float, a
/// double literal or M_PI making the expression double, the C library's sin and cos taking and giving doubles (musl's,
/// design.md §3.1), and C's (int) through <see cref="X86.FtoI"/>.
/// </summary>
public static class MathLib
{
    // quakedef.h: PITCH, YAW, ROLL
    public const int PITCH = 0;
    public const int YAW = 1;
    public const int ROLL = 2;

    // mathlib.h: M_PI, as gcc v2's math.h
    public const double M_PI = 3.14159265358979323846;

    // mathlib.c: vec3_origin
    public static readonly Vec3 vec3_origin;

    // mathlib.h: DotProduct
    public static float DotProduct(in Vec3 x, in Vec3 y) => (x[0] * y[0]) + (x[1] * y[1]) + (x[2] * y[2]);

    // mathlib.h: VectorSubtract
    public static void VectorSubtract(in Vec3 a, in Vec3 b, ref Vec3 c)
    {
        c[0] = a[0] - b[0];
        c[1] = a[1] - b[1];
        c[2] = a[2] - b[2];
    }

    // mathlib.h: VectorAdd
    public static void VectorAdd(in Vec3 a, in Vec3 b, ref Vec3 c)
    {
        c[0] = a[0] + b[0];
        c[1] = a[1] + b[1];
        c[2] = a[2] + b[2];
    }

    // mathlib.h: VectorCopy
    public static void VectorCopy(in Vec3 a, ref Vec3 b)
    {
        b[0] = a[0];
        b[1] = a[1];
        b[2] = a[2];
    }

    // mathlib.c: ProjectPointOnPlane
    public static void ProjectPointOnPlane(ref Vec3 dst, in Vec3 p, in Vec3 normal)
    {
        float d;
        Vec3 n = default;
        float inv_denom;

        inv_denom = 1.0F / DotProduct(normal, normal);

        d = DotProduct(normal, p) * inv_denom;

        n[0] = normal[0] * inv_denom;
        n[1] = normal[1] * inv_denom;
        n[2] = normal[2] * inv_denom;

        dst[0] = p[0] - (d * n[0]);
        dst[1] = p[1] - (d * n[1]);
        dst[2] = p[2] - (d * n[2]);
    }

    // mathlib.c: PerpendicularVector (assumes "src" is normalized)
    public static void PerpendicularVector(ref Vec3 dst, in Vec3 src)
    {
        int pos;
        int i;
        float minelem = 1.0F;
        Vec3 tempvec = default;

        // find the smallest magnitude axially aligned vector
        for (pos = 0, i = 0; i < 3; i++)
        {
            if (Math.Abs((double)src[i]) < minelem)
            {
                pos = i;
                minelem = (float)Math.Abs((double)src[i]);
            }
        }

        tempvec[0] = tempvec[1] = tempvec[2] = 0.0F;
        tempvec[pos] = 1.0F;

        // project the point onto the plane defined by src
        ProjectPointOnPlane(ref dst, tempvec, src);

        // normalize the result
        VectorNormalize(ref dst);
    }

    // mathlib.c: RotatePointAroundVector
    public static void RotatePointAroundVector(ref Vec3 dst, in Vec3 dir, in Vec3 point, float degrees)
    {
        Mat3 m = default;
        Mat3 im;
        Mat3 zrot = default;
        Mat3 tmpmat = default;
        Mat3 rot = default;
        int i;
        Vec3 vr = default, vup = default, vf = default;

        vf[0] = dir[0];
        vf[1] = dir[1];
        vf[2] = dir[2];

        PerpendicularVector(ref vr, dir);
        CrossProduct(vr, vf, ref vup);

        m[0][0] = vr[0];
        m[1][0] = vr[1];
        m[2][0] = vr[2];

        m[0][1] = vup[0];
        m[1][1] = vup[1];
        m[2][1] = vup[2];

        m[0][2] = vf[0];
        m[1][2] = vf[1];
        m[2][2] = vf[2];

        im = m;

        im[0][1] = m[1][0];
        im[0][2] = m[2][0];
        im[1][0] = m[0][1];
        im[1][2] = m[2][1];
        im[2][0] = m[0][2];
        im[2][1] = m[1][2];

        zrot[0][0] = zrot[1][1] = zrot[2][2] = 1.0F;

        // DEG2RAD( a ) ( a * M_PI ) / 180.0F
        zrot[0][0] = (float)Libm.cos(degrees * M_PI / 180.0F);
        zrot[0][1] = (float)Libm.sin(degrees * M_PI / 180.0F);
        zrot[1][0] = (float)-Libm.sin(degrees * M_PI / 180.0F);
        zrot[1][1] = (float)Libm.cos(degrees * M_PI / 180.0F);

        R_ConcatRotations(m, zrot, ref tmpmat);
        R_ConcatRotations(tmpmat, im, ref rot);

        for (i = 0; i < 3; i++)
        {
            dst[i] = (rot[i][0] * point[0]) + (rot[i][1] * point[1]) + (rot[i][2] * point[2]);
        }
    }

    // mathlib.c: anglemod
    public static float anglemod(float a) =>
        (float)(360.0 / 65536 * (X86.FtoI(a * (65536 / 360.0)) & 65535));

    // mathlib.c: BoxOnPlaneSide
    // Returns 1, 2, or 1 + 2
    public static int BoxOnPlaneSide(in Vec3 emins, in Vec3 emaxs, MPlane p)
    {
        float dist1, dist2;
        int sides;

        // general case
        switch (p.signbits)
        {
            case 0:
                dist1 = (p.normal[0] * emaxs[0]) + (p.normal[1] * emaxs[1]) + (p.normal[2] * emaxs[2]);
                dist2 = (p.normal[0] * emins[0]) + (p.normal[1] * emins[1]) + (p.normal[2] * emins[2]);
                break;
            case 1:
                dist1 = (p.normal[0] * emins[0]) + (p.normal[1] * emaxs[1]) + (p.normal[2] * emaxs[2]);
                dist2 = (p.normal[0] * emaxs[0]) + (p.normal[1] * emins[1]) + (p.normal[2] * emins[2]);
                break;
            case 2:
                dist1 = (p.normal[0] * emaxs[0]) + (p.normal[1] * emins[1]) + (p.normal[2] * emaxs[2]);
                dist2 = (p.normal[0] * emins[0]) + (p.normal[1] * emaxs[1]) + (p.normal[2] * emins[2]);
                break;
            case 3:
                dist1 = (p.normal[0] * emins[0]) + (p.normal[1] * emins[1]) + (p.normal[2] * emaxs[2]);
                dist2 = (p.normal[0] * emaxs[0]) + (p.normal[1] * emaxs[1]) + (p.normal[2] * emins[2]);
                break;
            case 4:
                dist1 = (p.normal[0] * emaxs[0]) + (p.normal[1] * emaxs[1]) + (p.normal[2] * emins[2]);
                dist2 = (p.normal[0] * emins[0]) + (p.normal[1] * emins[1]) + (p.normal[2] * emaxs[2]);
                break;
            case 5:
                dist1 = (p.normal[0] * emins[0]) + (p.normal[1] * emaxs[1]) + (p.normal[2] * emins[2]);
                dist2 = (p.normal[0] * emaxs[0]) + (p.normal[1] * emins[1]) + (p.normal[2] * emaxs[2]);
                break;
            case 6:
                dist1 = (p.normal[0] * emaxs[0]) + (p.normal[1] * emins[1]) + (p.normal[2] * emins[2]);
                dist2 = (p.normal[0] * emins[0]) + (p.normal[1] * emaxs[1]) + (p.normal[2] * emaxs[2]);
                break;
            case 7:
                dist1 = (p.normal[0] * emins[0]) + (p.normal[1] * emins[1]) + (p.normal[2] * emins[2]);
                dist2 = (p.normal[0] * emaxs[0]) + (p.normal[1] * emaxs[1]) + (p.normal[2] * emaxs[2]);
                break;
            default:
                // mathlib.c: BOPS_Error
                throw new QuakeException("BoxOnPlaneSide:  Bad signbits");
        }

        sides = 0;
        if (dist1 >= p.dist)
        {
            sides = 1;
        }

        if (dist2 < p.dist)
        {
            sides |= 2;
        }

        return sides;
    }

    // mathlib.h: BOX_ON_PLANE_SIDE
    public static int BOX_ON_PLANE_SIDE(in Vec3 emins, in Vec3 emaxs, MPlane p) =>
        p.type < 3
            ? p.dist <= emins[p.type] ? 1 : p.dist >= emaxs[p.type] ? 2 : 3
            : BoxOnPlaneSide(emins, emaxs, p);

    // mathlib.c: AngleVectors
    public static void AngleVectors(in Vec3 angles, ref Vec3 forward, ref Vec3 right, ref Vec3 up)
    {
        float angle;
        float sr, sp, sy, cr, cp, cy;

        angle = (float)(angles[YAW] * (M_PI * 2 / 360));
        sy = (float)Libm.sin(angle);
        cy = (float)Libm.cos(angle);
        angle = (float)(angles[PITCH] * (M_PI * 2 / 360));
        sp = (float)Libm.sin(angle);
        cp = (float)Libm.cos(angle);
        angle = (float)(angles[ROLL] * (M_PI * 2 / 360));
        sr = (float)Libm.sin(angle);
        cr = (float)Libm.cos(angle);

        forward[0] = cp * cy;
        forward[1] = cp * sy;
        forward[2] = -sp;
        right[0] = (-1 * sr * sp * cy) + (-1 * cr * -sy);
        right[1] = (-1 * sr * sp * sy) + (-1 * cr * cy);
        right[2] = -1 * sr * cp;
        up[0] = (cr * sp * cy) + (-sr * -sy);
        up[1] = (cr * sp * sy) + (-sr * cy);
        up[2] = cr * cp;
    }

    // mathlib.c: VectorCompare
    public static int VectorCompare(in Vec3 v1, in Vec3 v2)
    {
        for (int i = 0; i < 3; i++)
        {
            if (v1[i] != v2[i])
            {
                return 0;
            }
        }

        return 1;
    }

    // mathlib.c: VectorMA
    public static void VectorMA(in Vec3 veca, float scale, in Vec3 vecb, ref Vec3 vecc)
    {
        vecc[0] = veca[0] + (scale * vecb[0]);
        vecc[1] = veca[1] + (scale * vecb[1]);
        vecc[2] = veca[2] + (scale * vecb[2]);
    }

    // mathlib.c: CrossProduct
    public static void CrossProduct(in Vec3 v1, in Vec3 v2, ref Vec3 cross)
    {
        cross[0] = (v1[1] * v2[2]) - (v1[2] * v2[1]);
        cross[1] = (v1[2] * v2[0]) - (v1[0] * v2[2]);
        cross[2] = (v1[0] * v2[1]) - (v1[1] * v2[0]);
    }

    // mathlib.c: Length
    public static float Length(in Vec3 v)
    {
        int i;
        float length;

        length = 0;
        for (i = 0; i < 3; i++)
        {
            length += v[i] * v[i];
        }

        length = (float)Math.Sqrt(length); // FIXME

        return length;
    }

    // mathlib.c: VectorNormalize (returns the vector's length)
    public static float VectorNormalize(ref Vec3 v)
    {
        float length, ilength;

        length = (v[0] * v[0]) + (v[1] * v[1]) + (v[2] * v[2]);
        length = (float)Math.Sqrt(length); // FIXME

        if (length != 0)
        {
            ilength = 1 / length;
            v[0] *= ilength;
            v[1] *= ilength;
            v[2] *= ilength;
        }

        return length;
    }

    // mathlib.c: VectorInverse
    public static void VectorInverse(ref Vec3 v)
    {
        v[0] = -v[0];
        v[1] = -v[1];
        v[2] = -v[2];
    }

    // mathlib.c: VectorScale
    public static void VectorScale(in Vec3 @in, float scale, ref Vec3 @out)
    {
        @out[0] = @in[0] * scale;
        @out[1] = @in[1] * scale;
        @out[2] = @in[2] * scale;
    }

    // mathlib.c: Q_log2
    public static int Q_log2(int val)
    {
        int answer = 0;
        while ((val >>= 1) != 0)
        {
            answer++;
        }

        return answer;
    }

    // mathlib.c: R_ConcatRotations
    public static void R_ConcatRotations(in Mat3 in1, in Mat3 in2, ref Mat3 @out)
    {
        @out[0][0] = (in1[0][0] * in2[0][0]) + (in1[0][1] * in2[1][0]) + (in1[0][2] * in2[2][0]);
        @out[0][1] = (in1[0][0] * in2[0][1]) + (in1[0][1] * in2[1][1]) + (in1[0][2] * in2[2][1]);
        @out[0][2] = (in1[0][0] * in2[0][2]) + (in1[0][1] * in2[1][2]) + (in1[0][2] * in2[2][2]);
        @out[1][0] = (in1[1][0] * in2[0][0]) + (in1[1][1] * in2[1][0]) + (in1[1][2] * in2[2][0]);
        @out[1][1] = (in1[1][0] * in2[0][1]) + (in1[1][1] * in2[1][1]) + (in1[1][2] * in2[2][1]);
        @out[1][2] = (in1[1][0] * in2[0][2]) + (in1[1][1] * in2[1][2]) + (in1[1][2] * in2[2][2]);
        @out[2][0] = (in1[2][0] * in2[0][0]) + (in1[2][1] * in2[1][0]) + (in1[2][2] * in2[2][0]);
        @out[2][1] = (in1[2][0] * in2[0][1]) + (in1[2][1] * in2[1][1]) + (in1[2][2] * in2[2][1]);
        @out[2][2] = (in1[2][0] * in2[0][2]) + (in1[2][1] * in2[1][2]) + (in1[2][2] * in2[2][2]);
    }

    // mathlib.c: R_ConcatTransforms
    public static void R_ConcatTransforms(in Mat3x4 in1, in Mat3x4 in2, ref Mat3x4 @out)
    {
        @out[0][0] = (in1[0][0] * in2[0][0]) + (in1[0][1] * in2[1][0]) + (in1[0][2] * in2[2][0]);
        @out[0][1] = (in1[0][0] * in2[0][1]) + (in1[0][1] * in2[1][1]) + (in1[0][2] * in2[2][1]);
        @out[0][2] = (in1[0][0] * in2[0][2]) + (in1[0][1] * in2[1][2]) + (in1[0][2] * in2[2][2]);
        @out[0][3] = (in1[0][0] * in2[0][3]) + (in1[0][1] * in2[1][3]) + (in1[0][2] * in2[2][3]) + in1[0][3];
        @out[1][0] = (in1[1][0] * in2[0][0]) + (in1[1][1] * in2[1][0]) + (in1[1][2] * in2[2][0]);
        @out[1][1] = (in1[1][0] * in2[0][1]) + (in1[1][1] * in2[1][1]) + (in1[1][2] * in2[2][1]);
        @out[1][2] = (in1[1][0] * in2[0][2]) + (in1[1][1] * in2[1][2]) + (in1[1][2] * in2[2][2]);
        @out[1][3] = (in1[1][0] * in2[0][3]) + (in1[1][1] * in2[1][3]) + (in1[1][2] * in2[2][3]) + in1[1][3];
        @out[2][0] = (in1[2][0] * in2[0][0]) + (in1[2][1] * in2[1][0]) + (in1[2][2] * in2[2][0]);
        @out[2][1] = (in1[2][0] * in2[0][1]) + (in1[2][1] * in2[1][1]) + (in1[2][2] * in2[2][1]);
        @out[2][2] = (in1[2][0] * in2[0][2]) + (in1[2][1] * in2[1][2]) + (in1[2][2] * in2[2][2]);
        @out[2][3] = (in1[2][0] * in2[0][3]) + (in1[2][1] * in2[1][3]) + (in1[2][2] * in2[2][3]) + in1[2][3];
    }

    // mathlib.c: FloorDivMod
    // Returns mathematically correct (floor-based) quotient and remainder for numer and denom, both of which should
    // contain no fractional part. The quotient must fit in 32 bits.
    public static void FloorDivMod(double numer, double denom, out int quotient, out int rem)
    {
        int q, r;
        double x;

        if (denom <= 0.0)
        {
            // the C passes the double to %d, which prints its low 32 bits
            throw new QuakeException($"FloorDivMod: bad denominator {(int)BitConverter.DoubleToInt64Bits(denom)}\n");
        }

        if (numer >= 0.0)
        {
            x = Math.Floor(numer / denom);
            q = X86.FtoI(x);
            r = X86.FtoI(Math.Floor(numer - (x * denom)));
        }
        else
        {
            // perform operations with positive values, and fix mod to make floor-based
            x = Math.Floor(-numer / denom);
            q = -X86.FtoI(x);
            r = X86.FtoI(Math.Floor(-numer - (x * denom)));
            if (r != 0)
            {
                q--;
                r = X86.FtoI(denom) - r;
            }
        }

        quotient = q;
        rem = r;
    }

    // mathlib.c: GreatestCommonDivisor
    public static int GreatestCommonDivisor(int i1, int i2)
    {
        if (i1 > i2)
        {
            if (i2 == 0)
            {
                return i1;
            }

            return GreatestCommonDivisor(i2, i1 % i2);
        }
        else
        {
            if (i1 == 0)
            {
                return i2;
            }

            return GreatestCommonDivisor(i1, i2 % i1);
        }
    }

    // mathlib.c: Invert24To16 (inverts an 8.24 value to a 16.16 value)
    public static int Invert24To16(int val)
    {
        if (val < 256)
        {
            return unchecked((int)0xFFFFFFFF);
        }

        return X86.FtoI(((double)0x10000 * (double)0x1000000 / val) + 0.5);
    }
}
