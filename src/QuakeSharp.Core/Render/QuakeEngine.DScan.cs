using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Render;

namespace QuakeSharp.Core;

// d_scan.c: portable C scan-level rasterization code. The view buffer, the z-buffer and the texture (cacheblock) are
// arrays; C's pointers into them are offsets.
public sealed partial class QuakeEngine
{
    // d_scan.c: r_turb_pbase, r_turb_pdest (offsets in cacheblock and the view buffer), r_turb_turb (an offset in
    // sintable)
    public int r_turb_pbase, r_turb_pdest;
    public int r_turb_s, r_turb_t, r_turb_sstep, r_turb_tstep;
    public int r_turb_turb;
    public int r_turb_spancount;

    // d_scan.c: D_WarpScreen's rowptr and column (on the C's stack)
    private readonly int[] d_warp_rowptr = new int[MAXHEIGHT + (AMP2 * 2)];
    private readonly int[] d_warp_column = new int[MAXWIDTH + (AMP2 * 2)];

    // d_scan.c: D_WarpScreen
    // this performs a slight compression of the screen at the same time as the sine warp, to keep the edges from
    // wrapping
    public void D_WarpScreen()
    {
        int w, h;
        int u, v;
        int dest;
        int turb;
        int col;
        int row;
        int[] rowptr = d_warp_rowptr;
        int[] column = d_warp_column;
        float wratio, hratio;

        w = r_refdef.vrect.width;
        h = r_refdef.vrect.height;

        wratio = w / (float)scr_vrect.width;
        hratio = h / (float)scr_vrect.height;

        for (v = 0; v < scr_vrect.height + (AMP2 * 2); v++)
        {
            rowptr[v] = d_viewbufferoffset + (r_refdef.vrect.y * screenwidth) +
                (screenwidth * X86.FtoI((float)v * hratio * h / (h + (AMP2 * 2))));
        }

        for (u = 0; u < scr_vrect.width + (AMP2 * 2); u++)
        {
            column[u] = r_refdef.vrect.x +
                X86.FtoI((float)u * wratio * w / (w + (AMP2 * 2)));
        }

        turb = X86.FtoI(cl.time * SPEED) & (CYCLE - 1);
        dest = vid.bufferoffset + (scr_vrect.y * (int)vid.rowbytes) + scr_vrect.x;

        byte[] source = d_viewbuffer;
        byte[] buffer = vid.buffer;
        for (v = 0; v < scr_vrect.height; v++, dest += (int)vid.rowbytes)
        {
            col = intsintable[turb + v];
            row = v;

            for (u = 0; u < scr_vrect.width; u += 4)
            {
                buffer[dest + u + 0] = source[rowptr[row + intsintable[turb + u + 0]] + column[col + u + 0]];
                buffer[dest + u + 1] = source[rowptr[row + intsintable[turb + u + 1]] + column[col + u + 1]];
                buffer[dest + u + 2] = source[rowptr[row + intsintable[turb + u + 2]] + column[col + u + 2]];
                buffer[dest + u + 3] = source[rowptr[row + intsintable[turb + u + 3]] + column[col + u + 3]];
            }
        }
    }

    // d_scan.c: D_DrawTurbulent8Span
    public void D_DrawTurbulent8Span()
    {
        int sturb, tturb;
        byte[] dest = d_viewbuffer;
        byte[] pbase = cacheblock;

        do
        {
            sturb = ((r_turb_s + sintable[r_turb_turb + ((r_turb_t >> 16) & (CYCLE - 1))]) >> 16) & 63;
            tturb = ((r_turb_t + sintable[r_turb_turb + ((r_turb_s >> 16) & (CYCLE - 1))]) >> 16) & 63;
            dest[r_turb_pdest++] = pbase[r_turb_pbase + (tturb << 6) + sturb];
            r_turb_s += r_turb_sstep;
            r_turb_t += r_turb_tstep;
        }
        while (--r_turb_spancount > 0);
    }

