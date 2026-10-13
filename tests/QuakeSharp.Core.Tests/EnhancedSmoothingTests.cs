using QuakeSharp.Core.Common;
using QuakeSharp.Core.Enhanced;

namespace QuakeSharp.Core.Tests;

/// <summary>The enhanced mode's optional pose blending and step smoothing (design.md §11.6, step 6; decision 64).</summary>
public class EnhancedSmoothingTests
{
    private static readonly EnhancedAliasModel Monster = new()
    {
        Name = "progs/test.mdl",
        Scale = [1, 1, 1],
        ScaleOrigin = [0, 0, 0],
        VertexCount = 1,
        SkinWidth = 8,
        SkinHeight = 8,
        Poses = [0, 0, 0],
        Corners = [],
        Skins = [new byte[64]],
        Frames = [([0], []), ([1], []), ([2], [])],
        SkinGroups = [([0], [])],
    };

    [Fact]
    public void APoseChangeIsBlendedOverTheTimeSinceTheChangeBefore()
    {
        var smoothing = new EnhancedSmoothing { Poses = true };
        smoothing.Observe(Frame(0.0, frame: 0, new Vec3(0, 0, 0)));
        smoothing.Observe(Frame(0.1, frame: 1, new Vec3(0, 0, 0)));

        EnhancedEntity half = smoothing.Apply(Frame(0.15, frame: 1, new Vec3(0, 0, 0))).Entities[0];
        EnhancedEntity done = smoothing.Apply(Frame(0.2, frame: 1, new Vec3(0, 0, 0))).Entities[0];

        Assert.Equal(0, half.PoseFrom);
        Assert.Equal(0.5f, half.PoseBlend, 3);
        Assert.Equal(-1, done.PoseFrom);
    }

    [Fact]
    public void AStepAfterStandingStillIsDrawnAlongTheStep()
    {
        var smoothing = new EnhancedSmoothing { Steps = true };
        smoothing.Observe(Frame(0.0, frame: 0, new Vec3(0, 0, 0)));
        smoothing.Observe(Frame(0.05, frame: 0, new Vec3(0, 0, 0)));
        smoothing.Observe(Frame(0.1, frame: 0, new Vec3(8, 0, 0)));

        Assert.Equal(4, smoothing.Apply(Frame(0.15, frame: 0, new Vec3(8, 0, 0))).Entities[0].Origin[0], 3);
        Assert.Equal(8, smoothing.Apply(Frame(0.25, frame: 0, new Vec3(8, 0, 0))).Entities[0].Origin[0]);
    }

    [Fact]
    public void WhatMovesEveryFrameIsLeftAlone()
    {
        var smoothing = new EnhancedSmoothing { Steps = true };
        smoothing.Observe(Frame(0.0, frame: 0, new Vec3(0, 0, 0)));
        smoothing.Observe(Frame(0.0156, frame: 0, new Vec3(2, 0, 0)));
        smoothing.Observe(Frame(0.0313, frame: 0, new Vec3(4, 0, 0)));

        Assert.Equal(4, smoothing.Apply(Frame(0.0313, frame: 0, new Vec3(4, 0, 0))).Entities[0].Origin[0]);
    }

    [Fact]
    public void OffChangesNothing()
    {
        var smoothing = new EnhancedSmoothing();
        smoothing.Observe(Frame(0.0, frame: 0, new Vec3(0, 0, 0)));
        smoothing.Observe(Frame(0.1, frame: 1, new Vec3(8, 0, 0)));
        EnhancedFrame frame = Frame(0.15, frame: 1, new Vec3(8, 0, 0));

        Assert.Same(frame, smoothing.Apply(frame));
    }

    private static EnhancedFrame Frame(double time, int frame, Vec3 origin) => new()
    {
        Width = 4,
        Height = 4,
        Pixels = new byte[16],
        Mask = new byte[16],
        Palette = new byte[768],
        ChannelTables = new byte[768],
        Time = time,
        ViewDrawn = true,
        View = new EnhancedView(0, 0, 4, 4, default, default, 90, 73.74f, 0),
        InLevel = true,
        World = 0,
        Models = [null, new EnhancedModel { Name = Monster.Name, Alias = Monster }],
        Entities = [new EnhancedEntity(5, 1, origin, default, frame, 0, 0, 0, 0)],
        Particles = [],
        Dlights = [],
        LightStyles = new int[64],
        DrawEntities = true,
        Fullbright = false,
        WaterWarp = true,
        ClearColor = 2,
    };
}
