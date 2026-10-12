using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Render;
using static QuakeSharp.Core.Common.MathLib;
using static QuakeSharp.Core.Model.BspFile;

namespace QuakeSharp.Core;

// r_surf.c: surface-related refresh code: a surface's lightmap and its lit texture, built into the surface cache.
// The texture and the cache are byte arrays; C's pointers into them are offsets. WinQuake draws 8-bit pixels
// (r_pixbytes 1): the 16-bit paths are left out.
public sealed partial class QuakeEngine
{
    public readonly DrawSurf r_drawsurf = new();

    public int lightleft, sourcesstep, blocksize, sourcetstep;
    public int lightdelta, lightdeltastep;
    public int lightright, lightleftstep, lightrightstep, blockdivshift;
    public uint blockdivmask;

    // r_surf.c: prowdestbase (an offset in the surface cache), pbasesource (an offset in the texture's pixels)
    public int prowdestbase;
    public int pbasesource;

    /// <summary>used by ASM files</summary>
    public int surfrowbytes;

    /// <summary>r_surf.c: r_lightptr, an index in blocklights</summary>
    public int r_lightptr;

    public int r_stepback;
    public int r_lightwidth;
    public int r_numhblocks, r_numvblocks;

    // r_surf.c: r_source, r_sourcemax (offsets in the texture's pixels)
    public int r_source, r_sourcemax;

    public readonly uint[] blocklights = new uint[18 * 18];

    // r_surf.c: R_AddDynamicLights
    public void R_AddDynamicLights()
    {
        MSurface surf;
        int lnum;
        int sd, td;
        float dist, rad, minlight;
        Vec3 impact = default, local = default;
        int s, t;
        int i;
        int smax, tmax;
        MTexinfo tex;

        surf = r_drawsurf.surf;
        smax = (surf.extents[0] >> 4) + 1;
        tmax = (surf.extents[1] >> 4) + 1;
        tex = surf.texinfo;

        for (lnum = 0; lnum < MAX_DLIGHTS; lnum++)
        {
            if ((surf.dlightbits & (1 << lnum)) == 0)
            {
                continue; // not lit by this light
            }

            rad = cl_dlights[lnum].radius;
            dist = DotProduct(cl_dlights[lnum].origin, surf.plane.normal) -
                surf.plane.dist;
            rad = (float)(rad - Math.Abs((double)dist));
            minlight = cl_dlights[lnum].minlight;
            if (rad < minlight)
            {
                continue;
            }

            minlight = rad - minlight;

            for (i = 0; i < 3; i++)
            {
                impact[i] = cl_dlights[lnum].origin[i] -
                    (surf.plane.normal[i] * dist);
            }

            local[0] = (impact[0] * tex.vecs[0][0]) + (impact[1] * tex.vecs[0][1]) + (impact[2] * tex.vecs[0][2]) + tex.vecs[0][3];
            local[1] = (impact[0] * tex.vecs[1][0]) + (impact[1] * tex.vecs[1][1]) + (impact[2] * tex.vecs[1][2]) + tex.vecs[1][3];

            local[0] -= surf.texturemins[0];
            local[1] -= surf.texturemins[1];

            for (t = 0; t < tmax; t++)
            {
                td = X86.FtoI(local[1] - (t * 16));
                if (td < 0)
                {
                    td = -td;
                }

                for (s = 0; s < smax; s++)
                {
                    sd = X86.FtoI(local[0] - (s * 16));
                    if (sd < 0)
                    {
                        sd = -sd;
                    }

                    if (sd > td)
                    {
                        dist = sd + (td >> 1);
                    }
                    else
                    {
                        dist = td + (sd >> 1);
                    }

                    if (dist < minlight)
                    {
                        blocklights[(t * smax) + s] = (uint)X86.FtoI(blocklights[(t * smax) + s] + ((rad - dist) * 256));
                    }
                }
            }
        }
    }

