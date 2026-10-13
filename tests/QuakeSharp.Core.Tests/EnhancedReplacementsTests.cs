using System.Buffers.Binary;
using QuakeSharp.Core.Enhanced;

namespace QuakeSharp.Core.Tests;

/// <summary>A pack's world textures made ready for the GPU (design.md §11.7, decisions 69 and 70).</summary>
public class EnhancedReplacementsTests
{
    [Fact]
    public void NoReplacementNoColours()
    {
        using var files = new TestFiles();
        var pack = new EnhancedTexturePack([Path.Combine(files.Root, "id1")]);

        Assert.Null(EnhancedReplacements.World(pack, "e1m1", Texture("+0slip", 2, 2, [0, 0, 0, 0])));
    }

    [Fact]
    public void WithoutAGlowTheGamesFullbrightTexelsScaled()
    {
        using var files = new TestFiles();
        files.Write("id1/textures/lamp.tga", Tga(4, 4, Fill(16, 10, 20, 30, 255)));
        var pack = new EnhancedTexturePack([Path.Combine(files.Root, "id1")]);

        // the game's 2x2: its top right texel fullbright (palette 224-255)
        List<(int Width, int Height, byte[] Rgba)> mips = EnhancedReplacements.World(pack, "e1m1", Texture("lamp", 2, 2, [1, 230, 2, 3]))!;

        (int w, int h, byte[] rgba) = mips[0];
        Assert.Equal((4, 4), (w, h));
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int i = ((y * 4) + x) * 4;
                Assert.Equal<byte>([10, 20, 30], rgba[i..(i + 3)]);
                Assert.Equal(x >= 2 && y < 2 ? 255 : 0, rgba[i + 3]);
            }
        }

        Assert.Equal([(4, 4), (2, 2), (1, 1)], mips.Select(m => (m.Width, m.Height)));
    }

    [Fact]
    public void AGlowIsAddedAndWeighsTheFullbright()
    {
        using var files = new TestFiles();
        files.Write("id1/textures/lamp.tga", Tga(2, 1, [100, 100, 100, 255, 100, 100, 100, 255]));
        files.Write("id1/textures/lamp_luma.tga", Tga(2, 1, [255, 0, 0, 255, 0, 0, 0, 255]));
        var pack = new EnhancedTexturePack([Path.Combine(files.Root, "id1")]);

        byte[] rgba = EnhancedReplacements.World(pack, "e1m1", Texture("lamp", 2, 1, [0, 0]))![0].Rgba;

        // the glowing texel: the glow's colour, unlit; the other the replacement's, lit
        Assert.Equal<byte>([255, 0, 0, 255, 100, 100, 100, 0], rgba);
    }

    [Fact]
    public void ASkyKeepsItsFrontLayersAlphaAndOneLevel()
    {
        using var files = new TestFiles();
        files.Write("id1/textures/sky4.tga", Tga(4, 1, [1, 1, 1, 0, 2, 2, 2, 255, 3, 3, 3, 0, 4, 4, 4, 0]));
        var pack = new EnhancedTexturePack([Path.Combine(files.Root, "id1")]);

        List<(int Width, int Height, byte[] Rgba)> mips = EnhancedReplacements.World(pack, "e1m1", Texture("sky4", 4, 1, [0, 0, 0, 0]))!;

        Assert.Single(mips);
        Assert.Equal<byte>([0, 255, 255, 255], [.. Enumerable.Range(0, 4).Select(i => mips[0].Rgba[(i * 4) + 3])]);
    }

    [Fact]
    public void AFileThatDoesNotDecodeIsNoReplacement()
    {
        using var files = new TestFiles();
        files.Write("id1/textures/+0slip.png", [1, 2, 3]);
        var pack = new EnhancedTexturePack([Path.Combine(files.Root, "id1")]);

        Assert.Null(EnhancedReplacements.World(pack, "e1m1", Texture("+0slip", 2, 2, [0, 0, 0, 0])));
    }

    [Fact]
    public void TheTranslationsRowsAsClNewTranslationMovesThem()
    {
        Assert.Equal(31, EnhancedReplacements.Translate(31, 16, -1));
        Assert.Equal(79, EnhancedReplacements.Translate(31, 16, 64));
        Assert.Equal(128, EnhancedReplacements.Translate(31, 16, 128));
        Assert.Equal(143, EnhancedReplacements.Translate(16, 16, 128));
        Assert.Equal(111, EnhancedReplacements.Translate(111, 96, 96));
    }

    [Fact]
    public void ASkinWithoutAPlayersColours()
    {
        using var files = new TestFiles();
        files.Write("id1/progs/ogre.mdl_0.tga", Tga(2, 1, [10, 20, 30, 255, 40, 50, 60, 255]));
        var pack = new EnhancedTexturePack([Path.Combine(files.Root, "id1")]);

        EnhancedSkinLayers layers = EnhancedReplacements.SkinLayers(pack, "progs/ogre.mdl", 0, -1)!;
        byte[] rgba = EnhancedReplacements.Skin(layers, [1, 250], 2, 1, Palette(), -1, -1)![0].Rgba;

        // the game's second texel fullbright
        Assert.Equal<byte>([10, 20, 30, 0, 40, 50, 60, 255], rgba);
        Assert.Null(EnhancedReplacements.SkinLayers(pack, "progs/ogre.mdl", 1, -1));
    }

    [Fact]
    public void APlayersColoursTintTheShirtAndPants()
    {
        using var files = new TestFiles();
        files.Write("id1/progs/player.mdl_0.tga", Tga(2, 1, [5, 5, 5, 255, 0, 0, 0, 255]));
        files.Write("id1/progs/player.mdl_0_shirt.tga", Tga(2, 1, [255, 255, 255, 255, 0, 0, 0, 255]));
        files.Write("id1/progs/player.mdl_0_pants.tga", Tga(2, 1, [0, 0, 0, 255, 102, 102, 102, 255]));
        var pack = new EnhancedTexturePack([Path.Combine(files.Root, "id1")]);
        EnhancedSkinLayers layers = EnhancedReplacements.SkinLayers(pack, "progs/player.mdl", 0, -1)!;

        // a palette of greys, index i (i, i, i): a range's entry j at 255 * (range + j) / (range + 15) on its ramp
        byte[] palette = [.. Enumerable.Range(0, 256).SelectMany(i => new[] { (byte)i, (byte)i, (byte)i })];
        byte[] rgba = EnhancedReplacements.Skin(layers, [0, 0], 2, 1, palette, 64, 128)![0].Rgba;

        // the shirt's 255: the ramp's brightest, entry 15, in row 64: 79
        Assert.Equal<byte>([5 + 79, 5 + 79, 5 + 79, 0], rgba[..4]);

        // the pants' 102: darker than the ramp's darkest (entry 0 at 220.5), entry 0 of row 128 (reversed: 143) darkened
        byte dark = (byte)Math.Round(143 * 102 / (255 * 96 / 111.0));
        Assert.Equal<byte>([dark, dark, dark, 0], rgba[4..8]);

        // not translated: the skin's own range, entry 15 is 31
        byte[] own = EnhancedReplacements.Skin(layers, [0, 0], 2, 1, palette, -1, -1)![0].Rgba;
        Assert.Equal(5 + 31, own[0]);
    }

    [Fact]
    public void AGreyBetweenTwoEntriesIsBetweenTheirColours()
    {
        using var files = new TestFiles();
        files.Write("id1/progs/player.mdl_0.tga", Tga(1, 1, [0, 0, 0, 255]));
        files.Write("id1/progs/player.mdl_0_pants.tga", Tga(1, 1, [0, 0, 0, 255]));
        byte[] palette = [.. Enumerable.Range(0, 256).SelectMany(i => new[] { (byte)i, (byte)i, (byte)i })];

        // the shirt half way between entries 4 and 5 (20 and 21 of 31): row 160 reversed gives 171 and 170
        double grey = 255 * 20.5 / 31;
        files.Write("id1/progs/player.mdl_0_shirt.tga", Tga(1, 1, [(byte)Math.Round(grey), (byte)Math.Round(grey), (byte)Math.Round(grey), 255]));
        var pack = new EnhancedTexturePack([Path.Combine(files.Root, "id1")]);
        EnhancedSkinLayers layers = EnhancedReplacements.SkinLayers(pack, "progs/player.mdl", 0, -1)!;

        byte value = EnhancedReplacements.Skin(layers, [0], 1, 1, palette, 160, 0)![0].Rgba[0];

        Assert.InRange(value, 170, 171);
    }

    [Fact]
    public void APlayerWithoutBothLayersIsDrawnWithTheGamesSkin()
    {
        using var files = new TestFiles();
        files.Write("id1/progs/player.mdl_0.tga", Tga(1, 1, [5, 5, 5, 255]));
        files.Write("id1/progs/player.mdl_0_shirt.tga", Tga(1, 1, [255, 255, 255, 255]));
        var pack = new EnhancedTexturePack([Path.Combine(files.Root, "id1")]);
        EnhancedSkinLayers layers = EnhancedReplacements.SkinLayers(pack, "progs/player.mdl", 0, -1)!;

        Assert.Null(EnhancedReplacements.Skin(layers, [0], 1, 1, Palette(), 64, 128));
        Assert.NotNull(EnhancedReplacements.Skin(layers, [0], 1, 1, Palette(), -1, -1));
    }

    // a palette whose every colour differs: index i is (i, 255 - i, i / 2)
    private static byte[] Palette() => [.. Enumerable.Range(0, 256).SelectMany(i => new[] { (byte)i, (byte)(255 - i), (byte)(i / 2) })];

    private static EnhancedTexture Texture(string name, int width, int height, byte[] level0)
    {
        int size = width * height;
        byte[] pixels = [.. level0, .. new byte[(size / 4) + (size / 16) + (size / 64) + 3]];
        return new EnhancedTexture
        {
            Name = name,
            Width = width,
            Height = height,
            Pixels = pixels,
            MipOffsets = [0, size, size + (size / 4), size + (size / 4) + (size / 16)],
        };
    }

    private static byte[] Fill(int count, byte r, byte g, byte b, byte a) =>
        [.. Enumerable.Range(0, count).SelectMany(_ => new[] { r, g, b, a })];

    // A 32-bit TGA from RGBA, top row first
    private static byte[] Tga(int width, int height, byte[] rgba)
    {
        var header = new byte[18];
        header[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), (ushort)height);
        header[16] = 32;
        header[17] = 0x28;
        byte[] bgra = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (rgba[i + 2], rgba[i + 1], rgba[i], rgba[i + 3]);
        }

        return [.. header, .. bgra];
    }
}
