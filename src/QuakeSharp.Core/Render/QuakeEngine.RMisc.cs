using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Render;
using static QuakeSharp.Core.Common.MathLib;
using static QuakeSharp.Core.Model.BspFile;

namespace QuakeSharp.Core;

// r_misc.c: the frame's setup, the view's transforms and the renderer's statistics
public sealed partial class QuakeEngine
{
    // r_misc.c: R_CheckVariables's static oldbright
    private float r_oldbright;

    // r_misc.c: R_CheckVariables
    public void R_CheckVariables()
    {
        if (r_fullbright.value != r_oldbright)
        {
            r_oldbright = r_fullbright.value;
            D_FlushCaches(); // so all lighting changes
        }
    }

    // r_misc.c: R_TimeRefresh_f
    public void R_TimeRefresh_f()
    {
        int i;
        float start, stop, time;
        int startangle;

        startangle = (int)r_refdef.viewangles[1];

        start = (float)Sys_FloatTime();
        for (i = 0; i < 128; i++)
        {
            r_refdef.viewangles[1] = (float)(i / 128.0 * 360.0);

            VID_LockBuffer();

            R_RenderView();

            VID_UnlockBuffer();

            VID_Update(new VRect { x = r_refdef.vrect.x, y = r_refdef.vrect.y, width = r_refdef.vrect.width, height = r_refdef.vrect.height });
        }

        stop = (float)Sys_FloatTime();
        time = stop - start;
        Con_Printf("%f seconds (%f fps)\n", time, 128 / time);

        r_refdef.viewangles[1] = startangle;
    }

    // r_misc.c: R_SetupFrame
    public void R_SetupFrame()
    {
        int edgecount;
        VRect vrect;
        float w, h;

        // don't allow cheats in multiplayer
        if (cl.maxclients > 1)
        {
            Cvar_Set("r_draworder", "0");
            Cvar_Set("r_fullbright", "0");
            Cvar_Set("r_ambient", "0");
            Cvar_Set("r_drawflat", "0");
        }

        if (r_numsurfs.value != 0)
        {
            // (C's pointer differences from surfaces and r_edges: the indices)
            if (surface_p > r_maxsurfsseen)
            {
                r_maxsurfsseen = surface_p;
            }

            Con_Printf("Used %d of %d surfs; %d max\n", surface_p, surf_max, r_maxsurfsseen);
        }

        if (r_numedges.value != 0)
        {
            edgecount = edge_p;

            if (edgecount > r_maxedgesseen)
            {
                r_maxedgesseen = edgecount;
            }

            Con_Printf("Used %d of %d edges; %d max\n", edgecount, r_numallocatededges, r_maxedgesseen);
        }

        r_refdef.ambientlight = (int)r_ambient.value;

        if (r_refdef.ambientlight < 0)
        {
            r_refdef.ambientlight = 0;
        }

        if (!sv.active)
        {
            r_draworder.value = 0; // don't let cheaters look behind walls
        }

        R_CheckVariables();

        R_AnimateLight();

        r_framecount++;

        numbtofpolys = 0;

        // build the transformation matrix for the given view angles
        VectorCopy(r_refdef.vieworg, ref modelorg);
        VectorCopy(r_refdef.vieworg, ref r_origin);

        AngleVectors(r_refdef.viewangles, ref vpn, ref vright, ref vup);

        // current viewleaf
        r_oldviewleaf = r_viewleaf;
        r_viewleaf = Mod_PointInLeaf(r_origin, cl.worldmodel);

        r_dowarpold = r_dowarp;
        r_dowarp = r_waterwarp.value != 0 && r_viewleaf.contents <= CONTENTS_WATER;

        if (r_dowarp != r_dowarpold || r_viewchanged || lcd_x.value != 0)
        {
            if (r_dowarp)
            {
                if (vid.width <= vid.maxwarpwidth &&
                    vid.height <= vid.maxwarpheight)
                {
                    vrect.x = 0;
                    vrect.y = 0;
                    vrect.width = (int)vid.width;
                    vrect.height = (int)vid.height;

                    R_ViewChanged(vrect, sb_lines, vid.aspect);
                }
                else
                {
                    w = vid.width;
                    h = vid.height;

                    if (w > vid.maxwarpwidth)
                    {
                        h *= vid.maxwarpwidth / w;
                        w = vid.maxwarpwidth;
                    }

                    if (h > vid.maxwarpheight)
                    {
                        h = vid.maxwarpheight;
                        w *= vid.maxwarpheight / h;
                    }

                    vrect.x = 0;
                    vrect.y = 0;
                    vrect.width = (int)w;
                    vrect.height = (int)h;

                    R_ViewChanged(vrect,
                        (int)(sb_lines * (h / vid.height)),
                        vid.aspect * (h / w) * ((float)vid.width / vid.height));
                }
            }
            else
            {
                vrect.x = 0;
                vrect.y = 0;
                vrect.width = (int)vid.width;
                vrect.height = (int)vid.height;

                R_ViewChanged(vrect, sb_lines, vid.aspect);
            }

            r_viewchanged = false;
        }

        // start off with just the four screen edge clip planes
        R_TransformFrustum();

        // save base values
        VectorCopy(vpn, ref base_vpn);
        VectorCopy(vright, ref base_vright);
        VectorCopy(vup, ref base_vup);
        VectorCopy(modelorg, ref base_modelorg);

        R_SetSkyFrame();

        R_SetUpFrustumIndexes();

        r_cache_thrash = false;

        // clear frame counts
        c_faceclip = 0;
        d_spanpixcount = 0;
        r_polycount = 0;
        r_drawnpolycount = 0;
        r_wholepolycount = 0;
        r_amodels_drawn = 0;
        r_outofsurfaces = 0;
        r_outofedges = 0;

        D_SetupFrame();
    }

