using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Render;
using static QuakeSharp.Core.Common.MathLib;
using static QuakeSharp.Core.Model.SpriteGen;

namespace QuakeSharp.Core;

// r_sprite.c: a sprite's poster in worldspace, clipped to the frustum and projected; d_sprite.c draws it
public sealed partial class QuakeEngine
{
    // r_shared.h: MAXVERTS, MAXWORKINGVERTS
    private const int MAXVERTS = 16; // max points in a surface polygon
    private const int MAXWORKINGVERTS = MAXVERTS + 4; // max points in an intermediate polygon (while processing)

    private int clip_current;
    private readonly Vec5[][] clip_verts = [new Vec5[MAXWORKINGVERTS], new Vec5[MAXWORKINGVERTS]];
    private int sprite_width, sprite_height;

    public readonly SpriteDesc r_spritedesc = new();

    // r_sprite.c: R_SetupAndDrawSprite's outverts (on the C's stack)
    private readonly EmitPoint[] r_sprite_outverts = new EmitPoint[MAXWORKINGVERTS + 1];

    // r_sprite.c: R_RotateSprite
    public void R_RotateSprite(float beamlength)
    {
        Vec3 vec = default;

        if (beamlength == 0.0)
        {
            return;
        }

        VectorScale(r_spritedesc.vpn, -beamlength, ref vec);
        VectorAdd(r_entorigin, vec, ref r_entorigin);
        VectorSubtract(modelorg, vec, ref modelorg);
    }

    // r_sprite.c: R_ClipSpriteFace
    // Clips the winding at clip_verts[clip_current] and changes clip_current. Throws out the back side
    public int R_ClipSpriteFace(int nump, ClipPlane pclipplane)
    {
        int i, outcount;
        Span<float> dists = stackalloc float[MAXWORKINGVERTS + 1];
        float frac, clipdist;
        Vec5[] @in, outstep;
        int o;

        clipdist = pclipplane.dist;
        ref Vec3 pclipnormal = ref pclipplane.normal;

        // calc dists
        if (clip_current != 0)
        {
            @in = clip_verts[1];
            outstep = clip_verts[0];
            clip_current = 0;
        }
        else
        {
            @in = clip_verts[0];
            outstep = clip_verts[1];
            clip_current = 1;
        }

        for (i = 0; i < nump; i++)
        {
            dists[i] = (@in[i][0] * pclipnormal[0]) + (@in[i][1] * pclipnormal[1]) + (@in[i][2] * pclipnormal[2]) - clipdist;
        }

        // handle wraparound case
        dists[nump] = dists[0];
        @in[nump] = @in[0];

        // clip the winding
        outcount = 0;
        o = 0;

        for (i = 0; i < nump; i++)
        {
            if (dists[i] >= 0)
            {
                outstep[o] = @in[i];
                o++;
                outcount++;
            }

            if (dists[i] == 0 || dists[i + 1] == 0)
            {
                continue;
            }

            if ((dists[i] > 0) == (dists[i + 1] > 0))
            {
                continue;
            }

            // split it into a new vertex
            frac = dists[i] / (dists[i] - dists[i + 1]);

            ref Vec5 instep = ref @in[i];
            ref Vec5 vert2 = ref @in[i + 1];

            outstep[o][0] = instep[0] + (frac * (vert2[0] - instep[0]));
            outstep[o][1] = instep[1] + (frac * (vert2[1] - instep[1]));
            outstep[o][2] = instep[2] + (frac * (vert2[2] - instep[2]));
            outstep[o][3] = instep[3] + (frac * (vert2[3] - instep[3]));
            outstep[o][4] = instep[4] + (frac * (vert2[4] - instep[4]));

            o++;
            outcount++;
        }

        return outcount;
    }

