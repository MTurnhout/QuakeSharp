using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Numerics;
using static QuakeSharp.Core.Client.ClientState;
using static QuakeSharp.Core.Common.MathLib;
using static QuakeSharp.Core.Common.QString;
using static QuakeSharp.Core.Model.BspFile;

namespace QuakeSharp.Core;

// view.c: player eye positioning
//
// The view is allowed to move slightly from it's true position for bobbing, but if it exceeds 8 pixels linear
// distance (spherical, not box), the list of entities sent from the server may not include everything in the pvs,
// especially when crossing a water boudnary.
public sealed partial class QuakeEngine
{
    public readonly Cvar lcd_x = new("lcd_x", "0");
    public readonly Cvar lcd_yaw = new("lcd_yaw", "0");

    public readonly Cvar scr_ofsx = new("scr_ofsx", "0", false);
    public readonly Cvar scr_ofsy = new("scr_ofsy", "0", false);
    public readonly Cvar scr_ofsz = new("scr_ofsz", "0", false);

    public readonly Cvar cl_rollspeed = new("cl_rollspeed", "200");
    public readonly Cvar cl_rollangle = new("cl_rollangle", "2.0");

    public readonly Cvar cl_bob = new("cl_bob", "0.02", false);
    public readonly Cvar cl_bobcycle = new("cl_bobcycle", "0.6", false);
    public readonly Cvar cl_bobup = new("cl_bobup", "0.5", false);

    public readonly Cvar v_kicktime = new("v_kicktime", "0.5", false);
    public readonly Cvar v_kickroll = new("v_kickroll", "0.6", false);
    public readonly Cvar v_kickpitch = new("v_kickpitch", "0.6", false);

    public readonly Cvar v_iyaw_cycle = new("v_iyaw_cycle", "2", false);
    public readonly Cvar v_iroll_cycle = new("v_iroll_cycle", "0.5", false);
    public readonly Cvar v_ipitch_cycle = new("v_ipitch_cycle", "1", false);
    public readonly Cvar v_iyaw_level = new("v_iyaw_level", "0.3", false);
    public readonly Cvar v_iroll_level = new("v_iroll_level", "0.1", false);
    public readonly Cvar v_ipitch_level = new("v_ipitch_level", "0.3", false);

    public readonly Cvar v_idlescale = new("v_idlescale", "0", false);

    public readonly Cvar crosshair = new("crosshair", "0", true);
    public readonly Cvar cl_crossx = new("cl_crossx", "0", false);
    public readonly Cvar cl_crossy = new("cl_crossy", "0", false);

    public readonly Cvar gl_cshiftpercent = new("gl_cshiftpercent", "100", false);

    public readonly Cvar v_centermove = new("v_centermove", "0.15", false);
    public readonly Cvar v_centerspeed = new("v_centerspeed", "500");

    public readonly Cvar v_gamma = new("gamma", "1", true);

    public float v_dmg_time, v_dmg_roll, v_dmg_pitch;

    /// <summary>view.c's (and sv_user.c's: C's linker merges the two) forward, right, up</summary>
    public Vec3 forward, right, up;

    public Cshift cshift_empty = new(130, 80, 50, 0);
    public Cshift cshift_water = new(130, 80, 50, 128);
    public Cshift cshift_slime = new(0, 25, 5, 150);
    public Cshift cshift_lava = new(255, 80, 0, 150);

    /// <summary>palette is sent through this</summary>
    public readonly byte[] gammatable = new byte[256];

    // view.c: V_CheckGamma's static oldgammavalue; CalcGunAngle's static oldyaw, oldpitch; V_CalcRefdef's static oldz
    private float v_oldgammavalue;
    private float v_oldyaw;
    private float v_oldpitch;
    private float v_oldz;

