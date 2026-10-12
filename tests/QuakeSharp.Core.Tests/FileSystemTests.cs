using System.Buffers.Binary;
using System.Text;
using QuakeSharp.Core.Common;

namespace QuakeSharp.Core.Tests;

public sealed class FileSystemTests : IDisposable
{
    private readonly TestFiles files = new();
    private readonly List<string> console = [];

    public void Dispose() => files.Dispose();

    [Fact]
    public void PakFilesAreFoundInAnyCaseAndLaterPaksOverride()
    {
        files.Write("Id1/PAK0.PAK", TestFiles.Pak(("a.txt", "zero"u8.ToArray()), ("b.txt", "bee"u8.ToArray())));
        files.Write("Id1/Pak1.pak", TestFiles.Pak(("a.txt", "one"u8.ToArray())));
        FileSystem fs = Init();

        Assert.Equal("one\0", Text(fs.LoadFile("a.txt")));
        Assert.Equal("bee\0", Text(fs.LoadFile("b.txt")));
        Assert.Equal(3, fs.FileSize);
        Assert.Equal($"{files.Root}/id1", fs.GameDir);
        Assert.Contains($"Added packfile {files.Root}/id1/pak0.pak (2 files)\n", console);
        Assert.Contains($"Added packfile {files.Root}/id1/pak1.pak (1 files)\n", console);
    }

    [Fact]
    public void MissingFilesGiveNullAndSizeMinusOne()
    {
        files.Write("id1/pak0.pak", TestFiles.Pak(("a.txt", "a"u8.ToArray())));
        FileSystem fs = Init();

        Assert.Null(fs.LoadFile("nothing.txt"));
        Assert.Equal(-1, fs.FileSize);
    }

    [Fact]
    public void PaksGoBeforeTheLooseFilesOfTheirDirectory()
    {
        // COM_AddGameDirectory adds the directory, then puts its paks in front of it
        files.Write("id1/pak0.pak", TestFiles.Pak(("maps/x.txt", "pak"u8.ToArray())));
        files.Write("id1/maps/x.txt", "id1"u8.ToArray());
        files.Write("mod/maps/x.txt", "mod"u8.ToArray());
        Assert.Equal("pak\0", Text(Init().LoadFile("maps/x.txt")));
        Assert.Equal("mod\0", Text(Init("-game", "mod").LoadFile("maps/x.txt")));
    }

    [Fact]
    public void GameDirectoryGoesFirstAndMarksTheGameModified()
    {
        files.Write("id1/pak0.pak", TestFiles.Pak(("a.txt", "id1"u8.ToArray())));
        files.Write("mod/pak0.pak", TestFiles.Pak(("a.txt", "mod"u8.ToArray())));
        FileSystem fs = Init("-game", "mod");

        Assert.Equal("mod\0", Text(fs.LoadFile("a.txt")));
        Assert.Equal($"{files.Root}/mod", fs.GameDir);
        Assert.True(fs.Modified);
    }

    [Fact]
    public void PathReplacesTheSearchPath()
    {
        files.Write("id1/pak0.pak", TestFiles.Pak(("a.txt", "id1"u8.ToArray())));
        string other = files.Write("other.pak", TestFiles.Pak(("a.txt", "other"u8.ToArray())));
        FileSystem fs = Init("-path", other);

        Assert.Equal("other\0", Text(fs.LoadFile("a.txt")));
        console.Clear();
        fs.Path_f();
        Assert.Equal(["Current search path:\n", $"{other} (1 files)\n"], console);
    }

    [Fact]
    public void ReadingOnInsideAPakReadsTheNextFile()
    {
        files.Write("id1/pak0.pak", TestFiles.Pak(("a.txt", "ab"u8.ToArray()), ("b.txt", "cd"u8.ToArray())));
        FileSystem fs = Init();

        fs.FindFile("a.txt", out Stream? stream);
        using (stream)
        {
            var buffer = new byte[4];
            stream!.ReadExactly(buffer);
            Assert.Equal("abcd", Encoding.Latin1.GetString(buffer));
        }
    }

    [Fact]
    public void AnUnknownPakMarksTheGameModified()
    {
        files.Write("id1/pak0.pak", TestFiles.Pak(("a.txt", "a"u8.ToArray())));
        Assert.True(Init().Modified);
    }