    // r_misc.c: R_LineGraph
    // Only called by R_DisplayTime
    public void R_LineGraph(int x, int y, int h)
    {
        int i;
        int dest;
        int s;

        // FIXME: should be disabled on no-buffer adapters, or should be in the driver
        x += r_refdef.vrect.x;
        y += r_refdef.vrect.y;

        dest = vid.bufferoffset + (int)(vid.rowbytes * y) + x;

        s = (int)r_graphheight.value;

        if (h > s)
        {
            h = s;
        }

        for (i = 0; i < h; i++, dest -= (int)vid.rowbytes * 2)
        {
            vid.buffer[dest] = 0xff;
            vid.buffer[dest - (int)vid.rowbytes] = 0x30;
        }

        for (; i < s; i++, dest -= (int)vid.rowbytes * 2)
        {
            vid.buffer[dest] = 0x30;
            vid.buffer[dest - (int)vid.rowbytes] = 0x30;
        }
    }

    // r_misc.c: R_TimeGraph's statics
    private const int MAX_TIMINGS = 100;
    private int r_timegraph_timex;
    private readonly byte[] r_timings = new byte[MAX_TIMINGS];

    // r_misc.c: R_TimeGraph
    // Performance monitoring tool
    public void R_TimeGraph()
    {
        int a;
        float r_time2;
        int x;

        r_time2 = (float)Sys_FloatTime();

        a = X86.FtoI((r_time2 - r_time1) / 0.01);
        r_timings[r_timegraph_timex] = (byte)a;
        a = r_timegraph_timex;

        if (r_refdef.vrect.width <= MAX_TIMINGS)
        {
            x = r_refdef.vrect.width - 1;
        }
        else
        {
            x = r_refdef.vrect.width -
                ((r_refdef.vrect.width - MAX_TIMINGS) / 2);
        }

        do
        {
            R_LineGraph(x, r_refdef.vrect.height - 2, r_timings[a]);
            if (x == 0)
            {
                break; // screen too small to hold entire thing
            }

            x--;
            a--;
            if (a == -1)
            {
                a = MAX_TIMINGS - 1;
            }
        }
        while (a != r_timegraph_timex);

        r_timegraph_timex = (r_timegraph_timex + 1) % MAX_TIMINGS;
    }