    // view.c: V_CalcRoll
    // Used by view and sv_user
    public float V_CalcRoll(in Vec3 angles, in Vec3 velocity)
    {
        float sign;
        float side;
        float value;

        AngleVectors(angles, ref forward, ref right, ref up);
        side = DotProduct(velocity, right);
        sign = side < 0 ? -1 : 1;
        side = Math.Abs(side);

        value = cl_rollangle.value;

        if (side < cl_rollspeed.value)
        {
            side = side * value / cl_rollspeed.value;
        }
        else
        {
            side = value;
        }

        return side * sign;
    }

    // view.c: V_CalcBob
    public float V_CalcBob()
    {
        float bob;
        float cycle;

        cycle = (float)(cl.time - ((int)(cl.time / cl_bobcycle.value) * cl_bobcycle.value));
        cycle /= cl_bobcycle.value;
        if (cycle < cl_bobup.value)
        {
            cycle = (float)(M_PI * cycle / cl_bobup.value);
        }
        else
        {
            cycle = (float)(M_PI + (M_PI * (cycle - cl_bobup.value) / (1.0 - cl_bobup.value)));
        }

        // bob is proportional to velocity in the xy plane
        // (don't count Z, or jumping messes it up)
        bob = (float)(Math.Sqrt((cl.velocity[0] * cl.velocity[0]) + (cl.velocity[1] * cl.velocity[1])) * cl_bob.value);
        bob = (float)((bob * 0.3) + (bob * 0.7 * Libm.sin(cycle)));
        if (bob > 4)
        {
            bob = 4;
        }
        else if (bob < -7)
        {
            bob = -7;
        }

        return bob;
    }

    // =============================================================================

    // view.c: V_StartPitchDrift
    public void V_StartPitchDrift()
    {
        if (cl.laststop == cl.time)
        {
            return; // something else is keeping it from drifting
        }

        if (cl.nodrift || cl.pitchvel == 0)
        {
            cl.pitchvel = v_centerspeed.value;
            cl.nodrift = false;
            cl.driftmove = 0;
        }
    }

    // view.c: V_StopPitchDrift
    public void V_StopPitchDrift()
    {
        cl.laststop = cl.time;
        cl.nodrift = true;
        cl.pitchvel = 0;
    }

    // view.c: V_DriftPitch
    // Moves the client pitch angle towards cl.idealpitch sent by the server.
    //
    // If the user is adjusting pitch manually, either with lookup/lookdown, mlook and mouse, or klook and keyboard,
    // pitch drifting is constantly stopped.
    //
    // Drifting is enabled when the center view key is hit, mlook is released and lookspring is non 0, or when
    public void V_DriftPitch()
    {
        float delta, move;

        if (noclip_anglehack || !cl.onground || cls.demoplayback)
        {
            cl.driftmove = 0;
            cl.pitchvel = 0;
            return;
        }

        // don't count small mouse motion
        if (cl.nodrift)
        {
            if (Math.Abs(cl.cmd.forwardmove) < cl_forwardspeed.value)
            {
                cl.driftmove = 0;
            }
            else
            {
                cl.driftmove = (float)(cl.driftmove + host_frametime);
            }

            if (cl.driftmove > v_centermove.value)
            {
                V_StartPitchDrift();
            }

            return;
        }

        delta = cl.idealpitch - cl.viewangles[PITCH];

        if (delta == 0)
        {
            cl.pitchvel = 0;
            return;
        }

        move = (float)(host_frametime * cl.pitchvel);
        cl.pitchvel = (float)(cl.pitchvel + (host_frametime * v_centerspeed.value));

        if (delta > 0)
        {
            if (move > delta)
            {
                cl.pitchvel = 0;
                move = delta;
            }

            cl.viewangles[PITCH] += move;
        }
        else if (delta < 0)
        {
            if (move > -delta)
            {
                cl.pitchvel = 0;
                move = -delta;
            }

            cl.viewangles[PITCH] -= move;
        }
    }

    // ==============================================================================
    // PALETTE FLASHES
    // ==============================================================================

