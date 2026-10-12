using System.Buffers.Binary;
using QuakeSharp.Core.Sound;

namespace QuakeSharp.Core;

// snd_mem.c: sound caching. A loaded sound stays loaded (managed memory, design.md §3.2).
public sealed partial class QuakeEngine
{
    public int cache_full_cycle;

    // snd_mem.c: the WAV parser's data_p, iff_end, last_chunk, iff_data (indices in wav; -1 for NULL),
    // iff_chunk_len
    private byte[] wav = [];
    private int data_p;
    private int iff_end;
    private int last_chunk;
    private int iff_data;
    private int iff_chunk_len;

    // snd_mem.c: ResampleSfx
    private void ResampleSfx(Sfx sfx, int inrate, int inwidth, byte[] data, int dataofs)
    {
        int outcount;
        int srcsample;
        float stepscale;
        int i;
        int sample, samplefrac, fracstep;
        SfxCache? sc;

        sc = sfx.cache;
        if (sc == null)
        {
            return;
        }

        stepscale = (float)inrate / shm!.speed; // this is usually 0.5, 1, or 2

        outcount = (int)(sc.length / stepscale);
        sc.length = outcount;
        if (sc.loopstart != -1)
        {
            sc.loopstart = (int)(sc.loopstart / stepscale);
        }

        sc.speed = shm.speed;
        if (loadas8bit.value != 0)
        {
            sc.width = 1;
        }
        else
        {
            sc.width = inwidth;
        }

        sc.stereo = 0;

        // resample / decimate to the current source rate
        if (stepscale == 1 && inwidth == 1 && sc.width == 1)
        {
            // fast special case
            for (i = 0; i < outcount; i++)
            {
                sc.data[i] = (byte)(sbyte)(data[dataofs + i] - 128);
            }
        }
        else
        {
            // general case
            samplefrac = 0;
            fracstep = (int)(stepscale * 256);
            for (i = 0; i < outcount; i++)
            {
                srcsample = samplefrac >> 8;
                samplefrac += fracstep;
                if (inwidth == 2)
                {
                    sample = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(dataofs + (srcsample * 2)));
                }
                else
                {
                    sample = (data[dataofs + srcsample] - 128) << 8;
                }

                if (sc.width == 2)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(sc.data.AsSpan(i * 2), (short)sample);
                }
                else
                {
                    sc.data[i] = (byte)(sbyte)(sample >> 8);
                }
            }
        }
    }

    // =============================================================================

    // snd_mem.c: S_LoadSound
    public SfxCache? S_LoadSound(Sfx s)
    {
        string namebuffer;
        byte[]? data;
        WavInfo info;
        int len;
        float stepscale;
        SfxCache sc;

        // see if still in memory
        if (s.cache != null)
        {
            return s.cache;
        }

        // load it in
        namebuffer = "sound/" + s.name;

        data = COM_LoadStackFile(namebuffer);

        if (data == null)
        {
            Con_Printf("Couldn't load %s\n", namebuffer);
            return null;
        }

        info = GetWavinfo(s.name, data, com_filesize);
        if (info.channels != 1)
        {
            Con_Printf("%s is a stereo sample\n", s.name);
            return null;
        }

        stepscale = (float)info.rate / shm!.speed;
        len = (int)(info.samples / stepscale);

        len = len * info.width * info.channels;

        sc = new SfxCache { data = new byte[len] };
        s.cache = sc;

        sc.length = info.samples;
        sc.loopstart = info.loopstart;
        sc.speed = info.rate;
        sc.width = info.width;
        sc.stereo = info.channels;

        ResampleSfx(s, sc.speed, sc.width, data, info.dataofs);

        return sc;
    }

    // ===============================================================================
    // WAV loading
    // ===============================================================================

    // A byte of the file being parsed (past its end, the 0 the loader appends and then zeros: C reads on)
    private byte WavByte(int p) => p >= 0 && p < wav.Length ? wav[p] : (byte)0;

    // snd_mem.c: GetLittleShort
    private short GetLittleShort()
    {
        short val = 0;
        val = WavByte(data_p);
        val = (short)(val + (WavByte(data_p + 1) << 8));
        data_p += 2;
        return val;
    }

    // snd_mem.c: GetLittleLong
    private int GetLittleLong()
    {
        int val = 0;
        val = WavByte(data_p);
        val += WavByte(data_p + 1) << 8;
        val += WavByte(data_p + 2) << 16;
        val += WavByte(data_p + 3) << 24;
        data_p += 4;
        return val;
    }

    // strncmp (p, name, 4) on the file's bytes
    private bool WavNameIs(int p, string name)
    {
        for (int i = 0; i < 4; i++)
        {
            int c = WavByte(p + i);
            if (c != name[i])
            {
                return false;
            }

            if (c == 0)
            {
                return true;
            }
        }

        return true;
    }

    // snd_mem.c: FindNextChunk
    private void FindNextChunk(string name)
    {
        while (true)
        {
            data_p = last_chunk;

            if (data_p >= iff_end)
            {
                // didn't find the chunk
                data_p = -1;
                return;
            }

            data_p += 4;
            iff_chunk_len = GetLittleLong();
            if (iff_chunk_len < 0)
            {
                data_p = -1;
                return;
            }

            data_p -= 8;
            last_chunk = data_p + 8 + ((iff_chunk_len + 1) & ~1);
            if (WavNameIs(data_p, name))
            {
                return;
            }
        }
    }

    // snd_mem.c: FindChunk
    private void FindChunk(string name)
    {
        last_chunk = iff_data;
        FindNextChunk(name);
    }

    // snd_mem.c: GetWavinfo
    public WavInfo GetWavinfo(string name, byte[]? wav, int wavlength)
    {
        WavInfo info = default;
        int i;
        int format;
        int samples;

        if (wav == null)
        {
            return info;
        }

        this.wav = wav;
        iff_data = 0;
        iff_end = wavlength;

        // find "RIFF" chunk
        FindChunk("RIFF");
        if (!(data_p >= 0 && WavNameIs(data_p + 8, "WAVE")))
        {
            Con_Printf("Missing RIFF/WAVE chunks\n");
            return info;
        }

        // get "fmt " chunk
        iff_data = data_p + 12;

        FindChunk("fmt ");
        if (data_p < 0)
        {
            Con_Printf("Missing fmt chunk\n");
            return info;
        }

        data_p += 8;
        format = GetLittleShort();
        if (format != 1)
        {
            Con_Printf("Microsoft PCM format only\n");
            return info;
        }

        info.channels = GetLittleShort();
        info.rate = GetLittleLong();
        data_p += 4 + 2;
        info.width = GetLittleShort() / 8;

        // get cue chunk
        FindChunk("cue ");
        if (data_p >= 0)
        {
            data_p += 32;
            info.loopstart = GetLittleLong();

            // if the next chunk is a LIST chunk, look for a cue length marker
            FindNextChunk("LIST");
            if (data_p >= 0)
            {
                if (WavNameIs(data_p + 28, "mark"))
                {
                    // this is not a proper parse, but it works with cooledit...
                    data_p += 24;
                    i = GetLittleLong(); // samples in loop
                    info.samples = info.loopstart + i;
                }
            }
        }
        else
        {
            info.loopstart = -1;
        }

        // find data chunk
        FindChunk("data");
        if (data_p < 0)
        {
            Con_Printf("Missing data chunk\n");
            return info;
        }

        data_p += 4;
        samples = GetLittleLong() / info.width;

        if (info.samples != 0)
        {
            if (samples < info.samples)
            {
                throw Sys_Error("Sound %s has a bad loop length", name);
            }
        }
        else
        {
            info.samples = samples;
        }

        info.dataofs = data_p;

        return info;
    }
}
