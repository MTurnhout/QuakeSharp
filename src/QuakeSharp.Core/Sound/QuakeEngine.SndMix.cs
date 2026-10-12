using System.Buffers.Binary;
using QuakeSharp.Core.Sound;

namespace QuakeSharp.Core;

// snd_mix.c: portable code to mix sounds for snd_dma.c
public sealed partial class QuakeEngine
{
    private const int PAINTBUFFER_SIZE = 512;

    public readonly PortableSamplePair[] paintbuffer = new PortableSamplePair[PAINTBUFFER_SIZE];

    public readonly int[][] snd_scaletable = [.. Enumerable.Range(0, 32).Select(_ => new int[256])];

    // snd_mix.c: snd_p (an index in the paint buffer's ints: left, right, left...), snd_linear_count, snd_vol;
    // snd_out (an index in the DMA buffer's shorts)
    private int snd_p, snd_linear_count, snd_vol;
    private int snd_out;

    // snd_mix.c: Snd_WriteLinearBlastStereo16
    private void Snd_WriteLinearBlastStereo16()
    {
        int i;
        int val;
        Span<byte> pbuf = shm!.buffer;

        for (i = 0; i < snd_linear_count; i += 2)
        {
            val = (PaintSample(snd_p + i) * snd_vol) >> 8;
            if (val > 0x7fff)
            {
                WriteShort(pbuf, snd_out + i, 0x7fff);
            }
            else if (val < short.MinValue)
            {
                WriteShort(pbuf, snd_out + i, short.MinValue);
            }
            else
            {
                WriteShort(pbuf, snd_out + i, val);
            }

            val = (PaintSample(snd_p + i + 1) * snd_vol) >> 8;
            if (val > 0x7fff)
            {
                WriteShort(pbuf, snd_out + i + 1, 0x7fff);
            }
            else if (val < short.MinValue)
            {
                WriteShort(pbuf, snd_out + i + 1, short.MinValue);
            }
            else
            {
                WriteShort(pbuf, snd_out + i + 1, val);
            }
        }
    }

    // (int *)paintbuffer: the paint buffer as ints, left and right in turn
    private int PaintSample(int i) => (i & 1) == 0 ? paintbuffer[i >> 1].left : paintbuffer[i >> 1].right;

    private static void WriteShort(Span<byte> buffer, int index, int value) =>
        BinaryPrimitives.WriteInt16LittleEndian(buffer[(index * 2)..], (short)value);

    // snd_mix.c: S_TransferStereo16
    private void S_TransferStereo16(int endtime)
    {
        int lpos;
        int lpaintedtime;

        snd_vol = (int)(volume.value * 256);

        snd_p = 0;
        lpaintedtime = paintedtime;

        while (lpaintedtime < endtime)
        {
            // handle recirculating buffer issues
            lpos = lpaintedtime & ((shm!.samples >> 1) - 1);

            snd_out = lpos << 1;

            snd_linear_count = (shm.samples >> 1) - lpos;
            if (lpaintedtime + snd_linear_count > endtime)
            {
                snd_linear_count = endtime - lpaintedtime;
            }

            snd_linear_count <<= 1;

            // write a linear blast of samples
            Snd_WriteLinearBlastStereo16();

            snd_p += snd_linear_count;
            lpaintedtime += snd_linear_count >> 1;
        }
    }

    // snd_mix.c: S_TransferPaintBuffer
    private void S_TransferPaintBuffer(int endtime)
    {
        int out_idx;
        int count;
        int out_mask;
        int p;
        int step;
        int val;
        int snd_vol;

        if (shm!.samplebits == 16 && shm.channels == 2)
        {
            S_TransferStereo16(endtime);
            return;
        }

        p = 0;
        count = (endtime - paintedtime) * shm.channels;
        out_mask = shm.samples - 1;
        out_idx = (paintedtime * shm.channels) & out_mask;
        step = 3 - shm.channels;
        snd_vol = (int)(volume.value * 256);

        Span<byte> pbuf = shm.buffer;

        if (shm.samplebits == 16)
        {
            while (count-- != 0)
            {
                val = (PaintSample(p) * snd_vol) >> 8;
                p += step;
                if (val > 0x7fff)
                {
                    val = 0x7fff;
                }
                else if (val < short.MinValue)
                {
                    val = short.MinValue;
                }

                WriteShort(pbuf, out_idx, val);
                out_idx = (out_idx + 1) & out_mask;
            }
        }
        else if (shm.samplebits == 8)
        {
            while (count-- != 0)
            {
                val = (PaintSample(p) * snd_vol) >> 8;
                p += step;
                if (val > 0x7fff)
                {
                    val = 0x7fff;
                }
                else if (val < short.MinValue)
                {
                    val = short.MinValue;
                }

                pbuf[out_idx] = (byte)((val >> 8) + 128);
                out_idx = (out_idx + 1) & out_mask;
            }
        }
    }