    // d_scan.c: Turbulent8
    public void Turbulent8(Espan? pspan)
    {
        int count;
        int snext, tnext;
        float sdivz, tdivz, zi, z, du, dv, spancountminus1;
        float sdivz16stepu, tdivz16stepu, zi16stepu;

        r_turb_turb = X86.FtoI(cl.time * SPEED) & (CYCLE - 1);

        r_turb_sstep = 0; // keep compiler happy
        r_turb_tstep = 0; // ditto

        r_turb_pbase = cacheblockoffset;

        sdivz16stepu = d_sdivzstepu * 16;
        tdivz16stepu = d_tdivzstepu * 16;
        zi16stepu = d_zistepu * 16;

        do
        {
            r_turb_pdest = d_viewbufferoffset +
                (screenwidth * pspan!.v) + pspan.u;

            count = pspan.count;

            // calculate the initial s/z, t/z, 1/z, s, and t and clamp
            du = pspan.u;
            dv = pspan.v;

            sdivz = d_sdivzorigin + (dv * d_sdivzstepv) + (du * d_sdivzstepu);
            tdivz = d_tdivzorigin + (dv * d_tdivzstepv) + (du * d_tdivzstepu);
            zi = d_ziorigin + (dv * d_zistepv) + (du * d_zistepu);
            z = (float)0x10000 / zi; // prescale to 16.16 fixed-point

            r_turb_s = X86.FtoI(sdivz * z) + sadjust;
            if (r_turb_s > bbextents)
            {
                r_turb_s = bbextents;
            }
            else if (r_turb_s < 0)
            {
                r_turb_s = 0;
            }

            r_turb_t = X86.FtoI(tdivz * z) + tadjust;
            if (r_turb_t > bbextentt)
            {
                r_turb_t = bbextentt;
            }
            else if (r_turb_t < 0)
            {
                r_turb_t = 0;
            }

            do
            {
                // calculate s and t at the far end of the span
                if (count >= 16)
                {
                    r_turb_spancount = 16;
                }
                else
                {
                    r_turb_spancount = count;
                }

                count -= r_turb_spancount;

                if (count != 0)
                {
                    // calculate s/z, t/z, zi->fixed s and t at far end of span, calculate s and t steps across span
                    // by shifting
                    sdivz += sdivz16stepu;
                    tdivz += tdivz16stepu;
                    zi += zi16stepu;
                    z = (float)0x10000 / zi; // prescale to 16.16 fixed-point

                    snext = X86.FtoI(sdivz * z) + sadjust;
                    if (snext > bbextents)
                    {
                        snext = bbextents;
                    }
                    else if (snext < 16)
                    {
                        snext = 16; // prevent round-off error on <0 steps from causing overstepping & running off
                                    // the edge of the texture
                    }

                    tnext = X86.FtoI(tdivz * z) + tadjust;
                    if (tnext > bbextentt)
                    {
                        tnext = bbextentt;
                    }
                    else if (tnext < 16)
                    {
                        tnext = 16; // guard against round-off error on <0 steps
                    }

                    r_turb_sstep = (snext - r_turb_s) >> 4;
                    r_turb_tstep = (tnext - r_turb_t) >> 4;
                }
                else
                {
                    // calculate s/z, t/z, zi->fixed s and t at last pixel in span (so can't step off polygon), clamp,
                    // calculate s and t steps across span by division, biasing steps low so we don't run off the
                    // texture
                    spancountminus1 = r_turb_spancount - 1;
                    sdivz += d_sdivzstepu * spancountminus1;
                    tdivz += d_tdivzstepu * spancountminus1;
                    zi += d_zistepu * spancountminus1;
                    z = (float)0x10000 / zi; // prescale to 16.16 fixed-point
                    snext = X86.FtoI(sdivz * z) + sadjust;
                    if (snext > bbextents)
                    {
                        snext = bbextents;
                    }
                    else if (snext < 16)
                    {
                        snext = 16; // prevent round-off error on <0 steps from causing overstepping & running off
                                    // the edge of the texture
                    }

                    tnext = X86.FtoI(tdivz * z) + tadjust;
                    if (tnext > bbextentt)
                    {
                        tnext = bbextentt;
                    }
                    else if (tnext < 16)
                    {
                        tnext = 16; // guard against round-off error on <0 steps
                    }

                    if (r_turb_spancount > 1)
                    {
                        r_turb_sstep = (snext - r_turb_s) / (r_turb_spancount - 1);
                        r_turb_tstep = (tnext - r_turb_t) / (r_turb_spancount - 1);
                    }
                }

                r_turb_s &= (CYCLE << 16) - 1;
                r_turb_t &= (CYCLE << 16) - 1;

                D_DrawTurbulent8Span();

                r_turb_s = snext;
                r_turb_t = tnext;
            }
            while (count > 0);
        }
        while ((pspan = pspan.pnext) != null);
    }

