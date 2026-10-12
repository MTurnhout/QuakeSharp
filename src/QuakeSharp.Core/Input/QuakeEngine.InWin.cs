using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Input;
using QuakeSharp.Core.Numerics;
using static QuakeSharp.Core.QuakeDef;
using static QuakeSharp.Core.UI.Keys;

namespace QuakeSharp.Core;

// in_win.c: the mouse and the joystick, over the Win32 calls VirtualInput answers (design.md decision 43). DirectInput
// (-dinput) is not ported: dinput stays false, as without the switch.
public sealed partial class QuakeEngine
{
    // in_win.c: JOY_ABSOLUTE_AXIS... and _ControlList
    private const uint JOY_ABSOLUTE_AXIS = 0x00000000; // control like a joystick
    private const uint JOY_RELATIVE_AXIS = 0x00000010; // control like a mouse, spinner, trackball
    private const int JOY_MAX_AXES = 6; // X, Y, Z, R, U, V
    private const int JOY_AXIS_X = 0;
    private const int JOY_AXIS_Y = 1;
    private const int JOY_AXIS_Z = 2;
    private const int JOY_AXIS_R = 3;
    private const int JOY_AXIS_U = 4;
    private const int JOY_AXIS_V = 5;

    private const uint AxisNada = 0;
    private const uint AxisForward = 1;
    private const uint AxisLook = 2;
    private const uint AxisSide = 3;
    private const uint AxisTurn = 4;

    /// <summary>The Windows in_win.c talks to: the cursor, the system's mouse parameters, the joysticks.</summary>
    public readonly VirtualInput input;

    // mouse variables
    public readonly Cvar m_filter = new("m_filter", "0");

    public int mouse_buttons;
    public int mouse_oldbuttonstate;
    public int current_pos_x, current_pos_y;
    public int mouse_x, mouse_y, old_mouse_x, old_mouse_y, mx_accum, my_accum;

    private bool restore_spi;
    private readonly int[] originalmouseparms = new int[3];
    private readonly int[] newmouseparms = [0, 0, 1];

    public uint uiWheelMessage;
    public bool mouseactive;
    public bool mouseinitialized;
    private bool mouseparmsvalid, mouseactivatetoggle;
    private bool mouseshowtoggle = true;

    // DirectInput's (not ported: never acquired, no buttons)
    private const bool dinput_acquired = false;
    private const uint mstate_di = 0;

    private static readonly uint[] dwAxisFlags =
    [
        Win32.JOY_RETURNX, Win32.JOY_RETURNY, Win32.JOY_RETURNZ, Win32.JOY_RETURNR, Win32.JOY_RETURNU, Win32.JOY_RETURNV,
    ];

    public readonly uint[] dwAxisMap = new uint[JOY_MAX_AXES];
    public readonly uint[] dwControlMap = new uint[JOY_MAX_AXES];

    // pdwRawValue: the axis each entry reads of ji (RawValuePointer)
    private readonly int[] pdwRawValue = new int[JOY_MAX_AXES];

