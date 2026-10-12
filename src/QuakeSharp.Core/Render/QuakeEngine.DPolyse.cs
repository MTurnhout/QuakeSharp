using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Render;
using static QuakeSharp.Core.Common.MathLib;

namespace QuakeSharp.Core;

// d_polyse.c: routines for drawing sets of polygons sharing the same texture (used for Alias models). The skin, the
// view buffer, the z-buffer and the colormap are arrays; C's pointers into them are offsets.
public sealed partial class QuakeEngine
{
    // TODO: put in span spilling to shrink list size
    // !!! if this is changed, it must be changed in d_polysa.s too !!!
    private const int DPS_MAXSPANS = MAXHEIGHT + 1; // 1 extra for spanpackage that marks end

    public readonly int[] r_p0 = new int[6], r_p1 = new int[6], r_p2 = new int[6];

    /// <summary>d_polyse.c: d_pcolormap, an offset in acolormap</summary>
    public int d_pcolormap;

    public int d_aflatcolor;
    public int d_xdenom;

    public EdgeTable pedgetable = null!;

    public readonly EdgeTable[] edgetables;

    // FIXME: some of these can become statics
    public int a_sstepxfrac, a_tstepxfrac, r_lstepx, a_ststepxwhole;
    public int r_sstepx, r_tstepx, r_lstepy, r_sstepy, r_tstepy;
    public int r_zistepx, r_zistepy;
    public int d_aspancount, d_countextrastep;

    // d_polyse.c: a_spans (D_PolysetDraw's spans, on the C's stack), d_pedgespanpackage (an index in it)
    public readonly SpanPackage[] a_spans = new SpanPackage[DPS_MAXSPANS + 1 + 1];
    public int d_pedgespanpackage;

    private int ystart;

    // d_polyse.c: d_pdest, d_ptex, d_pz (offsets in the view buffer, the skin and the z-buffer)
    public int d_pdest, d_ptex;
    public int d_pz;
    public int d_sfrac, d_tfrac, d_light, d_zi;
    public int d_ptexextrastep, d_sfracextrastep;
    public int d_tfracextrastep, d_lightextrastep, d_pdestextrastep;
    public int d_lightbasestep, d_pdestbasestep, d_ptexbasestep;
    public int d_sfracbasestep, d_tfracbasestep;
    public int d_ziextrastep, d_zibasestep;
    public int d_pzextrastep, d_pzbasestep;

    /// <summary>d_polyse.c: skintable, offsets in the skin (skinstart)</summary>
    public readonly int[] skintable = new int[MAX_LBM_HEIGHT];

    public int skinwidth;
    public byte[]? skinstart;

    // D_PolysetRecursiveTriangle's new[6] at each depth (on the C's stack)
    private int[][] d_polyset_new = [];

    // d_polyse.c: D_PolysetDraw (its spans, on the C's stack, are a_spans)
    public void D_PolysetDraw()
    {
        if (r_affinetridesc.drawtype)
        {
            D_DrawSubdiv();
        }
        else
        {
            D_DrawNonSubdiv();
        }
    }

    // d_polyse.c: D_PolysetDrawFinalVerts
    public void D_PolysetDrawFinalVerts(FinalVert[] fv, int numverts)
    {
        int i, z;
        int zbuf;
        short[] zbuffer = d_pzbuffer!;
        byte[] skin = skinstart!;

        for (i = 0; i < numverts; i++)
        {
            // valid triangle coordinates for filling can include the bottom and right clip edges, due to the fill
            // rule; these shouldn't be drawn
            if ((fv[i].v[0] < r_refdef.vrectright) &&
                (fv[i].v[1] < r_refdef.vrectbottom))
            {
                z = fv[i].v[5] >> 16;
                zbuf = zspantable[fv[i].v[1]] + fv[i].v[0];
                if (z >= zbuffer[zbuf])
                {
                    int pix;

                    zbuffer[zbuf] = (short)z;
                    pix = skin[skintable[fv[i].v[3] >> 16] + (fv[i].v[2] >> 16)];
                    pix = acolormap[pix + (fv[i].v[4] & 0xFF00)];
                    d_viewbuffer[d_viewbufferoffset + d_scantable[fv[i].v[1]] + fv[i].v[0]] = (byte)pix;
                }
            }
        }
    }

