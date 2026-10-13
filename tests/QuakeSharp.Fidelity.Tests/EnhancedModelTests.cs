using System.Buffers.Binary;
using System.Text;
using QuakeSharp.Core;
using QuakeSharp.Core.Diagnostics;
using QuakeSharp.Core.Enhanced;
using QuakeSharp.Core.Model;

namespace QuakeSharp.Fidelity.Tests;

/// <summary>
/// The enhanced mode's brush models (design.md §11.6, step 3) against the BSP files themselves, read here without
/// model.c: every texture's four mip levels as the file has them, the fullbright texels, each face's light data offset
/// and styles, and the triangles each submodel draws.
/// </summary>
public sealed class EnhancedModelTests : IDisposable
{
    private readonly string userdir = Path.Combine(Path.GetTempPath(), "quakesharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(userdir))
        {
            Directory.Delete(userdir, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, "e1m1")]
    [InlineData(null, "start")]
    [InlineData(null, "e1m5")]
    [InlineData("hipnotic", "hip1m1")]
    [InlineData("rogue", "r1m1")]
    public void TheModelIsTheBspFiles(string? pack, string map)
    {
        string baseDir = pack == null ? GameDataLocator.RequireBaseDir() : GameDataLocator.RequirePackBaseDir(pack);
        string[] args = [.. pack == null ? [] : new[] { "-" + pack }, "+map", map, "-oracleframes", "3"];
        var run = new TraceRun(baseDir, userdir, args);
        foreach (IReadOnlyList<string> _ in run.Frames(quitAfterDemo: false, maxFrames: 3))
        {
        }

        QuakeEngine e = run.Engine;
        Model world = e.cl.worldmodel ?? throw new InvalidOperationException("no world");
        byte[] bsp = e.COM_LoadTempFile($"maps/{map}.bsp") ?? throw new InvalidOperationException("no bsp");
        EnhancedBrushModel model = EnhancedBrushModel.Build(world);

        // the textures: name, size and the four mip levels, byte for byte
        (int offset, _) = Lump(bsp, BspFile.LUMP_TEXTURES);
        int count = BinaryPrimitives.ReadInt32LittleEndian(bsp.AsSpan(offset));
        Assert.Equal(count, model.Textures.Length);
        for (int i = 0; i < count; i++)
        {
            int at = BinaryPrimitives.ReadInt32LittleEndian(bsp.AsSpan(offset + 4 + (i * 4)));
            if (at == -1)
            {
                continue; // a texture the BSP lacks
            }

            int miptex = offset + at;
            string name = Encoding.Latin1.GetString(bsp, miptex, 16).TrimEnd('\0');
            int width = BinaryPrimitives.ReadInt32LittleEndian(bsp.AsSpan(miptex + 16));
            int height = BinaryPrimitives.ReadInt32LittleEndian(bsp.AsSpan(miptex + 20));
            EnhancedTexture t = model.Textures[i];
            Assert.Equal(name.Split('\0')[0], t.Name);
            Assert.Equal((width, height), (t.Width, t.Height));
            for (int m = 0; m < 4; m++)
            {
                int from = miptex + BinaryPrimitives.ReadInt32LittleEndian(bsp.AsSpan(miptex + 24 + (m * 4)));
                int size = (width >> m) * (height >> m);
                Assert.True(bsp.AsSpan(from, size).SequenceEqual(t.Pixels.AsSpan(t.MipOffsets[m], size)), $"{t.Name} mip {m}");
            }

            // fullbright where the palette's last 32 are, in the colours' alpha
            byte[] rgba = EnhancedTextures.ColorMips(t, e.host_basepal, fullChain: true)[0].Rgba;
            for (int p = 0; p < width * height; p++)
            {
                Assert.Equal(t.Pixels[p] >= EnhancedTextures.Fullbright ? 255 : 0, rgba[(p * 4) + 3]);
            }
        }

        // the faces: their light data and styles
        (int faces, int length) = Lump(bsp, BspFile.LUMP_FACES);
        Assert.Equal(length / 20, model.Surfaces.Length);
        for (int i = 0; i < model.Surfaces.Length; i++)
        {
            int face = faces + (i * 20);
            int styles = BinaryPrimitives.ReadInt32LittleEndian(bsp.AsSpan(face + 12));
            int lightofs = BinaryPrimitives.ReadInt32LittleEndian(bsp.AsSpan(face + 16));
            Assert.Equal(styles, model.Surfaces[i].Styles);
            Assert.Equal(lightofs, model.Surfaces[i].LightOffset);
        }

        // each submodel draws its faces' triangles: a fan of (edges - 2) each
        for (int m = 0; m < world.numsubmodels; m++)
        {
            DModel sub = world.submodels[m];
            int expected = Enumerable.Range(sub.firstface, sub.numfaces).Sum(f => (world.surfaces[f].numedges - 2) * 3);
            Assert.Equal(expected, model.Submodels[m].Sum(d => d.Count));
        }
    }

    // The capture of -enhanced exists before Host_Init; a new game's CL_ClearState makes a new cl, and the gun is that
    // cl's viewent: still the view model, so it is interpolated with the view (it ghosted when it was not)
    [Fact]
    public void TheGunIsTheViewModelAfterANewGame()
    {
        string baseDir = GameDataLocator.RequireBaseDir();
        var run = new TraceRun(baseDir, userdir, ["+map", "e1m1", "-oracleframes", "60"]);
        var capture = new EnhancedCapture(run.Engine);
        run.Engine.enhanced = capture;
        foreach (IReadOnlyList<string> _ in run.Frames(quitAfterDemo: false, maxFrames: 60))
        {
        }

        EnhancedFrame frame = capture.Latest ?? throw new InvalidOperationException("no frame");
        Assert.Contains(frame.Entities, e => e.Id == EnhancedEntity.ViewModel && frame.Models[e.Model]?.Alias != null);
    }

    private static (int Offset, int Length) Lump(byte[] bsp, int lump) =>
        (BinaryPrimitives.ReadInt32LittleEndian(bsp.AsSpan(4 + (lump * 8))),
         BinaryPrimitives.ReadInt32LittleEndian(bsp.AsSpan(8 + (lump * 8))));
}
