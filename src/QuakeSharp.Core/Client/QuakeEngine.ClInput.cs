using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using static QuakeSharp.Core.Common.MathLib;
using static QuakeSharp.Core.Common.QString;
using static QuakeSharp.Core.Protocol;

namespace QuakeSharp.Core;

/// <summary>client.h: kbutton_t</summary>
public sealed class KButton
{
    /// <summary>key nums holding it down</summary>
    public readonly int[] down = new int[2];

    /// <summary>low bit is down state</summary>
    public int state;
}

// cl_input.c: builds an intended movement command to send to the server
//
// KEY BUTTONS
//
// Continuous button event tracking is complicated by the fact that two different input sources (say, mouse button 1
// and the control key) can both press the same button, but the button should only be released when both of the
// pressing key have been released.
//
// When a key event issues a button command (+forward, +attack, etc), it appends its key number as a parameter to the
// command so it can be matched up with the release.
//
// state bit 0 is the current state of the key
// state bit 1 is edge triggered on the up to down transition
// state bit 2 is edge triggered on the down to up transition
public sealed partial class QuakeEngine
{
    public readonly KButton in_mlook = new(), in_klook = new();
    public readonly KButton in_left = new(), in_right = new(), in_forward = new(), in_back = new();
    public readonly KButton in_lookup = new(), in_lookdown = new(), in_moveleft = new(), in_moveright = new();
    public readonly KButton in_strafe = new(), in_speed = new(), in_use = new(), in_jump = new(), in_attack = new();
    public readonly KButton in_up = new(), in_down = new();

    public int in_impulse;

    public readonly Cvar cl_upspeed = new("cl_upspeed", "200");
    public readonly Cvar cl_forwardspeed = new("cl_forwardspeed", "200", true);
    public readonly Cvar cl_backspeed = new("cl_backspeed", "200", true);
    public readonly Cvar cl_sidespeed = new("cl_sidespeed", "350");

    public readonly Cvar cl_movespeedkey = new("cl_movespeedkey", "2.0");

    public readonly Cvar cl_yawspeed = new("cl_yawspeed", "140");
    public readonly Cvar cl_pitchspeed = new("cl_pitchspeed", "150");

    public readonly Cvar cl_anglespeedkey = new("cl_anglespeedkey", "1.5");

    // cl_input.c: KeyDown
    public void KeyDown(KButton b)
    {
        int k;
        string c;

        c = Cmd_Argv(1);
        if (Ch(c, 0) != 0)
        {
            k = atoi(c);
        }
        else
        {
            k = -1; // typed manually at the console for continuous down
        }

        if (k == b.down[0] || k == b.down[1])
        {
            return; // repeating key
        }

        if (b.down[0] == 0)
        {
            b.down[0] = k;
        }
        else if (b.down[1] == 0)
        {
            b.down[1] = k;
        }
        else
        {
            Con_Printf("Three keys down for a button!\n");
            return;
        }

        if ((b.state & 1) != 0)
        {
            return; // still down
        }

        b.state |= 1 + 2; // down + impulse down
    }

    // cl_input.c: KeyUp
    public void KeyUp(KButton b)
    {
        int k;
        string c;

        c = Cmd_Argv(1);
        if (Ch(c, 0) != 0)
        {
            k = atoi(c);
        }
        else
        {
            // typed manually at the console, assume for unsticking, so clear all
            b.down[0] = b.down[1] = 0;
            b.state = 4; // impulse up
            return;
        }

        if (b.down[0] == k)
        {
            b.down[0] = 0;
        }
        else if (b.down[1] == k)
        {
            b.down[1] = 0;
        }
        else
        {
            return; // key up without coresponding down (menu pass through)
        }

        if (b.down[0] != 0 || b.down[1] != 0)
        {
            return; // some other key is still holding it down
        }

        if ((b.state & 1) == 0)
        {
            return; // still up (this should not happen)
        }

        b.state &= ~1; // now up
        b.state |= 4; // impulse up
    }

    public void IN_KLookDown() => KeyDown(in_klook);

    public void IN_KLookUp() => KeyUp(in_klook);

    public void IN_MLookDown() => KeyDown(in_mlook);

    public void IN_MLookUp()
    {
        KeyUp(in_mlook);
        if ((in_mlook.state & 1) == 0 && lookspring.value != 0)
        {
            V_StartPitchDrift();
        }
    }

    public void IN_UpDown() => KeyDown(in_up);

    public void IN_UpUp() => KeyUp(in_up);

    public void IN_DownDown() => KeyDown(in_down);

    public void IN_DownUp() => KeyUp(in_down);

    public void IN_LeftDown() => KeyDown(in_left);

    public void IN_LeftUp() => KeyUp(in_left);

    public void IN_RightDown() => KeyDown(in_right);

    public void IN_RightUp() => KeyUp(in_right);

    public void IN_ForwardDown() => KeyDown(in_forward);

    public void IN_ForwardUp() => KeyUp(in_forward);

