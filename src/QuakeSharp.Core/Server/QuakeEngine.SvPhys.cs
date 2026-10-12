using QuakeSharp.Core.Common;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Progs;
using QuakeSharp.Core.Server;
using static QuakeSharp.Core.Common.MathLib;
using static QuakeSharp.Core.Model.BspFile;
using static QuakeSharp.Core.QuakeDef;

namespace QuakeSharp.Core;

// sv_phys.c
//
// pushmove objects do not obey gravity, and do not interact with each other or trigger fields, but block normal
// movement and push normal objects when they move.
//
// onground is set for toss objects when they come to a complete rest. it is set for steping or walking objects
//
// doors, plats, etc are SOLID_BSP, and MOVETYPE_PUSH
// bonus items are SOLID_TRIGGER touch, and MOVETYPE_TOSS
// corpses are SOLID_NOT and MOVETYPE_TOSS
// crates are SOLID_BBOX and MOVETYPE_TOSS
// walking monsters are SOLID_SLIDEBOX and MOVETYPE_STEP
// flying/floating monsters are SOLID_SLIDEBOX and MOVETYPE_FLY
//
// solid_edge items only clip against bsp models.
public sealed partial class QuakeEngine
{
    private const double MOVE_EPSILON = 0.01;

    // mathlib.h: IS_NAN (an exponent of all ones: infinities count as NaNs)
    private static bool IS_NAN(float x)
    {
        const int nanmask = 255 << 23;
        return (BitConverter.SingleToInt32Bits(x) & nanmask) == nanmask;
    }

    // sv_phys.c: SV_CheckAllEnts
    public void SV_CheckAllEnts()
    {
        int e;
        Edict check;

        // see if any solid entities are inside the final position
        check = NEXT_EDICT(sv.edicts[0]);
        for (e = 1; e < sv.num_edicts; e++, check = NEXT_EDICT(check))
        {
            if (check.free)
            {
                continue;
            }

            if (check.v.movetype == MOVETYPE_PUSH
                || check.v.movetype == MOVETYPE_NONE
                || check.v.movetype == MOVETYPE_NOCLIP)
            {
                continue;
            }

            if (SV_TestEntityPosition(check) != null)
            {
                Con_Printf("entity in invalid position\n");
            }
        }
    }

    // sv_phys.c: SV_CheckVelocity
    public void SV_CheckVelocity(Edict ent)
    {
        int i;

        // bound velocity
        for (i = 0; i < 3; i++)
        {
            if (IS_NAN(ent.v.velocity[i]))
            {
                Con_Printf("Got a NaN velocity on %s\n", PR_GetString(ent.v.classname));
                ent.v.velocity[i] = 0;
            }

            if (IS_NAN(ent.v.origin[i]))
            {
                Con_Printf("Got a NaN origin on %s\n", PR_GetString(ent.v.classname));
                ent.v.origin[i] = 0;
            }

            if (ent.v.velocity[i] > sv_maxvelocity.value)
            {
                ent.v.velocity[i] = sv_maxvelocity.value;
            }
            else if (ent.v.velocity[i] < -sv_maxvelocity.value)
            {
                ent.v.velocity[i] = -sv_maxvelocity.value;
            }
        }
    }

    // sv_phys.c: SV_RunThink
    // Runs thinking code if time. There is some play in the exact time the think function will be called, because it
    // is called before any movement is done in a frame. Not used for pushmove objects, because they must be exact.
    // Returns false if the entity removed itself.
    public bool SV_RunThink(Edict ent)
    {
        float thinktime;

        thinktime = ent.v.nextthink;
        if (thinktime <= 0 || thinktime > sv.time + host_frametime)
        {
            return true;
        }

        if (thinktime < sv.time)
        {
            thinktime = (float)sv.time; // don't let things stay in the past. it is possible to start that way by a
                                        // trigger with a local time.
        }

        ent.v.nextthink = 0;
        pr_global_struct.time = thinktime;
        pr_global_struct.self = EDICT_TO_PROG(ent);
        pr_global_struct.other = EDICT_TO_PROG(sv.edicts[0]);
        PR_ExecuteProgram(ent.v.think);
        return !ent.free;
    }

