using QuakeSharp.Core.Model;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Render;

namespace QuakeSharp.Core;

// r_aclip.c: clip routines for drawing Alias models directly to the screen
public sealed partial class QuakeEngine
{
    // r_aclip.c: fv, av (statics)
    private readonly FinalVert[][] r_aclip_fv = [new FinalVert[8], new FinalVert[8]];
    private readonly AuxVert[] r_aclip_av = new AuxVert[8];

    // r_aclip.c: R_AliasClipTriangle's mtri
    private readonly MTriangle[] r_aclip_mtri = new MTriangle[1];

    /// <summary>r_aclip.c: the clip function R_AliasClip takes: the vertices pfv0 and pfv1 (in[i0], in[i1]) and the
    /// output vertex</summary>
    private delegate void AliasClipFunc(FinalVert[] @in, int i0, int i1, ref FinalVert @out);

    // r_aclip.c: R_Alias_clip_z
    // pfv0 is the unclipped vertex, pfv1 is the z-clipped vertex
    private void R_Alias_clip_z(FinalVert[] @in, int i0, int i1, ref FinalVert @out)
    {
        float scale;
        AuxVert avout = default;
        ref FinalVert pfv0 = ref @in[i0];
        ref FinalVert pfv1 = ref @in[i1];

        // (pfv0 and pfv1 are in fv[0]: their auxiliary vertices are av at the same indices)
        ref AuxVert pav0 = ref r_aclip_av[i0];
        ref AuxVert pav1 = ref r_aclip_av[i1];

        if (pfv0.v[1] >= pfv1.v[1])
        {
            scale = (ALIAS_Z_CLIP_PLANE - pav0.fv[2]) /
                (pav1.fv[2] - pav0.fv[2]);

            avout.fv[0] = pav0.fv[0] + ((pav1.fv[0] - pav0.fv[0]) * scale);
            avout.fv[1] = pav0.fv[1] + ((pav1.fv[1] - pav0.fv[1]) * scale);
            avout.fv[2] = ALIAS_Z_CLIP_PLANE;

            @out.v[2] = X86.FtoI(pfv0.v[2] + ((pfv1.v[2] - pfv0.v[2]) * scale));
            @out.v[3] = X86.FtoI(pfv0.v[3] + ((pfv1.v[3] - pfv0.v[3]) * scale));
            @out.v[4] = X86.FtoI(pfv0.v[4] + ((pfv1.v[4] - pfv0.v[4]) * scale));
        }
        else
        {
            scale = (ALIAS_Z_CLIP_PLANE - pav1.fv[2]) /
                (pav0.fv[2] - pav1.fv[2]);

            avout.fv[0] = pav1.fv[0] + ((pav0.fv[0] - pav1.fv[0]) * scale);
            avout.fv[1] = pav1.fv[1] + ((pav0.fv[1] - pav1.fv[1]) * scale);
            avout.fv[2] = ALIAS_Z_CLIP_PLANE;

            @out.v[2] = X86.FtoI(pfv1.v[2] + ((pfv0.v[2] - pfv1.v[2]) * scale));
            @out.v[3] = X86.FtoI(pfv1.v[3] + ((pfv0.v[3] - pfv1.v[3]) * scale));
            @out.v[4] = X86.FtoI(pfv1.v[4] + ((pfv0.v[4] - pfv1.v[4]) * scale));
        }

        R_AliasProjectFinalVert(ref @out, avout);

        if (@out.v[0] < r_refdef.aliasvrect.x)
        {
            @out.flags |= ALIAS_LEFT_CLIP;
        }

        if (@out.v[1] < r_refdef.aliasvrect.y)
        {
            @out.flags |= ALIAS_TOP_CLIP;
        }

        if (@out.v[0] > r_refdef.aliasvrectright)
        {
            @out.flags |= ALIAS_RIGHT_CLIP;
        }

        if (@out.v[1] > r_refdef.aliasvrectbottom)
        {
            @out.flags |= ALIAS_BOTTOM_CLIP;
        }
    }

