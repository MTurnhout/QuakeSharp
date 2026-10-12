using QuakeSharp.Core.Common;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Progs;
using QuakeSharp.Core.Server;
using static QuakeSharp.Core.Common.MathLib;
using static QuakeSharp.Core.Model.BspFile;
using static QuakeSharp.Core.Progs.PrComp;
using static QuakeSharp.Core.QuakeDef;

namespace QuakeSharp.Core;

// sv_move.c: monster movement
public sealed partial class QuakeEngine
{
    private int c_yes, c_no;

    // sv_move.c: SV_CheckBottom
    // Returns false if any part of the bottom of the entity is off an edge that is not a staircase.
    public bool SV_CheckBottom(Edict ent)
    {
        Vec3 mins = default, maxs = default, start = default, stop = default;
        Trace trace;
        int x, y;
        float mid, bottom;

        VectorAdd(ent.v.origin, ent.v.mins, ref mins);
        VectorAdd(ent.v.origin, ent.v.maxs, ref maxs);

        // if all of the points under the corners are solid world, don't bother with the tougher checks
        // the corners must be within 16 of the midpoint
        start[2] = mins[2] - 1;
        for (x = 0; x <= 1; x++)
        {
            for (y = 0; y <= 1; y++)
            {
                start[0] = x != 0 ? maxs[0] : mins[0];
                start[1] = y != 0 ? maxs[1] : mins[1];
                if (SV_PointContents(start) != CONTENTS_SOLID)
                {
                    goto realcheck;
                }
            }
        }

        c_yes++;
        return true; // we got out easy

    realcheck:
        c_no++;

        // check it for real...
        start[2] = mins[2];

        // the midpoint must be within 16 of the bottom
        start[0] = stop[0] = (float)((mins[0] + maxs[0]) * 0.5);
        start[1] = stop[1] = (float)((mins[1] + maxs[1]) * 0.5);
        stop[2] = start[2] - (2 * STEPSIZE);
        trace = SV_Move(start, vec3_origin, vec3_origin, stop, 1, ent);

        if (trace.fraction == 1.0)
        {
            return false;
        }

        mid = bottom = trace.endpos[2];

        // the corners must be within 16 of the midpoint
        for (x = 0; x <= 1; x++)
        {
            for (y = 0; y <= 1; y++)
            {
                start[0] = stop[0] = x != 0 ? maxs[0] : mins[0];
                start[1] = stop[1] = y != 0 ? maxs[1] : mins[1];

                trace = SV_Move(start, vec3_origin, vec3_origin, stop, 1, ent);

                if (trace.fraction != 1.0 && trace.endpos[2] > bottom)
                {
                    bottom = trace.endpos[2];
                }

                if (trace.fraction == 1.0 || mid - trace.endpos[2] > STEPSIZE)
                {
                    return false;
                }
            }
        }

        c_yes++;
        return true;
    }

