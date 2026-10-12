using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Render;
using static QuakeSharp.Core.Common.MathLib;

namespace QuakeSharp.Core;

// r_sky.c: the sky's two layers combined into one texture (newsky), drawn by d_sky.c
public sealed partial class QuakeEngine
{
    public int iskyspeed = 8;
    public int iskyspeed2 = 2;
    public float skyspeed, skyspeed2;

    public float skytime;

    /// <summary>r_sky.c: r_skysource (newsky)</summary>
    public byte[] r_skysource = [];

    public int r_skymade;

    // TODO: clean up these routines
    public readonly byte[] bottomsky = new byte[128 * 131];
    public readonly byte[] bottommask = new byte[128 * 131];

    /// <summary>newsky and topsky both pack in here, 128 bytes of newsky on the left of each scan, 128 bytes of topsky
    /// on the right, because the low-level drawers need 256-byte scan widths</summary>
    public readonly byte[] newsky = new byte[128 * 256];

    // r_sky.c: R_MakeSky's statics
    private int r_makesky_xlast = -1, r_makesky_ylast = -1;

    // r_sky.c: R_InitSky
    // A sky texture is 256*128, with the right side being a masked overlay
    public void R_InitSky(Texture mt)
    {
        int i, j;
        int src;
        byte[] pixels = mt.pixels;

        src = (int)mt.offsets[0];

        for (i = 0; i < 128; i++)
        {
            for (j = 0; j < 128; j++)
            {
                newsky[(i * 256) + j + 128] = pixels[src + (i * 256) + j + 128];
            }
        }

        for (i = 0; i < 128; i++)
        {
            for (j = 0; j < 131; j++)
            {
                if (pixels[src + (i * 256) + (j & 0x7F)] != 0)
                {
                    bottomsky[(i * 131) + j] = pixels[src + (i * 256) + (j & 0x7F)];
                    bottommask[(i * 131) + j] = 0;
                }
                else
                {
                    bottomsky[(i * 131) + j] = 0;
                    bottommask[(i * 131) + j] = 0xff;
                }
            }
        }

        r_skysource = newsky;
    }

    // r_sky.c: R_MakeSky (the C's UNALIGNED_OK path copies four bytes at a time from rows of 131: the same bytes)
    public void R_MakeSky()
    {
        int x, y;
        int ofs, baseofs;
        int xshift, yshift;
        int pnewsky;

        xshift = X86.FtoI(skytime * skyspeed);
        yshift = X86.FtoI(skytime * skyspeed);

        if ((xshift == r_makesky_xlast) && (yshift == r_makesky_ylast))
        {
            return;
        }

        r_makesky_xlast = xshift;
        r_makesky_ylast = yshift;

        pnewsky = 0;

        for (y = 0; y < SKYSIZE; y++)
        {
            baseofs = ((y + yshift) & SKYMASK) * 131;

            for (x = 0; x < SKYSIZE; x++)
            {
                ofs = baseofs + ((x + xshift) & SKYMASK);

                newsky[pnewsky] = (byte)((newsky[pnewsky + 128] &
                    bottommask[ofs]) |
                    bottomsky[ofs]);
                pnewsky++;
            }

            pnewsky += 128;
        }

        r_skymade = 1;
    }

    // r_sky.c: R_GenSkyTile
    public void R_GenSkyTile(byte[] pdest, int pd)
    {
        int x, y;
        int ofs, baseofs;
        int xshift, yshift;
        int pnewsky;

        xshift = X86.FtoI(skytime * skyspeed);
        yshift = X86.FtoI(skytime * skyspeed);

        pnewsky = 0;

        for (y = 0; y < SKYSIZE; y++)
        {
            baseofs = ((y + yshift) & SKYMASK) * 131;

            for (x = 0; x < SKYSIZE; x++)
            {
                ofs = baseofs + ((x + xshift) & SKYMASK);

                pdest[pd] = (byte)((newsky[pnewsky + 128] &
                    bottommask[ofs]) |
                    bottomsky[ofs]);
                pnewsky++;
                pd++;
            }

            pnewsky += 128;
        }
    }

