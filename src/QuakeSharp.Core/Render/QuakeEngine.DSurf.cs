using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using QuakeSharp.Core.Render;

namespace QuakeSharp.Core;

// d_surf.c: rasterization driver surface heap manager. The cache is one byte array, as the C's block of memory; its
// blocks keep their headers' fields in SurfCache objects at the offsets the C's headers have, with the C's 32-bit
// sizes, so the cache fills, wraps and thrashes (the "ram" icon) as the C's does.
public sealed partial class QuakeEngine
{
    // d_local.h: SURFCACHE_SIZE_AT_320X200
    private const int SURFCACHE_SIZE_AT_320X200 = 600 * 1024;

    private const int GUARDSIZE = 4;

    public float surfscale;

    /// <summary>set if surface cache is thrashing</summary>
    public bool r_cache_thrash;

    public int sc_size;
    public SurfCache? sc_rover, sc_base;

    /// <summary>the cache's memory (vid_win.c allocates it; here D_InitCaches does)</summary>
    public byte[] surfcache = [];

    // C's comparisons of surfcache_t pointers: a block's offset, NULL before every block
    private static int SCOffset(SurfCache? c) => c?.offset ?? -1;

    // d_surf.c: D_SurfaceCacheForRes
    public int D_SurfaceCacheForRes(int width, int height)
    {
        int size, pix;

        if (COM_CheckParm("-surfcachesize") != 0)
        {
            size = QString.Q_atoi(com_argv.Argv(COM_CheckParm("-surfcachesize") + 1)) * 1024;
            return size;
        }

        size = SURFCACHE_SIZE_AT_320X200;

        pix = width * height;
        if (pix > 64000)
        {
            size += (pix - 64000) * 3;
        }

        return size;
    }

    // d_surf.c: D_CheckCacheGuard
    public void D_CheckCacheGuard()
    {
        int s;
        int i;

        s = sc_base!.offset + sc_size;
        for (i = 0; i < GUARDSIZE; i++)
        {
            if (surfcache[s + i] != (byte)i)
            {
                throw Sys_Error("D_CheckCacheGuard: failed");
            }
        }
    }

    // d_surf.c: D_ClearCacheGuard
    public void D_ClearCacheGuard()
    {
        int s;
        int i;

        s = sc_base!.offset + sc_size;
        for (i = 0; i < GUARDSIZE; i++)
        {
            surfcache[s + i] = (byte)i;
        }
    }

    // d_surf.c: D_InitCaches (and vid_win.c's allocation of the cache's memory)
    public void D_InitCaches(int size)
    {
        if (!msg_suppress_1)
        {
            Con_Printf("%ik surface cache\n", size / 1024);
        }

        surfcache = new byte[size];

        sc_size = size - GUARDSIZE;
        sc_base = new SurfCache { offset = 0 };
        sc_rover = sc_base;

        sc_base.next = null;
        sc_base.owner = null;
        sc_base.size = sc_size;

        D_ClearCacheGuard();
    }

    // d_surf.c: D_FlushCaches
    public void D_FlushCaches()
    {
        SurfCache? c;

        if (sc_base == null)
        {
            return;
        }

        for (c = sc_base; c != null; c = c.next)
        {
            if (c.owner != null)
            {
                c.owner.cachespots[c.ownermip] = null;
            }
        }

        sc_rover = sc_base;
        sc_base.next = null;
        sc_base.owner = null;
        sc_base.size = sc_size;
    }