    // none of these cvars are saved over a session
    // this means that advanced controller configuration needs to be executed
    // each time.  this avoids any problems with getting back to a default usage
    // or when changing from one controller to another.  this way at least something
    // works.
    public readonly Cvar in_joystick = new("joystick", "0", true);
    public readonly Cvar joy_name = new("joyname", "joystick");
    public readonly Cvar joy_advanced = new("joyadvanced", "0");
    public readonly Cvar joy_advaxisx = new("joyadvaxisx", "0");
    public readonly Cvar joy_advaxisy = new("joyadvaxisy", "0");
    public readonly Cvar joy_advaxisz = new("joyadvaxisz", "0");
    public readonly Cvar joy_advaxisr = new("joyadvaxisr", "0");
    public readonly Cvar joy_advaxisu = new("joyadvaxisu", "0");
    public readonly Cvar joy_advaxisv = new("joyadvaxisv", "0");
    public readonly Cvar joy_forwardthreshold = new("joyforwardthreshold", "0.15");
    public readonly Cvar joy_sidethreshold = new("joysidethreshold", "0.15");
    public readonly Cvar joy_pitchthreshold = new("joypitchthreshold", "0.15");
    public readonly Cvar joy_yawthreshold = new("joyyawthreshold", "0.15");
    public readonly Cvar joy_forwardsensitivity = new("joyforwardsensitivity", "-1.0");
    public readonly Cvar joy_sidesensitivity = new("joysidesensitivity", "-1.0");
    public readonly Cvar joy_pitchsensitivity = new("joypitchsensitivity", "1.0");
    public readonly Cvar joy_yawsensitivity = new("joyyawsensitivity", "-1.0");
    public readonly Cvar joy_wwhack1 = new("joywwhack1", "0.0");
    public readonly Cvar joy_wwhack2 = new("joywwhack2", "0.0");

    public bool joy_avail, joy_advancedinit, joy_haspov;
    public uint joy_oldbuttonstate, joy_oldpovstate;

    public int joy_id;
    public uint joy_flags;
    public uint joy_numbuttons;

    private JoyInfoEx ji;

    // in_win.c: dinput, true with -dinput once DirectInput started (not ported: as without the switch)
    private const bool dinput = false;

    /// <summary>The trace's I line: in_win.c's state as the oracle's Oracle_InState gives it.</summary>
    public IEnumerable<int> InState()
    {
        yield return mouseactive ? 1 : 0;
        yield return mouseinitialized ? 1 : 0;
        yield return mouseactivatetoggle ? 1 : 0;
        yield return mouseshowtoggle ? 1 : 0;
        yield return mouseparmsvalid ? 1 : 0;
        yield return restore_spi ? 1 : 0;
        yield return dinput ? 1 : 0;
        yield return dinput_acquired ? 1 : 0;
        yield return (int)mstate_di;
        yield return mouse_buttons;
        yield return mouse_oldbuttonstate;
        yield return current_pos_x;
        yield return current_pos_y;
        yield return mx_accum;
        yield return my_accum;
        yield return mouse_x;
        yield return mouse_y;
        yield return old_mouse_x;
        yield return old_mouse_y;
        yield return joy_avail ? 1 : 0;
        yield return joy_advancedinit ? 1 : 0;
        yield return joy_haspov ? 1 : 0;
        yield return joy_id;
        yield return (int)joy_flags;
        yield return (int)joy_numbuttons;
        yield return (int)joy_oldbuttonstate;
        yield return (int)joy_oldpovstate;
        foreach (uint a in dwAxisMap)
        {
            yield return (int)a;
        }

        foreach (uint c in dwControlMap)
        {
            yield return (int)c;
        }
    }

    // in_win.c: Force_CenterView_f
    private void Force_CenterView_f() => cl.viewangles[PITCH] = 0;

    // in_win.c: IN_UpdateClipCursor
    public void IN_UpdateClipCursor()
    {
        if (mouseinitialized && mouseactive && !dinput)
        {
            input.ClipCursor(window_rect);
        }
    }

    // in_win.c: IN_ShowMouse
    public void IN_ShowMouse()
    {
        if (!mouseshowtoggle)
        {
            input.ShowCursor(true);
            mouseshowtoggle = true;
        }
    }

    // in_win.c: IN_HideMouse
    public void IN_HideMouse()
    {
        if (mouseshowtoggle)
        {
            input.ShowCursor(false);
            mouseshowtoggle = false;
        }
    }

    // in_win.c: IN_ActivateMouse
    public void IN_ActivateMouse()
    {
        mouseactivatetoggle = true;

        if (mouseinitialized)
        {
            if (mouseparmsvalid)
            {
                restore_spi = input.SystemParametersInfo(Win32.SPI_SETMOUSE, newmouseparms);
            }

            input.SetCursorPos(window_center_x, window_center_y);
            input.SetCapture();
            input.ClipCursor(window_rect);

            mouseactive = true;
        }
    }

