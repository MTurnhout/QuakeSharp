using System.Runtime.InteropServices;
using QuakeSharp.Core.Model;
using QuakeSharp.Core.Numerics;

namespace QuakeSharp.Core.Enhanced;

/// <summary>A corner of an alias model's triangle: its vertex and its skin coordinates (in texels, the seam's moved).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct EnhancedAliasCorner(int Vertex, float S, float T);

/// <summary>
/// An alias model (.mdl) made ready for the GPU (design.md §11.6, step 5): every pose's vertices (x, y, z and the
/// normal's index, a byte each, as the file packs them), the triangles' corners (r_alias.c moves a back-facing
/// triangle's seam vertices half the skin across), the skins as palette indices, and the frames' and skins' groups with
/// their intervals, chosen as R_AliasSetupFrame and R_AliasSetupSkin choose them. Never written after it is made.
/// </summary>
public sealed class EnhancedAliasModel
{
    public required string Name { get; init; }

    /// <summary>mdl_t: scale and scale_origin (a vertex is scale * v + scale_origin).</summary>
    public required float[] Scale { get; init; }

    public required float[] ScaleOrigin { get; init; }

    public required int VertexCount { get; init; }

    public required int SkinWidth { get; init; }

    public required int SkinHeight { get; init; }

    /// <summary>Every pose's vertices, <see cref="VertexCount"/> words each: x | y &lt;&lt; 8 | z &lt;&lt; 16 | normal &lt;&lt; 24.</summary>
    public required uint[] Poses { get; init; }

    public required EnhancedAliasCorner[] Corners { get; init; }

    /// <summary>The skins' palette indices, <see cref="SkinWidth"/> x <see cref="SkinHeight"/> each.</summary>
    public required byte[][] Skins { get; init; }

    /// <summary>For each frame, its poses (one, or a group's) and the group's intervals (empty for one).</summary>
    public required (int[] Poses, float[] Intervals)[] Frames { get; init; }

    /// <summary>For each skin number, its skins (one, or a group's) and the group's intervals.</summary>
    public required (int[] Skins, float[] Intervals)[] SkinGroups { get; init; }

    /// <summary>r_alias.c: R_AliasSetupFrame, a read-only copy: the pose shown for <paramref name="frame"/> at
    /// <paramref name="time"/> (cl.time + the entity's syncbase); a frame out of range is 0 (the C says so on the
    /// console in developer mode; this says nothing).</summary>
    public int Pose(int frame, double time) => Choose(Frames, frame, time);

    /// <summary>r_alias.c: R_AliasSetupSkin, a read-only copy: the skin shown for skin number <paramref name="skin"/>.</summary>
    public int Skin(int skin, double time) => Choose(SkinGroups, skin, time);

    public static EnhancedAliasModel Build(Model.Model model, AliasHdr header)
    {
        Mdl mdl = header.model;
        var poses = new List<uint>();
        var frames = new (int[], float[])[header.frames.Length];
        for (int f = 0; f < header.frames.Length; f++)
        {
            MAliasFrameDesc desc = header.frames[f];
            if (desc.type == aliasframetype_t.ALIAS_SINGLE)
            {
                frames[f] = ([AddPose(poses, (TriVertX[])desc.frame)], []);
            }
            else
            {
                var group = (MAliasGroup)desc.frame;
                frames[f] = ([.. group.frames.Take(group.numframes).Select(g => AddPose(poses, g.frame))], group.intervals[..group.numframes]);
            }
        }

        var skins = new List<byte[]>();
        var skinGroups = new (int[], float[])[header.skindesc.Length];
        int size = mdl.skinwidth * mdl.skinheight;
        for (int s = 0; s < header.skindesc.Length; s++)
        {
            MAliasSkinDesc desc = header.skindesc[s];
            if (desc.type == aliasskintype_t.ALIAS_SKIN_SINGLE)
            {
                skins.Add(((byte[])desc.skin)[..size]);
                skinGroups[s] = ([skins.Count - 1], []);
            }
            else
            {
                var group = (MAliasSkinGroup)desc.skin;
                var indices = new int[group.numskins];
                for (int i = 0; i < group.numskins; i++)
                {
                    skins.Add(((byte[])group.skindescs[i].skin)[..size]);
                    indices[i] = skins.Count - 1;
                }

                skinGroups[s] = (indices, group.intervals[..group.numskins]);
            }
        }

        // r_alias.c (R_AliasPreparePoints, D_PolysetDraw): a back-facing triangle's seam vertices take the skin's
        // right half
        var corners = new EnhancedAliasCorner[header.triangles.Length * 3];
        for (int t = 0; t < header.triangles.Length; t++)
        {
            MTriangle tri = header.triangles[t];
            for (int k = 0; k < 3; k++)
            {
                int v = tri.vertindex[k];
                // model.c keeps the skin coordinates as 16.16 (Mod_LoadAliasModel's << 16)
                StVert st = header.stverts[v];
                float s = st.s / 65536f;
                if (tri.facesfront == 0 && st.onseam != 0)
                {
                    s += mdl.skinwidth >> 1;
                }

                corners[(t * 3) + k] = new EnhancedAliasCorner(v, s, st.t / 65536f);
            }
        }

        return new EnhancedAliasModel
        {
            Name = model.name,
            Scale = [mdl.scale[0], mdl.scale[1], mdl.scale[2]],
            ScaleOrigin = [mdl.scale_origin[0], mdl.scale_origin[1], mdl.scale_origin[2]],
            VertexCount = mdl.numverts,
            SkinWidth = mdl.skinwidth,
            SkinHeight = mdl.skinheight,
            Poses = [.. poses],
            Corners = corners,
            Skins = [.. skins],
            Frames = frames,
            SkinGroups = skinGroups,
        };
    }

    private static int AddPose(List<uint> poses, TriVertX[] vertices)
    {
        int index = poses.Count;
        foreach (TriVertX v in vertices)
        {
            poses.Add(v.v[0] | ((uint)v.v[1] << 8) | ((uint)v.v[2] << 16) | ((uint)v.lightnormalindex << 24));
        }

        return index;
    }

    // r_alias.c's choice in a group: the first whose interval is past the time into the group's cycle
    private static int Choose((int[] Items, float[] Intervals)[] groups, int n, double time)
    {
        if (groups.Length == 0)
        {
            return 0;
        }

        if (n >= groups.Length || n < 0)
        {
            n = 0;
        }

        (int[] items, float[] intervals) = groups[n];
        if (intervals.Length == 0)
        {
            return items[0];
        }

        float full = intervals[^1];
        float t = (float)time;
        float target = t - (X86.FtoI(t / full) * full);
        int i = 0;
        while (i < items.Length - 1 && intervals[i] <= target)
        {
            i++;
        }

        return items[i];
    }
}
