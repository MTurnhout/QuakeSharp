using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using QuakeSharp.Core;
using QuakeSharp.Core.Sound;
using SDL;
using static SDL.SDL3;

namespace QuakeSharp.Platform.Sdl;

/// <summary>
/// The sound device, standing in for snd_win.c's DirectSound buffer: 64 KB of 16-bit stereo at 11025 Hz that an SDL3
/// audio stream plays round and round (SDL converts it to the device), the mixer painting ahead of its position
/// (design.md §7.1). With a clock (the executable's -recordevents), the position is the clock's instead, as the
/// oracle's virtual device has it (snd_oracle.c), so the mix is the one a trace of the recorded game has; the device
/// plays the buffer from that position, and without an audio device the buffer is there all the same, unheard (a
/// replay has the virtual device whatever machine recorded it).
/// </summary>
public sealed unsafe class SdlSoundDma : ISoundDma, IDisposable
{
    // snd_win.c: SECONDARY_BUFFER_SIZE
    private const int SecondaryBufferSize = 0x10000;

    private readonly int speed;
    private readonly Func<double>? clock;
    private SDL_AudioStream* stream;
    private GCHandle self;
    private Dma? shm;

    // Bytes of the buffer the device has taken so far, wrapping (written on SDL's audio thread)
    private int playPosition;

    /// <param name="speed">The mix rate (11025 Hz, as snd_win.c).</param>
    /// <param name="clock">A clock in seconds the play position follows (null: the device's own progress).</param>
    public SdlSoundDma(int speed = 11025, Func<double>? clock = null)
    {
        this.speed = speed;
        this.clock = clock;
    }

    // snd_oracle.c: SNDDMA_GetDMAPos's position in bytes: one sample frame per 1/speed s of the clock
    private int ClockPosition() => ((int)(clock!() * shm!.speed) * shm.channels * (shm.samplebits / 8)) & (shm.buffer.Length - 1);

    // snd_win.c: SNDDMA_Init
    public Dma? Init()
    {
        bool audio = SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO);
        if (!audio && clock == null)
        {
            return null;
        }

        shm = new Dma
        {
            channels = 2,
            samplebits = 16,
            speed = speed,
            soundalive = true,
            splitbuffer = false,
            samples = SecondaryBufferSize / 2,
            samplepos = 0,
            submission_chunk = 1,
            buffer = new byte[SecondaryBufferSize],
        };

        if (!audio)
        {
            return shm;
        }

        self = GCHandle.Alloc(this);
        var spec = new SDL_AudioSpec { format = SDL_AudioFormat.SDL_AUDIO_S16LE, channels = shm.channels, freq = shm.speed };
        stream = SDL_OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, &Fill, GCHandle.ToIntPtr(self));
        if (stream == null)
        {
            self.Free();
            SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO);
            if (clock == null)
            {
                shm = null;
            }

            return shm;
        }

        SDL_ResumeAudioStreamDevice(stream);
        return shm;
    }

    // snd_win.c: SNDDMA_GetDMAPos: the play position in mono samples, inside the buffer
    public int GetDMAPos()
    {
        if (shm == null)
        {
            return 0;
        }

        int s = (clock != null ? ClockPosition() : Volatile.Read(ref playPosition)) / (shm.samplebits / 8);
        return s & (shm.samples - 1);
    }

    public void Submit()
    {
    }

    // snd_win.c: SNDDMA_Shutdown
    public void Shutdown()
    {
        if (stream != null)
        {
            SDL_DestroyAudioStream(stream);
            stream = null;
            self.Free();
            SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO);
        }

        shm = null;
    }

    public void Dispose() => Shutdown();

    /// <summary>
    /// Silent while another program has the focus, as WinQuake's DirectSound buffer is (created without
    /// DSBCAPS_GLOBALFOCUS); the mixer goes on painting (design.md §7.1).
    /// </summary>
    public bool Muted
    {
        set
        {
            if (stream != null)
            {
                SDL_SetAudioStreamGain(stream, value ? 0f : 1f);
            }
        }
    }

    // SDL_AudioStreamCallback: the device needs additionalAmount more bytes, taken from the buffer where it got to
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Fill(IntPtr userdata, SDL_AudioStream* stream, int additionalAmount, int totalAmount)
    {
        var device = (SdlSoundDma)GCHandle.FromIntPtr(userdata).Target!;
        Dma? shm = device.shm;
        if (shm == null)
        {
            return;
        }

        int position = device.clock != null ? device.ClockPosition() : device.playPosition;
        int remaining = additionalAmount - (additionalAmount % 4);
        fixed (byte* buffer = shm.buffer)
        {
            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, shm.buffer.Length - position);
                SDL_PutAudioStreamData(stream, (IntPtr)(buffer + position), chunk);
                position = (position + chunk) % shm.buffer.Length;
                remaining -= chunk;
            }
        }

        Volatile.Write(ref device.playPosition, position);
    }
}
