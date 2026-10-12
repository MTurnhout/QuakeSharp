using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Render;
using static QuakeSharp.Core.Common.MathLib;
using static QuakeSharp.Core.Model.BspFile;
using ModelT = QuakeSharp.Core.Model.Model;

namespace QuakeSharp.Core;

// r_main.c: the renderer's setup, the view's geometry, the entities and the frame's order of work
public sealed partial class QuakeEngine
{
    // r_local.h: XCENTERING, YCENTERING
    private const double XCENTERING = 1.0 / 2.0;
    private const double YCENTERING = 1.0 / 2.0;

    // d_iface.h: WARP_WIDTH, WARP_HEIGHT
    public const int WARP_WIDTH = 320;
    public const int WARP_HEIGHT = 200;

    // r_shared.h: NUMSTACKEDGES, MINEDGES, NUMSTACKSURFACES, MINSURFACES
    private const int NUMSTACKEDGES = 2400;
    private const int MINEDGES = NUMSTACKEDGES;
    private const int NUMSTACKSURFACES = 800;
    private const int MINSURFACES = NUMSTACKSURFACES;

    // r_shared.h: MAXWIDTH, MAXHEIGHT
    public const int MAXWIDTH = 1280;
    public const int MAXHEIGHT = 1024;

    // r_shared.h: MAXDIMENSION, SIN_BUFFER_SIZE, MAXSPANS
    private const int MAXDIMENSION = MAXHEIGHT > MAXWIDTH ? MAXHEIGHT : MAXWIDTH;
    private const int SIN_BUFFER_SIZE = MAXDIMENSION + CYCLE;
    private const int MAXSPANS = 3000;

    // r_shared.h: the flags in finalvert_t.flags
    public const int ALIAS_LEFT_CLIP = 0x0001;
    public const int ALIAS_TOP_CLIP = 0x0002;
    public const int ALIAS_RIGHT_CLIP = 0x0004;
    public const int ALIAS_BOTTOM_CLIP = 0x0008;
    public const int ALIAS_Z_CLIP = 0x0010;
    public const int ALIAS_ONSEAM = 0x0020;
    public const int ALIAS_XY_CLIP_MASK = 0x000F;

    // r_local.h: BMODEL_FULLY_CLIPPED, CLIP_EPSILON, BACKFACE_EPSILON, NEAR_CLIP, ALIAS_Z_CLIP_PLANE,
    // AMP, AMP2, SPEED, MAX_BTOFPOLYS
    private const int BMODEL_FULLY_CLIPPED = 0x10;
    private const double CLIP_EPSILON = 0.001;
    private const double BACKFACE_EPSILON = 0.01;
    private const double NEAR_CLIP = 0.01;
    private const int ALIAS_Z_CLIP_PLANE = 5;
    private const int AMP = 8 * 0x10000;
    private const int AMP2 = 3;
    private const int SPEED = 20;
    private const int MAX_BTOFPOLYS = 5000;

    // d_iface.h: TURB_TEX_SIZE, CYCLE, TILE_SIZE, SKYSHIFT, SKYSIZE, SKYMASK
    private const int TURB_TEX_SIZE = 64;
    private const int CYCLE = 128;
    private const int TILE_SIZE = 128;
    private const int SKYSHIFT = 7;
    private const int SKYSIZE = 1 << SKYSHIFT;
    private const int SKYMASK = SKYSIZE - 1;

    public Vec3 viewlightvec;
    public readonly ALight r_viewlighting = new() { ambientlight = 128, shadelight = 192 };

    public float r_time1;
    public int r_numallocatededges;
    public bool r_drawpolys;
    public bool r_drawculledpolys;
    public bool r_worldpolysbacktofront;
    public bool r_recursiveaffinetriangles = true;
    public float r_aliasuvscale = 1.0f;
    public int r_outofsurfaces;
    public int r_outofedges;

    public bool r_dowarp, r_dowarpold, r_viewchanged;

    public int numbtofpolys;
    public BtofPoly[] pbtofpolys = [];
    public Vec3[] r_pcurrentvertbase = [];
    public int c_surf;
    public int r_maxsurfsseen, r_maxedgesseen, r_cnumsurfs;
    public bool r_surfsonstack;
    public int r_clipflags;
    public bool r_fov_greater_than_90;

    /// <summary>r_main.c: r_warpbuffer (R_RenderView_'s warpbuffer, on the C's stack)</summary>
    public readonly byte[] r_warpbuffer = new byte[WARP_WIDTH * WARP_HEIGHT];

