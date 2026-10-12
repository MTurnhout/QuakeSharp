using QuakeSharp.Core.Common;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Render;

namespace QuakeSharp.Core;

// d_init.c: rasterization driver initialization; d_modech.c: called when mode has just changed; d_vars.c: the
// driver's global variables. The view buffer, the texture being drawn (cacheblock) and the z-buffer are arrays with
// offsets where C has pointers.
public sealed partial class QuakeEngine
{
    private const int NUM_MIPS = 4;

    public readonly Cvar d_subdiv16 = new("d_subdiv16", "1");
    public readonly Cvar d_mipcap = new("d_mipcap", "0");
    public readonly Cvar d_mipscale = new("d_mipscale", "1");

    public SurfCache? d_initial_rover;
    public bool d_roverwrapped;
    public int d_minmip;
    public readonly float[] d_scalemip = new float[NUM_MIPS - 1];

    private static readonly float[] basemip = [1.0f, (float)(0.5 * 0.8), (float)(0.25 * 0.8)];

    // d_vars.c
    public float d_sdivzstepu, d_tdivzstepu, d_zistepu;
    public float d_sdivzstepv, d_tdivzstepv, d_zistepv;
    public float d_sdivzorigin, d_tdivzorigin, d_ziorigin;

    public int sadjust, tadjust, bbextents, bbextentt;

    // d_vars.c: cacheblock (an array and an offset in it), cachewidth
    public byte[] cacheblock = [];
    public int cacheblockoffset;
    public int cachewidth;

    // d_vars.c: d_viewbuffer (vid.buffer or r_warpbuffer, and an offset in it)
    public byte[] d_viewbuffer = [];
    public int d_viewbufferoffset;

    public short[]? d_pzbuffer;
    public uint d_zrowbytes;
    public uint d_zwidth;

    // d_modech.c
    public int d_vrectx, d_vrecty, d_vrectright_particle, d_vrectbottom_particle;

    public int d_y_aspect_shift, d_pix_min, d_pix_max, d_pix_shift;

    public readonly int[] d_scantable = new int[MAXHEIGHT];

    /// <summary>d_modech.c: zspantable, offsets in d_pzbuffer</summary>
    public readonly int[] zspantable = new int[MAXHEIGHT];

    /// <summary>r_sky.c: r_skydirect (not used?)</summary>
    public int r_skydirect;

    // d_init.c: D_Init
    public void D_Init()
    {
        r_skydirect = 1;

        Cvar_RegisterVariable(d_subdiv16);
        Cvar_RegisterVariable(d_mipcap);
        Cvar_RegisterVariable(d_mipscale);

        r_drawpolys = false;
        r_worldpolysbacktofront = false;
        r_recursiveaffinetriangles = true;
        r_aliasuvscale = 1.0f;
    }

    // d_init.c: D_EnableBackBufferAccess
    public void D_EnableBackBufferAccess() => VID_LockBuffer();

    // d_init.c: D_TurnZOn
    public static void D_TurnZOn()
    {
        // not needed for software version
    }

    // d_init.c: D_DisableBackBufferAccess
    public void D_DisableBackBufferAccess() => VID_UnlockBuffer();

    // d_init.c: D_SetupFrame
    public void D_SetupFrame()
    {
        int i;

        if (r_dowarp)
        {
            d_viewbuffer = r_warpbuffer;
            d_viewbufferoffset = 0;
        }
        else
        {
            d_viewbuffer = vid.buffer;
            d_viewbufferoffset = vid.bufferoffset;
        }

        if (r_dowarp)
        {
            screenwidth = WARP_WIDTH;
        }
        else
        {
            screenwidth = (int)vid.rowbytes;
        }

        d_roverwrapped = false;
        d_initial_rover = sc_rover;

        d_minmip = X86.FtoI(d_mipcap.value);
        if (d_minmip > 3)
        {
            d_minmip = 3;
        }
        else if (d_minmip < 0)
        {
            d_minmip = 0;
        }

        for (i = 0; i < (NUM_MIPS - 1); i++)
        {
            d_scalemip[i] = basemip[i] * d_mipscale.value;
        }

        // (d_drawspans is D_DrawSpans8: d_subdiv16 chooses the assembly's D_DrawSpans16)
        d_aflatcolor = 0;
    }

    // d_modech.c: D_ViewChanged
    public void D_ViewChanged()
    {
        int rowbytes;

        if (r_dowarp)
        {
            rowbytes = WARP_WIDTH;
        }
        else
        {
            rowbytes = (int)vid.rowbytes;
        }

        scale_for_mip = xscale;
        if (yscale > xscale)
        {
            scale_for_mip = yscale;
        }

        d_zrowbytes = vid.width * 2;
        d_zwidth = vid.width;

        d_pix_min = r_refdef.vrect.width / 320;
        if (d_pix_min < 1)
        {
            d_pix_min = 1;
        }

        d_pix_max = X86.FtoI(((float)r_refdef.vrect.width / (320.0 / 4.0)) + 0.5);
        d_pix_shift = 8 - X86.FtoI(((float)r_refdef.vrect.width / 320.0) + 0.5);
        if (d_pix_max < 1)
        {
            d_pix_max = 1;
        }

        if (pixelAspect > 1.4)
        {
            d_y_aspect_shift = 1;
        }
        else
        {
            d_y_aspect_shift = 0;
        }

        d_vrectx = r_refdef.vrect.x;
        d_vrecty = r_refdef.vrect.y;
        d_vrectright_particle = r_refdef.vrectright - d_pix_max;
        d_vrectbottom_particle =
            r_refdef.vrectbottom - (d_pix_max << d_y_aspect_shift);

        {
            int i;

            for (i = 0; i < vid.height; i++)
            {
                d_scantable[i] = i * rowbytes;
                zspantable[i] = (int)(i * d_zwidth);
            }
        }

        // (D_Patch: the assembly's)
    }

    // d_zpoint.c: D_DrawZPoint
    public void D_DrawZPoint()
    {
        int pdest;
        int pz;
        int izi;

        pz = (int)(d_zwidth * r_zpointdesc.v) + r_zpointdesc.u;
        pdest = d_viewbufferoffset + d_scantable[r_zpointdesc.v] + r_zpointdesc.u;
        izi = X86.FtoI(r_zpointdesc.zi * 0x8000);

        if (d_pzbuffer![pz] <= izi)
        {
            d_pzbuffer[pz] = (short)izi;
            d_viewbuffer[pdest] = (byte)r_zpointdesc.color;
        }
    }

    // d_fill.c: D_FillRect
    public void D_FillRect(in VRect rect, int color)
    {
        int rx, ry, rwidth, rheight;
        int dest;

        rx = rect.x;
        ry = rect.y;
        rwidth = rect.width;
        rheight = rect.height;

        if (rx < 0)
        {
            rwidth += rx;
            rx = 0;
        }

        if (ry < 0)
        {
            rheight += ry;
            ry = 0;
        }

        if (rx + rwidth > vid.width)
        {
            rwidth = (int)vid.width - rx;
        }

        if (ry + rheight > vid.height)
        {
            rheight = (int)vid.height - rx;
        }

        if (rwidth < 1 || rheight < 1)
        {
            return;
        }

        dest = vid.bufferoffset + (ry * (int)vid.rowbytes) + rx;

        // (the C clears four bytes at a time where it can: the same bytes)
        for (ry = 0; ry < rheight; ry++)
        {
            for (rx = 0; rx < rwidth; rx++)
            {
                vid.buffer[dest + rx] = (byte)color;
            }

            dest += (int)vid.rowbytes;
        }
    }
}
