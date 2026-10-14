using QuakeSharp.Core.Client;
using QuakeSharp.Core.Input;
using static QuakeSharp.Core.QuakeDef;
using static QuakeSharp.Core.UI.Keys;
using QuakeSharp.Core.Enhanced;
using QuakeSharp.Core.UI;

namespace QuakeSharp.Core;

// The enhanced mode's mouse and game controller (design.md decision 76). None of it is WinQuake's: the seams in in_win.c,
// keys.c and vid_win.c ask enhancedInput and behave as before while it is null, which it is in the faithful mode.
public sealed partial class QuakeEngine
{
    /// <summary>The enhanced mode's input while it is on (null: WinQuake's own).</summary>
    public EnhancedInput? enhancedInput;

    private JoyInfoEx pad;
    private int padId;

    /// <summary>The controller the enhanced input reads (its slot), for its rumble.</summary>
    public int EIN_PadSlot => padId;

    /// <summary>The input set on (or off, null), the pad's held keys let go first.</summary>
    public void EIN_Set(EnhancedInput? input)
    {
        enhancedInput?.Release(PadKey);
        enhancedInput = input;
    }

    // keys.c: the binding of a key, or the enhanced mode's default for one the player left unbound
    private string? KeyBinding(int key) => keybindings[key] ?? enhancedInput?.DefaultBinding(key);

    // in_win.c: in_mlook.state & 1, with the enhanced mode's free look
    private bool FreeLook() => (in_mlook.state & 1) != 0 || enhancedInput is { Mouse: true };

    // vid_win.c: _windowed_mouse as the mouse's taking sees it
    private int WindowedMouse() => enhancedInput is { Mouse: true } ? 1 : (int)_windowed_mouse.value;

    // a pad's key event; a key with nothing bound to it is not sent (it would print "is unbound")
    private void PadKey(int key, bool down)
    {
        if (key >= 200 && KeyBinding(key) == null)
        {
            return;
        }

        Key_Event(key, down);
    }