    // r_sprite.c: R_SetupAndDrawSprite
    public void R_SetupAndDrawSprite()
    {
        int i, nump;
        float dot, scale;
        Vec5[] pverts;
        Vec3 left = default, up = default, right = default, down = default, transformed = default, local = default;
        EmitPoint[] outverts = r_sprite_outverts;

        dot = DotProduct(r_spritedesc.vpn, modelorg);

        // backface cull
        if (dot >= 0)
        {
            return;
        }

        // build the sprite poster in worldspace
        VectorScale(r_spritedesc.vright, r_spritedesc.pspriteframe.right, ref right);
        VectorScale(r_spritedesc.vup, r_spritedesc.pspriteframe.up, ref up);
        VectorScale(r_spritedesc.vright, r_spritedesc.pspriteframe.left, ref left);
        VectorScale(r_spritedesc.vup, r_spritedesc.pspriteframe.down, ref down);

        pverts = clip_verts[0];

        pverts[0][0] = r_entorigin[0] + up[0] + left[0];
        pverts[0][1] = r_entorigin[1] + up[1] + left[1];
        pverts[0][2] = r_entorigin[2] + up[2] + left[2];
        pverts[0][3] = 0;
        pverts[0][4] = 0;

        pverts[1][0] = r_entorigin[0] + up[0] + right[0];
        pverts[1][1] = r_entorigin[1] + up[1] + right[1];
        pverts[1][2] = r_entorigin[2] + up[2] + right[2];
        pverts[1][3] = sprite_width;
        pverts[1][4] = 0;

        pverts[2][0] = r_entorigin[0] + down[0] + right[0];
        pverts[2][1] = r_entorigin[1] + down[1] + right[1];
        pverts[2][2] = r_entorigin[2] + down[2] + right[2];
        pverts[2][3] = sprite_width;
        pverts[2][4] = sprite_height;

        pverts[3][0] = r_entorigin[0] + down[0] + left[0];
        pverts[3][1] = r_entorigin[1] + down[1] + left[1];
        pverts[3][2] = r_entorigin[2] + down[2] + left[2];
        pverts[3][3] = 0;
        pverts[3][4] = sprite_height;

        // clip to the frustum in worldspace
        nump = 4;
        clip_current = 0;

        for (i = 0; i < 4; i++)
        {
            nump = R_ClipSpriteFace(nump, view_clipplanes[i]);
            if (nump < 3)
            {
                return;
            }

            if (nump >= MAXWORKINGVERTS)
            {
                throw Sys_Error("R_SetupAndDrawSprite: too many points");
            }
        }

        // transform vertices into viewspace and project
        Vec5[] pv = clip_verts[clip_current];
        r_spritedesc.nearzi = -999999;

        for (i = 0; i < nump; i++)
        {
            Vec3 point = new(pv[i][0], pv[i][1], pv[i][2]);
            VectorSubtract(point, r_origin, ref local);
            TransformVector(local, ref transformed);

            if (transformed[2] < NEAR_CLIP)
            {
                transformed[2] = (float)NEAR_CLIP;
            }

            ref EmitPoint pout = ref outverts[i];
            pout.zi = (float)(1.0 / transformed[2]);
            if (pout.zi > r_spritedesc.nearzi)
            {
                r_spritedesc.nearzi = pout.zi;
            }

            pout.s = pv[i][3];
            pout.t = pv[i][4];

            scale = xscale * pout.zi;
            pout.u = xcenter + (scale * transformed[0]);

            scale = yscale * pout.zi;
            pout.v = ycenter - (scale * transformed[1]);
        }

        // draw it
        r_spritedesc.nump = nump;
        r_spritedesc.pverts = outverts;
        D_DrawSprite();
    }

