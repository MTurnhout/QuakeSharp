using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Progs;
using QuakeSharp.Core.Server;
using QuakeSharp.Core.UI;
using static QuakeSharp.Core.Common.MathLib;
using static QuakeSharp.Core.Common.QString;
using static QuakeSharp.Core.Protocol;
using static QuakeSharp.Core.QuakeDef;

namespace QuakeSharp.Core;

// sv_user.c: server code for moving users
public sealed partial class QuakeEngine
{
    public Edict? sv_player;

    // sv_user.c: forward, right, up (static)
    private Vec3 sv_forward, sv_right, sv_up;

    public Vec3 wishdir;
    public float wishspeed;

    // sv_user.c: angles, origin, velocity: pointers into sv_player's fields, set by SV_ClientThink (the edict whose
    // fields they are)
    private Edict? sv_user_ent;

    private ref Vec3 angles => ref sv_user_ent!.v.angles;

    private ref Vec3 origin => ref sv_user_ent!.v.origin;

    private ref Vec3 velocity => ref sv_user_ent!.v.velocity;

    public bool onground;

    /// <summary>sv_user.c: cmd</summary>
    public UserCmd sv_cmd;

    private const int MAX_FORWARD = 6;

    // sv_user.c: SV_SetIdealPitch
    public void SV_SetIdealPitch()
    {
        float angleval, sinval, cosval;
        Trace tr;
        Vec3 top = default, bottom = default;
        Span<float> z = stackalloc float[MAX_FORWARD];
        int i, j;
        int step, dir, steps;

        if ((X86.FtoI(sv_player!.v.flags) & FL_ONGROUND) == 0)
        {
            return;
        }

        angleval = (float)(sv_player.v.angles[QuakeDef.YAW] * M_PI * 2 / 360);
        sinval = (float)Libm.sin(angleval);
        cosval = (float)Libm.cos(angleval);

        for (i = 0; i < MAX_FORWARD; i++)
        {
            top[0] = sv_player.v.origin[0] + (cosval * (i + 3) * 12);
            top[1] = sv_player.v.origin[1] + (sinval * (i + 3) * 12);
            top[2] = sv_player.v.origin[2] + sv_player.v.view_ofs[2];

            bottom[0] = top[0];
            bottom[1] = top[1];
            bottom[2] = top[2] - 160;

            tr = SV_Move(top, vec3_origin, vec3_origin, bottom, 1, sv_player);
            if (tr.allsolid)
            {
                return; // looking at a wall, leave ideal the way is was
            }

            if (tr.fraction == 1)
            {
                return; // near a dropoff
            }

            z[i] = top[2] + (tr.fraction * (bottom[2] - top[2]));
        }

        dir = 0;
        steps = 0;
        for (j = 1; j < i; j++)
        {
            step = X86.FtoI(z[j] - z[j - 1]);
            if (step > -ON_EPSILON && step < ON_EPSILON)
            {
                continue;
            }

            if (dir != 0 && (step - dir > ON_EPSILON || step - dir < -ON_EPSILON))
            {
                return; // mixed changes
            }

            steps++;
            dir = step;
        }

        if (dir == 0)
        {
            sv_player.v.idealpitch = 0;
            return;
        }

        if (steps < 2)
        {
            return;
        }

        sv_player.v.idealpitch = -dir * sv_idealpitchscale.value;
    }

    // sv_user.c: SV_UserFriction
    public void SV_UserFriction()
    {
        float speed, newspeed, control;
        Vec3 start = default, stop = default;
        float friction;
        Trace trace;

        ref Vec3 vel = ref velocity;

        speed = (float)Math.Sqrt((vel[0] * vel[0]) + (vel[1] * vel[1]));
        if (speed == 0)
        {
            return;
        }

        // if the leading edge is over a dropoff, increase friction
        start[0] = stop[0] = origin[0] + (vel[0] / speed * 16);
        start[1] = stop[1] = origin[1] + (vel[1] / speed * 16);
        start[2] = origin[2] + sv_player!.v.mins[2];
        stop[2] = start[2] - 34;

        trace = SV_Move(start, vec3_origin, vec3_origin, stop, 1, sv_player);

        if (trace.fraction == 1.0)
        {
            friction = sv_friction.value * sv_edgefriction.value;
        }
        else
        {
            friction = sv_friction.value;
        }

        // apply friction
        control = speed < sv_stopspeed.value ? sv_stopspeed.value : speed;
        newspeed = (float)(speed - (host_frametime * control * friction));

        if (newspeed < 0)
        {
            newspeed = 0;
        }

        newspeed /= speed;

        vel[0] = vel[0] * newspeed;
        vel[1] = vel[1] * newspeed;
        vel[2] = vel[2] * newspeed;
    }

