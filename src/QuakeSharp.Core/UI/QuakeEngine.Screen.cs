using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Render;
using static QuakeSharp.Core.Common.MathLib;

namespace QuakeSharp.Core;

// screen.c: master for refresh, status bar, console, chat, notify, etc. Its drawing (draw.c, sbar.c, the console's
// and the menu's) comes with milestone 6; what it decides and keeps is ported.
public sealed partial class QuakeEngine
{
    // only the refresh window will be updated unless these variables are flagged
    public int scr_copytop;
    public int scr_copyeverything;

    public float scr_con_current;

    /// <summary>lines of console to display</summary>
    public float scr_conlines;

    public float oldscreensize, oldfov;

    public readonly Cvar scr_viewsize = new("viewsize", "100", true);

    /// <summary>10 - 170</summary>
    public readonly Cvar scr_fov = new("fov", "90");

    public readonly Cvar scr_conspeed = new("scr_conspeed", "300");
    public readonly Cvar scr_centertime = new("scr_centertime", "2");
    public readonly Cvar scr_showram = new("showram", "1");
    public readonly Cvar scr_showturtle = new("showturtle", "0");
    public readonly Cvar scr_showpause = new("showpause", "1");
    public readonly Cvar scr_printspeed = new("scr_printspeed", "8");

    /// <summary>ready to draw</summary>
    public bool scr_initialized;

    public QPic? scr_ram;
    public QPic? scr_net;
    public QPic? scr_turtle;

    public int scr_fullupdate;

    public int clearconsole;
    public int clearnotify;

    public VRect scr_vrect;

    public bool scr_disabled_for_loading;
    public bool scr_drawloading;
    public float scr_disabled_time;
    public bool scr_skipupdate;

    public bool block_drawing;

    // ===============================================================================
    // CENTER PRINTING
    // ===============================================================================

    /// <summary>char[1024]</summary>
    public string scr_centerstring = "";

    /// <summary>for slow victory printing</summary>
    public float scr_centertime_start;

    public float scr_centertime_off;
    public int scr_center_lines;
    public int scr_erase_lines;
    public int scr_erase_center;

    public string scr_notifystring = "";
    public bool scr_drawdialog;

    // screen.c: SCR_UpdateScreen's static oldscr_viewsize, oldlcd_x; SCR_DrawTurtle's static count
    private float scr_oldscr_viewsize;
    private float scr_oldlcd_x;
    private int scr_turtle_count;

    // screen.c: SCR_CenterPrint
    // Called for important messages that should stay in the center of the screen for a few moments
    public void SCR_CenterPrint(string str)
    {
        scr_centerstring = str.Length > 1023 ? str[..1023] : str;
        scr_centertime_off = scr_centertime.value;
        scr_centertime_start = (float)cl.time;

        // count the number of lines for centering
        scr_center_lines = 1;
        foreach (char c in str)
        {
            if (c == '\0')
            {
                break;
            }

            if (c == '\n')
            {
                scr_center_lines++;
            }
        }
    }

    // screen.c: SCR_EraseCenterString
    public void SCR_EraseCenterString()
    {
        int y;

        if (scr_erase_center++ > vid.numpages)
        {
            scr_erase_lines = 0;
            return;
        }

        if (scr_center_lines <= 4)
        {
            y = (int)(vid.height * 0.35);
        }
        else
        {
            y = 48;
        }

        scr_copytop = 1;
        Draw_TileClear(0, y, (int)vid.width, 8 * scr_erase_lines);
    }