    // r_sprite.c: R_GetSpriteframe
    public MSpriteFrame R_GetSpriteframe(MSprite psprite)
    {
        MSpriteGroup pspritegroup;
        MSpriteFrame pspriteframe;
        int i, numframes, frame;
        float[] pintervals;
        float fullinterval, targettime, time;

        frame = currententity.frame;

        if ((frame >= psprite.numframes) || (frame < 0))
        {
            Con_Printf("R_DrawSprite: no such frame %d\n", frame);
            frame = 0;
        }

        if (psprite.frames[frame].type == spriteframetype_t.SPR_SINGLE)
        {
            pspriteframe = (MSpriteFrame)psprite.frames[frame].frameptr;
        }
        else
        {
            pspritegroup = (MSpriteGroup)psprite.frames[frame].frameptr;
            pintervals = pspritegroup.intervals;
            numframes = pspritegroup.numframes;
            fullinterval = pintervals[numframes - 1];

            time = (float)(cl.time + currententity.syncbase);

            // when loading in Mod_LoadSpriteGroup, we guaranteed all interval values are positive, so we don't have
            // to worry about division by 0
            targettime = time - (X86.FtoI(time / fullinterval) * fullinterval);

            for (i = 0; i < (numframes - 1); i++)
            {
                if (pintervals[i] > targettime)
                {
                    break;
                }
            }

            pspriteframe = pspritegroup.frames[i];
        }

        return pspriteframe;
    }

    // r_sprite.c: R_DrawSprite
    public void R_DrawSprite()
    {
        int i;
        MSprite psprite;
        Vec3 tvec = default;
        float dot, angle, sr, cr;

        psprite = (MSprite)currententity.model!.cache!;

        r_spritedesc.pspriteframe = R_GetSpriteframe(psprite);

        sprite_width = r_spritedesc.pspriteframe.width;
        sprite_height = r_spritedesc.pspriteframe.height;

        // TODO: make this caller-selectable
        if (psprite.type == SPR_FACING_UPRIGHT)
        {
            // generate the sprite's axes, with vup straight up in worldspace, and r_spritedesc.vright perpendicular
            // to modelorg. This will not work if the view direction is very close to straight up or down, because
            // the cross product will be between two nearly parallel vectors and starts to approach an undefined
            // state, so we don't draw if the two vectors are less than 1 degree apart
            tvec[0] = -modelorg[0];
            tvec[1] = -modelorg[1];
            tvec[2] = -modelorg[2];
            VectorNormalize(ref tvec);
            dot = tvec[2]; // same as DotProduct (tvec, r_spritedesc.vup) because r_spritedesc.vup is 0, 0, 1
            if ((dot > 0.999848) || (dot < -0.999848))
            {
                // cos(1 degree) = 0.999848
                return;
            }

            r_spritedesc.vup[0] = 0;
            r_spritedesc.vup[1] = 0;
            r_spritedesc.vup[2] = 1;
            r_spritedesc.vright[0] = tvec[1];

            // CrossProduct(r_spritedesc.vup, -modelorg, r_spritedesc.vright)
            r_spritedesc.vright[1] = -tvec[0];
            r_spritedesc.vright[2] = 0;
            VectorNormalize(ref r_spritedesc.vright);
            r_spritedesc.vpn[0] = -r_spritedesc.vright[1];
            r_spritedesc.vpn[1] = r_spritedesc.vright[0];
            r_spritedesc.vpn[2] = 0;

            // CrossProduct (r_spritedesc.vright, r_spritedesc.vup, r_spritedesc.vpn)
        }
        else if (psprite.type == SPR_VP_PARALLEL)
        {
            // generate the sprite's axes, completely parallel to the viewplane. There are no problem situations,
            // because the sprite is always in the same position relative to the viewer
            for (i = 0; i < 3; i++)
            {
                r_spritedesc.vup[i] = vup[i];
                r_spritedesc.vright[i] = vright[i];
                r_spritedesc.vpn[i] = vpn[i];
            }
        }
        else if (psprite.type == SPR_VP_PARALLEL_UPRIGHT)
        {
            // generate the sprite's axes, with vup straight up in worldspace, and r_spritedesc.vright parallel to the
            // viewplane. This will not work if the view direction is very close to straight up or down, because the
            // cross product will be between two nearly parallel vectors and starts to approach an undefined state,
            // so we don't draw if the two vectors are less than 1 degree apart
            dot = vpn[2]; // same as DotProduct (vpn, r_spritedesc.vup) because r_spritedesc.vup is 0, 0, 1
            if ((dot > 0.999848) || (dot < -0.999848))
            {
                // cos(1 degree) = 0.999848
                return;
            }

            r_spritedesc.vup[0] = 0;
            r_spritedesc.vup[1] = 0;
            r_spritedesc.vup[2] = 1;
            r_spritedesc.vright[0] = vpn[1];

            // CrossProduct (r_spritedesc.vup, vpn, r_spritedesc.vright)
            r_spritedesc.vright[1] = -vpn[0];
            r_spritedesc.vright[2] = 0;
            VectorNormalize(ref r_spritedesc.vright);
            r_spritedesc.vpn[0] = -r_spritedesc.vright[1];
            r_spritedesc.vpn[1] = r_spritedesc.vright[0];
            r_spritedesc.vpn[2] = 0;

            // CrossProduct (r_spritedesc.vright, r_spritedesc.vup, r_spritedesc.vpn)
        }
        else if (psprite.type == SPR_ORIENTED)
        {
            // generate the sprite's axes, according to the sprite's world orientation
            AngleVectors(currententity.angles, ref r_spritedesc.vpn,
                ref r_spritedesc.vright, ref r_spritedesc.vup);
        }
        else if (psprite.type == SPR_VP_PARALLEL_ORIENTED)
        {
            // generate the sprite's axes, parallel to the viewplane, but rotated in that plane around the center
            // according to the sprite entity's roll angle. So vpn stays the same, but vright and vup rotate
            angle = (float)(currententity.angles[QuakeDef.ROLL] * (M_PI * 2 / 360));
            sr = (float)Libm.sin(angle);
            cr = (float)Libm.cos(angle);

            for (i = 0; i < 3; i++)
            {
                r_spritedesc.vpn[i] = vpn[i];
                r_spritedesc.vright[i] = (vright[i] * cr) + (vup[i] * sr);
                r_spritedesc.vup[i] = (vright[i] * -sr) + (vup[i] * cr);
            }
        }
        else
        {
            throw Sys_Error("R_DrawSprite: Bad sprite type %d", psprite.type);
        }

        R_RotateSprite(psprite.beamlength);

        R_SetupAndDrawSprite();
    }