    // sv_phys.c: SV_Impact
    // Two entities have touched, so run their touch functions
    public void SV_Impact(Edict e1, Edict e2)
    {
        int old_self, old_other;

        old_self = pr_global_struct.self;
        old_other = pr_global_struct.other;

        pr_global_struct.time = (float)sv.time;
        if (e1.v.touch != 0 && e1.v.solid != SOLID_NOT)
        {
            pr_global_struct.self = EDICT_TO_PROG(e1);
            pr_global_struct.other = EDICT_TO_PROG(e2);
            PR_ExecuteProgram(e1.v.touch);
        }

        if (e2.v.touch != 0 && e2.v.solid != SOLID_NOT)
        {
            pr_global_struct.self = EDICT_TO_PROG(e2);
            pr_global_struct.other = EDICT_TO_PROG(e1);
            PR_ExecuteProgram(e2.v.touch);
        }

        pr_global_struct.self = old_self;
        pr_global_struct.other = old_other;
    }

    private const double STOP_EPSILON = 0.1;

    // sv_phys.c: ClipVelocity
    // Slide off of the impacting object
    // returns the blocked flags (1 = floor, 2 = step / wall); in and out may be the same vector
    public static int ClipVelocity(ref Vec3 @in, in Vec3 normal, ref Vec3 @out, float overbounce)
    {
        float backoff;
        float change;
        int i, blocked;

        blocked = 0;
        if (normal[2] > 0)
        {
            blocked |= 1; // floor
        }

        if (normal[2] == 0)
        {
            blocked |= 2; // step
        }

        backoff = DotProduct(@in, normal) * overbounce;

        for (i = 0; i < 3; i++)
        {
            change = normal[i] * backoff;
            @out[i] = @in[i] - change;
            if (@out[i] > -STOP_EPSILON && @out[i] < STOP_EPSILON)
            {
                @out[i] = 0;
            }
        }

        return blocked;
    }

    private const int MAX_CLIP_PLANES = 5;

    // sv_phys.c: SV_FlyMove
    // The basic solid body movement clip that slides along multiple planes
    // Returns the clipflags if the velocity was modified (hit something solid)
    // 1 = floor
    // 2 = wall / step
    // 4 = dead stop
    // If steptrace is not NULL, the trace of any vertical wall hit will be stored
    public int SV_FlyMove(Edict ent, float time, ref Trace steptrace, bool hasSteptrace)
    {
        int bumpcount, numbumps;
        Vec3 dir = default;
        float d;
        int numplanes;
        Span<Vec3> planes = stackalloc Vec3[MAX_CLIP_PLANES];
        Vec3 primal_velocity = default, original_velocity = default, new_velocity = default;
        int i, j;
        Trace trace;
        Vec3 end = default;
        float time_left;
        int blocked;

        numbumps = 4;

        blocked = 0;
        VectorCopy(ent.v.velocity, ref original_velocity);
        VectorCopy(ent.v.velocity, ref primal_velocity);
        numplanes = 0;

        time_left = time;

        for (bumpcount = 0; bumpcount < numbumps; bumpcount++)
        {
            if (ent.v.velocity[0] == 0 && ent.v.velocity[1] == 0 && ent.v.velocity[2] == 0)
            {
                break;
            }

            for (i = 0; i < 3; i++)
            {
                end[i] = ent.v.origin[i] + (time_left * ent.v.velocity[i]);
            }

            trace = SV_Move(ent.v.origin, ent.v.mins, ent.v.maxs, end, 0, ent);

            if (trace.allsolid)
            {
                // entity is trapped in another solid
                VectorCopy(vec3_origin, ref ent.v.velocity);
                return 3;
            }

            if (trace.fraction > 0)
            {
                // actually covered some distance
                VectorCopy(trace.endpos, ref ent.v.origin);
                VectorCopy(ent.v.velocity, ref original_velocity);
                numplanes = 0;
            }

            if (trace.fraction == 1)
            {
                break; // moved the entire distance
            }

            if (trace.ent == null)
            {
                throw Sys_Error("SV_FlyMove: !trace.ent");
            }

            if (trace.plane.normal[2] > 0.7)
            {
                blocked |= 1; // floor
                if (trace.ent.v.solid == SOLID_BSP)
                {
                    ent.v.flags = X86.FtoI(ent.v.flags) | FL_ONGROUND;
                    ent.v.groundentity = EDICT_TO_PROG(trace.ent);
                }
            }

            if (trace.plane.normal[2] == 0)
            {
                blocked |= 2; // step
                if (hasSteptrace)
                {
                    steptrace = trace; // save for player extrafriction
                }
            }

            // run the impact function
            SV_Impact(ent, trace.ent);
            if (ent.free)
            {
                break; // removed by the impact function
            }

            time_left -= time_left * trace.fraction;

            // cliped to another plane
            if (numplanes >= MAX_CLIP_PLANES)
            {
                // this shouldn't really happen
                VectorCopy(vec3_origin, ref ent.v.velocity);
                return 3;
            }

            VectorCopy(trace.plane.normal, ref planes[numplanes]);
            numplanes++;

            // modify original_velocity so it parallels all of the clip planes
            for (i = 0; i < numplanes; i++)
            {
                ClipVelocity(ref original_velocity, planes[i], ref new_velocity, 1);
                for (j = 0; j < numplanes; j++)
                {
                    if (j != i)
                    {
                        if (DotProduct(new_velocity, planes[j]) < 0)
                        {
                            break; // not ok
                        }
                    }
                }

                if (j == numplanes)
                {
                    break;
                }
            }

            if (i != numplanes)
            {
                // go along this plane
                VectorCopy(new_velocity, ref ent.v.velocity);
            }
            else
            {
                // go along the crease
                if (numplanes != 2)
                {
                    VectorCopy(vec3_origin, ref ent.v.velocity);
                    return 7;
                }

                CrossProduct(planes[0], planes[1], ref dir);
                d = DotProduct(dir, ent.v.velocity);
                VectorScale(dir, d, ref ent.v.velocity);
            }

            // if original velocity is against the original velocity, stop dead to avoid tiny occilations in sloping
            // corners
            if (DotProduct(ent.v.velocity, primal_velocity) <= 0)
            {
                VectorCopy(vec3_origin, ref ent.v.velocity);
                return blocked;
            }
        }

        return blocked;
    }

