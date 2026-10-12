using QuakeSharp.Core.Common;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Server;
using static QuakeSharp.Core.Common.MathLib;

namespace QuakeSharp.Core;

// chase.c: chase camera code
public sealed partial class QuakeEngine
{
    public readonly Cvar chase_back = new("chase_back", "100");
    public readonly Cvar chase_up = new("chase_up", "16");
    public readonly Cvar chase_right = new("chase_right", "0");
    public readonly Cvar chase_active = new("chase_active", "0");

    public Vec3 chase_pos;
    public Vec3 chase_angles;

    public Vec3 chase_dest;
    public Vec3 chase_dest_angles;

    // chase.c: Chase_Init
    public void Chase_Init()
    {
        Cvar_RegisterVariable(chase_back);
        Cvar_RegisterVariable(chase_up);
        Cvar_RegisterVariable(chase_right);
        Cvar_RegisterVariable(chase_active);
    }

    // chase.c: Chase_Reset
    public static void Chase_Reset()
    {
        // for respawning and teleporting
        // start position 12 units behind head
    }

    // chase.c: TraceLine. It calls SV_RecursiveHullCheck without a prototype, so the C passes its 1 as an int: p2f
    // is the float with the bits of 1 (design.md §3.2).
    public void TraceLine(in Vec3 start, in Vec3 end, ref Vec3 impact)
    {
        Trace trace = new();

        SV_RecursiveHullCheck(cl.worldmodel!.hulls[0], 0, 0, BitConverter.Int32BitsToSingle(1), start, end, trace);

        VectorCopy(trace.endpos, ref impact);
    }

    // chase.c: Chase_Update
    public void Chase_Update()
    {
        int i;
        float dist;
        Vec3 forward = default, up = default, right = default;
        Vec3 dest = default, stop = default;

        // if can't see player, reset
        AngleVectors(cl.viewangles, ref forward, ref right, ref up);

        // calc exact destination
        for (i = 0; i < 3; i++)
        {
            chase_dest[i] = r_refdef.vieworg[i]
                - (forward[i] * chase_back.value)
                - (right[i] * chase_right.value);
        }

        chase_dest[2] = r_refdef.vieworg[2] + chase_up.value;

        // find the spot the player is looking at
        VectorMA(r_refdef.vieworg, 4096, forward, ref dest);
        TraceLine(r_refdef.vieworg, dest, ref stop);

        // calculate pitch to look at the same spot from camera
        VectorSubtract(stop, r_refdef.vieworg, ref stop);
        dist = DotProduct(stop, forward);
        if (dist < 1)
        {
            dist = 1;
        }

        r_refdef.viewangles[PITCH] = (float)(-Libm.atan(stop[2] / dist) / M_PI * 180);

        // move towards destination
        VectorCopy(chase_dest, ref r_refdef.vieworg);
    }
}