    // d_local.h: DS_SPAN_LIST_END
    private const int DS_SPAN_LIST_END = -128;

    // d_sprite.c: sprite_height (static), minindex, maxindex, sprite_spans (D_DrawSprite's spans, on the C's stack)
    private int d_sprite_height;
    private int minindex, maxindex;
    private readonly SSpan[] sprite_spans = new SSpan[MAXHEIGHT + 1];

    // d_sprite.c: D_SpriteDrawSpans
    public void D_SpriteDrawSpans(SSpan[] spans)
    {
        int count, spancount, izistep;
        int izi;
        int pbase, pdest;
        int s, t, snext, tnext, sstep, tstep;
        float sdivz, tdivz, zi, z, du, dv, spancountminus1;
        float sdivz8stepu, tdivz8stepu, zi8stepu;
        byte btemp;
        int pz;
        int pspan = 0;
        byte[] texture = cacheblock;
        byte[] dest = d_viewbuffer;
        short[] zbuffer = d_pzbuffer!;

        sstep = 0; // keep compiler happy
        tstep = 0; // ditto

        pbase = cacheblockoffset;

        sdivz8stepu = d_sdivzstepu * 8;
        tdivz8stepu = d_tdivzstepu * 8;
        zi8stepu = d_zistepu * 8;

        // we count on FP exceptions being turned off to avoid range problems
        izistep = X86.FtoI(d_zistepu * 0x8000 * 0x10000);

        do
        {
            pdest = d_viewbufferoffset + (screenwidth * spans[pspan].v) + spans[pspan].u;
            pz = (int)(d_zwidth * spans[pspan].v) + spans[pspan].u;

            count = spans[pspan].count;

            if (count <= 0)
            {
                goto NextSpan;
            }

            // calculate the initial s/z, t/z, 1/z, s, and t and clamp
            du = spans[pspan].u;
            dv = spans[pspan].v;

            sdivz = d_sdivzorigin + (dv * d_sdivzstepv) + (du * d_sdivzstepu);
            tdivz = d_tdivzorigin + (dv * d_tdivzstepv) + (du * d_tdivzstepu);
            zi = d_ziorigin + (dv * d_zistepv) + (du * d_zistepu);
            z = (float)0x10000 / zi; // prescale to 16.16 fixed-point

            // we count on FP exceptions being turned off to avoid range problems
            izi = X86.FtoI(zi * 0x8000 * 0x10000);

            s = X86.FtoI(sdivz * z) + sadjust;
            if (s > bbextents)
            {
                s = bbextents;
            }
            else if (s < 0)
            {
                s = 0;
            }

            t = X86.FtoI(tdivz * z) + tadjust;
            if (t > bbextentt)
            {
                t = bbextentt;
            }
            else if (t < 0)
            {
                t = 0;
            }

            do
            {
                // calculate s and t at the far end of the span
                if (count >= 8)
                {
                    spancount = 8;
                }
                else
                {
                    spancount = count;
                }

                count -= spancount;

                if (count != 0)
                {
                    // calculate s/z, t/z, zi->fixed s and t at far end of span, calculate s and t steps across span
                    // by shifting
                    sdivz += sdivz8stepu;
                    tdivz += tdivz8stepu;
                    zi += zi8stepu;
                    z = (float)0x10000 / zi; // prescale to 16.16 fixed-point

                    snext = X86.FtoI(sdivz * z) + sadjust;
                    if (snext > bbextents)
                    {
                        snext = bbextents;
                    }
                    else if (snext < 8)
                    {
                        snext = 8; // prevent round-off error on <0 steps from causing overstepping & running off
                                   // the edge of the texture
                    }

                    tnext = X86.FtoI(tdivz * z) + tadjust;
                    if (tnext > bbextentt)
                    {
                        tnext = bbextentt;
                    }
                    else if (tnext < 8)
                    {
                        tnext = 8; // guard against round-off error on <0 steps
                    }

                    sstep = (snext - s) >> 3;
                    tstep = (tnext - t) >> 3;
                }
                else
                {
                    // calculate s/z, t/z, zi->fixed s and t at last pixel in span (so can't step off polygon), clamp,
                    // calculate s and t steps across span by division, biasing steps low so we don't run off the
                    // texture
                    spancountminus1 = spancount - 1;
                    sdivz += d_sdivzstepu * spancountminus1;
                    tdivz += d_tdivzstepu * spancountminus1;
                    zi += d_zistepu * spancountminus1;
                    z = (float)0x10000 / zi; // prescale to 16.16 fixed-point
                    snext = X86.FtoI(sdivz * z) + sadjust;
                    if (snext > bbextents)
                    {
                        snext = bbextents;
                    }
                    else if (snext < 8)
                    {
                        snext = 8; // prevent round-off error on <0 steps from causing overstepping & running off
                                   // the edge of the texture
                    }

                    tnext = X86.FtoI(tdivz * z) + tadjust;
                    if (tnext > bbextentt)
                    {
                        tnext = bbextentt;
                    }
                    else if (tnext < 8)
                    {
                        tnext = 8; // guard against round-off error on <0 steps
                    }

                    if (spancount > 1)
                    {
                        sstep = (snext - s) / (spancount - 1);
                        tstep = (tnext - t) / (spancount - 1);
                    }
                }

                do
                {
                    btemp = texture[pbase + (s >> 16) + ((t >> 16) * cachewidth)];
                    if (btemp != 255)
                    {
                        // a left edge clipped to u -1 on the frame's first row: the C writes the pixel and its depth
                        // before the frame and the z-buffer (design.md §3.3), where nothing is drawn
                        if (pz >= 0 && zbuffer[pz] <= (izi >> 16))
                        {
                            zbuffer[pz] = (short)(izi >> 16);
                            dest[pdest] = btemp;
                        }
                    }

                    izi += izistep;
                    pdest++;
                    pz++;
                    s += sstep;
                    t += tstep;
                }
                while (--spancount > 0);

                s = snext;
                t = tnext;
            }
            while (count > 0);

        NextSpan:
            pspan++;
        }
        while (spans[pspan].count != DS_SPAN_LIST_END);
    }