    // r_sky.c: R_SetSkyFrame
    public void R_SetSkyFrame()
    {
        int g, s1, s2;
        float temp;

        skyspeed = iskyspeed;
        skyspeed2 = iskyspeed2;

        g = GreatestCommonDivisor(iskyspeed, iskyspeed2);
        s1 = iskyspeed / g;
        s2 = iskyspeed2 / g;
        temp = SKYSIZE * s1 * s2;

        skytime = (float)(cl.time - (X86.FtoI(cl.time / temp) * temp));

        r_skymade = 0;
    }

    // d_sky.c: SKY_SPAN_SHIFT, SKY_SPAN_MAX
    private const int SKY_SPAN_SHIFT = 5;
    private const int SKY_SPAN_MAX = 1 << SKY_SPAN_SHIFT;

    // d_local.h: R_SKY_SMASK, R_SKY_TMASK
    private const int R_SKY_SMASK = 0x007F0000;
    private const int R_SKY_TMASK = 0x007F0000;

    // d_sky.c: D_Sky_uv_To_st
    public void D_Sky_uv_To_st(int u, int v, out int s, out int t)
    {
        float wu, wv, temp;
        Vec3 end = default;

        if (r_refdef.vrect.width >= r_refdef.vrect.height)
        {
            temp = r_refdef.vrect.width;
        }
        else
        {
            temp = r_refdef.vrect.height;
        }

        wu = (float)(8192.0 * (float)(u - ((int)vid.width >> 1)) / temp);
        wv = (float)(8192.0 * (float)(((int)vid.height >> 1) - v) / temp);

        end[0] = (4096 * vpn[0]) + (wu * vright[0]) + (wv * vup[0]);
        end[1] = (4096 * vpn[1]) + (wu * vright[1]) + (wv * vup[1]);
        end[2] = (4096 * vpn[2]) + (wu * vright[2]) + (wv * vup[2]);
        end[2] *= 3;
        VectorNormalize(ref end);

        temp = skytime * skyspeed; // TODO: add D_SetupFrame & set this there
        s = X86.FtoI((temp + (6 * ((SKYSIZE / 2) - 1) * end[0])) * 0x10000);
        t = X86.FtoI((temp + (6 * ((SKYSIZE / 2) - 1) * end[1])) * 0x10000);
    }

    // d_sky.c: D_DrawSkyScans8
    public void D_DrawSkyScans8(Espan? pspan)
    {
        int count, spancount, u, v;
        int pdest;
        int s, t, snext, tnext, sstep, tstep;
        int spancountminus1;
        byte[] dest = d_viewbuffer;
        byte[] sky = r_skysource;

        sstep = 0; // keep compiler happy
        tstep = 0; // ditto
        snext = tnext = 0;

        do
        {
            pdest = d_viewbufferoffset + (screenwidth * pspan!.v) + pspan.u;

            count = pspan.count;

            // calculate the initial s & t
            u = pspan.u;
            v = pspan.v;
            D_Sky_uv_To_st(u, v, out s, out t);

            do
            {
                if (count >= SKY_SPAN_MAX)
                {
                    spancount = SKY_SPAN_MAX;
                }
                else
                {
                    spancount = count;
                }

                count -= spancount;

                if (count != 0)
                {
                    u += spancount;

                    // calculate s and t at far end of span, calculate s and t steps across span by shifting
                    D_Sky_uv_To_st(u, v, out snext, out tnext);

                    sstep = (snext - s) >> SKY_SPAN_SHIFT;
                    tstep = (tnext - t) >> SKY_SPAN_SHIFT;
                }
                else
                {
                    // calculate s and t at last pixel in span, calculate s and t steps across span by division
                    spancountminus1 = spancount - 1;

                    if (spancountminus1 > 0)
                    {
                        u += spancountminus1;
                        D_Sky_uv_To_st(u, v, out snext, out tnext);

                        sstep = (snext - s) / spancountminus1;
                        tstep = (tnext - t) / spancountminus1;
                    }
                }

                do
                {
                    dest[pdest++] = sky[((t & R_SKY_TMASK) >> 8) +
                        ((s & R_SKY_SMASK) >> 16)];
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
}
