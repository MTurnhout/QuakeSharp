using QuakeSharp.Core.Model;
using QuakeSharp.Core.Render;

namespace QuakeSharp.Core;

// r_edge.c: the active edge table, which turns the frame's edges into the spans of the surfaces in front. Edges,
// surfaces and spans are arrays (on the C's stack or hunk); C's pointers into them are indices.
public sealed partial class QuakeEngine
{
    /// <summary>r_edge.c: r_edges (R_EdgeDrawing's ledges, or auxedges), edge_p, edge_max</summary>
    public Edge[] r_edges = [];

    public int edge_p, edge_max;

    /// <summary>
    /// r_edge.c: surfaces, surface_p, surf_max. Surfaces are generated in back to front order by the bsp, so if a
    /// surf pointer is greater than another one, it should be drawn in front. surfaces[1] is the background, and is
    /// used as the active surface stack. surfaces[0] is a dummy, because index 0 is used to indicate no surface
    /// attached to an edge_t
    /// </summary>
    public Surf[] surfaces = [];

    public int surface_p, surf_max;

    public readonly Edge?[] newedges = new Edge?[MAXHEIGHT];
    public readonly Edge?[] removeedges = new Edge?[MAXHEIGHT];

    // r_edge.c: span_p, max_span_p (indices in R_ScanEdges's basespans, on the C's stack)
    private readonly Espan[] basespans = [.. Enumerable.Range(0, MAXSPANS).Select(_ => new Espan())];
    public int span_p, max_span_p;

    public int r_currentkey;

    public int current_iv;

    public int edge_head_u_shift20, edge_tail_u_shift20;

    // r_edge.c: pdrawfunc (R_GenerateSpans or R_GenerateSpansBackward)
    private bool pdrawfunc_backward;

    public readonly Edge edge_head = new();
    public readonly Edge edge_tail = new();
    public readonly Edge edge_aftertail = new();
    public readonly Edge edge_sentinel = new();

    public float fv;

    /// <summary>r_vars.c: r_bmodelactive</summary>
    public int r_bmodelactive;

    // =============================================================================

    // r_edge.c: R_DrawCulledPolys
    public void R_DrawCulledPolys()
    {
        Surf s;
        MSurface pface;
        int i;

        currententity = cl_entities[0];

        if (r_worldpolysbacktofront)
        {
            for (i = surface_p - 1; i > 1; i--)
            {
                s = surfaces[i];
                if (s.spans == null)
                {
                    continue;
                }

                if ((s.flags & MSurface.SURF_DRAWBACKGROUND) == 0)
                {
                    pface = s.data!;
                    R_RenderPoly(pface, 15);
                }
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

                if ((s.flags & MSurface.SURF_DRAWBACKGROUND) == 0)
                {
                    pface = s.data!;
                    R_RenderPoly(pface, 15);
                }
            }
        }
    }

    // r_edge.c: R_BeginEdgeFrame
    public void R_BeginEdgeFrame()
    {
        int v;

        edge_p = 0;
        edge_max = r_numallocatededges;

        surface_p = 2; // background is surface 1, surface 0 is a dummy
        surfaces[1].spans = null; // no background spans yet
        surfaces[1].flags = MSurface.SURF_DRAWBACKGROUND;

        // put the background behind everything in the world
        if (r_draworder.value != 0)
        {
            pdrawfunc_backward = true;
            surfaces[1].key = 0;
            r_currentkey = 1;
        }
        else
        {
            pdrawfunc_backward = false;
            surfaces[1].key = 0x7FFFFFFF;
            r_currentkey = 0;
        }

        // FIXME: set with memset
        for (v = r_refdef.vrect.y; v < r_refdef.vrectbottom; v++)
        {
            newedges[v] = removeedges[v] = null;
        }
    }

    // r_edge.c: R_InsertNewEdges
    // Adds the edges in the linked list edgestoadd, adding them to the edges in the linked list edgelist. edgestoadd
    // is assumed to be sorted on u, and non-empty (this is actually newedges[v]). edgelist is assumed to be sorted on
    // u, with a sentinel at the end (actually, this is the active edge table starting at edge_head.next).
    public static void R_InsertNewEdges(Edge? edgestoadd, Edge edgelist)
    {
        Edge? next_edge;

        do
        {
            next_edge = edgestoadd!.next;
        edgesearch:
            if (edgelist.u >= edgestoadd.u)
            {
                goto addedge;
            }

            edgelist = edgelist.next!;
            if (edgelist.u >= edgestoadd.u)
            {
                goto addedge;
            }

            edgelist = edgelist.next!;
            if (edgelist.u >= edgestoadd.u)
            {
                goto addedge;
            }

            edgelist = edgelist.next!;
            if (edgelist.u >= edgestoadd.u)
            {
                goto addedge;
            }

            edgelist = edgelist.next!;
            goto edgesearch;

        // insert edgestoadd before edgelist
        addedge:
            edgestoadd.next = edgelist;
            edgestoadd.prev = edgelist.prev;
            edgelist.prev!.next = edgestoadd;
            edgelist.prev = edgestoadd;
        }
        while ((edgestoadd = next_edge) != null);
    }