    // screen.c: SCR_DrawCenterString
    public void SCR_DrawCenterString()
    {
        int start;
        int l;
        int j;
        int x, y;
        int remaining;

        // the finale prints the characters one at a time
        if (cl.intermission != 0)
        {
            remaining = X86.FtoI(scr_printspeed.value * (cl.time - scr_centertime_start));
        }
        else
        {
            remaining = 9999;
        }

        scr_erase_center = 0;
        start = 0;

        if (scr_center_lines <= 4)
        {
            y = (int)(vid.height * 0.35);
        }
        else
        {
            y = 48;
        }

        do
        {
            // scan the width of the line
            for (l = 0; l < 40; l++)
            {
                if (QString.Ch(scr_centerstring, start + l) is '\n' or 0)
                {
                    break;
                }
            }

            x = (int)((vid.width - (l * 8)) / 2);
            for (j = 0; j < l; j++, x += 8)
            {
                Draw_Character(x, y, QString.Ch(scr_centerstring, start + j));
                if (remaining-- == 0)
                {
                    return;
                }
            }

            y += 8;

            while (QString.Ch(scr_centerstring, start) != 0 && QString.Ch(scr_centerstring, start) != '\n')
            {
                start++;
            }

            if (QString.Ch(scr_centerstring, start) == 0)
            {
                break;
            }

            start++; // skip the \n
        }
        while (true);
    }

    // screen.c: SCR_CheckDrawCenterString
    public void SCR_CheckDrawCenterString()
    {
        scr_copytop = 1;
        if (scr_center_lines > scr_erase_lines)
        {
            scr_erase_lines = scr_center_lines;
        }

        scr_centertime_off = (float)(scr_centertime_off - host_frametime);

        if (scr_centertime_off <= 0 && cl.intermission == 0)
        {
            return;
        }

        if (key_dest != keydest_t.key_game)
        {
            return;
        }

        SCR_DrawCenterString();
    }

    // =============================================================================

    // screen.c: CalcFov
    public static float CalcFov(float fov_x, float width, float height)
    {
        float a;
        float x;

        if (fov_x < 1 || fov_x > 179)
        {
            throw Sys_Error("Bad fov: %f", fov_x);
        }

        x = (float)(width / Libm.tan(fov_x / 360 * M_PI));

        a = (float)Libm.atan(height / x);

        a = (float)(a * 360 / M_PI);

        return a;
    }

    // screen.c: SCR_CalcRefdef
    // Must be called whenever vid changes
    // Internal use only
    private void SCR_CalcRefdef()
    {
        VRect vrect;
        float size;

        scr_fullupdate = 0; // force a background redraw
        vid.recalc_refdef = 0;

        // force the status bar to redraw
        Sbar_Changed();

        // ========================================

        // bound viewsize
        if (scr_viewsize.value < 30)
        {
            Cvar_Set("viewsize", "30");
        }

        if (scr_viewsize.value > 120)
        {
            Cvar_Set("viewsize", "120");
        }

        // bound field of view
        if (scr_fov.value < 10)
        {
            Cvar_Set("fov", "10");
        }

        if (scr_fov.value > 170)
        {
            Cvar_Set("fov", "170");
        }

        r_refdef.fov_x = scr_fov.value;
        r_refdef.fov_y = CalcFov(r_refdef.fov_x, r_refdef.vrect.width, r_refdef.vrect.height);

        // intermission is always full screen
        if (cl.intermission != 0)
        {
            size = 120;
        }
        else
        {
            size = scr_viewsize.value;
        }

        if (size >= 120)
        {
            sb_lines = 0; // no status bar at all
        }
        else if (size >= 110)
        {
            sb_lines = 24; // no inventory
        }
        else
        {
            sb_lines = 24 + 16 + 8;
        }

        // these calculations mirror those in R_Init() for r_refdef, but take no account of water warping
        vrect.x = 0;
        vrect.y = 0;
        vrect.width = (int)vid.width;
        vrect.height = (int)vid.height;

        R_SetVrect(vrect, ref scr_vrect, sb_lines);

        // guard against going from one mode to another that's less than half the vertical resolution
        if (scr_con_current > vid.height)
        {
            scr_con_current = vid.height;
        }

        // notify the refresh of the change
        R_ViewChanged(vrect, sb_lines, vid.aspect);
    }

    // screen.c: SCR_SizeUp_f
    // Keybinding command
    public void SCR_SizeUp_f()
    {
        Cvar_SetValue("viewsize", scr_viewsize.value + 10);
        vid.recalc_refdef = 1;
    }

