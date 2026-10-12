using System.Buffers.Binary;
using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Platform;
using QuakeSharp.Core.Render;
using QuakeSharp.Core.Sound;

namespace QuakeSharp.Core.Tests;

/// <summary>The client's own logic, without game data (the demos check the rest against the oracle).</summary>
public class ClientTests
{
    [Theory]
    [InlineData(1 | 2, 0.5f)] // pressed and held this frame
    [InlineData(4, 0f)] // released this frame
    [InlineData(1, 1.0f)] // held the entire frame
    [InlineData(0, 0f)] // up the entire frame
    [InlineData(1 | 2 | 4, 0.75f)] // released and re-pressed this frame
    [InlineData(2 | 4, 0.25f)] // pressed and released this frame
    public void KeyStateGivesTheFractionOfTheFrameAndClearsTheImpulses(int state, float expected)
    {
        var key = new KButton { state = state };
        Assert.Equal(expected, QuakeEngine.CL_KeyState(key));
        Assert.Equal(state & 1, key.state);
    }

    [Fact]
    public void ButtonsTrackTwoKeysAndIgnoreRepeats()
    {
        QuakeEngine e = Engine();
        e.Cmd_TokenizeString("+forward 119");
        e.KeyDown(e.in_forward);
        e.KeyDown(e.in_forward); // repeating key
        e.Cmd_TokenizeString("+forward 200");
        e.KeyDown(e.in_forward);
        Assert.Equal([119, 200], e.in_forward.down);
        Assert.Equal(1 | 2, e.in_forward.state);

        // the button is up only when both keys are
        e.Cmd_TokenizeString("-forward 119");
        e.KeyUp(e.in_forward);
        Assert.Equal(1 | 2, e.in_forward.state);
        e.Cmd_TokenizeString("-forward 200");
        e.KeyUp(e.in_forward);
        Assert.Equal(2 | 4, e.in_forward.state);
    }

    [Theory]
    [InlineData(100, 320, 240, 48, 0, 0, 320, 192)]
    [InlineData(110, 320, 240, 24, 0, 0, 320, 216)]
    [InlineData(120, 320, 240, 0, 0, 0, 320, 240)]
    [InlineData(50, 320, 200, 48, 80, 26, 160, 100)]
    [InlineData(30, 320, 200, 48, 112, 46, 96, 60)]
    public void TheViewRectangleFollowsViewsize(int viewsize, int width, int height, int lineadj, int x, int y, int w, int h)
    {
        QuakeEngine e = Engine();
        e.Cvar_RegisterVariable(e.scr_viewsize);
        e.Cvar_RegisterVariable(e.lcd_x);
        e.Cvar_SetValue("viewsize", viewsize);

        VRect rect = default;
        e.R_SetVrect(new VRect { width = width, height = height }, ref rect, lineadj);
        Assert.Equal((x, y, w, h), (rect.x, rect.y, rect.width, rect.height));
    }

    [Fact]
    public void AWavIsResampledToTheMixRate()
    {
        QuakeEngine e = Engine();
        e.shm = new Dma { speed = 11025, channels = 2, samplebits = 16 };

        // 22050 Hz, 8 bit: every other sample at 11025, unsigned to signed
        byte[] wav = Wav(22050, 8, [0x80, 0x00, 0xff, 0x00, 0x00, 0x00, 0x90, 0x00], loopstart: 2);
        WavInfo info = e.GetWavinfo("test", wav, wav.Length);
        Assert.Equal((22050, 1, 1, 2, 8), (info.rate, info.width, info.channels, info.loopstart, info.samples));

        var sfx = new Sfx { name = "test.wav" };
        var files = new TestFiles();
        try
        {
            // in a pack: without the registered version, loose files in folders are not found
            files.Write("id1/pak0.pak", TestFiles.Pak(("sound/test.wav", wav)));
            QuakeEngine withFiles = Engine(files.Root);
            withFiles.shm = e.shm;
            SfxCache sc = withFiles.S_LoadSound(sfx)!;
            Assert.Equal((4, 1, 11025, 1), (sc.length, sc.loopstart, sc.speed, sc.width));
            Assert.Equal([0x00, 0x7f, 0x80, 0x10], sc.data);
        }
        finally
        {
            files.Dispose();
        }
    }

    // A RIFF WAVE file of mono PCM, with a cue point for a looped sound
    private static byte[] Wav(int rate, int bits, byte[] samples, int loopstart)
    {
        using var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        w.Write("RIFF"u8);
        w.Write(0);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write((short)1); // mono
        w.Write(rate);
        w.Write(rate * bits / 8);
        w.Write((short)(bits / 8));
        w.Write((short)bits);
        w.Write("cue "u8);
        w.Write(28);
        w.Write(new byte[24]);
        w.Write(loopstart);
        w.Write("data"u8);
        w.Write(samples.Length);
        w.Write(samples);
        byte[] wav = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        return wav;
    }

    private static QuakeEngine Engine(string? basedir = null)
    {
        var e = new QuakeEngine(new QuakeParms(basedir ?? ".", new CommandLine(["quake"])), new VirtualSystem());
        e.Cbuf_Init();
        e.Cmd_Init();
        if (basedir != null)
        {
            // the search path only (COM_Init would stop at a modified pak0.pak without the registered version)
            e.FileSystem.InitFilesystem(basedir);
        }

        return e;
    }
}