    // r_edge.c: R_RemoveEdges
    public static void R_RemoveEdges(Edge? pedge)
    {
        do
        {
            pedge!.next!.prev = pedge.prev;
            pedge.prev!.next = pedge.next;
        }
        while ((pedge = pedge.nextremove) != null);
    }

    // r_edge.c: R_StepActiveU
    public void R_StepActiveU(Edge pedge)
    {
        Edge pnext_edge, pwedge;

        while (true)
        {
        nextedge:
            pedge.u += pedge.u_step;
            if (pedge.u < pedge.prev!.u)
            {
                goto pushback;
            }

            pedge = pedge.next!;

            pedge.u += pedge.u_step;
            if (pedge.u < pedge.prev!.u)
            {
                goto pushback;
            }

            pedge = pedge.next!;

            pedge.u += pedge.u_step;
            if (pedge.u < pedge.prev!.u)
            {
                goto pushback;
            }

            pedge = pedge.next!;

            pedge.u += pedge.u_step;
            if (pedge.u < pedge.prev!.u)
            {
                goto pushback;
            }

            pedge = pedge.next!;

            goto nextedge;

        pushback:
            if (pedge == edge_aftertail)
            {
                return;
            }

            // push it back to keep it sorted
            pnext_edge = pedge.next!;

            // pull the edge out of the edge list
            pedge.next!.prev = pedge.prev;
            pedge.prev.next = pedge.next;

            // find out where the edge goes in the edge list
            pwedge = pedge.prev.prev!;

            while (pwedge.u > pedge.u)
            {
                pwedge = pwedge.prev!;
            }

            // put the edge back into the edge list
            pedge.next = pwedge.next;
            pedge.prev = pwedge;
            pedge.next!.prev = pedge;
            pwedge.next = pedge;

            pedge = pnext_edge;
            if (pedge == edge_tail)
            {
                return;
            }
        }
    }

    // r_edge.c: R_CleanupSpan
    public void R_CleanupSpan()
    {
        Surf surf;
        int iu;
        Espan span;

        // now that we've reached the right edge of the screen, we're done with any unfinished surfaces, so emit a
        // span for whatever's on top
        surf = surfaces[1].next!;
        iu = edge_tail_u_shift20;
        if (iu > surf.last_u)
        {
            span = basespans[span_p++];
            span.u = surf.last_u;
            span.count = iu - span.u;
            span.v = current_iv;
            span.pnext = surf.spans;
            surf.spans = span;
        }

        // reset spanstate for all surfaces in the surface stack
        do
        {
            surf.spanstate = 0;
            surf = surf.next!;
        }
        while (surf != surfaces[1]);
    }

    // r_edge.c: R_LeadingEdgeBackwards
    public void R_LeadingEdgeBackwards(Edge edge)
    {
        Espan span;
        Surf surf, surf2;
        int iu;

        // it's adding a new surface in, so find the correct place
        surf = surfaces[edge.surfs[1]];

        // don't start a span if this is an inverted span, with the end edge preceding the start edge (that is, we've
        // already seen the end edge)
        if (++surf.spanstate == 1)
        {
            surf2 = surfaces[1].next!;

            if (surf.key > surf2.key)
            {
                goto newtop;
            }

            // if it's two surfaces on the same plane, the one that's already active is in front, so keep going
            // unless it's a bmodel
            if (surf.insubmodel && (surf.key == surf2.key))
            {
                // must be two bmodels in the same leaf; don't care, because they'll never be farthest anyway
                goto newtop;
            }

        continue_search:

            do
            {
                surf2 = surf2.next!;
            }
            while (surf.key < surf2.key);

            if (surf.key == surf2.key)
            {
                // if it's two surfaces on the same plane, the one that's already active is in front, so keep going
                // unless it's a bmodel
                if (!surf.insubmodel)
                {
                    goto continue_search;
                }

                // must be two bmodels in the same leaf; don't care which is really in front, because they'll never
                // be farthest anyway
            }

            goto gotposition;

        newtop:
            // emit a span (obscures current top)
            iu = edge.u >> 20;

            if (iu > surf2.last_u)
            {
                span = basespans[span_p++];
                span.u = surf2.last_u;
                span.count = iu - span.u;
                span.v = current_iv;
                span.pnext = surf2.spans;
                surf2.spans = span;
            }

            // set last_u on the new span
            surf.last_u = iu;

        gotposition:
            // insert before surf2
            surf.next = surf2;
            surf.prev = surf2.prev;
            surf2.prev!.next = surf;
            surf2.prev = surf;
        }
    }

