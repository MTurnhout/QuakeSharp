namespace QuakeSharp.Core.Sound;

/// <summary>sound.h: dma_t, the buffer a sound device plays from and the mixer paints ahead into.</summary>
public sealed class Dma
{
    public bool gamealive;

    public bool soundalive;

    public bool splitbuffer;

    public int channels;

    /// <summary>mono samples in buffer</summary>
    public int samples;

    /// <summary>don't mix less than this #</summary>
    public int submission_chunk;

    /// <summary>in mono samples</summary>
    public int samplepos;

    public int samplebits;

    public int speed;

    public byte[] buffer = [];
}

/// <summary>
/// A sound device: snd_win.c's SNDDMA_* functions. The device plays <see cref="Dma.buffer"/> round and round; the
/// mixer paints ahead of its position (design.md §7.1).
/// </summary>
public interface ISoundDma
{
    // SNDDMA_Init: the device's buffer, or null if there is no sound
    Dma? Init();

    // SNDDMA_GetDMAPos: the current position in mono samples, inside the buffer
    int GetDMAPos();

    // SNDDMA_Submit: the mixer has painted
    void Submit();

    // SNDDMA_Shutdown
    void Shutdown();
}