    // in_win.c: IN_SetQuakeMouseState
    public void IN_SetQuakeMouseState()
    {
        if (mouseactivatetoggle)
        {
            IN_ActivateMouse();
        }
    }

    // in_win.c: IN_DeactivateMouse
    public void IN_DeactivateMouse()
    {
        mouseactivatetoggle = false;

        if (mouseinitialized)
        {
            if (restore_spi)
            {
                input.SystemParametersInfo(Win32.SPI_SETMOUSE, originalmouseparms);
            }

            input.ClipCursor(null);
            input.ReleaseCapture();

            mouseactive = false;
        }
    }

    // in_win.c: IN_RestoreOriginalMouseState
    public void IN_RestoreOriginalMouseState()
    {
        if (mouseactivatetoggle)
        {
            IN_DeactivateMouse();
            mouseactivatetoggle = true;
        }

        // try to redraw the cursor so it gets reinitialized, because sometimes it
        // has garbage after the mode switch
        input.ShowCursor(true);
        input.ShowCursor(false);
    }

    // in_win.c: IN_StartupMouse (without DirectInput)
    private void IN_StartupMouse()
    {
        if (COM_CheckParm("-nomouse") != 0)
        {
            return;
        }

        mouseinitialized = true;

        if (!dinput)
        {
            mouseparmsvalid = input.SystemParametersInfo(Win32.SPI_GETMOUSE, originalmouseparms);

            if (mouseparmsvalid)
            {
                if (COM_CheckParm("-noforcemspd") != 0)
                {
                    newmouseparms[2] = originalmouseparms[2];
                }

                if (COM_CheckParm("-noforcemaccel") != 0)
                {
                    newmouseparms[0] = originalmouseparms[0];
                    newmouseparms[1] = originalmouseparms[1];
                }

                if (COM_CheckParm("-noforcemparms") != 0)
                {
                    newmouseparms[0] = originalmouseparms[0];
                    newmouseparms[1] = originalmouseparms[1];
                    newmouseparms[2] = originalmouseparms[2];
                }
            }
        }

        mouse_buttons = 3;

        // if a fullscreen video mode was set before the mouse was initialized,
        // set the mouse state appropriately
        if (mouseactivatetoggle)
        {
            IN_ActivateMouse();
        }
    }

    // in_win.c: IN_Init
    public void IN_Init()
    {
        // mouse variables
        Cvar_RegisterVariable(m_filter);

        // joystick variables
        Cvar_RegisterVariable(in_joystick);
        Cvar_RegisterVariable(joy_name);
        Cvar_RegisterVariable(joy_advanced);
        Cvar_RegisterVariable(joy_advaxisx);
        Cvar_RegisterVariable(joy_advaxisy);
        Cvar_RegisterVariable(joy_advaxisz);
        Cvar_RegisterVariable(joy_advaxisr);
        Cvar_RegisterVariable(joy_advaxisu);
        Cvar_RegisterVariable(joy_advaxisv);
        Cvar_RegisterVariable(joy_forwardthreshold);
        Cvar_RegisterVariable(joy_sidethreshold);
        Cvar_RegisterVariable(joy_pitchthreshold);
        Cvar_RegisterVariable(joy_yawthreshold);
        Cvar_RegisterVariable(joy_forwardsensitivity);
        Cvar_RegisterVariable(joy_sidesensitivity);
        Cvar_RegisterVariable(joy_pitchsensitivity);
        Cvar_RegisterVariable(joy_yawsensitivity);
        Cvar_RegisterVariable(joy_wwhack1);
        Cvar_RegisterVariable(joy_wwhack2);

        Cmd_AddCommand("force_centerview", Force_CenterView_f);
        Cmd_AddCommand("joyadvancedupdate", Joy_AdvancedUpdate_f);

        uiWheelMessage = VirtualInput.RegisterWindowMessage("MSWHEEL_ROLLMSG");

        IN_StartupMouse();
        IN_StartupJoystick();
    }