    // d_scan.c: D_DrawSpans8
    public void D_DrawSpans8(Espan? pspan)
    {
        int count, spancount;
        int pbase, pdest;
        int s, t, snext, tnext, sstep, tstep;
        float sdivz, tdivz, zi, z, du, dv, spancountminus1;
        float sdivz8stepu, tdivz8stepu, zi8stepu;
        byte[] texture = cacheblock;
        byte[] dest = d_viewbuffer;
        int width = cachewidth;

        sstep = 0; // keep compiler happy
        tstep = 0; // ditto

        pbase = cacheblockoffset;

        sdivz8stepu = d_sdivzstepu * 8;
        tdivz8stepu = d_tdivzstepu * 8;
        zi8stepu = d_zistepu * 8;

        do
        {
            pdest = d_viewbufferoffset + (screenwidth * pspan!.v) + pspan.u;

            count = pspan.count;

            // calculate the initial s/z, t/z, 1/z, s, and t and clamp
            du = pspan.u;
            dv = pspan.v;

            sdivz = d_sdivzorigin + (dv * d_sdivzstepv) + (du * d_sdivzstepu);
            tdivz = d_tdivzorigin + (dv * d_tdivzstepv) + (du * d_tdivzstepu);
            zi = d_ziorigin + (dv * d_zistepv) + (du * d_zistepu);
            z = (float)0x10000 / zi; // prescale to 16.16 fixed-point

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
                    dest[pdest++] = texture[pbase + (s >> 16) + ((t >> 16) * width)];
                    s += sstep;
                    t += tstep;
                }
                while (--spancount > 0);

                s = snext;
                t = tnext;
            }
            while (count > 0);
        }
        while ((pspan = pspan.pnext) != null);
    }

    // d_scan.c: D_DrawZSpans. The z-buffer is written two values at a time where the C writes an int at an aligned
    // address; the high half of the pair keeps what the C's or of the sign extended first value leaves in it.
    public void D_DrawZSpans(Espan? pspan)
    {
        int count, doublecount, izistep;
        int izi;
        int pdest;
        uint ltemp;
        double zi;
        float du, dv;
        short[] zbuffer = d_pzbuffer!;

        // FIXME: check for clamping/range problems
        // we count on FP exceptions being turned off to avoid range problems
        izistep = X86.FtoI(d_zistepu * 0x8000 * 0x10000);

        do
        {
            pdest = (int)(d_zwidth * pspan!.v) + pspan.u;

            count = pspan.count;

            // calculate the initial 1/z
            du = pspan.u;
            dv = pspan.v;

            zi = d_ziorigin + (dv * d_zistepv) + (du * d_zistepu);

            // we count on FP exceptions being turned off to avoid range problems
            izi = X86.FtoI(zi * 0x8000 * 0x10000);

            if ((pdest & 1) != 0)
            {
                zbuffer[pdest++] = (short)(izi >> 16);
                izi += izistep;
                count--;
            }

            if ((doublecount = count >> 1) > 0)
            {
                do
                {
                    ltemp = (uint)(izi >> 16);
                    izi += izistep;
                    ltemp |= (uint)izi & 0xFFFF0000;
                    izi += izistep;
                    zbuffer[pdest] = (short)ltemp;
                    zbuffer[pdest + 1] = (short)(ltemp >> 16);
                    pdest += 2;
                }
                while (--doublecount > 0);
            }

            if ((count & 1) != 0)
            {
                zbuffer[pdest] = (short)(izi >> 16);
            }
        }
        while ((pspan = pspan.pnext) != null);
    }
}