    // SV_FlyMove (ent, time, NULL)
    public int SV_FlyMove(Edict ent, float time)
    {
        Trace none = default;
        return SV_FlyMove(ent, time, ref none, false);
    }

    // sv_phys.c: SV_AddGravity
    public void SV_AddGravity(Edict ent)
    {
        float ent_gravity;
        int val;

        val = GetEdictFieldValue(ent, "gravity");
        if (val >= 0 && sv.edictmem[val] != 0 && BitConverter.Int32BitsToSingle(sv.edictmem[val]) != 0)
        {
            ent_gravity = BitConverter.Int32BitsToSingle(sv.edictmem[val]);
        }
        else
        {
            ent_gravity = 1.0f;
        }

        ent.v.velocity[2] = (float)(ent.v.velocity[2] - (ent_gravity * sv_gravity.value * host_frametime));
    }

    // ===============================================================================
    // PUSHMOVE
    // ===============================================================================

    // sv_phys.c: SV_PushEntity
    // Does not change the entities velocity at all
    public Trace SV_PushEntity(Edict ent, in Vec3 push)
    {
        Trace trace;
        Vec3 end = default;

        VectorAdd(ent.v.origin, push, ref end);

        if (ent.v.movetype == MOVETYPE_FLYMISSILE)
        {
            trace = SV_Move(ent.v.origin, ent.v.mins, ent.v.maxs, end, MOVE_MISSILE, ent);
        }
        else if (ent.v.solid == SOLID_TRIGGER || ent.v.solid == SOLID_NOT)
        {
            // only clip against bmodels
            trace = SV_Move(ent.v.origin, ent.v.mins, ent.v.maxs, end, MOVE_NOMONSTERS, ent);
        }
        else
        {
            trace = SV_Move(ent.v.origin, ent.v.mins, ent.v.maxs, end, MOVE_NORMAL, ent);
        }

        VectorCopy(trace.endpos, ref ent.v.origin);
        SV_LinkEdict(ent, true);

        if (trace.ent != null)
        {
            SV_Impact(ent, trace.ent);
        }

        return trace;
    }

