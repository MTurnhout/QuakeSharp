using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Progs;
using QuakeSharp.Core.Server;
using static QuakeSharp.Core.Common.MathLib;
using static QuakeSharp.Core.Model.BspFile;
using static QuakeSharp.Core.QuakeDef;

namespace QuakeSharp.Core.Server
{
    /// <summary>world.c: areanode_t</summary>
    public sealed class AreaNode
    {
        /// <summary>-1 = leaf node</summary>
        public int axis;

        public float dist;

        public readonly AreaNode?[] children = new AreaNode?[2];

        public readonly Link trigger_edicts = new();

        public readonly Link solid_edicts = new();

        /// <summary>Its index in sv_areanodes (for the traces).</summary>
        public int index;
    }
}

namespace QuakeSharp.Core
{
    // world.c: world query functions
    //
    // entities never clip against themselves, or their owner
    //
    // line of sight checks trace->crosscontent, but bullets don't
    public sealed partial class QuakeEngine
    {
        // world.h: MOVE_NORMAL, MOVE_NOMONSTERS, MOVE_MISSILE
        public const int MOVE_NORMAL = 0;
        public const int MOVE_NOMONSTERS = 1;
        public const int MOVE_MISSILE = 2;

        // world.c: moveclip_t (its pointers to the caller's vectors are copies: nothing changes them during a move)
        private struct moveclip_t
        {
            public Vec3 boxmins, boxmaxs; // enclose the test object along entire move
            public Vec3 mins, maxs; // size of the moving object
            public Vec3 mins2, maxs2; // size when clipping against mosnters
            public Vec3 start, end;
            public Trace trace;
            public int type;
            public Edict? passedict;
        }

        // ===============================================================================
        // HULL BOXES
        // ===============================================================================

        private Hull box_hull;
        private readonly DClipnode[] box_clipnodes = new DClipnode[6];
        private readonly MPlane[] box_planes = [new(), new(), new(), new(), new(), new()];

        // world.c: SV_InitBoxHull
        // Set up the planes and clipnodes so that the six floats of a bounding box can just be stored out and get a
        // proper hull_t structure.
        public void SV_InitBoxHull()
        {
            int i;
            int side;

            box_hull.clipnodes = box_clipnodes;
            box_hull.planes = box_planes;
            box_hull.firstclipnode = 0;
            box_hull.lastclipnode = 5;

            for (i = 0; i < 6; i++)
            {
                box_clipnodes[i].planenum = i;

                side = i & 1;

                SetChild(ref box_clipnodes[i], side, CONTENTS_EMPTY);
                if (i != 5)
                {
                    SetChild(ref box_clipnodes[i], side ^ 1, i + 1);
                }
                else
                {
                    SetChild(ref box_clipnodes[i], side ^ 1, CONTENTS_SOLID);
                }

                box_planes[i].type = (byte)(i >> 1);
                box_planes[i].normal[i >> 1] = 1;
            }
        }

        // dclipnode_t's children[n]
        private static int Child(in DClipnode node, int n) => n == 0 ? node.children0 : node.children1;

        private static void SetChild(ref DClipnode node, int n, int value)
        {
            if (n == 0)
            {
                node.children0 = (short)value;
            }
            else
            {
                node.children1 = (short)value;
            }
        }

        // world.c: SV_HullForBox
        // To keep everything totally uniform, bounding boxes are turned into small BSP trees instead of being
        // compared directly.
        public Hull SV_HullForBox(in Vec3 mins, in Vec3 maxs)
        {
            box_planes[0].dist = maxs[0];
            box_planes[1].dist = mins[0];
            box_planes[2].dist = maxs[1];
            box_planes[3].dist = mins[1];
            box_planes[4].dist = maxs[2];
            box_planes[5].dist = mins[2];

            return box_hull;
        }

