using QuakeSharp.Core.Enhanced;

namespace QuakeSharp.Core.Tests;

/// <summary>A texture pack's file names (design.md §11.7, decision 68).</summary>
public class EnhancedPackNamesTests
{
    [Fact]
    public void AWorldTextureIsTheMapsOwnThenEveryMaps()
    {
        Assert.Equal(["textures/e1m1/+0slip", "textures/+0slip"], EnhancedPackNames.WorldTexture("e1m1", "+0slip"));
    }

    [Fact]
    public void TurbulentTexturesUseAHash()
    {
        Assert.Equal("textures/#water0", EnhancedPackNames.SharedTexture("*water0"));
        Assert.Equal("textures/e2m3/#lava1", EnhancedPackNames.MapTexture("e2m3", "*lava1"));
        Assert.True(EnhancedPackNames.IsTurbulent("*water0"));
        Assert.False(EnhancedPackNames.IsTurbulent("#water0"));
    }

    [Fact]
    public void SkinsAreNumberedAfterTheModelsName()
    {
        Assert.Equal("progs/player.mdl_0", EnhancedPackNames.Skin("progs/player.mdl", 0));
        Assert.Equal("progs/flame2.mdl_1_3", EnhancedPackNames.Skin("progs/flame2.mdl", 1, 3));
        Assert.Equal("progs/player.mdl_0_glow", EnhancedPackNames.Skin("progs/player.mdl", 0) + EnhancedPackNames.Glow);
    }

    [Fact]
    public void SkiesAreTexturesNamedSky()
    {
        Assert.True(EnhancedPackNames.IsSky("sky1"));
        Assert.True(EnhancedPackNames.IsSky("sky4"));
        Assert.False(EnhancedPackNames.IsSky("skip"));
    }
}
