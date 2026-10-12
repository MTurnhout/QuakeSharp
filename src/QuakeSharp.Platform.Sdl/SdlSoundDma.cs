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
/// (design.md §7.1).
/// </summary>
public sealed unsafe class SdlSoundDma : ISoundDma, IDisposable
{
    // snd_win.c: SECONDARY_BUFFER_SIZE
    private const int SecondaryBufferSize = 0x10000;

    private readonly int speed;
    private SDL_AudioStream* stream;
    private GCHandle self;
    private Dma? shm;

    // Bytes of the buffer the device has taken so far, wrapping (written on SDL's audio thread)
    private int playPosition;

    /// <param name="speed">The mix rate (11025 Hz, as snd_win.c).</param>
    public SdlSoundDma(int speed = 11025)
    {
        this.speed = speed;
    }

    // snd_win.c: SNDDMA_Init
    public Dma? Init()
    {
        if (!SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO))
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

        self = GCHandle.Alloc(this);
        var spec = new SDL_AudioSpec { format = SDL_AudioFormat.SDL_AUDIO_S16LE, channels = shm.channels, freq = shm.speed };
        stream = SDL_OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, &Fill, GCHandle.ToIntPtr(self));
        if (stream == null)
        {
            self.Free();
            SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO);
            shm = null;
            return null;
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

        int s = Volatile.Read(ref playPosition) / (shm.samplebits / 8);
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

        int position = device.playPosition;
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