    // The body of r_aclip.c's R_Alias_clip_left, _right, _top and _bottom, which differ only in the coordinate
    // (0: u, 1: v) and the edge clipped to
    private static void R_Alias_clip_edge(FinalVert[] @in, int i0, int i1, ref FinalVert @out, int coord, int edge)
    {
        float scale;
        int i;
        ref FinalVert pfv0 = ref @in[i0];
        ref FinalVert pfv1 = ref @in[i1];

        if (pfv0.v[1] >= pfv1.v[1])
        {
            scale = (float)(edge - pfv0.v[coord]) /
                (pfv1.v[coord] - pfv0.v[coord]);
            for (i = 0; i < 6; i++)
            {
                @out.v[i] = X86.FtoI(pfv0.v[i] + ((pfv1.v[i] - pfv0.v[i]) * scale) + 0.5);
            }
        }
        else
        {
            scale = (float)(edge - pfv1.v[coord]) /
                (pfv0.v[coord] - pfv1.v[coord]);
            for (i = 0; i < 6; i++)
            {
                @out.v[i] = X86.FtoI(pfv1.v[i] + ((pfv0.v[i] - pfv1.v[i]) * scale) + 0.5);
            }
        }
    }

    // r_aclip.c: R_Alias_clip_left
    private void R_Alias_clip_left(FinalVert[] @in, int i0, int i1, ref FinalVert @out) =>
        R_Alias_clip_edge(@in, i0, i1, ref @out, 0, r_refdef.aliasvrect.x);

    // r_aclip.c: R_Alias_clip_right
    private void R_Alias_clip_right(FinalVert[] @in, int i0, int i1, ref FinalVert @out) =>
        R_Alias_clip_edge(@in, i0, i1, ref @out, 0, r_refdef.aliasvrectright);

    // r_aclip.c: R_Alias_clip_top
    private void R_Alias_clip_top(FinalVert[] @in, int i0, int i1, ref FinalVert @out) =>
        R_Alias_clip_edge(@in, i0, i1, ref @out, 1, r_refdef.aliasvrect.y);

    // r_aclip.c: R_Alias_clip_bottom
    private void R_Alias_clip_bottom(FinalVert[] @in, int i0, int i1, ref FinalVert @out) =>
        R_Alias_clip_edge(@in, i0, i1, ref @out, 1, r_refdef.aliasvrectbottom);

    // r_aclip.c: R_AliasClip
    private int R_AliasClip(FinalVert[] @in, FinalVert[] @out, int flag, int count, AliasClipFunc clip)
    {
        int i, j, k;
        int flags, oldflags;

        j = count - 1;
        k = 0;
        for (i = 0; i < count; j = i, i++)
        {
            oldflags = @in[j].flags & flag;
            flags = @in[i].flags & flag;

            if (flags != 0 && oldflags != 0)
            {
                continue;
            }

            if ((oldflags ^ flags) != 0)
            {
                clip(@in, j, i, ref @out[k]);
                @out[k].flags = 0;
                if (@out[k].v[0] < r_refdef.aliasvrect.x)
                {
                    @out[k].flags |= ALIAS_LEFT_CLIP;
                }

                if (@out[k].v[1] < r_refdef.aliasvrect.y)
                {
                    @out[k].flags |= ALIAS_TOP_CLIP;
                }

                if (@out[k].v[0] > r_refdef.aliasvrectright)
                {
                    @out[k].flags |= ALIAS_RIGHT_CLIP;
                }

                if (@out[k].v[1] > r_refdef.aliasvrectbottom)
                {
                    @out[k].flags |= ALIAS_BOTTOM_CLIP;
                }

                k++;
            }

            if (flags == 0)
            {
                @out[k] = @in[i];
                k++;
            }
        }

        return k;
    }

