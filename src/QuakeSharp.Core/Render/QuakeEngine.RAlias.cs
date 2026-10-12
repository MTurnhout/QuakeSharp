using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Render;
using static QuakeSharp.Core.Common.MathLib;
using ModelT = QuakeSharp.Core.Model.Model;

namespace QuakeSharp.Core;

// r_alias.c: routines for setting up to draw alias models
public sealed partial class QuakeEngine
{
    /// <summary>lowest light value we'll allow, to avoid the need for inner-loop light clamping</summary>
    private const int LIGHT_MIN = 5;

    public readonly AffineTriDesc r_affinetridesc = new();

    /// <summary>FIXME: should go away</summary>
    public byte[] acolormap = [];

    // r_alias.c: r_apverts (the frame's vertices)
    public TriVertX[] r_apverts = [];

    // TODO: these probably will go away with optimized rasterization
    public Mdl pmdl = null!;
    public Vec3 r_plightvec;
    public int r_ambientlight;
    public float r_shadelight;
    public AliasHdr paliashdr = null!;

    // r_alias.c: pfinalverts, pauxverts (R_AliasDrawModel's finalverts and auxverts, on the C's stack)
    public readonly FinalVert[] pfinalverts = new FinalVert[MAXALIASVERTS];
    public readonly AuxVert[] pauxverts = new AuxVert[MAXALIASVERTS];

    private float ziscale;
    private ModelT pmodel = null!;

    private Vec3 alias_forward, alias_right, alias_up;

    private MAliasSkinDesc pskindesc;

    public int r_amodels_drawn;
    public int a_skinwidth;
    public int r_anumverts;

    public Mat3x4 aliastransform;

    // r_alias.c: R_AliasSetUpTransform's statics
    private Mat3x4 r_alias_tmatrix;
    private Mat3x4 r_alias_viewmatrix;

    // r_alias.c: aedges
    private static readonly int[] aedges =
    [
        0, 1, 1, 2, 2, 3, 3, 0,
        4, 5, 5, 6, 6, 7, 7, 4,
        0, 5, 1, 4, 2, 7, 3, 6,
    ];

    // r_alias.c: R_AliasCheckBBox's viewpts and viewaux
    private readonly FinalVert[] r_alias_viewpts = new FinalVert[16];
    private readonly AuxVert[] r_alias_viewaux = new AuxVert[16];