    // sv_user.c: SV_Accelerate
    public void SV_Accelerate()
    {
        int i;
        float addspeed, accelspeed, currentspeed;

        currentspeed = DotProduct(velocity, wishdir);
        addspeed = wishspeed - currentspeed;
        if (addspeed <= 0)
        {
            return;
        }

        accelspeed = (float)(sv_accelerate.value * host_frametime * wishspeed);
        if (accelspeed > addspeed)
        {
            accelspeed = addspeed;
        }

        for (i = 0; i < 3; i++)
        {
            velocity[i] += accelspeed * wishdir[i];
        }
    }

    // sv_user.c: SV_AirAccelerate
    public void SV_AirAccelerate(ref Vec3 wishveloc)
    {
        int i;
        float addspeed, wishspd, accelspeed, currentspeed;

        wishspd = VectorNormalize(ref wishveloc);
        if (wishspd > 30)
        {
            wishspd = 30;
        }

        currentspeed = DotProduct(velocity, wishveloc);
        addspeed = wishspd - currentspeed;
        if (addspeed <= 0)
        {
            return;
        }

        accelspeed = (float)(sv_accelerate.value * wishspeed * host_frametime);
        if (accelspeed > addspeed)
        {
            accelspeed = addspeed;
        }

        for (i = 0; i < 3; i++)
        {
            velocity[i] += accelspeed * wishveloc[i];
        }
    }

    // sv_user.c: DropPunchAngle
    public void DropPunchAngle()
    {
        float len;

        len = VectorNormalize(ref sv_player!.v.punchangle);

        len = (float)(len - (10 * host_frametime));
        if (len < 0)
        {
            len = 0;
        }

        VectorScale(sv_player.v.punchangle, len, ref sv_player.v.punchangle);
    }

    // sv_user.c: SV_WaterMove
    public void SV_WaterMove()
    {
        int i;
        Vec3 wishvel = default;
        float speed, newspeed, wishspeed, addspeed, accelspeed;

        // user intentions
        AngleVectors(sv_player!.v.v_angle, ref sv_forward, ref sv_right, ref sv_up);

        for (i = 0; i < 3; i++)
        {
            wishvel[i] = (sv_forward[i] * sv_cmd.forwardmove) + (sv_right[i] * sv_cmd.sidemove);
        }

        if (sv_cmd.forwardmove == 0 && sv_cmd.sidemove == 0 && sv_cmd.upmove == 0)
        {
            wishvel[2] -= 60; // drift towards bottom
        }
        else
        {
            wishvel[2] += sv_cmd.upmove;
        }

        wishspeed = Length(wishvel);
        if (wishspeed > sv_maxspeed.value)
        {
            VectorScale(wishvel, sv_maxspeed.value / wishspeed, ref wishvel);
            wishspeed = sv_maxspeed.value;
        }

        wishspeed = (float)(wishspeed * 0.7);

        // water friction
        speed = Length(velocity);
        if (speed != 0)
        {
            newspeed = (float)(speed - (host_frametime * speed * sv_friction.value));
            if (newspeed < 0)
            {
                newspeed = 0;
            }

            VectorScale(velocity, newspeed / speed, ref velocity);
        }
        else
        {
            newspeed = 0;
        }

        // water acceleration
        if (wishspeed == 0)
        {
            return;
        }

        addspeed = wishspeed - newspeed;
        if (addspeed <= 0)
        {
            return;
        }

        VectorNormalize(ref wishvel);
        accelspeed = (float)(sv_accelerate.value * wishspeed * host_frametime);
        if (accelspeed > addspeed)
        {
            accelspeed = addspeed;
        }

        for (i = 0; i < 3; i++)
        {
            velocity[i] += accelspeed * wishvel[i];
        }
    }

    // sv_user.c: SV_WaterJump
    public void SV_WaterJump()
    {
        if (sv.time > sv_player!.v.teleport_time
            || sv_player.v.waterlevel == 0)
        {
            sv_player.v.flags = X86.FtoI(sv_player.v.flags) & ~FL_WATERJUMP;
            sv_player.v.teleport_time = 0;
        }

        sv_player.v.velocity[0] = sv_player.v.movedir[0];
        sv_player.v.velocity[1] = sv_player.v.movedir[1];
    }