    // view origin
    public Vec3 vup, base_vup;
    public Vec3 vpn, base_vpn;
    public Vec3 vright, base_vright;
    public Vec3 r_origin;

    // screen size info
    public readonly RefDef r_refdef = new();
    public float xcenter, ycenter;
    public float xscale, yscale;
    public float xscaleinv, yscaleinv;
    public float xscaleshrink, yscaleshrink;
    public float aliasxscale, aliasyscale, aliasxcenter, aliasycenter;

    public int screenwidth;

    public float pixelAspect;
    public float screenAspect;
    public float verticalFieldOfView;
    public float xOrigin, yOrigin;

    public readonly MPlane[] screenedge = [new(), new(), new(), new()];

    /// <summary>r_main.c: pfrustum_indexes, offsets in r_frustum_indexes</summary>
    public readonly int[] pfrustum_indexes = new int[4];

    public readonly int[] r_frustum_indexes = new int[4 * 6];

    // refresh flags

    /// <summary>so frame counts initialized to 0 don't match</summary>
    public int r_framecount = 1;

    public int r_visframecount;
    public int d_spanpixcount;
    public int r_polycount;
    public int r_drawnpolycount;
    public int r_wholepolycount;

    /// <summary>if 1, surface cache is currently empty and must be reinitialized for current cache size</summary>
    public int reinit_surfcache = 1;

    public MLeaf? r_viewleaf, r_oldviewleaf;

    public Texture r_notexture_mip = null!;

    public float r_aliastransition, r_resfudge;

    /// <summary>8.8 fraction of base light value</summary>
    public readonly int[] d_lightstylevalue = new int[256];

    public float dp_time1, dp_time2, db_time1, db_time2, rw_time1, rw_time2;
    public float se_time1, se_time2, de_time1, de_time2, dv_time1, dv_time2;

    public readonly Cvar r_draworder = new("r_draworder", "0");
    public readonly Cvar r_speeds = new("r_speeds", "0");
    public readonly Cvar r_timegraph = new("r_timegraph", "0");
    public readonly Cvar r_graphheight = new("r_graphheight", "10");
    public readonly Cvar r_clearcolor = new("r_clearcolor", "2");
    public readonly Cvar r_waterwarp = new("r_waterwarp", "1");
    public readonly Cvar r_fullbright = new("r_fullbright", "0");
    public readonly Cvar r_drawentities = new("r_drawentities", "1");
    public readonly Cvar r_drawviewmodel = new("r_drawviewmodel", "1");
    public readonly Cvar r_aliasstats = new("r_polymodelstats", "0");
    public readonly Cvar r_dspeeds = new("r_dspeeds", "0");
    public readonly Cvar r_drawflat = new("r_drawflat", "0");
    public readonly Cvar r_ambient = new("r_ambient", "0");
    public readonly Cvar r_reportsurfout = new("r_reportsurfout", "0");
    public readonly Cvar r_maxsurfs = new("r_maxsurfs", "0");
    public readonly Cvar r_numsurfs = new("r_numsurfs", "0");
    public readonly Cvar r_reportedgeout = new("r_reportedgeout", "0");
    public readonly Cvar r_maxedges = new("r_maxedges", "0");
    public readonly Cvar r_numedges = new("r_numedges", "0");
    public readonly Cvar r_aliastransbase = new("r_aliastransbase", "200");
    public readonly Cvar r_aliastransadj = new("r_aliastransadj", "100");

    // r_main.c: R_InitTextures
    public void R_InitTextures()
    {
        int x, y, m;
        int dest;

        // create a simple checkerboard texture for the default
        r_notexture_mip = new Texture { pixels = new byte[(16 * 16) + (8 * 8) + (4 * 4) + (2 * 2)] };

        r_notexture_mip.width = r_notexture_mip.height = 16;
        r_notexture_mip.offsets[0] = 0;
        r_notexture_mip.offsets[1] = r_notexture_mip.offsets[0] + (16 * 16);
        r_notexture_mip.offsets[2] = r_notexture_mip.offsets[1] + (8 * 8);
        r_notexture_mip.offsets[3] = r_notexture_mip.offsets[2] + (4 * 4);

        for (m = 0; m < 4; m++)
        {
            dest = (int)r_notexture_mip.offsets[m];
            for (y = 0; y < (16 >> m); y++)
            {
                for (x = 0; x < (16 >> m); x++)
                {
                    if ((y < (8 >> m)) ^ (x < (8 >> m)))
                    {
                        r_notexture_mip.pixels[dest++] = 0;
                    }
                    else
                    {
                        r_notexture_mip.pixels[dest++] = 0xff;
                    }
                }
            }
        }
    }