    // r_alias.c: R_AliasCheckBBox
    public bool R_AliasCheckBBox()
    {
        int i, flags, frame, numv;
        AliasHdr pahdr;
        float zi, v0, v1, frac;
        Span<Vec3> basepts = stackalloc Vec3[8];
        FinalVert[] viewpts = r_alias_viewpts;
        AuxVert[] viewaux = r_alias_viewaux;
        MAliasFrameDesc pframedesc;
        bool zclipped, zfullyclipped;
        uint anyclip, allclip;
        int minz;

        // expand, rotate, and translate points into worldspace
        currententity.trivial_accept = 0;
        pmodel = currententity.model!;
        pahdr = (AliasHdr)Mod_Extradata(pmodel);
        pmdl = pahdr.model;

        R_AliasSetUpTransform(0);

        // construct the base bounding box for this frame
        frame = currententity.frame;

        // TODO: don't repeat this check when drawing?
        if ((frame >= pmdl.numframes) || (frame < 0))
        {
            Con_DPrintf("No such frame %d %s\n", frame,
                pmodel.name);
            frame = 0;
        }

        pframedesc = pahdr.frames[frame];

        // x worldspace coordinates
        basepts[0][0] = basepts[1][0] = basepts[2][0] = basepts[3][0] =
            pframedesc.bboxmin.v[0];
        basepts[4][0] = basepts[5][0] = basepts[6][0] = basepts[7][0] =
            pframedesc.bboxmax.v[0];

        // y worldspace coordinates
        basepts[0][1] = basepts[3][1] = basepts[5][1] = basepts[6][1] =
            pframedesc.bboxmin.v[1];
        basepts[1][1] = basepts[2][1] = basepts[4][1] = basepts[7][1] =
            pframedesc.bboxmax.v[1];

        // z worldspace coordinates
        basepts[0][2] = basepts[1][2] = basepts[4][2] = basepts[5][2] =
            pframedesc.bboxmin.v[2];
        basepts[2][2] = basepts[3][2] = basepts[6][2] = basepts[7][2] =
            pframedesc.bboxmax.v[2];

        zclipped = false;
        zfullyclipped = true;

        minz = 9999;
        for (i = 0; i < 8; i++)
        {
            R_AliasTransformVector(basepts[i], ref viewaux[i].fv);

            if (viewaux[i].fv[2] < ALIAS_Z_CLIP_PLANE)
            {
                // we must clip points that are closer than the near clip plane
                viewpts[i].flags = ALIAS_Z_CLIP;
                zclipped = true;
            }
            else
            {
                if (viewaux[i].fv[2] < minz)
                {
                    minz = X86.FtoI(viewaux[i].fv[2]);
                }

                viewpts[i].flags = 0;
                zfullyclipped = false;
            }
        }

        if (zfullyclipped)
        {
            return false; // everything was near-z-clipped
        }

        numv = 8;

        if (zclipped)
        {
            // organize points by edges, use edges to get new points (possible trivial reject)
            for (i = 0; i < 12; i++)
            {
                // edge endpoints
                int pv0 = aedges[i * 2];
                int pv1 = aedges[(i * 2) + 1];

                // if one end is clipped and the other isn't, make a new point
                if ((viewpts[pv0].flags ^ viewpts[pv1].flags) != 0)
                {
                    frac = (ALIAS_Z_CLIP_PLANE - viewaux[pv0].fv[2]) /
                        (viewaux[pv1].fv[2] - viewaux[pv0].fv[2]);
                    viewaux[numv].fv[0] = viewaux[pv0].fv[0] +
                        ((viewaux[pv1].fv[0] - viewaux[pv0].fv[0]) * frac);
                    viewaux[numv].fv[1] = viewaux[pv0].fv[1] +
                        ((viewaux[pv1].fv[1] - viewaux[pv0].fv[1]) * frac);
                    viewaux[numv].fv[2] = ALIAS_Z_CLIP_PLANE;
                    viewpts[numv].flags = 0;
                    numv++;
                }
            }
        }

        // project the vertices that remain after clipping
        anyclip = 0;
        allclip = ALIAS_XY_CLIP_MASK;

        // TODO: probably should do this loop in ASM, especially if we use floats
        for (i = 0; i < numv; i++)
        {
            // we don't need to bother with vertices that were z-clipped
            if ((viewpts[i].flags & ALIAS_Z_CLIP) != 0)
            {
                continue;
            }

            zi = (float)(1.0 / viewaux[i].fv[2]);

            // FIXME: do with chop mode in ASM, or convert to float
            v0 = (viewaux[i].fv[0] * xscale * zi) + xcenter;
            v1 = (viewaux[i].fv[1] * yscale * zi) + ycenter;

            flags = 0;

            if (v0 < r_refdef.fvrectx)
            {
                flags |= ALIAS_LEFT_CLIP;
            }

            if (v1 < r_refdef.fvrecty)
            {
                flags |= ALIAS_TOP_CLIP;
            }

            if (v0 > r_refdef.fvrectright)
            {
                flags |= ALIAS_RIGHT_CLIP;
            }

            if (v1 > r_refdef.fvrectbottom)
            {
                flags |= ALIAS_BOTTOM_CLIP;
            }

            anyclip |= (uint)flags;
            allclip &= (uint)flags;
        }

        if (allclip != 0)
        {
            return false; // trivial reject off one side
        }

        currententity.trivial_accept = (anyclip == 0 ? 1 : 0) & (!zclipped ? 1 : 0);

        if (currententity.trivial_accept != 0)
        {
            if (minz > (r_aliastransition + (pmdl.size * r_resfudge)))
            {
                currententity.trivial_accept |= 2;
            }
        }

        return true;
    }

    // r_alias.c: R_AliasTransformVector
    public void R_AliasTransformVector(in Vec3 @in, ref Vec3 @out)
    {
        @out[0] = (@in[0] * aliastransform[0][0]) + (@in[1] * aliastransform[0][1]) + (@in[2] * aliastransform[0][2]) + aliastransform[0][3];
        @out[1] = (@in[0] * aliastransform[1][0]) + (@in[1] * aliastransform[1][1]) + (@in[2] * aliastransform[1][2]) + aliastransform[1][3];
        @out[2] = (@in[0] * aliastransform[2][0]) + (@in[1] * aliastransform[2][1]) + (@in[2] * aliastransform[2][2]) + aliastransform[2][3];
    }

