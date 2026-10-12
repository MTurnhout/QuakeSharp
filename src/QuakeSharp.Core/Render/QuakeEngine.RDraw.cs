using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Render;
using static QuakeSharp.Core.Common.MathLib;
using static QuakeSharp.Core.Model.BspFile;
using ModelT = QuakeSharp.Core.Model.Model;

namespace QuakeSharp.Core;

// r_draw.c: the faces' edges, clipped to the view and emitted into the edge lists. A model edge's cached edge
// offset is the index of the edge it last emitted (C's byte offset in r_edges, in the same order).
public sealed partial class QuakeEngine
{
    private const int MAXLEFTCLIPEDGES = 100;

    // !!! if these are changed, they must be changed in asm_draw.h too !!!
    private const uint FULLY_CLIPPED_CACHED = 0x80000000;
    private const uint FRAMECOUNT_MASK = 0x7FFFFFFF;

    public uint cacheoffset;

    /// <summary>number of faces clipped</summary>
    public int c_faceclip;

    public ZPointDesc r_zpointdesc;

    public readonly PolyDesc r_polydesc = new();

    public ClipPlane? entity_clipplanes;
    public readonly ClipPlane[] view_clipplanes = [new(), new(), new(), new()];
    public readonly ClipPlane[] world_clipplanes = [.. Enumerable.Range(0, 16).Select(_ => new ClipPlane())];

    // r_draw.c: r_pedge (an array of model edges and an index in it); tedge, R_RenderFace's and
    // R_RenderBmodelFace's dummy edge for the caching to write to
    public MEdge[] r_pedge_array = [];
    public int r_pedge;
    private readonly MEdge[] tedge = new MEdge[1];

    public bool r_leftclipped, r_rightclipped;
    private bool makeleftedge, makerightedge;
    public bool r_nearzionly;

    public readonly int[] sintable = new int[SIN_BUFFER_SIZE];
    public readonly int[] intsintable = new int[SIN_BUFFER_SIZE];

    public Vec3 r_leftenter, r_leftexit;
    public Vec3 r_rightenter, r_rightexit;

    public int r_emitted;
    public float r_nearzi;
    public float r_u1, r_v1, r_lzi1;
    public int r_ceilv1;

    public bool r_lastvertvalid;

