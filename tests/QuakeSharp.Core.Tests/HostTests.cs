using QuakeSharp.Core.Common;
using QuakeSharp.Core.Platform;

namespace QuakeSharp.Core.Tests;

public class HostTests
{
    [Fact]
    public void FramesOfASixtyFourthOfASecondAllRun()
    {
        QuakeEngine e = Engine();
        for (int i = 0; i < 1000; i++)
        {
            Assert.True(e.Host_FilterTime(1f / 64));
            Assert.Equal(1.0 / 64, e.host_frametime);
        }

        Assert.Equal(1000.0 / 64, e.realtime);
    }

    [Fact]
    public void FramesShorterThanA72ndWait()
    {
        QuakeEngine e = Engine();

        // 0.01 s calls: a frame runs once at least 1/72 s has gathered, with all of it as its time
        Assert.False(e.Host_FilterTime(0.01f));
        Assert.True(e.Host_FilterTime(0.01f));
        Assert.Equal((double)0.01f + 0.01f, e.host_frametime);
        Assert.False(e.Host_FilterTime(0.01f));
    }

    [Fact]
    public void LongFramesAreCutAndHostFramerateWins()
    {
        QuakeEngine e = Engine();
        Assert.True(e.Host_FilterTime(0.5f));
        Assert.Equal(0.1, e.host_frametime);

        e.Cvar_Set("host_framerate", "0.25");
        Assert.True(e.Host_FilterTime(0.02f));
        Assert.Equal(0.25f, e.host_frametime);
    }

    [Fact]
    public void EveryCallOfHostFrameDrawsARandomNumber()
    {
        // _Host_Frame: "keep the random time dependent", even when the frame does not run
        QuakeEngine e = Engine();
        var reference = new Numerics.CRand();
        for (int i = 0; i < 10; i++)
        {
            e.Host_Frame(0.001f);
            reference.rand();
        }

        Assert.Equal(reference.Seed, e.RandSeed);
        Assert.Equal(0, e.host_framecount);

        e.Host_Frame(1f / 64);
        Assert.Equal(1, e.host_framecount);
    }

    [Fact]
    public void HostErrorAbortsTheFrame()
    {
        QuakeEngine e = Engine();
        e.Cmd_AddCommand("fail", () => throw e.Host_Error("%s went wrong", "something"));
        e.Cbuf_AddText("fail\necho after\n");
        e.Host_Frame(1f / 64);

        // the frame ended at the error: not counted, the rest of the buffer left for the next frame
        Assert.Equal(0, e.host_framecount);
        Assert.Equal("echo after\n", System.Text.Encoding.Latin1.GetString(e.cmd_text.data, 0, e.cmd_text.cursize));
    }

    private static QuakeEngine Engine()
    {
        var e = new QuakeEngine(new QuakeParms(".", new CommandLine(["quake"])), new VirtualSystem());
        e.Memory_Init();
        e.Cbuf_Init();
        e.Cmd_Init();
        e.Host_InitLocal();
        e.Key_Init();
        e.Con_Init();
        return e;
    }
}