    // d_sprite.c: D_SpriteScanLeftEdge
    public void D_SpriteScanLeftEdge()
    {
        int i, v, itop, ibottom, lmaxindex;
        int pspan;
        float du, dv, vtop, vbottom, slope;
        int u, u_step;
        EmitPoint[] pverts = r_spritedesc.pverts;

        pspan = 0;
        i = minindex;
        if (i == 0)
        {
            i = r_spritedesc.nump;
        }

        lmaxindex = maxindex;
        if (lmaxindex == 0)
        {
            lmaxindex = r_spritedesc.nump;
        }

        vtop = (float)Math.Ceiling(pverts[i].v);

        do
        {
            ref EmitPoint pvert = ref pverts[i];
            ref EmitPoint pnext = ref pverts[i - 1];

            vbottom = (float)Math.Ceiling(pnext.v);

            if (vtop < vbottom)
            {
                du = pnext.u - pvert.u;
                dv = pnext.v - pvert.v;
                slope = du / dv;
                u_step = X86.FtoI(slope * 0x10000);

                // adjust u to ceil the integer portion
                u = X86.FtoI((pvert.u + (slope * (vtop - pvert.v))) * 0x10000) +
                    (0x10000 - 1);
                itop = X86.FtoI(vtop);
                ibottom = X86.FtoI(vbottom);

                for (v = itop; v < ibottom; v++)
                {
                    sprite_spans[pspan].u = u >> 16;
                    sprite_spans[pspan].v = v;
                    u += u_step;
                    pspan++;
                }
            }

            vtop = vbottom;

            i--;
            if (i == 0)
            {
                i = r_spritedesc.nump;
            }
        }
        while (i != lmaxindex);
    }