    // r_main.c: R_Init
    public void R_Init()
    {
        R_InitTurb();

        Cmd_AddCommand("timerefresh", R_TimeRefresh_f);
        Cmd_AddCommand("pointfile", R_ReadPointFile_f);

        Cvar_RegisterVariable(r_draworder);
        Cvar_RegisterVariable(r_speeds);
        Cvar_RegisterVariable(r_timegraph);
        Cvar_RegisterVariable(r_graphheight);
        Cvar_RegisterVariable(r_drawflat);
        Cvar_RegisterVariable(r_ambient);
        Cvar_RegisterVariable(r_clearcolor);
        Cvar_RegisterVariable(r_waterwarp);
        Cvar_RegisterVariable(r_fullbright);
        Cvar_RegisterVariable(r_drawentities);
        Cvar_RegisterVariable(r_drawviewmodel);
        Cvar_RegisterVariable(r_aliasstats);
        Cvar_RegisterVariable(r_dspeeds);
        Cvar_RegisterVariable(r_reportsurfout);
        Cvar_RegisterVariable(r_maxsurfs);
        Cvar_RegisterVariable(r_numsurfs);
        Cvar_RegisterVariable(r_reportedgeout);
        Cvar_RegisterVariable(r_maxedges);
        Cvar_RegisterVariable(r_numedges);
        Cvar_RegisterVariable(r_aliastransbase);
        Cvar_RegisterVariable(r_aliastransadj);

        Cvar_SetValue("r_maxedges", NUMSTACKEDGES);
        Cvar_SetValue("r_maxsurfs", NUMSTACKSURFACES);

        view_clipplanes[0].leftedge = true;
        view_clipplanes[1].rightedge = true;
        view_clipplanes[1].leftedge = view_clipplanes[2].leftedge =
            view_clipplanes[3].leftedge = false;
        view_clipplanes[0].rightedge = view_clipplanes[2].rightedge =
            view_clipplanes[3].rightedge = false;

        r_refdef.xOrigin = (float)XCENTERING;
        r_refdef.yOrigin = (float)YCENTERING;

        R_InitParticles();

        D_Init();
    }

    // r_main.c: R_NewMap
    public void R_NewMap()
    {
        int i;

        // clear out efrags in case the level hasn't been reloaded
        // FIXME: is this one short?
        for (i = 0; i < cl.worldmodel!.numleafs; i++)
        {
            cl.worldmodel.leafs[i].efrags = null;
        }

        r_viewleaf = null;
        R_ClearParticles();

        r_cnumsurfs = X86.FtoI(r_maxsurfs.value);

        if (r_cnumsurfs <= MINSURFACES)
        {
            r_cnumsurfs = MINSURFACES;
        }

        // the surfaces: on the C's stack (R_EdgeDrawing) up to NUMSTACKSURFACES, else on the hunk; surface 0 doesn't
        // really exist; it's just a dummy because index 0 is used to indicate no edge attached to surface
        r_surfsonstack = r_cnumsurfs <= NUMSTACKSURFACES;
        surfaces = new Surf[r_cnumsurfs + 1];
        for (i = 0; i < surfaces.Length; i++)
        {
            surfaces[i] = new Surf();
        }

        surface_p = 1;
        surf_max = r_cnumsurfs + 1;

        r_maxedgesseen = 0;
        r_maxsurfsseen = 0;

        r_numallocatededges = X86.FtoI(r_maxedges.value);

        if (r_numallocatededges < MINEDGES)
        {
            r_numallocatededges = MINEDGES;
        }

        // the edges: on the C's stack (R_EdgeDrawing) up to NUMSTACKEDGES, else on the hunk (auxedges)
        r_edges = new Edge[r_numallocatededges];
        for (i = 0; i < r_edges.Length; i++)
        {
            r_edges[i] = new Edge();
        }

        r_dowarpold = false;
        r_viewchanged = false;
    }

