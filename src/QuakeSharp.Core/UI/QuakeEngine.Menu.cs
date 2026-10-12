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

// menu.c: the menus. The network menus (net, serial and modem configuration, LAN configuration, game options, the
// server search and list) are stand-ins until milestone 7's step 8 (QuakeEngine.Pending.cs).
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

            // fscanf (f, "%i\n", &version); fscanf (f, "%79s\n", name);
            string text = Encoding.Latin1.GetString(f);
            int p = 0;
            ScanToken(text, ref p, int.MaxValue);
            name = ScanToken(text, ref p, 79);
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

        // fscanf's %s: white space skipped, then up to width characters that are not white space
        static string ScanToken(string text, ref int p, int width)
        {
            while (p < text.Length && text[p] is ' ' or '\t' or '\n' or '\r' or '\v' or '\f')
            {
                p++;
            }

            int start = p;
            while (p < text.Length && p - start < width && text[p] is not (' ' or '\t' or '\n' or '\r' or '\v' or '\f'))
            {
                p++;
            }

            return text[start..p];
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
}