    // r_draw.c: R_EmitEdge
    public void R_EmitEdge(in Vec3 pv0, in Vec3 pv1)
    {
        Edge edge, pcheck;
        int u_check;
        float u, u_step;
        Vec3 local = default, transformed = default;
        int v, v2, ceilv0;
        float scale, lzi0, u0, v0;
        int side;

        if (r_lastvertvalid)
        {
            u0 = r_u1;
            v0 = r_v1;
            lzi0 = r_lzi1;
            ceilv0 = r_ceilv1;
        }
        else
        {
            // transform and project
            VectorSubtract(pv0, modelorg, ref local);
            TransformVector(local, ref transformed);

            if (transformed[2] < NEAR_CLIP)
            {
                transformed[2] = (float)NEAR_CLIP;
            }

            lzi0 = (float)(1.0 / transformed[2]);

            // FIXME: build x/yscale into transform?
            scale = xscale * lzi0;
            u0 = xcenter + (scale * transformed[0]);
            if (u0 < r_refdef.fvrectx_adj)
            {
                u0 = r_refdef.fvrectx_adj;
            }

            if (u0 > r_refdef.fvrectright_adj)
            {
                u0 = r_refdef.fvrectright_adj;
            }

            scale = yscale * lzi0;
            v0 = ycenter - (scale * transformed[1]);
            if (v0 < r_refdef.fvrecty_adj)
            {
                v0 = r_refdef.fvrecty_adj;
            }

            if (v0 > r_refdef.fvrectbottom_adj)
            {
                v0 = r_refdef.fvrectbottom_adj;
            }

            ceilv0 = X86.FtoI(Math.Ceiling(v0));
        }

        // transform and project
        VectorSubtract(pv1, modelorg, ref local);
        TransformVector(local, ref transformed);

        if (transformed[2] < NEAR_CLIP)
        {
            transformed[2] = (float)NEAR_CLIP;
        }

        r_lzi1 = (float)(1.0 / transformed[2]);

        scale = xscale * r_lzi1;
        r_u1 = xcenter + (scale * transformed[0]);
        if (r_u1 < r_refdef.fvrectx_adj)
        {
            r_u1 = r_refdef.fvrectx_adj;
        }

        if (r_u1 > r_refdef.fvrectright_adj)
        {
            r_u1 = r_refdef.fvrectright_adj;
        }

        scale = yscale * r_lzi1;
        r_v1 = ycenter - (scale * transformed[1]);
        if (r_v1 < r_refdef.fvrecty_adj)
        {
            r_v1 = r_refdef.fvrecty_adj;
        }

        if (r_v1 > r_refdef.fvrectbottom_adj)
        {
            r_v1 = r_refdef.fvrectbottom_adj;
        }

        if (r_lzi1 > lzi0)
        {
            lzi0 = r_lzi1;
        }

        if (lzi0 > r_nearzi)
        {
            // for mipmap finding
            r_nearzi = lzi0;
        }

        // for right edges, all we want is the effect on 1/z
        if (r_nearzionly)
        {
            return;
        }

        r_emitted = 1;

        r_ceilv1 = X86.FtoI(Math.Ceiling(r_v1));

        // create the edge
        if (ceilv0 == r_ceilv1)
        {
            // we cache unclipped horizontal edges as fully clipped
            if (cacheoffset != 0x7FFFFFFF)
            {
                cacheoffset = FULLY_CLIPPED_CACHED |
                    ((uint)r_framecount & FRAMECOUNT_MASK);
            }

            return; // horizontal edge
        }

        side = ceilv0 > r_ceilv1 ? 1 : 0;

        edge = r_edges[edge_p++];

        edge.ownerarray = r_pedge_array;
        edge.ownerindex = r_pedge;

        edge.nearzi = lzi0;

        if (side == 0)
        {
            // trailing edge (go from p1 to p2)
            v = ceilv0;
            v2 = r_ceilv1 - 1;

            edge.surfs[0] = (ushort)surface_p;
            edge.surfs[1] = 0;

            u_step = (r_u1 - u0) / (r_v1 - v0);
            u = u0 + (((float)v - v0) * u_step);
        }
        else
        {
            // leading edge (go from p2 to p1)
            v2 = ceilv0 - 1;
            v = r_ceilv1;

            edge.surfs[0] = 0;
            edge.surfs[1] = (ushort)surface_p;

            u_step = (u0 - r_u1) / (v0 - r_v1);
            u = r_u1 + (((float)v - r_v1) * u_step);
        }

        edge.u_step = X86.FtoI(u_step * 0x100000);
        edge.u = X86.FtoI((u * 0x100000) + 0xFFFFF);

        // we need to do this to avoid stepping off the edges if a very nearly horizontal edge is less than epsilon
        // above a scan, and numeric error causes it to incorrectly extend to the scan, and the extension of the line
        // goes off the edge of the screen
        // FIXME: is this actually needed?
        if (edge.u < r_refdef.vrect_x_adj_shift20)
        {
            edge.u = r_refdef.vrect_x_adj_shift20;
        }

        if (edge.u > r_refdef.vrectright_adj_shift20)
        {
            edge.u = r_refdef.vrectright_adj_shift20;
        }

        // sort the edge in normally
        u_check = edge.u;
        if (edge.surfs[0] != 0)
        {
            u_check++; // sort trailers after leaders
        }

        if (newedges[v] == null || newedges[v]!.u >= u_check)
        {
            edge.next = newedges[v];
            newedges[v] = edge;
        }
        else
        {
            pcheck = newedges[v]!;
            while (pcheck.next != null && pcheck.next.u < u_check)
            {
                pcheck = pcheck.next;
            }

            edge.next = pcheck.next;
            pcheck.next = edge;
        }

        edge.nextremove = removeedges[v2];
        removeedges[v2] = edge;
    }