    // d_polyse.c: D_DrawSubdiv
    public void D_DrawSubdiv()
    {
        MTriangle[] ptri;
        FinalVert[] pfv;
        int index0, index1, index2;
        int i;
        int lnumtriangles;

        pfv = r_affinetridesc.pfinalverts;
        ptri = r_affinetridesc.ptriangles;
        int start = r_affinetridesc.ptrianglesstart;
        lnumtriangles = r_affinetridesc.numtriangles;

        for (i = 0; i < lnumtriangles; i++)
        {
            index0 = ptri[start + i].vertindex[0];
            index1 = ptri[start + i].vertindex[1];
            index2 = ptri[start + i].vertindex[2];

            if ((((pfv[index0].v[1] - pfv[index1].v[1]) *
                (pfv[index0].v[0] - pfv[index2].v[0])) -
                ((pfv[index0].v[0] - pfv[index1].v[0]) *
                (pfv[index0].v[1] - pfv[index2].v[1]))) >= 0)
            {
                continue;
            }

            d_pcolormap = pfv[index0].v[4] & 0xFF00;

            if (ptri[start + i].facesfront != 0)
            {
                D_PolysetRecursiveTriangle(pfv[index0].v, pfv[index1].v, pfv[index2].v, 0);
            }
            else
            {
                int s0, s1, s2;

                s0 = pfv[index0].v[2];
                s1 = pfv[index1].v[2];
                s2 = pfv[index2].v[2];

                if ((pfv[index0].flags & ALIAS_ONSEAM) != 0)
                {
                    pfv[index0].v[2] += r_affinetridesc.seamfixupX16;
                }

                if ((pfv[index1].flags & ALIAS_ONSEAM) != 0)
                {
                    pfv[index1].v[2] += r_affinetridesc.seamfixupX16;
                }

                if ((pfv[index2].flags & ALIAS_ONSEAM) != 0)
                {
                    pfv[index2].v[2] += r_affinetridesc.seamfixupX16;
                }

                D_PolysetRecursiveTriangle(pfv[index0].v, pfv[index1].v, pfv[index2].v, 0);

                pfv[index0].v[2] = s0;
                pfv[index1].v[2] = s1;
                pfv[index2].v[2] = s2;
            }
        }
    }

    // d_polyse.c: D_DrawNonSubdiv
    public void D_DrawNonSubdiv()
    {
        MTriangle[] ptri;
        FinalVert[] pfv;
        int index0, index1, index2;
        int i;
        int lnumtriangles;

        pfv = r_affinetridesc.pfinalverts;
        ptri = r_affinetridesc.ptriangles;
        int start = r_affinetridesc.ptrianglesstart;
        lnumtriangles = r_affinetridesc.numtriangles;

        for (i = 0; i < lnumtriangles; i++)
        {
            ref MTriangle tri = ref ptri[start + i];
            index0 = tri.vertindex[0];
            index1 = tri.vertindex[1];
            index2 = tri.vertindex[2];

            d_xdenom = ((pfv[index0].v[1] - pfv[index1].v[1]) *
                (pfv[index0].v[0] - pfv[index2].v[0])) -
                ((pfv[index0].v[0] - pfv[index1].v[0]) * (pfv[index0].v[1] - pfv[index2].v[1]));

            if (d_xdenom >= 0)
            {
                continue;
            }

            r_p0[0] = pfv[index0].v[0]; // u
            r_p0[1] = pfv[index0].v[1]; // v
            r_p0[2] = pfv[index0].v[2]; // s
            r_p0[3] = pfv[index0].v[3]; // t
            r_p0[4] = pfv[index0].v[4]; // light
            r_p0[5] = pfv[index0].v[5]; // iz

            r_p1[0] = pfv[index1].v[0];
            r_p1[1] = pfv[index1].v[1];
            r_p1[2] = pfv[index1].v[2];
            r_p1[3] = pfv[index1].v[3];
            r_p1[4] = pfv[index1].v[4];
            r_p1[5] = pfv[index1].v[5];

            r_p2[0] = pfv[index2].v[0];
            r_p2[1] = pfv[index2].v[1];
            r_p2[2] = pfv[index2].v[2];
            r_p2[3] = pfv[index2].v[3];
            r_p2[4] = pfv[index2].v[4];
            r_p2[5] = pfv[index2].v[5];

            if (tri.facesfront == 0)
            {
                if ((pfv[index0].flags & ALIAS_ONSEAM) != 0)
                {
                    r_p0[2] += r_affinetridesc.seamfixupX16;
                }

                if ((pfv[index1].flags & ALIAS_ONSEAM) != 0)
                {
                    r_p1[2] += r_affinetridesc.seamfixupX16;
                }

                if ((pfv[index2].flags & ALIAS_ONSEAM) != 0)
                {
                    r_p2[2] += r_affinetridesc.seamfixupX16;
                }
            }

            D_PolysetSetEdgeTable();
            D_RasterizeAliasPolySmooth();
        }
    }