    // view.c: BuildGammaTable
    public void BuildGammaTable(float g)
    {
        int i, inf;

        if (g == 1.0)
        {
            for (i = 0; i < 256; i++)
            {
                gammatable[i] = (byte)i;
            }

            return;
        }

        for (i = 0; i < 256; i++)
        {
            inf = X86.FtoI((255 * Libm.pow((i + 0.5) / 255.5, g)) + 0.5);
            if (inf < 0)
            {
                inf = 0;
            }

            if (inf > 255)
            {
                inf = 255;
            }

            gammatable[i] = (byte)inf;
        }
    }

    // view.c: V_CheckGamma
    public bool V_CheckGamma()
    {
        if (v_gamma.value == v_oldgammavalue)
        {
            return false;
        }

        v_oldgammavalue = v_gamma.value;

        BuildGammaTable(v_gamma.value);
        vid.recalc_refdef = 1; // force a surface cache flush

        return true;
    }

    // view.c: V_ParseDamage
    public void V_ParseDamage()
    {
        int armor, blood;
        Vec3 from = default;
        int i;
        Vec3 forward = default, right = default, up = default;
        Entity ent;
        float side;
        float count;

        armor = MSG_ReadByte();
        blood = MSG_ReadByte();
        for (i = 0; i < 3; i++)
        {
            from[i] = MSG_ReadCoord();
        }

        count = (float)((blood * 0.5) + (armor * 0.5));
        if (count < 10)
        {
            count = 10;
        }

        cl.faceanimtime = (float)(cl.time + 0.2); // but sbar face into pain frame

        cl.cshifts[CSHIFT_DAMAGE].percent = (int)(cl.cshifts[CSHIFT_DAMAGE].percent + (3 * count));
        if (cl.cshifts[CSHIFT_DAMAGE].percent < 0)
        {
            cl.cshifts[CSHIFT_DAMAGE].percent = 0;
        }

        if (cl.cshifts[CSHIFT_DAMAGE].percent > 150)
        {
            cl.cshifts[CSHIFT_DAMAGE].percent = 150;
        }

        if (armor > blood)
        {
            cl.cshifts[CSHIFT_DAMAGE].destcolor[0] = 200;
            cl.cshifts[CSHIFT_DAMAGE].destcolor[1] = 100;
            cl.cshifts[CSHIFT_DAMAGE].destcolor[2] = 100;
        }
        else if (armor != 0)
        {
            cl.cshifts[CSHIFT_DAMAGE].destcolor[0] = 220;
            cl.cshifts[CSHIFT_DAMAGE].destcolor[1] = 50;
            cl.cshifts[CSHIFT_DAMAGE].destcolor[2] = 50;
        }
        else
        {
            cl.cshifts[CSHIFT_DAMAGE].destcolor[0] = 255;
            cl.cshifts[CSHIFT_DAMAGE].destcolor[1] = 0;
            cl.cshifts[CSHIFT_DAMAGE].destcolor[2] = 0;
        }

        // calculate view angle kicks
        ent = cl_entities[cl.viewentity];

        VectorSubtract(from, ent.origin, ref from);
        VectorNormalize(ref from);

        AngleVectors(ent.angles, ref forward, ref right, ref up);

        side = DotProduct(from, right);
        v_dmg_roll = count * side * v_kickroll.value;

        side = DotProduct(from, forward);
        v_dmg_pitch = count * side * v_kickpitch.value;

        v_dmg_time = v_kicktime.value;
    }

    // view.c: V_cshift_f
    public void V_cshift_f()
    {
        cshift_empty.destcolor[0] = atoi(Cmd_Argv(1));
        cshift_empty.destcolor[1] = atoi(Cmd_Argv(2));
        cshift_empty.destcolor[2] = atoi(Cmd_Argv(3));
        cshift_empty.percent = atoi(Cmd_Argv(4));
    }

