using QuakeSharp.Core.Sound;

namespace QuakeSharp.Core;

// snd_win.c: what the game shows of WinQuake's sound driver, its DirectSound path (design.md §7.1, decision 38): the
// messages SNDDMA_Init prints. The device itself is the ISoundDma.
public sealed partial class QuakeEngine
{
    // snd_win.c: snd_firsttime
    private bool snd_firsttime = true;

    // snd_win.c: SNDDMA_Init, with SNDDMA_InitDirect's messages: the device's buffer, or null if there is none. A device
    // that fails is DirectSound and waveOut failing (the C's "No sound device initialized" never prints: snd_firsttime
    // is already false there)
    public Dma? SNDDMA_Init()
    {
        Dma? dma = sounddma?.Init();

        if (dma != null)
        {
            if (snd_firsttime)
            {
                Con_SafePrintf("Set primary sound buffer format: yes\n");
                Con_SafePrintf("Using secondary sound buffer\n");
                Con_SafePrintf("   %d channel(s)\n   %d bits/sample\n   %d bytes/sec\n", dma.channels, dma.samplebits, dma.speed);
                Con_SafePrintf("DirectSound initialized\n");
            }
        }
        else
        {
            Con_SafePrintf("DirectSound failed to init\n");
            Con_SafePrintf("Wave sound failed to init\n");
        }

        snd_firsttime = false;
        return dma;
    }

    // snd_win.c: S_BlockSound: DirectSound takes care of blocking itself (only waveOut's buffers are reset)
    public void S_BlockSound()
    {
    }

    // snd_win.c: S_UnblockSound
    public void S_UnblockSound()
    {
    }
}
