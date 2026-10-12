using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using static QuakeSharp.Core.Common.MathLib;
using static QuakeSharp.Core.Model.BspFile;
using ModelT = QuakeSharp.Core.Model.Model;

namespace QuakeSharp.Core;

// r_efrag.c: entity fragments, which link the static entities into the leafs they touch
public sealed partial class QuakeEngine
{
    public MNodeBase? r_pefragtopnode;

    // r_efrag.c: lastlink (C's efrag_t **: the entity's first efrag or an efrag's entnext)
    private Entity? lastlink_entity;
    private Efrag? lastlink_efrag;

    public Vec3 r_emins, r_emaxs;

    public Entity? r_addent;

    // r_efrag.c: R_RemoveEfrags
    // Call when removing an object from the world or moving it to another position
    public void R_RemoveEfrags(Entity ent)
    {
        Efrag? ef, old, walk;

        ef = ent.efrag;

        while (ef != null)
        {
            // prev = &ef->leaf->efrags
            Efrag? prev = null;
            while (true)
            {
                walk = prev == null ? ef.leaf!.efrags : prev.leafnext;
                if (walk == null)
                {
                    break;
                }

                if (walk == ef)
                {
                    // remove this fragment
                    if (prev == null)
                    {
                        ef.leaf!.efrags = ef.leafnext;
                    }
                    else
                    {
                        prev.leafnext = ef.leafnext;
                    }

                    break;
                }
                else
                {
                    prev = walk;
                }
            }

            old = ef;
            ef = ef.entnext;

            // put it on the free list
            old.entnext = cl.free_efrags;
            cl.free_efrags = old;
        }

        ent.efrag = null;
    }

    // r_efrag.c: R_SplitEntityOnNode
    public void R_SplitEntityOnNode(MNodeBase node)
    {
        Efrag? ef;
        MPlane splitplane;
        MLeaf leaf;
        int sides;

        if (node.contents == CONTENTS_SOLID)
        {
            return;
        }

        // add an efrag if the node is a leaf
        if (node.contents < 0)
        {
            r_pefragtopnode ??= node;

            leaf = (MLeaf)node;

            // grab an efrag off the free list
            ef = cl.free_efrags;
            if (ef == null)
            {
                Con_Printf("Too many efrags!\n");
                return; // no free fragments...
            }

            cl.free_efrags = ef.entnext;

            ef.entity = r_addent;

            // add the entity link
            if (lastlink_efrag != null)
            {
                lastlink_efrag.entnext = ef;
            }
            else
            {
                lastlink_entity!.efrag = ef;
            }

            lastlink_efrag = ef;
            ef.entnext = null;

            // set the leaf links
            ef.leaf = leaf;
            ef.leafnext = leaf.efrags;
            leaf.efrags = ef;

            return;
        }

        // NODE_MIXED
        var mnode = (MNode)node;
        splitplane = mnode.plane;
        sides = BOX_ON_PLANE_SIDE(r_emins, r_emaxs, splitplane);

        if (sides == 3)
        {
            // split on this plane
            // if this is the first splitter of this bmodel, remember it
            r_pefragtopnode ??= node;
        }

        // recurse down the contacted sides
        if ((sides & 1) != 0)
        {
            R_SplitEntityOnNode(mnode.children[0]);
        }

        if ((sides & 2) != 0)
        {
            R_SplitEntityOnNode(mnode.children[1]);
        }
    }

    // r_efrag.c: R_SplitEntityOnNode2
    public void R_SplitEntityOnNode2(MNodeBase node)
    {
        MPlane splitplane;
        int sides;

        if (node.visframe != r_visframecount)
        {
            return;
        }

        if (node.contents < 0)
        {
            if (node.contents != CONTENTS_SOLID)
            {
                r_pefragtopnode = node; // we've reached a non-solid leaf, so it's visible and not BSP clipped
            }

            return;
        }

        var mnode = (MNode)node;
        splitplane = mnode.plane;
        sides = BOX_ON_PLANE_SIDE(r_emins, r_emaxs, splitplane);

        if (sides == 3)
        {
            // remember first splitter
            r_pefragtopnode = node;
            return;
        }

        // not split yet; recurse down the contacted side
        if ((sides & 1) != 0)
        {
            R_SplitEntityOnNode2(mnode.children[0]);
        }
        else
        {
            R_SplitEntityOnNode2(mnode.children[1]);
        }
    }

    // r_efrag.c: R_AddEfrags
    public void R_AddEfrags(Entity ent)
    {
        ModelT entmodel;
        int i;

        if (ent.model == null)
        {
            return;
        }

        if (ent == cl_entities[0])
        {
            return; // never add the world
        }

        r_addent = ent;

        lastlink_entity = ent;
        lastlink_efrag = null;
        r_pefragtopnode = null;

        entmodel = ent.model;

        for (i = 0; i < 3; i++)
        {
            r_emins[i] = ent.origin[i] + entmodel.mins[i];
            r_emaxs[i] = ent.origin[i] + entmodel.maxs[i];
        }

        R_SplitEntityOnNode(cl.worldmodel!.nodes[0]);

        ent.topnode = r_pefragtopnode;
    }

    // r_efrag.c: R_StoreEfrags (the leaf's efrags list)
    // FIXME: a lot of this goes away with edge-based
    public void R_StoreEfrags(MLeaf leaf)
    {
        Entity pent;
        ModelT clmodel;
        Efrag? pefrag;
        Efrag? link = null; // ppefrag: &leaf->efrags, then an efrag's leafnext

        while ((pefrag = link == null ? leaf.efrags : link.leafnext) != null)
        {
            pent = pefrag.entity!;
            clmodel = pent.model!;

            switch (clmodel.type)
            {
                case modtype_t.mod_alias:
                case modtype_t.mod_brush:
                case modtype_t.mod_sprite:
                    pent = pefrag.entity!;

                    if ((pent.visframe != r_framecount) &&
                        (cl_numvisedicts < MAX_VISEDICTS))
                    {
                        cl_visedicts[cl_numvisedicts++] = pent;

                        // mark that we've recorded this entity for this frame
                        pent.visframe = r_framecount;
                    }

                    link = pefrag;
                    break;

                default:
                    throw Sys_Error("R_StoreEfrags: Bad entity type %d\n", (int)clmodel.type);
            }
        }
    }
}