    // d_sprite.c: D_SpriteScanRightEdge
    public void D_SpriteScanRightEdge()
    {
        int i, v, itop, ibottom;
        int pspan;
        float du, dv, vtop, vbottom, slope, uvert, unext, vvert, vnext;
        int u, u_step;
        EmitPoint[] pverts = r_spritedesc.pverts;

        pspan = 0;
        i = minindex;

        vvert = pverts[i].v;
        if (vvert < r_refdef.fvrecty_adj)
        {
            vvert = r_refdef.fvrecty_adj;
        }

        if (vvert > r_refdef.fvrectbottom_adj)
        {
            vvert = r_refdef.fvrectbottom_adj;
        }

        vtop = (float)Math.Ceiling(vvert);

        do
        {
            ref EmitPoint pvert = ref pverts[i];
            ref EmitPoint pnext = ref pverts[i + 1];

            vnext = pnext.v;
            if (vnext < r_refdef.fvrecty_adj)
            {
                vnext = r_refdef.fvrecty_adj;
            }

            if (vnext > r_refdef.fvrectbottom_adj)
            {
                vnext = r_refdef.fvrectbottom_adj;
            }

            vbottom = (float)Math.Ceiling(vnext);

            if (vtop < vbottom)
            {
                uvert = pvert.u;
                if (uvert < r_refdef.fvrectx_adj)
                {
                    uvert = r_refdef.fvrectx_adj;
                }

                if (uvert > r_refdef.fvrectright_adj)
                {
                    uvert = r_refdef.fvrectright_adj;
                }

                unext = pnext.u;
                if (unext < r_refdef.fvrectx_adj)
                {
                    unext = r_refdef.fvrectx_adj;
                }

                if (unext > r_refdef.fvrectright_adj)
                {
                    unext = r_refdef.fvrectright_adj;
                }

                du = unext - uvert;
                dv = vnext - vvert;
                slope = du / dv;
                u_step = X86.FtoI(slope * 0x10000);

                // adjust u to ceil the integer portion
                u = X86.FtoI((uvert + (slope * (vtop - vvert))) * 0x10000) +
                    (0x10000 - 1);
                itop = X86.FtoI(vtop);
                ibottom = X86.FtoI(vbottom);

                for (v = itop; v < ibottom; v++)
                {
                    sprite_spans[pspan].count = (u >> 16) - sprite_spans[pspan].u;
                    u += u_step;
                    pspan++;
                }
            }

            vtop = vbottom;
            vvert = vnext;

            i++;
            if (i == r_spritedesc.nump)
            {
                i = 0;
            }
        }
        while (i != maxindex);

        sprite_spans[pspan].count = DS_SPAN_LIST_END; // mark the end of the span list
    }

