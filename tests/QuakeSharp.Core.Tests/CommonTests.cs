using QuakeSharp.Core.Common;

namespace QuakeSharp.Core.Tests;

public class CommonTests
{
    [Fact]
    public void CrcIsCcittWithInitialValueFfff()
    {
        // The standard check value of CRC-16/CCITT-FALSE (XMODEM's polynomial, initial value 0xffff)
        Crc.Init(out ushort crc);
        foreach (byte b in "123456789"u8)
        {
            Crc.ProcessByte(ref crc, b);
        }

        Assert.Equal(0x29b1, Crc.Value(crc));
    }

    [Theory]
    [InlineData("maps/e1m1.bsp", "e1m1")]
    [InlineData("progs/player.mdl", "player")]
    [InlineData("a/b.c/d.e", "d")]
    [InlineData("maps/e1m1", "?model?")] // no extension: s stays at the start
    [InlineData("maps/.bsp", "?model?")] // nothing between the '/' and the '.'
    [InlineData("x.y", "x")]
    [InlineData("ab.y", "ab")]
    public void FileBase(string path, string expected) => Assert.Equal(expected, Com.FileBase(path));

    [Theory]
    [InlineData("maps/e1m1.bsp", "bsp")]
    [InlineData("demo1", "")]
    [InlineData("a.b.c", "b.c")]
    [InlineData("x.abcdefghij", "abcdefg")]
    public void FileExtension(string path, string expected) => Assert.Equal(expected, Com.FileExtension(path));

    [Theory]
    [InlineData("demo1", ".dem", "demo1.dem")]
    [InlineData("demo1.dem", ".dem", "demo1.dem")]
    [InlineData("dir.x/demo1", ".dem", "dir.x/demo1.dem")]
    [InlineData(".x", ".dem", ".x.dem")] // the first character is never looked at
    public void DefaultExtension(string path, string extension, string expected) =>
        Assert.Equal(expected, Com.DefaultExtension(path, extension));

    [Fact]
    public void StripExtensionAndSkipPath()
    {
        Assert.Equal("maps/e1m1", Com.StripExtension("maps/e1m1.bsp"));
        Assert.Equal("e1m1.bsp", Com.SkipPath("maps/e1m1.bsp"));
        Assert.Equal("e1m1.bsp", Com.SkipPath("e1m1.bsp"));
    }

    [Fact]
    public void CommandLineFindsParametersAfterTheProgram()
    {
        var cl = new CommandLine(["quake", "-game", "mod", "+map", "e1m1"]);
        Assert.Equal(5, cl.Argc);
        Assert.Equal(1, cl.CheckParm("-game"));
        Assert.Equal(0, cl.CheckParm("quake"));
        Assert.Equal(" ", cl.Argv(cl.Argc));
        // sys_win.c gives argv[0] empty
        Assert.Equal(" -game mod +map e1m1 ", cl.Cmdline);
        Assert.True(cl.standard_quake);
    }

    // the cmdline cvar leaves the traces' harness switches out, with their values, as the oracle does (design.md §3.3)
    [Fact]
    public void CmdlineLeavesTheHarnessSwitchesOut()
    {
        var cl = new CommandLine(["quake", "-trace", "a.trace", "-oraclevid", "640", "480", "-window", "-netunplug", "-oracleframes"]);
        Assert.Equal(" -window ", cl.Cmdline);
        Assert.Equal(1, cl.CheckParm("-trace"));
    }

    [Fact]
    public void SafeAddsTheSafeModeSwitches()
    {
        var cl = new CommandLine(["quake", "-safe"]);
        Assert.Equal(9, cl.Argc);
        Assert.NotEqual(0, cl.CheckParm("-nosound"));
        Assert.NotEqual(0, cl.CheckParm("-dibonly"));
    }

    [Fact]
    public void MissionPackSwitchesClearStandardQuake()
    {
        var cl = new CommandLine(["quake", "-hipnotic"]);
        Assert.True(cl.hipnotic);
        Assert.False(cl.rogue);
        Assert.False(cl.standard_quake);
    }

    [Fact]
    public void CmdlineIsCutAt255Characters()
    {
        var cl = new CommandLine(["quake", new string('a', 300)]);
        Assert.Equal(255, cl.Cmdline.Length);
    }
}