    // r_main.c: R_SetVrect
    public void R_SetVrect(in VRect pvrectin, ref VRect pvrect, int lineadj)
    {
        int h;
        float size;

        size = scr_viewsize.value > 100 ? 100 : scr_viewsize.value;
        if (cl.intermission != 0)
        {
            size = 100;
            lineadj = 0;
        }

        size /= 100;

        h = pvrectin.height - lineadj;
        pvrect.width = (int)(pvrectin.width * size);
        if (pvrect.width < 96)
        {
            size = (float)(96.0 / pvrectin.width);
            pvrect.width = 96; // min for icons
        }

        pvrect.width &= ~7;
        pvrect.height = (int)(pvrectin.height * size);
        if (pvrect.height > pvrectin.height - lineadj)
        {
            pvrect.height = pvrectin.height - lineadj;
        }

        pvrect.height &= ~1;

        pvrect.x = (pvrectin.width - pvrect.width) / 2;
        pvrect.y = (h - pvrect.height) / 2;

        if (lcd_x.value != 0)
        {
            pvrect.y >>= 1;
            pvrect.height >>= 1;
        }
    }

    // r_main.c: R_ViewChanged
    // Called every time the vid structure or r_refdef changes. Guaranteed to be called before the first refresh
    public void R_ViewChanged(in VRect pvrect, int lineadj, float aspect)
    {
        int i;
        float res_scale;

        r_viewchanged = true;

        R_SetVrect(pvrect, ref r_refdef.vrect, lineadj);

        r_refdef.horizontalFieldOfView = (float)(2.0 * Libm.tan(r_refdef.fov_x / 360 * M_PI));
        r_refdef.fvrectx = r_refdef.vrect.x;
        r_refdef.fvrectx_adj = (float)(r_refdef.vrect.x - 0.5);
        r_refdef.vrect_x_adj_shift20 = (r_refdef.vrect.x << 20) + (1 << 19) - 1;
        r_refdef.fvrecty = r_refdef.vrect.y;
        r_refdef.fvrecty_adj = (float)(r_refdef.vrect.y - 0.5);
        r_refdef.vrectright = r_refdef.vrect.x + r_refdef.vrect.width;
        r_refdef.vrectright_adj_shift20 = (r_refdef.vrectright << 20) + (1 << 19) - 1;
        r_refdef.fvrectright = r_refdef.vrectright;
        r_refdef.fvrectright_adj = (float)(r_refdef.vrectright - 0.5);
        r_refdef.vrectrightedge = (float)(r_refdef.vrectright - 0.99);
        r_refdef.vrectbottom = r_refdef.vrect.y + r_refdef.vrect.height;
        r_refdef.fvrectbottom = r_refdef.vrectbottom;
        r_refdef.fvrectbottom_adj = (float)(r_refdef.vrectbottom - 0.5);

        r_refdef.aliasvrect.x = (int)(r_refdef.vrect.x * r_aliasuvscale);
        r_refdef.aliasvrect.y = (int)(r_refdef.vrect.y * r_aliasuvscale);
        r_refdef.aliasvrect.width = (int)(r_refdef.vrect.width * r_aliasuvscale);
        r_refdef.aliasvrect.height = (int)(r_refdef.vrect.height * r_aliasuvscale);
        r_refdef.aliasvrectright = r_refdef.aliasvrect.x + r_refdef.aliasvrect.width;
        r_refdef.aliasvrectbottom = r_refdef.aliasvrect.y + r_refdef.aliasvrect.height;

        pixelAspect = aspect;
        xOrigin = r_refdef.xOrigin;
        yOrigin = r_refdef.yOrigin;

        screenAspect = r_refdef.vrect.width * pixelAspect / r_refdef.vrect.height;

        // 320*200 1.0 pixelAspect = 1.6 screenAspect
        // 320*240 1.0 pixelAspect = 1.3333 screenAspect
        // proper 320*200 pixelAspect = 0.8333333
        verticalFieldOfView = r_refdef.horizontalFieldOfView / screenAspect;

        // values for perspective projection
        // if math were exact, the values would range from 0.5 to to range+0.5
        // hopefully they wll be in the 0.000001 to range+.999999 and truncate
        // the polygon rasterization will never render in the first row or column
        // but will definately render in the [range] row and column, so adjust the
        // buffer origin to get an exact edge to edge fill
        xcenter = (float)((r_refdef.vrect.width * XCENTERING) + r_refdef.vrect.x - 0.5);
        aliasxcenter = xcenter * r_aliasuvscale;
        ycenter = (float)((r_refdef.vrect.height * YCENTERING) + r_refdef.vrect.y - 0.5);
        aliasycenter = ycenter * r_aliasuvscale;

        xscale = r_refdef.vrect.width / r_refdef.horizontalFieldOfView;
        aliasxscale = xscale * r_aliasuvscale;
        xscaleinv = (float)(1.0 / xscale);
        yscale = xscale * pixelAspect;
        aliasyscale = yscale * r_aliasuvscale;
        yscaleinv = (float)(1.0 / yscale);
        xscaleshrink = (r_refdef.vrect.width - 6) / r_refdef.horizontalFieldOfView;
        yscaleshrink = xscaleshrink * pixelAspect;

        // left side clip
        screenedge[0].normal[0] = (float)(-1.0 / (xOrigin * r_refdef.horizontalFieldOfView));
        screenedge[0].normal[1] = 0;
        screenedge[0].normal[2] = 1;
        screenedge[0].type = (byte)PLANE_ANYZ;

        // right side clip
        screenedge[1].normal[0] = (float)(1.0 / ((1.0 - xOrigin) * r_refdef.horizontalFieldOfView));
        screenedge[1].normal[1] = 0;
        screenedge[1].normal[2] = 1;
        screenedge[1].type = (byte)PLANE_ANYZ;

        // top side clip
        screenedge[2].normal[0] = 0;
        screenedge[2].normal[1] = (float)(-1.0 / (yOrigin * verticalFieldOfView));
        screenedge[2].normal[2] = 1;
        screenedge[2].type = (byte)PLANE_ANYZ;

        // bottom side clip
        screenedge[3].normal[0] = 0;
        screenedge[3].normal[1] = (float)(1.0 / ((1.0 - yOrigin) * verticalFieldOfView));
        screenedge[3].normal[2] = 1;
        screenedge[3].type = (byte)PLANE_ANYZ;

        for (i = 0; i < 4; i++)
        {
            VectorNormalize(ref screenedge[i].normal);
        }

        res_scale = (float)(Math.Sqrt((double)(r_refdef.vrect.width * r_refdef.vrect.height) / (320.0 * 152.0)) *
            (2.0 / r_refdef.horizontalFieldOfView));
        r_aliastransition = r_aliastransbase.value * res_scale;
        r_resfudge = r_aliastransadj.value * res_scale;

        if (scr_fov.value <= 90.0)
        {
            r_fov_greater_than_90 = false;
        }
        else
        {
            r_fov_greater_than_90 = true;
        }

        D_ViewChanged();
    }