        // world.c: SV_HullForEntity
        // Returns a hull that can be used for testing or clipping an object of mins/maxs size. Offset is filled in
        // to contain the adjustment that must be added to the testing object's origin to get a point to use with the
        // returned hull.
        public Hull SV_HullForEntity(Edict ent, in Vec3 mins, in Vec3 maxs, ref Vec3 offset)
        {
            Model.Model? model;
            Vec3 size = default;
            Vec3 hullmins = default, hullmaxs = default;
            Hull hull;

            // decide which clipping hull to use, based on the size
            if (ent.v.solid == SOLID_BSP)
            {
                // explicit hulls in the BSP model
                if (ent.v.movetype != MOVETYPE_PUSH)
                {
                    throw Sys_Error("SOLID_BSP without MOVETYPE_PUSH");
                }

                model = sv.models[X86.FtoI(ent.v.modelindex)];

                if (model == null || model.type != modtype_t.mod_brush)
                {
                    throw Sys_Error("MOVETYPE_PUSH with a non bsp model");
                }

                VectorSubtract(maxs, mins, ref size);
                if (size[0] < 3)
                {
                    hull = model.hulls[0];
                }
                else if (size[0] <= 32)
                {
                    hull = model.hulls[1];
                }
                else
                {
                    hull = model.hulls[2];
                }

                // calculate an offset value to center the origin
                VectorSubtract(hull.clip_mins, mins, ref offset);
                VectorAdd(offset, ent.v.origin, ref offset);
            }
            else
            {
                // create a temp hull from bounding box sizes
                VectorSubtract(ent.v.mins, maxs, ref hullmins);
                VectorSubtract(ent.v.maxs, mins, ref hullmaxs);
                hull = SV_HullForBox(hullmins, hullmaxs);

                VectorCopy(ent.v.origin, ref offset);
            }

            return hull;
        }

        // ===============================================================================
        // ENTITY AREA CHECKING
        // ===============================================================================

        private const int AREA_DEPTH = 4;
        private const int AREA_NODES = 32;

        /// <summary>world.c: sv_areanodes</summary>
        public AreaNode[] sv_areanodes = [.. Enumerable.Range(0, AREA_NODES).Select(i => new AreaNode { index = i })];

        /// <summary>world.c: sv_numareanodes</summary>
        public int sv_numareanodes;

        // common.c: ClearLink (used for new headnodes)
        public static void ClearLink(Link l) => l.prev = l.next = l;

        // common.c: RemoveLink
        public static void RemoveLink(Link l)
        {
            l.next!.prev = l.prev;
            l.prev!.next = l.next;
        }

        // common.c: InsertLinkBefore
        public static void InsertLinkBefore(Link l, Link before)
        {
            l.next = before;
            l.prev = before.prev;
            l.prev!.next = l;
            l.next.prev = l;
        }

        // common.c: InsertLinkAfter
        public static void InsertLinkAfter(Link l, Link after)
        {
            l.next = after.next;
            l.prev = after;
            l.prev.next = l;
            l.next!.prev = l;
        }

        // world.c: SV_CreateAreaNode
        public AreaNode SV_CreateAreaNode(int depth, in Vec3 mins, in Vec3 maxs)
        {
            AreaNode anode;
            Vec3 size = default;
            Vec3 mins1 = default, maxs1 = default, mins2 = default, maxs2 = default;

            anode = sv_areanodes[sv_numareanodes];
            sv_numareanodes++;

            ClearLink(anode.trigger_edicts);
            ClearLink(anode.solid_edicts);

            if (depth == AREA_DEPTH)
            {
                anode.axis = -1;
                anode.children[0] = anode.children[1] = null;
                return anode;
            }

            VectorSubtract(maxs, mins, ref size);
            if (size[0] > size[1])
            {
                anode.axis = 0;
            }
            else
            {
                anode.axis = 1;
            }

            anode.dist = (float)(0.5 * (maxs[anode.axis] + mins[anode.axis]));
            VectorCopy(mins, ref mins1);
            VectorCopy(mins, ref mins2);
            VectorCopy(maxs, ref maxs1);
            VectorCopy(maxs, ref maxs2);

            maxs1[anode.axis] = mins2[anode.axis] = anode.dist;

            anode.children[0] = SV_CreateAreaNode(depth + 1, mins2, maxs2);
            anode.children[1] = SV_CreateAreaNode(depth + 1, mins1, maxs1);

            return anode;
        }

        // world.c: SV_ClearWorld
        public void SV_ClearWorld()
        {
            SV_InitBoxHull();

            sv_areanodes = [.. Enumerable.Range(0, AREA_NODES).Select(i => new AreaNode { index = i })];
            sv_numareanodes = 0;
            SV_CreateAreaNode(0, sv.worldmodel!.mins, sv.worldmodel.maxs);
        }

        // world.c: SV_UnlinkEdict
        public static void SV_UnlinkEdict(Edict ent)
        {
            if (ent.area.prev == null)
            {
                return; // not linked in anywhere
            }

            RemoveLink(ent.area);
            ent.area.prev = ent.area.next = null;
        }

