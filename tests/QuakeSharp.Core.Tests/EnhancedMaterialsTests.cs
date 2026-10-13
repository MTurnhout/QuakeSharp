using QuakeSharp.Core.Enhanced;
using static QuakeSharp.Core.Tests.EnhancedReplacementsTests;

namespace QuakeSharp.Core.Tests;

/// <summary>Material maps (design.md §11.7, step 5; decision 71): derived from colours, and a pack's made ready.</summary>
public class EnhancedMaterialsTests
{
    [Fact]
    public void FlatColoursAreAFlatNormal()
    {
        byte[] normals = EnhancedMaterials.Normals(Grey(4, 4, (_, _) => 90), 4, 4, strength: 12, sigma: 1, wrap: true);

        for (int i = 0; i < 16; i++)
        {
            Assert.Equal<byte>([128, 128, 255, 255], normals[(i * 4)..((i * 4) + 4)]);
        }
    }

    [Fact]
    public void BrighterRightwardsFacesLeft()
    {
        byte[] normals = EnhancedMaterials.Normals(Grey(5, 3, (x, _) => x * 40), 5, 3, strength: 1, sigma: 0, wrap: false);

        byte[] middle = normals[(((1 * 5) + 2) * 4)..((((1 * 5) + 2) * 4) + 4)];
        Assert.True(middle[0] < 128, $"x {middle[0]}");
        Assert.Equal(128, middle[1]);
        Assert.True(middle[2] < 255);
        // the slope 40/255 a texel: the normal (-0.157, 0, 1) normalised
        double nx = -40.0 / 255, length = Math.Sqrt((nx * nx) + 1);
        Assert.Equal(Math.Round((nx / length * 127.5) + 127.5), middle[0]);
    }

    [Fact]
    public void BrighterDownwardsFacesUp()
    {
        byte[] normals = EnhancedMaterials.Normals(Grey(3, 5, (_, y) => y * 40), 3, 5, strength: 1, sigma: 0, wrap: false);

        byte[] middle = normals[(((2 * 3) + 1) * 4)..((((2 * 3) + 1) * 4) + 4)];
        Assert.Equal(128, middle[0]);
        Assert.True(middle[1] > 128, $"y {middle[1]}");
    }

    [Fact]
    public void AStrongerStrengthIsSteeper()
    {
        byte[] gentle = EnhancedMaterials.Normals(Grey(5, 3, (x, _) => x * 20), 5, 3, strength: 1, sigma: 0, wrap: false);
        byte[] steep = EnhancedMaterials.Normals(Grey(5, 3, (x, _) => x * 20), 5, 3, strength: 8, sigma: 0, wrap: false);

        int middle = ((1 * 5) + 2) * 4;
        Assert.True(steep[middle] < gentle[middle]);
        Assert.True(steep[middle + 2] < gentle[middle + 2]);
    }

    [Fact]
    public void AWrappedTextureReadsItsOtherSide()
    {
        // a bright first column: the last column's right neighbour when the texture repeats
        byte[] rgba = Grey(4, 3, (x, _) => x == 0 ? 200 : 50);

        byte[] wrapped = EnhancedMaterials.Normals(rgba, 4, 3, strength: 1, sigma: 0, wrap: true);
        byte[] clamped = EnhancedMaterials.Normals(rgba, 4, 3, strength: 1, sigma: 0, wrap: false);

        int last = ((1 * 4) + 3) * 4;
        Assert.True(wrapped[last] < 128, $"wrapped {wrapped[last]}");
        Assert.Equal(128, clamped[last]);
        // the second column falls to the right as much: it faces the other way
        Assert.InRange(wrapped[((1 * 4) + 1) * 4], 254 - wrapped[last], 256 - wrapped[last]);
    }

    [Fact]
    public void GlossIsBrightGreyNotColour()
    {
        byte[] rgba = [255, 255, 255, 255, 100, 100, 100, 255, 255, 0, 0, 255, 200, 150, 100, 255];

        byte[] gloss = EnhancedMaterials.Gloss(rgba, 4, 1, scale: 1);

        Assert.Equal<byte>([255, 255, 255, 255], gloss[0..4]);
        Assert.Equal(Math.Round(100 / 255.0 * 255), gloss[4]);
        Assert.Equal(0, gloss[8]);
        Assert.InRange(gloss[12], 1, gloss[4] - 1); // brighter than the grey, but half its colour
        Assert.Equal(gloss[4], gloss[5]);
        Assert.Equal(255, gloss[7]);
    }