    // r_main.c: R_RenderView_
    // r_refdef must be set before the first call
    public void R_RenderView_()
    {
        if (r_timegraph.value != 0 || r_speeds.value != 0 || r_dspeeds.value != 0)
        {
            r_time1 = (float)Sys_FloatTime();
        }

        R_SetupFrame();

        R_MarkLeaves(); // done here so we know if we're in water

        // make FDIV fast. This reduces timing precision after we've been running for a while, so we don't do it
        // globally. This also sets chop mode, and we do it here so that setup stuff like the refresh area
        // calculations match what's done in screen.c (Sys_LowFPPrecision: the precision is single throughout)
        if (cl_entities[0].model == null || cl.worldmodel == null)
        {
            throw Sys_Error("R_RenderView: NULL worldmodel");
        }

        if (r_dspeeds.value == 0)
        {
            VID_UnlockBuffer();
            S_ExtraUpdate(); // don't let sound get messed up if going slow
            VID_LockBuffer();
        }

        R_EdgeDrawing();

        if (r_dspeeds.value == 0)
        {
            VID_UnlockBuffer();
            S_ExtraUpdate(); // don't let sound get messed up if going slow
            VID_LockBuffer();
        }

        if (r_dspeeds.value != 0)
        {
            se_time2 = (float)Sys_FloatTime();
            de_time1 = se_time2;
        }

        R_DrawEntitiesOnList();

        if (r_dspeeds.value != 0)
        {
            de_time2 = (float)Sys_FloatTime();
            dv_time1 = de_time2;
        }

        R_DrawViewModel();

        if (r_dspeeds.value != 0)
        {
            dv_time2 = (float)Sys_FloatTime();
            dp_time1 = (float)Sys_FloatTime();
        }

        R_DrawParticles();

        if (r_dspeeds.value != 0)
        {
            dp_time2 = (float)Sys_FloatTime();
        }

        if (r_dowarp)
        {
            D_WarpScreen();
        }

        V_SetContentsColor(r_viewleaf!.contents);

        if (r_timegraph.value != 0)
        {
            R_TimeGraph();
        }

        if (r_aliasstats.value != 0)
        {
            R_PrintAliasStats();
        }

        if (r_speeds.value != 0)
        {
            R_PrintTimes();
        }

        if (r_dspeeds.value != 0)
        {
            R_PrintDSpeeds();
        }

        if (r_reportsurfout.value != 0 && r_outofsurfaces != 0)
        {
            Con_Printf("Short %d surfaces\n", r_outofsurfaces);
        }

        if (r_reportedgeout.value != 0 && r_outofedges != 0)
        {
            Con_Printf("Short roughly %d edges\n", r_outofedges * 2 / 3);
        }

        // back to high floating-point precision (Sys_HighFPPrecision)
    }