    // in_win.c: IN_Shutdown
    public void IN_Shutdown()
    {
        IN_DeactivateMouse();
        IN_ShowMouse();
    }

    // in_win.c: IN_MouseEvent
    public void IN_MouseEvent(int mstate)
    {
        int i;

        if (mouseactive && !dinput)
        {
            // perform button actions
            for (i = 0; i < mouse_buttons; i++)
            {
                if ((mstate & (1 << i)) != 0 && (mouse_oldbuttonstate & (1 << i)) == 0)
                {
                    Key_Event(K_MOUSE1 + i, true);
                }

                if ((mstate & (1 << i)) == 0 && (mouse_oldbuttonstate & (1 << i)) != 0)
                {
                    Key_Event(K_MOUSE1 + i, false);
                }
            }

            mouse_oldbuttonstate = mstate;
        }
    }

    // in_win.c: IN_MouseMove (without DirectInput)
    private void IN_MouseMove(ref UserCmd cmd)
    {
        int mx, my;

        if (!mouseactive)
        {
            return;
        }

        input.GetCursorPos(out current_pos_x, out current_pos_y);
        mx = current_pos_x - window_center_x + mx_accum;
        my = current_pos_y - window_center_y + my_accum;
        mx_accum = 0;
        my_accum = 0;

        if (m_filter.value != 0)
        {
            mouse_x = (int)((mx + old_mouse_x) * 0.5);
            mouse_y = (int)((my + old_mouse_y) * 0.5);
        }
        else
        {
            mouse_x = mx;
            mouse_y = my;
        }

        old_mouse_x = mx;
        old_mouse_y = my;

        mouse_x = X86.FtoI(mouse_x * sensitivity.value);
        mouse_y = X86.FtoI(mouse_y * sensitivity.value);

        // add mouse X/Y movement to cmd
        if ((in_strafe.state & 1) != 0 || (lookstrafe.value != 0 && (in_mlook.state & 1) != 0))
        {
            cmd.sidemove += m_side.value * mouse_x;
        }
        else
        {
            cl.viewangles[YAW] -= m_yaw.value * mouse_x;
        }

        if ((in_mlook.state & 1) != 0)
        {
            V_StopPitchDrift();
        }

        if ((in_mlook.state & 1) != 0 && (in_strafe.state & 1) == 0)
        {
            cl.viewangles[PITCH] += m_pitch.value * mouse_y;
            if (cl.viewangles[PITCH] > 80)
            {
                cl.viewangles[PITCH] = 80;
            }

            if (cl.viewangles[PITCH] < -70)
            {
                cl.viewangles[PITCH] = -70;
            }
        }
        else
        {
            if ((in_strafe.state & 1) != 0 && noclip_anglehack)
            {
                cmd.upmove -= m_forward.value * mouse_y;
            }
            else
            {
                cmd.forwardmove -= m_forward.value * mouse_y;
            }
        }

        // if the mouse has moved, force it to the center, so there's room to move
        if (mx != 0 || my != 0)
        {
            input.SetCursorPos(window_center_x, window_center_y);
        }
    }

    // in_win.c: IN_Move
    public void IN_Move(ref UserCmd cmd)
    {
        if (ActiveApp && !Minimized)
        {
            IN_MouseMove(ref cmd);
            IN_JoyMove(ref cmd);
        }
    }

    // in_win.c: IN_Accumulate
    public void IN_Accumulate()
    {
        if (mouseactive)
        {
            if (!dinput)
            {
                input.GetCursorPos(out current_pos_x, out current_pos_y);

                mx_accum += current_pos_x - window_center_x;
                my_accum += current_pos_y - window_center_y;

                // force the mouse to the center, so there's room to move
                input.SetCursorPos(window_center_x, window_center_y);
            }
        }
    }