    public void IN_BackDown() => KeyDown(in_back);

    public void IN_BackUp() => KeyUp(in_back);

    public void IN_LookupDown() => KeyDown(in_lookup);

    public void IN_LookupUp() => KeyUp(in_lookup);

    public void IN_LookdownDown() => KeyDown(in_lookdown);

    public void IN_LookdownUp() => KeyUp(in_lookdown);

    public void IN_MoveleftDown() => KeyDown(in_moveleft);

    public void IN_MoveleftUp() => KeyUp(in_moveleft);

    public void IN_MoverightDown() => KeyDown(in_moveright);

    public void IN_MoverightUp() => KeyUp(in_moveright);

    public void IN_SpeedDown() => KeyDown(in_speed);

    public void IN_SpeedUp() => KeyUp(in_speed);

    public void IN_StrafeDown() => KeyDown(in_strafe);

    public void IN_StrafeUp() => KeyUp(in_strafe);

    public void IN_AttackDown() => KeyDown(in_attack);

    public void IN_AttackUp() => KeyUp(in_attack);

    public void IN_UseDown() => KeyDown(in_use);

    public void IN_UseUp() => KeyUp(in_use);

    public void IN_JumpDown() => KeyDown(in_jump);

    public void IN_JumpUp() => KeyUp(in_jump);

    public void IN_Impulse() => in_impulse = Q_atoi(Cmd_Argv(1));

    // cl_input.c: CL_KeyState
    // Returns 0.25 if a key was pressed and released during the frame,
    // 0.5 if it was pressed and held
    // 0 if held then released, and
    // 1.0 if held for the entire time
    public static float CL_KeyState(KButton key)
    {
        float val;
        bool impulsedown, impulseup, down;

        impulsedown = (key.state & 2) != 0;
        impulseup = (key.state & 4) != 0;
        down = (key.state & 1) != 0;
        val = 0;

        if (impulsedown && !impulseup)
        {
            if (down)
            {
                val = 0.5f; // pressed and held this frame
            }
            else
            {
                val = 0; // I_Error ();
            }
        }

        if (impulseup && !impulsedown)
        {
            if (down)
            {
                val = 0; // I_Error ();
            }
            else
            {
                val = 0; // released this frame
            }
        }

        if (!impulsedown && !impulseup)
        {
            if (down)
            {
                val = 1.0f; // held the entire frame
            }
            else
            {
                val = 0; // up the entire frame
            }
        }

        if (impulsedown && impulseup)
        {
            if (down)
            {
                val = 0.75f; // released and re-pressed this frame
            }
            else
            {
                val = 0.25f; // pressed and released this frame
            }
        }

        key.state &= 1; // clear impulses

        return val;
    }

    // ==========================================================================

    // cl_input.c: CL_AdjustAngles
    // Moves the local angle positions
    public void CL_AdjustAngles()
    {
        float speed;
        float up, down;

        if ((in_speed.state & 1) != 0)
        {
            speed = (float)(host_frametime * cl_anglespeedkey.value);
        }
        else
        {
            speed = (float)host_frametime;
        }

        if ((in_strafe.state & 1) == 0)
        {
            cl.viewangles[YAW] -= speed * cl_yawspeed.value * CL_KeyState(in_right);
            cl.viewangles[YAW] += speed * cl_yawspeed.value * CL_KeyState(in_left);
            cl.viewangles[YAW] = anglemod(cl.viewangles[YAW]);
        }

        if ((in_klook.state & 1) != 0)
        {
            V_StopPitchDrift();
            cl.viewangles[PITCH] -= speed * cl_pitchspeed.value * CL_KeyState(in_forward);
            cl.viewangles[PITCH] += speed * cl_pitchspeed.value * CL_KeyState(in_back);
        }

        up = CL_KeyState(in_lookup);
        down = CL_KeyState(in_lookdown);

        cl.viewangles[PITCH] -= speed * cl_pitchspeed.value * up;
        cl.viewangles[PITCH] += speed * cl_pitchspeed.value * down;

        if (up != 0 || down != 0)
        {
            V_StopPitchDrift();
        }

        if (cl.viewangles[PITCH] > 80)
        {
            cl.viewangles[PITCH] = 80;
        }

        if (cl.viewangles[PITCH] < -70)
        {
            cl.viewangles[PITCH] = -70;
        }

        if (cl.viewangles[ROLL] > 50)
        {
            cl.viewangles[ROLL] = 50;
        }

        if (cl.viewangles[ROLL] < -50)
        {
            cl.viewangles[ROLL] = -50;
        }
    }