    // r_main.c: R_RenderView (its checks of the stack and of alignment are the C's)
    public void R_RenderView() => R_RenderView_();

    // r_main.c: R_MarkLeaves
    public void R_MarkLeaves()
    {
        byte[] vis;
        MNodeBase? node;
        int i;

        if (r_oldviewleaf == r_viewleaf)
        {
            return;
        }

        r_visframecount++;
        r_oldviewleaf = r_viewleaf;

        vis = Mod_LeafPVS(r_viewleaf!, cl.worldmodel!);

        for (i = 0; i < cl.worldmodel!.numleafs; i++)
        {
            if ((vis[i >> 3] & (1 << (i & 7))) != 0)
            {
                node = cl.worldmodel.leafs[i + 1];
                do
                {
                    if (node.visframe == r_visframecount)
                    {
                        break;
                    }

                    node.visframe = r_visframecount;
                    node = node.parent;
                }
                while (node != null);
            }
        }
    }

    // r_main.c: R_DrawEntitiesOnList
    public void R_DrawEntitiesOnList()
    {
        int i;
        int j;
        int lnum;
        var lighting = new ALight();

        // FIXME: remove and do real lighting
        Vec3 lightvec = new(-1, 0, 0);
        Vec3 dist = default;
        float add;

        if (r_drawentities.value == 0)
        {
            return;
        }

        for (i = 0; i < cl_numvisedicts; i++)
        {
            currententity = cl_visedicts[i]!;

            if (currententity == cl_entities[cl.viewentity])
            {
                continue; // don't draw the player
            }

            switch (currententity.model!.type)
            {
                case modtype_t.mod_sprite:
                    VectorCopy(currententity.origin, ref r_entorigin);
                    VectorSubtract(r_origin, r_entorigin, ref modelorg);
                    R_DrawSprite();
                    break;

                case modtype_t.mod_alias:
                    VectorCopy(currententity.origin, ref r_entorigin);
                    VectorSubtract(r_origin, r_entorigin, ref modelorg);

                    // see if the bounding box lets us trivially reject, also sets trivial accept status
                    if (R_AliasCheckBBox())
                    {
                        j = R_LightPoint(currententity.origin);

                        lighting.ambientlight = j;
                        lighting.shadelight = j;

                        lighting.plightvec = lightvec;

                        for (lnum = 0; lnum < MAX_DLIGHTS; lnum++)
                        {
                            if (cl_dlights[lnum].die >= cl.time)
                            {
                                VectorSubtract(currententity.origin,
                                    cl_dlights[lnum].origin,
                                    ref dist);
                                add = cl_dlights[lnum].radius - Length(dist);

                                if (add > 0)
                                {
                                    lighting.ambientlight = X86.FtoI(lighting.ambientlight + add);
                                }
                            }
                        }

                        // clamp lighting so it doesn't overbright as much
                        if (lighting.ambientlight > 128)
                        {
                            lighting.ambientlight = 128;
                        }

                        if (lighting.ambientlight + lighting.shadelight > 192)
                        {
                            lighting.shadelight = 192 - lighting.ambientlight;
                        }

                        R_AliasDrawModel(lighting);
                    }

                    break;

                default:
                    break;
            }
        }
    }