    // r_draw.c: R_ClipEdge
    public void R_ClipEdge(in Vec3 pv0, in Vec3 pv1, ClipPlane? clip)
    {
        float d0, d1, f;
        Vec3 clipvert = default;

        if (clip != null)
        {
            do
            {
                d0 = DotProduct(pv0, clip.normal) - clip.dist;
                d1 = DotProduct(pv1, clip.normal) - clip.dist;

                if (d0 >= 0)
                {
                    // point 0 is unclipped
                    if (d1 >= 0)
                    {
                        // both points are unclipped
                        continue;
                    }

                    // only point 1 is clipped

                    // we don't cache clipped edges
                    cacheoffset = 0x7FFFFFFF;

                    f = d0 / (d0 - d1);
                    clipvert[0] = pv0[0] +
                        (f * (pv1[0] - pv0[0]));
                    clipvert[1] = pv0[1] +
                        (f * (pv1[1] - pv0[1]));
                    clipvert[2] = pv0[2] +
                        (f * (pv1[2] - pv0[2]));

                    if (clip.leftedge)
                    {
                        r_leftclipped = true;
                        r_leftexit = clipvert;
                    }
                    else if (clip.rightedge)
                    {
                        r_rightclipped = true;
                        r_rightexit = clipvert;
                    }

                    R_ClipEdge(pv0, clipvert, clip.next);
                    return;
                }
                else
                {
                    // point 0 is clipped
                    if (d1 < 0)
                    {
                        // both points are clipped
                        // we do cache fully clipped edges
                        if (!r_leftclipped)
                        {
                            cacheoffset = FULLY_CLIPPED_CACHED |
                                ((uint)r_framecount & FRAMECOUNT_MASK);
                        }

                        return;
                    }

                    // only point 0 is clipped
                    r_lastvertvalid = false;

                    // we don't cache partially clipped edges
                    cacheoffset = 0x7FFFFFFF;

                    f = d0 / (d0 - d1);
                    clipvert[0] = pv0[0] +
                        (f * (pv1[0] - pv0[0]));
                    clipvert[1] = pv0[1] +
                        (f * (pv1[1] - pv0[1]));
                    clipvert[2] = pv0[2] +
                        (f * (pv1[2] - pv0[2]));

                    if (clip.leftedge)
                    {
                        r_leftclipped = true;
                        r_leftenter = clipvert;
                    }
                    else if (clip.rightedge)
                    {
                        r_rightclipped = true;
                        r_rightenter = clipvert;
                    }

                    R_ClipEdge(clipvert, pv1, clip.next);
                    return;
                }
            }
            while ((clip = clip.next) != null);
        }

        // add the edge
        R_EmitEdge(pv0, pv1);
    }

    // r_draw.c: R_EmitCachedEdge
    public void R_EmitCachedEdge()
    {
        Edge pedge_t;

        pedge_t = r_edges[r_pedge_array[r_pedge].cachededgeoffset];

        if (pedge_t.surfs[0] == 0)
        {
            pedge_t.surfs[0] = (ushort)surface_p;
        }
        else
        {
            pedge_t.surfs[1] = (ushort)surface_p;
        }

        if (pedge_t.nearzi > r_nearzi)
        {
            // for mipmap finding
            r_nearzi = pedge_t.nearzi;
        }

        r_emitted = 1;
    }