    // in_win.c: IN_ClearStates
    public void IN_ClearStates()
    {
        if (mouseactive)
        {
            mx_accum = 0;
            my_accum = 0;
            mouse_oldbuttonstate = 0;
        }
    }

    // in_win.c: IN_StartupJoystick
    private void IN_StartupJoystick()
    {
        uint numdevs;
        JoyCaps jc;
        uint mmr = 0;

        // assume no joystick
        joy_avail = false;

        // abort startup if user requests no joystick
        if (COM_CheckParm("-nojoy") != 0)
        {
            return;
        }

        // verify joystick driver is present
        if ((numdevs = input.joyGetNumDevs()) == 0)
        {
            Con_Printf("\njoystick not found -- driver not present\n\n");
            return;
        }

        // cycle through the joystick ids for the first valid one
        for (joy_id = 0; joy_id < numdevs; joy_id++)
        {
            ji = default;
            ji.dwSize = 52;
            ji.dwFlags = Win32.JOY_RETURNCENTERED;

            if ((mmr = input.joyGetPosEx((uint)joy_id, ref ji)) == Win32.JOYERR_NOERROR)
            {
                break;
            }
        }

        // abort startup if we didn't find a valid joystick
        if (mmr != Win32.JOYERR_NOERROR)
        {
            Con_Printf("\njoystick not found -- no valid joysticks (%x)\n\n", mmr);
            return;
        }

        // get the capabilities of the selected joystick
        // abort startup if command fails
        if ((mmr = input.joyGetDevCaps((uint)joy_id, out jc)) != Win32.JOYERR_NOERROR)
        {
            Con_Printf("\njoystick not found -- invalid joystick capabilities (%x)\n\n", mmr);
            return;
        }

        // save the joystick's number of buttons and POV status
        joy_numbuttons = jc.wNumButtons;
        joy_haspov = (jc.wCaps & Win32.JOYCAPS_HASPOV) != 0;

        // old button and POV states default to no buttons pressed
        joy_oldbuttonstate = joy_oldpovstate = 0;

        // mark the joystick as available and advanced initialization not completed
        // this is needed as cvars are not available during initialization
        joy_avail = true;
        joy_advancedinit = false;

        Con_Printf("\njoystick detected\n\n");
    }

    // in_win.c: RawValuePointer: the axis's position in ji
    private uint RawValue(int axis) => axis switch
    {
        JOY_AXIS_X => ji.dwXpos,
        JOY_AXIS_Y => ji.dwYpos,
        JOY_AXIS_Z => ji.dwZpos,
        JOY_AXIS_R => ji.dwRpos,
        JOY_AXIS_U => ji.dwUpos,
        _ => ji.dwVpos,
    };