    // view.c: V_BonusFlash_f
    // When you run over an item, the server sends this command
    public void V_BonusFlash_f()
    {
        cl.cshifts[CSHIFT_BONUS].destcolor[0] = 215;
        cl.cshifts[CSHIFT_BONUS].destcolor[1] = 186;
        cl.cshifts[CSHIFT_BONUS].destcolor[2] = 69;
        cl.cshifts[CSHIFT_BONUS].percent = 50;
    }

    // view.c: V_SetContentsColor
    // Underwater, lava, etc each has a color shift
    public void V_SetContentsColor(int contents)
    {
        switch (contents)
        {
            case CONTENTS_EMPTY:
            case CONTENTS_SOLID:
                cl.cshifts[CSHIFT_CONTENTS] = cshift_empty;
                break;
            case CONTENTS_LAVA:
                cl.cshifts[CSHIFT_CONTENTS] = cshift_lava;
                break;
            case CONTENTS_SLIME:
                cl.cshifts[CSHIFT_CONTENTS] = cshift_slime;
                break;
            default:
                cl.cshifts[CSHIFT_CONTENTS] = cshift_water;
                break;
        }
    }

    // view.c: V_CalcPowerupCshift
    public void V_CalcPowerupCshift()
    {
        if ((cl.items & QuakeDef.IT_QUAD) != 0)
        {
            cl.cshifts[CSHIFT_POWERUP].destcolor[0] = 0;
            cl.cshifts[CSHIFT_POWERUP].destcolor[1] = 0;
            cl.cshifts[CSHIFT_POWERUP].destcolor[2] = 255;
            cl.cshifts[CSHIFT_POWERUP].percent = 30;
        }
        else if ((cl.items & QuakeDef.IT_SUIT) != 0)
        {
            cl.cshifts[CSHIFT_POWERUP].destcolor[0] = 0;
            cl.cshifts[CSHIFT_POWERUP].destcolor[1] = 255;
            cl.cshifts[CSHIFT_POWERUP].destcolor[2] = 0;
            cl.cshifts[CSHIFT_POWERUP].percent = 20;
        }
        else if ((cl.items & QuakeDef.IT_INVISIBILITY) != 0)
        {
            cl.cshifts[CSHIFT_POWERUP].destcolor[0] = 100;
            cl.cshifts[CSHIFT_POWERUP].destcolor[1] = 100;
            cl.cshifts[CSHIFT_POWERUP].destcolor[2] = 100;
            cl.cshifts[CSHIFT_POWERUP].percent = 100;
        }
        else if ((cl.items & QuakeDef.IT_INVULNERABILITY) != 0)
        {
            cl.cshifts[CSHIFT_POWERUP].destcolor[0] = 255;
            cl.cshifts[CSHIFT_POWERUP].destcolor[1] = 255;
            cl.cshifts[CSHIFT_POWERUP].destcolor[2] = 0;
            cl.cshifts[CSHIFT_POWERUP].percent = 30;
        }
        else
        {
            cl.cshifts[CSHIFT_POWERUP].percent = 0;
        }
    }

