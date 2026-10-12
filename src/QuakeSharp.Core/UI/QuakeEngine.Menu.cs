using System.Text;
using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Numerics;
using static QuakeSharp.Core.Common.QString;
using static QuakeSharp.Core.QuakeDef;
using static QuakeSharp.Core.UI.Keys;

namespace QuakeSharp.Core;

/// <summary>menu.c: the type of m_state (an anonymous enum in the C)</summary>
public enum m_state_t
{
    m_none,
    m_main,
    m_singleplayer,
    m_load,
    m_save,
    m_multiplayer,
    m_setup,
    m_net,
    m_options,
    m_video,
    m_keys,
    m_help,
    m_quit,
    m_serialconfig,
    m_modemconfig,
    m_lanconfig,
    m_gameoptions,
    m_search,
    m_slist,
}

// menu.c: the menus
public sealed partial class QuakeEngine
{
    public m_state_t m_state;

    /// <summary>play after drawing a frame, so caching won't disrupt the sound</summary>
    public bool m_entersound;

    public bool m_recursiveDraw;

    public m_state_t m_return_state;
    public bool m_return_onerror;

    /// <summary>char[32]</summary>
    public string m_return_reason = "";

    // menu.c: M_DrawCharacter
    // Draws one solid graphics character
    public void M_DrawCharacter(int cx, int line, int num) => Draw_Character(cx + (int)((vid.width - 320) >> 1), line, num);

    // menu.c: M_Print
    public void M_Print(int cx, int cy, string str)
    {
        int c;
        for (int i = 0; (c = Ch(str, i)) != 0; i++)
        {
            M_DrawCharacter(cx, cy, c + 128);
            cx += 8;
        }
    }

    // menu.c: M_PrintWhite
    public void M_PrintWhite(int cx, int cy, string str)
    {
        int c;
        for (int i = 0; (c = Ch(str, i)) != 0; i++)
        {
            M_DrawCharacter(cx, cy, c);
            cx += 8;
        }
    }

    // menu.c: M_DrawTransPic
    public void M_DrawTransPic(int x, int y, QPic pic) => Draw_TransPic(x + (int)((vid.width - 320) >> 1), y, pic);

    // menu.c: M_DrawPic
    public void M_DrawPic(int x, int y, QPic pic) => Draw_Pic(x + (int)((vid.width - 320) >> 1), y, pic);

    public readonly byte[] identityTable = new byte[256];
    public readonly byte[] translationTable = new byte[256];

    // menu.c: M_BuildTranslationTable
    public void M_BuildTranslationTable(int top, int bottom)
    {
        int j;

        for (j = 0; j < 256; j++)
        {
            identityTable[j] = (byte)j;
        }

        Array.Copy(identityTable, translationTable, 256);

        if (top < 128)
        {
            // the artists made some backwards ranges.  sigh.
            Array.Copy(identityTable, top, translationTable, TOP_RANGE, 16);
        }
        else
        {
            for (j = 0; j < 16; j++)
            {
                translationTable[TOP_RANGE + j] = identityTable[top + 15 - j];
            }
        }

        if (bottom < 128)
        {
            Array.Copy(identityTable, bottom, translationTable, BOTTOM_RANGE, 16);
        }
        else
        {
            for (j = 0; j < 16; j++)
            {
                translationTable[BOTTOM_RANGE + j] = identityTable[bottom + 15 - j];
            }
        }
    }

    // menu.c: M_DrawTransPicTranslate
    public void M_DrawTransPicTranslate(int x, int y, QPic pic) =>
        Draw_TransPicTranslate(x + (int)((vid.width - 320) >> 1), y, pic, translationTable);

    // menu.c: M_DrawTextBox
    public void M_DrawTextBox(int x, int y, int width, int lines)
    {
        QPic p;
        int cx, cy;
        int n;

        // draw left side
        cx = x;
        cy = y;
        p = Draw_CachePic("gfx/box_tl.lmp");
        M_DrawTransPic(cx, cy, p);
        p = Draw_CachePic("gfx/box_ml.lmp");
        for (n = 0; n < lines; n++)
        {
            cy += 8;
            M_DrawTransPic(cx, cy, p);
        }

        p = Draw_CachePic("gfx/box_bl.lmp");
        M_DrawTransPic(cx, cy + 8, p);

        // draw middle
        cx += 8;
        while (width > 0)
        {
            cy = y;
            p = Draw_CachePic("gfx/box_tm.lmp");
            M_DrawTransPic(cx, cy, p);
            p = Draw_CachePic("gfx/box_mm.lmp");
            for (n = 0; n < lines; n++)
            {
                cy += 8;
                if (n == 1)
                {
                    p = Draw_CachePic("gfx/box_mm2.lmp");
                }

                M_DrawTransPic(cx, cy, p);
            }

            p = Draw_CachePic("gfx/box_bm.lmp");
            M_DrawTransPic(cx, cy + 8, p);
            width -= 2;
            cx += 16;
        }

        // draw right side
        cy = y;
        p = Draw_CachePic("gfx/box_tr.lmp");
        M_DrawTransPic(cx, cy, p);
        p = Draw_CachePic("gfx/box_mr.lmp");
        for (n = 0; n < lines; n++)
        {
            cy += 8;
            M_DrawTransPic(cx, cy, p);
        }

        p = Draw_CachePic("gfx/box_br.lmp");
        M_DrawTransPic(cx, cy + 8, p);
    }

    // =============================================================================

    public int m_save_demonum;

    // menu.c: M_ToggleMenu_f
    public void M_ToggleMenu_f()
    {
        m_entersound = true;

        if (key_dest == keydest_t.key_menu)
        {
            if (m_state != m_state_t.m_main)
            {
                M_Menu_Main_f();
                return;
            }

            key_dest = keydest_t.key_game;
            m_state = m_state_t.m_none;
            return;
        }

        if (key_dest == keydest_t.key_console)
        {
            Con_ToggleConsole_f();
        }
        else
        {
            M_Menu_Main_f();
        }
    }

    // =============================================================================
    // MAIN MENU

    public int m_main_cursor;
    private const int MAIN_ITEMS = 5;

    // menu.c: M_Menu_Main_f
    public void M_Menu_Main_f()
    {
        if (key_dest != keydest_t.key_menu)
        {
            m_save_demonum = cls.demonum;
            cls.demonum = -1;
        }

        key_dest = keydest_t.key_menu;
        m_state = m_state_t.m_main;
        m_entersound = true;
    }

    // menu.c: M_Main_Draw
    public void M_Main_Draw()
    {
        int f;
        QPic p;

        M_DrawTransPic(16, 4, Draw_CachePic("gfx/qplaque.lmp"));
        p = Draw_CachePic("gfx/ttl_main.lmp");
        M_DrawPic((320 - p.width) / 2, 4, p);
        M_DrawTransPic(72, 32, Draw_CachePic("gfx/mainmenu.lmp"));

        f = (int)(host_time * 10) % 6;

        M_DrawTransPic(54, 32 + (m_main_cursor * 20), Draw_CachePic(va("gfx/menudot%i.lmp", f + 1)));
    }

    // menu.c: M_Main_Key
    public void M_Main_Key(int key)
    {
        switch (key)
        {
            case K_ESCAPE:
                key_dest = keydest_t.key_game;
                m_state = m_state_t.m_none;
                cls.demonum = m_save_demonum;
                if (cls.demonum != -1 && !cls.demoplayback && cls.state != cactive_t.ca_connected)
                {
                    CL_NextDemo();
                }

                break;

            case K_DOWNARROW:
                S_LocalSound("misc/menu1.wav");
                if (++m_main_cursor >= MAIN_ITEMS)
                {
                    m_main_cursor = 0;
                }

                break;

            case K_UPARROW:
                S_LocalSound("misc/menu1.wav");
                if (--m_main_cursor < 0)
                {
                    m_main_cursor = MAIN_ITEMS - 1;
                }

                break;

            case K_ENTER:
                m_entersound = true;

                switch (m_main_cursor)
                {
                    case 0:
                        M_Menu_SinglePlayer_f();
                        break;

                    case 1:
                        M_Menu_MultiPlayer_f();
                        break;

                    case 2:
                        M_Menu_Options_f();
                        break;

                    case 3:
                        M_Menu_Help_f();
                        break;

                    case 4:
                        M_Menu_Quit_f();
                        break;
                }

                break;
        }
    }

    // =============================================================================
    // SINGLE PLAYER MENU

    public int m_singleplayer_cursor;
    private const int SINGLEPLAYER_ITEMS = 3;

    // menu.c: M_Menu_SinglePlayer_f
    public void M_Menu_SinglePlayer_f()
    {
        key_dest = keydest_t.key_menu;
        m_state = m_state_t.m_singleplayer;
        m_entersound = true;
    }

    // menu.c: M_SinglePlayer_Draw
    public void M_SinglePlayer_Draw()
    {
        int f;
        QPic p;

        M_DrawTransPic(16, 4, Draw_CachePic("gfx/qplaque.lmp"));
        p = Draw_CachePic("gfx/ttl_sgl.lmp");
        M_DrawPic((320 - p.width) / 2, 4, p);
        M_DrawTransPic(72, 32, Draw_CachePic("gfx/sp_menu.lmp"));

        f = (int)(host_time * 10) % 6;

        M_DrawTransPic(54, 32 + (m_singleplayer_cursor * 20), Draw_CachePic(va("gfx/menudot%i.lmp", f + 1)));
    }

    // menu.c: M_SinglePlayer_Key
    public void M_SinglePlayer_Key(int key)
    {
        switch (key)
        {
            case K_ESCAPE:
                M_Menu_Main_f();
                break;

            case K_DOWNARROW:
                S_LocalSound("misc/menu1.wav");
                if (++m_singleplayer_cursor >= SINGLEPLAYER_ITEMS)
                {
                    m_singleplayer_cursor = 0;
                }

                break;

            case K_UPARROW:
                S_LocalSound("misc/menu1.wav");
                if (--m_singleplayer_cursor < 0)
                {
                    m_singleplayer_cursor = SINGLEPLAYER_ITEMS - 1;
                }

                break;

            case K_ENTER:
                m_entersound = true;

                switch (m_singleplayer_cursor)
                {
                    case 0:
                        if (sv.active)
                        {
                            if (!SCR_ModalMessage("Are you sure you want to\nstart a new game?\n"))
                            {
                                break;
                            }
                        }

                        key_dest = keydest_t.key_game;
                        if (sv.active)
                        {
                            Cbuf_AddText("disconnect\n");
                        }

                        Cbuf_AddText("maxplayers 1\n");
                        Cbuf_AddText("map start\n");
                        break;

                    case 1:
                        M_Menu_Load_f();
                        break;

                    case 2:
                        M_Menu_Save_f();
                        break;
                }

                break;
        }
    }

    // =============================================================================
    // LOAD/SAVE MENU

    /// <summary>0 &lt; load_cursor &lt; MAX_SAVEGAMES</summary>
    public int load_cursor;

    private const int MAX_SAVEGAMES = 12;

    /// <summary>char[MAX_SAVEGAMES][SAVEGAME_COMMENT_LENGTH+1]</summary>
    public readonly string[] m_filenames = new string[MAX_SAVEGAMES];

    public readonly bool[] loadable = new bool[MAX_SAVEGAMES];

