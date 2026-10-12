using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Render;
using static QuakeSharp.Core.Common.MathLib;

namespace QuakeSharp.Core;

// d_edge.c: the surfaces' spans drawn: texture gradients, the surface cache, sky, water and the background
public sealed partial class QuakeEngine
{
    // d_edge.c: miplevel (static)
    private int miplevel;

    public float scale_for_mip;
    public int ubasestep, errorterm, erroradjustup, erroradjustdown;
    public int vstartscan;

    public Vec3 transformed_modelorg;

    // d_edge.c: D_DrawPoly
    public static void D_DrawPoly()
    {
        // this driver takes spans, not polygons
    }

    // d_edge.c: D_MipLevelForScale
    public int D_MipLevelForScale(float scale)
    {
        int lmiplevel;

        if (scale >= d_scalemip[0])
        {
            lmiplevel = 0;
        }
        else if (scale >= d_scalemip[1])
        {
            lmiplevel = 1;
        }
        else if (scale >= d_scalemip[2])
        {
            lmiplevel = 2;
        }
        else
        {
            lmiplevel = 3;
        }

        if (lmiplevel < d_minmip)
        {
            lmiplevel = d_minmip;
        }

        return lmiplevel;
    }

    // d_edge.c: D_DrawSolidSurface (the C writes a byte or four at a time, all of them the color)
    // FIXME: clean this up
    public void D_DrawSolidSurface(Surf surf, int color)
    {
        Espan? span;
        int pdest;
        int u, u2;
        byte pix = (byte)color;
        byte[] buffer = d_viewbuffer;

        for (span = surf.spans; span != null; span = span.pnext)
        {
            pdest = d_viewbufferoffset + (screenwidth * span.v);
            u = span.u;
            u2 = span.u + span.count - 1;
            for (; u <= u2; u++)
            {
                buffer[pdest + u] = pix;
            }
        }
    }

    // d_edge.c: D_CalcGradients
    public void D_CalcGradients(MSurface pface)
    {
        float mipscale;
        Vec3 p_temp1 = default;
        Vec3 p_saxis = default, p_taxis = default;
        float t;

        mipscale = (float)(1.0 / (float)(1 << miplevel));

        Vec3 vecs0 = new(pface.texinfo.vecs[0][0], pface.texinfo.vecs[0][1], pface.texinfo.vecs[0][2]);
        Vec3 vecs1 = new(pface.texinfo.vecs[1][0], pface.texinfo.vecs[1][1], pface.texinfo.vecs[1][2]);
        TransformVector(vecs0, ref p_saxis);
        TransformVector(vecs1, ref p_taxis);

        t = xscaleinv * mipscale;
        d_sdivzstepu = p_saxis[0] * t;
        d_tdivzstepu = p_taxis[0] * t;

        t = yscaleinv * mipscale;
        d_sdivzstepv = -p_saxis[1] * t;
        d_tdivzstepv = -p_taxis[1] * t;

        d_sdivzorigin = (p_saxis[2] * mipscale) - (xcenter * d_sdivzstepu) -
            (ycenter * d_sdivzstepv);
        d_tdivzorigin = (p_taxis[2] * mipscale) - (xcenter * d_tdivzstepu) -
            (ycenter * d_tdivzstepv);

        VectorScale(transformed_modelorg, mipscale, ref p_temp1);

        t = 0x10000 * mipscale;
        sadjust = X86.FtoI((X86.FtoI((DotProduct(p_temp1, p_saxis) * 0x10000) + 0.5) -
            ((pface.texturemins[0] << 16) >> miplevel))
            + (pface.texinfo.vecs[0][3] * t));
        tadjust = X86.FtoI((X86.FtoI((DotProduct(p_temp1, p_taxis) * 0x10000) + 0.5) -
            ((pface.texturemins[1] << 16) >> miplevel))
            + (pface.texinfo.vecs[1][3] * t));

        // -1 (-epsilon) so we never wander off the edge of the texture
        bbextents = ((pface.extents[0] << 16) >> miplevel) - 1;
        bbextentt = ((pface.extents[1] << 16) >> miplevel) - 1;
    }