    // r_alias.c: R_AliasPreparePoints
    // General clipped case
    public void R_AliasPreparePoints()
    {
        int i;
        StVert[] pstverts;
        MTriangle[] ptri;
        int pfv0, pfv1, pfv2;

        pstverts = paliashdr.stverts;
        r_anumverts = pmdl.numverts;

        for (i = 0; i < r_anumverts; i++)
        {
            ref FinalVert fv = ref pfinalverts[i];
            ref AuxVert av = ref pauxverts[i];
            R_AliasTransformFinalVert(ref fv, ref av, r_apverts[i], pstverts[i]);
            if (av.fv[2] < ALIAS_Z_CLIP_PLANE)
            {
                fv.flags |= ALIAS_Z_CLIP;
            }
            else
            {
                R_AliasProjectFinalVert(ref fv, av);

                if (fv.v[0] < r_refdef.aliasvrect.x)
                {
                    fv.flags |= ALIAS_LEFT_CLIP;
                }

                if (fv.v[1] < r_refdef.aliasvrect.y)
                {
                    fv.flags |= ALIAS_TOP_CLIP;
                }

                if (fv.v[0] > r_refdef.aliasvrectright)
                {
                    fv.flags |= ALIAS_RIGHT_CLIP;
                }

                if (fv.v[1] > r_refdef.aliasvrectbottom)
                {
                    fv.flags |= ALIAS_BOTTOM_CLIP;
                }
            }
        }

        // clip and draw all triangles
        r_affinetridesc.numtriangles = 1;

        ptri = paliashdr.triangles;
        for (i = 0; i < pmdl.numtris; i++)
        {
            pfv0 = ptri[i].vertindex[0];
            pfv1 = ptri[i].vertindex[1];
            pfv2 = ptri[i].vertindex[2];

            if ((pfinalverts[pfv0].flags & pfinalverts[pfv1].flags & pfinalverts[pfv2].flags &
                (ALIAS_XY_CLIP_MASK | ALIAS_Z_CLIP)) != 0)
            {
                continue; // completely clipped
            }

            if (((pfinalverts[pfv0].flags | pfinalverts[pfv1].flags | pfinalverts[pfv2].flags) &
                (ALIAS_XY_CLIP_MASK | ALIAS_Z_CLIP)) == 0)
            {
                // totally unclipped
                r_affinetridesc.pfinalverts = pfinalverts;
                r_affinetridesc.ptriangles = ptri;
                r_affinetridesc.ptrianglesstart = i;
                D_PolysetDraw();
            }
            else
            {
                // partially clipped
                R_AliasClipTriangle(ptri[i]);
            }
        }
    }