    // sv_phys.c: SV_PushMove
    public void SV_PushMove(Edict pusher, float movetime)
    {
        int i, e;
        Edict check;
        Edict? block;
        Vec3 mins = default, maxs = default, move = default;
        Vec3 entorig = default, pushorig = default;
        int num_moved;
        var moved_edict = new Edict[MAX_EDICTS];
        var moved_from = new Vec3[MAX_EDICTS];

        if (pusher.v.velocity[0] == 0 && pusher.v.velocity[1] == 0 && pusher.v.velocity[2] == 0)
        {
            pusher.v.ltime += movetime;
            return;
        }

        for (i = 0; i < 3; i++)
        {
            move[i] = pusher.v.velocity[i] * movetime;
            mins[i] = pusher.v.absmin[i] + move[i];
            maxs[i] = pusher.v.absmax[i] + move[i];
        }

        VectorCopy(pusher.v.origin, ref pushorig);

        // move the pusher to it's final position
        VectorAdd(pusher.v.origin, move, ref pusher.v.origin);
        pusher.v.ltime += movetime;
        SV_LinkEdict(pusher, false);

        // see if any solid entities are inside the final position
        num_moved = 0;
        check = NEXT_EDICT(sv.edicts[0]);
        for (e = 1; e < sv.num_edicts; e++, check = NEXT_EDICT(check))
        {
            if (check.free)
            {
                continue;
            }

            if (check.v.movetype == MOVETYPE_PUSH
                || check.v.movetype == MOVETYPE_NONE
                || check.v.movetype == MOVETYPE_NOCLIP)
            {
                continue;
            }

            // if the entity is standing on the pusher, it will definately be moved
            if (!((X86.FtoI(check.v.flags) & FL_ONGROUND) != 0 && PROG_TO_EDICT(check.v.groundentity) == pusher))
            {
                if (check.v.absmin[0] >= maxs[0]
                    || check.v.absmin[1] >= maxs[1]
                    || check.v.absmin[2] >= maxs[2]
                    || check.v.absmax[0] <= mins[0]
                    || check.v.absmax[1] <= mins[1]
                    || check.v.absmax[2] <= mins[2])
                {
                    continue;
                }

                // see if the ent's bbox is inside the pusher's final position
                if (SV_TestEntityPosition(check) == null)
                {
                    continue;
                }
            }

            // remove the onground flag for non-players
            if (check.v.movetype != MOVETYPE_WALK)
            {
                check.v.flags = X86.FtoI(check.v.flags) & ~FL_ONGROUND;
            }

            VectorCopy(check.v.origin, ref entorig);
            VectorCopy(check.v.origin, ref moved_from[num_moved]);
            moved_edict[num_moved] = check;
            num_moved++;

            // try moving the contacted entity
            pusher.v.solid = SOLID_NOT;
            SV_PushEntity(check, move);
            pusher.v.solid = SOLID_BSP;

            // if it is still inside the pusher, block
            block = SV_TestEntityPosition(check);
            if (block != null)
            {
                // fail the move
                if (check.v.mins[0] == check.v.maxs[0])
                {
                    continue;
                }

                if (check.v.solid == SOLID_NOT || check.v.solid == SOLID_TRIGGER)
                {
                    // corpse
                    check.v.mins[0] = check.v.mins[1] = 0;
                    VectorCopy(check.v.mins, ref check.v.maxs);
                    continue;
                }

                VectorCopy(entorig, ref check.v.origin);
                SV_LinkEdict(check, true);

                VectorCopy(pushorig, ref pusher.v.origin);
                SV_LinkEdict(pusher, false);
                pusher.v.ltime -= movetime;

                // if the pusher has a "blocked" function, call it
                // otherwise, just stay in place until the obstacle is gone
                if (pusher.v.blocked != 0)
                {
                    pr_global_struct.self = EDICT_TO_PROG(pusher);
                    pr_global_struct.other = EDICT_TO_PROG(check);
                    PR_ExecuteProgram(pusher.v.blocked);
                }

                // move back any entities we already moved
                for (i = 0; i < num_moved; i++)
                {
                    VectorCopy(moved_from[i], ref moved_edict[i].v.origin);
                    SV_LinkEdict(moved_edict[i], false);
                }

                return;
            }
        }
    }

    // sv_phys.c: SV_Physics_Pusher
    public void SV_Physics_Pusher(Edict ent)
    {
        float thinktime;
        float oldltime;
        float movetime;

        oldltime = ent.v.ltime;

        thinktime = ent.v.nextthink;
        if (thinktime < ent.v.ltime + host_frametime)
        {
            movetime = thinktime - ent.v.ltime;
            if (movetime < 0)
            {
                movetime = 0;
            }
        }
        else
        {
            movetime = (float)host_frametime;
        }

        if (movetime != 0)
        {
            SV_PushMove(ent, movetime); // advances ent->v.ltime if not blocked
        }

        if (thinktime > oldltime && thinktime <= ent.v.ltime)
        {
            ent.v.nextthink = 0;
            pr_global_struct.time = (float)sv.time;
            pr_global_struct.self = EDICT_TO_PROG(ent);
            pr_global_struct.other = EDICT_TO_PROG(sv.edicts[0]);
            PR_ExecuteProgram(ent.v.think);
            if (ent.free)
            {
                return;
            }
        }
    }