    // in_win.c: Joy_AdvancedUpdate_f
    private void Joy_AdvancedUpdate_f()
    {
        // called once by IN_ReadJoystick and by user whenever an update is needed
        // cvars are now available
        int i;
        uint dwTemp;

        // initialize all the maps
        for (i = 0; i < JOY_MAX_AXES; i++)
        {
            dwAxisMap[i] = AxisNada;
            dwControlMap[i] = JOY_ABSOLUTE_AXIS;
            pdwRawValue[i] = i;
        }

        if (joy_advanced.value == 0.0)
        {
            // default joystick initialization
            // 2 axes only with joystick control
            dwAxisMap[JOY_AXIS_X] = AxisTurn;
            // dwControlMap[JOY_AXIS_X] = JOY_ABSOLUTE_AXIS;
            dwAxisMap[JOY_AXIS_Y] = AxisForward;
            // dwControlMap[JOY_AXIS_Y] = JOY_ABSOLUTE_AXIS;
        }
        else
        {
            if (QString.Q_strcmp(joy_name.@string, "joystick") != 0)
            {
                // notify user of advanced controller
                Con_Printf("\n%s configured\n\n", joy_name.@string);
            }

            // advanced initialization here
            // data supplied by user via joy_axisn cvars
            dwTemp = (uint)X86.FtoI(joy_advaxisx.value);
            dwAxisMap[JOY_AXIS_X] = dwTemp & 0x0000000f;
            dwControlMap[JOY_AXIS_X] = dwTemp & JOY_RELATIVE_AXIS;
            dwTemp = (uint)X86.FtoI(joy_advaxisy.value);
            dwAxisMap[JOY_AXIS_Y] = dwTemp & 0x0000000f;
            dwControlMap[JOY_AXIS_Y] = dwTemp & JOY_RELATIVE_AXIS;
            dwTemp = (uint)X86.FtoI(joy_advaxisz.value);
            dwAxisMap[JOY_AXIS_Z] = dwTemp & 0x0000000f;
            dwControlMap[JOY_AXIS_Z] = dwTemp & JOY_RELATIVE_AXIS;
            dwTemp = (uint)X86.FtoI(joy_advaxisr.value);
            dwAxisMap[JOY_AXIS_R] = dwTemp & 0x0000000f;
            dwControlMap[JOY_AXIS_R] = dwTemp & JOY_RELATIVE_AXIS;
            dwTemp = (uint)X86.FtoI(joy_advaxisu.value);
            dwAxisMap[JOY_AXIS_U] = dwTemp & 0x0000000f;
            dwControlMap[JOY_AXIS_U] = dwTemp & JOY_RELATIVE_AXIS;
            dwTemp = (uint)X86.FtoI(joy_advaxisv.value);
            dwAxisMap[JOY_AXIS_V] = dwTemp & 0x0000000f;
            dwControlMap[JOY_AXIS_V] = dwTemp & JOY_RELATIVE_AXIS;
        }

        // compute the axes to collect from DirectInput
        joy_flags = Win32.JOY_RETURNCENTERED | Win32.JOY_RETURNBUTTONS | Win32.JOY_RETURNPOV;
        for (i = 0; i < JOY_MAX_AXES; i++)
        {
            if (dwAxisMap[i] != AxisNada)
            {
                joy_flags |= dwAxisFlags[i];
            }
        }
    }

    // in_win.c: IN_Commands
    public void IN_Commands()
    {
        int i, key_index;
        uint buttonstate, povstate;

        if (!joy_avail)
        {
            return;
        }

        // loop through the joystick buttons
        // key a joystick event or auxillary event for higher number buttons for each state change
        buttonstate = ji.dwButtons;
        for (i = 0; i < joy_numbuttons; i++)
        {
            if ((buttonstate & (1u << i)) != 0 && (joy_oldbuttonstate & (1u << i)) == 0)
            {
                key_index = (i < 4) ? K_JOY1 : K_AUX1;
                Key_Event(key_index + i, true);
            }

            if ((buttonstate & (1u << i)) == 0 && (joy_oldbuttonstate & (1u << i)) != 0)
            {
                key_index = (i < 4) ? K_JOY1 : K_AUX1;
                Key_Event(key_index + i, false);
            }
        }

        joy_oldbuttonstate = buttonstate;

        if (joy_haspov)
        {
            // convert POV information into 4 bits of state information
            // this avoids any potential problems related to moving from one
            // direction to another without going through the center position
            povstate = 0;
            if (ji.dwPOV != Win32.JOY_POVCENTERED)
            {
                if (ji.dwPOV == Win32.JOY_POVFORWARD)
                {
                    povstate |= 0x01;
                }

                if (ji.dwPOV == Win32.JOY_POVRIGHT)
                {
                    povstate |= 0x02;
                }

                if (ji.dwPOV == Win32.JOY_POVBACKWARD)
                {
                    povstate |= 0x04;
                }

                if (ji.dwPOV == Win32.JOY_POVLEFT)
                {
                    povstate |= 0x08;
                }
            }

            // determine which bits have changed and key an auxillary event for each change
            for (i = 0; i < 4; i++)
            {
                if ((povstate & (1u << i)) != 0 && (joy_oldpovstate & (1u << i)) == 0)
                {
                    Key_Event(K_AUX29 + i, true);
                }

                if ((povstate & (1u << i)) == 0 && (joy_oldpovstate & (1u << i)) != 0)
                {
                    Key_Event(K_AUX29 + i, false);
                }
            }

            joy_oldpovstate = povstate;
        }
    }