    // r_edge.c: R_TrailingEdge
    public void R_TrailingEdge(Surf surf, Edge edge)
    {
        Espan span;
        int iu;

        // don't generate a span if this is an inverted span, with the end edge preceding the start edge (that is, we
        // haven't seen the start edge yet)
        if (--surf.spanstate == 0)
        {
            if (surf.insubmodel)
            {
                r_bmodelactive--;
            }

            if (surf == surfaces[1].next)
            {
                // emit a span (current top going away)
                iu = edge.u >> 20;
                if (iu > surf.last_u)
                {
                    span = basespans[span_p++];
                    span.u = surf.last_u;
                    span.count = iu - span.u;
                    span.v = current_iv;
                    span.pnext = surf.spans;
                    surf.spans = span;
                }

                // set last_u on the surface below
                surf.next!.last_u = iu;
            }

            surf.prev!.next = surf.next;
            surf.next!.prev = surf.prev;
        }
    }

    // r_edge.c: R_LeadingEdge
    public void R_LeadingEdge(Edge edge)
    {
        Espan span;
        Surf surf, surf2;
        int iu;
        double fu, newzi, testzi, newzitop, newzibottom;

        if (edge.surfs[1] != 0)
        {
            // it's adding a new surface in, so find the correct place
            surf = surfaces[edge.surfs[1]];

            // don't start a span if this is an inverted span, with the end edge preceding the start edge (that is,
            // we've already seen the end edge)
            if (++surf.spanstate == 1)
            {
                if (surf.insubmodel)
                {
                    r_bmodelactive++;
                }

                surf2 = surfaces[1].next!;

                if (surf.key < surf2.key)
                {
                    goto newtop;
                }

                // if it's two surfaces on the same plane, the one that's already active is in front, so keep going
                // unless it's a bmodel
                if (surf.insubmodel && (surf.key == surf2.key))
                {
                    // must be two bmodels in the same leaf; sort on 1/z
                    fu = (float)(edge.u - 0xFFFFF) * (1.0 / 0x100000);
                    newzi = surf.d_ziorigin + (fv * surf.d_zistepv) +
                        (fu * surf.d_zistepu);
                    newzibottom = newzi * 0.99;

                    testzi = surf2.d_ziorigin + (fv * surf2.d_zistepv) +
                        (fu * surf2.d_zistepu);

                    if (newzibottom >= testzi)
                    {
                        goto newtop;
                    }

                    newzitop = newzi * 1.01;
                    if (newzitop >= testzi)
                    {
                        if (surf.d_zistepu >= surf2.d_zistepu)
                        {
                            goto newtop;
                        }
                    }
                }

            continue_search:

                do
                {
                    surf2 = surf2.next!;
                }
                while (surf.key > surf2.key);

                if (surf.key == surf2.key)
                {
                    // if it's two surfaces on the same plane, the one that's already active is in front, so keep
                    // going unless it's a bmodel
                    if (!surf.insubmodel)
                    {
                        goto continue_search;
                    }

                    // must be two bmodels in the same leaf; sort on 1/z
                    fu = (float)(edge.u - 0xFFFFF) * (1.0 / 0x100000);
                    newzi = surf.d_ziorigin + (fv * surf.d_zistepv) +
                        (fu * surf.d_zistepu);
                    newzibottom = newzi * 0.99;

                    testzi = surf2.d_ziorigin + (fv * surf2.d_zistepv) +
                        (fu * surf2.d_zistepu);

                    if (newzibottom >= testzi)
                    {
                        goto gotposition;
                    }

                    newzitop = newzi * 1.01;
                    if (newzitop >= testzi)
                    {
                        if (surf.d_zistepu >= surf2.d_zistepu)
                        {
                            goto gotposition;
                        }
                    }

                    goto continue_search;
                }

                goto gotposition;

            newtop:
                // emit a span (obscures current top)
                iu = edge.u >> 20;

                if (iu > surf2.last_u)
                {
                    span = basespans[span_p++];
                    span.u = surf2.last_u;
                    span.count = iu - span.u;
                    span.v = current_iv;
                    span.pnext = surf2.spans;
                    surf2.spans = span;
                }

                // set last_u on the new span
                surf.last_u = iu;

            gotposition:
                // insert before surf2
                surf.next = surf2;
                surf.prev = surf2.prev;
                surf2.prev!.next = surf;
                surf2.prev = surf;
            }
        }
    }