    [Fact]
    public void APacksNormalsAndGlossTogether()
    {
        using var files = new TestFiles();
        files.Write("id1/textures/wall.tga", Tga(2, 2, Fill(4, 50, 50, 50)));
        files.Write("id1/textures/wall_norm.tga", Tga(2, 2, [10, 20, 30, 7, 40, 50, 60, 7, 70, 80, 90, 7, 100, 110, 120, 7]));
        // the gloss at half the size: scaled to the normals', nearest; its brightest channel
        files.Write("id1/textures/wall_gloss.tga", Tga(1, 1, [9, 200, 3, 255]));
        var pack = new EnhancedTexturePack([Path.Combine(files.Root, "id1")]);

        List<(int Width, int Height, byte[] Rgba)> mips = EnhancedMaterials.World(pack, "e1m1", Texture("wall", 1, 1, [0]))!;

        Assert.Equal([(2, 2), (1, 1)], mips.Select(m => (m.Width, m.Height)));
        Assert.Equal<byte>([10, 20, 30, 200, 40, 50, 60, 200, 70, 80, 90, 200, 100, 110, 120, 200], mips[0].Rgba);
    }

    [Fact]
    public void OnlyAGlossIsAFlatNormalOnlyANormalNoGloss()
    {
        using var files = new TestFiles();
        files.Write("id1/textures/a.tga", Tga(1, 1, Fill(1, 50, 50, 50)));
        files.Write("id1/textures/a_gloss.tga", Tga(1, 1, [80, 80, 80, 255]));
        files.Write("id1/textures/b.tga", Tga(1, 1, Fill(1, 50, 50, 50)));
        files.Write("id1/textures/b_norm.tga", Tga(1, 1, [1, 2, 3, 255]));
        var pack = new EnhancedTexturePack([Path.Combine(files.Root, "id1")]);

        Assert.Equal<byte>([128, 128, 255, 80], EnhancedMaterials.World(pack, "e1m1", Texture("a", 1, 1, [0]))![0].Rgba);
        Assert.Equal<byte>([1, 2, 3, 0], EnhancedMaterials.World(pack, "e1m1", Texture("b", 1, 1, [0]))![0].Rgba);
    }

    [Fact]
    public void NoMaterialWithoutLayersTheirTextureOrLight()
    {
        using var files = new TestFiles();
        files.Write("id1/textures/plain.tga", Tga(1, 1, Fill(1, 50, 50, 50)));
        files.Write("id1/textures/orphan_norm.tga", Tga(1, 1, [1, 2, 3, 255]));
        files.Write("id1/textures/sky4.tga", Tga(2, 1, Fill(2, 50, 50, 50)));
        files.Write("id1/textures/sky4_norm.tga", Tga(2, 1, Fill(2, 128, 128, 255)));
        files.Write("id1/textures/#water0.tga", Tga(1, 1, Fill(1, 50, 50, 50)));
        files.Write("id1/textures/#water0_norm.tga", Tga(1, 1, [128, 128, 255, 255]));
        var pack = new EnhancedTexturePack([Path.Combine(files.Root, "id1")]);

        Assert.Null(EnhancedMaterials.World(pack, "e1m1", Texture("plain", 1, 1, [0])));
        Assert.Null(EnhancedMaterials.World(pack, "e1m1", Texture("orphan", 1, 1, [0])));
        Assert.Null(EnhancedMaterials.World(pack, "e1m1", Texture("sky4", 2, 1, [0, 0])));
        Assert.Null(EnhancedMaterials.World(pack, "e1m1", Texture("*water0", 1, 1, [0])));
    }

    [Fact]
    public void AMapsOwnTexturesLayers()
    {
        using var files = new TestFiles();
        files.Write("id1/textures/wall.tga", Tga(1, 1, Fill(1, 50, 50, 50)));
        files.Write("id1/textures/wall_norm.tga", Tga(1, 1, [1, 1, 1, 255]));
        files.Write("id1/textures/e1m1/wall.tga", Tga(1, 1, Fill(1, 50, 50, 50)));
        files.Write("id1/textures/e1m1/wall_norm.tga", Tga(1, 1, [2, 2, 2, 255]));
        var pack = new EnhancedTexturePack([Path.Combine(files.Root, "id1")]);

        Assert.Equal(2, EnhancedMaterials.World(pack, "e1m1", Texture("wall", 1, 1, [0]))![0].Rgba[0]);
        Assert.Equal(1, EnhancedMaterials.World(pack, "e2m1", Texture("wall", 1, 1, [0]))![0].Rgba[0]);
    }

    private static byte[] Fill(int count, byte r, byte g, byte b) =>
        [.. Enumerable.Range(0, count).SelectMany(_ => new[] { r, g, b, (byte)255 })];

    private static byte[] Grey(int width, int height, Func<int, int, int> value)
    {
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                rgba[i] = rgba[i + 1] = rgba[i + 2] = (byte)value(x, y);
                rgba[i + 3] = 255;
            }
        }

        return rgba;
    }
}