    // sv_move.c: SV_movestep
    // Called by monster program code. The move will be adjusted for slopes and stairs, but if the move isn't
    // possible, no move is done, false is returned, and pr_global_struct->trace_normal is set to the normal of the
    // blocking wall
    public bool SV_movestep(Edict ent, in Vec3 move, bool relink)
    {
        float dz;
        Vec3 oldorg = default, neworg = default, end = default;
        Trace trace;
        int i;
        Edict enemy;

        // try the move
        VectorCopy(ent.v.origin, ref oldorg);
        VectorAdd(ent.v.origin, move, ref neworg);

        // flying monsters don't step up
        if ((X86.FtoI(ent.v.flags) & (FL_SWIM | FL_FLY)) != 0)
        {
            // try one move with vertical motion, then one without
            for (i = 0; i < 2; i++)
            {
                VectorAdd(ent.v.origin, move, ref neworg);
                enemy = PROG_TO_EDICT(ent.v.enemy);
                if (i == 0 && enemy != sv.edicts[0])
                {
                    dz = ent.v.origin[2] - PROG_TO_EDICT(ent.v.enemy).v.origin[2];
                    if (dz > 40)
                    {
                        neworg[2] -= 8;
                    }

                    if (dz < 30)
                    {
                        neworg[2] += 8;
                    }
                }

                trace = SV_Move(ent.v.origin, ent.v.mins, ent.v.maxs, neworg, 0, ent);

                if (trace.fraction == 1)
                {
                    if ((X86.FtoI(ent.v.flags) & FL_SWIM) != 0 && SV_PointContents(trace.endpos) == CONTENTS_EMPTY)
                    {
                        return false; // swim monster left water
                    }

                    VectorCopy(trace.endpos, ref ent.v.origin);
                    if (relink)
                    {
                        SV_LinkEdict(ent, true);
                    }

                    return true;
                }

                if (enemy == sv.edicts[0])
                {
                    break;
                }
            }

            return false;
        }

        // push down from a step height above the wished position
        neworg[2] += STEPSIZE;
        VectorCopy(neworg, ref end);
        end[2] -= STEPSIZE * 2;

        trace = SV_Move(neworg, ent.v.mins, ent.v.maxs, end, 0, ent);

        if (trace.allsolid)
        {
            return false;
        }

        if (trace.startsolid)
        {
            neworg[2] -= STEPSIZE;
            trace = SV_Move(neworg, ent.v.mins, ent.v.maxs, end, 0, ent);
            if (trace.allsolid || trace.startsolid)
            {
                return false;
            }
        }

        if (trace.fraction == 1)
        {
            // if monster had the ground pulled out, go ahead and fall
            if ((X86.FtoI(ent.v.flags) & FL_PARTIALGROUND) != 0)
            {
                VectorAdd(ent.v.origin, move, ref ent.v.origin);
                if (relink)
                {
                    SV_LinkEdict(ent, true);
                }

                ent.v.flags = X86.FtoI(ent.v.flags) & ~FL_ONGROUND;
                return true;
            }

            return false; // walked off an edge
        }

        // check point traces down for dangling corners
        VectorCopy(trace.endpos, ref ent.v.origin);

        if (!SV_CheckBottom(ent))
        {
            if ((X86.FtoI(ent.v.flags) & FL_PARTIALGROUND) != 0)
            {
                // entity had floor mostly pulled out from underneath it and is trying to correct
                if (relink)
                {
                    SV_LinkEdict(ent, true);
                }

                return true;
            }

            VectorCopy(oldorg, ref ent.v.origin);
            return false;
        }

        if ((X86.FtoI(ent.v.flags) & FL_PARTIALGROUND) != 0)
        {
            ent.v.flags = X86.FtoI(ent.v.flags) & ~FL_PARTIALGROUND;
        }

        ent.v.groundentity = EDICT_TO_PROG(trace.ent!);

        // the move is ok
        if (relink)
        {
            SV_LinkEdict(ent, true);
        }

        return true;
    }

    // ============================================================================

    // sv_move.c: SV_StepDirection
    // Turns to the movement direction, and walks the current distance if facing it.
    public bool SV_StepDirection(Edict ent, float yaw, float dist)
    {
        Vec3 move = default, oldorigin = default;
        float delta;

        ent.v.ideal_yaw = yaw;
        PF_changeyaw();

        yaw = (float)(yaw * M_PI * 2 / 360);
        move[0] = (float)(Libm.cos(yaw) * dist);
        move[1] = (float)(Libm.sin(yaw) * dist);
        move[2] = 0;

        VectorCopy(ent.v.origin, ref oldorigin);
        if (SV_movestep(ent, move, false))
        {
            delta = ent.v.angles[QuakeDef.YAW] - ent.v.ideal_yaw;
            if (delta > 45 && delta < 315)
            {
                // not turned far enough, so don't take the step
                VectorCopy(oldorigin, ref ent.v.origin);
            }

            SV_LinkEdict(ent, true);
            return true;
        }

        SV_LinkEdict(ent, true);

        return false;
    }

    // sv_move.c: SV_FixCheckBottom
    public static void SV_FixCheckBottom(Edict ent) => ent.v.flags = X86.FtoI(ent.v.flags) | FL_PARTIALGROUND;

    private const int DI_NODIR = -1;

