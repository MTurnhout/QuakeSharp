using System.Buffers.Binary;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Render;
using QuakeSharp.Core.Video;

namespace QuakeSharp.Core;

// vid_win.c: the video driver, as WinQuake has it in a window (and the oracle's vid_oracle.c): one mode of the size
// QuakeParms asks for, the 8-bit frame in memory, shown by the platform's IVideoOutput. What vid_win.c does that the
// game shows is kept (design.md §6.4): its cvars and commands, the remapping of the colormap's entries 0, S_Init
// between the hidden first mode set and the real one, the pixel aspect, the surface cache and the mode's name on the
// console; and what it does with the mouse (in_win.c's, over VirtualInput's screen): taken in a window only with
// _windowed_mouse, released while paused. The window's client area is at the screen's top left, as the oracle's. Its
// other modes and their menu come with milestone 10 (design.md §11.4).
/// <summary>winquake.h: modestate_t</summary>
public enum modestate_t
{
    MS_WINDOWED,
    MS_FULLSCREEN,
    MS_FULLDIB,
    MS_UNINIT,
}

public sealed partial class QuakeEngine
{
    // vid.h: VID_CBITS, VID_GRADES
    public const int VID_CBITS = 6;
    public const int VID_GRADES = 1 << VID_CBITS;

    /// <summary>vid_win.c: vid, the global video state</summary>
    public readonly VidDef vid = new();

    /// <summary>vid_win.c: the palette last set (the frame's colours)</summary>
    public readonly byte[] vid_curpal = new byte[256 * 3];

    public readonly Cvar vid_mode = new("vid_mode", "0", false);
    public readonly Cvar _vid_default_mode = new("_vid_default_mode", "0", true);
    public readonly Cvar _vid_default_mode_win = new("_vid_default_mode_win", "3", true);
    public readonly Cvar vid_wait = new("vid_wait", "0");
    public readonly Cvar vid_nopageflip = new("vid_nopageflip", "0", true);
    public readonly Cvar _vid_wait_override = new("_vid_wait_override", "0", true);
    public readonly Cvar vid_config_x = new("vid_config_x", "800", true);
    public readonly Cvar vid_config_y = new("vid_config_y", "600", true);
    public readonly Cvar vid_stretch_by_2 = new("vid_stretch_by_2", "1", true);
    public readonly Cvar _windowed_mouse = new("_windowed_mouse", "0", true);
    public readonly Cvar vid_fullscreen_mode = new("vid_fullscreen_mode", "3", true);
    public readonly Cvar vid_windowed_mode = new("vid_windowed_mode", "0", true);
    public readonly Cvar block_switch = new("block_switch", "0", true);
    public readonly Cvar vid_window_x = new("vid_window_x", "0", true);
    public readonly Cvar vid_window_y = new("vid_window_y", "0", true);

    /// <summary>winquake.h: modestate (the mode is a window, as the oracle's)</summary>
    public modestate_t modestate = modestate_t.MS_UNINIT;

    // vid_win.c: MODE_WINDOWED, NO_MODE, vid_modenum
    private const int MODE_WINDOWED = 0;
    private const int NO_MODE = MODE_WINDOWED - 1;
    public int vid_modenum = NO_MODE;

    // vid_win.c: the window's place and size on the screen, its rectangle and centre (in_win.c's cursor)
    public int window_center_x, window_center_y, window_x, window_y, window_width, window_height;
    public Input.Rect window_rect;

    // vid_win.c: vid_stretched, windowed_mouse
    private int vid_stretched;
    public int windowed_mouse;

    // vid_win.c: Minimized (only the full-screen DIB modes set it)
    public bool Minimized;

    /// <summary>vid_win.c: vid_menudrawfn, vid_menukeyfn (menu.c's video menu calls them)</summary>
    public Action? vid_menudrawfn;

    public Action<int>? vid_menukeyfn;

    // vid_win.c: the mode (one, of the size the host asks for) and whether it is hidden (VID_Init's first set)
    private int mode_width = 320, mode_height = 240;
    private bool hide_window;

    // vid_win.c: VID_SetPalette
    public void VID_SetPalette(ReadOnlySpan<byte> palette) => palette[..vid_curpal.Length].CopyTo(vid_curpal);

    // vid_win.c: VID_ShiftPalette
    public void VID_ShiftPalette(ReadOnlySpan<byte> palette) => VID_SetPalette(palette);

    // vid_win.c: VID_MenuDraw: the list of modes (one mode here: nothing to list, as the oracle's)
    private static void VID_MenuDraw()
    {
    }

    // vid_win.c: VID_MenuKey: Escape goes back to the options (the other keys choose among the modes)
    private void VID_MenuKey(int key)
    {
        switch (key)
        {
            case UI.Keys.K_ESCAPE:
                S_LocalSound("misc/menu1.wav");
                M_Menu_Options_f();
                break;
        }
    }

    // vid_win.c's mode commands, which have nothing to do with one mode
    private static void VID_NoMode_f()
    {
    }