    // r_draw.c: R_RenderFace
    public void R_RenderFace(MSurface fa, int clipflags)
    {
        int i, lindex;
        uint mask;
        MPlane pplane;
        float distinv;
        Vec3 p_normal = default;
        MEdge[] pedges;
        ClipPlane? pclip;

        // skip out if no more surfs
        if (surface_p >= surf_max)
        {
            r_outofsurfaces++;
            return;
        }

        // ditto if not enough edges left, or switch to auxedges if possible
        if ((edge_p + fa.numedges + 4) >= edge_max)
        {
            r_outofedges += fa.numedges;
            return;
        }

        c_faceclip++;

        // set up clip planes
        pclip = null;

        for (i = 3, mask = 0x08; i >= 0; i--, mask >>= 1)
        {
            if ((clipflags & mask) != 0)
            {
                view_clipplanes[i].next = pclip;
                pclip = view_clipplanes[i];
            }
        }

        // push the edges through
        r_emitted = 0;
        r_nearzi = 0;
        r_nearzionly = false;
        makeleftedge = makerightedge = false;
        ModelT model = currententity.model!;
        pedges = model.edges;
        r_lastvertvalid = false;

        for (i = 0; i < fa.numedges; i++)
        {
            lindex = model.surfedges[fa.firstedge + i];

            if (lindex > 0)
            {
                r_pedge_array = pedges;
                r_pedge = lindex;

                // if the edge is cached, we can just reuse the edge
                if (!insubmodel)
                {
                    if ((pedges[lindex].cachededgeoffset & FULLY_CLIPPED_CACHED) != 0)
                    {
                        if ((pedges[lindex].cachededgeoffset & FRAMECOUNT_MASK) ==
                            r_framecount)
                        {
                            r_lastvertvalid = false;
                            continue;
                        }
                    }
                    else
                    {
                        if (((uint)edge_p > pedges[lindex].cachededgeoffset) &&
                            r_edges[pedges[lindex].cachededgeoffset].ownerarray == pedges &&
                            r_edges[pedges[lindex].cachededgeoffset].ownerindex == lindex)
                        {
                            R_EmitCachedEdge();
                            r_lastvertvalid = false;
                            continue;
                        }
                    }
                }

                // assume it's cacheable
                cacheoffset = (uint)edge_p;
                r_leftclipped = r_rightclipped = false;
                R_ClipEdge(r_pcurrentvertbase[pedges[lindex].v0],
                    r_pcurrentvertbase[pedges[lindex].v1],
                    pclip);
                pedges[lindex].cachededgeoffset = cacheoffset;

                if (r_leftclipped)
                {
                    makeleftedge = true;
                }

                if (r_rightclipped)
                {
                    makerightedge = true;
                }

                r_lastvertvalid = true;
            }
            else
            {
                lindex = -lindex;
                r_pedge_array = pedges;
                r_pedge = lindex;

                // if the edge is cached, we can just reuse the edge
                if (!insubmodel)
                {
                    if ((pedges[lindex].cachededgeoffset & FULLY_CLIPPED_CACHED) != 0)
                    {
                        if ((pedges[lindex].cachededgeoffset & FRAMECOUNT_MASK) ==
                            r_framecount)
                        {
                            r_lastvertvalid = false;
                            continue;
                        }
                    }
                    else
                    {
                        // it's cached if the cached edge is valid and is owned by this medge_t
                        if (((uint)edge_p > pedges[lindex].cachededgeoffset) &&
                            r_edges[pedges[lindex].cachededgeoffset].ownerarray == pedges &&
                            r_edges[pedges[lindex].cachededgeoffset].ownerindex == lindex)
                        {
                            R_EmitCachedEdge();
                            r_lastvertvalid = false;
                            continue;
                        }
                    }
                }

                // assume it's cacheable
                cacheoffset = (uint)edge_p;
                r_leftclipped = r_rightclipped = false;
                R_ClipEdge(r_pcurrentvertbase[pedges[lindex].v1],
                    r_pcurrentvertbase[pedges[lindex].v0],
                    pclip);
                pedges[lindex].cachededgeoffset = cacheoffset;

                if (r_leftclipped)
                {
                    makeleftedge = true;
                }

                if (r_rightclipped)
                {
                    makerightedge = true;
                }

                r_lastvertvalid = true;
            }
        }

        // if there was a clip off the left edge, add that edge too
        // FIXME: faster to do in screen space?
        // FIXME: share clipped edges?
        if (makeleftedge)
        {
            r_pedge_array = tedge;
            r_pedge = 0;
            r_lastvertvalid = false;
            R_ClipEdge(r_leftexit, r_leftenter, pclip!.next);
        }

        // if there was a clip off the right edge, get the right r_nearzi
        if (makerightedge)
        {
            r_pedge_array = tedge;
            r_pedge = 0;
            r_lastvertvalid = false;
            r_nearzionly = true;
            R_ClipEdge(r_rightexit, r_rightenter, view_clipplanes[1].next);
        }

        // if no edges made it out, return without posting the surface
        if (r_emitted == 0)
        {
            return;
        }

        r_polycount++;

        Surf surf = surfaces[surface_p];
        surf.data = fa;
        surf.nearzi = r_nearzi;
        surf.flags = fa.flags;
        surf.insubmodel = insubmodel;
        surf.spanstate = 0;
        surf.entity = currententity;
        surf.key = r_currentkey++;
        surf.spans = null;

        pplane = fa.plane;

        // FIXME: cache this?
        TransformVector(pplane.normal, ref p_normal);

        // FIXME: cache this?
        distinv = (float)(1.0 / (pplane.dist - DotProduct(modelorg, pplane.normal)));

        surf.d_zistepu = p_normal[0] * xscaleinv * distinv;
        surf.d_zistepv = -p_normal[1] * yscaleinv * distinv;
        surf.d_ziorigin = (p_normal[2] * distinv) -
            (xcenter * surf.d_zistepu) -
            (ycenter * surf.d_zistepv);

        surface_p++;
    }

