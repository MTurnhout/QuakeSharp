using QuakeSharp.Core.Platform;

namespace QuakeSharp.Core.Sound;

/// <summary>
/// The oracle's sound device (tools/oracle/src/snd_oracle.c): 64 KB of 16-bit stereo at 11025 Hz, standing in for
/// snd_win.c's DirectSound buffer, whose play position follows the system's clock. On a virtual clock the mixer then
/// paints the same samples at the same frames in every run, which the game depends on: S_StartSound draws from
/// rand() when an identical sound has not been painted yet (design.md §7.1).
/// </summary>
public sealed class VirtualSoundDma : ISoundDma
{
    private const int SecondaryBufferSize = 0x10000;

    private readonly ISystem system;
    private Dma? shm;

    public VirtualSoundDma(ISystem system)
    {
        this.system = system;
    }

    // snd_oracle.c: SNDDMA_Init
    public Dma? Init()
    {
        shm = new Dma
        {
            channels = 2,
            samplebits = 16,
            speed = 11025,
            soundalive = true,
            splitbuffer = false,
            samples = SecondaryBufferSize / 2,
            samplepos = 0,
            submission_chunk = 1,
            buffer = new byte[SecondaryBufferSize],
        };
        return shm;
    }

    // snd_oracle.c: SNDDMA_GetDMAPos: one sample frame per 1/speed s of the clock, wrapping in the buffer
    public int GetDMAPos()
    {
        int s;

        s = (int)(system.FloatTime() * shm!.speed) * shm.channels;
        s &= shm.samples - 1;
        return s;
    }

    public void Submit()
    {
    }

    public void Shutdown()
    {
    }
}