    // d_edge.c: D_DrawSurfaces
    public void D_DrawSurfaces()
    {
        Surf s;
        int i;
        MSurface pface;
        SurfCache pcurrentcache;
        Vec3 world_transformed_modelorg = default;
        Vec3 local_modelorg = default;

        currententity = cl_entities[0];
        TransformVector(modelorg, ref transformed_modelorg);
        VectorCopy(transformed_modelorg, ref world_transformed_modelorg);

        // TODO: could preset a lot of this at mode set time
        if (r_drawflat.value != 0)
        {
            for (i = 1; i < surface_p; i++)
            {
                s = surfaces[i];
                if (s.spans == null)
                {
                    continue;
                }

                d_zistepu = s.d_zistepu;
                d_zistepv = s.d_zistepv;
                d_ziorigin = s.d_ziorigin;

                // (the C's color is the low byte of the msurface_t's address: here the surface's index)
                D_DrawSolidSurface(s, i & 0xFF);
                D_DrawZSpans(s.spans);
            }
        }
        else
        {
            for (i = 1; i < surface_p; i++)
            {
                s = surfaces[i];
                if (s.spans == null)
                {
                    continue;
                }

                r_drawnpolycount++;

                d_zistepu = s.d_zistepu;
                d_zistepv = s.d_zistepv;
                d_ziorigin = s.d_ziorigin;

                if ((s.flags & MSurface.SURF_DRAWSKY) != 0)
                {
                    if (r_skymade == 0)
                    {
                        R_MakeSky();
                    }

                    D_DrawSkyScans8(s.spans);
                    D_DrawZSpans(s.spans);
                }
                else if ((s.flags & MSurface.SURF_DRAWBACKGROUND) != 0)
                {
                    // set up a gradient for the background surface that places it effectively at infinity distance
                    // from the viewpoint
                    d_zistepu = 0;
                    d_zistepv = 0;
                    d_ziorigin = -0.9f;

                    D_DrawSolidSurface(s, X86.FtoI(r_clearcolor.value) & 0xFF);
                    D_DrawZSpans(s.spans);
                }
                else if ((s.flags & MSurface.SURF_DRAWTURB) != 0)
                {
                    pface = s.data!;
                    miplevel = 0;
                    cacheblock = pface.texinfo.texture.pixels;
                    cacheblockoffset = (int)pface.texinfo.texture.offsets[0];
                    cachewidth = 64;

                    if (s.insubmodel)
                    {
                        // FIXME: we don't want to do all this for every polygon!
                        // TODO: store once at start of frame
                        currententity = s.entity!; // FIXME: make this passed in to R_RotateBmodel ()
                        VectorSubtract(r_origin, currententity.origin,
                            ref local_modelorg);
                        TransformVector(local_modelorg, ref transformed_modelorg);

                        R_RotateBmodel(); // FIXME: don't mess with the frustum, make entity passed in
                    }

                    D_CalcGradients(pface);
                    Turbulent8(s.spans);
                    D_DrawZSpans(s.spans);

                    if (s.insubmodel)
                    {
                        // restore the old drawing state
                        // FIXME: we don't want to do this every time!
                        // TODO: speed up
                        currententity = cl_entities[0];
                        VectorCopy(world_transformed_modelorg,
                            ref transformed_modelorg);
                        VectorCopy(base_vpn, ref vpn);
                        VectorCopy(base_vup, ref vup);
                        VectorCopy(base_vright, ref vright);
                        VectorCopy(base_modelorg, ref modelorg);
                        R_TransformFrustum();
                    }
                }
                else
                {
                    if (s.insubmodel)
                    {
                        // FIXME: we don't want to do all this for every polygon!
                        // TODO: store once at start of frame
                        currententity = s.entity!; // FIXME: make this passed in to R_RotateBmodel ()
                        VectorSubtract(r_origin, currententity.origin, ref local_modelorg);
                        TransformVector(local_modelorg, ref transformed_modelorg);

                        R_RotateBmodel(); // FIXME: don't mess with the frustum, make entity passed in
                    }

                    pface = s.data!;
                    miplevel = D_MipLevelForScale(s.nearzi * scale_for_mip
                        * pface.texinfo.mipadjust);

                    // FIXME: make this passed in to D_CacheSurface
                    pcurrentcache = D_CacheSurface(pface, miplevel);

                    cacheblock = surfcache;
                    cacheblockoffset = pcurrentcache.DataOffset;
                    cachewidth = (int)pcurrentcache.width;

                    D_CalcGradients(pface);

                    D_DrawSpans8(s.spans);

                    D_DrawZSpans(s.spans);

                    if (s.insubmodel)
                    {
                        // restore the old drawing state
                        // FIXME: we don't want to do this every time!
                        // TODO: speed up
                        currententity = cl_entities[0];
                        VectorCopy(world_transformed_modelorg,
                            ref transformed_modelorg);
                        VectorCopy(base_vpn, ref vpn);
                        VectorCopy(base_vup, ref vup);
                        VectorCopy(base_vright, ref vright);
                        VectorCopy(base_modelorg, ref modelorg);
                        R_TransformFrustum();
                    }
                }
            }
        }
    }
}