    // d_polyse.c: D_PolysetRecursiveTriangle (depth: which of the per-depth new[6] arrays to use)
    public void D_PolysetRecursiveTriangle(Span<int> lp1, Span<int> lp2, Span<int> lp3, int depth)
    {
        Span<int> temp;
        int d;
        int z;
        int zbuf;

        if (depth >= d_polyset_new.Length)
        {
            d_polyset_new = [.. d_polyset_new, .. Enumerable.Range(0, 32).Select(_ => new int[6])];
        }

        Span<int> @new = d_polyset_new[depth];

        d = lp2[0] - lp1[0];
        if (d is < -1 or > 1)
        {
            goto split;
        }

        d = lp2[1] - lp1[1];
        if (d is < -1 or > 1)
        {
            goto split;
        }

        d = lp3[0] - lp2[0];
        if (d is < -1 or > 1)
        {
            goto split2;
        }

        d = lp3[1] - lp2[1];
        if (d is < -1 or > 1)
        {
            goto split2;
        }

        d = lp1[0] - lp3[0];
        if (d is < -1 or > 1)
        {
            goto split3;
        }

        d = lp1[1] - lp3[1];
        if (d is < -1 or > 1)
        {
            goto split3;
        }

        return; // entire tri is filled

    split3:
        temp = lp1;
        lp1 = lp3;
        lp3 = lp2;
        lp2 = temp;

        goto split;

    split2:
        temp = lp1;
        lp1 = lp2;
        lp2 = lp3;
        lp3 = temp;

    split:
        // split this edge
        @new[0] = (lp1[0] + lp2[0]) >> 1;
        @new[1] = (lp1[1] + lp2[1]) >> 1;
        @new[2] = (lp1[2] + lp2[2]) >> 1;
        @new[3] = (lp1[3] + lp2[3]) >> 1;
        @new[5] = (lp1[5] + lp2[5]) >> 1;

        // draw the point if splitting a leading edge
        if (lp2[1] > lp1[1])
        {
            goto nodraw;
        }

        if ((lp2[1] == lp1[1]) && (lp2[0] < lp1[0]))
        {
            goto nodraw;
        }

        z = @new[5] >> 16;
        zbuf = zspantable[@new[1]] + @new[0];
        if (z >= d_pzbuffer![zbuf])
        {
            int pix;

            d_pzbuffer[zbuf] = (short)z;
            pix = acolormap[d_pcolormap + skinstart![skintable[@new[3] >> 16] + (@new[2] >> 16)]];
            d_viewbuffer[d_viewbufferoffset + d_scantable[@new[1]] + @new[0]] = (byte)pix;
        }

    nodraw:
        // recursively continue
        D_PolysetRecursiveTriangle(lp3, lp1, @new, depth + 1);
        D_PolysetRecursiveTriangle(lp3, @new, lp2, depth + 1);
    }

    // d_polyse.c: D_PolysetUpdateTables
    public void D_PolysetUpdateTables()
    {
        int i;
        int s;

        if (r_affinetridesc.skinwidth != skinwidth ||
            r_affinetridesc.pskin != skinstart)
        {
            skinwidth = r_affinetridesc.skinwidth;
            skinstart = r_affinetridesc.pskin;
            s = 0;
            for (i = 0; i < MAX_LBM_HEIGHT; i++, s += skinwidth)
            {
                skintable[i] = s;
            }
        }
    }

