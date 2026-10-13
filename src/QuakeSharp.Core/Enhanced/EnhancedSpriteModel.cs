using QuakeSharp.Core.Model;
using QuakeSharp.Core.Numerics;

namespace QuakeSharp.Core.Enhanced;

/// <summary>A sprite frame: its palette indices (255 clear) and how far it reaches from the origin each way.</summary>
public sealed record EnhancedSpriteFrame(int Width, int Height, float Up, float Down, float Left, float Right, byte[] Pixels);

/// <summary>
/// A sprite (.spr) made ready for the GPU (design.md §11.6, step 5): its frames, its groups with their intervals
/// (chosen as R_GetSpriteframe chooses them), its type and beam length for r_sprite.c's orientations. Never written
/// after it is made.
/// </summary>
public sealed class EnhancedSpriteModel
{
    public required string Name { get; init; }

    /// <summary>SPR_VP_PARALLEL_UPRIGHT, SPR_FACING_UPRIGHT, SPR_VP_PARALLEL, SPR_ORIENTED, SPR_VP_PARALLEL_ORIENTED.</summary>
    public required int Type { get; init; }

    public required float BeamLength { get; init; }

    public required EnhancedSpriteFrame[] Pictures { get; init; }

    /// <summary>For each frame, its pictures (one, or a group's) and the group's intervals.</summary>
    public required (int[] Pictures, float[] Intervals)[] Frames { get; init; }

    public static EnhancedSpriteModel Build(Model.Model model, MSprite sprite)
    {
        var pictures = new List<EnhancedSpriteFrame>();
        var frames = new (int[], float[])[sprite.numframes];
        for (int f = 0; f < sprite.numframes; f++)
        {
            MSpriteFrameDesc desc = sprite.frames[f];
            if (desc.type == spriteframetype_t.SPR_SINGLE)
            {
                frames[f] = ([Add(pictures, (MSpriteFrame)desc.frameptr)], []);
            }
            else
            {
                var group = (MSpriteGroup)desc.frameptr;
                frames[f] = ([.. group.frames.Take(group.numframes).Select(p => Add(pictures, p))], group.intervals[..group.numframes]);
            }
        }

        return new EnhancedSpriteModel
        {
            Name = model.name,
            Type = sprite.type,
            BeamLength = sprite.beamlength,
            Pictures = [.. pictures],
            Frames = frames,
        };
    }

    /// <summary>r_sprite.c: R_GetSpriteframe, a read-only copy: the picture shown for <paramref name="frame"/> at
    /// <paramref name="time"/> (cl.time + syncbase); a frame out of range is 0 (the C prints a line; this does not).</summary>
    public int Picture(int frame, double time)
    {
        if (frame >= Frames.Length || frame < 0)
        {
            frame = 0;
        }

        (int[] pictures, float[] intervals) = Frames[frame];
        if (intervals.Length == 0)
        {
            return pictures[0];
        }

        float full = intervals[^1];
        float t = (float)time;
        float target = t - (X86.FtoI(t / full) * full);
        int i = 0;
        while (i < pictures.Length - 1 && intervals[i] <= target)
        {
            i++;
        }

        return pictures[i];
    }

    /// <summary>
    /// r_sprite.c: R_DrawSprite's axes for the sprite's type, a read-only copy (in double precision): its up and right
    /// and where its origin goes (R_RotateSprite's beam length along -vpn); null where the C draws nothing (an upright
    /// sprite seen from straight above or below).
    /// </summary>
    public (double[] Up, double[] Right, double[] Origin)? Axes(EnhancedView view, Common.Vec3 origin, Common.Vec3 angles)
    {
        (double[] vpn, double[] vright, double[] vup) = EnhancedCamera.AngleVectors(view.Angles[0], view.Angles[1], view.Angles[2]);
        double[] up, right, forward;
        switch (Type)
        {
            case SpriteGen.SPR_FACING_UPRIGHT:
            {
                double[] t = [origin[0] - view.Origin[0], origin[1] - view.Origin[1], origin[2] - view.Origin[2]];
                Normalize(t);
                if (t[2] > 0.999848 || t[2] < -0.999848)
                {
                    return null;
                }

                up = [0, 0, 1];
                right = [t[1], -t[0], 0];
                Normalize(right);
                forward = [-right[1], right[0], 0];
                break;
            }

            case SpriteGen.SPR_VP_PARALLEL:
                (up, right, forward) = (vup, vright, vpn);
                break;

            case SpriteGen.SPR_VP_PARALLEL_UPRIGHT:
                if (vpn[2] > 0.999848 || vpn[2] < -0.999848)
                {
                    return null;
                }

                up = [0, 0, 1];
                right = [vpn[1], -vpn[0], 0];
                Normalize(right);
                forward = [-right[1], right[0], 0];
                break;

            case SpriteGen.SPR_ORIENTED:
                (forward, right, up) = EnhancedCamera.AngleVectors(angles[0], angles[1], angles[2]);
                break;

            case SpriteGen.SPR_VP_PARALLEL_ORIENTED:
            {
                double a = angles[2] * Math.PI / 180, sr = Math.Sin(a), cr = Math.Cos(a);
                forward = vpn;
                right = [.. Enumerable.Range(0, 3).Select(i => (vright[i] * cr) + (vup[i] * sr))];
                up = [.. Enumerable.Range(0, 3).Select(i => (vright[i] * -sr) + (vup[i] * cr))];
                break;
            }

            default:
                return null; // the C stops the game here (Sys_Error); a snapshot never holds one
        }

        double[] at = [origin[0] - (forward[0] * BeamLength), origin[1] - (forward[1] * BeamLength), origin[2] - (forward[2] * BeamLength)];
        return (up, right, at);
    }

    private static int Add(List<EnhancedSpriteFrame> pictures, MSpriteFrame f)
    {
        pictures.Add(new EnhancedSpriteFrame(f.width, f.height, f.up, f.down, f.left, f.right, f.pixels[..(f.width * f.height)]));
        return pictures.Count - 1;
    }

    private static void Normalize(double[] v)
    {
        double length = Math.Sqrt((v[0] * v[0]) + (v[1] * v[1]) + (v[2] * v[2]));
        if (length != 0)
        {
            v[0] /= length;
            v[1] /= length;
            v[2] /= length;
        }
    }
}
