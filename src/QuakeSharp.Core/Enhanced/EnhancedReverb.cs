namespace QuakeSharp.Core.Enhanced;

/// <summary>
/// The enhanced mode's reverb (design.md §11.9, enhanced_reverb, off by default): Jezar's Freeverb at the mixer's 44100
/// Hz, eight damped comb filters and four all-passes a side (the right side's delays 23 frames longer), mixed over the
/// dry sound by <see cref="Wet"/>. One room for every place: the game has no rooms' sizes to give it.
/// </summary>
public sealed class EnhancedReverb
{
    private static readonly int[] CombDelays = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    private static readonly int[] AllpassDelays = [556, 441, 341, 225];
    private const int Spread = 23;
    private const float FixedGain = 0.015f;
    private const float AllpassFeedback = 0.5f;

    private readonly float[][] combs = new float[16][];
    private readonly int[] combAt = new int[16];
    private readonly float[] combLow = new float[16];
    private readonly float[][] allpasses = new float[8][];
    private readonly int[] allpassAt = new int[8];
    private readonly float feedback;
    private readonly float damping;

    /// <param name="wet">How much of the reverb goes over the dry sound, 0..1.</param>
    /// <param name="room">The room's size, 0..1 (the combs' feedback 0.7 to 0.98).</param>
    /// <param name="damp">How quickly the high frequencies die, 0..1.</param>
    public EnhancedReverb(float wet, float room = 0.6f, float damp = 0.5f)
    {
        Wet = Math.Clamp(wet, 0f, 1f);
        feedback = 0.7f + (0.28f * Math.Clamp(room, 0f, 1f));
        damping = 0.4f * Math.Clamp(damp, 0f, 1f);
        for (int i = 0; i < 8; i++)
        {
            combs[i] = new float[CombDelays[i]];
            combs[i + 8] = new float[CombDelays[i] + Spread];
        }

        for (int i = 0; i < 4; i++)
        {
            allpasses[i] = new float[AllpassDelays[i]];
            allpasses[i + 4] = new float[AllpassDelays[i] + Spread];
        }
    }

    /// <summary>The reverb's share over the dry sound.</summary>
    public float Wet { get; }

    /// <summary>One stereo frame through the room: the dry sound with the reverb added.</summary>
    public (float Left, float Right) Process(float left, float right)
    {
        float input = (left + right) * FixedGain;
        float outLeft = 0, outRight = 0;
        for (int i = 0; i < 8; i++)
        {
            outLeft += Comb(i, input);
            outRight += Comb(i + 8, input);
        }

        for (int i = 0; i < 4; i++)
        {
            outLeft = Allpass(i, outLeft);
            outRight = Allpass(i + 4, outRight);
        }

        // Freeverb's wet gain of 3 (its scalewet), with its width 1: each side its own
        return (left + (outLeft * 3 * Wet), right + (outRight * 3 * Wet));
    }

    /// <summary>The room silent again.</summary>
    public void Clear()
    {
        foreach (float[] b in combs)
        {
            Array.Clear(b);
        }

        foreach (float[] b in allpasses)
        {
            Array.Clear(b);
        }

        Array.Clear(combLow);
    }

    // a comb filter whose feedback is low-passed (damped)
    private float Comb(int i, float input)
    {
        float[] buffer = combs[i];
        int at = combAt[i];
        float output = buffer[at];
        combLow[i] = (output * (1 - damping)) + (combLow[i] * damping);
        buffer[at] = input + (combLow[i] * feedback);
        combAt[i] = at + 1 == buffer.Length ? 0 : at + 1;
        return output;
    }

    private float Allpass(int i, float input)
    {
        float[] buffer = allpasses[i];
        int at = allpassAt[i];
        float delayed = buffer[at];
        float output = -input + delayed;
        buffer[at] = input + (delayed * AllpassFeedback);
        allpassAt[i] = at + 1 == buffer.Length ? 0 : at + 1;
        return output;
    }
}