    // ===============================================================================
    // CLIENT MOVEMENT
    // ===============================================================================

    // sv_phys.c: SV_CheckStuck
    // This is a big hack to try and fix the rare case of getting stuck in the world clipping hull.
    public void SV_CheckStuck(Edict ent)
    {
        int i, j;
        int z;
        Vec3 org = default;

        if (SV_TestEntityPosition(ent) == null)
        {
            VectorCopy(ent.v.origin, ref ent.v.oldorigin);
            return;
        }

        VectorCopy(ent.v.origin, ref org);
        VectorCopy(ent.v.oldorigin, ref ent.v.origin);
        if (SV_TestEntityPosition(ent) == null)
        {
            Con_DPrintf("Unstuck.\n");
            SV_LinkEdict(ent, true);
            return;
        }

        for (z = 0; z < 18; z++)
        {
            for (i = -1; i <= 1; i++)
            {
                for (j = -1; j <= 1; j++)
                {
                    ent.v.origin[0] = org[0] + i;
                    ent.v.origin[1] = org[1] + j;
                    ent.v.origin[2] = org[2] + z;
                    if (SV_TestEntityPosition(ent) == null)
                    {
                        Con_DPrintf("Unstuck.\n");
                        SV_LinkEdict(ent, true);
                        return;
                    }
                }
            }
        }

        VectorCopy(org, ref ent.v.origin);
        Con_DPrintf("player is stuck.\n");
    }

    // sv_phys.c: SV_CheckWater
    public bool SV_CheckWater(Edict ent)
    {
        Vec3 point = default;
        int cont;

        point[0] = ent.v.origin[0];
        point[1] = ent.v.origin[1];
        point[2] = ent.v.origin[2] + ent.v.mins[2] + 1;

        ent.v.waterlevel = 0;
        ent.v.watertype = CONTENTS_EMPTY;
        cont = SV_PointContents(point);
        if (cont <= CONTENTS_WATER)
        {
            ent.v.watertype = cont;
            ent.v.waterlevel = 1;
            point[2] = (float)(ent.v.origin[2] + ((ent.v.mins[2] + ent.v.maxs[2]) * 0.5));
            cont = SV_PointContents(point);
            if (cont <= CONTENTS_WATER)
            {
                ent.v.waterlevel = 2;
                point[2] = ent.v.origin[2] + ent.v.view_ofs[2];
                cont = SV_PointContents(point);
                if (cont <= CONTENTS_WATER)
                {
                    ent.v.waterlevel = 3;
                }
            }
        }

        return ent.v.waterlevel > 1;
    }

    // sv_phys.c: SV_WallFriction
    public void SV_WallFriction(Edict ent, in Trace trace)
    {
        Vec3 forward = default, right = default, up = default;
        float d, i;
        Vec3 into = default, side = default;

        AngleVectors(ent.v.v_angle, ref forward, ref right, ref up);
        d = DotProduct(trace.plane.normal, forward);

        d = (float)(d + 0.5);
        if (d >= 0)
        {
            return;
        }

        // cut the tangential velocity
        i = DotProduct(trace.plane.normal, ent.v.velocity);
        VectorScale(trace.plane.normal, i, ref into);
        VectorSubtract(ent.v.velocity, into, ref side);

        ent.v.velocity[0] = side[0] * (1 + d);
        ent.v.velocity[1] = side[1] * (1 + d);
    }

    // sv_phys.c: SV_TryUnstick
    // Player has come to a dead stop, possibly due to the problem with limited float precision at some angle joins in
    // the BSP hull.
    //
    // Try fixing by pushing one pixel in each direction.
    //
    // This is a hack, but in the interest of good gameplay...
    public int SV_TryUnstick(Edict ent, in Vec3 oldvel)
    {
        int i;
        Vec3 oldorg = default;
        Vec3 dir = default;
        int clip;
        Trace steptrace = default;

        VectorCopy(ent.v.origin, ref oldorg);
        VectorCopy(vec3_origin, ref dir);

        for (i = 0; i < 8; i++)
        {
            // try pushing a little in an axial direction
            switch (i)
            {
                case 0: dir[0] = 2; dir[1] = 0; break;
                case 1: dir[0] = 0; dir[1] = 2; break;
                case 2: dir[0] = -2; dir[1] = 0; break;
                case 3: dir[0] = 0; dir[1] = -2; break;
                case 4: dir[0] = 2; dir[1] = 2; break;
                case 5: dir[0] = -2; dir[1] = 2; break;
                case 6: dir[0] = 2; dir[1] = -2; break;
                case 7: dir[0] = -2; dir[1] = -2; break;
            }

            SV_PushEntity(ent, dir);

            // retry the original move
            ent.v.velocity[0] = oldvel[0];
            ent.v.velocity[1] = oldvel[1];
            ent.v.velocity[2] = 0;
            clip = SV_FlyMove(ent, (float)0.1, ref steptrace, true);

            if (Math.Abs(oldorg[1] - ent.v.origin[1]) > 4
                || Math.Abs(oldorg[0] - ent.v.origin[0]) > 4)
            {
                return clip;
            }

            // go back to the original pos and try again
            VectorCopy(oldorg, ref ent.v.origin);
        }

        VectorCopy(vec3_origin, ref ent.v.velocity);
        return 7; // still not moving
    }