    // r_alias.c: R_AliasSetUpTransform
    public void R_AliasSetUpTransform(int trivial_accept)
    {
        int i;
        Mat3x4 rotationmatrix = default, t2matrix = default;
        Vec3 angles = default;

        // TODO: should really be stored with the entity instead of being reconstructed
        // TODO: should use a look-up table
        // TODO: could cache lazily, stored in the entity
        angles[QuakeDef.ROLL] = currententity.angles[QuakeDef.ROLL];
        angles[QuakeDef.PITCH] = -currententity.angles[QuakeDef.PITCH];
        angles[QuakeDef.YAW] = currententity.angles[QuakeDef.YAW];
        AngleVectors(angles, ref alias_forward, ref alias_right, ref alias_up);

        r_alias_tmatrix[0][0] = pmdl.scale[0];
        r_alias_tmatrix[1][1] = pmdl.scale[1];
        r_alias_tmatrix[2][2] = pmdl.scale[2];

        r_alias_tmatrix[0][3] = pmdl.scale_origin[0];
        r_alias_tmatrix[1][3] = pmdl.scale_origin[1];
        r_alias_tmatrix[2][3] = pmdl.scale_origin[2];

        // TODO: can do this with simple matrix rearrangement
        for (i = 0; i < 3; i++)
        {
            t2matrix[i][0] = alias_forward[i];
            t2matrix[i][1] = -alias_right[i];
            t2matrix[i][2] = alias_up[i];
        }

        t2matrix[0][3] = -modelorg[0];
        t2matrix[1][3] = -modelorg[1];
        t2matrix[2][3] = -modelorg[2];

        // FIXME: can do more efficiently than full concatenation
        R_ConcatTransforms(t2matrix, r_alias_tmatrix, ref rotationmatrix);

        // TODO: should be global, set when vright, etc., set
        r_alias_viewmatrix[0][0] = vright[0];
        r_alias_viewmatrix[0][1] = vright[1];
        r_alias_viewmatrix[0][2] = vright[2];
        r_alias_viewmatrix[1][0] = -vup[0];
        r_alias_viewmatrix[1][1] = -vup[1];
        r_alias_viewmatrix[1][2] = -vup[2];
        r_alias_viewmatrix[2][0] = vpn[0];
        r_alias_viewmatrix[2][1] = vpn[1];
        r_alias_viewmatrix[2][2] = vpn[2];

        R_ConcatTransforms(r_alias_viewmatrix, rotationmatrix, ref aliastransform);

        // do the scaling up of x and y to screen coordinates as part of the transform for the unclipped case (it would
        // mess up clipping in the clipped case). Also scale down z, so 1/z is scaled 31 bits for free, and scale down
        // x and y correspondingly so the projected x and y come out right
        // FIXME: make this work for clipped case too?
        if (trivial_accept != 0)
        {
            for (i = 0; i < 4; i++)
            {
                aliastransform[0][i] = (float)(aliastransform[0][i] * (aliasxscale *
                    (1.0 / ((float)0x8000 * 0x10000))));
                aliastransform[1][i] = (float)(aliastransform[1][i] * (aliasyscale *
                    (1.0 / ((float)0x8000 * 0x10000))));
                aliastransform[2][i] = (float)(aliastransform[2][i] * (1.0 / ((float)0x8000 * 0x10000)));
            }
        }
    }

    // r_alias.c: R_AliasTransformFinalVert
    public void R_AliasTransformFinalVert(ref FinalVert fv, ref AuxVert av, in TriVertX pverts, in StVert pstverts)
    {
        int temp;
        float lightcos;
        int plightnormal;

        av.fv[0] = (pverts.v[0] * aliastransform[0][0]) + (pverts.v[1] * aliastransform[0][1]) +
            (pverts.v[2] * aliastransform[0][2]) + aliastransform[0][3];
        av.fv[1] = (pverts.v[0] * aliastransform[1][0]) + (pverts.v[1] * aliastransform[1][1]) +
            (pverts.v[2] * aliastransform[1][2]) + aliastransform[1][3];
        av.fv[2] = (pverts.v[0] * aliastransform[2][0]) + (pverts.v[1] * aliastransform[2][1]) +
            (pverts.v[2] * aliastransform[2][2]) + aliastransform[2][3];

        fv.v[2] = pstverts.s;
        fv.v[3] = pstverts.t;

        fv.flags = pstverts.onseam;

        // lighting
        plightnormal = pverts.lightnormalindex * 3;
        lightcos = (r_avertexnormals[plightnormal] * r_plightvec[0]) + (r_avertexnormals[plightnormal + 1] * r_plightvec[1]) +
            (r_avertexnormals[plightnormal + 2] * r_plightvec[2]);
        temp = r_ambientlight;

        if (lightcos < 0)
        {
            temp += X86.FtoI(r_shadelight * lightcos);

            // clamp; because we limited the minimum ambient and shading light, we don't have to clamp low light, just
            // bright
            if (temp < 0)
            {
                temp = 0;
            }
        }

        fv.v[4] = temp;
    }