    // d_sprite.c: D_SpriteCalculateGradients
    public void D_SpriteCalculateGradients()
    {
        Vec3 p_normal = default, p_saxis = default, p_taxis = default, p_temp1 = default;
        float distinv;

        TransformVector(r_spritedesc.vpn, ref p_normal);
        TransformVector(r_spritedesc.vright, ref p_saxis);
        TransformVector(r_spritedesc.vup, ref p_taxis);
        VectorInverse(ref p_taxis);

        distinv = (float)(1.0 / -DotProduct(modelorg, r_spritedesc.vpn));

        d_sdivzstepu = p_saxis[0] * xscaleinv;
        d_tdivzstepu = p_taxis[0] * xscaleinv;

        d_sdivzstepv = -p_saxis[1] * yscaleinv;
        d_tdivzstepv = -p_taxis[1] * yscaleinv;

        d_zistepu = p_normal[0] * xscaleinv * distinv;
        d_zistepv = -p_normal[1] * yscaleinv * distinv;

        d_sdivzorigin = p_saxis[2] - (xcenter * d_sdivzstepu) -
            (ycenter * d_sdivzstepv);
        d_tdivzorigin = p_taxis[2] - (xcenter * d_tdivzstepu) -
            (ycenter * d_tdivzstepv);
        d_ziorigin = (p_normal[2] * distinv) - (xcenter * d_zistepu) -
            (ycenter * d_zistepv);

        TransformVector(modelorg, ref p_temp1);

        sadjust = X86.FtoI((DotProduct(p_temp1, p_saxis) * 0x10000) + 0.5) -
            (-(cachewidth >> 1) << 16);
        tadjust = X86.FtoI((DotProduct(p_temp1, p_taxis) * 0x10000) + 0.5) -
            (-(d_sprite_height >> 1) << 16);

        // -1 (-epsilon) so we never wander off the edge of the texture
        bbextents = (cachewidth << 16) - 1;
        bbextentt = (d_sprite_height << 16) - 1;
    }

    // d_sprite.c: D_DrawSprite (its spans, on the C's stack, are sprite_spans)
    public void D_DrawSprite()
    {
        int i, nump;
        float ymin, ymax;
        EmitPoint[] pverts;

        // find the top and bottom vertices, and make sure there's at least one scan to draw
        ymin = 999999.9f;
        ymax = -999999.9f;
        pverts = r_spritedesc.pverts;

        for (i = 0; i < r_spritedesc.nump; i++)
        {
            if (pverts[i].v < ymin)
            {
                ymin = pverts[i].v;
                minindex = i;
            }

            if (pverts[i].v > ymax)
            {
                ymax = pverts[i].v;
                maxindex = i;
            }
        }

        ymin = (float)Math.Ceiling(ymin);
        ymax = (float)Math.Ceiling(ymax);

        if (ymin >= ymax)
        {
            return; // doesn't cross any scans at all
        }

        cachewidth = r_spritedesc.pspriteframe.width;
        d_sprite_height = r_spritedesc.pspriteframe.height;
        cacheblock = r_spritedesc.pspriteframe.pixels;
        cacheblockoffset = 0;

        // copy the first vertex to the last vertex, so we don't have to deal with wrapping
        nump = r_spritedesc.nump;
        pverts = r_spritedesc.pverts;
        pverts[nump] = pverts[0];

        D_SpriteCalculateGradients();
        D_SpriteScanLeftEdge();
        D_SpriteScanRightEdge();
        D_SpriteDrawSpans(sprite_spans);
    }
}