    // sv_user.c: SV_AirMove
    public void SV_AirMove()
    {
        int i;
        Vec3 wishvel = default;
        float fmove, smove;

        AngleVectors(sv_player!.v.angles, ref sv_forward, ref sv_right, ref sv_up);

        fmove = sv_cmd.forwardmove;
        smove = sv_cmd.sidemove;

        // hack to not let you back into teleporter
        if (sv.time < sv_player.v.teleport_time && fmove < 0)
        {
            fmove = 0;
        }

        for (i = 0; i < 3; i++)
        {
            wishvel[i] = (sv_forward[i] * fmove) + (sv_right[i] * smove);
        }

        if (X86.FtoI(sv_player.v.movetype) != MOVETYPE_WALK)
        {
            wishvel[2] = sv_cmd.upmove;
        }
        else
        {
            wishvel[2] = 0;
        }

        VectorCopy(wishvel, ref wishdir);
        wishspeed = VectorNormalize(ref wishdir);
        if (wishspeed > sv_maxspeed.value)
        {
            VectorScale(wishvel, sv_maxspeed.value / wishspeed, ref wishvel);
            wishspeed = sv_maxspeed.value;
        }

        if (sv_player.v.movetype == MOVETYPE_NOCLIP)
        {
            // noclip
            VectorCopy(wishvel, ref velocity);
        }
        else if (onground)
        {
            SV_UserFriction();
            SV_Accelerate();
        }
        else
        {
            // not on ground, so little effect on velocity
            SV_AirAccelerate(ref wishvel);
        }
    }

    // sv_user.c: SV_ClientThink
    // the move fields specify an intended velocity in pix/sec
    // the angle fields specify an exact angular motion in degrees
    public void SV_ClientThink()
    {
        Vec3 v_angle = default;

        if (sv_player!.v.movetype == MOVETYPE_NONE)
        {
            return;
        }

        onground = (X86.FtoI(sv_player.v.flags) & FL_ONGROUND) != 0;

        sv_user_ent = sv_player; // origin = sv_player->v.origin; velocity = sv_player->v.velocity;

        DropPunchAngle();

        // if dead, behave differently
        if (sv_player.v.health <= 0)
        {
            return;
        }

        // angles
        // show 1/3 the pitch angle and all the roll angle
        sv_cmd = host_client!.cmd;

        VectorAdd(sv_player.v.v_angle, sv_player.v.punchangle, ref v_angle);
        angles[QuakeDef.ROLL] = V_CalcRoll(sv_player.v.angles, sv_player.v.velocity) * 4;
        if (sv_player.v.fixangle == 0)
        {
            angles[QuakeDef.PITCH] = -v_angle[QuakeDef.PITCH] / 3;
            angles[QuakeDef.YAW] = v_angle[QuakeDef.YAW];
        }

        if ((X86.FtoI(sv_player.v.flags) & FL_WATERJUMP) != 0)
        {
            SV_WaterJump();
            return;
        }

        // walk
        if (sv_player.v.waterlevel >= 2
            && sv_player.v.movetype != MOVETYPE_NOCLIP)
        {
            SV_WaterMove();
            return;
        }

        SV_AirMove();
    }

    // sv_user.c: SV_ReadClientMove
    public void SV_ReadClientMove(ref UserCmd move)
    {
        int i;
        Vec3 angle = default;
        int bits;

        // read ping time
        host_client!.ping_times[host_client.num_pings % NUM_PING_TIMES] = (float)(sv.time - MSG_ReadFloat());
        host_client.num_pings++;

        // read current angles
        for (i = 0; i < 3; i++)
        {
            angle[i] = MSG_ReadAngle();
        }

        VectorCopy(angle, ref host_client.edict!.v.v_angle);

        // read movement
        move.forwardmove = MSG_ReadShort();
        move.sidemove = MSG_ReadShort();
        move.upmove = MSG_ReadShort();

        // read buttons
        bits = MSG_ReadByte();
        host_client.edict.v.button0 = bits & 1;
        host_client.edict.v.button2 = (bits & 2) >> 1;

        i = MSG_ReadByte();
        if (i != 0)
        {
            host_client.edict.v.impulse = i;
        }
    }