    // view.c: V_UpdatePalette (the software renderer's, not GLQuake's)
    public void V_UpdatePalette()
    {
        int i, j;
        bool @new;
        int basepal, newpal;
        byte[] pal = new byte[768];
        int r, g, b;
        bool force;

        V_CalcPowerupCshift();

        @new = false;

        for (i = 0; i < NUM_CSHIFTS; i++)
        {
            if (cl.cshifts[i].percent != cl.prev_cshifts[i].percent)
            {
                @new = true;
                cl.prev_cshifts[i].percent = cl.cshifts[i].percent;
            }

            for (j = 0; j < 3; j++)
            {
                if (cl.cshifts[i].destcolor[j] != cl.prev_cshifts[i].destcolor[j])
                {
                    @new = true;
                    cl.prev_cshifts[i].destcolor[j] = cl.cshifts[i].destcolor[j];
                }
            }
        }

        // drop the damage value
        cl.cshifts[CSHIFT_DAMAGE].percent = (int)(cl.cshifts[CSHIFT_DAMAGE].percent - (host_frametime * 150));
        if (cl.cshifts[CSHIFT_DAMAGE].percent <= 0)
        {
            cl.cshifts[CSHIFT_DAMAGE].percent = 0;
        }

        // drop the bonus value
        cl.cshifts[CSHIFT_BONUS].percent = (int)(cl.cshifts[CSHIFT_BONUS].percent - (host_frametime * 100));
        if (cl.cshifts[CSHIFT_BONUS].percent <= 0)
        {
            cl.cshifts[CSHIFT_BONUS].percent = 0;
        }

        force = V_CheckGamma();
        if (!@new && !force)
        {
            return;
        }

        basepal = 0;
        newpal = 0;

        for (i = 0; i < 256; i++)
        {
            r = host_basepal[basepal + 0];
            g = host_basepal[basepal + 1];
            b = host_basepal[basepal + 2];
            basepal += 3;

            for (j = 0; j < NUM_CSHIFTS; j++)
            {
                r += (cl.cshifts[j].percent * (cl.cshifts[j].destcolor[0] - r)) >> 8;
                g += (cl.cshifts[j].percent * (cl.cshifts[j].destcolor[1] - g)) >> 8;
                b += (cl.cshifts[j].percent * (cl.cshifts[j].destcolor[2] - b)) >> 8;
            }

            pal[newpal + 0] = gammatable[r];
            pal[newpal + 1] = gammatable[g];
            pal[newpal + 2] = gammatable[b];
            newpal += 3;
        }

        VID_ShiftPalette(pal);
    }

    // ==============================================================================
    // VIEW RENDERING
    // ==============================================================================

    // view.c: angledelta
    public static float angledelta(float a)
    {
        a = anglemod(a);
        if (a > 180)
        {
            a -= 360;
        }

        return a;
    }

    // view.c: CalcGunAngle
    public void CalcGunAngle()
    {
        float yaw, pitch, move;

        yaw = r_refdef.viewangles[YAW];
        pitch = -r_refdef.viewangles[PITCH];

        yaw = (float)(angledelta(yaw - r_refdef.viewangles[YAW]) * 0.4);
        if (yaw > 10)
        {
            yaw = 10;
        }

        if (yaw < -10)
        {
            yaw = -10;
        }

        pitch = (float)(angledelta(-pitch - r_refdef.viewangles[PITCH]) * 0.4);
        if (pitch > 10)
        {
            pitch = 10;
        }

        if (pitch < -10)
        {
            pitch = -10;
        }

        move = (float)(host_frametime * 20);
        if (yaw > v_oldyaw)
        {
            if (v_oldyaw + move < yaw)
            {
                yaw = v_oldyaw + move;
            }
        }
        else
        {
            if (v_oldyaw - move > yaw)
            {
                yaw = v_oldyaw - move;
            }
        }

        if (pitch > v_oldpitch)
        {
            if (v_oldpitch + move < pitch)
            {
                pitch = v_oldpitch + move;
            }
        }
        else
        {
            if (v_oldpitch - move > pitch)
            {
                pitch = v_oldpitch - move;
            }
        }

        v_oldyaw = yaw;
        v_oldpitch = pitch;

        cl.viewent.angles[YAW] = r_refdef.viewangles[YAW] + yaw;
        cl.viewent.angles[PITCH] = -(r_refdef.viewangles[PITCH] + pitch);

        cl.viewent.angles[ROLL] = (float)(cl.viewent.angles[ROLL] -
            (v_idlescale.value * Libm.sin(cl.time * v_iroll_cycle.value) * v_iroll_level.value));
        cl.viewent.angles[PITCH] = (float)(cl.viewent.angles[PITCH] -
            (v_idlescale.value * Libm.sin(cl.time * v_ipitch_cycle.value) * v_ipitch_level.value));
        cl.viewent.angles[YAW] = (float)(cl.viewent.angles[YAW] -
            (v_idlescale.value * Libm.sin(cl.time * v_iyaw_cycle.value) * v_iyaw_level.value));
    }

