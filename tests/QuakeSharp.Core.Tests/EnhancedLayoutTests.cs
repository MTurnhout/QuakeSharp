using QuakeSharp.Core.Common;
using QuakeSharp.Core.Enhanced;

namespace QuakeSharp.Core.Tests;

/// <summary>Where the enhanced frame's 2D and 3D go in the window (design.md §11.6).</summary>
public class EnhancedLayoutTests
{
    [Fact]
    public void TheViewFallsWhereItsRectangleIsOnTheFrame()
    {
        // 320x240 shown 4:3 in 1280x960 (four times), the view 320x192 at the top (the status bar below it)
        EnhancedLayout layout = EnhancedLayout.Compute(Frame(0, 0, 320, 192), 1280, 960, squarePixels: false, widescreen: true);

        Assert.Equal((0, 0, 1280, 960), layout.Frame);
        Assert.Equal(4, layout.Scale);
        Assert.Equal((0, 0, 1280, 768), layout.View);
        Assert.False(layout.Widened);
    }

    [Fact]
    public void AWideWindowWidensAViewAsWideAsTheFrame()
    {
        EnhancedLayout layout = EnhancedLayout.Compute(Frame(0, 0, 320, 192), 1920, 1080, squarePixels: false, widescreen: true);

        Assert.Equal((240, 0, 1440, 1080), layout.Frame);
        Assert.Equal((0, 0, 1920, 864), layout.View);
        Assert.True(layout.Widened);
    }

    [Fact]
    public void ASmallerViewOrWidescreenOffIsNotWidened()
    {
        EnhancedLayout small = EnhancedLayout.Compute(Frame(32, 24, 256, 144), 1920, 1080, squarePixels: false, widescreen: true);
        EnhancedLayout off = EnhancedLayout.Compute(Frame(0, 0, 320, 192), 1920, 1080, squarePixels: false, widescreen: false);

        Assert.Equal((240 + 144, 108, 1152, 648), small.View);
        Assert.False(small.Widened);
        Assert.Equal((240, 0, 1440, 864), off.View);
        Assert.False(off.Widened);
    }

    [Fact]
    public void WithoutAViewThereIsNone()
    {
        EnhancedFrame frame = Frame(0, 0, 320, 192);
        EnhancedLayout layout = EnhancedLayout.Compute(
            EnhancedFrame.Lerp(frame, Copy(frame, viewDrawn: false), 1), 1920, 1080, squarePixels: false, widescreen: true);

        Assert.False(layout.HasView);
    }

    private static EnhancedFrame Frame(int x, int y, int width, int height) => Copy(new EnhancedFrame
    {
        Width = 320,
        Height = 240,
        Pixels = new byte[320 * 240],
        Mask = new byte[320 * 240],
        Palette = new byte[768],
        ChannelTables = new byte[768],
        Time = 0,
        ViewDrawn = true,
        View = new EnhancedView(x, y, width, height, default(Vec3), default(Vec3), 90, 73.74f, 0),
        InLevel = true,
        World = 1,
        Entities = [],
        Particles = [],
        Dlights = [],
        LightStyles = new int[64],
        DrawEntities = true,
        Fullbright = false,
        WaterWarp = true,
        ClearColor = 2,
    }, viewDrawn: true);

    private static EnhancedFrame Copy(EnhancedFrame f, bool viewDrawn) => new()
    {
        Width = f.Width,
        Height = f.Height,
        Pixels = f.Pixels,
        Mask = f.Mask,
        Palette = f.Palette,
        ChannelTables = f.ChannelTables,
        Time = f.Time,
        ViewDrawn = viewDrawn,
        View = f.View,
        InLevel = f.InLevel,
        World = f.World,
        Entities = f.Entities,
        Particles = f.Particles,
        Dlights = f.Dlights,
        LightStyles = f.LightStyles,
        DrawEntities = f.DrawEntities,
        Fullbright = f.Fullbright,
        WaterWarp = f.WaterWarp,
        ClearColor = f.ClearColor,
    };
}