    // in_win.c: IN_ReadJoystick
    private bool IN_ReadJoystick()
    {
        ji = default;
        ji.dwSize = 52;
        ji.dwFlags = joy_flags;

        if (input.joyGetPosEx((uint)joy_id, ref ji) == Win32.JOYERR_NOERROR)
        {
            // this is a hack -- there is a bug in the Logitech WingMan Warrior DirectInput Driver
            // rather than having 32768 be the zero point, they have the zero point at 32668
            // go figure -- anyway, now we get the full resolution out of the device
            if (joy_wwhack1.value != 0.0)
            {
                ji.dwUpos += 100;
            }

            return true;
        }
        else
        {
            // read error occurred
            // turning off the joystick seems too harsh for 1 read error,\
            // but what should be done?
            // Con_Printf ("IN_ReadJoystick: no response\n");
            // joy_avail = false;
            return false;
        }
    }

    // in_win.c: IN_JoyMove
    private void IN_JoyMove(ref UserCmd cmd)
    {
        float speed, aspeed;
        float fAxisValue, fTemp;
        int i;

        // complete initialization if first time in
        // this is needed as cvars are not available at initialization time
        if (!joy_advancedinit)
        {
            Joy_AdvancedUpdate_f();
            joy_advancedinit = true;
        }

        // verify joystick is available and that the user wants to use it
        if (!joy_avail || in_joystick.value == 0)
        {
            return;
        }

        // collect the joystick data, if possible
        if (!IN_ReadJoystick())
        {
            return;
        }

        if ((in_speed.state & 1) != 0)
        {
            speed = cl_movespeedkey.value;
        }
        else
        {
            speed = 1;
        }

        aspeed = (float)(speed * host_frametime);

        // loop through the axes
        for (i = 0; i < JOY_MAX_AXES; i++)
        {
            // get the floating point zero-centered, potentially-inverted data for the current axis
            fAxisValue = RawValue(pdwRawValue[i]);
            // move centerpoint to zero
            fAxisValue = (float)(fAxisValue - 32768.0);

            if (joy_wwhack2.value != 0.0)
            {
                if (dwAxisMap[i] == AxisTurn)
                {
                    // this is a special formula for the Logitech WingMan Warrior
                    // y=ax^b; where a = 300 and b = 1.3
                    // also x values are in increments of 800 (so this is factored out)
                    // then bounds check result to level out excessively high spin rates
                    // (abs is the int's: the float truncated first)
                    fTemp = (float)(300.0 * Libm.pow(Math.Abs(X86.FtoI(fAxisValue)) / 800.0, 1.3));
                    if (fTemp > 14000.0)
                    {
                        fTemp = 14000.0f;
                    }

                    // restore direction information
                    fAxisValue = (fAxisValue > 0.0) ? fTemp : -fTemp;
                }
            }

            // convert range from -32768..32767 to -1..1
            fAxisValue = (float)(fAxisValue / 32768.0);

            switch (dwAxisMap[i])
            {
                case AxisForward:
                    if ((joy_advanced.value == 0.0) && (in_mlook.state & 1) != 0)
                    {
                        // user wants forward control to become look control
                        if (Math.Abs((double)fAxisValue) > joy_pitchthreshold.value)
                        {
                            // if mouse invert is on, invert the joystick pitch value
                            // only absolute control support here (joy_advanced is false)
                            if (m_pitch.value < 0.0)
                            {
                                cl.viewangles[PITCH] -= fAxisValue * joy_pitchsensitivity.value * aspeed * cl_pitchspeed.value;
                            }
                            else
                            {
                                cl.viewangles[PITCH] += fAxisValue * joy_pitchsensitivity.value * aspeed * cl_pitchspeed.value;
                            }

                            V_StopPitchDrift();
                        }
                        else
                        {
                            // no pitch movement
                            // disable pitch return-to-center unless requested by user
                            // *** this code can be removed when the lookspring bug is fixed
                            // *** the bug always has the lookspring feature on
                            if (lookspring.value == 0.0)
                            {
                                V_StopPitchDrift();
                            }
                        }
                    }
                    else
                    {
                        // user wants forward control to be forward control
                        if (Math.Abs((double)fAxisValue) > joy_forwardthreshold.value)
                        {
                            cmd.forwardmove += fAxisValue * joy_forwardsensitivity.value * speed * cl_forwardspeed.value;
                        }
                    }

                    break;

                case AxisSide:
                    if (Math.Abs((double)fAxisValue) > joy_sidethreshold.value)
                    {
                        cmd.sidemove += fAxisValue * joy_sidesensitivity.value * speed * cl_sidespeed.value;
                    }

                    break;

                case AxisTurn:
                    if ((in_strafe.state & 1) != 0 || (lookstrafe.value != 0 && (in_mlook.state & 1) != 0))
                    {
                        // user wants turn control to become side control
                        if (Math.Abs((double)fAxisValue) > joy_sidethreshold.value)
                        {
                            cmd.sidemove -= fAxisValue * joy_sidesensitivity.value * speed * cl_sidespeed.value;
                        }
                    }
                    else
                    {
                        // user wants turn control to be turn control
                        if (Math.Abs((double)fAxisValue) > joy_yawthreshold.value)
                        {
                            if (dwControlMap[i] == JOY_ABSOLUTE_AXIS)
                            {
                                cl.viewangles[YAW] += fAxisValue * joy_yawsensitivity.value * aspeed * cl_yawspeed.value;
                            }
                            else
                            {
                                cl.viewangles[YAW] = (float)(cl.viewangles[YAW] + (fAxisValue * joy_yawsensitivity.value * speed * 180.0));
                            }
                        }
                    }

                    break;

                case AxisLook:
                    if ((in_mlook.state & 1) != 0)
                    {
                        if (Math.Abs((double)fAxisValue) > joy_pitchthreshold.value)
                        {
                            // pitch movement detected and pitch movement desired by user
                            if (dwControlMap[i] == JOY_ABSOLUTE_AXIS)
                            {
                                cl.viewangles[PITCH] += fAxisValue * joy_pitchsensitivity.value * aspeed * cl_pitchspeed.value;
                            }
                            else
                            {
                                cl.viewangles[PITCH] = (float)(cl.viewangles[PITCH] + (fAxisValue * joy_pitchsensitivity.value * speed * 180.0));
                            }

                            V_StopPitchDrift();
                        }
                        else
                        {
                            // no pitch movement
                            // disable pitch return-to-center unless requested by user
                            // *** this code can be removed when the lookspring bug is fixed
                            // *** the bug always has the lookspring feature on
                            if (lookspring.value == 0.0)
                            {
                                V_StopPitchDrift();
                            }
                        }
                    }

                    break;

                default:
                    break;
            }
        }

        // bounds check pitch
        if (cl.viewangles[PITCH] > 80.0)
        {
            cl.viewangles[PITCH] = 80.0f;
        }

        if (cl.viewangles[PITCH] < -70.0)
        {
            cl.viewangles[PITCH] = -70.0f;
        }
    }
}