    // vid_win.c: VID_UpdateWindowStatus
    private void VID_UpdateWindowStatus()
    {
        window_rect = new Input.Rect(window_x, window_y, window_x + window_width, window_y + window_height);
        window_center_x = (window_rect.left + window_rect.right) / 2;
        window_center_y = (window_rect.top + window_rect.bottom) / 2;

        IN_UpdateClipCursor();
    }

    // vid_win.c: ClearAllStates
    // send an up event for each key, to make sure the server clears them all
    private void ClearAllStates()
    {
        int i;

        for (i = 0; i < 256; i++)
        {
            Key_Event(i, false);
        }

        Key_ClearStates();
        IN_ClearStates();
    }

    // vid_win.c: VID_SetWindowedMode, the vid fields
    private void VID_SetWindowedMode()
    {
        modestate = modestate_t.MS_WINDOWED;
        vid.numpages = 1;
        vid.maxwarpwidth = WARP_WIDTH;
        vid.maxwarpheight = WARP_HEIGHT;
        vid.height = vid.conheight = (uint)mode_height;
        vid.width = vid.conwidth = (uint)mode_width;
        vid.aspect = (float)((float)vid.height / vid.width * (320.0 / 240.0));
        vid_stretched = 0;
    }

    // vid_win.c: VID_SetMode (with VID_AllocBuffers)
    public bool VID_SetMode(int modenum, byte[] palette)
    {
        bool temp;
        int surfcachesize;

        _ = modenum;

        // so Con_Printfs don't mess us up by forcing vid and snd updates
        temp = scr_disabled_for_loading;
        scr_disabled_for_loading = true;

        CDAudio_Pause();
        S_ClearBuffer();

        // the windowed mode, with the mouse taken or released around VID_SetWindowedMode
        if (_windowed_mouse.value != 0)
        {
            VID_SetWindowedMode();
            IN_ActivateMouse();
            IN_HideMouse();
        }
        else
        {
            IN_DeactivateMouse();
            IN_ShowMouse();
            VID_SetWindowedMode();
        }

        window_width = (int)vid.width << vid_stretched;
        window_height = (int)vid.height << vid_stretched;
        VID_UpdateWindowStatus();

        CDAudio_Resume();
        scr_disabled_for_loading = temp;

        if (hide_window)
        {
            return true;
        }

        VID_SetPalette(palette);
        vid_modenum = MODE_WINDOWED;
        Cvar_SetValue("vid_mode", vid_modenum);

        if (d_pzbuffer != null)
        {
            D_FlushCaches();
        }

        vid.buffer = vid.conbuffer = new byte[mode_width * mode_height];
        vid.bufferoffset = 0;
        vid.rowbytes = (uint)mode_width;
        vid.conrowbytes = mode_width;
        d_pzbuffer = new short[mode_width * mode_height];
        surfcachesize = D_SurfaceCacheForRes(mode_width, mode_height);

        D_InitCaches(surfcachesize);

        ClearAllStates();

        if (!msg_suppress_1)
        {
            Con_SafePrintf("%dx%d\n", mode_width, mode_height);
        }

        VID_SetPalette(palette);
        vid.recalc_refdef = 1;
        return true;
    }

    // vid_win.c: VID_Init
    public void VID_Init(byte[] palette)
    {
        int i, t, bestmatch = 0, bestmatchmetric, dr, dg, db;

        Cvar_RegisterVariable(vid_mode);
        Cvar_RegisterVariable(vid_wait);
        Cvar_RegisterVariable(vid_nopageflip);
        Cvar_RegisterVariable(_vid_wait_override);
        Cvar_RegisterVariable(_vid_default_mode);
        Cvar_RegisterVariable(_vid_default_mode_win);
        Cvar_RegisterVariable(vid_config_x);
        Cvar_RegisterVariable(vid_config_y);
        Cvar_RegisterVariable(vid_stretch_by_2);
        Cvar_RegisterVariable(_windowed_mouse);
        Cvar_RegisterVariable(vid_fullscreen_mode);
        Cvar_RegisterVariable(vid_windowed_mode);
        Cvar_RegisterVariable(block_switch);
        Cvar_RegisterVariable(vid_window_x);
        Cvar_RegisterVariable(vid_window_y);

        Cmd_AddCommand("vid_testmode", VID_NoMode_f);
        Cmd_AddCommand("vid_nummodes", VID_NoMode_f);
        Cmd_AddCommand("vid_describecurrentmode", VID_NoMode_f);
        Cmd_AddCommand("vid_describemode", VID_NoMode_f);
        Cmd_AddCommand("vid_describemodes", VID_NoMode_f);
        Cmd_AddCommand("vid_forcemode", VID_NoMode_f);
        Cmd_AddCommand("vid_windowed", VID_NoMode_f);
        Cmd_AddCommand("vid_fullscreen", VID_NoMode_f);
        Cmd_AddCommand("vid_minimize", VID_NoMode_f);

        mode_width = host_parms.vidwidth;
        mode_height = host_parms.vidheight;
        if (mode_width < 320 || mode_height < 200 || mode_width > MAXWIDTH || mode_height > MAXHEIGHT)
        {
            throw Sys_Error("%dx%d is not between 320x200 and %dx%d", mode_width, mode_height, MAXWIDTH, MAXHEIGHT);
        }

        vid.maxwarpwidth = WARP_WIDTH;
        vid.maxwarpheight = WARP_HEIGHT;
        vid.colormap = host_colormap;
        vid.fullbright = 256 - BinaryPrimitives.ReadInt32LittleEndian(vid.colormap.AsSpan(2048 * 4));

        // GDI doesn't let us remap palette index 0, so we'll remap color mappings from that black to another one
        bestmatchmetric = 256 * 256 * 3;

        for (i = 1; i < 256; i++)
        {
            dr = palette[0] - palette[i * 3];
            dg = palette[1] - palette[(i * 3) + 1];
            db = palette[2] - palette[(i * 3) + 2];

            t = (dr * dr) + (dg * dg) + (db * db);

            if (t < bestmatchmetric)
            {
                bestmatchmetric = t;
                bestmatch = i;

                if (t == 0)
                {
                    break;
                }
            }
        }

        for (i = 0; i < 1 << (VID_CBITS + 8); i++)
        {
            if (vid.colormap[i] == 0)
            {
                vid.colormap[i] = (byte)bestmatch;
            }
        }

        // sound initialization has to go here, preceded by a windowed mode set
        hide_window = true;
        VID_SetMode(0, palette);
        hide_window = false;
        S_Init();

        VID_SetMode(0, palette);

        VID_SetPalette(palette);

        vid_menudrawfn = VID_MenuDraw;
        vid_menukeyfn = VID_MenuKey;
    }