    // r_edge.c: R_GenerateSpans
    public void R_GenerateSpans()
    {
        Edge edge;
        Surf surf;

        r_bmodelactive = 0;

        // clear active surfaces to just the background surface
        surfaces[1].next = surfaces[1].prev = surfaces[1];
        surfaces[1].last_u = edge_head_u_shift20;

        // generate spans
        for (edge = edge_head.next!; edge != edge_tail; edge = edge.next!)
        {
            if (edge.surfs[0] != 0)
            {
                // it has a left surface, so a surface is going away for this span
                surf = surfaces[edge.surfs[0]];

                R_TrailingEdge(surf, edge);

                if (edge.surfs[1] == 0)
                {
                    continue;
                }
            }

            R_LeadingEdge(edge);
        }

        R_CleanupSpan();
    }

    // r_edge.c: R_GenerateSpansBackward
    public void R_GenerateSpansBackward()
    {
        Edge edge;

        r_bmodelactive = 0;

        // clear active surfaces to just the background surface
        surfaces[1].next = surfaces[1].prev = surfaces[1];
        surfaces[1].last_u = edge_head_u_shift20;

        // generate spans
        for (edge = edge_head.next!; edge != edge_tail; edge = edge.next!)
        {
            if (edge.surfs[0] != 0)
            {
                R_TrailingEdge(surfaces[edge.surfs[0]], edge);
            }

            if (edge.surfs[1] != 0)
            {
                R_LeadingEdgeBackwards(edge);
            }
        }

        R_CleanupSpan();
    }

    // r_edge.c: pdrawfunc
    private void pdrawfunc()
    {
        if (pdrawfunc_backward)
        {
            R_GenerateSpansBackward();
        }
        else
        {
            R_GenerateSpans();
        }
    }

    // r_edge.c: R_ScanEdges
    // Input: newedges[] array; this has links to edges, which have links to surfaces
    // Output: Each surface has a linked list of its visible spans
    public void R_ScanEdges()
    {
        int iv, bottom;
        int basespan_p;
        int s;

        basespan_p = 0;
        max_span_p = basespan_p + MAXSPANS - r_refdef.vrect.width;

        span_p = basespan_p;

        // clear active edges to just the background edges around the whole screen
        // FIXME: most of this only needs to be set up once
        edge_head.u = r_refdef.vrect.x << 20;
        edge_head_u_shift20 = edge_head.u >> 20;
        edge_head.u_step = 0;
        edge_head.prev = null;
        edge_head.next = edge_tail;
        edge_head.surfs[0] = 0;
        edge_head.surfs[1] = 1;

        edge_tail.u = (r_refdef.vrectright << 20) + 0xFFFFF;
        edge_tail_u_shift20 = edge_tail.u >> 20;
        edge_tail.u_step = 0;
        edge_tail.prev = edge_head;
        edge_tail.next = edge_aftertail;
        edge_tail.surfs[0] = 1;
        edge_tail.surfs[1] = 0;

        edge_aftertail.u = -1; // force a move
        edge_aftertail.u_step = 0;
        edge_aftertail.next = edge_sentinel;
        edge_aftertail.prev = edge_tail;

        // FIXME: do we need this now that we clamp x in r_draw.c?
        edge_sentinel.u = 2000 << 24; // make sure nothing sorts past this
        edge_sentinel.prev = edge_aftertail;

        // process all scan lines
        bottom = r_refdef.vrectbottom - 1;

        for (iv = r_refdef.vrect.y; iv < bottom; iv++)
        {
            current_iv = iv;
            fv = iv;

            // mark that the head (background start) span is pre-included
            surfaces[1].spanstate = 1;

            if (newedges[iv] != null)
            {
                R_InsertNewEdges(newedges[iv], edge_head.next!);
            }

            pdrawfunc();

            // flush the span list if we can't be sure we have enough spans left for the next scan
            if (span_p >= max_span_p)
            {
                VID_UnlockBuffer();
                S_ExtraUpdate(); // don't let sound get messed up if going slow
                VID_LockBuffer();

                if (r_drawculledpolys)
                {
                    R_DrawCulledPolys();
                }
                else
                {
                    D_DrawSurfaces();
                }

                // clear the surface span pointers
                for (s = 1; s < surface_p; s++)
                {
                    surfaces[s].spans = null;
                }

                span_p = basespan_p;
            }

            if (removeedges[iv] != null)
            {
                R_RemoveEdges(removeedges[iv]);
            }

            if (edge_head.next != edge_tail)
            {
                R_StepActiveU(edge_head.next!);
            }
        }

        // do the last scan (no need to step or sort or remove on the last scan)
        current_iv = iv;
        fv = iv;

        // mark that the head (background start) span is pre-included
        surfaces[1].spanstate = 1;

        if (newedges[iv] != null)
        {
            R_InsertNewEdges(newedges[iv], edge_head.next!);
        }

        pdrawfunc();

        // draw whatever's left in the span list
        if (r_drawculledpolys)
        {
            R_DrawCulledPolys();
        }
        else
        {
            D_DrawSurfaces();
        }
    }
}