        // world.c: SV_TouchLinks
        public void SV_TouchLinks(Edict ent, AreaNode node)
        {
            Link? l, next;
            Edict touch;
            int old_self, old_other;

            // touch linked edicts
            for (l = node.trigger_edicts.next; l != node.trigger_edicts; l = next)
            {
                next = l!.next;
                touch = l.owner!;
                if (touch == ent)
                {
                    continue;
                }

                if (touch.v.touch == 0 || touch.v.solid != SOLID_TRIGGER)
                {
                    continue;
                }

                if (ent.v.absmin[0] > touch.v.absmax[0]
                    || ent.v.absmin[1] > touch.v.absmax[1]
                    || ent.v.absmin[2] > touch.v.absmax[2]
                    || ent.v.absmax[0] < touch.v.absmin[0]
                    || ent.v.absmax[1] < touch.v.absmin[1]
                    || ent.v.absmax[2] < touch.v.absmin[2])
                {
                    continue;
                }

                old_self = pr_global_struct.self;
                old_other = pr_global_struct.other;

                pr_global_struct.self = EDICT_TO_PROG(touch);
                pr_global_struct.other = EDICT_TO_PROG(ent);
                pr_global_struct.time = (float)sv.time;
                PR_ExecuteProgram(touch.v.touch);

                pr_global_struct.self = old_self;
                pr_global_struct.other = old_other;
            }

            // recurse down both sides
            if (node.axis == -1)
            {
                return;
            }

            if (ent.v.absmax[node.axis] > node.dist)
            {
                SV_TouchLinks(ent, node.children[0]!);
            }

            if (ent.v.absmin[node.axis] < node.dist)
            {
                SV_TouchLinks(ent, node.children[1]!);
            }
        }

        // world.c: SV_FindTouchedLeafs
        public void SV_FindTouchedLeafs(Edict ent, MNodeBase node)
        {
            MPlane splitplane;
            MLeaf leaf;
            int sides;
            int leafnum;

            if (node.contents == CONTENTS_SOLID)
            {
                return;
            }

            // add an efrag if the node is a leaf
            if (node.contents < 0)
            {
                if (ent.num_leafs == Edict.MAX_ENT_LEAFS)
                {
                    return;
                }

                leaf = (MLeaf)node;
                leafnum = leaf.index - 1;

                ent.leafnums[ent.num_leafs] = (short)leafnum;
                ent.num_leafs++;
                return;
            }

            // NODE_MIXED
            var mnode = (MNode)node;
            splitplane = mnode.plane;
            sides = BOX_ON_PLANE_SIDE(ent.v.absmin, ent.v.absmax, splitplane);

            // recurse down the contacted sides
            if ((sides & 1) != 0)
            {
                SV_FindTouchedLeafs(ent, mnode.children[0]);
            }

            if ((sides & 2) != 0)
            {
                SV_FindTouchedLeafs(ent, mnode.children[1]);
            }
        }

        // world.c: SV_LinkEdict
        public void SV_LinkEdict(Edict ent, bool touch_triggers)
        {
            AreaNode node;

            if (ent.area.prev != null)
            {
                SV_UnlinkEdict(ent); // unlink from old position
            }

            if (ent == sv.edicts[0])
            {
                return; // don't add the world
            }

            if (ent.free)
            {
                return;
            }

            // set the abs box
            VectorAdd(ent.v.origin, ent.v.mins, ref ent.v.absmin);
            VectorAdd(ent.v.origin, ent.v.maxs, ref ent.v.absmax);

            // to make items easier to pick up and allow them to be grabbed off of shelves, the abs sizes are
            // expanded
            if ((X86.FtoI(ent.v.flags) & FL_ITEM) != 0)
            {
                ent.v.absmin[0] -= 15;
                ent.v.absmin[1] -= 15;
                ent.v.absmax[0] += 15;
                ent.v.absmax[1] += 15;
            }
            else
            {
                // because movement is clipped an epsilon away from an actual edge, we must fully check even when
                // bounding boxes don't quite touch
                ent.v.absmin[0] -= 1;
                ent.v.absmin[1] -= 1;
                ent.v.absmin[2] -= 1;
                ent.v.absmax[0] += 1;
                ent.v.absmax[1] += 1;
                ent.v.absmax[2] += 1;
            }

            // link to PVS leafs
            ent.num_leafs = 0;
            if (ent.v.modelindex != 0)
            {
                SV_FindTouchedLeafs(ent, sv.worldmodel!.nodes[0]);
            }

            if (ent.v.solid == SOLID_NOT)
            {
                return;
            }

            // find the first node that the ent's box crosses
            node = sv_areanodes[0];
            while (true)
            {
                if (node.axis == -1)
                {
                    break;
                }

                if (ent.v.absmin[node.axis] > node.dist)
                {
                    node = node.children[0]!;
                }
                else if (ent.v.absmax[node.axis] < node.dist)
                {
                    node = node.children[1]!;
                }
                else
                {
                    break; // crosses the node
                }
            }

            // link it in
            if (ent.v.solid == SOLID_TRIGGER)
            {
                InsertLinkBefore(ent.area, node.trigger_edicts);
            }
            else
            {
                InsertLinkBefore(ent.area, node.solid_edicts);
            }

            // if touch_triggers, touch all entities at this node and decend for more
            if (touch_triggers)
            {
                SV_TouchLinks(ent, sv_areanodes[0]);
            }
        }