    // sv_move.c: SV_NewChaseDir
    public void SV_NewChaseDir(Edict actor, Edict enemy, float dist)
    {
        float deltax, deltay;
        Span<float> d = stackalloc float[3];
        float tdir, olddir, turnaround;

        olddir = anglemod(X86.FtoI(actor.v.ideal_yaw / 45) * 45);
        turnaround = anglemod(olddir - 180);

        deltax = enemy.v.origin[0] - actor.v.origin[0];
        deltay = enemy.v.origin[1] - actor.v.origin[1];
        if (deltax > 10)
        {
            d[1] = 0;
        }
        else if (deltax < -10)
        {
            d[1] = 180;
        }
        else
        {
            d[1] = DI_NODIR;
        }

        if (deltay < -10)
        {
            d[2] = 270;
        }
        else if (deltay > 10)
        {
            d[2] = 90;
        }
        else
        {
            d[2] = DI_NODIR;
        }

        // try direct route
        if (d[1] != DI_NODIR && d[2] != DI_NODIR)
        {
            if (d[1] == 0)
            {
                tdir = d[2] == 90 ? 45 : 315;
            }
            else
            {
                tdir = d[2] == 90 ? 135 : 215;
            }

            if (tdir != turnaround && SV_StepDirection(actor, tdir, dist))
            {
                return;
            }
        }

        // try other directions (abs is the C's int abs: the deltas are truncated)
        if (((rand() & 3) & 1) != 0 || CAbs(X86.FtoI(deltay)) > CAbs(X86.FtoI(deltax)))
        {
            tdir = d[1];
            d[1] = d[2];
            d[2] = tdir;
        }

        if (d[1] != DI_NODIR && d[1] != turnaround
            && SV_StepDirection(actor, d[1], dist))
        {
            return;
        }

        if (d[2] != DI_NODIR && d[2] != turnaround
            && SV_StepDirection(actor, d[2], dist))
        {
            return;
        }

        // there is no direct path to the player, so pick another direction
        if (olddir != DI_NODIR && SV_StepDirection(actor, olddir, dist))
        {
            return;
        }

        if ((rand() & 1) != 0)
        {
            // randomly determine direction of search
            for (tdir = 0; tdir <= 315; tdir += 45)
            {
                if (tdir != turnaround && SV_StepDirection(actor, tdir, dist))
                {
                    return;
                }
            }
        }
        else
        {
            for (tdir = 315; tdir >= 0; tdir -= 45)
            {
                if (tdir != turnaround && SV_StepDirection(actor, tdir, dist))
                {
                    return;
                }
            }
        }

        if (turnaround != DI_NODIR && SV_StepDirection(actor, turnaround, dist))
        {
            return;
        }

        actor.v.ideal_yaw = olddir; // can't move

        // if a bridge was pulled out from underneath a monster, it may not have a valid standing position at all
        if (!SV_CheckBottom(actor))
        {
            SV_FixCheckBottom(actor);
        }
    }

    // the C runtime's abs (abs(INT_MIN) is INT_MIN)
    private static int CAbs(int x) => x < 0 ? unchecked(-x) : x;

    // sv_move.c: SV_CloseEnough
    public static bool SV_CloseEnough(Edict ent, Edict goal, float dist)
    {
        int i;

        for (i = 0; i < 3; i++)
        {
            if (goal.v.absmin[i] > ent.v.absmax[i] + dist)
            {
                return false;
            }

            if (goal.v.absmax[i] < ent.v.absmin[i] - dist)
            {
                return false;
            }
        }

        return true;
    }

    // sv_move.c: SV_MoveToGoal
    public void SV_MoveToGoal()
    {
        Edict ent, goal;
        float dist;

        ent = PROG_TO_EDICT(pr_global_struct.self);
        goal = PROG_TO_EDICT(ent.v.goalentity);
        dist = G_FLOAT(OFS_PARM0);

        if ((X86.FtoI(ent.v.flags) & (FL_ONGROUND | FL_FLY | FL_SWIM)) == 0)
        {
            G_FLOAT(OFS_RETURN) = 0;
            return;
        }

        // if the next step hits the enemy, return immediately
        if (PROG_TO_EDICT(ent.v.enemy) != sv.edicts[0] && SV_CloseEnough(ent, goal, dist))
        {
            return;
        }

        // bump around...
        if ((rand() & 3) == 1 || !SV_StepDirection(ent, ent.v.ideal_yaw, dist))
        {
            SV_NewChaseDir(ent, goal, dist);
        }
    }
}