    // vid_win.c: VID_Shutdown
    public static void VID_Shutdown()
    {
    }

    // vid_win.c: VID_Update: the platform shows the frame; the mouse taken or released when _windowed_mouse changes
    public void VID_Update(in VRect rects)
    {
        _ = rects;
        video?.Present(vid.buffer, (int)vid.width, (int)vid.height, (int)vid.rowbytes, vid_curpal);

        // handle the mouse state when windowed if that's changed
        if (modestate == modestate_t.MS_WINDOWED)
        {
            if ((int)_windowed_mouse.value != windowed_mouse)
            {
                if (_windowed_mouse.value != 0)
                {
                    IN_ActivateMouse();
                    IN_HideMouse();
                }
                else
                {
                    IN_DeactivateMouse();
                    IN_ShowMouse();
                }

                windowed_mouse = (int)_windowed_mouse.value;
            }
        }
    }

    // vid_win.c: VID_LockBuffer, VID_UnlockBuffer (DirectDraw's surface lock: nothing to lock)
    public static void VID_LockBuffer()
    {
    }

    public static void VID_UnlockBuffer()
    {
    }

    // vid_win.c: VID_HandlePause
    public void VID_HandlePause(bool pause)
    {
        if (modestate == modestate_t.MS_WINDOWED && _windowed_mouse.value != 0)
        {
            if (pause)
            {
                IN_DeactivateMouse();
                IN_ShowMouse();
            }
            else
            {
                IN_ActivateMouse();
                IN_HideMouse();
            }
        }
    }

    // vid_win.c: MainWndProc: the window's keys and mouse (the platform's other messages are its own)
    private readonly WindowProcedure windowProcedure;

    private sealed class WindowProcedure(QuakeEngine engine) : Platform.IWindowMessages
    {
        // WM_KEYDOWN, WM_KEYUP; WM_MOUSEWHEEL
        public void KeyEvent(int key, bool down) => engine.Key_Event(key, down);

        // WM_MOUSEMOVE: the cursor moved, the buttons' state to in_win.c
        public void MouseMove(int dx, int dy)
        {
            engine.input.Move(dx, dy);
            engine.IN_MouseEvent(engine.input.Buttons);
        }

        // WM_LBUTTONDOWN...
        public void MouseButtons(int state)
        {
            engine.input.Buttons = state;
            engine.IN_MouseEvent(state);
        }
    }

    // vid_win.c: ActiveApp, and AppActivate's static sound_active
    public bool ActiveApp = true;
    private bool sound_active;

    // vid_win.c: AppActivate, its part the game shows (the palette and the mouse are the platform's): the sound blocked
    // and its buffer cleared when the window loses the focus, unblocked and cleared when it gets it back. The platform
    // calls it from its events, as WM_ACTIVATE does; never in a trace or a recording, as the oracle never loses the
    // focus (design.md §7.1). minimize only counts in the full-screen DIB modes (vid_fulldib_on_focus_mode), never in
    // a window
    public void AppActivate(bool fActive, bool minimize)
    {
        _ = minimize;
        ActiveApp = fActive;

        // enable/disable sound on focus gain/loss
        if (!ActiveApp && sound_active)
        {
            S_BlockSound();
            S_ClearBuffer();
            sound_active = false;
        }
        else if (ActiveApp && !sound_active)
        {
            S_UnblockSound();
            S_ClearBuffer();
            sound_active = true;
        }
    }
}