    // r_aclip.c: R_AliasClipTriangle
    public void R_AliasClipTriangle(in MTriangle ptri)
    {
        int i, k, pingpong;
        uint clipflags;
        FinalVert[][] fv = r_aclip_fv;

        // copy vertexes and fix seam texture coordinates
        if (ptri.facesfront != 0)
        {
            fv[0][0] = pfinalverts[ptri.vertindex[0]];
            fv[0][1] = pfinalverts[ptri.vertindex[1]];
            fv[0][2] = pfinalverts[ptri.vertindex[2]];
        }
        else
        {
            for (i = 0; i < 3; i++)
            {
                fv[0][i] = pfinalverts[ptri.vertindex[i]];

                if (ptri.facesfront == 0 && (fv[0][i].flags & ALIAS_ONSEAM) != 0)
                {
                    fv[0][i].v[2] += r_affinetridesc.seamfixupX16;
                }
            }
        }

        // clip
        clipflags = (uint)(fv[0][0].flags | fv[0][1].flags | fv[0][2].flags);

        if ((clipflags & ALIAS_Z_CLIP) != 0)
        {
            for (i = 0; i < 3; i++)
            {
                r_aclip_av[i] = pauxverts[ptri.vertindex[i]];
            }

            k = R_AliasClip(fv[0], fv[1], ALIAS_Z_CLIP, 3, R_Alias_clip_z);
            if (k == 0)
            {
                return;
            }

            pingpong = 1;
            clipflags = (uint)(fv[1][0].flags | fv[1][1].flags | fv[1][2].flags);
        }
        else
        {
            pingpong = 0;
            k = 3;
        }

        if ((clipflags & ALIAS_LEFT_CLIP) != 0)
        {
            k = R_AliasClip(fv[pingpong], fv[pingpong ^ 1],
                ALIAS_LEFT_CLIP, k, R_Alias_clip_left);
            if (k == 0)
            {
                return;
            }

            pingpong ^= 1;
        }

        if ((clipflags & ALIAS_RIGHT_CLIP) != 0)
        {
            k = R_AliasClip(fv[pingpong], fv[pingpong ^ 1],
                ALIAS_RIGHT_CLIP, k, R_Alias_clip_right);
            if (k == 0)
            {
                return;
            }

            pingpong ^= 1;
        }

        if ((clipflags & ALIAS_BOTTOM_CLIP) != 0)
        {
            k = R_AliasClip(fv[pingpong], fv[pingpong ^ 1],
                ALIAS_BOTTOM_CLIP, k, R_Alias_clip_bottom);
            if (k == 0)
            {
                return;
            }

            pingpong ^= 1;
        }

        if ((clipflags & ALIAS_TOP_CLIP) != 0)
        {
            k = R_AliasClip(fv[pingpong], fv[pingpong ^ 1],
                ALIAS_TOP_CLIP, k, R_Alias_clip_top);
            if (k == 0)
            {
                return;
            }

            pingpong ^= 1;
        }

        for (i = 0; i < k; i++)
        {
            if (fv[pingpong][i].v[0] < r_refdef.aliasvrect.x)
            {
                fv[pingpong][i].v[0] = r_refdef.aliasvrect.x;
            }
            else if (fv[pingpong][i].v[0] > r_refdef.aliasvrectright)
            {
                fv[pingpong][i].v[0] = r_refdef.aliasvrectright;
            }

            if (fv[pingpong][i].v[1] < r_refdef.aliasvrect.y)
            {
                fv[pingpong][i].v[1] = r_refdef.aliasvrect.y;
            }
            else if (fv[pingpong][i].v[1] > r_refdef.aliasvrectbottom)
            {
                fv[pingpong][i].v[1] = r_refdef.aliasvrectbottom;
            }

            fv[pingpong][i].flags = 0;
        }

        // draw triangles
        r_aclip_mtri[0].facesfront = ptri.facesfront;
        r_affinetridesc.ptriangles = r_aclip_mtri;
        r_affinetridesc.ptrianglesstart = 0;
        r_affinetridesc.pfinalverts = fv[pingpong];

        // FIXME: do all at once as trifan?
        r_aclip_mtri[0].vertindex[0] = 0;
        for (i = 1; i < k - 1; i++)
        {
            r_aclip_mtri[0].vertindex[1] = i;
            r_aclip_mtri[0].vertindex[2] = i + 1;
            D_PolysetDraw();
        }
    }
}