    // ===============================================================================
    // CHANNEL MIXING
    // ===============================================================================

    // snd_mix.c: S_PaintChannels
    public void S_PaintChannels(int endtime)
    {
        int i;
        int end;
        Channel ch;
        SfxCache? sc;
        int ltime, count;

        while (paintedtime < endtime)
        {
            // if paintbuffer is smaller than DMA buffer
            end = endtime;
            if (endtime - paintedtime > PAINTBUFFER_SIZE)
            {
                end = paintedtime + PAINTBUFFER_SIZE;
            }

            // clear the paint buffer
            Array.Clear(paintbuffer, 0, end - paintedtime);

            // paint in the channels.
            for (i = 0; i < total_channels; i++)
            {
                ch = channels[i];
                if (ch.sfx == null)
                {
                    continue;
                }

                if (ch.leftvol == 0 && ch.rightvol == 0)
                {
                    continue;
                }

                sc = S_LoadSound(ch.sfx);
                if (sc == null)
                {
                    continue;
                }

                ltime = paintedtime;

                while (ltime < end)
                {
                    // paint up to end
                    if (ch.end < end)
                    {
                        count = ch.end - ltime;
                    }
                    else
                    {
                        count = end - ltime;
                    }

                    if (count > 0)
                    {
                        if (sc.width == 1)
                        {
                            SND_PaintChannelFrom8(ch, sc, count);
                        }
                        else
                        {
                            SND_PaintChannelFrom16(ch, sc, count);
                        }

                        ltime += count;
                    }

                    // if at end of loop, restart
                    if (ltime >= ch.end)
                    {
                        if (sc.loopstart >= 0)
                        {
                            ch.pos = sc.loopstart;
                            ch.end = ltime + sc.length - ch.pos;
                        }
                        else
                        {
                            // channel just stopped
                            ch.sfx = null;
                            break;
                        }
                    }
                }
            }

            // transfer out according to DMA format
            S_TransferPaintBuffer(end);
            paintedtime = end;
        }
    }

    // snd_mix.c: SND_InitScaletable
    public void SND_InitScaletable()
    {
        int i, j;

        for (i = 0; i < 32; i++)
        {
            for (j = 0; j < 256; j++)
            {
                snd_scaletable[i][j] = (sbyte)j * i * 8;
            }
        }
    }

    // snd_mix.c: SND_PaintChannelFrom8 (the samples are read unsigned, as the C's unsigned char pointer has them)
    private void SND_PaintChannelFrom8(Channel ch, SfxCache sc, int count)
    {
        int data;
        int[] lscale, rscale;
        int sfx;
        int i;

        if (ch.leftvol > 255)
        {
            ch.leftvol = 255;
        }

        if (ch.rightvol > 255)
        {
            ch.rightvol = 255;
        }

        lscale = snd_scaletable[ch.leftvol >> 3];
        rscale = snd_scaletable[ch.rightvol >> 3];
        sfx = ch.pos;

        for (i = 0; i < count; i++)
        {
            data = sc.data[sfx + i];
            paintbuffer[i].left += lscale[data];
            paintbuffer[i].right += rscale[data];
        }

        ch.pos += count;
    }

    // snd_mix.c: SND_PaintChannelFrom16
    private void SND_PaintChannelFrom16(Channel ch, SfxCache sc, int count)
    {
        int data;
        int left, right;
        int leftvol, rightvol;
        int sfx;
        int i;

        leftvol = ch.leftvol;
        rightvol = ch.rightvol;
        sfx = ch.pos;

        for (i = 0; i < count; i++)
        {
            data = BinaryPrimitives.ReadInt16LittleEndian(sc.data.AsSpan((sfx + i) * 2));
            left = (data * leftvol) >> 8;
            right = (data * rightvol) >> 8;
            paintbuffer[i].left += left;
            paintbuffer[i].right += right;
        }

        ch.pos += count;
    }
}
