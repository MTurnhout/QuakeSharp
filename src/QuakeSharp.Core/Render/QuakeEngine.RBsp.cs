using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Render;
using static QuakeSharp.Core.Common.MathLib;
using static QuakeSharp.Core.Model.BspFile;
using ModelT = QuakeSharp.Core.Model.Model;

namespace QuakeSharp.Core;

// r_bsp.c: the world's BSP walk and the brush models' polygons
public sealed partial class QuakeEngine
{
    // current entity info
    public bool insubmodel;
    public Entity currententity = null!;

    /// <summary>the viewpoint reletive to the currently rendering entity</summary>
    public Vec3 modelorg, base_modelorg;

    /// <summary>the currently rendering entity in world coordinates</summary>
    public Vec3 r_entorigin;

    public Mat3 entity_rotation;

    public Vec3 r_worldmodelorg;

    public int r_currentbkey;

    private const int MAX_BMODEL_VERTS = 500; // 6K
    private const int MAX_BMODEL_EDGES = 1000; // 12K

    // r_bsp.c: pbverts, pbedges (R_DrawSolidClippedSubmodelPolygons's bverts and bedges, on the C's stack),
    // numbverts, numbedges
    private readonly Vec3[] pbverts = new Vec3[MAX_BMODEL_VERTS];
    private readonly BEdge[] pbedges = [.. Enumerable.Range(0, MAX_BMODEL_EDGES).Select(_ => new BEdge())];
    private int numbverts, numbedges;

    // r_bsp.c: pfrontenter, pfrontexit (vertices in pbverts)
    private int pfrontenter = -1, pfrontexit = -1;

    private bool makeclippededge;

    // ===========================================================================

    // r_bsp.c: R_EntityRotate
    public void R_EntityRotate(ref Vec3 vec)
    {
        Vec3 tvec = default;

        VectorCopy(vec, ref tvec);
        vec[0] = DotProduct(entity_rotation[0], tvec);
        vec[1] = DotProduct(entity_rotation[1], tvec);
        vec[2] = DotProduct(entity_rotation[2], tvec);
    }

    // r_bsp.c: R_RotateBmodel
    public void R_RotateBmodel()
    {
        float angle, s, c;
        Mat3 temp1 = default, temp2 = default, temp3 = default;

        // TODO: should use a look-up table
        // TODO: should really be stored with the entity instead of being reconstructed
        // TODO: could cache lazily, stored in the entity
        // TODO: share work with R_SetUpAliasTransform

        // yaw
        angle = currententity.angles[QuakeDef.YAW];
        angle = (float)(angle * M_PI * 2 / 360);
        s = (float)Libm.sin(angle);
        c = (float)Libm.cos(angle);

        temp1[0][0] = c;
        temp1[0][1] = s;
        temp1[0][2] = 0;
        temp1[1][0] = -s;
        temp1[1][1] = c;
        temp1[1][2] = 0;
        temp1[2][0] = 0;
        temp1[2][1] = 0;
        temp1[2][2] = 1;

        // pitch
        angle = currententity.angles[QuakeDef.PITCH];
        angle = (float)(angle * M_PI * 2 / 360);
        s = (float)Libm.sin(angle);
        c = (float)Libm.cos(angle);

        temp2[0][0] = c;
        temp2[0][1] = 0;
        temp2[0][2] = -s;
        temp2[1][0] = 0;
        temp2[1][1] = 1;
        temp2[1][2] = 0;
        temp2[2][0] = s;
        temp2[2][1] = 0;
        temp2[2][2] = c;

        R_ConcatRotations(temp2, temp1, ref temp3);

        // roll
        angle = currententity.angles[QuakeDef.ROLL];
        angle = (float)(angle * M_PI * 2 / 360);
        s = (float)Libm.sin(angle);
        c = (float)Libm.cos(angle);

        temp1[0][0] = 1;
        temp1[0][1] = 0;
        temp1[0][2] = 0;
        temp1[1][0] = 0;
        temp1[1][1] = c;
        temp1[1][2] = s;
        temp1[2][0] = 0;
        temp1[2][1] = -s;
        temp1[2][2] = c;

        R_ConcatRotations(temp1, temp3, ref entity_rotation);

        // rotate modelorg and the transformation matrix
        R_EntityRotate(ref modelorg);
        R_EntityRotate(ref vpn);
        R_EntityRotate(ref vright);
        R_EntityRotate(ref vup);

        R_TransformFrustum();
    }

