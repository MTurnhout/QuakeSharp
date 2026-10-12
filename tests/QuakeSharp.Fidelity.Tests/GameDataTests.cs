using QuakeSharp.Core.Common;

namespace QuakeSharp.Fidelity.Tests;

/// <summary>The file system and gfx.wad on the original data (milestone 1).</summary>
public sealed class GameDataTests
{
    private readonly List<string> console = [];

    [Fact]
    public void TheOriginalInstallIsRegistered()
    {
        FileSystem fs = Init(GameDataLocator.RequireBaseDir());

        Assert.True(fs.CheckRegistered());
        Assert.Contains("Playing registered version.\n", console);

        // only pak0 matches PAK0_COUNT and PAK0_CRC: pak1 counts as modified, in the original too
        Assert.True(fs.Modified);
    }

    [Fact]
    public void Pak0AloneIsTheUnmodifiedShareware()
    {
        string baseDir = GameDataLocator.RequireBaseDir();
        string temp = Path.Combine(Path.GetTempPath(), "quakesharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "id1"));
        try
        {
            File.Copy(HostPath.Resolve(Path.Combine(baseDir, "id1/pak0.pak"))!, Path.Combine(temp, "id1", "pak0.pak"));
            FileSystem fs = Init(temp);

            // the directory's 339 files and CRC are the ones common.c checks for
            Assert.Contains($"Added packfile {temp}/id1/pak0.pak (339 files)\n", console);
            Assert.False(fs.Modified);
            Assert.False(fs.CheckRegistered());
            Assert.Contains("Playing shareware version.\n", console);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void EveryFileLoads()
    {
        FileSystem fs = Init(GameDataLocator.RequireBaseDir());

        int count = 0;
        foreach (string name in fs.ListFiles().Where(n => n.Contains('/', StringComparison.Ordinal)))
        {
            byte[]? data = fs.LoadFile(name);
            Assert.NotNull(data);
            Assert.Equal(fs.FileSize + 1, data.Length);
            Assert.Equal(0, data[^1]);
            count++;
        }

        Assert.True(count >= 339 + 1, $"only {count} files");
    }

    [Fact]
    public void GfxWadHasTheLumpsTheCodeLoads()
    {
        FileSystem fs = Init(GameDataLocator.RequireBaseDir());
        Wad wad = Wad.LoadWadFile(fs, "gfx.wad");

        Wad.LumpInfo conchars = wad.GetLumpinfo("conchars");
        Assert.Equal(128 * 128, conchars.Size);
        Assert.Equal(Wad.TYP_QPIC, wad.GetLumpinfo("disc").Type);
        Assert.Equal(Wad.TYP_QPIC, wad.GetLumpinfo("backtile").Type);
        // sbar.c's pictures, through Draw_PicFromWad
        Assert.Equal(Wad.TYP_QPIC, wad.GetLumpinfo("num_0").Type);
        Assert.Equal(Wad.TYP_QPIC, wad.GetLumpinfo("inv_shotgun").Type);
    }

    [Fact]
    public void PaletteAndColormapHaveTheirSizes()
    {
        FileSystem fs = Init(GameDataLocator.RequireBaseDir());

        Assert.NotNull(fs.LoadFile("gfx/palette.lmp"));
        Assert.Equal(768, fs.FileSize);
        Assert.NotNull(fs.LoadFile("gfx/colormap.lmp"));
        Assert.Equal((256 * 64) + 1, fs.FileSize);
    }

    private FileSystem Init(string baseDir)
    {
        var fs = new FileSystem(new CommandLine(["quake"]), console.Add, _ => { });
        fs.InitFilesystem(baseDir);
        return fs;
    }
}