    // view.c: V_BoundOffsets
    public void V_BoundOffsets()
    {
        Entity ent;

        ent = cl_entities[cl.viewentity];

        // absolutely bound refresh reletive to entity clipping hull so the view can never be inside a solid wall
        if (r_refdef.vieworg[0] < ent.origin[0] - 14)
        {
            r_refdef.vieworg[0] = ent.origin[0] - 14;
        }
        else if (r_refdef.vieworg[0] > ent.origin[0] + 14)
        {
            r_refdef.vieworg[0] = ent.origin[0] + 14;
        }

        if (r_refdef.vieworg[1] < ent.origin[1] - 14)
        {
            r_refdef.vieworg[1] = ent.origin[1] - 14;
        }
        else if (r_refdef.vieworg[1] > ent.origin[1] + 14)
        {
            r_refdef.vieworg[1] = ent.origin[1] + 14;
        }

        if (r_refdef.vieworg[2] < ent.origin[2] - 22)
        {
            r_refdef.vieworg[2] = ent.origin[2] - 22;
        }
        else if (r_refdef.vieworg[2] > ent.origin[2] + 30)
        {
            r_refdef.vieworg[2] = ent.origin[2] + 30;
        }
    }

    // view.c: V_AddIdle
    // Idle swaying
    public void V_AddIdle()
    {
        r_refdef.viewangles[ROLL] = (float)(r_refdef.viewangles[ROLL] +
            (v_idlescale.value * Libm.sin(cl.time * v_iroll_cycle.value) * v_iroll_level.value));
        r_refdef.viewangles[PITCH] = (float)(r_refdef.viewangles[PITCH] +
            (v_idlescale.value * Libm.sin(cl.time * v_ipitch_cycle.value) * v_ipitch_level.value));
        r_refdef.viewangles[YAW] = (float)(r_refdef.viewangles[YAW] +
            (v_idlescale.value * Libm.sin(cl.time * v_iyaw_cycle.value) * v_iyaw_level.value));
    }

    // view.c: V_CalcViewRoll
    // Roll is induced by movement and damage
    public void V_CalcViewRoll()
    {
        float side;

        side = V_CalcRoll(cl_entities[cl.viewentity].angles, cl.velocity);
        r_refdef.viewangles[ROLL] += side;

        if (v_dmg_time > 0)
        {
            r_refdef.viewangles[ROLL] += v_dmg_time / v_kicktime.value * v_dmg_roll;
            r_refdef.viewangles[PITCH] += v_dmg_time / v_kicktime.value * v_dmg_pitch;
            v_dmg_time = (float)(v_dmg_time - host_frametime);
        }

        if (cl.stats[QuakeDef.STAT_HEALTH] <= 0)
        {
            r_refdef.viewangles[ROLL] = 80; // dead view angle
            return;
        }
    }

    // view.c: V_CalcIntermissionRefdef
    public void V_CalcIntermissionRefdef()
    {
        Entity ent, view;
        float old;

        // ent is the player model (visible when out of body)
        ent = cl_entities[cl.viewentity];

        // view is the weapon model (only visible from inside body)
        view = cl.viewent;

        VectorCopy(ent.origin, ref r_refdef.vieworg);
        VectorCopy(ent.angles, ref r_refdef.viewangles);
        view.model = null;

        // allways idle in intermission
        old = v_idlescale.value;
        v_idlescale.value = 1;
        V_AddIdle();
        v_idlescale.value = old;
    }