        // ===============================================================================
        // POINT TESTING IN HULLS
        // ===============================================================================

        // world.c: SV_HullPointContents
        public static int SV_HullPointContents(in Hull hull, int num, in Vec3 p)
        {
            float d;
            MPlane plane;

            while (num >= 0)
            {
                if (num < hull.firstclipnode || num > hull.lastclipnode)
                {
                    throw Sys_Error("SV_HullPointContents: bad node number");
                }

                ref DClipnode node = ref hull.clipnodes[num];
                plane = hull.planes[node.planenum];

                if (plane.type < 3)
                {
                    d = p[plane.type] - plane.dist;
                }
                else
                {
                    d = DotProduct(plane.normal, p) - plane.dist;
                }

                if (d < 0)
                {
                    num = node.children1;
                }
                else
                {
                    num = node.children0;
                }
            }

            return num;
        }

        // world.c: SV_PointContents
        public int SV_PointContents(in Vec3 p)
        {
            int cont;

            cont = SV_HullPointContents(sv.worldmodel!.hulls[0], 0, p);
            if (cont <= CONTENTS_CURRENT_0 && cont >= CONTENTS_CURRENT_DOWN)
            {
                cont = CONTENTS_WATER;
            }

            return cont;
        }

        // world.c: SV_TruePointContents
        public int SV_TruePointContents(in Vec3 p) => SV_HullPointContents(sv.worldmodel!.hulls[0], 0, p);

        // ===========================================================================

        // world.c: SV_TestEntityPosition
        // This could be a lot more efficient...
        public Edict? SV_TestEntityPosition(Edict ent)
        {
            Trace trace;

            trace = SV_Move(ent.v.origin, ent.v.mins, ent.v.maxs, ent.v.origin, 0, ent);

            if (trace.startsolid)
            {
                return sv.edicts[0];
            }

            return null;
        }

        // ===============================================================================
        // LINE TESTING IN HULLS
        // ===============================================================================

        // 1/32 epsilon to keep floating point happy
        private const double DIST_EPSILON = 0.03125;

