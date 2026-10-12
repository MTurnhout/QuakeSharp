using QuakeSharp.Core.Render;
using static QuakeSharp.Core.Common.MathLib;
using static QuakeSharp.Core.Model.BspFile;

namespace QuakeSharp.Core;

// r_misc.c: the frame's setup and the renderer's commands (its drawing comes with the renderer, milestone 6)
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
            R_PrintSurfaceCounts();
        }

        if (r_numedges.value != 0)
        {
            R_PrintEdgeCounts();
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
}