    // r_surf.c: R_BuildLightMap
    // Combine and scale multiple lightmaps into the 8.8 format in blocklights
    public void R_BuildLightMap()
    {
        int smax, tmax;
        int t;
        int i, size;
        int lightmap;
        uint scale;
        int maps;
        MSurface surf;

        surf = r_drawsurf.surf;

        smax = (surf.extents[0] >> 4) + 1;
        tmax = (surf.extents[1] >> 4) + 1;
        size = smax * tmax;
        lightmap = surf.samples;

        if (r_fullbright.value != 0 || cl.worldmodel!.lightdata == null)
        {
            for (i = 0; i < size; i++)
            {
                blocklights[i] = 0;
            }

            return;
        }

        // clear to ambient
        for (i = 0; i < size; i++)
        {
            blocklights[i] = (uint)(r_refdef.ambientlight << 8);
        }

        // add all the lightmaps
        if (lightmap >= 0)
        {
            byte[] lightdata = surf.lightdata!;
            for (maps = 0; maps < MAXLIGHTMAPS && surf.styles[maps] != 255; maps++)
            {
                scale = (uint)r_drawsurf.lightadj[maps]; // 8.8 fraction
                for (i = 0; i < size; i++)
                {
                    blocklights[i] += lightdata[lightmap + i] * scale;
                }

                lightmap += size; // skip to next lightmap
            }
        }

        // add all the dynamic lights
        if (surf.dlightframe == r_framecount)
        {
            R_AddDynamicLights();
        }

        // bound, invert, and shift
        for (i = 0; i < size; i++)
        {
            t = ((255 * 256) - (int)blocklights[i]) >> (8 - VID_CBITS);

            if (t < (1 << 6))
            {
                t = 1 << 6;
            }

            blocklights[i] = (uint)t;
        }
    }

    // r_surf.c: R_TextureAnimation
    // Returns the proper texture for a given time and base texture
    public Texture R_TextureAnimation(Texture @base)
    {
        int reletive;
        int count;

        if (currententity.frame != 0)
        {
            if (@base.alternate_anims != null)
            {
                @base = @base.alternate_anims;
            }
        }

        if (@base.anim_total == 0)
        {
            return @base;
        }

        reletive = X86.FtoI(cl.time * 10) % @base.anim_total;

        count = 0;
        while (@base.anim_min > reletive || @base.anim_max <= reletive)
        {
            @base = @base.anim_next ?? throw Sys_Error("R_TextureAnimation: broken cycle");
            if (++count > 100)
            {
                throw Sys_Error("R_TextureAnimation: infinite cycle");
            }
        }

        return @base;
    }