    // d_surf.c: D_SCAlloc
    public SurfCache D_SCAlloc(int width, int size)
    {
        SurfCache @new;
        bool wrapped_this_time;

        if ((width < 0) || (width > 256))
        {
            throw Sys_Error("D_SCAlloc: bad cache width %d\n", width);
        }

        if ((size <= 0) || (size > 0x10000))
        {
            throw Sys_Error("D_SCAlloc: bad cache size %d\n", size);
        }

        size = SurfCache.HeaderSize + size;
        size = (size + 3) & ~3;
        if (size > sc_size)
        {
            throw Sys_Error("D_SCAlloc: %i > cache size", size);
        }

        // if there is not size bytes after the rover, reset to the start
        wrapped_this_time = false;

        if (sc_rover == null || sc_rover.offset - sc_base!.offset > sc_size - size)
        {
            if (sc_rover != null)
            {
                wrapped_this_time = true;
            }

            sc_rover = sc_base;
        }

        // colect and free surfcache_t blocks until the rover block is large enough
        @new = sc_rover!;
        if (sc_rover!.owner != null)
        {
            sc_rover.owner.cachespots[sc_rover.ownermip] = null;
        }

        while (@new.size < size)
        {
            // free another
            sc_rover = sc_rover!.next ?? throw Sys_Error("D_SCAlloc: hit the end of memory");
            if (sc_rover.owner != null)
            {
                sc_rover.owner.cachespots[sc_rover.ownermip] = null;
            }

            @new.size += sc_rover.size;
            @new.next = sc_rover.next;
        }

        // create a fragment out of any leftovers
        if (@new.size - size > 256)
        {
            sc_rover = new SurfCache
            {
                offset = @new.offset + size,
                size = @new.size - size,
                next = @new.next,
                width = 0,
                owner = null,
            };
            @new.next = sc_rover;
            @new.size = size;
        }
        else
        {
            sc_rover = @new.next;
        }

        @new.width = (uint)width;

        // DEBUG
        if (width > 0)
        {
            @new.height = (uint)(size - SurfCache.Size + 4) / (uint)width;
        }

        @new.owner = null; // should be set properly after return

        if (d_roverwrapped)
        {
            if (wrapped_this_time || (SCOffset(sc_rover) >= SCOffset(d_initial_rover)))
            {
                r_cache_thrash = true;
            }
        }
        else if (wrapped_this_time)
        {
            d_roverwrapped = true;
        }

        D_CheckCacheGuard(); // DEBUG
        return @new;
    }

    // =============================================================================

    // d_surf.c: MaskForNum
    // if the num is not a power of 2, assume it will not repeat
    public static int MaskForNum(int num) => num switch
    {
        128 => 127,
        64 => 63,
        32 => 31,
        16 => 15,
        _ => 255,
    };

    // d_surf.c: D_log2
    public static int D_log2(int num)
    {
        int c;

        c = 0;

        while ((num >>= 1) != 0)
        {
            c++;
        }

        return c;
    }

    // =============================================================================

    // d_surf.c: D_CacheSurface
    public SurfCache D_CacheSurface(MSurface surface, int miplevel)
    {
        SurfCache? cache;

        // if the surface is animating or flashing, flush the cache
        r_drawsurf.texture = R_TextureAnimation(surface.texinfo.texture);
        r_drawsurf.lightadj[0] = d_lightstylevalue[surface.styles[0]];
        r_drawsurf.lightadj[1] = d_lightstylevalue[surface.styles[1]];
        r_drawsurf.lightadj[2] = d_lightstylevalue[surface.styles[2]];
        r_drawsurf.lightadj[3] = d_lightstylevalue[surface.styles[3]];

        // see if the cache holds apropriate data
        cache = surface.cachespots[miplevel];

        if (cache != null && cache.dlight == 0 && surface.dlightframe != r_framecount
            && cache.texture == r_drawsurf.texture
            && cache.lightadj[0] == r_drawsurf.lightadj[0]
            && cache.lightadj[1] == r_drawsurf.lightadj[1]
            && cache.lightadj[2] == r_drawsurf.lightadj[2]
            && cache.lightadj[3] == r_drawsurf.lightadj[3])
        {
            return cache;
        }

        // determine shape of surface
        surfscale = (float)(1.0 / (1 << miplevel));
        r_drawsurf.surfmip = miplevel;
        r_drawsurf.surfwidth = surface.extents[0] >> miplevel;
        r_drawsurf.rowbytes = r_drawsurf.surfwidth;
        r_drawsurf.surfheight = surface.extents[1] >> miplevel;

        // allocate memory if needed
        if (cache == null)
        {
            // if a texture just animated, don't reallocate it
            cache = D_SCAlloc(r_drawsurf.surfwidth,
                r_drawsurf.surfwidth * r_drawsurf.surfheight);
            surface.cachespots[miplevel] = cache;
            cache.owner = surface;
            cache.ownermip = miplevel;
            cache.mipscale = surfscale;
        }

        if (surface.dlightframe == r_framecount)
        {
            cache.dlight = 1;
        }
        else
        {
            cache.dlight = 0;
        }

        r_drawsurf.surfdat = cache.DataOffset;

        cache.texture = r_drawsurf.texture;
        cache.lightadj[0] = r_drawsurf.lightadj[0];
        cache.lightadj[1] = r_drawsurf.lightadj[1];
        cache.lightadj[2] = r_drawsurf.lightadj[2];
        cache.lightadj[3] = r_drawsurf.lightadj[3];

        // draw and light the surface texture
        r_drawsurf.surf = surface;

        c_surf++;
        R_DrawSurface();

        return surface.cachespots[miplevel]!;
    }
}
