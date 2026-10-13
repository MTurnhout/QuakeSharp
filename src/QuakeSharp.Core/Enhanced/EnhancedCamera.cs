namespace QuakeSharp.Core.Enhanced;

/// <summary>
/// The enhanced view's projection (design.md §11.6): the software renderer's (r_misc.c's R_ViewChanged: its
/// horizontal field of view from fov_x, the vertical one from the view's shape and vid.aspect), with the vertical
/// field kept and the horizontal one following the shape of the view's rectangle of the window (Hor+), and Vulkan's
/// clip space with reversed depth (the near plane at 1, the far plane at infinity: depth 1 near, 0 far away).
/// </summary>
public static class EnhancedCamera
{
    /// <summary>The near plane's distance (Quake's units).</summary>
    public const float Near = 1;

    /// <summary>
    /// mathlib.c's AngleVectors (pitch, yaw, roll in degrees): the view's forward, right and up, in double precision
    /// (the enhanced mode's own arithmetic; nothing of it goes back into the game).
    /// </summary>
    public static (double[] Forward, double[] Right, double[] Up) AngleVectors(float pitch, float yaw, float roll)
    {
        double y = yaw * Math.PI / 180, p = pitch * Math.PI / 180, r = roll * Math.PI / 180;
        double sy = Math.Sin(y), cy = Math.Cos(y), sp = Math.Sin(p), cp = Math.Cos(p), sr = Math.Sin(r), cr = Math.Cos(r);
        double[] forward = [cp * cy, cp * sy, -sp];
        double[] right = [(-sr * sp * cy) + (cr * sy), (-sr * sp * sy) - (cr * cy), -sr * cp];
        double[] up = [(cr * sp * cy) + (sr * sy), (cr * sp * sy) - (sr * cy), cr * cp];
        return (forward, right, up);
    }

    /// <summary>The tangent of half the software view's vertical field of view (R_ViewChanged's verticalFieldOfView / 2).</summary>
    public static double TanHalfVertical(EnhancedView view, float pixelAspect)
    {
        double horizontal = 2.0 * Math.Tan(view.FovX / 360.0 * Math.PI);
        double screenAspect = view.Width * pixelAspect / view.Height;
        return horizontal / screenAspect / 2;
    }

    /// <summary>
    /// The view's world-to-clip transform as four rows (clip = row · (x, y, z, 1)) for a view rectangle of the window
    /// <paramref name="aspect"/> wide for 1 high.
    /// </summary>
    public static float[] ViewProjection(EnhancedView view, float pixelAspect, double aspect)
    {
        (double[] f, double[] r, double[] u) = AngleVectors(view.Angles[0], view.Angles[1], view.Angles[2]);
        double tanY = TanHalfVertical(view, pixelAspect);
        double tanX = tanY * aspect;
        double ox = view.Origin[0], oy = view.Origin[1], oz = view.Origin[2];
        double Dot(double[] a) => (a[0] * ox) + (a[1] * oy) + (a[2] * oz);

        return
        [
            (float)(r[0] / tanX), (float)(r[1] / tanX), (float)(r[2] / tanX), (float)(-Dot(r) / tanX),
            (float)(-u[0] / tanY), (float)(-u[1] / tanY), (float)(-u[2] / tanY), (float)(Dot(u) / tanY),
            0, 0, 0, Near,
            (float)f[0], (float)f[1], (float)f[2], (float)-Dot(f),
        ];
    }

    /// <summary>
    /// A brush entity's model-to-world transform as three rows (world = row · (x, y, z, 1)): r_bsp.c's R_RotateBmodel
    /// turns the view into the entity's space by entity_rotation (roll · pitch · yaw), so its transpose turns the
    /// model's points into the world's, then the entity's origin.
    /// </summary>
    public static float[] BrushTransform(Common.Vec3 origin, Common.Vec3 angles)
    {
        double y = angles[1] * Math.PI / 180, p = angles[0] * Math.PI / 180, r = angles[2] * Math.PI / 180;
        double[,] yaw = { { Math.Cos(y), Math.Sin(y), 0 }, { -Math.Sin(y), Math.Cos(y), 0 }, { 0, 0, 1 } };
        double[,] pitch = { { Math.Cos(p), 0, -Math.Sin(p) }, { 0, 1, 0 }, { Math.Sin(p), 0, Math.Cos(p) } };
        double[,] roll = { { 1, 0, 0 }, { 0, Math.Cos(r), Math.Sin(r) }, { 0, -Math.Sin(r), Math.Cos(r) } };
        double[,] e = Multiply(roll, Multiply(pitch, yaw));
        return
        [
            (float)e[0, 0], (float)e[1, 0], (float)e[2, 0], origin[0],
            (float)e[0, 1], (float)e[1, 1], (float)e[2, 1], origin[1],
            (float)e[0, 2], (float)e[1, 2], (float)e[2, 2], origin[2],
        ];
    }

    private static double[,] Multiply(double[,] a, double[,] b)
    {
        var m = new double[3, 3];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                m[i, j] = (a[i, 0] * b[0, j]) + (a[i, 1] * b[1, j]) + (a[i, 2] * b[2, j]);
            }
        }

        return m;
    }
}