    // screen.c: SCR_SizeDown_f
    // Keybinding command
    public void SCR_SizeDown_f()
    {
        Cvar_SetValue("viewsize", scr_viewsize.value - 10);
        vid.recalc_refdef = 1;
    }

    // ============================================================================

    // screen.c: SCR_Init
    public void SCR_Init()
    {
        Cvar_RegisterVariable(scr_fov);
        Cvar_RegisterVariable(scr_viewsize);
        Cvar_RegisterVariable(scr_conspeed);
        Cvar_RegisterVariable(scr_showram);
        Cvar_RegisterVariable(scr_showturtle);
        Cvar_RegisterVariable(scr_showpause);
        Cvar_RegisterVariable(scr_centertime);
        Cvar_RegisterVariable(scr_printspeed);

        // register our commands
        Cmd_AddCommand("screenshot", SCR_ScreenShot_f);
        Cmd_AddCommand("sizeup", SCR_SizeUp_f);
        Cmd_AddCommand("sizedown", SCR_SizeDown_f);

        scr_ram = Draw_PicFromWad("ram");
        scr_net = Draw_PicFromWad("net");
        scr_turtle = Draw_PicFromWad("turtle");

        scr_initialized = true;
    }

    // screen.c: SCR_DrawRam
    public void SCR_DrawRam()
    {
        if (scr_showram.value == 0)
        {
            return;
        }

        if (!r_cache_thrash)
        {
            return;
        }

        Draw_Pic(scr_vrect.x + 32, scr_vrect.y, scr_ram);
    }

    // screen.c: SCR_DrawTurtle
    public void SCR_DrawTurtle()
    {
        if (scr_showturtle.value == 0)
        {
            return;
        }

        if (host_frametime < 0.1)
        {
            scr_turtle_count = 0;
            return;
        }

        scr_turtle_count++;
        if (scr_turtle_count < 3)
        {
            return;
        }

        Draw_Pic(scr_vrect.x, scr_vrect.y, scr_turtle);
    }

    // screen.c: SCR_DrawNet
    public void SCR_DrawNet()
    {
        if (realtime - cl.last_received_message < 0.3)
        {
            return;
        }

        if (cls.demoplayback)
        {
            return;
        }

        Draw_Pic(scr_vrect.x + 64, scr_vrect.y, scr_net);
    }

    // screen.c: SCR_DrawPause
    public void SCR_DrawPause()
    {
        QPic pic;

        if (scr_showpause.value == 0)
        {
            // turn off for screenshots
            return;
        }

        if (cl.paused == 0)
        {
            return;
        }

        pic = Draw_CachePic("gfx/pause.lmp");
        Draw_Pic((int)((vid.width - pic.width) / 2), (int)((vid.height - 48 - pic.height) / 2), pic);
    }

    // screen.c: SCR_DrawLoading
    public void SCR_DrawLoading()
    {
        QPic pic;

        if (!scr_drawloading)
        {
            return;
        }

        pic = Draw_CachePic("gfx/loading.lmp");
        Draw_Pic((int)((vid.width - pic.width) / 2), (int)((vid.height - 48 - pic.height) / 2), pic);
    }

    // =============================================================================

