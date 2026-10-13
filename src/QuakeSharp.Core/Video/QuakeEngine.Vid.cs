using System.Buffers.Binary;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Render;
using QuakeSharp.Core.Video;
using static QuakeSharp.Core.UI.Keys;

namespace QuakeSharp.Core;

// vid_win.c: the video driver with the machine taken out, as the oracle's vid_oracle.c (design.md §6.4, decision 44).
// The game's side of vid_win.c is kept: the mode list, VID_SetMode and its windowed and full-screen modes, the
// commands, the Video menu, the first frame's switch to _vid_default_mode_win, the buffer's locking, the cvars, the
// remapping of the colormap's entries 0, S_Init between the hidden first mode set and the real one, the surface cache,
// ClearAllStates, AppActivate's part, and what it does with the mouse (in_win.c's, over VirtualInput's screen).
//
// The machine is the one Winquake.exe 1.09 was audited on (design.md §3.4): a 1920x1080 desktop, so the windowed
// default is stretched (mode 1) unless -noautostretch; the windowed modes 320x240 (QuakeParms' size), 640x480 and
// 800x600 (the last two stretched by vid_stretch_by_2); and DirectDraw (DDRAW8.DRV) with the 8-bit full-screen modes
// 320x240, 640x480, 800x600 and 1024x768, each of one page, so vid_win.c draws them in a memory DC (vid.numpages 1).
// The platform's IVideoOutput shows the frame: a window of the mode's window size, or the whole screen. The window's
// client area is at the screen's top left; it gets the focus when it is first shown. Not part of it: -dibonly and the
// full-screen DIB modes, the window's position (vid_window_x and vid_window_y stay cvars), minimizing, an 8-bit
// desktop's palette, and the loading disc (WinQuake draws it on the screen: never in the frame).
/// <summary>winquake.h: modestate_t</summary>
public enum modestate_t
{
    MS_WINDOWED,
    MS_FULLSCREEN,
    MS_FULLDIB,
    MS_UNINIT,
}

/// <summary>vid_win.c: vmode_t, a mode of the list</summary>
public sealed class VMode
{
    public modestate_t type;
    public int width;
    public int height;
    public int modenum;
    public int mode13;
    public int stretched;
    public int dib;
    public int fullscreen;
    public int bpp;
    public int halfscreen;
    public string modedesc = "";

    public VMode Clone() => (VMode)MemberwiseClone();
}

public sealed partial class QuakeEngine
{
    // vid.h: VID_CBITS, VID_GRADES
    public const int VID_CBITS = 6;
    public const int VID_GRADES = 1 << VID_CBITS;

    private const int MAX_MODE_LIST = 30;
    private const int VID_ROW_SIZE = 3;

    private const int MODE_WINDOWED = 0;
    private const int MODE_SETTABLE_WINDOW = 2;
    private const int NO_MODE = MODE_WINDOWED - 1;
    private const int MODE_FULLSCREEN_DEFAULT = MODE_WINDOWED + 3;

    // the machine: the desktop's width (GetDeviceCaps' HORZRES), DirectDraw's 8-bit modes (MGL's numbers, none VGA's
    // mode 0x13) and its driver's name
    private const int DesktopWidth = Input.VirtualInput.ScreenWidth;
    private static readonly (int Mode, int Width, int Height, int Bits)[] DirectDrawModes =
    [
        (1, 320, 240, 8),
        (2, 640, 480, 8),
        (3, 800, 600, 8),
        (4, 1024, 768, 8),
    ];

    private const string ModeDriverName = "DDRAW8.DRV";

    public bool dibonly;

    /// <summary>vid_win.c: vid, the global video state</summary>
    public readonly VidDef vid = new();

    // vid_win.c: the window's place and size on the screen, its rectangle and centre (in_win.c's cursor)
    public int window_center_x, window_center_y, window_x, window_y, window_width, window_height;
    public Input.Rect window_rect;

    // (vid_win.c's windowed_mode_set is never set, is_mode0x13 never true here, force_minimized only for the
    // full-screen DIB modes, vid_palettized and palette_changed only for FlipScreen's palette)
    private bool startwindowed;
    private int firstupdate = 1;
    private bool vid_initialized;
    private int lockcount;
    private int vid_fulldib_on_focus_mode;
    private bool in_mode_set, force_mode_set;
    private int vid_stretched;
    public int windowed_mouse;
    private bool vid_mode_set, hide_window;

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

    public int vid_modenum = NO_MODE;
    public int vid_testingmode, vid_realmode;
    public double vid_testendtime;
    public int vid_default = MODE_WINDOWED;
    private int windowed_default;

    /// <summary>winquake.h: modestate</summary>
    public modestate_t modestate = modestate_t.MS_UNINIT;

    private int vid_surfcachesize;

    /// <summary>vid_win.c: the palette last set (the frame's colours)</summary>
    public readonly byte[] vid_curpal = new byte[256 * 3];

    /// <summary>vid_win.c: vid_menudrawfn, vid_menukeyfn (menu.c's video menu calls them)</summary>
    public Action? vid_menudrawfn;

    public Action<int>? vid_menukeyfn;

    // winquake.h: DDActive, ActiveApp, Minimized
    public bool DDActive;
    public bool ActiveApp = true;
    public bool Minimized;

    private readonly VMode[] modelist = [.. Enumerable.Range(0, MAX_MODE_LIST).Select(_ => new VMode())];
    private int nummodes;
    private readonly VMode badmode = new();

    // the windowed modes' DIB and the full-screen modes' memory DC (MGL_createMemoryDC: rows of 4-byte multiples),
    // whichever the mode draws in, and its row length
    private byte[]? dibdc, memdc;
    private int dc_rowbytes;

    // whether the window has been shown: Windows activates it then
    private bool window_shown;

    // vid_win.c: backingbuf, what the loading disc covers (D_BeginDirectRect)
    private readonly byte[] backingbuf = new byte[48 * 24];

    /// <summary>
    /// The frame, locked or not (the traces' V line, -traceshot and the platform's): the DIB or the full-screen memory
    /// DC, and its row length.
    /// </summary>
    public (byte[] Frame, int RowBytes) VID_Frame() => (dibdc ?? memdc ?? [], dc_rowbytes);