    // r_bsp.c: R_RecursiveClipBPoly
    public void R_RecursiveClipBPoly(BEdge? pedges, MNodeBase pnodebase, MSurface psurf)
    {
        BEdge? psideedges0 = null, psideedges1 = null;
        BEdge? pnextedge;
        BEdge ptedge;
        int i, side, lastside;
        float dist, frac, lastdist;
        MPlane splitplane;
        Vec3 tplanenormal = default;
        float tplanedist;
        int ptvert;
        MNodeBase pn;

        var pnode = (MNode)pnodebase;

        makeclippededge = false;

        // transform the BSP plane into model space
        // FIXME: cache these?
        splitplane = pnode.plane;
        tplanedist = splitplane.dist -
            DotProduct(r_entorigin, splitplane.normal);
        tplanenormal[0] = DotProduct(entity_rotation[0], splitplane.normal);
        tplanenormal[1] = DotProduct(entity_rotation[1], splitplane.normal);
        tplanenormal[2] = DotProduct(entity_rotation[2], splitplane.normal);

        // clip edges to BSP plane
        for (; pedges != null; pedges = pnextedge)
        {
            pnextedge = pedges.pnext;

            // set the status for the last point as the previous point
            // FIXME: cache this stuff somehow?
            ref Vec3 plastvert = ref pedges.v0array[pedges.v0];
            lastdist = DotProduct(plastvert, tplanenormal) -
                tplanedist;

            if (lastdist > 0)
            {
                lastside = 0;
            }
            else
            {
                lastside = 1;
            }

            ref Vec3 pvert = ref pedges.v1array[pedges.v1];

            dist = DotProduct(pvert, tplanenormal) - tplanedist;

            if (dist > 0)
            {
                side = 0;
            }
            else
            {
                side = 1;
            }

            if (side != lastside)
            {
                // clipped
                if (numbverts >= MAX_BMODEL_VERTS)
                {
                    return;
                }

                // generate the clipped vertex
                frac = lastdist / (lastdist - dist);
                ptvert = numbverts++;
                pbverts[ptvert][0] = plastvert[0] +
                    (frac * (pvert[0] -
                    plastvert[0]));
                pbverts[ptvert][1] = plastvert[1] +
                    (frac * (pvert[1] -
                    plastvert[1]));
                pbverts[ptvert][2] = plastvert[2] +
                    (frac * (pvert[2] -
                    plastvert[2]));

                // split into two edges, one on each side, and remember entering and exiting points
                // FIXME: share the clip edge by having a winding direction flag?
                if (numbedges >= (MAX_BMODEL_EDGES - 1))
                {
                    Con_Printf("Out of edges for bmodel\n");
                    return;
                }

                ptedge = pbedges[numbedges];
                ptedge.pnext = lastside == 0 ? psideedges0 : psideedges1;
                if (lastside == 0)
                {
                    psideedges0 = ptedge;
                }
                else
                {
                    psideedges1 = ptedge;
                }

                ptedge.v0array = pedges.v0array;
                ptedge.v0 = pedges.v0;
                ptedge.v1array = pbverts;
                ptedge.v1 = ptvert;

                ptedge = pbedges[numbedges + 1];
                ptedge.pnext = side == 0 ? psideedges0 : psideedges1;
                if (side == 0)
                {
                    psideedges0 = ptedge;
                }
                else
                {
                    psideedges1 = ptedge;
                }

                ptedge.v0array = pbverts;
                ptedge.v0 = ptvert;
                ptedge.v1array = pedges.v1array;
                ptedge.v1 = pedges.v1;

                numbedges += 2;

                if (side == 0)
                {
                    // entering for front, exiting for back
                    pfrontenter = ptvert;
                    makeclippededge = true;
                }
                else
                {
                    pfrontexit = ptvert;
                    makeclippededge = true;
                }
            }
            else
            {
                // add the edge to the appropriate side
                pedges.pnext = side == 0 ? psideedges0 : psideedges1;
                if (side == 0)
                {
                    psideedges0 = pedges;
                }
                else
                {
                    psideedges1 = pedges;
                }
            }
        }

        // if anything was clipped, reconstitute and add the edges along the clip plane to both sides (but in
        // opposite directions)
        if (makeclippededge)
        {
            if (numbedges >= (MAX_BMODEL_EDGES - 2))
            {
                Con_Printf("Out of edges for bmodel\n");
                return;
            }

            ptedge = pbedges[numbedges];
            ptedge.pnext = psideedges0;
            psideedges0 = ptedge;
            ptedge.v0array = pbverts;
            ptedge.v0 = pfrontexit;
            ptedge.v1array = pbverts;
            ptedge.v1 = pfrontenter;

            ptedge = pbedges[numbedges + 1];
            ptedge.pnext = psideedges1;
            psideedges1 = ptedge;
            ptedge.v0array = pbverts;
            ptedge.v0 = pfrontenter;
            ptedge.v1array = pbverts;
            ptedge.v1 = pfrontexit;

            numbedges += 2;
        }

        // draw or recurse further
        for (i = 0; i < 2; i++)
        {
            BEdge? psideedges = i == 0 ? psideedges0 : psideedges1;
            if (psideedges != null)
            {
                // draw if we've reached a non-solid leaf, done if all that's left is a solid leaf, and continue down
                // the tree if it's not a leaf
                pn = pnode.children[i];

                // we're done with this branch if the node or leaf isn't in the PVS
                if (pn.visframe == r_visframecount)
                {
                    if (pn.contents < 0)
                    {
                        if (pn.contents != CONTENTS_SOLID)
                        {
                            r_currentbkey = ((MLeaf)pn).key;
                            R_RenderBmodelFace(psideedges, psurf);
                        }
                    }
                    else
                    {
                        R_RecursiveClipBPoly(psideedges, pnode.children[i],
                            psurf);
                    }
                }
            }
        }
    }