    // screen.c: SCR_SetUpToDrawConsole
    public void SCR_SetUpToDrawConsole()
    {
        Con_CheckResize();

        if (scr_drawloading)
        {
            return; // never a console with loading plaque
        }

        // decide on the height of the console
        con_forcedup = cl.worldmodel == null || cls.signon != SIGNONS;

        if (con_forcedup)
        {
            scr_conlines = vid.height; // full screen
            scr_con_current = scr_conlines;
        }
        else if (key_dest == keydest_t.key_console)
        {
            scr_conlines = vid.height / 2; // half screen
        }
        else
        {
            scr_conlines = 0; // none visible
        }

        if (scr_conlines < scr_con_current)
        {
            scr_con_current = (float)(scr_con_current - (scr_conspeed.value * host_frametime));
            if (scr_conlines > scr_con_current)
            {
                scr_con_current = scr_conlines;
            }
        }
        else if (scr_conlines > scr_con_current)
        {
            scr_con_current = (float)(scr_con_current + (scr_conspeed.value * host_frametime));
            if (scr_conlines < scr_con_current)
            {
                scr_con_current = scr_conlines;
            }
        }

        if (clearconsole++ < vid.numpages)
        {
            scr_copytop = 1;
            Draw_TileClear(0, (int)scr_con_current, (int)vid.width, (int)vid.height - (int)scr_con_current);
            Sbar_Changed();
        }
        else if (clearnotify++ < vid.numpages)
        {
            scr_copytop = 1;
            Draw_TileClear(0, 0, (int)vid.width, con_notifylines);
        }
        else
        {
            con_notifylines = 0;
        }
    }

    // screen.c: SCR_DrawConsole
    public void SCR_DrawConsole()
    {
        if (scr_con_current != 0)
        {
            scr_copyeverything = 1;
            Con_DrawConsole((int)scr_con_current, true);
            clearconsole = 0;
        }
        else
        {
            if (key_dest is keydest_t.key_game or keydest_t.key_message)
            {
                Con_DrawNotify(); // only draw notify in game
            }
        }
    }

    // ==============================================================================
    // SCREEN SHOTS
    // ==============================================================================

    // screen.c: WritePCXfile
    public void WritePCXfile(string filename, byte[] data, int dataofs, int width, int height, int rowbytes, byte[] palette)
    {
        int i, j;
        var pcx = new List<byte>((width * height * 2) + 1000);

        // pcx_t: manufacturer, version, encoding, bits_per_pixel, xmin, ymin, xmax, ymax, hres, vres, palette[48],
        // reserved, color_planes, bytes_per_line, palette_type, filler[58]
        pcx.Add(0x0a); // PCX id
        pcx.Add(5); // 256 color
        pcx.Add(1); // uncompressed
        pcx.Add(8); // 256 color
        AddShort(0);
        AddShort(0);
        AddShort((short)(width - 1));
        AddShort((short)(height - 1));
        AddShort((short)width);
        AddShort((short)height);
        pcx.AddRange(new byte[48]);
        pcx.Add(0);
        pcx.Add(1); // chunky image
        AddShort((short)width);
        AddShort(2); // not a grey scale
        pcx.AddRange(new byte[58]);

        // pack the image
        int p = dataofs;
        for (i = 0; i < height; i++)
        {
            for (j = 0; j < width; j++)
            {
                if ((data[p] & 0xc0) != 0xc0)
                {
                    pcx.Add(data[p++]);
                }
                else
                {
                    pcx.Add(0xc1);
                    pcx.Add(data[p++]);
                }
            }

            p += rowbytes - width;
        }

        // write the palette
        pcx.Add(0x0c); // palette ID byte
        for (i = 0; i < 768; i++)
        {
            pcx.Add(palette[i]);
        }

        // write output file
        COM_WriteFile(filename, pcx.ToArray());

        void AddShort(short v)
        {
            pcx.Add((byte)v);
            pcx.Add((byte)(v >> 8));
        }
    }

    // screen.c: SCR_ScreenShot_f
    public void SCR_ScreenShot_f()
    {
        int i;
        char[] pcxname = "quake00.pcx".ToCharArray();

        // find a file name to save it to
        for (i = 0; i <= 99; i++)
        {
            pcxname[5] = (char)((i / 10) + '0');
            pcxname[6] = (char)((i % 10) + '0');
            if (!File.Exists(fileSystem.WritePath(new string(pcxname))))
            {
                break; // file doesn't exist
            }
        }

        if (i == 100)
        {
            Con_Printf("SCR_ScreenShot_f: Couldn't create a PCX file\n");
            return;
        }

        // save the pcx file
        D_EnableBackBufferAccess(); // enable direct drawing of console to back buffer

        WritePCXfile(new string(pcxname), vid.buffer, vid.bufferoffset, (int)vid.width, (int)vid.height, (int)vid.rowbytes,
            host_basepal);

        D_DisableBackBufferAccess(); // for adapters that can't stay mapped in for linear writes all the time

        Con_Printf("Wrote %s\n", new string(pcxname));
    }