    // cl_input.c: CL_BaseMove
    // Send the intended movement message to the server
    public void CL_BaseMove(ref UserCmd cmd)
    {
        if (cls.signon != SIGNONS)
        {
            return;
        }

        CL_AdjustAngles();

        cmd = default;

        if ((in_strafe.state & 1) != 0)
        {
            cmd.sidemove += cl_sidespeed.value * CL_KeyState(in_right);
            cmd.sidemove -= cl_sidespeed.value * CL_KeyState(in_left);
        }

        cmd.sidemove += cl_sidespeed.value * CL_KeyState(in_moveright);
        cmd.sidemove -= cl_sidespeed.value * CL_KeyState(in_moveleft);

        cmd.upmove += cl_upspeed.value * CL_KeyState(in_up);
        cmd.upmove -= cl_upspeed.value * CL_KeyState(in_down);

        if ((in_klook.state & 1) == 0)
        {
            cmd.forwardmove += cl_forwardspeed.value * CL_KeyState(in_forward);
            cmd.forwardmove -= cl_backspeed.value * CL_KeyState(in_back);
        }

        // adjust for speed key
        if ((in_speed.state & 1) != 0)
        {
            cmd.forwardmove *= cl_movespeedkey.value;
            cmd.sidemove *= cl_movespeedkey.value;
            cmd.upmove *= cl_movespeedkey.value;
        }
    }

    // cl_input.c: CL_SendMove
    public void CL_SendMove(in UserCmd cmd)
    {
        int i;
        int bits;
        var buf = new SizeBuf(128) { maxsize = 128 };

        cl.cmd = cmd;

        // send the movement message
        MSG_WriteByte(buf, clc_move);

        MSG_WriteFloat(buf, (float)cl.mtime[0]); // so server can get ping times

        for (i = 0; i < 3; i++)
        {
            MSG_WriteAngle(buf, cl.viewangles[i]);
        }

        MSG_WriteShort(buf, Numerics.X86.FtoI(cmd.forwardmove));
        MSG_WriteShort(buf, Numerics.X86.FtoI(cmd.sidemove));
        MSG_WriteShort(buf, Numerics.X86.FtoI(cmd.upmove));

        // send button bits
        bits = 0;

        if ((in_attack.state & 3) != 0)
        {
            bits |= 1;
        }

        in_attack.state &= ~2;

        if ((in_jump.state & 3) != 0)
        {
            bits |= 2;
        }

        in_jump.state &= ~2;

        MSG_WriteByte(buf, bits);

        MSG_WriteByte(buf, in_impulse);
        in_impulse = 0;

        // deliver the message
        if (cls.demoplayback)
        {
            return;
        }

        // allways dump the first two message, because it may contain leftover inputs from the last level
        if (++cl.movemessages <= 2)
        {
            return;
        }

        if (NET_SendUnreliableMessage(cls.netcon, buf) == -1)
        {
            Con_Printf("CL_SendMove: lost server connection\n");
            CL_Disconnect();
        }
    }

    // cl_input.c: CL_InitInput
    public void CL_InitInput()
    {
        Cmd_AddCommand("+moveup", IN_UpDown);
        Cmd_AddCommand("-moveup", IN_UpUp);
        Cmd_AddCommand("+movedown", IN_DownDown);
        Cmd_AddCommand("-movedown", IN_DownUp);
        Cmd_AddCommand("+left", IN_LeftDown);
        Cmd_AddCommand("-left", IN_LeftUp);
        Cmd_AddCommand("+right", IN_RightDown);
        Cmd_AddCommand("-right", IN_RightUp);
        Cmd_AddCommand("+forward", IN_ForwardDown);
        Cmd_AddCommand("-forward", IN_ForwardUp);
        Cmd_AddCommand("+back", IN_BackDown);
        Cmd_AddCommand("-back", IN_BackUp);
        Cmd_AddCommand("+lookup", IN_LookupDown);
        Cmd_AddCommand("-lookup", IN_LookupUp);
        Cmd_AddCommand("+lookdown", IN_LookdownDown);
        Cmd_AddCommand("-lookdown", IN_LookdownUp);
        Cmd_AddCommand("+strafe", IN_StrafeDown);
        Cmd_AddCommand("-strafe", IN_StrafeUp);
        Cmd_AddCommand("+moveleft", IN_MoveleftDown);
        Cmd_AddCommand("-moveleft", IN_MoveleftUp);
        Cmd_AddCommand("+moveright", IN_MoverightDown);
        Cmd_AddCommand("-moveright", IN_MoverightUp);
        Cmd_AddCommand("+speed", IN_SpeedDown);
        Cmd_AddCommand("-speed", IN_SpeedUp);
        Cmd_AddCommand("+attack", IN_AttackDown);
        Cmd_AddCommand("-attack", IN_AttackUp);
        Cmd_AddCommand("+use", IN_UseDown);
        Cmd_AddCommand("-use", IN_UseUp);
        Cmd_AddCommand("+jump", IN_JumpDown);
        Cmd_AddCommand("-jump", IN_JumpUp);
        Cmd_AddCommand("impulse", IN_Impulse);
        Cmd_AddCommand("+klook", IN_KLookDown);
        Cmd_AddCommand("-klook", IN_KLookUp);
        Cmd_AddCommand("+mlook", IN_MLookDown);
        Cmd_AddCommand("-mlook", IN_MLookUp);
    }
}