    private const int STEPSIZE = 18;

    // sv_phys.c: SV_WalkMove
    // Only used by players
    public void SV_WalkMove(Edict ent)
    {
        Vec3 upmove = default, downmove = default;
        Vec3 oldorg = default, oldvel = default;
        Vec3 nosteporg = default, nostepvel = default;
        int clip;
        int oldonground;
        Trace steptrace = default, downtrace;

        // do a regular slide move unless it looks like you ran into a step
        oldonground = X86.FtoI(ent.v.flags) & FL_ONGROUND;
        ent.v.flags = X86.FtoI(ent.v.flags) & ~FL_ONGROUND;

        VectorCopy(ent.v.origin, ref oldorg);
        VectorCopy(ent.v.velocity, ref oldvel);

        clip = SV_FlyMove(ent, (float)host_frametime, ref steptrace, true);

        if ((clip & 2) == 0)
        {
            return; // move didn't block on a step
        }

        if (oldonground == 0 && ent.v.waterlevel == 0)
        {
            return; // don't stair up while jumping
        }

        if (ent.v.movetype != MOVETYPE_WALK)
        {
            return; // gibbed by a trigger
        }

        if (sv_nostep.value != 0)
        {
            return;
        }

        if ((X86.FtoI(sv_player!.v.flags) & FL_WATERJUMP) != 0)
        {
            return;
        }

        VectorCopy(ent.v.origin, ref nosteporg);
        VectorCopy(ent.v.velocity, ref nostepvel);

        // try moving up and forward to go up a step
        VectorCopy(oldorg, ref ent.v.origin); // back to start pos

        VectorCopy(vec3_origin, ref upmove);
        VectorCopy(vec3_origin, ref downmove);
        upmove[2] = STEPSIZE;
        downmove[2] = (float)(-STEPSIZE + (oldvel[2] * host_frametime));

        // move up
        SV_PushEntity(ent, upmove); // FIXME: don't link?

        // move forward
        ent.v.velocity[0] = oldvel[0];
        ent.v.velocity[1] = oldvel[1];
        ent.v.velocity[2] = 0;
        clip = SV_FlyMove(ent, (float)host_frametime, ref steptrace, true);

        // check for stuckness, possibly due to the limited precision of floats in the clipping hulls
        if (clip != 0)
        {
            if (Math.Abs(oldorg[1] - ent.v.origin[1]) < 0.03125
                && Math.Abs(oldorg[0] - ent.v.origin[0]) < 0.03125)
            {
                // stepping up didn't make any progress
                clip = SV_TryUnstick(ent, oldvel);
            }
        }

        // extra friction based on view angle
        if ((clip & 2) != 0)
        {
            SV_WallFriction(ent, steptrace);
        }

        // move down
        downtrace = SV_PushEntity(ent, downmove); // FIXME: don't link?

        if (downtrace.plane.normal[2] > 0.7)
        {
            if (ent.v.solid == SOLID_BSP)
            {
                ent.v.flags = X86.FtoI(ent.v.flags) | FL_ONGROUND;
                ent.v.groundentity = EDICT_TO_PROG(downtrace.ent!);
            }
        }
        else
        {
            // if the push down didn't end up on good ground, use the move without the step up. This happens near wall
            // / slope combinations, and can cause the player to hop up higher on a slope too steep to climb
            VectorCopy(nosteporg, ref ent.v.origin);
            VectorCopy(nostepvel, ref ent.v.velocity);
        }
    }