    // view.c: V_CalcRefdef
    public void V_CalcRefdef()
    {
        Entity ent, view;
        int i;
        Vec3 forward = default, right = default, up = default;
        Vec3 angles = default;
        float bob;

        V_DriftPitch();

        // ent is the player model (visible when out of body)
        ent = cl_entities[cl.viewentity];

        // view is the weapon model (only visible from inside body)
        view = cl.viewent;

        // transform the view offset by the model's matrix to get the offset from model origin for the view
        ent.angles[YAW] = cl.viewangles[YAW]; // the model should face the view dir
        ent.angles[PITCH] = -cl.viewangles[PITCH]; // the model should face the view dir

        bob = V_CalcBob();

        // refresh position
        VectorCopy(ent.origin, ref r_refdef.vieworg);
        r_refdef.vieworg[2] += cl.viewheight + bob;

        // never let it sit exactly on a node line, because a water plane can dissapear when viewed with the eye
        // exactly on it. the server protocol only specifies to 1/16 pixel, so add 1/32 in each axis
        r_refdef.vieworg[0] = (float)(r_refdef.vieworg[0] + (1.0 / 32));
        r_refdef.vieworg[1] = (float)(r_refdef.vieworg[1] + (1.0 / 32));
        r_refdef.vieworg[2] = (float)(r_refdef.vieworg[2] + (1.0 / 32));

        VectorCopy(cl.viewangles, ref r_refdef.viewangles);
        V_CalcViewRoll();
        V_AddIdle();

        // offsets
        angles[PITCH] = -ent.angles[PITCH]; // because entity pitches are actually backward
        angles[YAW] = ent.angles[YAW];
        angles[ROLL] = ent.angles[ROLL];

        AngleVectors(angles, ref forward, ref right, ref up);

        for (i = 0; i < 3; i++)
        {
            r_refdef.vieworg[i] += (scr_ofsx.value * forward[i])
                + (scr_ofsy.value * right[i])
                + (scr_ofsz.value * up[i]);
        }

        V_BoundOffsets();

        // set up gun position
        VectorCopy(cl.viewangles, ref view.angles);

        CalcGunAngle();

        VectorCopy(ent.origin, ref view.origin);
        view.origin[2] += cl.viewheight;

        for (i = 0; i < 3; i++)
        {
            view.origin[i] = (float)(view.origin[i] + (forward[i] * bob * 0.4));
        }

        view.origin[2] += bob;

        // fudge position around to keep amount of weapon visible roughly equal with different FOV
        if (scr_viewsize.value == 110)
        {
            view.origin[2] += 1;
        }
        else if (scr_viewsize.value == 100)
        {
            view.origin[2] += 2;
        }
        else if (scr_viewsize.value == 90)
        {
            view.origin[2] += 1;
        }
        else if (scr_viewsize.value == 80)
        {
            view.origin[2] = (float)(view.origin[2] + 0.5);
        }

        view.model = cl.model_precache[cl.stats[QuakeDef.STAT_WEAPON]];
        view.frame = cl.stats[QuakeDef.STAT_WEAPONFRAME];
        view.colormap = vid.colormap;

        // set up the refresh position
        VectorAdd(r_refdef.viewangles, cl.punchangle, ref r_refdef.viewangles);

        // smooth out stair step ups
        if (cl.onground && ent.origin[2] - v_oldz > 0)
        {
            float steptime;

            steptime = (float)(cl.time - cl.oldtime);
            if (steptime < 0)
            {
                // FIXME I_Error ("steptime < 0");
                steptime = 0;
            }

            v_oldz += steptime * 80;
            if (v_oldz > ent.origin[2])
            {
                v_oldz = ent.origin[2];
            }

            if (ent.origin[2] - v_oldz > 12)
            {
                v_oldz = ent.origin[2] - 12;
            }

            r_refdef.vieworg[2] += v_oldz - ent.origin[2];
            view.origin[2] += v_oldz - ent.origin[2];
        }
        else
        {
            v_oldz = ent.origin[2];
        }

        if (chase_active.value != 0)
        {
            Chase_Update();
        }
    }