    // the first controller that answers
    private bool ReadPad()
    {
        if (COM_CheckParm("-nojoy") != 0)
        {
            return false;
        }

        uint devices = input.joyGetNumDevs();
        for (uint n = 0; n < devices; n++)
        {
            uint id = (uint)((padId + n) % devices);
            pad = default;
            pad.dwSize = 52;
            pad.dwFlags = Win32.JOY_RETURNX | Win32.JOY_RETURNY | Win32.JOY_RETURNZ | Win32.JOY_RETURNR | Win32.JOY_RETURNU
                | Win32.JOY_RETURNV | Win32.JOY_RETURNPOV | Win32.JOY_RETURNBUTTONS | Win32.JOY_RETURNCENTERED;
            if (input.joyGetPosEx(id, ref pad) == Win32.JOYERR_NOERROR)
            {
                padId = (int)id;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The enhanced mode's pointer in a menu (design.md §11.9) at pixel (x, y) of the frame: over an item of the main,
    /// single player, multiplayer, load, save, options or keys menu, that item chosen as the arrow keys would choose it.
    /// </summary>
    public void EIN_MenuPoint(int x, int y)
    {
        if (key_dest != keydest_t.key_menu || !MenuItemAt(x, y, out int item, out _))
        {
            return;
        }

        ref int cursor = ref MenuCursor();
        if (cursor != item)
        {
            cursor = item;
            S_LocalSound("misc/menu1.wav");
        }
    }

    /// <summary>
    /// A click in a menu: the left button chooses (Enter; on an options slider or switch, its left or right half moves
    /// it as the arrow keys do), button 2 (right) goes back (Escape), 4 and 5 (the wheel up and down) are the arrow keys;
    /// the middle button does nothing but bind itself in the keys menu.
    /// </summary>
    public void EIN_MenuClick(int x, int y, int button)
    {
        if (key_dest != keydest_t.key_menu)
        {
            return;
        }

        int key;
        if (m_state == m_state_t.m_keys && bind_grab && button is 1 or 2 or 3)
        {
            key = K_MOUSE1 + button - 1; // the keys menu waits for a key: the button is the key bound, as WinQuake's
        }
        else if (button == 1)
        {
            bool over = MenuItemAt(x, y, out int item, out bool adjustable);
            if (m_state is m_state_t.m_main or m_state_t.m_singleplayer or m_state_t.m_multiplayer or m_state_t.m_load
                or m_state_t.m_save or m_state_t.m_options or m_state_t.m_keys && !over)
            {
                return; // beside a menu's items
            }

            key = adjustable && item == MenuCursor()
                ? (x - ((int)(vid.width - 320) >> 1) < 220 + ((SLIDER_RANGE + 2) * 4) ? K_LEFTARROW : K_RIGHTARROW)
                : K_ENTER;
        }
        else
        {
            key = button switch
            {
                2 => K_ESCAPE,
                4 => K_UPARROW,
                5 => K_DOWNARROW,
                _ => 0,
            };
        }

        if (key == 0)
        {
            return;
        }

        Key_Event(key, true);
        Key_Event(key, false);
    }

    // the menu's item at the frame's pixel (x, y), and whether it is a slider or switch of the options
    private bool MenuItemAt(int x, int y, out int item, out bool adjustable)
    {
        int mx = x - ((int)(vid.width - 320) >> 1);
        item = -1;
        adjustable = false;
        (int top, int height, int count, int left, int right) = m_state switch
        {
            m_state_t.m_main => (32, 20, MAIN_ITEMS, 72, 312),
            m_state_t.m_singleplayer => (32, 20, SINGLEPLAYER_ITEMS, 72, 312),
            m_state_t.m_multiplayer => (32, 20, MULTIPLAYER_ITEMS, 72, 312),
            m_state_t.m_load or m_state_t.m_save => (32, 8, MAX_SAVEGAMES, 8, 320),
            m_state_t.m_options => (32, 8, OPTIONS_ITEMS, 0, 320),
            m_state_t.m_keys => (48, 8, NUMCOMMANDS, 0, 320),
            _ => (0, 0, 0, 0, 0),
        };
        if (count == 0 || mx < left || mx >= right || y < top || y >= top + (height * count))
        {
            return false;
        }

        item = (y - top) / height;
        if (m_state == m_state_t.m_options)
        {
            // as M_Options_Key skips them: the video modes without a video menu, the mouse's switch full screen
            if ((item == 12 && vid_menudrawfn == null) || (item == 13 && modestate != modestate_t.MS_WINDOWED))
            {
                return false;
            }

            adjustable = item is >= 3 and <= 11 or 13;
        }

        return true;
    }

    // the cursor of the menu shown
    private ref int MenuCursor()
    {
        switch (m_state)
        {
            case m_state_t.m_main:
                return ref m_main_cursor;
            case m_state_t.m_singleplayer:
                return ref m_singleplayer_cursor;
            case m_state_t.m_multiplayer:
                return ref m_multiplayer_cursor;
            case m_state_t.m_load:
            case m_state_t.m_save:
                return ref load_cursor;
            case m_state_t.m_options:
                return ref options_cursor;
            default:
                return ref keys_cursor;
        }
    }

    // in_win.c: IN_Commands, for the enhanced mode's controller
    private void EIN_PadCommands()
    {
        EnhancedInput e = enhancedInput!;
        if (ReadPad())
        {
            e.PadKeys(pad, key_dest == keydest_t.key_menu, host_frametime, PadKey);
        }
        else
        {
            e.Release(PadKey);
        }
    }

    // in_win.c: IN_JoyMove, for the enhanced mode's controller: the left stick walks and strafes (a full push runs), the
    // right stick turns and looks
    private void EIN_PadMove(ref UserCmd cmd)
    {
        EnhancedInput e = enhancedInput!;
        if (key_dest != keydest_t.key_game || !ReadPad())
        {
            return;
        }

        (float lx, float ly) = EnhancedInput.Stick(pad.dwXpos, pad.dwYpos, e.Deadzone);
        cmd.forwardmove -= ly * cl_forwardspeed.value * cl_movespeedkey.value;
        cmd.sidemove += lx * cl_sidespeed.value * cl_movespeedkey.value;

        (float rx, float ry) = EnhancedInput.Stick(pad.dwZpos, pad.dwRpos, e.Deadzone);
        float yaw = EnhancedInput.Curve(rx) * EnhancedInput.YawRate * e.Sensitivity * (float)host_frametime;
        float pitch = EnhancedInput.Curve(ry) * EnhancedInput.PitchRate * e.Sensitivity * (float)host_frametime;
        cl.viewangles[YAW] -= yaw;
        if (pitch != 0)
        {
            // down on the stick looks down, and an inverted m_pitch inverts it as it does the mouse
            cl.viewangles[PITCH] += m_pitch.value < 0 ? -pitch : pitch;
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
    }
}
