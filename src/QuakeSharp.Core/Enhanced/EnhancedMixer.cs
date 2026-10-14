using QuakeSharp.Core.Sound;

namespace QuakeSharp.Core.Enhanced;

/// <summary>
/// The enhanced mode's sound (design.md §11.6, step 7; decision 63): a second mixer that follows WinQuake's channels
/// and paints the span snd_dma.c's S_Update_ is about to paint, at four times WinQuake's 11025 Hz (44.1 kHz) into a
/// ring four times as long as WinQuake's buffer, so the two wrap together and a position in one is one in the other:
/// each channel's samples from where S_PaintChannels will take them (its pos, its end, its loop), resampled from the
/// sound's own rate with Hermite interpolation, its volumes (SND_Spatialize's, the volume cvar's) ramped over a few
/// milliseconds instead of stepping at each update, in floating point. It reads the channels and never writes them:
/// WinQuake's mixer still paints its own buffer every frame, which the game depends on. Unlike S_PaintChannels it
/// paints a loop's restart where it falls (snd_mix.c paints it again from the start of its 512-sample chunk).
/// </summary>
public sealed class EnhancedMixer
{
    /// <summary>How many of the ring's frames there are to each of the game's samples.</summary>
    public const int Upsample = 4;

    /// <summary>The output's rate (four times WinQuake's 11025 Hz).</summary>
    public const int Rate = 11025 * Upsample;

    /// <summary>The ring's length in sample frames: four times WinQuake's buffer (16384 stereo samples, 1.49 s).</summary>
    public const int RingFrames = 0x10000 / 4 * Upsample;

    // a volume change is ramped over this many output frames (5 ms)
    private const int RampFrames = 240;

    // the channels' last volumes (0-1), by slot
    private readonly float[] lastLeft = new float[QuakeEngine.MAX_CHANNELS];
    private readonly float[] lastRight = new float[QuakeEngine.MAX_CHANNELS];

    // placement by direction: each slot's ears' low-pass state
    private readonly float[] lowLeft = new float[QuakeEngine.MAX_CHANNELS];
    private readonly float[] lowRight = new float[QuakeEngine.MAX_CHANNELS];

    // the listener (snd_dma.c's listener_origin, listener_forward, listener_right, and cl.viewentity)
    private Common.Vec3 listenerOrigin, listenerForward, listenerRight;
    private int viewEntity;

    /// <summary>
    /// Sounds placed by direction (enhanced_3d_sfx, decision 63, off by default), as DoomSharp's: equal-power panning
    /// by how far to the side a sound is (in place of SND_Spatialize's 1 ± dot), the far ear up to 0.66 ms later and
    /// duller (a one-pole low-pass down to 3.5 kHz, the head's shadow), sounds from behind quieter and duller (down to
    /// 5 kHz). The view entity's own sounds are not placed (SND_Spatialize gives them full volume on both sides).
    /// </summary>
    public bool Placement { get; set; }

    /// <summary>The room the mix goes through (enhanced_reverb, design.md §11.9; null: none).</summary>
    public EnhancedReverb? Reverb { get; set; }

    /// <summary>The listener as S_Update set it, for the placement.</summary>
    public void Listen(Common.Vec3 origin, Common.Vec3 forward, Common.Vec3 right, int viewentity)
    {
        listenerOrigin = origin;
        listenerForward = forward;
        listenerRight = right;
        viewEntity = viewentity;
    }

    /// <summary>The ring the device plays: stereo float frames at <see cref="Rate"/>; the game's sample t (paintedtime's
    /// time) is frames 4t to 4t + 3, at index 4t &amp; (<see cref="RingFrames"/> - 1).</summary>
    public float[] Ring { get; } = new float[RingFrames * 2];

    /// <summary>The ring's first frame of a game sample.</summary>
    public static long FrameOf(long gameSample) => gameSample * Upsample;

    /// <summary>snd_dma.c: S_ClearBuffer: the ring silent.</summary>
    public void Clear()
    {
        Array.Clear(Ring);
        Reverb?.Clear();
    }