    // =============================================================================

    // screen.c: SCR_BeginLoadingPlaque
    public void SCR_BeginLoadingPlaque()
    {
        S_StopAllSounds(true);

        if (cls.state != cactive_t.ca_connected)
        {
            return;
        }

        if (cls.signon != SIGNONS)
        {
            return;
        }

        // redraw with no console and the loading plaque
        Con_ClearNotify();
        scr_centertime_off = 0;
        scr_con_current = 0;

        scr_drawloading = true;
        scr_fullupdate = 0;
        Sbar_Changed();
        SCR_UpdateScreen();
        scr_drawloading = false;

        scr_disabled_for_loading = true;
        scr_disabled_time = (float)realtime;
        scr_fullupdate = 0;
    }

    // screen.c: SCR_EndLoadingPlaque
    public void SCR_EndLoadingPlaque()
    {
        scr_disabled_for_loading = false;
        scr_fullupdate = 0;
        Con_ClearNotify();
    }

    // =============================================================================

    // screen.c: SCR_DrawNotifyString
    public void SCR_DrawNotifyString()
    {
        int start;
        int l;
        int j;
        int x, y;

        start = 0;

        y = (int)(vid.height * 0.35);

        do
        {
            // scan the width of the line
            for (l = 0; l < 40; l++)
            {
                if (QString.Ch(scr_notifystring, start + l) is '\n' or 0)
                {
                    break;
                }
            }

            x = (int)((vid.width - (l * 8)) / 2);
            for (j = 0; j < l; j++, x += 8)
            {
                Draw_Character(x, y, QString.Ch(scr_notifystring, start + j));
            }

            y += 8;

            while (QString.Ch(scr_notifystring, start) != 0 && QString.Ch(scr_notifystring, start) != '\n')
            {
                start++;
            }

            if (QString.Ch(scr_notifystring, start) == 0)
            {
                break;
            }

            start++; // skip the \n
        }
        while (true);
    }

    // screen.c: SCR_ModalMessage
    // Displays a text string in the center of the screen and waits for a Y or N keypress.
    public bool SCR_ModalMessage(string text)
    {
        if (cls.state == cactive_t.ca_dedicated)
        {
            return true;
        }

        scr_notifystring = text;

        // draw a fresh screen
        scr_fullupdate = 0;
        scr_drawdialog = true;
        SCR_UpdateScreen();
        scr_drawdialog = false;

        S_ClearBuffer(); // so dma doesn't loop current sound

        do
        {
            key_count = -1; // wait for a key down and up
            Sys_SendKeyEvents();
        }
        while (key_lastpress != 'y' && key_lastpress != 'n' && key_lastpress != UI.Keys.K_ESCAPE);

        scr_fullupdate = 0;
        SCR_UpdateScreen();

        return key_lastpress == 'y';
    }

    // =============================================================================

    // screen.c: SCR_BringDownConsole
    // Brings the console down and fades the palettes back to normal
    public void SCR_BringDownConsole()
    {
        int i;

        scr_centertime_off = 0;

        for (i = 0; i < 20 && scr_conlines != scr_con_current; i++)
        {
            SCR_UpdateScreen();
        }

        cl.cshifts[0].percent = 0; // no area contents palette on next frame
        VID_SetPalette(host_basepal);
    }

