using QuakeSharp.Core.Common;
using QuakeSharp.Core.Enhanced;

namespace QuakeSharp.Core.Tests;

/// <summary>The enhanced view's projection against the software renderer's (r_misc.c's R_ViewChanged, design.md §11.6).</summary>
public class EnhancedCameraTests
{
    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(-20f, 135f, 0f)]
    [InlineData(10f, 270f, 15f)]
    public void APointFallsWhereTheSoftwareProjectsIt(float pitch, float yaw, float roll)
    {
        // the 320x200 frame's view of 320x152 at fov 90, its pixels 0.8333 as wide as tall (vid.aspect)
        var view = new EnhancedView(0, 0, 320, 152, new Vec3(10, 20, 30), new Vec3(pitch, yaw, roll), 90, 0, 0);
        const float pixelAspect = 200f / 320f * 320f / 240f;
        float[] m = EnhancedCamera.ViewProjection(view, pixelAspect, 320 * pixelAspect / 152);
        (double[] f, double[] r, double[] u) = EnhancedCamera.AngleVectors(pitch, yaw, roll);

        // a point 100 ahead, 30 to the right, 12 up
        double[] p = [.. Enumerable.Range(0, 3).Select(i => view.Origin[i] + (100 * f[i]) + (30 * r[i]) + (12 * u[i]))];

        // the software: xscale = width / horizontalFieldOfView, yscale = xscale * pixelAspect
        double horizontal = 2.0 * Math.Tan(Math.PI / 4);
        double xscale = 320 / horizontal, yscale = xscale * pixelAspect;
        double ndcX = xscale * 30 / 100 / 160, ndcY = -yscale * 12 / 100 / 76;

        double w = Row(m, 3, p);
        Assert.Equal(ndcX, Row(m, 0, p) / w, 5);
        Assert.Equal(ndcY, Row(m, 1, p) / w, 5);
        Assert.Equal(EnhancedCamera.Near / 100, Row(m, 2, p) / w, 5);
    }

    [Fact]
    public void AWiderRectangleSeesMoreToTheSidesAndTheSameUpAndDown()
    {
        var view = new EnhancedView(0, 0, 320, 192, default, default, 90, 0, 0);
        float[] narrow = EnhancedCamera.ViewProjection(view, 1, 320.0 / 192);
        float[] wide = EnhancedCamera.ViewProjection(view, 1, 2 * 320.0 / 192);

        double[] p = [100, -40, 25];
        Assert.Equal(Row(narrow, 0, p) / 2, Row(wide, 0, p), 4);
        Assert.Equal(Row(narrow, 1, p), Row(wide, 1, p), 4);
    }

    private static double Row(float[] m, int row, double[] p) =>
        (m[row * 4] * p[0]) + (m[(row * 4) + 1] * p[1]) + (m[(row * 4) + 2] * p[2]) + m[(row * 4) + 3];
}