    // sv_user.c: SV_ReadClientMessage
    // Returns false if the client should be killed
    public bool SV_ReadClientMessage()
    {
        int ret;
        int cmd;
        string s;

        do
        {
        nextmsg:
            ret = NET_GetMessage(host_client!.netconnection);
            if (ret == -1)
            {
                Sys_Printf("SV_ReadClientMessage: NET_GetMessage failed\n");
                return false;
            }

            if (ret == 0)
            {
                return true;
            }

            MSG_BeginReading();

            while (true)
            {
                if (!host_client.active)
                {
                    return false; // a command caused an error
                }

                if (msg_badread)
                {
                    Sys_Printf("SV_ReadClientMessage: badread\n");
                    return false;
                }

                cmd = MSG_ReadChar();

                switch (cmd)
                {
                    case -1:
                        goto nextmsg; // end of message

                    default:
                        Sys_Printf("SV_ReadClientMessage: unknown command char\n");
                        return false;

                    case clc_nop:
                        break;

                    case clc_stringcmd:
                        s = MSG_ReadString();
                        if (host_client.privileged)
                        {
                            ret = 2;
                        }
                        else
                        {
                            ret = 0;
                        }

                        if (Q_strncasecmp(s, "status", 6) == 0)
                        {
                            ret = 1;
                        }
                        else if (Q_strncasecmp(s, "god", 3) == 0)
                        {
                            ret = 1;
                        }
                        else if (Q_strncasecmp(s, "notarget", 8) == 0)
                        {
                            ret = 1;
                        }
                        else if (Q_strncasecmp(s, "fly", 3) == 0)
                        {
                            ret = 1;
                        }
                        else if (Q_strncasecmp(s, "name", 4) == 0)
                        {
                            ret = 1;
                        }
                        else if (Q_strncasecmp(s, "noclip", 6) == 0)
                        {
                            ret = 1;
                        }
                        else if (Q_strncasecmp(s, "say", 3) == 0)
                        {
                            ret = 1;
                        }
                        else if (Q_strncasecmp(s, "say_team", 8) == 0)
                        {
                            ret = 1;
                        }
                        else if (Q_strncasecmp(s, "tell", 4) == 0)
                        {
                            ret = 1;
                        }
                        else if (Q_strncasecmp(s, "color", 5) == 0)
                        {
                            ret = 1;
                        }
                        else if (Q_strncasecmp(s, "kill", 4) == 0)
                        {
                            ret = 1;
                        }
                        else if (Q_strncasecmp(s, "pause", 5) == 0)
                        {
                            ret = 1;
                        }
                        else if (Q_strncasecmp(s, "spawn", 5) == 0)
                        {
                            ret = 1;
                        }
                        else if (Q_strncasecmp(s, "begin", 5) == 0)
                        {
                            ret = 1;
                        }
                        else if (Q_strncasecmp(s, "prespawn", 8) == 0)
                        {
                            ret = 1;
                        }
                        else if (Q_strncasecmp(s, "kick", 4) == 0)
                        {
                            ret = 1;
                        }
                        else if (Q_strncasecmp(s, "ping", 4) == 0)
                        {
                            ret = 1;
                        }
                        else if (Q_strncasecmp(s, "give", 4) == 0)
                        {
                            ret = 1;
                        }
                        else if (Q_strncasecmp(s, "ban", 3) == 0)
                        {
                            ret = 1;
                        }

                        if (ret == 2)
                        {
                            Cbuf_InsertText(s);
                        }
                        else if (ret == 1)
                        {
                            Cmd_ExecuteString(s, cmd_source_t.src_client);
                        }
                        else
                        {
                            Con_DPrintf("%s tried to %s\n", host_client.name, s);
                        }

                        break;

                    case clc_disconnect:
                        return false;

                    case clc_move:
                        SV_ReadClientMove(ref host_client.cmd);
                        break;
                }
            }
        }
        while (ret == 1);
    }

    // sv_user.c: SV_RunClients
    public void SV_RunClients()
    {
        int i;

        for (i = 0, host_client = svs.Client(0); i < svs.maxclients; i++, host_client = svs.Client(i))
        {
            if (!host_client.active)
            {
                continue;
            }

            sv_player = host_client.edict;

            if (!SV_ReadClientMessage())
            {
                SV_DropClient(false); // client misbehaved...
                continue;
            }

            if (!host_client.spawned)
            {
                // clear client movement until a new packet is received
                host_client.cmd = default;
                continue;
            }

            // always pause in single player if in console or menus
            if (!sv.paused && (svs.maxclients > 1 || key_dest == keydest_t.key_game))
            {
                SV_ClientThink();
            }
        }
    }
}
