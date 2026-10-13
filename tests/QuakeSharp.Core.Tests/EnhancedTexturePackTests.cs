using QuakeSharp.Core.Enhanced;

namespace QuakeSharp.Core.Tests;

/// <summary>Finding a texture pack's files (design.md §11.7, decision 68).</summary>
public class EnhancedTexturePackTests
{
    [Fact]
    public void TheFirstDirectorysFileWins()
    {
        using var files = new TestFiles();
        string user = Path.Combine(files.Root, "user", "id1");
        string quake = Path.Combine(files.Root, "quake", "id1");
        files.Write("user/id1/textures/+0slip.tga", [1]);
        files.Write("quake/id1/textures/+0slip.png", [2]);
        files.Write("quake/id1/textures/sky4.png", [3]);

        var pack = new EnhancedTexturePack([user, quake]);

        Assert.Equal(Path.Combine(user, "textures", "+0slip.tga"), pack.Find("textures/+0slip"));
        Assert.Equal(Path.Combine(quake, "textures", "sky4.png"), pack.Find("textures/sky4"));
        Assert.Equal(2, pack.Count);
    }

    [Fact]
    public void PngBeforeTgaInOneDirectory()
    {
        using var files = new TestFiles();
        files.Write("id1/progs/player.mdl_0.tga", [1]);
        files.Write("id1/progs/player.mdl_0.png", [2]);

        var pack = new EnhancedTexturePack([Path.Combine(files.Root, "id1")]);

        Assert.EndsWith("player.mdl_0.png", pack.Find("progs/player.mdl_0"));
    }

    [Fact]
    public void AMissionPacksOwnBeforeId1s()
    {
        using var files = new TestFiles();
        files.Write("hipnotic/textures/metal5_2.png", [1]);
        files.Write("id1/textures/metal5_2.png", [2]);
        files.Write("id1/textures/metal5_4.png", [3]);

        var pack = new EnhancedTexturePack([Path.Combine(files.Root, "hipnotic"), Path.Combine(files.Root, "id1")]);

        Assert.Contains("hipnotic", pack.Find("textures/metal5_2"));
        Assert.Contains("id1", pack.Find("textures/metal5_4"));
    }

    [Fact]
    public void NamesAreFoundWhateverTheirCase()
    {
        using var files = new TestFiles();
        files.Write("id1/Textures/E1M1/+0SLIP.PNG", [1]);
        files.Write("id1/PROGS/Player.mdl_0.Tga", [2]);

        var pack = new EnhancedTexturePack([Path.Combine(files.Root, "id1")]);

        Assert.NotNull(pack.Find("textures/e1m1/+0slip"));
        Assert.NotNull(pack.Find("TEXTURES/e1m1/+0Slip"));
        Assert.NotNull(pack.Find("progs/player.mdl_0"));
    }

    [Fact]
    public void AWorldTextureIsTheMapsOwnThenEveryMaps()
    {
        using var files = new TestFiles();
        files.Write("id1/textures/e1m1/sky4.png", [1]);
        files.Write("id1/textures/sky4.png", [2]);
        files.Write("id1/textures/#water0.png", [3]);

        var pack = new EnhancedTexturePack([Path.Combine(files.Root, "id1")]);

        Assert.Equal("textures/e1m1/sky4", pack.FindWorld("e1m1", "sky4")?.Name);
        Assert.Equal("textures/sky4", pack.FindWorld("e1m5", "sky4")?.Name);
        Assert.Equal("textures/#water0", pack.FindWorld("e1m1", "*water0")?.Name);
        Assert.Null(pack.FindWorld("e1m1", "+0slip"));
    }

    [Fact]
    public void GlowThenLuma()
    {
        using var files = new TestFiles();
        files.Write("id1/textures/a_glow.png", [1]);
        files.Write("id1/textures/a_luma.png", [2]);
        files.Write("id1/textures/b_luma.tga", [3]);
        files.Write("id1/textures/b_norm.png", [4]);

        var pack = new EnhancedTexturePack([Path.Combine(files.Root, "id1")]);

        Assert.EndsWith("a_glow.png", pack.FindLayer("textures/a", EnhancedPackNames.Glow));
        Assert.EndsWith("b_luma.tga", pack.FindLayer("textures/b", EnhancedPackNames.Glow));
        Assert.EndsWith("b_norm.png", pack.FindLayer("textures/b", EnhancedPackNames.Norm));
        Assert.Null(pack.FindLayer("textures/b", EnhancedPackNames.Gloss));
    }

    [Fact]
    public void OnlyPngAndTgaUnderTexturesAndProgs()
    {
        using var files = new TestFiles();
        files.Write("id1/textures/a.jpg", [1]);
        files.Write("id1/textures/readme.txt", [1]);
        files.Write("id1/maps/b.png", [1]);
        files.Write("id1/progs/player.mdl", [1]);

        var pack = new EnhancedTexturePack([Path.Combine(files.Root, "id1"), Path.Combine(files.Root, "missing")]);

        Assert.Equal(0, pack.Count);
    }
}