    // d_polyse.c: D_PolysetScanLeftEdge
    public void D_PolysetScanLeftEdge(int height)
    {
        do
        {
            ref SpanPackage p = ref a_spans[d_pedgespanpackage];
            p.pdest = d_pdest;
            p.pz = d_pz;
            p.count = d_aspancount;
            p.ptex = d_ptex;

            p.sfrac = d_sfrac;
            p.tfrac = d_tfrac;

            // FIXME: need to clamp l, s, t, at both ends?
            p.light = d_light;
            p.zi = d_zi;

            d_pedgespanpackage++;

            errorterm += erroradjustup;
            if (errorterm >= 0)
            {
                d_pdest += d_pdestextrastep;
                d_pz += d_pzextrastep;
                d_aspancount += d_countextrastep;
                d_ptex += d_ptexextrastep;
                d_sfrac += d_sfracextrastep;
                d_ptex += d_sfrac >> 16;

                d_sfrac &= 0xFFFF;
                d_tfrac += d_tfracextrastep;
                if ((d_tfrac & 0x10000) != 0)
                {
                    d_ptex += r_affinetridesc.skinwidth;
                    d_tfrac &= 0xFFFF;
                }

                d_light += d_lightextrastep;
                d_zi += d_ziextrastep;
                errorterm -= erroradjustdown;
            }
            else
            {
                d_pdest += d_pdestbasestep;
                d_pz += d_pzbasestep;
                d_aspancount += ubasestep;
                d_ptex += d_ptexbasestep;
                d_sfrac += d_sfracbasestep;
                d_ptex += d_sfrac >> 16;
                d_sfrac &= 0xFFFF;
                d_tfrac += d_tfracbasestep;
                if ((d_tfrac & 0x10000) != 0)
                {
                    d_ptex += r_affinetridesc.skinwidth;
                    d_tfrac &= 0xFFFF;
                }

                d_light += d_lightbasestep;
                d_zi += d_zibasestep;
            }
        }
        while (--height != 0);
    }

    // d_polyse.c: D_PolysetSetUpForLineScan
    public void D_PolysetSetUpForLineScan(int startvertu, int startvertv,
        int endvertu, int endvertv)
    {
        double dm, dn;
        int tm, tn;
        int ptemp;

        // TODO: implement x86 version
        errorterm = -1;

        tm = endvertu - startvertu;
        tn = endvertv - startvertv;

        if (((tm <= 16) && (tm >= -15)) &&
            ((tn <= 16) && (tn >= -15)))
        {
            ptemp = ((tm + 15) << 5) + (tn + 15);
            ubasestep = adivtab[ptemp * 2];
            erroradjustup = adivtab[(ptemp * 2) + 1];
            erroradjustdown = tn;
        }
        else
        {
            dm = tm;
            dn = tn;

            FloorDivMod(dm, dn, out ubasestep, out erroradjustup);

            erroradjustdown = X86.FtoI(dn);
        }
    }

    // d_polyse.c: D_PolysetCalcGradients
    public void D_PolysetCalcGradients(int skinwidth)
    {
        float xstepdenominv, ystepdenominv, t0, t1;
        float p01_minus_p21, p11_minus_p21, p00_minus_p20, p10_minus_p20;

        p00_minus_p20 = r_p0[0] - r_p2[0];
        p01_minus_p21 = r_p0[1] - r_p2[1];
        p10_minus_p20 = r_p1[0] - r_p2[0];
        p11_minus_p21 = r_p1[1] - r_p2[1];

        xstepdenominv = (float)(1.0 / (float)d_xdenom);

        ystepdenominv = -xstepdenominv;

        // ceil () for light so positive steps are exaggerated, negative steps diminished,  pushing us away from
        // underflow toward overflow. Underflow is very visible, overflow is very unlikely, because of ambient lighting
        t0 = r_p0[4] - r_p2[4];
        t1 = r_p1[4] - r_p2[4];
        r_lstepx = X86.FtoI(
            Math.Ceiling(((t1 * p01_minus_p21) - (t0 * p11_minus_p21)) * xstepdenominv));
        r_lstepy = X86.FtoI(
            Math.Ceiling(((t1 * p00_minus_p20) - (t0 * p10_minus_p20)) * ystepdenominv));

        t0 = r_p0[2] - r_p2[2];
        t1 = r_p1[2] - r_p2[2];
        r_sstepx = X86.FtoI(((t1 * p01_minus_p21) - (t0 * p11_minus_p21)) *
            xstepdenominv);
        r_sstepy = X86.FtoI(((t1 * p00_minus_p20) - (t0 * p10_minus_p20)) *
            ystepdenominv);

        t0 = r_p0[3] - r_p2[3];
        t1 = r_p1[3] - r_p2[3];
        r_tstepx = X86.FtoI(((t1 * p01_minus_p21) - (t0 * p11_minus_p21)) *
            xstepdenominv);
        r_tstepy = X86.FtoI(((t1 * p00_minus_p20) - (t0 * p10_minus_p20)) *
            ystepdenominv);

        t0 = r_p0[5] - r_p2[5];
        t1 = r_p1[5] - r_p2[5];
        r_zistepx = X86.FtoI(((t1 * p01_minus_p21) - (t0 * p11_minus_p21)) *
            xstepdenominv);
        r_zistepy = X86.FtoI(((t1 * p00_minus_p20) - (t0 * p10_minus_p20)) *
            ystepdenominv);

        a_sstepxfrac = r_sstepx & 0xFFFF;
        a_tstepxfrac = r_tstepx & 0xFFFF;

        a_ststepxwhole = (skinwidth * (r_tstepx >> 16)) + (r_sstepx >> 16);
    }