    // menu.c: M_ScanSaves (the savegames are where the game writes, design.md §9)
    public void M_ScanSaves()
    {
        int i, j;
        string name;

        for (i = 0; i < MAX_SAVEGAMES; i++)
        {
            m_filenames[i] = "--- UNUSED SLOT ---";
            loadable[i] = false;
            string path = fileSystem.WritePath(CFormat.Sprintf("s%i.sav", i));
            byte[] f;
            try
            {
                f = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            // fscanf (f, "%i\n", &version); fscanf (f, "%79s\n", name): name still holds the file's name if no
            // string is read; strncpy stops at a 0
            var file = new CStdioReader(f);
            if (file.ScanInt() != null)
            {
                file.SkipSpace();
            }

            name = file.ScanString(79) ?? CFormat.Sprintf("%s/s%i.sav", com_gamedir, i);
            name = name[..QString.Q_strlen(name)];
            var comment = new StringBuilder(name.Length > SAVEGAME_COMMENT_LENGTH ? name[..SAVEGAME_COMMENT_LENGTH] : name);

            // change _ back to space
            for (j = 0; j < comment.Length; j++)
            {
                if (comment[j] == '_')
                {
                    comment[j] = ' ';
                }
            }

            m_filenames[i] = comment.ToString();
            loadable[i] = true;
        }
    }

    // menu.c: M_Menu_Load_f
    public void M_Menu_Load_f()
    {
        m_entersound = true;
        m_state = m_state_t.m_load;
        key_dest = keydest_t.key_menu;
        M_ScanSaves();
    }

    // menu.c: M_Menu_Save_f
    public void M_Menu_Save_f()
    {
        if (!sv.active)
        {
            return;
        }

        if (cl.intermission != 0)
        {
            return;
        }

        if (svs.maxclients != 1)
        {
            return;
        }

        m_entersound = true;
        m_state = m_state_t.m_save;
        key_dest = keydest_t.key_menu;
        M_ScanSaves();
    }

    // menu.c: M_Load_Draw
    public void M_Load_Draw()
    {
        int i;
        QPic p;

        p = Draw_CachePic("gfx/p_load.lmp");
        M_DrawPic((320 - p.width) / 2, 4, p);

        for (i = 0; i < MAX_SAVEGAMES; i++)
        {
            M_Print(16, 32 + (8 * i), m_filenames[i]);
        }

        // line cursor
        M_DrawCharacter(8, 32 + (load_cursor * 8), 12 + ((int)(realtime * 4) & 1));
    }

    // menu.c: M_Save_Draw
    public void M_Save_Draw()
    {
        int i;
        QPic p;

        p = Draw_CachePic("gfx/p_save.lmp");
        M_DrawPic((320 - p.width) / 2, 4, p);

        for (i = 0; i < MAX_SAVEGAMES; i++)
        {
            M_Print(16, 32 + (8 * i), m_filenames[i]);
        }

        // line cursor
        M_DrawCharacter(8, 32 + (load_cursor * 8), 12 + ((int)(realtime * 4) & 1));
    }

    // menu.c: M_Load_Key
    public void M_Load_Key(int k)
    {
        switch (k)
        {
            case K_ESCAPE:
                M_Menu_SinglePlayer_f();
                break;

            case K_ENTER:
                S_LocalSound("misc/menu2.wav");
                if (!loadable[load_cursor])
                {
                    return;
                }

                m_state = m_state_t.m_none;
                key_dest = keydest_t.key_game;

                // Host_Loadgame_f can't bring up the loading plaque because too much stack space has been used, so
                // do it now
                SCR_BeginLoadingPlaque();

                // issue the load command
                Cbuf_AddText(va("load s%i\n", load_cursor));
                return;

            case K_UPARROW:
            case K_LEFTARROW:
                S_LocalSound("misc/menu1.wav");
                load_cursor--;
                if (load_cursor < 0)
                {
                    load_cursor = MAX_SAVEGAMES - 1;
                }

                break;

            case K_DOWNARROW:
            case K_RIGHTARROW:
                S_LocalSound("misc/menu1.wav");
                load_cursor++;
                if (load_cursor >= MAX_SAVEGAMES)
                {
                    load_cursor = 0;
                }

                break;
        }
    }

    // menu.c: M_Save_Key
    public void M_Save_Key(int k)
    {
        switch (k)
        {
            case K_ESCAPE:
                M_Menu_SinglePlayer_f();
                break;

            case K_ENTER:
                m_state = m_state_t.m_none;
                key_dest = keydest_t.key_game;
                Cbuf_AddText(va("save s%i\n", load_cursor));
                return;

            case K_UPARROW:
            case K_LEFTARROW:
                S_LocalSound("misc/menu1.wav");
                load_cursor--;
                if (load_cursor < 0)
                {
                    load_cursor = MAX_SAVEGAMES - 1;
                }

                break;

            case K_DOWNARROW:
            case K_RIGHTARROW:
                S_LocalSound("misc/menu1.wav");
                load_cursor++;
                if (load_cursor >= MAX_SAVEGAMES)
                {
                    load_cursor = 0;
                }

                break;
        }
    }

    // =============================================================================
    // MULTIPLAYER MENU

    public int m_multiplayer_cursor;
    private const int MULTIPLAYER_ITEMS = 3;

    // menu.c: M_Menu_MultiPlayer_f
    public void M_Menu_MultiPlayer_f()
    {
        key_dest = keydest_t.key_menu;
        m_state = m_state_t.m_multiplayer;
        m_entersound = true;
    }

    // menu.c: M_MultiPlayer_Draw
    public void M_MultiPlayer_Draw()
    {
        int f;
        QPic p;

        M_DrawTransPic(16, 4, Draw_CachePic("gfx/qplaque.lmp"));
        p = Draw_CachePic("gfx/p_multi.lmp");
        M_DrawPic((320 - p.width) / 2, 4, p);
        M_DrawTransPic(72, 32, Draw_CachePic("gfx/mp_menu.lmp"));

        f = (int)(host_time * 10) % 6;

        M_DrawTransPic(54, 32 + (m_multiplayer_cursor * 20), Draw_CachePic(va("gfx/menudot%i.lmp", f + 1)));

        if (serialAvailable || ipxAvailable || tcpipAvailable)
        {
            return;
        }

        M_PrintWhite((320 / 2) - (27 * 8 / 2), 148, "No Communications Available");
    }

    // menu.c: M_MultiPlayer_Key
    public void M_MultiPlayer_Key(int key)
    {
        switch (key)
        {
            case K_ESCAPE:
                M_Menu_Main_f();
                break;

            case K_DOWNARROW:
                S_LocalSound("misc/menu1.wav");
                if (++m_multiplayer_cursor >= MULTIPLAYER_ITEMS)
                {
                    m_multiplayer_cursor = 0;
                }

                break;

            case K_UPARROW:
                S_LocalSound("misc/menu1.wav");
                if (--m_multiplayer_cursor < 0)
                {
                    m_multiplayer_cursor = MULTIPLAYER_ITEMS - 1;
                }

                break;

            case K_ENTER:
                m_entersound = true;
                switch (m_multiplayer_cursor)
                {
                    case 0:
                        if (serialAvailable || ipxAvailable || tcpipAvailable)
                        {
                            M_Menu_Net_f();
                        }

                        break;

                    case 1:
                        if (serialAvailable || ipxAvailable || tcpipAvailable)
                        {
                            M_Menu_Net_f();
                        }

                        break;

                    case 2:
                        M_Menu_Setup_f();
                        break;
                }

                break;
        }
    }

    // =============================================================================
    // SETUP MENU

    public int setup_cursor = 4;
    private static readonly int[] setup_cursor_table = [40, 56, 80, 104, 140];

    /// <summary>char[16]</summary>
    public string setup_hostname = "";

    /// <summary>char[16]</summary>
    public string setup_myname = "";

    public int setup_oldtop;
    public int setup_oldbottom;
    public int setup_top;
    public int setup_bottom;

    private const int NUM_SETUP_CMDS = 5;

    // menu.c: M_Menu_Setup_f
    public void M_Menu_Setup_f()
    {
        key_dest = keydest_t.key_menu;
        m_state = m_state_t.m_setup;
        m_entersound = true;
        setup_myname = CString15(cl_name.@string);
        setup_hostname = CString15(hostname.@string);
        setup_top = setup_oldtop = ((int)cl_color.value) >> 4;
        setup_bottom = setup_oldbottom = ((int)cl_color.value) & 15;

        // Q_strcpy into char[16]: a longer string overruns the C's array; here it is cut to what fits
        static string CString15(string s)
        {
            int l = Q_strlen(s);
            return s[..Math.Min(l, 15)];
        }
    }

    // menu.c: M_Setup_Draw
    public void M_Setup_Draw()
    {
        QPic p;

        M_DrawTransPic(16, 4, Draw_CachePic("gfx/qplaque.lmp"));
        p = Draw_CachePic("gfx/p_multi.lmp");
        M_DrawPic((320 - p.width) / 2, 4, p);

        M_Print(64, 40, "Hostname");
        M_DrawTextBox(160, 32, 16, 1);
        M_Print(168, 40, setup_hostname);

        M_Print(64, 56, "Your name");
        M_DrawTextBox(160, 48, 16, 1);
        M_Print(168, 56, setup_myname);

        M_Print(64, 80, "Shirt color");
        M_Print(64, 104, "Pants color");

        M_DrawTextBox(64, 140 - 8, 14, 1);
        M_Print(72, 140, "Accept Changes");

        p = Draw_CachePic("gfx/bigbox.lmp");
        M_DrawTransPic(160, 64, p);
        p = Draw_CachePic("gfx/menuplyr.lmp");
        M_BuildTranslationTable(setup_top * 16, setup_bottom * 16);
        M_DrawTransPicTranslate(172, 72, p);

        M_DrawCharacter(56, setup_cursor_table[setup_cursor], 12 + ((int)(realtime * 4) & 1));

        if (setup_cursor == 0)
        {
            M_DrawCharacter(168 + (8 * setup_hostname.Length), setup_cursor_table[setup_cursor], 10 + ((int)(realtime * 4) & 1));
        }

        if (setup_cursor == 1)
        {
            M_DrawCharacter(168 + (8 * setup_myname.Length), setup_cursor_table[setup_cursor], 10 + ((int)(realtime * 4) & 1));
        }
    }

    // menu.c: M_Setup_Key
    public void M_Setup_Key(int k)
    {
        int l;
        bool forward = false;

        switch (k)
        {
            case K_ESCAPE:
                M_Menu_MultiPlayer_f();
                break;

            case K_UPARROW:
                S_LocalSound("misc/menu1.wav");
                setup_cursor--;
                if (setup_cursor < 0)
                {
                    setup_cursor = NUM_SETUP_CMDS - 1;
                }

                break;

            case K_DOWNARROW:
                S_LocalSound("misc/menu1.wav");
                setup_cursor++;
                if (setup_cursor >= NUM_SETUP_CMDS)
                {
                    setup_cursor = 0;
                }

                break;

            case K_LEFTARROW:
                if (setup_cursor < 2)
                {
                    return;
                }

                S_LocalSound("misc/menu3.wav");
                if (setup_cursor == 2)
                {
                    setup_top--;
                }

                if (setup_cursor == 3)
                {
                    setup_bottom--;
                }

                break;

            case K_RIGHTARROW:
                if (setup_cursor < 2)
                {
                    return;
                }

                forward = true;
                break;

            case K_ENTER:
                if (setup_cursor is 0 or 1)
                {
                    return;
                }

                if (setup_cursor is 2 or 3)
                {
                    forward = true;
                    break;
                }

                // setup_cursor == 4 (OK)
                if (Q_strcmp(cl_name.@string, setup_myname) != 0)
                {
                    Cbuf_AddText(va("name \"%s\"\n", setup_myname));
                }

                if (Q_strcmp(hostname.@string, setup_hostname) != 0)
                {
                    Cvar_Set("hostname", setup_hostname);
                }

                if (setup_top != setup_oldtop || setup_bottom != setup_oldbottom)
                {
                    Cbuf_AddText(va("color %i %i\n", setup_top, setup_bottom));
                }

                m_entersound = true;
                M_Menu_MultiPlayer_f();
                break;

            case K_BACKSPACE:
                if (setup_cursor == 0)
                {
                    if (setup_hostname.Length != 0)
                    {
                        setup_hostname = setup_hostname[..^1];
                    }
                }

                if (setup_cursor == 1)
                {
                    if (setup_myname.Length != 0)
                    {
                        setup_myname = setup_myname[..^1];
                    }
                }

                break;

            default:
                if (k is < 32 or > 127)
                {
                    break;
                }

                if (setup_cursor == 0)
                {
                    l = setup_hostname.Length;
                    if (l < 15)
                    {
                        setup_hostname += (char)k;
                    }
                }

                if (setup_cursor == 1)
                {
                    l = setup_myname.Length;
                    if (l < 15)
                    {
                        setup_myname += (char)k;
                    }
                }

                break;
        }

        // forward:
        if (forward)
        {
            S_LocalSound("misc/menu3.wav");
            if (setup_cursor == 2)
            {
                setup_top++;
            }

            if (setup_cursor == 3)
            {
                setup_bottom++;
            }
        }

        if (setup_top > 13)
        {
            setup_top = 0;
        }

        if (setup_top < 0)
        {
            setup_top = 13;
        }

        if (setup_bottom > 13)
        {
            setup_bottom = 0;
        }

        if (setup_bottom < 0)
        {
            setup_bottom = 13;
        }
    }

    // =============================================================================
    // NET MENU

    // menu.c: StartingGame, JoiningGame, SerialConfig, DirectConfig, IPXConfig, TCPIPConfig
    private bool StartingGame => m_multiplayer_cursor == 1;

    private bool JoiningGame => m_multiplayer_cursor == 0;

    private bool SerialConfig => m_net_cursor == 0;

    private bool DirectConfig => m_net_cursor == 1;

    private bool IPXConfig => m_net_cursor == 2;

    private bool TCPIPConfig => m_net_cursor == 3;

    public int m_net_cursor;
    public int m_net_items;
    public int m_net_saveHeight;

    private static readonly string[] net_helpMessage =
    [
        // .........1.........2....
        "                        ",
        " Two computers connected",
        "   through two modems.  ",
        "                        ",

        "                        ",
        " Two computers connected",
        " by a null-modem cable. ",
        "                        ",

        " Novell network LANs    ",
        " or Windows 95 DOS-box. ",
        "                        ",
        "(LAN=Local Area Network)",

        " Commonly used to play  ",
        " over the Internet, but ",
        " also used on a Local   ",
        " Area Network.          ",
    ];

    // menu.c: M_Menu_Net_f
    public void M_Menu_Net_f()
    {
        key_dest = keydest_t.key_menu;
        m_state = m_state_t.m_net;
        m_entersound = true;
        m_net_items = 4;

        if (m_net_cursor >= m_net_items)
        {
            m_net_cursor = 0;
        }

        m_net_cursor--;
        M_Net_Key(K_DOWNARROW);
    }

    // menu.c: M_Net_Draw (WinQuake's: no dimmed pictures for the serial links)
    public void M_Net_Draw()
    {
        int f;
        QPic? p;

        M_DrawTransPic(16, 4, Draw_CachePic("gfx/qplaque.lmp"));
        p = Draw_CachePic("gfx/p_multi.lmp");
        M_DrawPic((320 - p.width) / 2, 4, p);

        f = 32;

        if (serialAvailable)
        {
            p = Draw_CachePic("gfx/netmen1.lmp");
        }
        else
        {
            p = null;
        }

        if (p != null)
        {
            M_DrawTransPic(72, f, p);
        }

        f += 19;

        if (serialAvailable)
        {
            p = Draw_CachePic("gfx/netmen2.lmp");
        }
        else
        {
            p = null;
        }

        if (p != null)
        {
            M_DrawTransPic(72, f, p);
        }

        f += 19;
        if (ipxAvailable)
        {
            p = Draw_CachePic("gfx/netmen3.lmp");
        }
        else
        {
            p = Draw_CachePic("gfx/dim_ipx.lmp");
        }

        M_DrawTransPic(72, f, p);

        f += 19;
        if (tcpipAvailable)
        {
            p = Draw_CachePic("gfx/netmen4.lmp");
        }
        else
        {
            p = Draw_CachePic("gfx/dim_tcp.lmp");
        }

        M_DrawTransPic(72, f, p);

        if (m_net_items == 5)
        {
            // JDC, could just be removed
            f += 19;
            p = Draw_CachePic("gfx/netmen5.lmp");
            M_DrawTransPic(72, f, p);
        }

        f = (320 - (26 * 8)) / 2;
        M_DrawTextBox(f, 134, 24, 4);
        f += 8;
        M_Print(f, 142, net_helpMessage[(m_net_cursor * 4) + 0]);
        M_Print(f, 150, net_helpMessage[(m_net_cursor * 4) + 1]);
        M_Print(f, 158, net_helpMessage[(m_net_cursor * 4) + 2]);
        M_Print(f, 166, net_helpMessage[(m_net_cursor * 4) + 3]);

        f = (int)(host_time * 10) % 6;
        M_DrawTransPic(54, 32 + (m_net_cursor * 20), Draw_CachePic(va("gfx/menudot%i.lmp", f + 1)));
    }

    // menu.c: M_Net_Key
    public void M_Net_Key(int k)
    {
    again:
        switch (k)
        {
            case K_ESCAPE:
                M_Menu_MultiPlayer_f();
                break;

            case K_DOWNARROW:
                S_LocalSound("misc/menu1.wav");
                if (++m_net_cursor >= m_net_items)
                {
                    m_net_cursor = 0;
                }

                break;

            case K_UPARROW:
                S_LocalSound("misc/menu1.wav");
                if (--m_net_cursor < 0)
                {
                    m_net_cursor = m_net_items - 1;
                }

                break;

            case K_ENTER:
                m_entersound = true;

                switch (m_net_cursor)
                {
                    case 0:
                        M_Menu_SerialConfig_f();
                        break;

                    case 1:
                        M_Menu_SerialConfig_f();
                        break;

                    case 2:
                        M_Menu_LanConfig_f();
                        break;

                    case 3:
                        M_Menu_LanConfig_f();
                        break;

                    case 4:
                        // multiprotocol
                        break;
                }

                break;
        }

        if (m_net_cursor == 0 && !serialAvailable)
        {
            goto again;
        }

        if (m_net_cursor == 1 && !serialAvailable)
        {
            goto again;
        }

        if (m_net_cursor == 2 && !ipxAvailable)
        {
            goto again;
        }

        if (m_net_cursor == 3 && !tcpipAvailable)
        {
            goto again;
        }
    }

    // =============================================================================
    // OPTIONS MENU

    private const int OPTIONS_ITEMS = 14;

    private const int SLIDER_RANGE = 10;

    public int options_cursor;

    // menu.c: M_Menu_Options_f
    public void M_Menu_Options_f()
    {
        key_dest = keydest_t.key_menu;
        m_state = m_state_t.m_options;
        m_entersound = true;

        if ((options_cursor == 13) && (modestate != modestate_t.MS_WINDOWED))
        {
            options_cursor = 0;
        }
    }

    // menu.c: M_AdjustSliders
    public void M_AdjustSliders(int dir)
    {
        S_LocalSound("misc/menu3.wav");

        switch (options_cursor)
        {
            case 3: // screen size
                scr_viewsize.value += dir * 10;
                if (scr_viewsize.value < 30)
                {
                    scr_viewsize.value = 30;
                }

                if (scr_viewsize.value > 120)
                {
                    scr_viewsize.value = 120;
                }

                Cvar_SetValue("viewsize", scr_viewsize.value);
                break;

            case 4: // gamma
                v_gamma.value = (float)(v_gamma.value - (dir * 0.05));
                if (v_gamma.value < 0.5)
                {
                    v_gamma.value = 0.5f;
                }

                if (v_gamma.value > 1)
                {
                    v_gamma.value = 1;
                }

                Cvar_SetValue("gamma", v_gamma.value);
                break;

            case 5: // mouse speed
                sensitivity.value = (float)(sensitivity.value + (dir * 0.5));
                if (sensitivity.value < 1)
                {
                    sensitivity.value = 1;
                }

                if (sensitivity.value > 11)
                {
                    sensitivity.value = 11;
                }

                Cvar_SetValue("sensitivity", sensitivity.value);
                break;

            case 6: // music volume
                bgmvolume.value = (float)(bgmvolume.value + (dir * 1.0));
                if (bgmvolume.value < 0)
                {
                    bgmvolume.value = 0;
                }

                if (bgmvolume.value > 1)
                {
                    bgmvolume.value = 1;
                }

                Cvar_SetValue("bgmvolume", bgmvolume.value);
                break;

            case 7: // sfx volume
                volume.value = (float)(volume.value + (dir * 0.1));
                if (volume.value < 0)
                {
                    volume.value = 0;
                }

                if (volume.value > 1)
                {
                    volume.value = 1;
                }

                Cvar_SetValue("volume", volume.value);
                break;

            case 8: // allways run
                if (cl_forwardspeed.value > 200)
                {
                    Cvar_SetValue("cl_forwardspeed", 200);
                    Cvar_SetValue("cl_backspeed", 200);
                }
                else
                {
                    Cvar_SetValue("cl_forwardspeed", 400);
                    Cvar_SetValue("cl_backspeed", 400);
                }

                break;

            case 9: // invert mouse
                Cvar_SetValue("m_pitch", -m_pitch.value);
                break;

            case 10: // lookspring
                Cvar_SetValue("lookspring", lookspring.value == 0 ? 1 : 0);
                break;

            case 11: // lookstrafe
                Cvar_SetValue("lookstrafe", lookstrafe.value == 0 ? 1 : 0);
                break;

            case 13: // _windowed_mouse
                Cvar_SetValue("_windowed_mouse", _windowed_mouse.value == 0 ? 1 : 0);
                break;
        }
    }

    // menu.c: M_DrawSlider
    public void M_DrawSlider(int x, int y, float range)
    {
        int i;

        if (range < 0)
        {
            range = 0;
        }

        if (range > 1)
        {
            range = 1;
        }

        M_DrawCharacter(x - 8, y, 128);
        for (i = 0; i < SLIDER_RANGE; i++)
        {
            M_DrawCharacter(x + (i * 8), y, 129);
        }

        M_DrawCharacter(x + (i * 8), y, 130);
        M_DrawCharacter((int)(x + ((SLIDER_RANGE - 1) * 8 * range)), y, 131);
    }

    // menu.c: M_DrawCheckbox
    public void M_DrawCheckbox(int x, int y, int on)
    {
        if (on != 0)
        {
            M_Print(x, y, "on");
        }
        else
        {
            M_Print(x, y, "off");
        }
    }

    // menu.c: M_Options_Draw
    public void M_Options_Draw()
    {
        float r;
        QPic p;

        M_DrawTransPic(16, 4, Draw_CachePic("gfx/qplaque.lmp"));
        p = Draw_CachePic("gfx/p_option.lmp");
        M_DrawPic((320 - p.width) / 2, 4, p);

        M_Print(16, 32, "    Customize controls");
        M_Print(16, 40, "         Go to console");
        M_Print(16, 48, "     Reset to defaults");

        M_Print(16, 56, "           Screen size");
        r = (scr_viewsize.value - 30) / (120 - 30);
        M_DrawSlider(220, 56, r);

        M_Print(16, 64, "            Brightness");
        r = (float)((1.0 - v_gamma.value) / 0.5);
        M_DrawSlider(220, 64, r);

        M_Print(16, 72, "           Mouse Speed");
        r = (sensitivity.value - 1) / 10;
        M_DrawSlider(220, 72, r);

        M_Print(16, 80, "       CD Music Volume");
        r = bgmvolume.value;
        M_DrawSlider(220, 80, r);

        M_Print(16, 88, "          Sound Volume");
        r = volume.value;
        M_DrawSlider(220, 88, r);

        M_Print(16, 96, "            Always Run");
        M_DrawCheckbox(220, 96, cl_forwardspeed.value > 200 ? 1 : 0);

        M_Print(16, 104, "          Invert Mouse");
        M_DrawCheckbox(220, 104, m_pitch.value < 0 ? 1 : 0);

        M_Print(16, 112, "            Lookspring");
        M_DrawCheckbox(220, 112, X86.FtoI(lookspring.value));

        M_Print(16, 120, "            Lookstrafe");
        M_DrawCheckbox(220, 120, X86.FtoI(lookstrafe.value));

        if (vid_menudrawfn != null)
        {
            M_Print(16, 128, "         Video Options");
        }

        if (modestate == modestate_t.MS_WINDOWED)
        {
            M_Print(16, 136, "             Use Mouse");
            M_DrawCheckbox(220, 136, X86.FtoI(_windowed_mouse.value));
        }

        // cursor
        M_DrawCharacter(200, 32 + (options_cursor * 8), 12 + ((int)(realtime * 4) & 1));
    }

    // menu.c: M_Options_Key
    public void M_Options_Key(int k)
    {
        switch (k)
        {
            case K_ESCAPE:
                M_Menu_Main_f();
                break;

            case K_ENTER:
                m_entersound = true;
                switch (options_cursor)
                {
                    case 0:
                        M_Menu_Keys_f();
                        break;
                    case 1:
                        m_state = m_state_t.m_none;
                        Con_ToggleConsole_f();
                        break;
                    case 2:
                        Cbuf_AddText("exec default.cfg\n");
                        break;
                    case 12:
                        M_Menu_Video_f();
                        break;
                    default:
                        M_AdjustSliders(1);
                        break;
                }

                return;

            case K_UPARROW:
                S_LocalSound("misc/menu1.wav");
                options_cursor--;
                if (options_cursor < 0)
                {
                    options_cursor = OPTIONS_ITEMS - 1;
                }

                break;

            case K_DOWNARROW:
                S_LocalSound("misc/menu1.wav");
                options_cursor++;
                if (options_cursor >= OPTIONS_ITEMS)
                {
                    options_cursor = 0;
                }

                break;

            case K_LEFTARROW:
                M_AdjustSliders(-1);
                break;

            case K_RIGHTARROW:
                M_AdjustSliders(1);
                break;
        }

        if (options_cursor == 12 && vid_menudrawfn == null)
        {
            if (k == K_UPARROW)
            {
                options_cursor = 11;
            }
            else
            {
                options_cursor = 0;
            }
        }

        if ((options_cursor == 13) && (modestate != modestate_t.MS_WINDOWED))
        {
            if (k == K_UPARROW)
            {
                options_cursor = 12;
            }
            else
            {
                options_cursor = 0;
            }
        }
    }

    // =============================================================================
    // KEYS MENU

    private static readonly string[][] bindnames =
    [
        ["+attack", "attack"],
        ["impulse 10", "change weapon"],
        ["+jump", "jump / swim up"],
        ["+forward", "walk forward"],
        ["+back", "backpedal"],
        ["+left", "turn left"],
        ["+right", "turn right"],
        ["+speed", "run"],
        ["+moveleft", "step left"],
        ["+moveright", "step right"],
        ["+strafe", "sidestep"],
        ["+lookup", "look up"],
        ["+lookdown", "look down"],
        ["centerview", "center view"],
        ["+mlook", "mouse look"],
        ["+klook", "keyboard look"],
        ["+moveup", "swim up"],
        ["+movedown", "swim down"],
    ];

    private static readonly int NUMCOMMANDS = bindnames.Length;

    public int keys_cursor;
    public bool bind_grab;

    // menu.c: M_Menu_Keys_f
    public void M_Menu_Keys_f()
    {
        key_dest = keydest_t.key_menu;
        m_state = m_state_t.m_keys;
        m_entersound = true;
    }

    // menu.c: M_FindKeysForCommand
    public void M_FindKeysForCommand(string command, Span<int> twokeys)
    {
        int count;
        int j;
        int l;
        string? b;

        twokeys[0] = twokeys[1] = -1;
        l = command.Length;
        count = 0;

        for (j = 0; j < 256; j++)
        {
            b = keybindings[j];
            if (b == null)
            {
                continue;
            }

            if (Q_strncmp(b, command, l) == 0)
            {
                twokeys[count] = j;
                count++;
                if (count == 2)
                {
                    break;
                }
            }
        }
    }

    // menu.c: M_UnbindCommand
    public void M_UnbindCommand(string command)
    {
        int j;
        int l;
        string? b;

        l = command.Length;

        for (j = 0; j < 256; j++)
        {
            b = keybindings[j];
            if (b == null)
            {
                continue;
            }

            if (Q_strncmp(b, command, l) == 0)
            {
                Key_SetBinding(j, "");
            }
        }
    }

    // menu.c: M_Keys_Draw
    public void M_Keys_Draw()
    {
        int i;
        Span<int> keys = stackalloc int[2];
        string name;
        int x, y;
        QPic p;

        p = Draw_CachePic("gfx/ttl_cstm.lmp");
        M_DrawPic((320 - p.width) / 2, 4, p);

        if (bind_grab)
        {
            M_Print(12, 32, "Press a key or button for this action");
        }
        else
        {
            M_Print(18, 32, "Enter to change, backspace to clear");
        }

        // search for known bindings
        for (i = 0; i < NUMCOMMANDS; i++)
        {
            y = 48 + (8 * i);

            M_Print(16, y, bindnames[i][1]);

            M_FindKeysForCommand(bindnames[i][0], keys);

            if (keys[0] == -1)
            {
                M_Print(140, y, "???");
            }
            else
            {
                name = Key_KeynumToString(keys[0]);
                M_Print(140, y, name);
                x = Q_strlen(name) * 8;
                if (keys[1] != -1)
                {
                    M_Print(140 + x + 8, y, "or");
                    M_Print(140 + x + 32, y, Key_KeynumToString(keys[1]));
                }
            }
        }

        if (bind_grab)
        {
            M_DrawCharacter(130, 48 + (keys_cursor * 8), '=');
        }
        else
        {
            M_DrawCharacter(130, 48 + (keys_cursor * 8), 12 + ((int)(realtime * 4) & 1));
        }
    }

    // menu.c: M_Keys_Key
    public void M_Keys_Key(int k)
    {
        string cmd;
        Span<int> keys = stackalloc int[2];

        if (bind_grab)
        {
            // defining a key
            S_LocalSound("misc/menu1.wav");
            if (k == K_ESCAPE)
            {
                bind_grab = false;
            }
            else if (k != '`')
            {
                cmd = CFormat.Sprintf("bind \"%s\" \"%s\"\n", Key_KeynumToString(k), bindnames[keys_cursor][0]);
                Cbuf_InsertText(cmd);
            }

            bind_grab = false;
            return;
        }

        switch (k)
        {
            case K_ESCAPE:
                M_Menu_Options_f();
                break;

            case K_LEFTARROW:
            case K_UPARROW:
                S_LocalSound("misc/menu1.wav");
                keys_cursor--;
                if (keys_cursor < 0)
                {
                    keys_cursor = NUMCOMMANDS - 1;
                }

                break;

            case K_DOWNARROW:
            case K_RIGHTARROW:
                S_LocalSound("misc/menu1.wav");
                keys_cursor++;
                if (keys_cursor >= NUMCOMMANDS)
                {
                    keys_cursor = 0;
                }

                break;

            case K_ENTER: // go into bind mode
                M_FindKeysForCommand(bindnames[keys_cursor][0], keys);
                S_LocalSound("misc/menu2.wav");
                if (keys[1] != -1)
                {
                    M_UnbindCommand(bindnames[keys_cursor][0]);
                }

                bind_grab = true;
                break;

            case K_BACKSPACE: // delete bindings
            case K_DEL: // delete bindings
                S_LocalSound("misc/menu2.wav");
                M_UnbindCommand(bindnames[keys_cursor][0]);
                break;
        }
    }

    // =============================================================================
    // VIDEO MENU

    // menu.c: M_Menu_Video_f
    public void M_Menu_Video_f()
    {
        key_dest = keydest_t.key_menu;
        m_state = m_state_t.m_video;
        m_entersound = true;
    }

    // menu.c: M_Video_Draw
    public void M_Video_Draw() => vid_menudrawfn!();

    // menu.c: M_Video_Key
    public void M_Video_Key(int key) => vid_menukeyfn!(key);

    // =============================================================================
    // HELP MENU

    public int help_page;
    private const int NUM_HELP_PAGES = 6;

    // menu.c: M_Menu_Help_f
    public void M_Menu_Help_f()
    {
        key_dest = keydest_t.key_menu;
        m_state = m_state_t.m_help;
        m_entersound = true;
        help_page = 0;
    }

    // menu.c: M_Help_Draw
    public void M_Help_Draw() => M_DrawPic(0, 0, Draw_CachePic(va("gfx/help%i.lmp", help_page)));

    // menu.c: M_Help_Key
    public void M_Help_Key(int key)
    {
        switch (key)
        {
            case K_ESCAPE:
                M_Menu_Main_f();
                break;

            case K_UPARROW:
            case K_RIGHTARROW:
                m_entersound = true;
                if (++help_page >= NUM_HELP_PAGES)
                {
                    help_page = 0;
                }

                break;

            case K_DOWNARROW:
            case K_LEFTARROW:
                m_entersound = true;
                if (--help_page < 0)
                {
                    help_page = NUM_HELP_PAGES - 1;
                }

                break;
        }
    }

    // =============================================================================
    // QUIT MENU

    public int msgNumber;
    public m_state_t m_quit_prevstate;
    public bool wasInMenus;

    // menu.c: M_Menu_Quit_f
    public void M_Menu_Quit_f()
    {
        if (m_state == m_state_t.m_quit)
        {
            return;
        }

        wasInMenus = key_dest == keydest_t.key_menu;
        key_dest = keydest_t.key_menu;
        m_quit_prevstate = m_state;
        m_state = m_state_t.m_quit;
        m_entersound = true;
        msgNumber = rand() & 7;
    }

    // menu.c: M_Quit_Key
    public void M_Quit_Key(int key)
    {
        switch (key)
        {
            case K_ESCAPE:
            case 'n':
            case 'N':
                if (wasInMenus)
                {
                    m_state = m_quit_prevstate;
                    m_entersound = true;
                }
                else
                {
                    key_dest = keydest_t.key_game;
                    m_state = m_state_t.m_none;
                }

                break;

            case 'Y':
            case 'y':
                key_dest = keydest_t.key_console;
                Host_Quit_f();
                break;

            default:
                break;
        }
    }

    // menu.c: M_Quit_Draw (WinQuake's: the credits)
    public void M_Quit_Draw()
    {
        if (wasInMenus)
        {
            m_state = m_quit_prevstate;
            m_recursiveDraw = true;
            M_Draw();
            m_state = m_state_t.m_quit;
        }

        M_DrawTextBox(0, 0, 38, 23);
        M_PrintWhite(16, 12, "  Quake version 1.09 by id Software\n\n");
        M_PrintWhite(16, 28, "Programming        Art \n");
        M_Print(16, 36, " John Carmack       Adrian Carmack\n");
        M_Print(16, 44, " Michael Abrash     Kevin Cloud\n");
        M_Print(16, 52, " John Cash          Paul Steed\n");
        M_Print(16, 60, " Dave 'Zoid' Kirsch\n");
        M_PrintWhite(16, 68, "Design             Biz\n");
        M_Print(16, 76, " John Romero        Jay Wilbur\n");
        M_Print(16, 84, " Sandy Petersen     Mike Wilson\n");
        M_Print(16, 92, " American McGee     Donna Jackson\n");
        M_Print(16, 100, " Tim Willits        Todd Hollenshead\n");
        M_PrintWhite(16, 108, "Support            Projects\n");
        M_Print(16, 116, " Barrett Alexander  Shawn Green\n");
        M_PrintWhite(16, 124, "Sound Effects\n");
        M_Print(16, 132, " Trent Reznor and Nine Inch Nails\n\n");
        M_PrintWhite(16, 140, "Quake is a trademark of Id Software,\n");
        M_PrintWhite(16, 148, "inc., (c)1996 Id Software, inc. All\n");
        M_PrintWhite(16, 156, "rights reserved. NIN logo is a\n");
        M_PrintWhite(16, 164, "registered trademark licensed to\n");
        M_PrintWhite(16, 172, "Nothing Interactive, Inc. All rights\n");
        M_PrintWhite(16, 180, "reserved. Press y to exit\n");
    }

    // =============================================================================
    // SERIAL CONFIG MENU (unreachable in WinQuake: no serial driver, so serialAvailable stays false)

    public int serialConfig_cursor;
    private static readonly int[] serialConfig_cursor_table = [48, 64, 80, 96, 112, 132];
    private const int NUM_SERIALCONFIG_CMDS = 6;

    private static readonly int[] ISA_uarts = [0x3f8, 0x2f8, 0x3e8, 0x2e8];
    private static readonly int[] ISA_IRQs = [4, 3, 4, 3];
    private static readonly int[] serialConfig_baudrate = [9600, 14400, 19200, 28800, 38400, 57600];

    public int serialConfig_comport;
    public int serialConfig_irq;
    public int serialConfig_baud;

    /// <summary>char[16]</summary>
    public string serialConfig_phone = "";

    // menu.c: M_Menu_SerialConfig_f
    public void M_Menu_SerialConfig_f()
    {
        int n;
        int port;
        int baudrate;

        key_dest = keydest_t.key_menu;
        m_state = m_state_t.m_serialconfig;
        m_entersound = true;
        if (JoiningGame && SerialConfig)
        {
            serialConfig_cursor = 4;
        }
        else
        {
            serialConfig_cursor = 5;
        }

        GetComPortConfig!(0, out port, out serialConfig_irq, out baudrate, out _);

        // map uart's port to COMx
        for (n = 0; n < 4; n++)
        {
            if (ISA_uarts[n] == port)
            {
                break;
            }
        }

        if (n == 4)
        {
            n = 0;
            serialConfig_irq = 4;
        }

        serialConfig_comport = n + 1;

        // map baudrate to index
        for (n = 0; n < 6; n++)
        {
            if (serialConfig_baudrate[n] == baudrate)
            {
                break;
            }
        }

        if (n == 6)
        {
            n = 5;
        }

        serialConfig_baud = n;

        m_return_onerror = false;
        m_return_reason = "";
    }

    // menu.c: M_SerialConfig_Draw
    public void M_SerialConfig_Draw()
    {
        QPic p;
        int basex;
        string startJoin;
        string directModem;

        M_DrawTransPic(16, 4, Draw_CachePic("gfx/qplaque.lmp"));
        p = Draw_CachePic("gfx/p_multi.lmp");
        basex = (320 - p.width) / 2;
        M_DrawPic(basex, 4, p);

        if (StartingGame)
        {
            startJoin = "New Game";
        }
        else
        {
            startJoin = "Join Game";
        }

        if (SerialConfig)
        {
            directModem = "Modem";
        }
        else
        {
            directModem = "Direct Connect";
        }

        M_Print(basex, 32, va("%s - %s", startJoin, directModem));
        basex += 8;

        M_Print(basex, serialConfig_cursor_table[0], "Port");
        M_DrawTextBox(160, 40, 4, 1);
        M_Print(168, serialConfig_cursor_table[0], va("COM%u", serialConfig_comport));

        M_Print(basex, serialConfig_cursor_table[1], "IRQ");
        M_DrawTextBox(160, serialConfig_cursor_table[1] - 8, 1, 1);
        M_Print(168, serialConfig_cursor_table[1], va("%u", serialConfig_irq));

        M_Print(basex, serialConfig_cursor_table[2], "Baud");
        M_DrawTextBox(160, serialConfig_cursor_table[2] - 8, 5, 1);
        M_Print(168, serialConfig_cursor_table[2], va("%u", serialConfig_baudrate[serialConfig_baud]));

        if (SerialConfig)
        {
            M_Print(basex, serialConfig_cursor_table[3], "Modem Setup...");
            if (JoiningGame)
            {
                M_Print(basex, serialConfig_cursor_table[4], "Phone number");
                M_DrawTextBox(160, serialConfig_cursor_table[4] - 8, 16, 1);
                M_Print(168, serialConfig_cursor_table[4], serialConfig_phone);
            }
        }

        if (JoiningGame)
        {
            M_DrawTextBox(basex, serialConfig_cursor_table[5] - 8, 7, 1);
            M_Print(basex + 8, serialConfig_cursor_table[5], "Connect");
        }
        else
        {
            M_DrawTextBox(basex, serialConfig_cursor_table[5] - 8, 2, 1);
            M_Print(basex + 8, serialConfig_cursor_table[5], "OK");
        }

        M_DrawCharacter(basex - 8, serialConfig_cursor_table[serialConfig_cursor], 12 + (X86.FtoI(realtime * 4) & 1));

        if (serialConfig_cursor == 4)
        {
            M_DrawCharacter(168 + (8 * serialConfig_phone.Length), serialConfig_cursor_table[serialConfig_cursor], 10 + (X86.FtoI(realtime * 4) & 1));
        }

        if (m_return_reason.Length != 0)
        {
            M_PrintWhite(basex, 148, m_return_reason);
        }
    }

    // menu.c: M_SerialConfig_Key
    public void M_SerialConfig_Key(int key)
    {
        int l;

        switch (key)
        {
            case K_ESCAPE:
                M_Menu_Net_f();
                break;

            case K_UPARROW:
                S_LocalSound("misc/menu1.wav");
                serialConfig_cursor--;
                if (serialConfig_cursor < 0)
                {
                    serialConfig_cursor = NUM_SERIALCONFIG_CMDS - 1;
                }

                break;

            case K_DOWNARROW:
                S_LocalSound("misc/menu1.wav");
                serialConfig_cursor++;
                if (serialConfig_cursor >= NUM_SERIALCONFIG_CMDS)
                {
                    serialConfig_cursor = 0;
                }

                break;

            case K_LEFTARROW:
                if (serialConfig_cursor > 2)
                {
                    break;
                }

                S_LocalSound("misc/menu3.wav");

                if (serialConfig_cursor == 0)
                {
                    serialConfig_comport--;
                    if (serialConfig_comport == 0)
                    {
                        serialConfig_comport = 4;
                    }

                    serialConfig_irq = ISA_IRQs[serialConfig_comport - 1];
                }

                if (serialConfig_cursor == 1)
                {
                    serialConfig_irq--;
                    if (serialConfig_irq == 6)
                    {
                        serialConfig_irq = 5;
                    }

                    if (serialConfig_irq == 1)
                    {
                        serialConfig_irq = 7;
                    }
                }

                if (serialConfig_cursor == 2)
                {
                    serialConfig_baud--;
                    if (serialConfig_baud < 0)
                    {
                        serialConfig_baud = 5;
                    }
                }

                break;

            case K_RIGHTARROW:
                if (serialConfig_cursor > 2)
                {
                    break;
                }

                SerialConfigForward();
                break;

            case K_ENTER:
                if (serialConfig_cursor < 3)
                {
                    SerialConfigForward();
                    break;
                }

                m_entersound = true;

                if (serialConfig_cursor == 3)
                {
                    SetComPortConfig!(0, ISA_uarts[serialConfig_comport - 1], serialConfig_irq, serialConfig_baudrate[serialConfig_baud], SerialConfig);

                    M_Menu_ModemConfig_f();
                    break;
                }

                if (serialConfig_cursor == 4)
                {
                    serialConfig_cursor = 5;
                    break;
                }

                // serialConfig_cursor == 5 (OK/CONNECT)
                SetComPortConfig!(0, ISA_uarts[serialConfig_comport - 1], serialConfig_irq, serialConfig_baudrate[serialConfig_baud], SerialConfig);

                M_ConfigureNetSubsystem();

                if (StartingGame)
                {
                    M_Menu_GameOptions_f();
                    break;
                }

                m_return_state = m_state;
                m_return_onerror = true;
                key_dest = keydest_t.key_game;
                m_state = m_state_t.m_none;

                if (SerialConfig)
                {
                    Cbuf_AddText(va("connect \"%s\"\n", serialConfig_phone));
                }
                else
                {
                    Cbuf_AddText("connect\n");
                }

                break;

            case K_BACKSPACE:
                if (serialConfig_cursor == 4)
                {
                    if (serialConfig_phone.Length != 0)
                    {
                        serialConfig_phone = serialConfig_phone[..^1];
                    }
                }

                break;

            default:
                if (key < 32 || key > 127)
                {
                    break;
                }

                if (serialConfig_cursor == 4)
                {
                    l = serialConfig_phone.Length;
                    if (l < 15)
                    {
                        serialConfig_phone += (char)key;
                    }
                }

                break;
        }

        if (DirectConfig && (serialConfig_cursor == 3 || serialConfig_cursor == 4))
        {
            if (key == K_UPARROW)
            {
                serialConfig_cursor = 2;
            }
            else
            {
                serialConfig_cursor = 5;
            }
        }

        if (SerialConfig && StartingGame && serialConfig_cursor == 4)
        {
            if (key == K_UPARROW)
            {
                serialConfig_cursor = 3;
            }
            else
            {
                serialConfig_cursor = 5;
            }
        }
    }

    // menu.c: M_SerialConfig_Key's "forward:" label (the right arrow, and Enter on the first three lines)
    private void SerialConfigForward()
    {
        S_LocalSound("misc/menu3.wav");

        if (serialConfig_cursor == 0)
        {
            serialConfig_comport++;
            if (serialConfig_comport > 4)
            {
                serialConfig_comport = 1;
            }

            serialConfig_irq = ISA_IRQs[serialConfig_comport - 1];
        }

        if (serialConfig_cursor == 1)
        {
            serialConfig_irq++;
            if (serialConfig_irq == 6)
            {
                serialConfig_irq = 7;
            }

            if (serialConfig_irq == 8)
            {
                serialConfig_irq = 2;
            }
        }

        if (serialConfig_cursor == 2)
        {
            serialConfig_baud++;
            if (serialConfig_baud > 5)
            {
                serialConfig_baud = 0;
            }
        }
    }

    // =============================================================================
    // MODEM CONFIG MENU

    public int modemConfig_cursor;
    private static readonly int[] modemConfig_cursor_table = [40, 56, 88, 120, 156];
    private const int NUM_MODEMCONFIG_CMDS = 5;

    public char modemConfig_dialing;

    /// <summary>char[16]</summary>
    public string modemConfig_clear = "";

    /// <summary>char[32]</summary>
    public string modemConfig_init = "";

    /// <summary>char[16]</summary>
    public string modemConfig_hangup = "";

    // menu.c: M_Menu_ModemConfig_f
    public void M_Menu_ModemConfig_f()
    {
        key_dest = keydest_t.key_menu;
        m_state = m_state_t.m_modemconfig;
        m_entersound = true;
        GetModemConfig!(0, out modemConfig_dialing, out modemConfig_clear, out modemConfig_init, out modemConfig_hangup);
    }

    // menu.c: M_ModemConfig_Draw
    public void M_ModemConfig_Draw()
    {
        QPic p;
        int basex;

        M_DrawTransPic(16, 4, Draw_CachePic("gfx/qplaque.lmp"));
        p = Draw_CachePic("gfx/p_multi.lmp");
        basex = (320 - p.width) / 2;
        M_DrawPic(basex, 4, p);
        basex += 8;

        if (modemConfig_dialing == 'P')
        {
            M_Print(basex, modemConfig_cursor_table[0], "Pulse Dialing");
        }
        else
        {
            M_Print(basex, modemConfig_cursor_table[0], "Touch Tone Dialing");
        }

        M_Print(basex, modemConfig_cursor_table[1], "Clear");
        M_DrawTextBox(basex, modemConfig_cursor_table[1] + 4, 16, 1);
        M_Print(basex + 8, modemConfig_cursor_table[1] + 12, modemConfig_clear);
        if (modemConfig_cursor == 1)
        {
            M_DrawCharacter(basex + 8 + (8 * modemConfig_clear.Length), modemConfig_cursor_table[1] + 12, 10 + (X86.FtoI(realtime * 4) & 1));
        }

        M_Print(basex, modemConfig_cursor_table[2], "Init");
        M_DrawTextBox(basex, modemConfig_cursor_table[2] + 4, 30, 1);
        M_Print(basex + 8, modemConfig_cursor_table[2] + 12, modemConfig_init);
        if (modemConfig_cursor == 2)
        {
            M_DrawCharacter(basex + 8 + (8 * modemConfig_init.Length), modemConfig_cursor_table[2] + 12, 10 + (X86.FtoI(realtime * 4) & 1));
        }

        M_Print(basex, modemConfig_cursor_table[3], "Hangup");
        M_DrawTextBox(basex, modemConfig_cursor_table[3] + 4, 16, 1);
        M_Print(basex + 8, modemConfig_cursor_table[3] + 12, modemConfig_hangup);
        if (modemConfig_cursor == 3)
        {
            M_DrawCharacter(basex + 8 + (8 * modemConfig_hangup.Length), modemConfig_cursor_table[3] + 12, 10 + (X86.FtoI(realtime * 4) & 1));
        }

        M_DrawTextBox(basex, modemConfig_cursor_table[4] - 8, 2, 1);
        M_Print(basex + 8, modemConfig_cursor_table[4], "OK");

        M_DrawCharacter(basex - 8, modemConfig_cursor_table[modemConfig_cursor], 12 + (X86.FtoI(realtime * 4) & 1));
    }

    // menu.c: M_ModemConfig_Key
    public void M_ModemConfig_Key(int key)
    {
        switch (key)
        {
            case K_ESCAPE:
                M_Menu_SerialConfig_f();
                break;

            case K_UPARROW:
                S_LocalSound("misc/menu1.wav");
                modemConfig_cursor--;
                if (modemConfig_cursor < 0)
                {
                    modemConfig_cursor = NUM_MODEMCONFIG_CMDS - 1;
                }

                break;

            case K_DOWNARROW:
                S_LocalSound("misc/menu1.wav");
                modemConfig_cursor++;
                if (modemConfig_cursor >= NUM_MODEMCONFIG_CMDS)
                {
                    modemConfig_cursor = 0;
                }

                break;

            case K_LEFTARROW:
            case K_RIGHTARROW:
                if (modemConfig_cursor == 0)
                {
                    if (modemConfig_dialing == 'P')
                    {
                        modemConfig_dialing = 'T';
                    }
                    else
                    {
                        modemConfig_dialing = 'P';
                    }

                    S_LocalSound("misc/menu1.wav");
                }

                break;

            case K_ENTER:
                if (modemConfig_cursor == 0)
                {
                    if (modemConfig_dialing == 'P')
                    {
                        modemConfig_dialing = 'T';
                    }
                    else
                    {
                        modemConfig_dialing = 'P';
                    }

                    m_entersound = true;
                }

                if (modemConfig_cursor == 4)
                {
                    SetModemConfig!(0, va("%c", modemConfig_dialing), modemConfig_clear, modemConfig_init, modemConfig_hangup);
                    m_entersound = true;
                    M_Menu_SerialConfig_f();
                }

                break;

            case K_BACKSPACE:
                if (modemConfig_cursor == 1)
                {
                    if (modemConfig_clear.Length != 0)
                    {
                        modemConfig_clear = modemConfig_clear[..^1];
                    }
                }

                if (modemConfig_cursor == 2)
                {
                    if (modemConfig_init.Length != 0)
                    {
                        modemConfig_init = modemConfig_init[..^1];
                    }
                }

                if (modemConfig_cursor == 3)
                {
                    if (modemConfig_hangup.Length != 0)
                    {
                        modemConfig_hangup = modemConfig_hangup[..^1];
                    }
                }

                break;

            default:
                if (key < 32 || key > 127)
                {
                    break;
                }

                if (modemConfig_cursor == 1 && modemConfig_clear.Length < 15)
                {
                    modemConfig_clear += (char)key;
                }

                if (modemConfig_cursor == 2 && modemConfig_init.Length < 29)
                {
                    modemConfig_init += (char)key;
                }

                if (modemConfig_cursor == 3 && modemConfig_hangup.Length < 15)
                {
                    modemConfig_hangup += (char)key;
                }

                break;
        }
    }

    // =============================================================================
    // LAN CONFIG MENU

    public int lanConfig_cursor = -1;
    private static readonly int[] lanConfig_cursor_table = [72, 92, 124];
    private const int NUM_LANCONFIG_CMDS = 3;

    public int lanConfig_port;

    /// <summary>char[6]</summary>
    public string lanConfig_portname = "";

    /// <summary>char[22]</summary>
    public string lanConfig_joinname = "";

    // menu.c: M_Menu_LanConfig_f
    public void M_Menu_LanConfig_f()
    {
        key_dest = keydest_t.key_menu;
        m_state = m_state_t.m_lanconfig;
        m_entersound = true;
        if (lanConfig_cursor == -1)
        {
            if (JoiningGame && TCPIPConfig)
            {
                lanConfig_cursor = 2;
            }
            else
            {
                lanConfig_cursor = 1;
            }
        }

        if (StartingGame && lanConfig_cursor == 2)
        {
            lanConfig_cursor = 1;
        }

        lanConfig_port = DEFAULTnet_hostport;
        lanConfig_portname = CFormat.Sprintf("%u", lanConfig_port);

        m_return_onerror = false;
        m_return_reason = "";
    }

    // menu.c: M_LanConfig_Draw
    public void M_LanConfig_Draw()
    {
        QPic p;
        int basex;
        string startJoin;
        string protocol;

        M_DrawTransPic(16, 4, Draw_CachePic("gfx/qplaque.lmp"));
        p = Draw_CachePic("gfx/p_multi.lmp");
        basex = (320 - p.width) / 2;
        M_DrawPic(basex, 4, p);

        if (StartingGame)
        {
            startJoin = "New Game";
        }
        else
        {
            startJoin = "Join Game";
        }

        if (IPXConfig)
        {
            protocol = "IPX";
        }
        else
        {
            protocol = "TCP/IP";
        }

        M_Print(basex, 32, va("%s - %s", startJoin, protocol));
        basex += 8;

        M_Print(basex, 52, "Address:");
        if (IPXConfig)
        {
            M_Print(basex + (9 * 8), 52, my_ipx_address);
        }
        else
        {
            M_Print(basex + (9 * 8), 52, my_tcpip_address);
        }

        M_Print(basex, lanConfig_cursor_table[0], "Port");
        M_DrawTextBox(basex + (8 * 8), lanConfig_cursor_table[0] - 8, 6, 1);
        M_Print(basex + (9 * 8), lanConfig_cursor_table[0], lanConfig_portname);

        if (JoiningGame)
        {
            M_Print(basex, lanConfig_cursor_table[1], "Search for local games...");
            M_Print(basex, 108, "Join game at:");
            M_DrawTextBox(basex + 8, lanConfig_cursor_table[2] - 8, 22, 1);
            M_Print(basex + 16, lanConfig_cursor_table[2], lanConfig_joinname);
        }
        else
        {
            M_DrawTextBox(basex, lanConfig_cursor_table[1] - 8, 2, 1);
            M_Print(basex + 8, lanConfig_cursor_table[1], "OK");
        }

        M_DrawCharacter(basex - 8, lanConfig_cursor_table[lanConfig_cursor], 12 + (X86.FtoI(realtime * 4) & 1));

        if (lanConfig_cursor == 0)
        {
            M_DrawCharacter(basex + (9 * 8) + (8 * lanConfig_portname.Length), lanConfig_cursor_table[0], 10 + (X86.FtoI(realtime * 4) & 1));
        }

        if (lanConfig_cursor == 2)
        {
            M_DrawCharacter(basex + 16 + (8 * lanConfig_joinname.Length), lanConfig_cursor_table[2], 10 + (X86.FtoI(realtime * 4) & 1));
        }

        if (m_return_reason.Length != 0)
        {
            M_PrintWhite(basex, 148, m_return_reason);
        }
    }

    // menu.c: M_LanConfig_Key
    public void M_LanConfig_Key(int key)
    {
        int l;

        switch (key)
        {
            case K_ESCAPE:
                M_Menu_Net_f();
                break;

            case K_UPARROW:
                S_LocalSound("misc/menu1.wav");
                lanConfig_cursor--;
                if (lanConfig_cursor < 0)
                {
                    lanConfig_cursor = NUM_LANCONFIG_CMDS - 1;
                }

                break;

            case K_DOWNARROW:
                S_LocalSound("misc/menu1.wav");
                lanConfig_cursor++;
                if (lanConfig_cursor >= NUM_LANCONFIG_CMDS)
                {
                    lanConfig_cursor = 0;
                }

                break;

            case K_ENTER:
                if (lanConfig_cursor == 0)
                {
                    break;
                }

                m_entersound = true;

                M_ConfigureNetSubsystem();

                if (lanConfig_cursor == 1)
                {
                    if (StartingGame)
                    {
                        M_Menu_GameOptions_f();
                        break;
                    }

                    M_Menu_Search_f();
                    break;
                }

                if (lanConfig_cursor == 2)
                {
                    m_return_state = m_state;
                    m_return_onerror = true;
                    key_dest = keydest_t.key_game;
                    m_state = m_state_t.m_none;
                    Cbuf_AddText(va("connect \"%s\"\n", lanConfig_joinname));
                    break;
                }

                break;

            case K_BACKSPACE:
                if (lanConfig_cursor == 0)
                {
                    if (lanConfig_portname.Length != 0)
                    {
                        lanConfig_portname = lanConfig_portname[..^1];
                    }
                }

                if (lanConfig_cursor == 2)
                {
                    if (lanConfig_joinname.Length != 0)
                    {
                        lanConfig_joinname = lanConfig_joinname[..^1];
                    }
                }

                break;

            default:
                if (key < 32 || key > 127)
                {
                    break;
                }

                if (lanConfig_cursor == 2)
                {
                    l = lanConfig_joinname.Length;
                    if (l < 21)
                    {
                        lanConfig_joinname += (char)key;
                    }
                }

                if (key < '0' || key > '9')
                {
                    break;
                }

                if (lanConfig_cursor == 0)
                {
                    l = lanConfig_portname.Length;
                    if (l < 5)
                    {
                        lanConfig_portname += (char)key;
                    }
                }

                break;
        }

        if (StartingGame && lanConfig_cursor == 2)
        {
            if (key == K_UPARROW)
            {
                lanConfig_cursor = 1;
            }
            else
            {
                lanConfig_cursor = 0;
            }
        }

        l = Q_atoi(lanConfig_portname);
        if (l > 65535)
        {
            l = lanConfig_port;
        }
        else
        {
            lanConfig_port = l;
        }

        lanConfig_portname = CFormat.Sprintf("%u", lanConfig_port);
    }

    // =============================================================================
    // GAME OPTIONS MENU

    // menu.c: level_t
    private readonly record struct level_t(string name, string description);

    private static readonly level_t[] levels =
    [
        new("start", "Entrance"), // 0

        new("e1m1", "Slipgate Complex"), // 1
        new("e1m2", "Castle of the Damned"),
        new("e1m3", "The Necropolis"),
        new("e1m4", "The Grisly Grotto"),
        new("e1m5", "Gloom Keep"),
        new("e1m6", "The Door To Chthon"),
        new("e1m7", "The House of Chthon"),
        new("e1m8", "Ziggurat Vertigo"),

        new("e2m1", "The Installation"), // 9
        new("e2m2", "Ogre Citadel"),
        new("e2m3", "Crypt of Decay"),
        new("e2m4", "The Ebon Fortress"),
        new("e2m5", "The Wizard's Manse"),
        new("e2m6", "The Dismal Oubliette"),
        new("e2m7", "Underearth"),

        new("e3m1", "Termination Central"), // 16
        new("e3m2", "The Vaults of Zin"),
        new("e3m3", "The Tomb of Terror"),
        new("e3m4", "Satan's Dark Delight"),
        new("e3m5", "Wind Tunnels"),
        new("e3m6", "Chambers of Torment"),
        new("e3m7", "The Haunted Halls"),

        new("e4m1", "The Sewage System"), // 23
        new("e4m2", "The Tower of Despair"),
        new("e4m3", "The Elder God Shrine"),
        new("e4m4", "The Palace of Hate"),
        new("e4m5", "Hell's Atrium"),
        new("e4m6", "The Pain Maze"),
        new("e4m7", "Azure Agony"),
        new("e4m8", "The Nameless City"),

        new("end", "Shub-Niggurath's Pit"), // 31

        new("dm1", "Place of Two Deaths"), // 32
        new("dm2", "Claustrophobopolis"),
        new("dm3", "The Abandoned Base"),
        new("dm4", "The Bad Place"),
        new("dm5", "The Cistern"),
        new("dm6", "The Dark Zone"),
    ];

    // MED 01/06/97 added hipnotic levels
    private static readonly level_t[] hipnoticlevels =
    [
        new("start", "Command HQ"), // 0

        new("hip1m1", "The Pumping Station"), // 1
        new("hip1m2", "Storage Facility"),
        new("hip1m3", "The Lost Mine"),
        new("hip1m4", "Research Facility"),
        new("hip1m5", "Military Complex"),

        new("hip2m1", "Ancient Realms"), // 6
        new("hip2m2", "The Black Cathedral"),
        new("hip2m3", "The Catacombs"),
        new("hip2m4", "The Crypt"),
        new("hip2m5", "Mortum's Keep"),
        new("hip2m6", "The Gremlin's Domain"),

        new("hip3m1", "Tur Torment"), // 12
        new("hip3m2", "Pandemonium"),
        new("hip3m3", "Limbo"),
        new("hip3m4", "The Gauntlet"),

        new("hipend", "Armagon's Lair"), // 16

        new("hipdm1", "The Edge of Oblivion"), // 17
    ];

    // PGM 01/07/97 added rogue levels
    // PGM 03/02/97 added dmatch level
    private static readonly level_t[] roguelevels =
    [
        new("start", "Split Decision"),
        new("r1m1", "Deviant's Domain"),
        new("r1m2", "Dread Portal"),
        new("r1m3", "Judgement Call"),
        new("r1m4", "Cave of Death"),
        new("r1m5", "Towers of Wrath"),
        new("r1m6", "Temple of Pain"),
        new("r1m7", "Tomb of the Overlord"),
        new("r2m1", "Tempus Fugit"),
        new("r2m2", "Elemental Fury I"),
        new("r2m3", "Elemental Fury II"),
        new("r2m4", "Curse of Osiris"),
        new("r2m5", "Wizard's Keep"),
        new("r2m6", "Blood Sacrifice"),
        new("r2m7", "Last Bastion"),
        new("r2m8", "Source of Evil"),
        new("ctf1", "Division of Change"),
    ];

    // menu.c: episode_t
    private readonly record struct episode_t(string description, int firstLevel, int levels);

    private static readonly episode_t[] episodes =
    [
        new("Welcome to Quake", 0, 1),
        new("Doomed Dimension", 1, 8),
        new("Realm of Black Magic", 9, 7),
        new("Netherworld", 16, 7),
        new("The Elder World", 23, 8),
        new("Final Level", 31, 1),
        new("Deathmatch Arena", 32, 6),
    ];

    // MED 01/06/97  added hipnotic episodes
    private static readonly episode_t[] hipnoticepisodes =
    [
        new("Scourge of Armagon", 0, 1),
        new("Fortress of the Dead", 1, 5),
        new("Dominion of Darkness", 6, 6),
        new("The Rift", 12, 4),
        new("Final Level", 16, 1),
        new("Deathmatch Arena", 17, 1),
    ];

    // PGM 01/07/97 added rogue episodes
    // PGM 03/02/97 added dmatch episode
    private static readonly episode_t[] rogueepisodes =
    [
        new("Introduction", 0, 1),
        new("Hell's Fortress", 1, 7),
        new("Corridors of Time", 8, 8),
        new("Deathmatch Arena", 16, 1),
    ];

    public int startepisode;
    public int startlevel;
    public int maxplayers;
    public bool m_serverInfoMessage;
    public double m_serverInfoMessageTime;

    // menu.c: M_Menu_GameOptions_f
    public void M_Menu_GameOptions_f()
    {
        key_dest = keydest_t.key_menu;
        m_state = m_state_t.m_gameoptions;
        m_entersound = true;
        if (maxplayers == 0)
        {
            maxplayers = svs.maxclients;
        }

        if (maxplayers < 2)
        {
            maxplayers = svs.maxclientslimit;
        }
    }

    private static readonly int[] gameoptions_cursor_table = [40, 56, 64, 72, 80, 88, 96, 112, 120];
    private const int NUM_GAMEOPTIONS = 9;
    public int gameoptions_cursor;

    // menu.c: M_GameOptions_Draw
    public void M_GameOptions_Draw()
    {
        QPic p;
        int x;

        M_DrawTransPic(16, 4, Draw_CachePic("gfx/qplaque.lmp"));
        p = Draw_CachePic("gfx/p_multi.lmp");
        M_DrawPic((320 - p.width) / 2, 4, p);

        M_DrawTextBox(152, 32, 10, 1);
        M_Print(160, 40, "begin game");

        M_Print(0, 56, "      Max players");
        M_Print(160, 56, va("%i", maxplayers));

        M_Print(0, 64, "        Game Type");
        if (coop.value != 0)
        {
            M_Print(160, 64, "Cooperative");
        }
        else
        {
            M_Print(160, 64, "Deathmatch");
        }

        M_Print(0, 72, "        Teamplay");
        if (com_argv.rogue)
        {
            string msg = X86.FtoI(teamplay.value) switch
            {
                1 => "No Friendly Fire",
                2 => "Friendly Fire",
                3 => "Tag",
                4 => "Capture the Flag",
                5 => "One Flag CTF",
                6 => "Three Team CTF",
                _ => "Off",
            };
            M_Print(160, 72, msg);
        }
        else
        {
            string msg = X86.FtoI(teamplay.value) switch
            {
                1 => "No Friendly Fire",
                2 => "Friendly Fire",
                _ => "Off",
            };
            M_Print(160, 72, msg);
        }

        M_Print(0, 80, "            Skill");
        if (skill.value == 0)
        {
            M_Print(160, 80, "Easy difficulty");
        }
        else if (skill.value == 1)
        {
            M_Print(160, 80, "Normal difficulty");
        }
        else if (skill.value == 2)
        {
            M_Print(160, 80, "Hard difficulty");
        }
        else
        {
            M_Print(160, 80, "Nightmare difficulty");
        }

        M_Print(0, 88, "       Frag Limit");
        if (fraglimit.value == 0)
        {
            M_Print(160, 88, "none");
        }
        else
        {
            M_Print(160, 88, va("%i frags", X86.FtoI(fraglimit.value)));
        }

        M_Print(0, 96, "       Time Limit");
        if (timelimit.value == 0)
        {
            M_Print(160, 96, "none");
        }
        else
        {
            M_Print(160, 96, va("%i minutes", X86.FtoI(timelimit.value)));
        }

        M_Print(0, 112, "         Episode");

        // MED 01/06/97 added hipnotic episodes
        if (com_argv.hipnotic)
        {
            M_Print(160, 112, hipnoticepisodes[startepisode].description);
        }

        // PGM 01/07/97 added rogue episodes
        else if (com_argv.rogue)
        {
            M_Print(160, 112, rogueepisodes[startepisode].description);
        }
        else
        {
            M_Print(160, 112, episodes[startepisode].description);
        }

        M_Print(0, 120, "           Level");

        // MED 01/06/97 added hipnotic episodes
        if (com_argv.hipnotic)
        {
            M_Print(160, 120, hipnoticlevels[hipnoticepisodes[startepisode].firstLevel + startlevel].description);
            M_Print(160, 128, hipnoticlevels[hipnoticepisodes[startepisode].firstLevel + startlevel].name);
        }

        // PGM 01/07/97 added rogue episodes
        else if (com_argv.rogue)
        {
            M_Print(160, 120, roguelevels[rogueepisodes[startepisode].firstLevel + startlevel].description);
            M_Print(160, 128, roguelevels[rogueepisodes[startepisode].firstLevel + startlevel].name);
        }
        else
        {
            M_Print(160, 120, levels[episodes[startepisode].firstLevel + startlevel].description);
            M_Print(160, 128, levels[episodes[startepisode].firstLevel + startlevel].name);
        }

        // line cursor
        M_DrawCharacter(144, gameoptions_cursor_table[gameoptions_cursor], 12 + (X86.FtoI(realtime * 4) & 1));

        if (m_serverInfoMessage)
        {
            if (realtime - m_serverInfoMessageTime < 5.0)
            {
                x = (320 - (26 * 8)) / 2;
                M_DrawTextBox(x, 138, 24, 4);
                x += 8;
                M_Print(x, 146, "  More than 4 players   ");
                M_Print(x, 154, " requires using command ");
                M_Print(x, 162, "line parameters; please ");
                M_Print(x, 170, "   see techinfo.txt.    ");
            }
            else
            {
                m_serverInfoMessage = false;
            }
        }
    }

    // menu.c: M_NetStart_Change
    public void M_NetStart_Change(int dir)
    {
        int count;

        switch (gameoptions_cursor)
        {
            case 1:
                maxplayers += dir;
                if (maxplayers > svs.maxclientslimit)
                {
                    maxplayers = svs.maxclientslimit;
                    m_serverInfoMessage = true;
                    m_serverInfoMessageTime = realtime;
                }

                if (maxplayers < 2)
                {
                    maxplayers = 2;
                }

                break;

            case 2:
                Cvar_SetValue("coop", coop.value != 0 ? 0 : 1);
                break;

            case 3:
                if (com_argv.rogue)
                {
                    count = 6;
                }
                else
                {
                    count = 2;
                }

                Cvar_SetValue("teamplay", teamplay.value + dir);
                if (teamplay.value > count)
                {
                    Cvar_SetValue("teamplay", 0);
                }
                else if (teamplay.value < 0)
                {
                    Cvar_SetValue("teamplay", count);
                }

                break;

            case 4:
                Cvar_SetValue("skill", skill.value + dir);
                if (skill.value > 3)
                {
                    Cvar_SetValue("skill", 0);
                }

                if (skill.value < 0)
                {
                    Cvar_SetValue("skill", 3);
                }

                break;

            case 5:
                Cvar_SetValue("fraglimit", fraglimit.value + (dir * 10));
                if (fraglimit.value > 100)
                {
                    Cvar_SetValue("fraglimit", 0);
                }

                if (fraglimit.value < 0)
                {
                    Cvar_SetValue("fraglimit", 100);
                }

                break;

            case 6:
                Cvar_SetValue("timelimit", timelimit.value + (dir * 5));
                if (timelimit.value > 60)
                {
                    Cvar_SetValue("timelimit", 0);
                }

                if (timelimit.value < 0)
                {
                    Cvar_SetValue("timelimit", 60);
                }

                break;

            case 7:
                startepisode += dir;

                // MED 01/06/97 added hipnotic count
                if (com_argv.hipnotic)
                {
                    count = 6;
                }

                // PGM 01/07/97 added rogue count
                // PGM 03/02/97 added 1 for dmatch episode
                else if (com_argv.rogue)
                {
                    count = 4;
                }
                else if (registered.value != 0)
                {
                    count = 7;
                }
                else
                {
                    count = 2;
                }

                if (startepisode < 0)
                {
                    startepisode = count - 1;
                }

                if (startepisode >= count)
                {
                    startepisode = 0;
                }

                startlevel = 0;
                break;

            case 8:
                startlevel += dir;

                // MED 01/06/97 added hipnotic episodes
                if (com_argv.hipnotic)
                {
                    count = hipnoticepisodes[startepisode].levels;
                }

                // PGM 01/06/97 added hipnotic episodes
                else if (com_argv.rogue)
                {
                    count = rogueepisodes[startepisode].levels;
                }
                else
                {
                    count = episodes[startepisode].levels;
                }

                if (startlevel < 0)
                {
                    startlevel = count - 1;
                }

                if (startlevel >= count)
                {
                    startlevel = 0;
                }

                break;
        }
    }

    // menu.c: M_GameOptions_Key
    public void M_GameOptions_Key(int key)
    {
        switch (key)
        {
            case K_ESCAPE:
                M_Menu_Net_f();
                break;

            case K_UPARROW:
                S_LocalSound("misc/menu1.wav");
                gameoptions_cursor--;
                if (gameoptions_cursor < 0)
                {
                    gameoptions_cursor = NUM_GAMEOPTIONS - 1;
                }

                break;

            case K_DOWNARROW:
                S_LocalSound("misc/menu1.wav");
                gameoptions_cursor++;
                if (gameoptions_cursor >= NUM_GAMEOPTIONS)
                {
                    gameoptions_cursor = 0;
                }

                break;

            case K_LEFTARROW:
                if (gameoptions_cursor == 0)
                {
                    break;
                }

                S_LocalSound("misc/menu3.wav");
                M_NetStart_Change(-1);
                break;

            case K_RIGHTARROW:
                if (gameoptions_cursor == 0)
                {
                    break;
                }

                S_LocalSound("misc/menu3.wav");
                M_NetStart_Change(1);
                break;

            case K_ENTER:
                S_LocalSound("misc/menu2.wav");
                if (gameoptions_cursor == 0)
                {
                    if (sv.active)
                    {
                        Cbuf_AddText("disconnect\n");
                    }

                    Cbuf_AddText("listen 0\n"); // so host_netport will be re-examined
                    Cbuf_AddText(va("maxplayers %u\n", maxplayers));
                    SCR_BeginLoadingPlaque();

                    if (com_argv.hipnotic)
                    {
                        Cbuf_AddText(va("map %s\n", hipnoticlevels[hipnoticepisodes[startepisode].firstLevel + startlevel].name));
                    }
                    else if (com_argv.rogue)
                    {
                        Cbuf_AddText(va("map %s\n", roguelevels[rogueepisodes[startepisode].firstLevel + startlevel].name));
                    }
                    else
                    {
                        Cbuf_AddText(va("map %s\n", levels[episodes[startepisode].firstLevel + startlevel].name));
                    }

                    return;
                }

                M_NetStart_Change(1);
                break;
        }
    }

    // =============================================================================
    // SEARCH MENU

    public bool searchComplete;
    public double searchCompleteTime;

    // menu.c: M_Menu_Search_f
    public void M_Menu_Search_f()
    {
        key_dest = keydest_t.key_menu;
        m_state = m_state_t.m_search;
        m_entersound = false;
        slistSilent = true;
        slistLocal = false;
        searchComplete = false;
        NET_Slist_f();
    }

    // menu.c: M_Search_Draw
    public void M_Search_Draw()
    {
        QPic p;
        int x;

        p = Draw_CachePic("gfx/p_multi.lmp");
        M_DrawPic((320 - p.width) / 2, 4, p);
        x = (320 / 2) - (12 * 8 / 2) + 4;
        M_DrawTextBox(x - 8, 32, 12, 1);
        M_Print(x, 40, "Searching...");

        if (slistInProgress)
        {
            NET_Poll();
            return;
        }

        if (!searchComplete)
        {
            searchComplete = true;
            searchCompleteTime = realtime;
        }

        if (hostCacheCount != 0)
        {
            M_Menu_ServerList_f();
            return;
        }

        M_PrintWhite((320 / 2) - (22 * 8 / 2), 64, "No Quake servers found");
        if (realtime - searchCompleteTime < 3.0)
        {
            return;
        }

        M_Menu_LanConfig_f();
    }

    // menu.c: M_Search_Key
    public void M_Search_Key(int key)
    {
    }

    // =============================================================================
    // SLIST MENU

    public int slist_cursor;
    public bool slist_sorted;

    // menu.c: M_Menu_ServerList_f
    public void M_Menu_ServerList_f()
    {
        key_dest = keydest_t.key_menu;
        m_state = m_state_t.m_slist;
        m_entersound = true;
        slist_cursor = 0;
        m_return_onerror = false;
        m_return_reason = "";
        slist_sorted = false;
    }

    // menu.c: M_ServerList_Draw
    public void M_ServerList_Draw()
    {
        int n;
        string @string;
        QPic p;

        if (!slist_sorted)
        {
            if (hostCacheCount > 1)
            {
                int i, j;
                for (i = 0; i < hostCacheCount; i++)
                {
                    for (j = i + 1; j < hostCacheCount; j++)
                    {
                        if (CStrcmp(hostcache[j].name, hostcache[i].name) < 0)
                        {
                            (hostcache[i], hostcache[j]) = (hostcache[j], hostcache[i]);
                        }
                    }
                }
            }

            slist_sorted = true;
        }

        p = Draw_CachePic("gfx/p_multi.lmp");
        M_DrawPic((320 - p.width) / 2, 4, p);
        for (n = 0; n < hostCacheCount; n++)
        {
            if (hostcache[n].maxusers != 0)
            {
                @string = CFormat.Sprintf("%-15.15s %-15.15s %2u/%2u\n", hostcache[n].name, hostcache[n].map, hostcache[n].users, hostcache[n].maxusers);
            }
            else
            {
                @string = CFormat.Sprintf("%-15.15s %-15.15s\n", hostcache[n].name, hostcache[n].map);
            }

            M_Print(16, 32 + (8 * n), @string);
        }

        M_DrawCharacter(0, 32 + (slist_cursor * 8), 12 + (X86.FtoI(realtime * 4) & 1));

        if (m_return_reason.Length != 0)
        {
            M_PrintWhite(16, 148, m_return_reason);
        }
    }

    // menu.c: M_ServerList_Key
    public void M_ServerList_Key(int k)
    {
        switch (k)
        {
            case K_ESCAPE:
                M_Menu_LanConfig_f();
                break;

            case K_SPACE:
                M_Menu_Search_f();
                break;

            case K_UPARROW:
            case K_LEFTARROW:
                S_LocalSound("misc/menu1.wav");
                slist_cursor--;
                if (slist_cursor < 0)
                {
                    slist_cursor = hostCacheCount - 1;
                }

                break;

            case K_DOWNARROW:
            case K_RIGHTARROW:
                S_LocalSound("misc/menu1.wav");
                slist_cursor++;
                if (slist_cursor >= hostCacheCount)
                {
                    slist_cursor = 0;
                }

                break;

            case K_ENTER:
                S_LocalSound("misc/menu2.wav");
                m_return_state = m_state;
                m_return_onerror = true;
                slist_sorted = false;
                key_dest = keydest_t.key_game;
                m_state = m_state_t.m_none;
                Cbuf_AddText(va("connect \"%s\"\n", hostcache[slist_cursor].cname));
                break;

            default:
                break;
        }
    }

    // =============================================================================
    // Menu Subsystem

    // menu.c: M_Init
    public void M_Init()
    {
        Cmd_AddCommand("togglemenu", M_ToggleMenu_f);

        Cmd_AddCommand("menu_main", M_Menu_Main_f);
        Cmd_AddCommand("menu_singleplayer", M_Menu_SinglePlayer_f);
        Cmd_AddCommand("menu_load", M_Menu_Load_f);
        Cmd_AddCommand("menu_save", M_Menu_Save_f);
        Cmd_AddCommand("menu_multiplayer", M_Menu_MultiPlayer_f);
        Cmd_AddCommand("menu_setup", M_Menu_Setup_f);
        Cmd_AddCommand("menu_options", M_Menu_Options_f);
        Cmd_AddCommand("menu_keys", M_Menu_Keys_f);
        Cmd_AddCommand("menu_video", M_Menu_Video_f);
        Cmd_AddCommand("help", M_Menu_Help_f);
        Cmd_AddCommand("menu_quit", M_Menu_Quit_f);
    }

    // menu.c: M_Draw
    public void M_Draw()
    {
        if (m_state == m_state_t.m_none || key_dest != keydest_t.key_menu)
        {
            return;
        }

        if (!m_recursiveDraw)
        {
            scr_copyeverything = 1;

            if (scr_con_current != 0)
            {
                Draw_ConsoleBackground((int)vid.height);
                VID_UnlockBuffer();
                S_ExtraUpdate();
                VID_LockBuffer();
            }
            else
            {
                Draw_FadeScreen();
            }

            scr_fullupdate = 0;
        }
        else
        {
            m_recursiveDraw = false;
        }

        switch (m_state)
        {
            case m_state_t.m_none:
                break;

            case m_state_t.m_main:
                M_Main_Draw();
                break;

            case m_state_t.m_singleplayer:
                M_SinglePlayer_Draw();
                break;

            case m_state_t.m_load:
                M_Load_Draw();
                break;

            case m_state_t.m_save:
                M_Save_Draw();
                break;

            case m_state_t.m_multiplayer:
                M_MultiPlayer_Draw();
                break;

            case m_state_t.m_setup:
                M_Setup_Draw();
                break;

            case m_state_t.m_net:
                M_Net_Draw();
                break;

            case m_state_t.m_options:
                M_Options_Draw();
                break;

            case m_state_t.m_keys:
                M_Keys_Draw();
                break;

            case m_state_t.m_video:
                M_Video_Draw();
                break;

            case m_state_t.m_help:
                M_Help_Draw();
                break;

            case m_state_t.m_quit:
                M_Quit_Draw();
                break;

            case m_state_t.m_serialconfig:
                M_SerialConfig_Draw();
                break;

            case m_state_t.m_modemconfig:
                M_ModemConfig_Draw();
                break;

            case m_state_t.m_lanconfig:
                M_LanConfig_Draw();
                break;

            case m_state_t.m_gameoptions:
                M_GameOptions_Draw();
                break;

            case m_state_t.m_search:
                M_Search_Draw();
                break;

            case m_state_t.m_slist:
                M_ServerList_Draw();
                break;
        }

        if (m_entersound)
        {
            S_LocalSound("misc/menu2.wav");
            m_entersound = false;
        }

        VID_UnlockBuffer();
        S_ExtraUpdate();
        VID_LockBuffer();
    }

    // menu.c: M_Keydown
    public void M_Keydown(int key)
    {
        switch (m_state)
        {
            case m_state_t.m_none:
                return;

            case m_state_t.m_main:
                M_Main_Key(key);
                return;

            case m_state_t.m_singleplayer:
                M_SinglePlayer_Key(key);
                return;

            case m_state_t.m_load:
                M_Load_Key(key);
                return;

            case m_state_t.m_save:
                M_Save_Key(key);
                return;

            case m_state_t.m_multiplayer:
                M_MultiPlayer_Key(key);
                return;

            case m_state_t.m_setup:
                M_Setup_Key(key);
                return;

            case m_state_t.m_net:
                M_Net_Key(key);
                return;

            case m_state_t.m_options:
                M_Options_Key(key);
                return;

            case m_state_t.m_keys:
                M_Keys_Key(key);
                return;

            case m_state_t.m_video:
                M_Video_Key(key);
                return;

            case m_state_t.m_help:
                M_Help_Key(key);
                return;

            case m_state_t.m_quit:
                M_Quit_Key(key);
                return;

            case m_state_t.m_serialconfig:
                M_SerialConfig_Key(key);
                return;

            case m_state_t.m_modemconfig:
                M_ModemConfig_Key(key);
                return;

            case m_state_t.m_lanconfig:
                M_LanConfig_Key(key);
                return;

            case m_state_t.m_gameoptions:
                M_GameOptions_Key(key);
                return;

            case m_state_t.m_search:
                M_Search_Key(key);
                break;

            case m_state_t.m_slist:
                M_ServerList_Key(key);
                return;
        }
    }

    // menu.c: M_ConfigureNetSubsystem
    public void M_ConfigureNetSubsystem()
    {
        // enable/disable net systems to match desired config
        Cbuf_AddText("stopdemo\n");
        if (SerialConfig || DirectConfig)
        {
            Cbuf_AddText("com1 enable\n");
        }

        if (IPXConfig || TCPIPConfig)
        {
            net_hostport = lanConfig_port;
        }
    }
}
