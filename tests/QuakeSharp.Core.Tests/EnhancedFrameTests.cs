using QuakeSharp.Core.Common;
using QuakeSharp.Core.Enhanced;

namespace QuakeSharp.Core.Tests;

/// <summary>
/// The enhanced mode's frames between two snapshots (design.md §11.6): what is found in both moves part way, what
/// teleported, appeared, went or changed its model does not, and the rest is the later frame's.
/// </summary>
public class EnhancedFrameTests
{
    [Theory]
    [InlineData(0f, 10f)]
    [InlineData(0.5f, 15f)]
    [InlineData(1f, 20f)]
    public void AnEntityFoundInBothMovesPartWay(float fraction, float x)
    {
        EnhancedFrame before = Frame(0, [Entity(5, 1, new Vec3(10, 0, 0), new Vec3(0, 350, 0))]);
        EnhancedFrame after = Frame(1, [Entity(5, 1, new Vec3(20, 0, 0), new Vec3(0, 10, 0))]);

        EnhancedFrame between = EnhancedFrame.Lerp(before, after, fraction);

        Assert.Equal(x, between.Entities[0].Origin[0]);
        // 350 to 10 the shorter way round, through 360
        Assert.Equal((350 + (20 * fraction)) % 360, between.Entities[0].Angles[1] % 360, 3);
        Assert.Equal(fraction, between.Time, 6);
    }

    [Fact]
    public void ATeleportIsNotInterpolated()
    {
        EnhancedFrame before = Frame(0, [Entity(5, 1, new Vec3(0, 0, 0), default)]);
        EnhancedFrame after = Frame(1, [Entity(5, 1, new Vec3(0, EnhancedFrame.MaxMove + 1, 0), default)]);

        Assert.Equal(EnhancedFrame.MaxMove + 1, EnhancedFrame.Lerp(before, after, 0.5f).Entities[0].Origin[1]);
    }

    [Fact]
    public void AnotherModelInTheSlotIsNotInterpolated()
    {
        EnhancedFrame before = Frame(0, [Entity(5, 1, new Vec3(0, 0, 0), default)]);
        EnhancedFrame after = Frame(1, [Entity(5, 2, new Vec3(8, 0, 0), default)]);

        Assert.Equal(8, EnhancedFrame.Lerp(before, after, 0.5f).Entities[0].Origin[0]);
    }

    [Fact]
    public void WhatAppearedOrHasNoSlotIsWhereTheLaterFrameHasIt()
    {
        EnhancedFrame before = Frame(0, [Entity(EnhancedEntity.Other, 1, new Vec3(0, 0, 0), default)]);
        EnhancedFrame after = Frame(1,
        [
            Entity(EnhancedEntity.Other, 1, new Vec3(8, 0, 0), default),
            Entity(7, 1, new Vec3(16, 0, 0), default),
        ]);

        EnhancedFrame between = EnhancedFrame.Lerp(before, after, 0.5f);

        Assert.Equal(8, between.Entities[0].Origin[0]);
        Assert.Equal(16, between.Entities[1].Origin[0]);
    }

    [Fact]
    public void AParticleMovesPartWayUnlessItsSlotHoldsAnotherOne()
    {
        EnhancedFrame before = Frame(0, [], [new EnhancedParticle(3, 2, new Vec3(0, 0, 0), 15), new EnhancedParticle(4, 2, default, 15)]);
        EnhancedFrame after = Frame(1, [], [new EnhancedParticle(3, 2, new Vec3(0, 0, 4), 16), new EnhancedParticle(4, 3, new Vec3(0, 0, 4), 15)]);

        EnhancedFrame between = EnhancedFrame.Lerp(before, after, 0.5f);

        Assert.Equal(2, between.Particles[0].Origin[2]);
        Assert.Equal(16, between.Particles[0].Color);
        Assert.Equal(4, between.Particles[1].Origin[2]);
    }

    [Fact]
    public void TheViewMovesPartWay()
    {
        EnhancedFrame before = Frame(0, [], view: new Vec3(0, 0, 0));
        EnhancedFrame after = Frame(1, [], view: new Vec3(0, 6, 0));

        Assert.Equal(3, EnhancedFrame.Lerp(before, after, 0.5f).View.Origin[1]);
    }

    [Fact]
    public void ADirectRectIsDrawnAsTheOverlay()
    {
        EnhancedFrame frame = Frame(0, []);
        // the screen's buffer, 5 bytes a row, with the disc drawn at (1, 1)
        byte[] screen = [9, 9, 9, 9, 9, 9, 1, 2, 9, 9, 9, 3, 4, 9, 9, 9, 9, 9, 9, 9];

        EnhancedFrame shown = frame.WithDirectRect(screen, 5, 1, 1, 2, 2);

        Assert.Equal([0, 0, 0, 0, 0, 1, 2, 0, 0, 3, 4, 0, 0, 0, 0, 0], shown.Pixels);
        Assert.Equal(EnhancedMask.Overlay, shown.Mask[5]);
        Assert.Equal(EnhancedMask.View, shown.Mask[0]);
        Assert.Equal(0, frame.Pixels[5]);
    }

    [Fact]
    public void ThePaletteTablesAreCheckedAgainstThePalette()
    {
        byte[] basePalette = [.. Enumerable.Range(0, 768).Select(i => (byte)(i / 3))];
        byte[] tables = [.. Enumerable.Range(0, 768).Select(i => (byte)(255 - (i % 256)))];
        EnhancedFrame frame = Frame(0, []);
        EnhancedFrame shifted = Copy(frame, tables, [.. basePalette.Select(c => (byte)(255 - c))]);

        Assert.True(shifted.TablesMakePalette(basePalette));
        Assert.False(Copy(frame, tables, basePalette).TablesMakePalette(basePalette));
    }

    private static EnhancedEntity Entity(int id, int model, Vec3 origin, Vec3 angles) =>
        new(id, model, origin, angles, 0, 0, 0, 0, 0);

    private static EnhancedFrame Frame(double time, EnhancedEntity[] entities, EnhancedParticle[]? particles = null,
        Vec3 view = default) => new()
    {
        Width = 4,
        Height = 4,
        Pixels = new byte[16],
        Mask = [.. Enumerable.Repeat(EnhancedMask.View, 16)],
        Palette = new byte[768],
        ChannelTables = new byte[768],
        Time = time,
        ViewDrawn = true,
        View = new EnhancedView(0, 0, 4, 4, view, default, 90, 73.74f, 0),
        InLevel = true,
        World = 1,
        Entities = entities,
        Particles = particles ?? [],
        Dlights = [],
        LightStyles = new int[64],
        DrawEntities = true,
        Fullbright = false,
        WaterWarp = true,
        ClearColor = 2,
    };

    private static EnhancedFrame Copy(EnhancedFrame f, byte[] tables, byte[] palette) => new()
    {
        Width = f.Width,
        Height = f.Height,
        Pixels = f.Pixels,
        Mask = f.Mask,
        Palette = palette,
        ChannelTables = tables,
        Time = f.Time,
        ViewDrawn = f.ViewDrawn,
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