    // sv_phys.c: SV_Physics_Client
    // Player character actions
    public void SV_Physics_Client(Edict ent, int num)
    {
        if (!svs.clients[num - 1].active)
        {
            return; // unconnected slot
        }

        // call standard client pre-think
        pr_global_struct.time = (float)sv.time;
        pr_global_struct.self = EDICT_TO_PROG(ent);
        PR_ExecuteProgram(pr_global_struct.PlayerPreThink);

        // do a move
        SV_CheckVelocity(ent);

        // decide which move function to call
        switch (X86.FtoI(ent.v.movetype))
        {
            case MOVETYPE_NONE:
                if (!SV_RunThink(ent))
                {
                    return;
                }

                break;

            case MOVETYPE_WALK:
                if (!SV_RunThink(ent))
                {
                    return;
                }

                if (!SV_CheckWater(ent) && (X86.FtoI(ent.v.flags) & FL_WATERJUMP) == 0)
                {
                    SV_AddGravity(ent);
                }

                SV_CheckStuck(ent);
                SV_WalkMove(ent);

                break;

            case MOVETYPE_TOSS:
            case MOVETYPE_BOUNCE:
                SV_Physics_Toss(ent);
                break;

            case MOVETYPE_FLY:
                if (!SV_RunThink(ent))
                {
                    return;
                }

                SV_FlyMove(ent, (float)host_frametime);
                break;

            case MOVETYPE_NOCLIP:
                if (!SV_RunThink(ent))
                {
                    return;
                }

                VectorMA(ent.v.origin, (float)host_frametime, ent.v.velocity, ref ent.v.origin);
                break;

            default:
                throw Sys_Error("SV_Physics_client: bad movetype %i", X86.FtoI(ent.v.movetype));
        }

        // call standard player post-think
        SV_LinkEdict(ent, true);

        pr_global_struct.time = (float)sv.time;
        pr_global_struct.self = EDICT_TO_PROG(ent);
        PR_ExecuteProgram(pr_global_struct.PlayerPostThink);
    }

    // ============================================================================

    // sv_phys.c: SV_Physics_None
    // Non moving objects can only think
    public void SV_Physics_None(Edict ent)
    {
        // regular thinking
        SV_RunThink(ent);
    }

    // sv_phys.c: SV_Physics_Noclip
    // A moving object that doesn't obey physics
    public void SV_Physics_Noclip(Edict ent)
    {
        // regular thinking
        if (!SV_RunThink(ent))
        {
            return;
        }

        VectorMA(ent.v.angles, (float)host_frametime, ent.v.avelocity, ref ent.v.angles);
        VectorMA(ent.v.origin, (float)host_frametime, ent.v.velocity, ref ent.v.origin);

        SV_LinkEdict(ent, false);
    }

    // ==============================================================================
    // TOSS / BOUNCE
    // ==============================================================================

    // sv_phys.c: SV_CheckWaterTransition
    public void SV_CheckWaterTransition(Edict ent)
    {
        int cont;

        cont = SV_PointContents(ent.v.origin);
        if (ent.v.watertype == 0)
        {
            // just spawned here
            ent.v.watertype = cont;
            ent.v.waterlevel = 1;
            return;
        }

        if (cont <= CONTENTS_WATER)
        {
            if (ent.v.watertype == CONTENTS_EMPTY)
            {
                // just crossed into water
                SV_StartSound(ent, 0, "misc/h2ohit1.wav", 255, 1);
            }

            ent.v.watertype = cont;
            ent.v.waterlevel = 1;
        }
        else
        {
            if (ent.v.watertype != CONTENTS_EMPTY)
            {
                // just crossed into water
                SV_StartSound(ent, 0, "misc/h2ohit1.wav", 255, 1);
            }

            ent.v.watertype = CONTENTS_EMPTY;
            ent.v.waterlevel = cont;
        }
    }