    // r_bsp.c: R_DrawSolidClippedSubmodelPolygons
    public void R_DrawSolidClippedSubmodelPolygons(ModelT pmodel)
    {
        int i, j, lindex;
        float dot;
        MSurface psurf;
        int numsurfaces;
        MPlane pplane;
        BEdge pbedge;
        MEdge[] pedges;

        // FIXME: use bounding-box-based frustum clipping info?
        numsurfaces = pmodel.nummodelsurfaces;
        pedges = pmodel.edges;

        for (i = 0; i < numsurfaces; i++)
        {
            psurf = pmodel.surfaces[pmodel.firstmodelsurface + i];

            // find which side of the node we are on
            pplane = psurf.plane;

            dot = DotProduct(modelorg, pplane.normal) - pplane.dist;

            // draw the polygon
            if (((psurf.flags & MSurface.SURF_PLANEBACK) != 0 && (dot < -BACKFACE_EPSILON)) ||
                ((psurf.flags & MSurface.SURF_PLANEBACK) == 0 && (dot > BACKFACE_EPSILON)))
            {
                // FIXME: use bounding-box-based frustum clipping info?

                // copy the edges to bedges, flipping if necessary so always clockwise winding
                // FIXME: if edges and vertices get caches, these assignments must move outside the loop, and overflow
                // checking must be done here
                numbverts = numbedges = 0;

                if (psurf.numedges > 0)
                {
                    int first = numbedges;
                    numbedges += psurf.numedges;

                    for (j = 0; j < psurf.numedges; j++)
                    {
                        lindex = pmodel.surfedges[psurf.firstedge + j];
                        pbedge = pbedges[first + j];

                        if (lindex > 0)
                        {
                            ref MEdge pedge = ref pedges[lindex];
                            pbedge.v0array = r_pcurrentvertbase;
                            pbedge.v0 = pedge.v0;
                            pbedge.v1array = r_pcurrentvertbase;
                            pbedge.v1 = pedge.v1;
                        }
                        else
                        {
                            lindex = -lindex;
                            ref MEdge pedge = ref pedges[lindex];
                            pbedge.v0array = r_pcurrentvertbase;
                            pbedge.v0 = pedge.v1;
                            pbedge.v1array = r_pcurrentvertbase;
                            pbedge.v1 = pedge.v0;
                        }

                        pbedge.pnext = first + j + 1 < MAX_BMODEL_EDGES ? pbedges[first + j + 1] : null;
                    }

                    pbedges[first + j - 1].pnext = null; // mark end of edges

                    R_RecursiveClipBPoly(pbedges[first], currententity.topnode!, psurf);
                }
                else
                {
                    throw Sys_Error("no edges in bmodel");
                }
            }
        }
    }