    // r_draw.c: R_RenderBmodelFace
    public void R_RenderBmodelFace(BEdge? pedges, MSurface psurf)
    {
        int i;
        uint mask;
        MPlane pplane;
        float distinv;
        Vec3 p_normal = default;
        ClipPlane? pclip;

        // skip out if no more surfs
        if (surface_p >= surf_max)
        {
            r_outofsurfaces++;
            return;
        }

        // ditto if not enough edges left, or switch to auxedges if possible
        if ((edge_p + psurf.numedges + 4) >= edge_max)
        {
            r_outofedges += psurf.numedges;
            return;
        }

        c_faceclip++;

        // this is a dummy to give the caching mechanism someplace to write to
        r_pedge_array = tedge;
        r_pedge = 0;

        // set up clip planes
        pclip = null;

        for (i = 3, mask = 0x08; i >= 0; i--, mask >>= 1)
        {
            if ((r_clipflags & mask) != 0)
            {
                view_clipplanes[i].next = pclip;
                pclip = view_clipplanes[i];
            }
        }

        // push the edges through
        r_emitted = 0;
        r_nearzi = 0;
        r_nearzionly = false;
        makeleftedge = makerightedge = false;

        // FIXME: keep clipped bmodel edges in clockwise order so last vertex caching can be used?
        r_lastvertvalid = false;

        for (; pedges != null; pedges = pedges.pnext)
        {
            r_leftclipped = r_rightclipped = false;
            R_ClipEdge(pedges.v0array[pedges.v0], pedges.v1array[pedges.v1], pclip);

            if (r_leftclipped)
            {
                makeleftedge = true;
            }

            if (r_rightclipped)
            {
                makerightedge = true;
            }
        }

        // if there was a clip off the left edge, add that edge too
        // FIXME: faster to do in screen space?
        // FIXME: share clipped edges?
        if (makeleftedge)
        {
            r_pedge_array = tedge;
            r_pedge = 0;
            R_ClipEdge(r_leftexit, r_leftenter, pclip!.next);
        }

        // if there was a clip off the right edge, get the right r_nearzi
        if (makerightedge)
        {
            r_pedge_array = tedge;
            r_pedge = 0;
            r_nearzionly = true;
            R_ClipEdge(r_rightexit, r_rightenter, view_clipplanes[1].next);
        }

        // if no edges made it out, return without posting the surface
        if (r_emitted == 0)
        {
            return;
        }

        r_polycount++;

        Surf surf = surfaces[surface_p];
        surf.data = psurf;
        surf.nearzi = r_nearzi;
        surf.flags = psurf.flags;
        surf.insubmodel = true;
        surf.spanstate = 0;
        surf.entity = currententity;
        surf.key = r_currentbkey;
        surf.spans = null;

        pplane = psurf.plane;

        // FIXME: cache this?
        TransformVector(pplane.normal, ref p_normal);

        // FIXME: cache this?
        distinv = (float)(1.0 / (pplane.dist - DotProduct(modelorg, pplane.normal)));

        surf.d_zistepu = p_normal[0] * xscaleinv * distinv;
        surf.d_zistepv = -p_normal[1] * yscaleinv * distinv;
        surf.d_ziorigin = (p_normal[2] * distinv) -
            (xcenter * surf.d_zistepu) -
            (ycenter * surf.d_zistepv);

        surface_p++;
    }