    // sv_phys.c: SV_Physics_Toss
    // Toss, bounce, and fly movement. When onground, do nothing.
    public void SV_Physics_Toss(Edict ent)
    {
        Trace trace;
        Vec3 move = default;
        float backoff;

        // regular thinking
        if (!SV_RunThink(ent))
        {
            return;
        }

        // if onground, return without moving
        if ((X86.FtoI(ent.v.flags) & FL_ONGROUND) != 0)
        {
            return;
        }

        SV_CheckVelocity(ent);

        // add gravity
        if (ent.v.movetype != MOVETYPE_FLY
            && ent.v.movetype != MOVETYPE_FLYMISSILE)
        {
            SV_AddGravity(ent);
        }

        // move angles
        VectorMA(ent.v.angles, (float)host_frametime, ent.v.avelocity, ref ent.v.angles);

        // move origin
        VectorScale(ent.v.velocity, (float)host_frametime, ref move);
        trace = SV_PushEntity(ent, move);
        if (trace.fraction == 1)
        {
            return;
        }

        if (ent.free)
        {
            return;
        }

        if (ent.v.movetype == MOVETYPE_BOUNCE)
        {
            backoff = 1.5f;
        }
        else
        {
            backoff = 1;
        }

        ClipVelocity(ref ent.v.velocity, trace.plane.normal, ref ent.v.velocity, backoff);

        // stop if on ground
        if (trace.plane.normal[2] > 0.7)
        {
            if (ent.v.velocity[2] < 60 || ent.v.movetype != MOVETYPE_BOUNCE)
            {
                ent.v.flags = X86.FtoI(ent.v.flags) | FL_ONGROUND;
                ent.v.groundentity = EDICT_TO_PROG(trace.ent!);
                VectorCopy(vec3_origin, ref ent.v.velocity);
                VectorCopy(vec3_origin, ref ent.v.avelocity);
            }
        }

        // check for in water
        SV_CheckWaterTransition(ent);
    }

    // ===============================================================================
    // STEPPING MOVEMENT
    // ===============================================================================

    // sv_phys.c: SV_Physics_Step
    // Monsters freefall when they don't have a ground entity, otherwise all movement is done with discrete steps.
    //
    // This is also used for objects that have become still on the ground, but will fall if the floor is pulled out
    // from under them.
    public void SV_Physics_Step(Edict ent)
    {
        bool hitsound;

        // freefall if not onground
        if ((X86.FtoI(ent.v.flags) & (FL_ONGROUND | FL_FLY | FL_SWIM)) == 0)
        {
            hitsound = ent.v.velocity[2] < sv_gravity.value * -0.1;

            SV_AddGravity(ent);
            SV_CheckVelocity(ent);
            SV_FlyMove(ent, (float)host_frametime);
            SV_LinkEdict(ent, true);

            if ((X86.FtoI(ent.v.flags) & FL_ONGROUND) != 0)
            {
                // just hit ground
                if (hitsound)
                {
                    SV_StartSound(ent, 0, "demon/dland2.wav", 255, 1);
                }
            }
        }

        // regular thinking
        SV_RunThink(ent);

        SV_CheckWaterTransition(ent);
    }

    // ============================================================================

    // sv_phys.c: SV_Physics
    public void SV_Physics()
    {
        int i;
        Edict ent;

        // let the progs know that a new frame has started
        pr_global_struct.self = EDICT_TO_PROG(sv.edicts[0]);
        pr_global_struct.other = EDICT_TO_PROG(sv.edicts[0]);
        pr_global_struct.time = (float)sv.time;
        PR_ExecuteProgram(pr_global_struct.StartFrame);

        // treat each object in turn
        ent = sv.edicts[0];
        for (i = 0; i < sv.num_edicts; i++, ent = NEXT_EDICT(ent))
        {
            if (ent.free)
            {
                continue;
            }

            if (pr_global_struct.force_retouch != 0)
            {
                SV_LinkEdict(ent, true); // force retouch even for stationary
            }

            if (i > 0 && i <= svs.maxclients)
            {
                SV_Physics_Client(ent, i);
            }
            else if (ent.v.movetype == MOVETYPE_PUSH)
            {
                SV_Physics_Pusher(ent);
            }
            else if (ent.v.movetype == MOVETYPE_NONE)
            {
                SV_Physics_None(ent);
            }
            else if (ent.v.movetype == MOVETYPE_NOCLIP)
            {
                SV_Physics_Noclip(ent);
            }
            else if (ent.v.movetype == MOVETYPE_STEP)
            {
                SV_Physics_Step(ent);
            }
            else if (ent.v.movetype == MOVETYPE_TOSS
                || ent.v.movetype == MOVETYPE_BOUNCE
                || ent.v.movetype == MOVETYPE_FLY
                || ent.v.movetype == MOVETYPE_FLYMISSILE)
            {
                SV_Physics_Toss(ent);
            }
            else
            {
                throw Sys_Error("SV_Physics: bad movetype %i", X86.FtoI(ent.v.movetype));
            }
        }

        if (pr_global_struct.force_retouch != 0)
        {
            pr_global_struct.force_retouch--;
        }

        sv.time += host_frametime;
    }
}