    // d_polyse.c: D_PolysetDrawSpans8
    public void D_PolysetDrawSpans8(int pspanpackage)
    {
        int lcount;
        int lpdest;
        int lptex;
        int lsfrac, ltfrac;
        int llight;
        int lzi;
        int lpz;
        byte[] dest = d_viewbuffer;
        short[] zbuffer = d_pzbuffer!;
        byte[] skin = r_affinetridesc.pskin;
        byte[] colormap = acolormap;

        do
        {
            ref SpanPackage p = ref a_spans[pspanpackage];
            lcount = d_aspancount - p.count;

            errorterm += erroradjustup;
            if (errorterm >= 0)
            {
                d_aspancount += d_countextrastep;
                errorterm -= erroradjustdown;
            }
            else
            {
                d_aspancount += ubasestep;
            }

            if (lcount != 0)
            {
                lpdest = p.pdest;
                lptex = p.ptex;
                lpz = p.pz;
                lsfrac = p.sfrac;
                ltfrac = p.tfrac;
                llight = p.light;
                lzi = p.zi;

                do
                {
                    if ((lzi >> 16) >= zbuffer[lpz])
                    {
                        dest[lpdest] = colormap[skin[lptex] + (llight & 0xFF00)];
                        zbuffer[lpz] = (short)(lzi >> 16);
                    }

                    lpdest++;
                    lzi += r_zistepx;
                    lpz++;
                    llight += r_lstepx;
                    lptex += a_ststepxwhole;
                    lsfrac += a_sstepxfrac;
                    lptex += lsfrac >> 16;
                    lsfrac &= 0xFFFF;
                    ltfrac += a_tstepxfrac;
                    if ((ltfrac & 0x10000) != 0)
                    {
                        lptex += r_affinetridesc.skinwidth;
                        ltfrac &= 0xFFFF;
                    }
                }
                while (--lcount != 0);
            }

            pspanpackage++;
        }
        while (a_spans[pspanpackage].count != -999999);
    }