    // r_draw.c: R_RenderPoly's verts and pverts (on the C's stack)
    private readonly Vec3[][] r_renderpoly_verts = [new Vec3[100], new Vec3[100]];
    private readonly PolyVert[] r_renderpoly_pverts = new PolyVert[100];

    // r_draw.c: R_RenderPoly (for a driver that wants polygons: WinQuake's never does, r_drawpolys is false)
    public void R_RenderPoly(MSurface fa, int clipflags)
    {
        int i, lindex, lnumverts, s_axis, t_axis;
        float dist, lastdist, lzi, scale, u, v, frac;
        uint mask;
        Vec3 local = default, transformed = default;
        ClipPlane? pclip;
        MEdge[] pedges;
        MPlane pplane;
        Vec3[][] verts = r_renderpoly_verts;
        PolyVert[] pverts = r_renderpoly_pverts;
        int vertpage, newverts, newpage, lastvert;
        bool visible;

        // FIXME: clean this up and make it faster
        // FIXME: guard against running out of vertices
        s_axis = t_axis = 0; // keep compiler happy

        // set up clip planes
        pclip = null;

        for (i = 3, mask = 0x08; i >= 0; i--, mask >>= 1)
        {
            if ((clipflags & mask) != 0)
            {
                view_clipplanes[i].next = pclip;
                pclip = view_clipplanes[i];
            }
        }

        // reconstruct the polygon
        // FIXME: these should be precalculated and loaded off disk
        ModelT model = currententity.model!;
        pedges = model.edges;
        lnumverts = fa.numedges;
        vertpage = 0;

        for (i = 0; i < lnumverts; i++)
        {
            lindex = model.surfedges[fa.firstedge + i];

            if (lindex > 0)
            {
                r_pedge_array = pedges;
                r_pedge = lindex;
                verts[0][i] = r_pcurrentvertbase[pedges[lindex].v0];
            }
            else
            {
                r_pedge_array = pedges;
                r_pedge = -lindex;
                verts[0][i] = r_pcurrentvertbase[pedges[-lindex].v1];
            }
        }

        // clip the polygon, done if not visible
        while (pclip != null)
        {
            lastvert = lnumverts - 1;
            lastdist = DotProduct(verts[vertpage][lastvert], pclip.normal) - pclip.dist;

            visible = false;
            newverts = 0;
            newpage = vertpage ^ 1;

            for (i = 0; i < lnumverts; i++)
            {
                dist = DotProduct(verts[vertpage][i], pclip.normal) -
                    pclip.dist;

                if ((lastdist > 0) != (dist > 0))
                {
                    frac = dist / (dist - lastdist);
                    verts[newpage][newverts][0] =
                        verts[vertpage][i][0] +
                        ((verts[vertpage][lastvert][0] -
                          verts[vertpage][i][0]) * frac);
                    verts[newpage][newverts][1] =
                        verts[vertpage][i][1] +
                        ((verts[vertpage][lastvert][1] -
                          verts[vertpage][i][1]) * frac);
                    verts[newpage][newverts][2] =
                        verts[vertpage][i][2] +
                        ((verts[vertpage][lastvert][2] -
                          verts[vertpage][i][2]) * frac);
                    newverts++;
                }

                if (dist >= 0)
                {
                    verts[newpage][newverts] = verts[vertpage][i];
                    newverts++;
                    visible = true;
                }

                lastvert = i;
                lastdist = dist;
            }

            if (!visible || (newverts < 3))
            {
                return;
            }

            lnumverts = newverts;
            vertpage ^= 1;
            pclip = pclip.next;
        }

        // transform and project, remembering the z values at the vertices and r_nearzi, and extract the s and t
        // coordinates at the vertices
        pplane = fa.plane;
        switch (pplane.type)
        {
            case PLANE_X:
            case PLANE_ANYX:
                s_axis = 1;
                t_axis = 2;
                break;
            case PLANE_Y:
            case PLANE_ANYY:
                s_axis = 0;
                t_axis = 2;
                break;
            case PLANE_Z:
            case PLANE_ANYZ:
                s_axis = 0;
                t_axis = 1;
                break;
        }

        r_nearzi = 0;

        for (i = 0; i < lnumverts; i++)
        {
            // transform and project
            VectorSubtract(verts[vertpage][i], modelorg, ref local);
            TransformVector(local, ref transformed);

            if (transformed[2] < NEAR_CLIP)
            {
                transformed[2] = (float)NEAR_CLIP;
            }

            lzi = (float)(1.0 / transformed[2]);

            if (lzi > r_nearzi)
            {
                // for mipmap finding
                r_nearzi = lzi;
            }

            // FIXME: build x/yscale into transform?
            scale = xscale * lzi;
            u = xcenter + (scale * transformed[0]);
            if (u < r_refdef.fvrectx_adj)
            {
                u = r_refdef.fvrectx_adj;
            }

            if (u > r_refdef.fvrectright_adj)
            {
                u = r_refdef.fvrectright_adj;
            }

            scale = yscale * lzi;
            v = ycenter - (scale * transformed[1]);
            if (v < r_refdef.fvrecty_adj)
            {
                v = r_refdef.fvrecty_adj;
            }

            if (v > r_refdef.fvrectbottom_adj)
            {
                v = r_refdef.fvrectbottom_adj;
            }

            pverts[i].u = u;
            pverts[i].v = v;
            pverts[i].zi = lzi;
            pverts[i].s = verts[vertpage][i][s_axis];
            pverts[i].t = verts[vertpage][i][t_axis];
        }

        // build the polygon descriptor, including fa, r_nearzi, and u, v, s, t, and z for each vertex
        r_polydesc.numverts = lnumverts;
        r_polydesc.nearzi = r_nearzi;
        r_polydesc.pcurrentface = fa;
        r_polydesc.pverts = pverts;

        // draw the polygon
        D_DrawPoly();
    }

    // r_draw.c: R_ZDrawSubmodelPolys
    public void R_ZDrawSubmodelPolys(ModelT pmodel)
    {
        int i, numsurfaces;
        MSurface psurf;
        float dot;
        MPlane pplane;

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
                // FIXME: use bounding-box-based frustum clipping info?
                R_RenderPoly(psurf, 15);
            }
        }
    }
}