    [Fact]
    public void NotAPackfileIsAnError()
    {
        files.Write("id1/pak0.pak", "JUNKJUNKJUNK"u8.ToArray());
        var e = Assert.Throws<QuakeException>(() => Init());
        Assert.Equal($"{files.Root}/id1/pak0.pak is not a packfile", e.Message);
    }

    [Fact]
    public void PopLmpMakesTheRegisteredVersion()
    {
        var pop = new byte[256];
        for (int i = 0; i < 128; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(pop.AsSpan(i * 2), FileSystem.pop[i]);
        }

        files.Write("id1/pak0.pak", TestFiles.Pak(("gfx/pop.lmp", pop)));
        FileSystem fs = Init();

        Assert.True(fs.CheckRegistered());
        Assert.True(fs.StaticRegistered);
        Assert.Contains("Playing registered version.\n", console);
    }

    [Fact]
    public void AWrongPopLmpIsCorrupted()
    {
        files.Write("id1/pak0.pak", TestFiles.Pak(("gfx/pop.lmp", new byte[256])));
        FileSystem fs = Init();

        // the table starts with zeros; its first non-zero word is the 11th
        Assert.Equal("Corrupted data file.", Assert.Throws<QuakeException>(() => fs.CheckRegistered()).Message);
    }

    [Fact]
    public void SharewareRefusesModifiedGames()
    {
        files.Write("id1/pak0.pak", TestFiles.Pak(("a.txt", "a"u8.ToArray())));
        FileSystem fs = Init();

        Assert.Equal("You must have the registered version to use modified games",
            Assert.Throws<QuakeException>(() => fs.CheckRegistered()).Message);
        Assert.False(fs.StaticRegistered);
    }

    [Fact]
    public void SharewareFindsNoLooseFilesInSubdirectories()
    {
        files.Write("id1/maps/x.txt", "loose"u8.ToArray());
        files.Write("id1/y.txt", "top"u8.ToArray());
        FileSystem fs = Init();
        Assert.False(fs.CheckRegistered());

        Assert.Null(fs.LoadFile("maps/x.txt"));
        Assert.Equal("top\0", Text(fs.LoadFile("y.txt")));
    }

    [Fact]
    public void WriteFileWritesIntoTheGameDirectory()
    {
        Directory.CreateDirectory(Path.Combine(files.Root, "id1"));
        FileSystem fs = Init();

        fs.WriteFile("out.txt", "data"u8);
        Assert.Equal("data", File.ReadAllText(Path.Combine(files.Root, "id1", "out.txt")));
    }

    [Fact]
    public void WadNamesAreCleanedUpForLookups()
    {
        files.Write("id1/pak0.pak", TestFiles.Pak(("gfx.wad", TestFiles.Wad2(
            ("CONCHARS", Wad.TYP_MIPTEX, new byte[] { 1, 2 }),
            ("disc", Wad.TYP_QPIC, new byte[] { 3 })))));
        FileSystem fs = Init();

        Wad wad = Wad.LoadWadFile(fs, "gfx.wad");
        Assert.Equal(["conchars", "disc"], wad.Lumps.Select(l => l.Name));
        Assert.Equal(12, wad.GetLumpName("ConChars"));
        Assert.Equal(3, wad.Data[wad.GetLumpName("DISC")]);
        Assert.Equal(Wad.TYP_QPIC, wad.GetLumpinfo("disc").Type);
        Assert.Equal("W_GetLumpinfo: nope not found", Assert.Throws<QuakeException>(() => wad.GetLumpinfo("nope")).Message);
    }

    [Fact]
    public void WadWithoutItsIdIsAnError()
    {
        files.Write("id1/pak0.pak", TestFiles.Pak(("gfx.wad", "WAD3........"u8.ToArray())));
        FileSystem fs = Init();

        Assert.Equal("Wad file gfx.wad doesn't have WAD2 id\n",
            Assert.Throws<QuakeException>(() => Wad.LoadWadFile(fs, "gfx.wad")).Message);
    }

    private FileSystem Init(params string[] args)
    {
        var fs = new FileSystem(new CommandLine(["quake", .. args]), console.Add, _ => { });
        fs.InitFilesystem(files.Root);
        return fs;
    }

    private static string Text(byte[]? data) => Encoding.Latin1.GetString(data!);
}