    /// <summary>
    /// Paints the game's samples from <paramref name="from"/> to <paramref name="to"/> (paintedtime to S_Update_'s
    /// endtime) from <paramref name="channels"/> (the first <paramref name="count"/>) as they are before
    /// S_PaintChannels advances them, at <paramref name="volume"/> (the volume cvar).
    /// </summary>
    public void Paint(Channel[] channels, int count, int from, int to, float volume)
    {
        if (to <= from)
        {
            return;
        }

        long first = FrameOf(from), last = FrameOf(to);
        for (long n = first; n < last; n++)
        {
            int i = (int)(n & (RingFrames - 1)) * 2;
            Ring[i] = Ring[i + 1] = 0;
        }

        const double step = 1.0 / Upsample;
        for (int c = 0; c < count && c < channels.Length; c++)
        {
            Channel ch = channels[c];
            SfxCache? sc = ch.sfx?.cache;
            if (sc == null || sc.length <= 0 || (ch.leftvol == 0 && ch.rightvol == 0))
            {
                continue;
            }

            // snd_mix.c's scale: an 8-bit sample times (vol & ~7), a 16-bit one times vol / 256, then volume * 256 >> 8
            float scale = (sc.width == 1 ? 1f / 128 : 1f / 32768) * volume / 255;
            float left = ch.leftvol * scale, right = ch.rightvol * scale;
            double delayLeft = 0, delayRight = 0;
            float cutLeft = 1, cutRight = 1;
            bool placed = Placement && ch.entnum != viewEntity && Place(ch, ref left, ref right, out delayLeft, out delayRight, out cutLeft, out cutRight);
            float fromLeft = lastLeft[c], fromRight = lastRight[c];
            lastLeft[c] = left;
            lastRight[c] = right;

            // S_PaintChannels' timeline for the channel: its pos at from, to its end, then its loop or its stop
            int pos = ch.pos, end = ch.end;
            int ltime = from;
            int ramp = 0;
            while (ltime < to)
            {
                int stop = Math.Min(end, to);
                if (stop > ltime)
                {
                    for (long n = FrameOf(ltime); n < FrameOf(stop); n++)
                    {
                        double t = (n * step) - ltime;
                        float r = ramp < RampFrames ? (float)ramp++ / RampFrames : 1;
                        int i = (int)(n & (RingFrames - 1)) * 2;
                        if (placed)
                        {
                            lowLeft[c] += cutLeft * (Sample(sc, pos + t - delayLeft) - lowLeft[c]);
                            lowRight[c] += cutRight * (Sample(sc, pos + t - delayRight) - lowRight[c]);
                            Ring[i] += lowLeft[c] * (fromLeft + ((left - fromLeft) * r));
                            Ring[i + 1] += lowRight[c] * (fromRight + ((right - fromRight) * r));
                            continue;
                        }

                        float sample = Sample(sc, pos + t);
                        Ring[i] += sample * (fromLeft + ((left - fromLeft) * r));
                        Ring[i + 1] += sample * (fromRight + ((right - fromRight) * r));
                    }

                    pos += stop - ltime;
                    ltime = stop;
                }

                if (ltime >= end)
                {
                    if (sc.loopstart < 0)
                    {
                        break;
                    }

                    pos = sc.loopstart;
                    end = ltime + sc.length - pos;
                }
            }
        }

        // what was painted in the game's sound is limited to its 16 bits: here to -1 to 1 (after the room, if any)
        for (long n = first; n < last; n++)
        {
            int i = (int)(n & (RingFrames - 1)) * 2;
            if (Reverb != null)
            {
                (Ring[i], Ring[i + 1]) = Reverb.Process(Ring[i], Ring[i + 1]);
            }

            Ring[i] = Math.Clamp(Ring[i], -1, 1);
            Ring[i + 1] = Math.Clamp(Ring[i + 1], -1, 1);
        }
    }

    // The placement of a sound: its volumes from SND_Spatialize's (their mean is its distance's loudness), the far ear's
    // delay in the game's samples, and each ear's low-pass coefficient
    private bool Place(Channel ch, ref float left, ref float right, out double delayLeft, out double delayRight, out float cutLeft, out float cutRight)
    {
        delayLeft = delayRight = 0;
        cutLeft = cutRight = 1;
        double dx = ch.origin[0] - listenerOrigin[0], dy = ch.origin[1] - listenerOrigin[1], dz = ch.origin[2] - listenerOrigin[2];
        double length = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        if (length < 1)
        {
            return false; // on the listener: as SND_Spatialize has it
        }

        double side = Math.Clamp(((dx * listenerRight[0]) + (dy * listenerRight[1]) + (dz * listenerRight[2])) / length, -1, 1);
        double front = ((dx * listenerForward[0]) + (dy * listenerForward[1]) + (dz * listenerForward[2])) / length;
        float loudness = (left + right) / 2;
        float behind = front < 0 ? (float)(1 - (0.3 * -front)) : 1;
        left = loudness * (float)Math.Sqrt(1 - side) * behind;
        right = loudness * (float)Math.Sqrt(1 + side) * behind;

        // the far ear: up to 0.66 ms later and down to 3.5 kHz; from behind, both down to 5 kHz
        double far = Math.Abs(side) * 0.00066 * Rate / Upsample;
        double farCut = 20000 + ((3500 - 20000) * Math.Abs(side));
        double rearCut = front < 0 ? 20000 + ((5000 - 20000) * -front) : 20000;
        double cutL = Math.Min(rearCut, side > 0 ? farCut : 20000), cutR = Math.Min(rearCut, side < 0 ? farCut : 20000);
        if (side > 0)
        {
            delayLeft = far;
        }
        else
        {
            delayRight = far;
        }

        cutLeft = (float)(1 - Math.Exp(-2 * Math.PI * cutL / Rate));
        cutRight = (float)(1 - Math.Exp(-2 * Math.PI * cutR / Rate));
        return true;
    }

    /// <summary>The sound's sample at a fractional position, by Hermite interpolation of the four around it (past the
    /// ends, the end's; a looping sound's samples past its end come from its loop).</summary>
    public static float Sample(SfxCache sc, double position)
    {
        int i = (int)Math.Floor(position);
        float f = (float)(position - i);
        float y0 = At(sc, i - 1), y1 = At(sc, i), y2 = At(sc, i + 1), y3 = At(sc, i + 2);
        float c1 = 0.5f * (y2 - y0);
        float c2 = y0 - (2.5f * y1) + (2f * y2) - (0.5f * y3);
        float c3 = (0.5f * (y3 - y0)) + (1.5f * (y1 - y2));
        return (((((c3 * f) + c2) * f) + c1) * f) + y1;
    }

    private static float At(SfxCache sc, int i)
    {
        if (i >= sc.length)
        {
            if (sc.loopstart < 0 || sc.loopstart >= sc.length)
            {
                i = sc.length - 1;
            }
            else
            {
                i = sc.loopstart + ((i - sc.loopstart) % (sc.length - sc.loopstart));
            }
        }

        if (i < 0)
        {
            i = 0;
        }

        return sc.width == 1 ? (sbyte)sc.data[i] : (short)(sc.data[i * 2] | (sc.data[(i * 2) + 1] << 8));
    }
}