    // screen.c: SCR_UpdateScreen
    // This is called every frame, and can also be called explicitly to flush text to the screen.
    //
    // WARNING: be very careful calling this from elsewhere, because the refresh needs almost the entire 256k of stack
    // space!
    public void SCR_UpdateScreen()
    {
        VRect vrect;

        if (scr_skipupdate || block_drawing)
        {
            return;
        }

        scr_copytop = 0;
        scr_copyeverything = 0;

        if (scr_disabled_for_loading)
        {
            if (realtime - scr_disabled_time > 60)
            {
                scr_disabled_for_loading = false;
                Con_Printf("load failed.\n");
            }
            else
            {
                return;
            }
        }

        if (cls.state == cactive_t.ca_dedicated)
        {
            return; // stdout only
        }

        if (!scr_initialized || !con_initialized)
        {
            return; // not initialized yet
        }

        if (scr_viewsize.value != scr_oldscr_viewsize)
        {
            scr_oldscr_viewsize = scr_viewsize.value;
            vid.recalc_refdef = 1;
        }

        // check for vid changes
        if (oldfov != scr_fov.value)
        {
            oldfov = scr_fov.value;
            vid.recalc_refdef = 1;
        }

        if (scr_oldlcd_x != lcd_x.value)
        {
            scr_oldlcd_x = lcd_x.value;
            vid.recalc_refdef = 1;
        }

        if (oldscreensize != scr_viewsize.value)
        {
            oldscreensize = scr_viewsize.value;
            vid.recalc_refdef = 1;
        }

        if (vid.recalc_refdef != 0)
        {
            // something changed, so reorder the screen
            SCR_CalcRefdef();
        }

        // do 3D refresh drawing, and then update the screen
        D_EnableBackBufferAccess(); // of all overlay stuff if drawing directly

        if (scr_fullupdate++ < vid.numpages)
        {
            // clear the entire screen
            scr_copyeverything = 1;
            Draw_TileClear(0, 0, (int)vid.width, (int)vid.height);
            Sbar_Changed();
        }

        SCR_SetUpToDrawConsole();
        SCR_EraseCenterString();

        D_DisableBackBufferAccess(); // for adapters that can't stay mapped in for linear writes all the time

        VID_LockBuffer();

        V_RenderView();

        VID_UnlockBuffer();

        D_EnableBackBufferAccess(); // of all overlay stuff if drawing directly

        if (scr_drawdialog)
        {
            Sbar_Draw();
            Draw_FadeScreen();
            SCR_DrawNotifyString();
            scr_copyeverything = 1;
        }
        else if (scr_drawloading)
        {
            SCR_DrawLoading();
            Sbar_Draw();
        }
        else if (cl.intermission == 1 && key_dest == keydest_t.key_game)
        {
            Sbar_IntermissionOverlay();
        }
        else if (cl.intermission == 2 && key_dest == keydest_t.key_game)
        {
            Sbar_FinaleOverlay();
            SCR_CheckDrawCenterString();
        }
        else if (cl.intermission == 3 && key_dest == keydest_t.key_game)
        {
            SCR_CheckDrawCenterString();
        }
        else
        {
            SCR_DrawRam();
            SCR_DrawNet();
            SCR_DrawTurtle();
            SCR_DrawPause();
            SCR_CheckDrawCenterString();
            Sbar_Draw();
            SCR_DrawConsole();
            M_Draw();
        }

        D_DisableBackBufferAccess(); // for adapters that can't stay mapped in for linear writes all the time

        V_UpdatePalette();

        // update one of three areas
        if (scr_copyeverything != 0)
        {
            vrect.x = 0;
            vrect.y = 0;
            vrect.width = (int)vid.width;
            vrect.height = (int)vid.height;

            VID_Update(vrect);
        }
        else if (scr_copytop != 0)
        {
            vrect.x = 0;
            vrect.y = 0;
            vrect.width = (int)vid.width;
            vrect.height = (int)vid.height - sb_lines;

            VID_Update(vrect);
        }
        else
        {
            vrect.x = scr_vrect.x;
            vrect.y = scr_vrect.y;
            vrect.width = scr_vrect.width;
            vrect.height = scr_vrect.height;

            VID_Update(vrect);
        }
    }

    // screen.c: SCR_UpdateWholeScreen
    public void SCR_UpdateWholeScreen()
    {
        scr_fullupdate = 0;
        SCR_UpdateScreen();
    }
}