    // view.c: V_RenderView
    // The player's clipping box goes from (-16 -16 -24) to (16 16 32) from the entity origin, so any view position
    // inside that will be valid
    public void V_RenderView()
    {
        if (con_forcedup)
        {
            return;
        }

        // don't allow cheats in multiplayer
        if (cl.maxclients > 1)
        {
            Cvar_Set("scr_ofsx", "0");
            Cvar_Set("scr_ofsy", "0");
            Cvar_Set("scr_ofsz", "0");
        }

        if (cl.intermission != 0)
        {
            // intermission / finale rendering
            V_CalcIntermissionRefdef();
        }
        else
        {
            if (cl.paused == 0)
            {
                V_CalcRefdef();
            }
        }

        R_PushDlights();

        if (lcd_x.value != 0)
        {
            // render two interleaved views
            int i;

            vid.rowbytes <<= 1;
            vid.aspect = (float)(vid.aspect * 0.5);

            r_refdef.viewangles[YAW] -= lcd_yaw.value;
            for (i = 0; i < 3; i++)
            {
                r_refdef.vieworg[i] -= right[i] * lcd_x.value;
            }

            R_RenderView();

            vid.bufferoffset += (int)(vid.rowbytes >> 1);

            R_PushDlights();

            r_refdef.viewangles[YAW] += lcd_yaw.value * 2;
            for (i = 0; i < 3; i++)
            {
                r_refdef.vieworg[i] += 2 * right[i] * lcd_x.value;
            }

            R_RenderView();

            vid.bufferoffset -= (int)(vid.rowbytes >> 1);

            r_refdef.vrect.height <<= 1;

            vid.rowbytes >>= 1;
            vid.aspect *= 2;
        }
        else
        {
            R_RenderView();
        }

        if (crosshair.value != 0)
        {
            Draw_Character((int)(scr_vrect.x + (scr_vrect.width / 2) + cl_crossx.value),
                (int)(scr_vrect.y + (scr_vrect.height / 2) + cl_crossy.value), '+');
        }
    }

    // ============================================================================

    // view.c: V_Init
    public void V_Init()
    {
        Cmd_AddCommand("v_cshift", V_cshift_f);
        Cmd_AddCommand("bf", V_BonusFlash_f);
        Cmd_AddCommand("centerview", V_StartPitchDrift);

        Cvar_RegisterVariable(lcd_x);
        Cvar_RegisterVariable(lcd_yaw);

        Cvar_RegisterVariable(v_centermove);
        Cvar_RegisterVariable(v_centerspeed);

        Cvar_RegisterVariable(v_iyaw_cycle);
        Cvar_RegisterVariable(v_iroll_cycle);
        Cvar_RegisterVariable(v_ipitch_cycle);
        Cvar_RegisterVariable(v_iyaw_level);
        Cvar_RegisterVariable(v_iroll_level);
        Cvar_RegisterVariable(v_ipitch_level);

        Cvar_RegisterVariable(v_idlescale);
        Cvar_RegisterVariable(crosshair);
        Cvar_RegisterVariable(cl_crossx);
        Cvar_RegisterVariable(cl_crossy);
        Cvar_RegisterVariable(gl_cshiftpercent);

        Cvar_RegisterVariable(scr_ofsx);
        Cvar_RegisterVariable(scr_ofsy);
        Cvar_RegisterVariable(scr_ofsz);
        Cvar_RegisterVariable(cl_rollspeed);
        Cvar_RegisterVariable(cl_rollangle);
        Cvar_RegisterVariable(cl_bob);
        Cvar_RegisterVariable(cl_bobcycle);
        Cvar_RegisterVariable(cl_bobup);

        Cvar_RegisterVariable(v_kicktime);
        Cvar_RegisterVariable(v_kickroll);
        Cvar_RegisterVariable(v_kickpitch);

        BuildGammaTable(1.0f); // no gamma yet
        Cvar_RegisterVariable(v_gamma);
    }
}