    // r_bsp.c: R_DrawSubmodelPolygons
    public void R_DrawSubmodelPolygons(ModelT pmodel, int clipflags)
    {
        int i;
        float dot;
        MSurface psurf;
        int numsurfaces;
        MPlane pplane;

        // FIXME: use bounding-box-based frustum clipping info?
        numsurfaces = pmodel.nummodelsurfaces;

        for (i = 0; i < numsurfaces; i++)
        {
            psurf = pmodel.surfaces[pmodel.firstmodelsurface + i];

            // find which side of the node we are on
            pplane = psurf.plane;

            dot = DotProduct(modelorg, pplane.normal) - pplane.dist;

            // draw the polygon
            if (((psurf.flags & MSurface.SURF_PLANEBACK) != 0 && (dot < -BACKFACE_EPSILON)) ||
                ((psurf.flags & MSurface.SURF_PLANEBACK) == 0 && (dot > BACKFACE_EPSILON)))
            {
                r_currentkey = ((MLeaf)currententity.topnode!).key;

                // FIXME: use bounding-box-based frustum clipping info?
                R_RenderFace(psurf, clipflags);
            }
        }
    }

    // r_bsp.c: R_RecursiveWorldNode
    public void R_RecursiveWorldNode(MNodeBase nodebase, int clipflags)
    {
        int i, c, side, pindex;
        Vec3 acceptpt = default, rejectpt = default;
        MPlane plane;
        MSurface surf;
        MLeaf pleaf;
        double d, dot;

        if (nodebase.contents == CONTENTS_SOLID)
        {
            return; // solid
        }

        if (nodebase.visframe != r_visframecount)
        {
            return;
        }

        // cull the clipping planes if not trivial accept
        // FIXME: the compiler is doing a lousy job of optimizing here; it could be twice as fast in ASM
        if (clipflags != 0)
        {
            for (i = 0; i < 4; i++)
            {
                if ((clipflags & (1 << i)) == 0)
                {
                    continue; // don't need to clip against it
                }

                // generate accept and reject points
                // FIXME: do with fast look-ups or integer tests based on the sign bit of the floating point values
                pindex = pfrustum_indexes[i];

                rejectpt[0] = nodebase.minmaxs[r_frustum_indexes[pindex + 0]];
                rejectpt[1] = nodebase.minmaxs[r_frustum_indexes[pindex + 1]];
                rejectpt[2] = nodebase.minmaxs[r_frustum_indexes[pindex + 2]];

                d = DotProduct(rejectpt, view_clipplanes[i].normal);
                d -= view_clipplanes[i].dist;

                if (d <= 0)
                {
                    return;
                }

                acceptpt[0] = nodebase.minmaxs[r_frustum_indexes[pindex + 3 + 0]];
                acceptpt[1] = nodebase.minmaxs[r_frustum_indexes[pindex + 3 + 1]];
                acceptpt[2] = nodebase.minmaxs[r_frustum_indexes[pindex + 3 + 2]];

                d = DotProduct(acceptpt, view_clipplanes[i].normal);
                d -= view_clipplanes[i].dist;

                if (d >= 0)
                {
                    clipflags &= ~(1 << i); // node is entirely on screen
                }
            }
        }

        // if a leaf node, draw stuff
        if (nodebase.contents < 0)
        {
            pleaf = (MLeaf)nodebase;

            int mark = pleaf.firstmarksurface;
            c = pleaf.nummarksurfaces;

            if (c != 0)
            {
                MSurface[] marksurfaces = cl.worldmodel!.marksurfaces;
                do
                {
                    marksurfaces[mark].visframe = r_framecount;
                    mark++;
                }
                while (--c != 0);
            }

            // deal with model fragments in this leaf
            if (pleaf.efrags != null)
            {
                R_StoreEfrags(pleaf);
            }

            pleaf.key = r_currentkey;
            r_currentkey++; // all bmodels in a leaf share the same key
        }
        else
        {
            var node = (MNode)nodebase;

            // node is just a decision point, so go down the apropriate sides

            // find which side of the node we are on
            plane = node.plane;

            switch (plane.type)
            {
                case PLANE_X:
                    dot = modelorg[0] - plane.dist;
                    break;
                case PLANE_Y:
                    dot = modelorg[1] - plane.dist;
                    break;
                case PLANE_Z:
                    dot = modelorg[2] - plane.dist;
                    break;
                default:
                    dot = DotProduct(modelorg, plane.normal) - plane.dist;
                    break;
            }

            if (dot >= 0)
            {
                side = 0;
            }
            else
            {
                side = 1;
            }

            // recurse down the children, front side first
            R_RecursiveWorldNode(node.children[side], clipflags);

            // draw stuff
            c = node.numsurfaces;

            if (c != 0)
            {
                MSurface[] surfaces = cl.worldmodel!.surfaces;
                int s = node.firstsurface;

                if (dot < -BACKFACE_EPSILON)
                {
                    do
                    {
                        surf = surfaces[s];
                        if ((surf.flags & MSurface.SURF_PLANEBACK) != 0 &&
                            (surf.visframe == r_framecount))
                        {
                            if (r_drawpolys)
                            {
                                if (r_worldpolysbacktofront)
                                {
                                    if (numbtofpolys < MAX_BTOFPOLYS)
                                    {
                                        pbtofpolys[numbtofpolys].clipflags =
                                            clipflags;
                                        pbtofpolys[numbtofpolys].psurf = surf;
                                        numbtofpolys++;
                                    }
                                }
                                else
                                {
                                    R_RenderPoly(surf, clipflags);
                                }
                            }
                            else
                            {
                                R_RenderFace(surf, clipflags);
                            }
                        }

                        s++;
                    }
                    while (--c != 0);
                }
                else if (dot > BACKFACE_EPSILON)
                {
                    do
                    {
                        surf = surfaces[s];
                        if ((surf.flags & MSurface.SURF_PLANEBACK) == 0 &&
                            (surf.visframe == r_framecount))
                        {
                            if (r_drawpolys)
                            {
                                if (r_worldpolysbacktofront)
                                {
                                    if (numbtofpolys < MAX_BTOFPOLYS)
                                    {
                                        pbtofpolys[numbtofpolys].clipflags =
                                            clipflags;
                                        pbtofpolys[numbtofpolys].psurf = surf;
                                        numbtofpolys++;
                                    }
                                }
                                else
                                {
                                    R_RenderPoly(surf, clipflags);
                                }
                            }
                            else
                            {
                                R_RenderFace(surf, clipflags);
                            }
                        }

                        s++;
                    }
                    while (--c != 0);
                }

                // all surfaces on the same node share the same sequence number
                r_currentkey++;
            }

            // recurse down the back side
            R_RecursiveWorldNode(node.children[side == 0 ? 1 : 0], clipflags);
        }
    }

    // r_bsp.c: R_RenderWorld (its btofpolys, on the C's stack, are pbtofpolys)
    public void R_RenderWorld()
    {
        int i;
        ModelT clmodel;

        if (pbtofpolys.Length == 0)
        {
            pbtofpolys = new BtofPoly[MAX_BTOFPOLYS];
        }

        currententity = cl_entities[0];
        VectorCopy(r_origin, ref modelorg);
        clmodel = currententity.model!;
        r_pcurrentvertbase = clmodel.vertexes;

        R_RecursiveWorldNode(clmodel.nodes[0], 15);

        // if the driver wants the polygons back to front, play the visible ones back in that order
        if (r_worldpolysbacktofront)
        {
            for (i = numbtofpolys - 1; i >= 0; i--)
            {
                R_RenderPoly(pbtofpolys[i].psurf, pbtofpolys[i].clipflags);
            }
        }
    }
}