    // r_misc.c: R_PrintTimes
    public void R_PrintTimes()
    {
        float r_time2;
        float ms;

        r_time2 = (float)Sys_FloatTime();

        ms = 1000 * (r_time2 - r_time1);

        Con_Printf("%5.1f ms %3i/%3i/%3i poly %3i surf\n",
            ms, c_faceclip, r_polycount, r_drawnpolycount, c_surf);
        c_surf = 0;
    }

    // r_misc.c: R_PrintDSpeeds
    public void R_PrintDSpeeds()
    {
        float ms, dp_time, r_time2, rw_time, db_time, se_time, de_time, dv_time;

        r_time2 = (float)Sys_FloatTime();

        dp_time = (dp_time2 - dp_time1) * 1000;
        rw_time = (rw_time2 - rw_time1) * 1000;
        db_time = (db_time2 - db_time1) * 1000;
        se_time = (se_time2 - se_time1) * 1000;
        de_time = (de_time2 - de_time1) * 1000;
        dv_time = (dv_time2 - dv_time1) * 1000;
        ms = (r_time2 - r_time1) * 1000;

        Con_Printf("%3i %4.1fp %3iw %4.1fb %3is %4.1fe %4.1fv\n",
            X86.FtoI(ms), dp_time, X86.FtoI(rw_time), db_time, X86.FtoI(se_time), de_time,
            dv_time);
    }

    // r_misc.c: R_PrintAliasStats
    public void R_PrintAliasStats() => Con_Printf("%3i polygon model drawn\n", r_amodels_drawn);

    // r_misc.c: R_TransformFrustum
    public void R_TransformFrustum()
    {
        int i;
        Vec3 v = default, v2 = default;

        for (i = 0; i < 4; i++)
        {
            v[0] = screenedge[i].normal[2];
            v[1] = -screenedge[i].normal[0];
            v[2] = screenedge[i].normal[1];

            v2[0] = (v[1] * vright[0]) + (v[2] * vup[0]) + (v[0] * vpn[0]);
            v2[1] = (v[1] * vright[1]) + (v[2] * vup[1]) + (v[0] * vpn[1]);
            v2[2] = (v[1] * vright[2]) + (v[2] * vup[2]) + (v[0] * vpn[2]);

            VectorCopy(v2, ref view_clipplanes[i].normal);

            view_clipplanes[i].dist = DotProduct(modelorg, v2);
        }
    }

    // r_misc.c: TransformVector
    public void TransformVector(in Vec3 @in, ref Vec3 @out)
    {
        @out[0] = DotProduct(@in, vright);
        @out[1] = DotProduct(@in, vup);
        @out[2] = DotProduct(@in, vpn);
    }

    // r_misc.c: R_TransformPlane
    public void R_TransformPlane(MPlane p, ref Vec3 normal, out float dist)
    {
        float d;

        d = DotProduct(r_origin, p.normal);
        dist = p.dist - d;

        // TODO: when we have rotating entities, this will need to use the view matrix
        TransformVector(p.normal, ref normal);
    }

    // r_misc.c: R_SetUpFrustumIndexes
    public void R_SetUpFrustumIndexes()
    {
        int i, j, pindex;

        pindex = 0;

        for (i = 0; i < 4; i++)
        {
            for (j = 0; j < 3; j++)
            {
                if (view_clipplanes[i].normal[j] < 0)
                {
                    r_frustum_indexes[pindex + j] = j;
                    r_frustum_indexes[pindex + j + 3] = j + 3;
                }
                else
                {
                    r_frustum_indexes[pindex + j] = j + 3;
                    r_frustum_indexes[pindex + j + 3] = j;
                }
            }

            // FIXME: do just once at start
            pfrustum_indexes[i] = pindex;
            pindex += 6;
        }
    }
}