    // r_alias.c: R_AliasTransformAndProjectFinalVerts
    public void R_AliasTransformAndProjectFinalVerts(FinalVert[] fv, StVert[] pstverts)
    {
        int i, temp;
        float lightcos, zi;
        int plightnormal;
        TriVertX[] pverts;

        pverts = r_apverts;

        for (i = 0; i < r_anumverts; i++)
        {
            ref TriVertX pv = ref pverts[i];

            // transform and project
            zi = (float)(1.0 / ((pv.v[0] * aliastransform[2][0]) + (pv.v[1] * aliastransform[2][1]) +
                (pv.v[2] * aliastransform[2][2]) + aliastransform[2][3]));

            // x, y, and z are scaled down by 1/2**31 in the transform, so 1/z is scaled up by 1/2**31, and the
            // scaling cancels out for x and y in the projection
            fv[i].v[5] = X86.FtoI(zi);

            fv[i].v[0] = X86.FtoI((((pv.v[0] * aliastransform[0][0]) + (pv.v[1] * aliastransform[0][1]) +
                (pv.v[2] * aliastransform[0][2]) + aliastransform[0][3]) * zi) + aliasxcenter);
            fv[i].v[1] = X86.FtoI((((pv.v[0] * aliastransform[1][0]) + (pv.v[1] * aliastransform[1][1]) +
                (pv.v[2] * aliastransform[1][2]) + aliastransform[1][3]) * zi) + aliasycenter);

            fv[i].v[2] = pstverts[i].s;
            fv[i].v[3] = pstverts[i].t;
            fv[i].flags = pstverts[i].onseam;

            // lighting
            plightnormal = pv.lightnormalindex * 3;
            lightcos = (r_avertexnormals[plightnormal] * r_plightvec[0]) + (r_avertexnormals[plightnormal + 1] * r_plightvec[1]) +
                (r_avertexnormals[plightnormal + 2] * r_plightvec[2]);
            temp = r_ambientlight;

            if (lightcos < 0)
            {
                temp += X86.FtoI(r_shadelight * lightcos);

                // clamp; because we limited the minimum ambient and shading light, we don't have to clamp low
                // light, just bright
                if (temp < 0)
                {
                    temp = 0;
                }
            }

            fv[i].v[4] = temp;
        }
    }

    // r_alias.c: R_AliasProjectFinalVert
    public void R_AliasProjectFinalVert(ref FinalVert fv, in AuxVert av)
    {
        float zi;

        // project points
        zi = (float)(1.0 / av.fv[2]);

        fv.v[5] = X86.FtoI(zi * ziscale);

        fv.v[0] = X86.FtoI((av.fv[0] * aliasxscale * zi) + aliasxcenter);
        fv.v[1] = X86.FtoI((av.fv[1] * aliasyscale * zi) + aliasycenter);
    }

    // r_alias.c: R_AliasPrepareUnclippedPoints
    public void R_AliasPrepareUnclippedPoints()
    {
        StVert[] pstverts;

        pstverts = paliashdr.stverts;
        r_anumverts = pmdl.numverts;

        // FIXME: just use pfinalverts directly?
        R_AliasTransformAndProjectFinalVerts(pfinalverts, pstverts);

        if (r_affinetridesc.drawtype)
        {
            D_PolysetDrawFinalVerts(pfinalverts, r_anumverts);
        }

        r_affinetridesc.pfinalverts = pfinalverts;
        r_affinetridesc.ptriangles = paliashdr.triangles;
        r_affinetridesc.ptrianglesstart = 0;
        r_affinetridesc.numtriangles = pmdl.numtris;

        D_PolysetDraw();
    }

    // r_alias.c: R_AliasSetupSkin
    public void R_AliasSetupSkin()
    {
        int skinnum;
        int i, numskins;
        MAliasSkinGroup paliasskingroup;
        float[] pskinintervals;
        float fullskininterval;
        float skintargettime, skintime;

        skinnum = currententity.skinnum;
        if ((skinnum >= pmdl.numskins) || (skinnum < 0))
        {
            Con_DPrintf("R_AliasSetupSkin: no such skin # %d\n", skinnum);
            skinnum = 0;
        }

        pskindesc = paliashdr.skindesc[skinnum];
        a_skinwidth = pmdl.skinwidth;

        if (pskindesc.type == aliasskintype_t.ALIAS_SKIN_GROUP)
        {
            paliasskingroup = (MAliasSkinGroup)pskindesc.skin;
            pskinintervals = paliasskingroup.intervals;
            numskins = paliasskingroup.numskins;
            fullskininterval = pskinintervals[numskins - 1];

            skintime = (float)(cl.time + currententity.syncbase);

            // when loading in Mod_LoadAliasSkinGroup, we guaranteed all interval values are positive, so we don't
            // have to worry about division by 0
            skintargettime = skintime -
                (X86.FtoI(skintime / fullskininterval) * fullskininterval);

            for (i = 0; i < (numskins - 1); i++)
            {
                if (pskinintervals[i] > skintargettime)
                {
                    break;
                }
            }

            pskindesc = paliasskingroup.skindescs[i];
        }

        r_affinetridesc.pskin = (byte[])pskindesc.skin;
        r_affinetridesc.skinwidth = a_skinwidth;
        r_affinetridesc.seamfixupX16 = (a_skinwidth >> 1) << 16;
        r_affinetridesc.skinheight = pmdl.skinheight;
    }