        // world.c: SV_RecursiveHullCheck
        public bool SV_RecursiveHullCheck(in Hull hull, int num, float p1f, float p2f, in Vec3 p1, in Vec3 p2, ref Trace trace)
        {
            MPlane plane;
            float t1, t2;
            float frac;
            int i;
            Vec3 mid = default;
            int side;
            float midf;

            // check for empty
            if (num < 0)
            {
                if (num != CONTENTS_SOLID)
                {
                    trace.allsolid = false;
                    if (num == CONTENTS_EMPTY)
                    {
                        trace.inopen = true;
                    }
                    else
                    {
                        trace.inwater = true;
                    }
                }
                else
                {
                    trace.startsolid = true;
                }

                return true; // empty
            }

            if (num < hull.firstclipnode || num > hull.lastclipnode)
            {
                throw Sys_Error("SV_RecursiveHullCheck: bad node number");
            }

            // find the point distances
            DClipnode node = hull.clipnodes[num];
            plane = hull.planes[node.planenum];

            if (plane.type < 3)
            {
                t1 = p1[plane.type] - plane.dist;
                t2 = p2[plane.type] - plane.dist;
            }
            else
            {
                t1 = DotProduct(plane.normal, p1) - plane.dist;
                t2 = DotProduct(plane.normal, p2) - plane.dist;
            }

            if (t1 >= 0 && t2 >= 0)
            {
                return SV_RecursiveHullCheck(hull, node.children0, p1f, p2f, p1, p2, ref trace);
            }

            if (t1 < 0 && t2 < 0)
            {
                return SV_RecursiveHullCheck(hull, node.children1, p1f, p2f, p1, p2, ref trace);
            }

            // put the crosspoint DIST_EPSILON pixels on the near side
            if (t1 < 0)
            {
                frac = (float)((t1 + DIST_EPSILON) / (t1 - t2));
            }
            else
            {
                frac = (float)((t1 - DIST_EPSILON) / (t1 - t2));
            }

            if (frac < 0)
            {
                frac = 0;
            }

            if (frac > 1)
            {
                frac = 1;
            }

            midf = p1f + ((p2f - p1f) * frac);
            for (i = 0; i < 3; i++)
            {
                mid[i] = p1[i] + (frac * (p2[i] - p1[i]));
            }

            side = t1 < 0 ? 1 : 0;

            // move up to the node
            if (!SV_RecursiveHullCheck(hull, Child(node, side), p1f, midf, p1, mid, ref trace))
            {
                return false;
            }

            if (SV_HullPointContents(hull, Child(node, side ^ 1), mid) != CONTENTS_SOLID)
            {
                // go past the node
                return SV_RecursiveHullCheck(hull, Child(node, side ^ 1), midf, p2f, mid, p2, ref trace);
            }

            if (trace.allsolid)
            {
                return false; // never got out of the solid area
            }

            // ==================
            // the other side of the node is solid, this is the impact point
            // ==================
            if (side == 0)
            {
                VectorCopy(plane.normal, ref trace.plane.normal);
                trace.plane.dist = plane.dist;
            }
            else
            {
                VectorSubtract(vec3_origin, plane.normal, ref trace.plane.normal);
                trace.plane.dist = -plane.dist;
            }

            while (SV_HullPointContents(hull, hull.firstclipnode, mid) == CONTENTS_SOLID)
            {
                // shouldn't really happen, but does occasionally
                frac = (float)(frac - 0.1);
                if (frac < 0)
                {
                    trace.fraction = midf;
                    VectorCopy(mid, ref trace.endpos);
                    Con_DPrintf("backup past 0\n");
                    return false;
                }

                midf = p1f + ((p2f - p1f) * frac);
                for (i = 0; i < 3; i++)
                {
                    mid[i] = p1[i] + (frac * (p2[i] - p1[i]));
                }
            }

            trace.fraction = midf;
            VectorCopy(mid, ref trace.endpos);

            return false;
        }

        // world.c: SV_ClipMoveToEntity
        // Handles selection or creation of a clipping hull, and offseting (and eventually rotation) of the end points
        public Trace SV_ClipMoveToEntity(Edict ent, in Vec3 start, in Vec3 mins, in Vec3 maxs, in Vec3 end)
        {
            Trace trace;
            Vec3 offset = default;
            Vec3 start_l = default, end_l = default;
            Hull hull;

            // fill in a default trace
            trace = default;
            trace.fraction = 1;
            trace.allsolid = true;
            VectorCopy(end, ref trace.endpos);

            // get the clipping hull
            hull = SV_HullForEntity(ent, mins, maxs, ref offset);

            VectorSubtract(start, offset, ref start_l);
            VectorSubtract(end, offset, ref end_l);

            // trace a line through the apropriate clipping hull
            SV_RecursiveHullCheck(hull, hull.firstclipnode, 0, 1, start_l, end_l, ref trace);

            // fix trace up by the offset
            if (trace.fraction != 1)
            {
                VectorAdd(trace.endpos, offset, ref trace.endpos);
            }

            // did we clip the move?
            if (trace.fraction < 1 || trace.startsolid)
            {
                trace.ent = ent;
            }

            return trace;
        }

        // ===========================================================================