    // d_polyse.c: D_RasterizeAliasPolySmooth
    public void D_RasterizeAliasPolySmooth()
    {
        int initialleftheight, initialrightheight;
        int[] plefttop, prighttop, pleftbottom, prightbottom;
        int working_lstepx, originalcount;

        plefttop = pedgetable.pleftedgevert0;
        prighttop = pedgetable.prightedgevert0;

        pleftbottom = pedgetable.pleftedgevert1;
        prightbottom = pedgetable.prightedgevert1;

        initialleftheight = pleftbottom[1] - plefttop[1];
        initialrightheight = prightbottom[1] - prighttop[1];

        // set the s, t, and light gradients, which are consistent across the triangle because being a triangle,
        // things are affine
        D_PolysetCalcGradients(r_affinetridesc.skinwidth);

        // rasterize the polygon

        // scan out the top (and possibly only) part of the left edge
        d_pedgespanpackage = 0;

        ystart = plefttop[1];
        d_aspancount = plefttop[0] - prighttop[0];

        d_ptex = (plefttop[2] >> 16) +
            ((plefttop[3] >> 16) * r_affinetridesc.skinwidth);
        d_sfrac = plefttop[2] & 0xFFFF;
        d_tfrac = plefttop[3] & 0xFFFF;
        d_light = plefttop[4];
        d_zi = plefttop[5];

        d_pdest = d_viewbufferoffset +
            (ystart * screenwidth) + plefttop[0];
        d_pz = (int)(ystart * d_zwidth) + plefttop[0];

        if (initialleftheight == 1)
        {
            ref SpanPackage p = ref a_spans[d_pedgespanpackage];
            p.pdest = d_pdest;
            p.pz = d_pz;
            p.count = d_aspancount;
            p.ptex = d_ptex;

            p.sfrac = d_sfrac;
            p.tfrac = d_tfrac;

            // FIXME: need to clamp l, s, t, at both ends?
            p.light = d_light;
            p.zi = d_zi;

            d_pedgespanpackage++;
        }
        else
        {
            D_PolysetSetUpForLineScan(plefttop[0], plefttop[1],
                pleftbottom[0], pleftbottom[1]);

            d_pzbasestep = (int)(d_zwidth + ubasestep);
            d_pzextrastep = d_pzbasestep + 1;

            d_pdestbasestep = screenwidth + ubasestep;
            d_pdestextrastep = d_pdestbasestep + 1;

            // TODO: can reuse partial expressions here

            // for negative steps in x along left edge, bias toward overflow rather than underflow (sort of turning
            // the floor () we did in the gradient calcs into ceil (), but plus a little bit)
            if (ubasestep < 0)
            {
                working_lstepx = r_lstepx - 1;
            }
            else
            {
                working_lstepx = r_lstepx;
            }

            d_countextrastep = ubasestep + 1;
            d_ptexbasestep = ((r_sstepy + (r_sstepx * ubasestep)) >> 16) +
                (((r_tstepy + (r_tstepx * ubasestep)) >> 16) *
                r_affinetridesc.skinwidth);
            d_sfracbasestep = (r_sstepy + (r_sstepx * ubasestep)) & 0xFFFF;
            d_tfracbasestep = (r_tstepy + (r_tstepx * ubasestep)) & 0xFFFF;
            d_lightbasestep = r_lstepy + (working_lstepx * ubasestep);
            d_zibasestep = r_zistepy + (r_zistepx * ubasestep);

            d_ptexextrastep = ((r_sstepy + (r_sstepx * d_countextrastep)) >> 16) +
                (((r_tstepy + (r_tstepx * d_countextrastep)) >> 16) *
                r_affinetridesc.skinwidth);
            d_sfracextrastep = (r_sstepy + (r_sstepx * d_countextrastep)) & 0xFFFF;
            d_tfracextrastep = (r_tstepy + (r_tstepx * d_countextrastep)) & 0xFFFF;
            d_lightextrastep = d_lightbasestep + working_lstepx;
            d_ziextrastep = d_zibasestep + r_zistepx;

            D_PolysetScanLeftEdge(initialleftheight);
        }

        // scan out the bottom part of the left edge, if it exists
        if (pedgetable.numleftedges == 2)
        {
            int height;

            plefttop = pleftbottom;
            pleftbottom = pedgetable.pleftedgevert2!;

            height = pleftbottom[1] - plefttop[1];

            // TODO: make this a function; modularize this function in general
            ystart = plefttop[1];
            d_aspancount = plefttop[0] - prighttop[0];
            d_ptex = (plefttop[2] >> 16) +
                ((plefttop[3] >> 16) * r_affinetridesc.skinwidth);
            d_sfrac = 0;
            d_tfrac = 0;
            d_light = plefttop[4];
            d_zi = plefttop[5];

            d_pdest = d_viewbufferoffset + (ystart * screenwidth) + plefttop[0];
            d_pz = (int)(ystart * d_zwidth) + plefttop[0];

            if (height == 1)
            {
                ref SpanPackage p = ref a_spans[d_pedgespanpackage];
                p.pdest = d_pdest;
                p.pz = d_pz;
                p.count = d_aspancount;
                p.ptex = d_ptex;

                p.sfrac = d_sfrac;
                p.tfrac = d_tfrac;

                // FIXME: need to clamp l, s, t, at both ends?
                p.light = d_light;
                p.zi = d_zi;

                d_pedgespanpackage++;
            }
            else
            {
                D_PolysetSetUpForLineScan(plefttop[0], plefttop[1],
                    pleftbottom[0], pleftbottom[1]);

                d_pdestbasestep = screenwidth + ubasestep;
                d_pdestextrastep = d_pdestbasestep + 1;

                d_pzbasestep = (int)(d_zwidth + ubasestep);
                d_pzextrastep = d_pzbasestep + 1;

                if (ubasestep < 0)
                {
                    working_lstepx = r_lstepx - 1;
                }
                else
                {
                    working_lstepx = r_lstepx;
                }

                d_countextrastep = ubasestep + 1;
                d_ptexbasestep = ((r_sstepy + (r_sstepx * ubasestep)) >> 16) +
                    (((r_tstepy + (r_tstepx * ubasestep)) >> 16) *
                    r_affinetridesc.skinwidth);
                d_sfracbasestep = (r_sstepy + (r_sstepx * ubasestep)) & 0xFFFF;
                d_tfracbasestep = (r_tstepy + (r_tstepx * ubasestep)) & 0xFFFF;
                d_lightbasestep = r_lstepy + (working_lstepx * ubasestep);
                d_zibasestep = r_zistepy + (r_zistepx * ubasestep);

                d_ptexextrastep = ((r_sstepy + (r_sstepx * d_countextrastep)) >> 16) +
                    (((r_tstepy + (r_tstepx * d_countextrastep)) >> 16) *
                    r_affinetridesc.skinwidth);
                d_sfracextrastep = (r_sstepy + (r_sstepx * d_countextrastep)) & 0xFFFF;
                d_tfracextrastep = (r_tstepy + (r_tstepx * d_countextrastep)) & 0xFFFF;
                d_lightextrastep = d_lightbasestep + working_lstepx;
                d_ziextrastep = d_zibasestep + r_zistepx;

                D_PolysetScanLeftEdge(height);
            }
        }

        // scan out the top (and possibly only) part of the right edge, updating the count field
        d_pedgespanpackage = 0;

        D_PolysetSetUpForLineScan(prighttop[0], prighttop[1],
            prightbottom[0], prightbottom[1]);
        d_aspancount = 0;
        d_countextrastep = ubasestep + 1;
        originalcount = a_spans[initialrightheight].count;
        a_spans[initialrightheight].count = -999999; // mark end of the spanpackages
        D_PolysetDrawSpans8(0);

        // scan out the bottom part of the right edge, if it exists
        if (pedgetable.numrightedges == 2)
        {
            int height;
            int pstart;

            pstart = initialrightheight;
            a_spans[pstart].count = originalcount;

            d_aspancount = prightbottom[0] - prighttop[0];

            prighttop = prightbottom;
            prightbottom = pedgetable.prightedgevert2!;

            height = prightbottom[1] - prighttop[1];

            D_PolysetSetUpForLineScan(prighttop[0], prighttop[1],
                prightbottom[0], prightbottom[1]);

            d_countextrastep = ubasestep + 1;
            a_spans[initialrightheight + height].count = -999999; // mark end of the spanpackages
            D_PolysetDrawSpans8(pstart);
        }
    }