    private byte[] MemoryDC(int width, int height)
    {
        dc_rowbytes = (width + 3) & ~3;
        return new byte[dc_rowbytes * height];
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

    // vid_win.c: VID_CheckAdequateMem
    private bool VID_CheckAdequateMem(int width, int height)
    {
        int tbuffersize;

        tbuffersize = width * height * sizeof(short);

        tbuffersize += D_SurfaceCacheForRes(width, height);

        // see if there's enough memory, allowing for the normal mode 0x13 pixel,
        // z, and surface buffers
        if (host_parms.memsize - tbuffersize + SURFCACHE_SIZE_AT_320X200 + (0x10000 * 3) < minimum_memory)
        {
            return false; // not enough memory for mode
        }

        return true;
    }

    // vid_win.c: VID_AllocBuffers (the z-buffer and the surface cache in arrays of their own rather than on the hunk)
    private bool VID_AllocBuffers(int width, int height)
    {
        int tsize, tbuffersize;

        tbuffersize = width * height * sizeof(short);

        tsize = D_SurfaceCacheForRes(width, height);

        tbuffersize += tsize;

        // see if there's enough memory, allowing for the normal mode 0x13 pixel,
        // z, and surface buffers
        if (host_parms.memsize - tbuffersize + SURFCACHE_SIZE_AT_320X200 + (0x10000 * 3) < minimum_memory)
        {
            Con_SafePrintf("Not enough memory for video mode\n");
            return false; // not enough memory for mode
        }

        vid_surfcachesize = tsize;

        if (d_pzbuffer != null)
        {
            D_FlushCaches();
            d_pzbuffer = null;
        }

        d_pzbuffer = new short[width * height];

        return true;
    }

    // vid_win.c: VID_InitMGLDIB: the windowed modes (mode 0 of QuakeParms' size)
    private void VID_InitMGLDIB()
    {
        modelist[0].type = modestate_t.MS_WINDOWED;
        modelist[0].width = host_parms.vidwidth;
        modelist[0].height = host_parms.vidheight;
        modelist[0].modedesc = CFormat.Sprintf("%dx%d", host_parms.vidwidth, host_parms.vidheight);
        modelist[0].mode13 = 0;
        modelist[0].modenum = MODE_WINDOWED;
        modelist[0].stretched = 0;
        modelist[0].dib = 1;
        modelist[0].fullscreen = 0;
        modelist[0].halfscreen = 0;
        modelist[0].bpp = 8;

        modelist[1].type = modestate_t.MS_WINDOWED;
        modelist[1].width = 640;
        modelist[1].height = 480;
        modelist[1].modedesc = "640x480";
        modelist[1].mode13 = 0;
        modelist[1].modenum = MODE_WINDOWED + 1;
        modelist[1].stretched = 1;
        modelist[1].dib = 1;
        modelist[1].fullscreen = 0;
        modelist[1].halfscreen = 0;
        modelist[1].bpp = 8;

        modelist[2].type = modestate_t.MS_WINDOWED;
        modelist[2].width = 800;
        modelist[2].height = 600;
        modelist[2].modedesc = "800x600";
        modelist[2].mode13 = 0;
        modelist[2].modenum = MODE_WINDOWED + 2;
        modelist[2].stretched = 1;
        modelist[2].dib = 1;
        modelist[2].fullscreen = 0;
        modelist[2].halfscreen = 0;
        modelist[2].bpp = 8;

        // automatically stretch the default mode up if > 640x480 desktop resolution
        if (DesktopWidth > 640 && COM_CheckParm("-noautostretch") == 0)
        {
            vid_default = MODE_WINDOWED + 1;
        }
        else
        {
            vid_default = MODE_WINDOWED;
        }

        windowed_default = vid_default;

        nummodes = 3; // reserve space for windowed mode

        DDActive = false;
    }

    // vid_win.c: VID_InitMGLFull: the full-screen modes DirectDraw offers
    private void VID_InitMGLFull()
    {
        int i, xRes, yRes, bits, lowres, curmode, mode;
        int lowstretchedres, stretchedmode, lowstretched;

        lowres = lowstretchedres = 99999;
        lowstretched = 0;
        curmode = 0;
        mode = 0;
        stretchedmode = 0;

        // find the lowest-res mode, or a mode we can stretch up to and get
        // lowest-res that way
        for (i = 0; i < DirectDrawModes.Length; i++)
        {
            (_, xRes, yRes, bits) = DirectDrawModes[i];

            if (bits == 8 && xRes <= MAXWIDTH && yRes <= MAXHEIGHT && curmode < MAX_MODE_LIST)
            {
                if (xRes < lowres)
                {
                    lowres = xRes;
                    mode = i;
                }

                if (xRes < lowstretchedres && (xRes >> 1) >= 320)
                {
                    lowstretchedres = xRes >> 1;
                    stretchedmode = i;
                }
            }

            curmode++;
        }

        // if there's a mode we can stretch by 2 up to, thereby effectively getting
        // a lower-res mode than the lowest-res real but still at least 320x200, that
        // will be our default mode
        if (lowstretchedres < lowres)
        {
            mode = stretchedmode;
            lowres = lowstretchedres;
            lowstretched = 1;
        }

        // build the mode list, leaving room for the low-res stretched mode, if any
        nummodes++; // leave room for default mode

        for (i = 0; i < DirectDrawModes.Length; i++)
        {
            (_, xRes, yRes, bits) = DirectDrawModes[i];

            if (bits == 8 && xRes <= MAXWIDTH && yRes <= MAXHEIGHT && nummodes < MAX_MODE_LIST)
            {
                if (i == mode)
                {
                    if (lowstretched != 0)
                    {
                        stretchedmode = nummodes;
                        curmode = nummodes++;
                    }
                    else
                    {
                        curmode = MODE_FULLSCREEN_DEFAULT;
                    }
                }
                else
                {
                    curmode = nummodes++;
                }

                modelist[curmode].type = modestate_t.MS_FULLSCREEN;
                modelist[curmode].width = xRes;
                modelist[curmode].height = yRes;
                modelist[curmode].modedesc = CFormat.Sprintf("%dx%d", xRes, yRes);
                modelist[curmode].mode13 = 0;
                modelist[curmode].modenum = DirectDrawModes[i].Mode;
                modelist[curmode].stretched = 0;
                modelist[curmode].dib = 0;
                modelist[curmode].fullscreen = 1;
                modelist[curmode].halfscreen = 0;
                modelist[curmode].bpp = 8;
            }
        }

        if (lowstretched != 0)
        {
            modelist[MODE_FULLSCREEN_DEFAULT] = modelist[stretchedmode].Clone();
            modelist[MODE_FULLSCREEN_DEFAULT].stretched = 1;
            modelist[MODE_FULLSCREEN_DEFAULT].width >>= 1;
            modelist[MODE_FULLSCREEN_DEFAULT].height >>= 1;
            modelist[MODE_FULLSCREEN_DEFAULT].modedesc = CFormat.Sprintf("%dx%d",
                modelist[MODE_FULLSCREEN_DEFAULT].width, modelist[MODE_FULLSCREEN_DEFAULT].height);
        }

        vid_default = MODE_FULLSCREEN_DEFAULT;
    }

    // vid_win.c: VID_NumModes
    private int VID_NumModes() => nummodes;

    // vid_win.c: VID_GetModePtr
    private VMode VID_GetModePtr(int modenum) =>
        modenum >= 0 && modenum < nummodes ? modelist[modenum] : badmode;

    // vid_win.c: VID_CheckModedescFixup (the cvars' values raised, not their strings)
    private void VID_CheckModedescFixup(int mode)
    {
        int x, y, stretch;

        if (mode == MODE_SETTABLE_WINDOW)
        {
            modelist[mode].stretched = (int)vid_stretch_by_2.value;
            stretch = modelist[mode].stretched;

            if (vid_config_x.value < (320 << stretch))
            {
                vid_config_x.value = 320 << stretch;
            }

            if (vid_config_y.value < (200 << stretch))
            {
                vid_config_y.value = 200 << stretch;
            }

            x = (int)vid_config_x.value;
            y = (int)vid_config_y.value;
            modelist[mode].modedesc = CFormat.Sprintf("%dx%d", x, y);
            modelist[mode].width = x;
            modelist[mode].height = y;
        }
    }

    // vid_win.c: VID_GetModeDescriptionMemCheck
    private string? VID_GetModeDescriptionMemCheck(int mode)
    {
        string pinfo;
        VMode pv;

        if (mode < 0 || mode >= nummodes)
        {
            return null;
        }

        VID_CheckModedescFixup(mode);

        pv = VID_GetModePtr(mode);
        pinfo = pv.modedesc;

        if (VID_CheckAdequateMem(pv.width, pv.height))
        {
            return pinfo;
        }
        else
        {
            return null;
        }
    }

    // vid_win.c: VID_GetModeDescription
    private string? VID_GetModeDescription(int mode)
    {
        if (mode < 0 || mode >= nummodes)
        {
            return null;
        }

        VID_CheckModedescFixup(mode);

        return VID_GetModePtr(mode).modedesc;
    }

    // vid_win.c: VID_GetModeDescription2
    // Tacks on "windowed" or "fullscreen"
    private string? VID_GetModeDescription2(int mode)
    {
        VMode pv;

        if (mode < 0 || mode >= nummodes)
        {
            return null;
        }

        VID_CheckModedescFixup(mode);

        pv = VID_GetModePtr(mode);

        if (modelist[mode].type == modestate_t.MS_FULLSCREEN)
        {
            return CFormat.Sprintf("%s fullscreen", pv.modedesc);
        }
        else if (modelist[mode].type == modestate_t.MS_FULLDIB)
        {
            return CFormat.Sprintf("%s fullscreen", pv.modedesc);
        }
        else
        {
            return CFormat.Sprintf("%s windowed", pv.modedesc);
        }
    }

    // vid_win.c: VID_GetExtModeDescription
    // KJB: Added this to return the mode driver name in description for console
    private string? VID_GetExtModeDescription(int mode)
    {
        VMode pv;

        if (mode < 0 || mode >= nummodes)
        {
            return null;
        }

        VID_CheckModedescFixup(mode);

        pv = VID_GetModePtr(mode);
        if (modelist[mode].type == modestate_t.MS_FULLSCREEN)
        {
            return CFormat.Sprintf("%s fullscreen %s", pv.modedesc, ModeDriverName);
        }
        else if (modelist[mode].type == modestate_t.MS_FULLDIB)
        {
            return CFormat.Sprintf("%s fullscreen DIB", pv.modedesc);
        }
        else
        {
            return CFormat.Sprintf("%s windowed", pv.modedesc);
        }
    }

    // vid_win.c: DestroyDIBWindow, DestroyFullscreenWindow: the DCs
    private void DestroyDIBWindow()
    {
        if (modestate == modestate_t.MS_WINDOWED)
        {
            // destroy the associated MGL DC's; the window gets reused
            dibdc = null;
        }
    }

    private void DestroyFullscreenWindow()
    {
        if (modestate == modestate_t.MS_FULLSCREEN)
        {
            // destroy the existing fullscreen mode and DC's
            memdc = null;
        }
    }

    // vid_win.c: VID_SetWindowedMode. The window is shown the first time a mode is set in it, and Windows then
    // activates it (WM_ACTIVATE)
    private bool VID_SetWindowedMode(int modenum)
    {
        bool stretched;
        int DIBWidth, DIBHeight;

        // (if (!windowed_mode_set): the C never sets it)
        if (COM_CheckParm("-resetwinpos") != 0)
        {
            Cvar_SetValue("vid_window_x", 0.0f);
            Cvar_SetValue("vid_window_y", 0.0f);
        }

        VID_CheckModedescFixup(modenum);

        DDActive = false;

        DestroyFullscreenWindow();

        dibdc = null;

        stretched = modelist[modenum].stretched != 0;

        DIBWidth = modelist[modenum].width;
        DIBHeight = modelist[modenum].height;

        if (stretched)
        {
            DIBWidth >>= 1;
            DIBHeight >>= 1;
        }

        // the first time we're called to set the mode, create the window we'll use
        // for the rest of the session
        if (!vid_mode_set)
        {
            vid_mode_set = true;
        }

        if (hide_window)
        {
            return true;
        }

        // position and show the DIB window: the first time, Windows activates it
        if (!window_shown)
        {
            window_shown = true;
            WM_ACTIVATE(true, false);
        }

        modestate = modestate_t.MS_WINDOWED;
        vid_fulldib_on_focus_mode = 0;

        // Create the MGL window DC and the MGL memory DC
        dibdc = MemoryDC(DIBWidth, DIBHeight);

        vid.buffer = vid.conbuffer = dibdc;
        vid.bufferoffset = 0;
        vid.rowbytes = (uint)dc_rowbytes;
        vid.conrowbytes = dc_rowbytes;
        vid.numpages = 1;
        vid.maxwarpwidth = WARP_WIDTH;
        vid.maxwarpheight = WARP_HEIGHT;
        vid.height = vid.conheight = (uint)DIBHeight;
        vid.width = vid.conwidth = (uint)DIBWidth;
        vid.aspect = (float)((float)vid.height / vid.width * (320.0 / 240.0));

        vid_stretched = stretched ? 1 : 0;

        return true;
    }

    // vid_win.c: VID_SetFullscreenMode (createDisplayDC's memory DC: the display has one page)
    private bool VID_SetFullscreenMode(int modenum)
    {
        DDActive = true;

        DestroyDIBWindow();

        // Destroy old DC's, resetting back to fullscreen mode
        memdc = MemoryDC(modelist[modenum].width, modelist[modenum].height);
        vid.numpages = 1;

        modestate = modestate_t.MS_FULLSCREEN;
        vid_fulldib_on_focus_mode = 0;

        // vid.buffer and vid.rowbytes wait for VID_LockBuffer
        vid.maxwarpwidth = WARP_WIDTH;
        vid.maxwarpheight = WARP_HEIGHT;
        vid.height = vid.conheight = (uint)modelist[modenum].height;
        vid.width = vid.conwidth = (uint)modelist[modenum].width;
        vid.aspect = (float)((float)vid.height / vid.width * (320.0 / 240.0));

        vid_stretched = modelist[modenum].stretched;

        // needed because we're not getting WM_MOVE messages fullscreen on NT
        window_x = 0;
        window_y = 0;

        // shouldn't be needed, but Kendall needs to let us get the activation
        // message for this not to be needed on NT
        AppActivate(true, false);

        return true;
    }

    // vid_win.c: VID_RestoreOldMode
    private bool restoreOldModeInError;

    private void VID_RestoreOldMode(int original_mode)
    {
        if (restoreOldModeInError)
        {
            return;
        }

        in_mode_set = false;
        restoreOldModeInError = true;

        // make sure mode set happens (video mode changes)
        vid_modenum = original_mode - 1;

        if (!VID_SetMode(original_mode, vid_curpal))
        {
            vid_modenum = MODE_WINDOWED - 1;

            if (!VID_SetMode(windowed_default, vid_curpal))
            {
                throw Sys_Error("Can't set any video mode");
            }
        }

        restoreOldModeInError = false;
    }

    // vid_win.c: VID_SetDefaultMode
    public void VID_SetDefaultMode()
    {
        if (vid_initialized)
        {
            VID_SetMode(0, vid_curpal);
        }

        IN_DeactivateMouse();
    }

    // vid_win.c: VID_SetMode
    public bool VID_SetMode(int modenum, byte[] palette)
    {
        int original_mode;
        bool temp, stat;

        while (modenum >= nummodes || modenum < 0)
        {
            if (vid_modenum == NO_MODE)
            {
                if (modenum == vid_default)
                {
                    modenum = windowed_default;
                }
                else
                {
                    modenum = vid_default;
                }

                Cvar_SetValue("vid_mode", modenum);
            }
            else
            {
                Cvar_SetValue("vid_mode", vid_modenum);
                return false;
            }
        }

        if (!force_mode_set && modenum == vid_modenum)
        {
            return true;
        }

        // so Con_Printfs don't mess us up by forcing vid and snd updates
        temp = scr_disabled_for_loading;
        scr_disabled_for_loading = true;
        in_mode_set = true;

        CDAudio_Pause();
        S_ClearBuffer();

        if (vid_modenum == NO_MODE)
        {
            original_mode = windowed_default;
        }
        else
        {
            original_mode = vid_modenum;
        }

        // Set either the fullscreen or windowed mode
        if (modelist[modenum].type == modestate_t.MS_WINDOWED)
        {
            if (_windowed_mouse.value != 0)
            {
                stat = VID_SetWindowedMode(modenum);
                IN_ActivateMouse();
                IN_HideMouse();
            }
            else
            {
                IN_DeactivateMouse();
                IN_ShowMouse();
                stat = VID_SetWindowedMode(modenum);
            }
        }
        else
        {
            stat = VID_SetFullscreenMode(modenum);
            IN_ActivateMouse();
            IN_HideMouse();
        }

        window_width = (int)vid.width << vid_stretched;
        window_height = (int)vid.height << vid_stretched;
        VID_UpdateWindowStatus();

        CDAudio_Resume();
        scr_disabled_for_loading = temp;

        if (!stat)
        {
            VID_RestoreOldMode(original_mode);
            return false;
        }

        if (hide_window)
        {
            return true;
        }

        // the platform's window follows the mode
        video?.SetMode(window_width, window_height, modestate != modestate_t.MS_WINDOWED);

        VID_SetPalette(palette);

        vid_modenum = modenum;
        Cvar_SetValue("vid_mode", vid_modenum);

        if (!VID_AllocBuffers((int)vid.width, (int)vid.height))
        {
            // couldn't get memory for this mode; try to fall back to previous mode
            VID_RestoreOldMode(original_mode);
            return false;
        }

        D_InitCaches(vid_surfcachesize);

        // fix the leftover Alt from any Alt-Tab or the like that switched us away
        ClearAllStates();

        if (!msg_suppress_1)
        {
            Con_SafePrintf("%s\n", VID_GetModeDescription(vid_modenum));
        }

        VID_SetPalette(palette);

        in_mode_set = false;
        vid.recalc_refdef = 1;

        return true;
    }

    // vid_win.c: VID_LockBuffer, VID_UnlockBuffer. In the full-screen modes the frame is only at hand while locked:
    // locking sets vid.buffer and vid.rowbytes (unlocking leaves them; the C's NULL only catches unlocked accesses)
    public void VID_LockBuffer()
    {
        if (dibdc != null)
        {
            return;
        }

        lockcount++;

        if (lockcount > 1)
        {
            return;
        }

        if (memdc != null)
        {
            // Update surface pointer for linear access modes
            vid.buffer = vid.conbuffer = memdc;
            vid.bufferoffset = 0;
            vid.rowbytes = (uint)dc_rowbytes;
            vid.conrowbytes = dc_rowbytes;
        }

        if (r_dowarp)
        {
            d_viewbuffer = r_warpbuffer;
            d_viewbufferoffset = 0;
        }
        else
        {
            d_viewbuffer = vid.buffer;
            d_viewbufferoffset = vid.bufferoffset;
        }

        if (r_dowarp)
        {
            screenwidth = WARP_WIDTH;
        }
        else
        {
            screenwidth = (int)vid.rowbytes;
        }

        if (lcd_x.value != 0)
        {
            screenwidth <<= 1;
        }
    }

    public void VID_UnlockBuffer()
    {
        if (dibdc != null)
        {
            return;
        }

        lockcount--;

        if (lockcount > 0)
        {
            return;
        }

        if (lockcount < 0)
        {
            throw Sys_Error("Unbalanced unlock");
        }
    }

    // vid_win.c: VID_ForceUnlockedAndReturnState
    public int VID_ForceUnlockedAndReturnState()
    {
        int lk;

        if (lockcount == 0)
        {
            return 0;
        }

        lk = lockcount;

        if (dibdc != null)
        {
            lockcount = 0;
        }
        else
        {
            lockcount = 1;
            VID_UnlockBuffer();
        }

        return lk;
    }

    // vid_win.c: VID_ForceLockState
    public void VID_ForceLockState(int lk)
    {
        if (dibdc == null && lk != 0)
        {
            lockcount = 0;
            VID_LockBuffer();
        }

        lockcount = lk;
    }

    // vid_win.c: VID_SetPalette (the desktop is not palettized; the platform shows the palette with every frame)
    public void VID_SetPalette(ReadOnlySpan<byte> palette) => palette[..vid_curpal.Length].CopyTo(vid_curpal);

    // vid_win.c: VID_ShiftPalette
    public void VID_ShiftPalette(ReadOnlySpan<byte> palette) => VID_SetPalette(palette);

    // vid_win.c: VID_DescribeCurrentMode_f
    private void VID_DescribeCurrentMode_f() => Con_Printf("%s\n", VID_GetExtModeDescription(vid_modenum));

    // vid_win.c: VID_NumModes_f
    private void VID_NumModes_f()
    {
        if (nummodes == 1)
        {
            Con_Printf("%d video mode is available\n", nummodes);
        }
        else
        {
            Con_Printf("%d video modes are available\n", nummodes);
        }
    }

    // vid_win.c: VID_DescribeMode_f
    private void VID_DescribeMode_f()
    {
        int modenum;

        modenum = QString.Q_atoi(Cmd_Argv(1));

        Con_Printf("%s\n", VID_GetExtModeDescription(modenum));
    }

    // vid_win.c: VID_DescribeModes_f
    private void VID_DescribeModes_f()
    {
        int i, lnummodes;
        string? pinfo;
        bool na;
        VMode pv;

        na = false;

        lnummodes = VID_NumModes();

        for (i = 0; i < lnummodes; i++)
        {
            pv = VID_GetModePtr(i);
            pinfo = VID_GetExtModeDescription(i);

            if (VID_CheckAdequateMem(pv.width, pv.height))
            {
                Con_Printf("%2d: %s\n", i, pinfo);
            }
            else
            {
                Con_Printf("**: %s\n", pinfo);
                na = true;
            }
        }

        if (na)
        {
            Con_Printf("\n[**: not enough system RAM for mode]\n");
        }
    }

    // vid_win.c: VID_TestMode_f
    private void VID_TestMode_f()
    {
        int modenum;
        double testduration;

        if (vid_testingmode == 0)
        {
            modenum = QString.Q_atoi(Cmd_Argv(1));

            if (VID_SetMode(modenum, vid_curpal))
            {
                vid_testingmode = 1;
                testduration = QString.Q_atof(Cmd_Argv(2));
                if (testduration == 0)
                {
                    testduration = 5.0;
                }

                vid_testendtime = realtime + testduration;
            }
        }
    }

    // vid_win.c: VID_Windowed_f
    private void VID_Windowed_f() => VID_SetMode((int)vid_windowed_mode.value, vid_curpal);

    // vid_win.c: VID_Fullscreen_f
    private void VID_Fullscreen_f() => VID_SetMode((int)vid_fullscreen_mode.value, vid_curpal);

    // vid_win.c: VID_Minimize_f (minimizing is not part of the machine: the window stays)
    private static void VID_Minimize_f()
    {
    }

    // vid_win.c: VID_ForceMode_f
    private void VID_ForceMode_f()
    {
        int modenum;

        if (vid_testingmode == 0)
        {
            modenum = QString.Q_atoi(Cmd_Argv(1));

            force_mode_set = true;
            VID_SetMode(modenum, vid_curpal);
            force_mode_set = false;
        }
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

        Cmd_AddCommand("vid_testmode", VID_TestMode_f);
        Cmd_AddCommand("vid_nummodes", VID_NumModes_f);
        Cmd_AddCommand("vid_describecurrentmode", VID_DescribeCurrentMode_f);
        Cmd_AddCommand("vid_describemode", VID_DescribeMode_f);
        Cmd_AddCommand("vid_describemodes", VID_DescribeModes_f);
        Cmd_AddCommand("vid_forcemode", VID_ForceMode_f);
        Cmd_AddCommand("vid_windowed", VID_Windowed_f);
        Cmd_AddCommand("vid_fullscreen", VID_Fullscreen_f);
        Cmd_AddCommand("vid_minimize", VID_Minimize_f);

        if (host_parms.vidwidth < 320 || host_parms.vidheight < 200 || host_parms.vidwidth > MAXWIDTH || host_parms.vidheight > MAXHEIGHT)
        {
            throw Sys_Error("%dx%d is not between 320x200 and %dx%d", host_parms.vidwidth, host_parms.vidheight, MAXWIDTH, MAXHEIGHT);
        }

        if (COM_CheckParm("-dibonly") != 0)
        {
            dibonly = true;
        }

        VID_InitMGLDIB();

        if (!dibonly)
        {
            VID_InitMGLFull();
        }

        vid.maxwarpwidth = WARP_WIDTH;
        vid.maxwarpheight = WARP_HEIGHT;
        vid.colormap = host_colormap;
        vid.fullbright = 256 - BinaryPrimitives.ReadInt32LittleEndian(vid.colormap.AsSpan(2048 * 4));
        vid_testingmode = 0;

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

        if (COM_CheckParm("-startwindowed") != 0)
        {
            startwindowed = true;
            vid_default = windowed_default;
        }

        // sound initialization has to go here, preceded by a windowed mode set,
        // so there's a window for DirectSound to work with but we're not yet
        // fullscreen so the "hardware already in use" dialog is visible if it
        // gets displayed

        // keep the window minimized until we're ready for the first real mode set
        hide_window = true;
        VID_SetMode(MODE_WINDOWED, palette);
        hide_window = false;
        S_Init();

        vid_initialized = true;

        force_mode_set = true;
        VID_SetMode(vid_default, palette);
        force_mode_set = false;

        vid_realmode = vid_modenum;

        VID_SetPalette(palette);

        vid_menudrawfn = VID_MenuDraw;
        vid_menukeyfn = VID_MenuKey;

        badmode.modedesc = "Bad mode";
    }

    // vid_win.c: VID_Shutdown
    public static void VID_Shutdown()
    {
    }

    // vid_win.c: VID_Update: the platform shows the frame (FlipScreen); the first frame's switch to the default mode, a
    // tested mode's end, a mode vid_mode asks for, and the mouse taken or released when _windowed_mouse changes
    public void VID_Update(in VRect rects)
    {
        _ = rects;

        if (firstupdate != 0)
        {
            if (_vid_default_mode_win.value != vid_default
                && (!startwindowed || _vid_default_mode_win.value < MODE_FULLSCREEN_DEFAULT))
            {
                firstupdate = 0;

                if (COM_CheckParm("-resetwinpos") != 0)
                {
                    Cvar_SetValue("vid_window_x", 0.0f);
                    Cvar_SetValue("vid_window_y", 0.0f);
                }

                if (_vid_default_mode_win.value < 0 || _vid_default_mode_win.value >= nummodes)
                {
                    Cvar_SetValue("_vid_default_mode_win", windowed_default);
                }

                Cvar_SetValue("vid_mode", _vid_default_mode_win.value);
            }
        }

        // We've drawn the frame; copy it to the screen
        (byte[] frame, int rowbytes) = VID_Frame();
        video?.Present(frame, (int)vid.width, (int)vid.height, rowbytes, vid_curpal);

        if (vid_testingmode != 0)
        {
            if (realtime >= vid_testendtime)
            {
                VID_SetMode(vid_realmode, vid_curpal);
                vid_testingmode = 0;
            }
        }
        else
        {
            if ((int)vid_mode.value != vid_realmode)
            {
                VID_SetMode((int)vid_mode.value, vid_curpal);
                Cvar_SetValue("vid_mode", vid_modenum);
                // so if mode set fails, we don't keep on
                //  trying to set that mode
                vid_realmode = vid_modenum;
            }
        }

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

    // vid_win.c: D_BeginDirectRect. The loading disc drawn into the screen's buffer around a file's read, what was
    // under it kept, and the rectangle shown (FlipScreen: the platform's); every mode has one page
    public void D_BeginDirectRect(int x, int y, byte[]? pbitmap, int width, int height)
    {
        int i, j, reps, repshift;

        if (!vid_initialized)
        {
            return;
        }

        if (vid.aspect > 1.5)
        {
            reps = 2;
            repshift = 1;
        }
        else
        {
            reps = 1;
            repshift = 0;
        }

        VID_LockBuffer();

        (byte[] direct, _) = VID_Frame();
        if (direct.Length == 0)
        {
            throw Sys_Error("NULL vid.direct pointer");
        }

        for (i = 0; i < (height << repshift); i += reps)
        {
            for (j = 0; j < reps; j++)
            {
                int row = x + (((y << repshift) + i + j) * (int)vid.rowbytes);
                direct.AsSpan(row, width).CopyTo(backingbuf.AsSpan((i + j) * 24, width));
                pbitmap.AsSpan((i >> repshift) * width, width).CopyTo(direct.AsSpan(row, width));
            }
        }

        VID_UnlockBuffer();

        video?.PresentRect(direct, (int)vid.rowbytes, x, y, width, height << repshift);
    }

    // vid_win.c: D_EndDirectRect: what was under the disc put back, and shown
    public void D_EndDirectRect(int x, int y, int width, int height)
    {
        int i, j, reps, repshift;

        if (!vid_initialized)
        {
            return;
        }

        if (vid.aspect > 1.5)
        {
            reps = 2;
            repshift = 1;
        }
        else
        {
            reps = 1;
            repshift = 0;
        }

        VID_LockBuffer();

        (byte[] direct, _) = VID_Frame();
        if (direct.Length == 0)
        {
            throw Sys_Error("NULL vid.direct pointer");
        }

        for (i = 0; i < (height << repshift); i += reps)
        {
            for (j = 0; j < reps; j++)
            {
                int row = x + (((y << repshift) + i + j) * (int)vid.rowbytes);
                backingbuf.AsSpan((i + j) * 24, width).CopyTo(direct.AsSpan(row, width));
            }
        }

        VID_UnlockBuffer();

        video?.PresentRect(direct, (int)vid.rowbytes, x, y, width, height << repshift);
    }

    // vid_win.c: AppActivate (the desktop is not palettized; the window is minimized only in the full-screen DIB modes,
    // which the machine has not)
    private bool sound_active;

    public void AppActivate(bool fActive, bool minimize)
    {
        ActiveApp = fActive;

        // messy, but it seems to work
        if (vid_fulldib_on_focus_mode != 0)
        {
            Minimized = minimize;

            if (Minimized)
            {
                ActiveApp = false;
            }
        }

        if (vid_initialized)
        {
            if (!Minimized)
            {
                VID_SetPalette(vid_curpal);
            }

            scr_fullupdate = 0;
        }

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

        // minimize/restore fulldib windows/mouse-capture normal windows on demand
        if (!in_mode_set)
        {
            if (ActiveApp)
            {
                if (modestate == modestate_t.MS_WINDOWED && _windowed_mouse.value != 0)
                {
                    IN_ActivateMouse();
                    IN_HideMouse();
                }
            }

            if (!ActiveApp)
            {
                if (modestate == modestate_t.MS_WINDOWED && _windowed_mouse.value != 0)
                {
                    IN_DeactivateMouse();
                    IN_ShowMouse();
                }
            }
        }
    }

    /// <summary>
    /// vid_win.c: MainWndProc's WM_ACTIVATE: the window got the focus or lost it. The platform calls it from its events;
    /// never in a trace or a recording, where the window keeps the focus it got when it was first shown (design.md §7.1).
    /// </summary>
    public void WM_ACTIVATE(bool fActive, bool fMinimized)
    {
        AppActivate(fActive, fMinimized);

        // fix the leftover Alt from any Alt-Tab or the like that switched us away
        ClearAllStates();

        if (!in_mode_set)
        {
            VID_SetPalette(vid_curpal);
        }
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

    // vid_win.c: MainWndProc: the window's keys and mouse (the platform's other messages are its own); nothing reaches
    // the game while a mode is being set
    private readonly WindowProcedure windowProcedure;

    private sealed class WindowProcedure(QuakeEngine engine) : Platform.IWindowMessages
    {
        // WM_KEYDOWN, WM_KEYUP; WM_MOUSEWHEEL
        public void KeyEvent(int key, bool down)
        {
            if (!engine.in_mode_set)
            {
                engine.Key_Event(key, down);
            }
        }

        // WM_MOUSEMOVE: the cursor moved, the buttons' state to in_win.c
        public void MouseMove(int dx, int dy)
        {
            engine.input.Move(dx, dy);
            if (!engine.in_mode_set)
            {
                engine.IN_MouseEvent(engine.input.Buttons);
            }
        }

        // WM_LBUTTONDOWN...
        public void MouseButtons(int state)
        {
            engine.input.Buttons = state;
            if (!engine.in_mode_set)
            {
                engine.IN_MouseEvent(state);
            }
        }
    }

    // vid_win.c: the Video menu

    private int vid_line, vid_wmodes;

    /// <summary>vid_win.c: modedesc_t</summary>
    private struct ModeDesc
    {
        public int modenum;
        public string? desc;
        public int iscur;
        public int ismode13;
        public int width;
    }

    private const int MAX_COLUMN_SIZE = 5;
    private const int MODE_AREA_HEIGHT = MAX_COLUMN_SIZE + 6;
    private const int MAX_MODEDESCS = MAX_COLUMN_SIZE * 3;

    private readonly ModeDesc[] modedescs = new ModeDesc[MAX_MODEDESCS];

    // vid_win.c: VID_MenuDraw
    private void VID_MenuDraw()
    {
        QPic p;
        string? ptr;
        int lnummodes, i, j, k, column, row, dupmode = 0;
        bool dup;
        string temp;
        VMode pv;
        ModeDesc tmodedesc;

        p = Draw_CachePic("gfx/vidmodes.lmp");
        M_DrawPic((320 - p.width) / 2, 4, p);

        for (i = 0; i < 3; i++)
        {
            ptr = VID_GetModeDescriptionMemCheck(i);
            modedescs[i].modenum = modelist[i].modenum;
            modedescs[i].desc = ptr;
            modedescs[i].ismode13 = 0;
            modedescs[i].iscur = 0;

            if (vid_modenum == i)
            {
                modedescs[i].iscur = 1;
            }
        }

        vid_wmodes = 3;
        lnummodes = VID_NumModes();

        for (i = 3; i < lnummodes; i++)
        {
            ptr = VID_GetModeDescriptionMemCheck(i);
            pv = VID_GetModePtr(i);

            // we only have room for 15 fullscreen modes, so don't allow
            // 360-wide modes, because if there are 5 320-wide modes and
            // 5 360-wide modes, we'll run out of space
            if (ptr != null && (pv.width != 360 || COM_CheckParm("-allow360") != 0))
            {
                dup = false;

                for (j = 3; j < vid_wmodes; j++)
                {
                    if (QString.Q_strcmp(modedescs[j].desc ?? "", ptr) == 0)
                    {
                        dup = true;
                        dupmode = j;
                        break;
                    }
                }

                if (dup || vid_wmodes < MAX_MODEDESCS)
                {
                    if (!dup || modedescs[dupmode].ismode13 == 0 || COM_CheckParm("-noforcevga") != 0)
                    {
                        if (dup)
                        {
                            k = dupmode;
                        }
                        else
                        {
                            k = vid_wmodes;
                        }

                        modedescs[k].modenum = i;
                        modedescs[k].desc = ptr;
                        modedescs[k].ismode13 = pv.mode13;
                        modedescs[k].iscur = 0;
                        modedescs[k].width = pv.width;

                        if (i == vid_modenum)
                        {
                            modedescs[k].iscur = 1;
                        }

                        if (!dup)
                        {
                            vid_wmodes++;
                        }
                    }
                }
            }
        }

        // sort the modes on width (to handle picking up oddball dibonly modes
        // after all the others)
        for (i = 3; i < vid_wmodes - 1; i++)
        {
            for (j = i + 1; j < vid_wmodes; j++)
            {
                if (modedescs[i].width > modedescs[j].width)
                {
                    tmodedesc = modedescs[i];
                    modedescs[i] = modedescs[j];
                    modedescs[j] = tmodedesc;
                }
            }
        }

        M_Print(13 * 8, 36, "Windowed Modes");

        column = 16;
        row = 36 + (2 * 8);

        for (i = 0; i < 3; i++)
        {
            if (modedescs[i].iscur != 0)
            {
                M_PrintWhite(column, row, modedescs[i].desc ?? "");
            }
            else
            {
                M_Print(column, row, modedescs[i].desc ?? "");
            }

            column += 13 * 8;
        }

        if (vid_wmodes > 3)
        {
            M_Print(12 * 8, 36 + (4 * 8), "Fullscreen Modes");

            column = 16;
            row = 36 + (6 * 8);

            for (i = 3; i < vid_wmodes; i++)
            {
                if (modedescs[i].iscur != 0)
                {
                    M_PrintWhite(column, row, modedescs[i].desc ?? "");
                }
                else
                {
                    M_Print(column, row, modedescs[i].desc ?? "");
                }

                column += 13 * 8;

                if ((i - 3) % VID_ROW_SIZE == VID_ROW_SIZE - 1)
                {
                    column = 16;
                    row += 8;
                }
            }
        }

        // line cursor
        if (vid_testingmode != 0)
        {
            temp = CFormat.Sprintf("TESTING %s", modedescs[vid_line].desc);
            M_Print(13 * 8, 36 + (MODE_AREA_HEIGHT * 8) + (8 * 4), temp);
            M_Print(9 * 8, 36 + (MODE_AREA_HEIGHT * 8) + (8 * 6), "Please wait 5 seconds...");
        }
        else
        {
            M_Print(9 * 8, 36 + (MODE_AREA_HEIGHT * 8) + 8, "Press Enter to set mode");
            M_Print(6 * 8, 36 + (MODE_AREA_HEIGHT * 8) + (8 * 3), "T to test mode for 5 seconds");
            ptr = VID_GetModeDescription2(vid_modenum);

            if (ptr != null)
            {
                temp = CFormat.Sprintf("D to set default: %s", ptr);
                M_Print(2 * 8, 36 + (MODE_AREA_HEIGHT * 8) + (8 * 5), temp);
            }

            ptr = VID_GetModeDescription2((int)_vid_default_mode_win.value);

            if (ptr != null)
            {
                temp = CFormat.Sprintf("Current default: %s", ptr);
                M_Print(3 * 8, 36 + (MODE_AREA_HEIGHT * 8) + (8 * 6), temp);
            }

            M_Print(15 * 8, 36 + (MODE_AREA_HEIGHT * 8) + (8 * 8), "Esc to exit");

            row = 36 + (2 * 8) + (vid_line / VID_ROW_SIZE * 8);
            column = 8 + (vid_line % VID_ROW_SIZE * 13 * 8);

            if (vid_line >= 3)
            {
                row += 3 * 8;
            }

            M_DrawCharacter(column, row, 12 + ((int)(realtime * 4) & 1));
        }
    }

    // vid_win.c: VID_MenuKey
    private void VID_MenuKey(int key)
    {
        if (vid_testingmode != 0)
        {
            return;
        }

        switch (key)
        {
            case K_ESCAPE:
                S_LocalSound("misc/menu1.wav");
                M_Menu_Options_f();
                break;

            case K_LEFTARROW:
                S_LocalSound("misc/menu1.wav");
                vid_line = (vid_line / VID_ROW_SIZE * VID_ROW_SIZE) + ((vid_line + 2) % VID_ROW_SIZE);

                if (vid_line >= vid_wmodes)
                {
                    vid_line = vid_wmodes - 1;
                }

                break;

            case K_RIGHTARROW:
                S_LocalSound("misc/menu1.wav");
                vid_line = (vid_line / VID_ROW_SIZE * VID_ROW_SIZE) + ((vid_line + 4) % VID_ROW_SIZE);

                if (vid_line >= vid_wmodes)
                {
                    vid_line = vid_line / VID_ROW_SIZE * VID_ROW_SIZE;
                }

                break;

            case K_UPARROW:
                S_LocalSound("misc/menu1.wav");
                vid_line -= VID_ROW_SIZE;

                if (vid_line < 0)
                {
                    vid_line += (vid_wmodes + (VID_ROW_SIZE - 1)) / VID_ROW_SIZE * VID_ROW_SIZE;

                    while (vid_line >= vid_wmodes)
                    {
                        vid_line -= VID_ROW_SIZE;
                    }
                }

                break;

            case K_DOWNARROW:
                S_LocalSound("misc/menu1.wav");
                vid_line += VID_ROW_SIZE;

                if (vid_line >= vid_wmodes)
                {
                    vid_line -= (vid_wmodes + (VID_ROW_SIZE - 1)) / VID_ROW_SIZE * VID_ROW_SIZE;

                    while (vid_line < 0)
                    {
                        vid_line += VID_ROW_SIZE;
                    }
                }

                break;

            case K_ENTER:
                S_LocalSound("misc/menu1.wav");
                VID_SetMode(modedescs[vid_line].modenum, vid_curpal);
                break;

            case 'T':
            case 't':
                S_LocalSound("misc/menu1.wav");
                // have to set this before setting the mode because WM_PAINT
                // happens during the mode set and does a VID_Update, which
                // checks vid_testingmode
                vid_testingmode = 1;
                vid_testendtime = realtime + 5.0;

                if (!VID_SetMode(modedescs[vid_line].modenum, vid_curpal))
                {
                    vid_testingmode = 0;
                }

                break;

            case 'D':
            case 'd':
                S_LocalSound("misc/menu1.wav");
                firstupdate = 0;
                Cvar_SetValue("_vid_default_mode_win", vid_modenum);
                break;

            default:
                break;
        }
    }
}