        // world.c: SV_ClipToLinks
        // Mins and maxs enclose the entire area swept by the move
        private void SV_ClipToLinks(AreaNode node, ref moveclip_t clip)
        {
            Link? l, next;
            Edict touch;
            Trace trace;

            // touch linked edicts
            for (l = node.solid_edicts.next; l != node.solid_edicts; l = next)
            {
                next = l!.next;
                touch = l.owner!;
                if (touch.v.solid == SOLID_NOT)
                {
                    continue;
                }

                if (touch == clip.passedict)
                {
                    continue;
                }

                if (touch.v.solid == SOLID_TRIGGER)
                {
                    throw Sys_Error("Trigger in clipping list");
                }

                if (clip.type == MOVE_NOMONSTERS && touch.v.solid != SOLID_BSP)
                {
                    continue;
                }

                if (clip.boxmins[0] > touch.v.absmax[0]
                    || clip.boxmins[1] > touch.v.absmax[1]
                    || clip.boxmins[2] > touch.v.absmax[2]
                    || clip.boxmaxs[0] < touch.v.absmin[0]
                    || clip.boxmaxs[1] < touch.v.absmin[1]
                    || clip.boxmaxs[2] < touch.v.absmin[2])
                {
                    continue;
                }

                if (clip.passedict != null && clip.passedict.v.size[0] != 0 && touch.v.size[0] == 0)
                {
                    continue; // points never interact
                }

                // might intersect, so do an exact clip
                if (clip.trace.allsolid)
                {
                    return;
                }

                if (clip.passedict != null)
                {
                    if (PROG_TO_EDICT(touch.v.owner) == clip.passedict)
                    {
                        continue; // don't clip against own missiles
                    }

                    if (PROG_TO_EDICT(clip.passedict.v.owner) == touch)
                    {
                        continue; // don't clip against owner
                    }
                }

                if ((X86.FtoI(touch.v.flags) & FL_MONSTER) != 0)
                {
                    trace = SV_ClipMoveToEntity(touch, clip.start, clip.mins2, clip.maxs2, clip.end);
                }
                else
                {
                    trace = SV_ClipMoveToEntity(touch, clip.start, clip.mins, clip.maxs, clip.end);
                }

                if (trace.allsolid || trace.startsolid || trace.fraction < clip.trace.fraction)
                {
                    trace.ent = touch;
                    if (clip.trace.startsolid)
                    {
                        clip.trace = trace;
                        clip.trace.startsolid = true;
                    }
                    else
                    {
                        clip.trace = trace;
                    }
                }
                else if (trace.startsolid)
                {
                    clip.trace.startsolid = true;
                }
            }

            // recurse down both sides
            if (node.axis == -1)
            {
                return;
            }

            if (clip.boxmaxs[node.axis] > node.dist)
            {
                SV_ClipToLinks(node.children[0]!, ref clip);
            }

            if (clip.boxmins[node.axis] < node.dist)
            {
                SV_ClipToLinks(node.children[1]!, ref clip);
            }
        }

        // world.c: SV_MoveBounds
        public static void SV_MoveBounds(in Vec3 start, in Vec3 mins, in Vec3 maxs, in Vec3 end, ref Vec3 boxmins, ref Vec3 boxmaxs)
        {
            int i;

            for (i = 0; i < 3; i++)
            {
                if (end[i] > start[i])
                {
                    boxmins[i] = start[i] + mins[i] - 1;
                    boxmaxs[i] = end[i] + maxs[i] + 1;
                }
                else
                {
                    boxmins[i] = end[i] + mins[i] - 1;
                    boxmaxs[i] = start[i] + maxs[i] + 1;
                }
            }
        }

        // world.c: SV_Move
        public Trace SV_Move(in Vec3 start, in Vec3 mins, in Vec3 maxs, in Vec3 end, int type, Edict? passedict)
        {
            moveclip_t clip;
            int i;

            clip = default;

            // clip to world
            clip.trace = SV_ClipMoveToEntity(sv.edicts[0], start, mins, maxs, end);

            clip.start = start;
            clip.end = end;
            clip.mins = mins;
            clip.maxs = maxs;
            clip.type = type;
            clip.passedict = passedict;

            if (type == MOVE_MISSILE)
            {
                for (i = 0; i < 3; i++)
                {
                    clip.mins2[i] = -15;
                    clip.maxs2[i] = 15;
                }
            }
            else
            {
                VectorCopy(mins, ref clip.mins2);
                VectorCopy(maxs, ref clip.maxs2);
            }

            // create the bounding box of the entire move
            SV_MoveBounds(start, clip.mins2, clip.maxs2, end, ref clip.boxmins, ref clip.boxmaxs);

            // clip to entities
            SV_ClipToLinks(sv_areanodes[0], ref clip);

            return clip.trace;
        }
    }
}