    // d_polyse.c: D_PolysetSetEdgeTable
    public void D_PolysetSetEdgeTable()
    {
        int edgetableindex;

        edgetableindex = 0; // assume the vertices are already in top to bottom order

        // determine which edges are right & left, and the order in which to rasterize them
        if (r_p0[1] >= r_p1[1])
        {
            if (r_p0[1] == r_p1[1])
            {
                if (r_p0[1] < r_p2[1])
                {
                    pedgetable = edgetables[2];
                }
                else
                {
                    pedgetable = edgetables[5];
                }

                return;
            }
            else
            {
                edgetableindex = 1;
            }
        }

        if (r_p0[1] == r_p2[1])
        {
            if (edgetableindex != 0)
            {
                pedgetable = edgetables[8];
            }
            else
            {
                pedgetable = edgetables[9];
            }

            return;
        }
        else if (r_p1[1] == r_p2[1])
        {
            if (edgetableindex != 0)
            {
                pedgetable = edgetables[10];
            }
            else
            {
                pedgetable = edgetables[11];
            }

            return;
        }

        if (r_p0[1] > r_p2[1])
        {
            edgetableindex += 2;
        }

        if (r_p1[1] > r_p2[1])
        {
            edgetableindex += 4;
        }

        pedgetable = edgetables[edgetableindex];
    }
}