    // r_surf.c: R_DrawSurface
    public void R_DrawSurface()
    {
        int basetptr;
        int smax, tmax, twidth;
        int u;
        int soffset, basetoffset, texwidth;
        int horzblockstep;
        int pcolumndest;
        Texture mt;

        // calculate the lightings
        R_BuildLightMap();

        surfrowbytes = r_drawsurf.rowbytes;

        mt = r_drawsurf.texture;

        r_source = (int)mt.offsets[r_drawsurf.surfmip];

        // the fractional light values should range from 0 to (VID_GRADES - 1) << 16 from a source range of 0 - 255
        texwidth = (int)(mt.width >> r_drawsurf.surfmip);

        blocksize = 16 >> r_drawsurf.surfmip;
        blockdivshift = 4 - r_drawsurf.surfmip;
        blockdivmask = (uint)((1 << blockdivshift) - 1);

        r_lightwidth = (r_drawsurf.surf.extents[0] >> 4) + 1;

        r_numhblocks = r_drawsurf.surfwidth >> blockdivshift;
        r_numvblocks = r_drawsurf.surfheight >> blockdivshift;

        // ==============================

        // TODO: only needs to be set when there is a display settings change
        horzblockstep = blocksize;

        smax = (int)(mt.width >> r_drawsurf.surfmip);
        twidth = texwidth;
        tmax = (int)(mt.height >> r_drawsurf.surfmip);
        sourcetstep = texwidth;
        r_stepback = tmax * twidth;

        r_sourcemax = r_source + (tmax * smax);

        soffset = r_drawsurf.surf.texturemins[0];
        basetoffset = r_drawsurf.surf.texturemins[1];

        // << 16 components are to guarantee positive values for %
        soffset = ((soffset >> r_drawsurf.surfmip) + (smax << 16)) % smax;
        basetptr = r_source + (((((basetoffset >> r_drawsurf.surfmip)
            + (tmax << 16)) % tmax) * twidth));

        pcolumndest = r_drawsurf.surfdat;

        for (u = 0; u < r_numhblocks; u++)
        {
            r_lightptr = u;

            prowdestbase = pcolumndest;

            pbasesource = basetptr + soffset;

            switch (r_drawsurf.surfmip)
            {
                case 0:
                    R_DrawSurfaceBlock8_mip0(mt.pixels);
                    break;
                case 1:
                    R_DrawSurfaceBlock8_mip1(mt.pixels);
                    break;
                case 2:
                    R_DrawSurfaceBlock8_mip2(mt.pixels);
                    break;
                default:
                    R_DrawSurfaceBlock8_mip3(mt.pixels);
                    break;
            }

            soffset += blocksize;
            if (soffset >= smax)
            {
                soffset = 0;
            }

            pcolumndest += horzblockstep;
        }
    }

    // =============================================================================

    // r_surf.c: R_DrawSurfaceBlock8_mip0
    public void R_DrawSurfaceBlock8_mip0(byte[] texture) => R_DrawSurfaceBlock8(texture, 16, 4);

    // r_surf.c: R_DrawSurfaceBlock8_mip1
    public void R_DrawSurfaceBlock8_mip1(byte[] texture) => R_DrawSurfaceBlock8(texture, 8, 3);

    // r_surf.c: R_DrawSurfaceBlock8_mip2
    public void R_DrawSurfaceBlock8_mip2(byte[] texture) => R_DrawSurfaceBlock8(texture, 4, 2);

    // r_surf.c: R_DrawSurfaceBlock8_mip3
    public void R_DrawSurfaceBlock8_mip3(byte[] texture) => R_DrawSurfaceBlock8(texture, 2, 1);

    // The body of the four R_DrawSurfaceBlock8_mip functions, which differ only in their block size (16 >> mip) and
    // its shift. The light pointer is unsigned, so the steps down the block are unsigned differences shifted right.
    private void R_DrawSurfaceBlock8(byte[] texture, int size, int shift)
    {
        int v, i, b, lightstep, lighttemp, light;
        byte pix;
        int psource, prowdest;
        byte[] colormap = vid.colormap;
        byte[] dest = surfcache;

        psource = pbasesource;
        prowdest = prowdestbase;

        for (v = 0; v < r_numvblocks; v++)
        {
            // FIXME: make these locals?
            // FIXME: use delta rather than both right and left, like ASM?
            lightleft = (int)blocklights[r_lightptr];
            lightright = (int)blocklights[r_lightptr + 1];
            r_lightptr += r_lightwidth;
            lightleftstep = (int)((blocklights[r_lightptr] - (uint)lightleft) >> shift);
            lightrightstep = (int)((blocklights[r_lightptr + 1] - (uint)lightright) >> shift);

            for (i = 0; i < size; i++)
            {
                lighttemp = lightleft - lightright;
                lightstep = lighttemp >> shift;

                light = lightright;

                for (b = size - 1; b >= 0; b--)
                {
                    pix = texture[psource + b];
                    dest[prowdest + b] = colormap[(light & 0xFF00) + pix];
                    light += lightstep;
                }

                psource += sourcetstep;
                lightright += lightrightstep;
                lightleft += lightleftstep;
                prowdest += surfrowbytes;
            }

            if (psource >= r_sourcemax)
            {
                psource -= r_stepback;
            }
        }
    }
}
