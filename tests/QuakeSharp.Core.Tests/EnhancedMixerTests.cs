using QuakeSharp.Core.Enhanced;
using QuakeSharp.Core.Sound;

namespace QuakeSharp.Core.Tests;

/// <summary>The enhanced mode's mixer (design.md §11.6, step 7): it follows WinQuake's channels at four times the rate.</summary>
public class EnhancedMixerTests
{
    [Fact]
    public void ASoundStartsWhereTheGameStartsIt()
    {
        // S_StartSound: pos 0, end at paintedtime plus the length: it plays from the next paint on, at four frames a
        // sample
        var mixer = new EnhancedMixer();
        Channel ch = Playing(Sound([.. Enumerable.Repeat((byte)64, 100)]), at: 1000);

        mixer.Paint([ch], 1, 1000, 1100, 1);

        Assert.Equal(0, Left(mixer, (1000 * 4) - 1));
        Assert.NotEqual(0, Left(mixer, (1000 * 4) + 1));
        Assert.Equal(0, Left(mixer, 1100 * 4));
    }

    [Fact]
    public void ALevelIsWinQuakesOnceTheRampIsDone()
    {
        var mixer = new EnhancedMixer();
        Channel ch = Playing(Sound([.. Enumerable.Repeat((byte)64, 400)]), at: 0);
        ch.leftvol = 255;
        ch.rightvol = 128;
        mixer.Paint([ch], 1, 0, 50, 1); // the ramp from silence
        mixer.Paint([ch], 1, 50, 300, 0.5f);

        // snd_mix.c: an 8-bit 64 at volume 255 (& ~7: 248) is 64 * 248 = 15872 of 32768 before the volume cvar
        Assert.Equal(64f / 128 * 0.5f, Left(mixer, 200 * 4), 3);
        Assert.Equal(64f / 128 * 0.5f * 128 / 255, Right(mixer, 200 * 4), 3);
    }

    [Fact]
    public void ALoopStartsAgainAtItsLoopAndAnEndStops()
    {
        var mixer = new EnhancedMixer();
        byte[] data = [.. Enumerable.Range(0, 20).Select(i => (byte)(i < 10 ? 10 : 100))];
        SfxCache looping = Sound(data, loopstart: 10);
        Channel loop = Playing(looping, at: 0);
        Channel once = Playing(Sound(data), at: 0);

        var loopMixer = new EnhancedMixer();
        loopMixer.Paint([loop], 1, 0, 120, 1);
        mixer.Paint([once], 1, 0, 120, 1);

        // after its 20 samples the looping sound plays its second half again, the other one nothing (past the ramp)
        Assert.Equal(100f / 128, Left(loopMixer, 105 * 4), 2);
        Assert.Equal(0, Left(mixer, 105 * 4));
    }

    [Fact]
    public void APlacedSoundOnTheRightIsLouderAndSoonerOnTheRight()
    {
        var mixer = new EnhancedMixer { Placement = true };
        mixer.Listen(new Common.Vec3(0, 0, 0), new Common.Vec3(1, 0, 0), new Common.Vec3(0, -1, 0), viewentity: 1);
        byte[] click = [.. Enumerable.Range(0, 400).Select(i => (byte)(i >= 100 ? 100 : 0))];
        Channel ch = Playing(Sound(click), at: 0);
        ch.entnum = 2;
        ch.origin = new Common.Vec3(0, -200, 0); // straight to the right

        mixer.Paint([ch], 1, 0, 400, 1);

        // the right ear hears the step at sample 100 first; the left one later, and less of it
        float rightAtStep = Right(mixer, (100 * 4) + 8), leftAtStep = Left(mixer, (100 * 4) + 8);
        Assert.True(rightAtStep > 0.1f);
        Assert.True(Math.Abs(leftAtStep) < 0.01f);
        Assert.True(Right(mixer, 300 * 4) > Left(mixer, 300 * 4));
    }

    [Fact]
    public void BetweenSamplesItIsHermitesCurveThroughThem()
    {
        SfxCache sc = Sound([0, 0, 64, 64]);
        Assert.Equal(0f, EnhancedMixer.Sample(sc, 1));
        Assert.Equal(32f, EnhancedMixer.Sample(sc, 1.5), 3);
        Assert.Equal(64f, EnhancedMixer.Sample(sc, 2));
    }

    private static SfxCache Sound(byte[] data, int loopstart = -1) =>
        new() { length = data.Length, loopstart = loopstart, speed = 11025, width = 1, stereo = 0, data = data };

    // a channel as S_StartSound leaves it: pos 0, end at its start time plus its length
    private static Channel Playing(SfxCache sc, int at)
    {
        var ch = new Channel { sfx = new Sfx { name = "test", cache = sc }, leftvol = 255, rightvol = 255, pos = 0 };
        ch.end = at + sc.length;
        return ch;
    }

    private static float Left(EnhancedMixer mixer, long frame) => mixer.Ring[(int)(frame & (EnhancedMixer.RingFrames - 1)) * 2];

    private static float Right(EnhancedMixer mixer, long frame) => mixer.Ring[((int)(frame & (EnhancedMixer.RingFrames - 1)) * 2) + 1];
}