    // r_alias.c: R_AliasSetupLighting
    public void R_AliasSetupLighting(ALight plighting)
    {
        // guarantee that no vertex will ever be lit below LIGHT_MIN, so we don't have to clamp off the bottom
        r_ambientlight = plighting.ambientlight;

        if (r_ambientlight < LIGHT_MIN)
        {
            r_ambientlight = LIGHT_MIN;
        }

        r_ambientlight = (255 - r_ambientlight) << VID_CBITS;

        if (r_ambientlight < LIGHT_MIN)
        {
            r_ambientlight = LIGHT_MIN;
        }

        r_shadelight = plighting.shadelight;

        if (r_shadelight < 0)
        {
            r_shadelight = 0;
        }

        r_shadelight *= VID_GRADES;

        // rotate the lighting vector into the model's frame of reference
        r_plightvec[0] = DotProduct(plighting.plightvec, alias_forward);
        r_plightvec[1] = -DotProduct(plighting.plightvec, alias_right);
        r_plightvec[2] = DotProduct(plighting.plightvec, alias_up);
    }

    // r_alias.c: R_AliasSetupFrame
    // set r_apverts
    public void R_AliasSetupFrame()
    {
        int frame;
        int i, numframes;
        MAliasGroup paliasgroup;
        float[] pintervals;
        float fullinterval, targettime, time;

        frame = currententity.frame;
        if ((frame >= pmdl.numframes) || (frame < 0))
        {
            Con_DPrintf("R_AliasSetupFrame: no such frame %d\n", frame);
            frame = 0;
        }

        if (paliashdr.frames[frame].type == aliasframetype_t.ALIAS_SINGLE)
        {
            r_apverts = (TriVertX[])paliashdr.frames[frame].frame;
            return;
        }

        paliasgroup = (MAliasGroup)paliashdr.frames[frame].frame;
        pintervals = paliasgroup.intervals;
        numframes = paliasgroup.numframes;
        fullinterval = pintervals[numframes - 1];

        time = (float)(cl.time + currententity.syncbase);

        // when loading in Mod_LoadAliasGroup, we guaranteed all interval values are positive, so we don't have to
        // worry about division by 0
        targettime = time - (X86.FtoI(time / fullinterval) * fullinterval);

        for (i = 0; i < (numframes - 1); i++)
        {
            if (pintervals[i] > targettime)
            {
                break;
            }
        }

        r_apverts = paliasgroup.frames[i].frame;
    }

    // r_alias.c: R_AliasDrawModel (its finalverts and auxverts, on the C's stack, are pfinalverts and pauxverts)
    public void R_AliasDrawModel(ALight plighting)
    {
        r_amodels_drawn++;

        // the z-clipped vertices are not projected, but R_Alias_clip_z reads their v[1]: the C's finalverts are
        // uninitialized stack; they start from zeros here and in the oracle (design.md §3.3)
        Array.Clear(pfinalverts);

        paliashdr = (AliasHdr)Mod_Extradata(currententity.model!);
        pmdl = paliashdr.model;

        R_AliasSetupSkin();
        R_AliasSetUpTransform(currententity.trivial_accept);
        R_AliasSetupLighting(plighting);
        R_AliasSetupFrame();

        if (currententity.colormap == null)
        {
            throw Sys_Error("R_AliasDrawModel: !currententity->colormap");
        }

        r_affinetridesc.drawtype = (currententity.trivial_accept == 3) &&
            r_recursiveaffinetriangles;

        if (r_affinetridesc.drawtype)
        {
            D_PolysetUpdateTables(); // FIXME: precalc...
        }

        acolormap = currententity.colormap;

        if (currententity != cl.viewent)
        {
            ziscale = (float)0x8000 * (float)0x10000;
        }
        else
        {
            ziscale = (float)((float)0x8000 * (float)0x10000 * 3.0);
        }

        if (currententity.trivial_accept != 0)
        {
            R_AliasPrepareUnclippedPoints();
        }
        else
        {
            R_AliasPreparePoints();
        }
    }
}