    // r_main.c: R_DrawViewModel
    public void R_DrawViewModel()
    {
        // FIXME: remove and do real lighting
        Vec3 lightvec = new(-1, 0, 0);
        int j;
        int lnum;
        Vec3 dist = default;
        float add;
        Client.Dlight dl;

        if (r_drawviewmodel.value == 0 || r_fov_greater_than_90)
        {
            return;
        }

        if ((cl.items & QuakeDef.IT_INVISIBILITY) != 0)
        {
            return;
        }

        if (cl.stats[QuakeDef.STAT_HEALTH] <= 0)
        {
            return;
        }

        currententity = cl.viewent;
        if (currententity.model == null)
        {
            return;
        }

        VectorCopy(currententity.origin, ref r_entorigin);
        VectorSubtract(r_origin, r_entorigin, ref modelorg);

        VectorCopy(vup, ref viewlightvec);
        VectorInverse(ref viewlightvec);

        j = R_LightPoint(currententity.origin);

        if (j < 24)
        {
            j = 24; // allways give some light on gun
        }

        r_viewlighting.ambientlight = j;
        r_viewlighting.shadelight = j;

        // add dynamic lights
        for (lnum = 0; lnum < MAX_DLIGHTS; lnum++)
        {
            dl = cl_dlights[lnum];
            if (dl.radius == 0)
            {
                continue;
            }

            if (dl.radius == 0)
            {
                continue;
            }

            if (dl.die < cl.time)
            {
                continue;
            }

            VectorSubtract(currententity.origin, dl.origin, ref dist);
            add = dl.radius - Length(dist);
            if (add > 0)
            {
                r_viewlighting.ambientlight = X86.FtoI(r_viewlighting.ambientlight + add);
            }
        }

        // clamp lighting so it doesn't overbright as much
        if (r_viewlighting.ambientlight > 128)
        {
            r_viewlighting.ambientlight = 128;
        }

        if (r_viewlighting.ambientlight + r_viewlighting.shadelight > 192)
        {
            r_viewlighting.shadelight = 192 - r_viewlighting.ambientlight;
        }

        r_viewlighting.plightvec = lightvec;

        R_AliasDrawModel(r_viewlighting);
    }

    // r_main.c: R_BmodelCheckBBox
    public int R_BmodelCheckBBox(ModelT clmodel, ReadOnlySpan<float> minmaxs)
    {
        int i, pindex, clipflags;
        Vec3 acceptpt = default, rejectpt = default;
        double d;

        clipflags = 0;

        if (currententity.angles[0] != 0 || currententity.angles[1] != 0
            || currententity.angles[2] != 0)
        {
            for (i = 0; i < 4; i++)
            {
                d = DotProduct(currententity.origin, view_clipplanes[i].normal);
                d -= view_clipplanes[i].dist;

                if (d <= -clmodel.radius)
                {
                    return BMODEL_FULLY_CLIPPED;
                }

                if (d <= clmodel.radius)
                {
                    clipflags |= 1 << i;
                }
            }
        }
        else
        {
            for (i = 0; i < 4; i++)
            {
                // generate accept and reject points
                // FIXME: do with fast look-ups or integer tests based on the sign bit of the floating point values
                pindex = pfrustum_indexes[i];

                rejectpt[0] = minmaxs[r_frustum_indexes[pindex + 0]];
                rejectpt[1] = minmaxs[r_frustum_indexes[pindex + 1]];
                rejectpt[2] = minmaxs[r_frustum_indexes[pindex + 2]];

                d = DotProduct(rejectpt, view_clipplanes[i].normal);
                d -= view_clipplanes[i].dist;

                if (d <= 0)
                {
                    return BMODEL_FULLY_CLIPPED;
                }

                acceptpt[0] = minmaxs[r_frustum_indexes[pindex + 3 + 0]];
                acceptpt[1] = minmaxs[r_frustum_indexes[pindex + 3 + 1]];
                acceptpt[2] = minmaxs[r_frustum_indexes[pindex + 3 + 2]];

                d = DotProduct(acceptpt, view_clipplanes[i].normal);
                d -= view_clipplanes[i].dist;

                if (d <= 0)
                {
                    clipflags |= 1 << i;
                }
            }
        }

        return clipflags;
    }

