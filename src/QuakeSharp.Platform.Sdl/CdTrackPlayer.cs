using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NVorbis;
using QuakeSharp.Core;
using SDL;
using static SDL.SDL3;

namespace QuakeSharp.Platform.Sdl;

/// <summary>
/// Plays a CD track's file on its own SDL3 audio stream on the default device, which SDL mixes with the game's sound:
/// the CD's music never passes through Quake's mixer (design.md §7.2). Ogg Vorbis is decoded by NVorbis, WAV by SDL.
/// <see cref="Ended"/> is raised on SDL's audio thread when a track not looping has played to its end.
/// </summary>
public sealed unsafe class CdTrackPlayer : IDisposable
{
    private readonly Lock sync = new();
    private SDL_AudioStream* stream;
    private GCHandle self;
    private ITrackSource? source;
    private bool looping;
    private bool audio;

    /// <summary>A track not looping has played to its end (raised on SDL's audio thread).</summary>
    public Action? Ended { get; set; }

    /// <summary>The length of a track's file.</summary>
    public static TimeSpan Length(string path)
    {
        using ITrackSource track = Open(path);
        return track.Length;
    }

    /// <summary>Plays a file from its start, round and round when <paramref name="loop"/>.</summary>
    public void Play(string path, bool loop)
    {
        Stop();
        if (!audio)
        {
            if (!SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO))
            {
                return;
            }

            audio = true;
        }

        ITrackSource track;
        try
        {
            track = Open(path);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or QuakeException)
        {
            Console.Error.WriteLine($"{path}: {e.Message}");
            return;
        }

        lock (sync)
        {
            source = track;
            looping = loop;
            if (!self.IsAllocated)
            {
                self = GCHandle.Alloc(this);
            }

            SDL_AudioSpec spec = track.Spec;
            stream = SDL_OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, &Fill, GCHandle.ToIntPtr(self));
            if (stream == null)
            {
                source.Dispose();
                source = null;
                return;
            }
        }

        SDL_ResumeAudioStreamDevice(stream);
    }

    /// <summary>Holds the track where it is.</summary>
    public void Pause()
    {
        if (stream != null)
        {
            SDL_PauseAudioStreamDevice(stream);
        }
    }

    /// <summary>Goes on from where <see cref="Pause"/> held it.</summary>
    public void Resume()
    {
        if (stream != null)
        {
            SDL_ResumeAudioStreamDevice(stream);
        }
    }

    /// <summary>Whether a track is loaded (playing or paused).</summary>
    public bool Loaded
    {
        get
        {
            lock (sync)
            {
                return source != null;
            }
        }
    }

    public void Stop()
    {
        SDL_AudioStream* old;
        lock (sync)
        {
            old = stream;
            stream = null;
            source?.Dispose();
            source = null;
        }

        // outside the lock: destroying the stream waits for its callback, which takes the lock
        if (old != null)
        {
            SDL_DestroyAudioStream(old);
        }
    }

    public void Dispose()
    {
        Stop();
        if (self.IsAllocated)
        {
            self.Free();
        }

        if (audio)
        {
            SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO);
            audio = false;
        }
    }

    private static ITrackSource Open(string path) =>
        Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase) ? new WavSource(path) : new OggSource(path);

    // SDL_AudioStreamCallback: the device needs additionalAmount more bytes
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Fill(IntPtr userdata, SDL_AudioStream* stream, int additionalAmount, int totalAmount)
    {
        var player = (CdTrackPlayer)GCHandle.FromIntPtr(userdata).Target!;
        bool ended = false;
        lock (player.sync)
        {
            ITrackSource? source = player.source;
            if (source == null || stream != player.stream)
            {
                return;
            }

            byte[] buffer = new byte[Math.Max(additionalAmount, 0)];
            int filled = 0;
            while (filled < buffer.Length)
            {
                int read = source.Read(buffer.AsSpan(filled));
                filled += read;
                if (read == 0)
                {
                    if (!player.looping)
                    {
                        ended = true;
                        break;
                    }

                    source.Rewind();
                    if (source.Read(buffer.AsSpan(filled, 0)) == 0 && source.Length == TimeSpan.Zero)
                    {
                        break; // an empty file: nothing to loop
                    }
                }
            }

            fixed (byte* data = buffer)
            {
                SDL_PutAudioStreamData(stream, (IntPtr)data, filled);
            }

            if (ended)
            {
                source.Dispose();
                player.source = null;
            }
        }

        if (ended)
        {
            player.Ended?.Invoke();
        }
    }

    // a track's samples in the format of its Spec
    private interface ITrackSource : IDisposable
    {
        SDL_AudioSpec Spec { get; }

        TimeSpan Length { get; }

        int Read(Span<byte> buffer);

        void Rewind();
    }

    // Ogg Vorbis, decoded by NVorbis into 32-bit floats
    private sealed class OggSource : ITrackSource
    {
        private readonly VorbisReader reader;
        private float[] samples = [];

        public OggSource(string path)
        {
            reader = new VorbisReader(path);
            Spec = new SDL_AudioSpec { format = SDL_AudioFormat.SDL_AUDIO_F32LE, channels = reader.Channels, freq = reader.SampleRate };
        }

        public SDL_AudioSpec Spec { get; }

        public TimeSpan Length => reader.TotalTime;

        public int Read(Span<byte> buffer)
        {
            // whole sample frames
            int count = buffer.Length / sizeof(float) / reader.Channels * reader.Channels;
            if (samples.Length < count)
            {
                samples = new float[count];
            }

            int read = reader.ReadSamples(samples, 0, count);
            MemoryMarshal.AsBytes(samples.AsSpan(0, read)).CopyTo(buffer);
            return read * sizeof(float);
        }

        public void Rewind() => reader.SeekTo(0);

        public void Dispose() => reader.Dispose();
    }

    // WAV, loaded by SDL in its own format
    private sealed class WavSource : ITrackSource
    {
        private readonly byte[] data;
        private int position;

        public WavSource(string path)
        {
            SDL_AudioSpec spec;
            byte* audio;
            uint length;
            if (!SDL_LoadWAV(path, &spec, &audio, &length))
            {
                throw new InvalidDataException(SDL_GetError() ?? "SDL_LoadWAV failed");
            }

            data = new ReadOnlySpan<byte>(audio, (int)length).ToArray();
            SDL_free(audio);
            Spec = spec;
            int frame = SDL_AUDIO_FRAMESIZE(spec);
            Length = TimeSpan.FromSeconds((double)data.Length / Math.Max(frame, 1) / Math.Max(spec.freq, 1));
        }

        public SDL_AudioSpec Spec { get; }

        public TimeSpan Length { get; }

        public int Read(Span<byte> buffer)
        {
            int count = Math.Min(buffer.Length, data.Length - position);
            data.AsSpan(position, count).CopyTo(buffer);
            position += count;
            return count;
        }

        public void Rewind() => position = 0;

        public void Dispose()
        {
        }
    }
}