    // r_main.c: R_DrawBEntitiesOnList
    public void R_DrawBEntitiesOnList()
    {
        int i, j, k, clipflags;
        Vec3 oldorigin = default;
        ModelT clmodel;
        Span<float> minmaxs = stackalloc float[6];

        if (r_drawentities.value == 0)
        {
            return;
        }

        VectorCopy(modelorg, ref oldorigin);
        insubmodel = true;
        r_dlightframecount = r_framecount;

        for (i = 0; i < cl_numvisedicts; i++)
        {
            currententity = cl_visedicts[i]!;

            switch (currententity.model!.type)
            {
                case modtype_t.mod_brush:

                    clmodel = currententity.model;

                    // see if the bounding box lets us trivially reject, also sets trivial accept status
                    for (j = 0; j < 3; j++)
                    {
                        minmaxs[j] = currententity.origin[j] +
                            clmodel.mins[j];
                        minmaxs[3 + j] = currententity.origin[j] +
                            clmodel.maxs[j];
                    }

                    clipflags = R_BmodelCheckBBox(clmodel, minmaxs);

                    if (clipflags != BMODEL_FULLY_CLIPPED)
                    {
                        VectorCopy(currententity.origin, ref r_entorigin);
                        VectorSubtract(r_origin, r_entorigin, ref modelorg);

                        // FIXME: is this needed?
                        VectorCopy(modelorg, ref r_worldmodelorg);

                        r_pcurrentvertbase = clmodel.vertexes;

                        // FIXME: stop transforming twice
                        R_RotateBmodel();

                        // calculate dynamic lighting for bmodel if it's not an instanced model
                        if (clmodel.firstmodelsurface != 0)
                        {
                            for (k = 0; k < MAX_DLIGHTS; k++)
                            {
                                if ((cl_dlights[k].die < cl.time) ||
                                    (cl_dlights[k].radius == 0))
                                {
                                    continue;
                                }

                                R_MarkLights(cl_dlights[k], 1 << k,
                                    clmodel.nodes[clmodel.hulls[0].firstclipnode]);
                            }
                        }

                        // if the driver wants polygons, deliver those. Z-buffering is on at this point, so no
                        // clipping to the world tree is needed, just frustum clipping
                        if (r_drawpolys | r_drawculledpolys)
                        {
                            R_ZDrawSubmodelPolys(clmodel);
                        }
                        else
                        {
                            r_pefragtopnode = null;

                            for (j = 0; j < 3; j++)
                            {
                                r_emins[j] = minmaxs[j];
                                r_emaxs[j] = minmaxs[3 + j];
                            }

                            R_SplitEntityOnNode2(cl.worldmodel!.nodes[0]);

                            if (r_pefragtopnode != null)
                            {
                                currententity.topnode = r_pefragtopnode;

                                if (r_pefragtopnode.contents >= 0)
                                {
                                    // not a leaf; has to be clipped to the world BSP
                                    r_clipflags = clipflags;
                                    R_DrawSolidClippedSubmodelPolygons(clmodel);
                                }
                                else
                                {
                                    // falls entirely in one leaf, so we just put all the edges in the edge list
                                    // and let 1/z sorting handle drawing order
                                    R_DrawSubmodelPolygons(clmodel, clipflags);
                                }

                                currententity.topnode = null;
                            }
                        }

                        // put back world rotation and frustum clipping
                        // FIXME: R_RotateBmodel should just work off base_vxx
                        VectorCopy(base_vpn, ref vpn);
                        VectorCopy(base_vup, ref vup);
                        VectorCopy(base_vright, ref vright);
                        VectorCopy(base_modelorg, ref modelorg);
                        VectorCopy(oldorigin, ref modelorg);
                        R_TransformFrustum();
                    }

                    break;

                default:
                    break;
            }
        }

        insubmodel = false;
    }

    // r_main.c: R_EdgeDrawing (its edges and surfaces, on the C's stack, are allocated by R_NewMap)
    public void R_EdgeDrawing()
    {
        // (R_SurfacePatch: nonintel.c's does nothing)
        R_BeginEdgeFrame();

        if (r_dspeeds.value != 0)
        {
            rw_time1 = (float)Sys_FloatTime();
        }

        R_RenderWorld();

        if (r_drawculledpolys)
        {
            R_ScanEdges();
        }

        // only the world can be drawn back to front with no z reads or compares, just z writes, so have the driver
        // turn z compares on now
        D_TurnZOn();

        if (r_dspeeds.value != 0)
        {
            rw_time2 = (float)Sys_FloatTime();
            db_time1 = rw_time2;
        }

        R_DrawBEntitiesOnList();

        if (r_dspeeds.value != 0)
        {
            db_time2 = (float)Sys_FloatTime();
            se_time1 = db_time2;
        }

        if (r_dspeeds.value == 0)
        {
            VID_UnlockBuffer();
            S_ExtraUpdate(); // don't let sound get messed up if going slow
            VID_LockBuffer();
        }

        if (!(r_drawpolys | r_drawculledpolys))
        {
            R_ScanEdges();
        }
    }

    // r_main.c: R_InitTurb
    public void R_InitTurb()
    {
        int i;

        for (i = 0; i < SIN_BUFFER_SIZE; i++)
        {
            sintable[i] = X86.FtoI(AMP + (Libm.sin(i * 3.14159 * 2 / CYCLE) * AMP));
            intsintable[i] = X86.FtoI(AMP2 + (Libm.sin(i * 3.14159 * 2 / CYCLE) * AMP2)); // AMP2, not 20
        }
    }
}
