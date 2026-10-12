using System.Text;
using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using static QuakeSharp.Core.Common.QString;

namespace QuakeSharp.Core;

// console.c: the console's text and its drawing
public sealed partial class QuakeEngine
{
    public const int CON_TEXTSIZE = 16384;
    private const int NUM_CON_TIMES = 4;

    // client.h: SIGNONS, signon messages to receive before connected
    public const int SIGNONS = 4;

    public int con_linewidth;

    public float con_cursorspeed = 4;

    /// <summary>because no entities to refresh</summary>
    public bool con_forcedup;

    /// <summary>total lines in console scrollback</summary>
    public int con_totallines;

    /// <summary>lines up from bottom to display</summary>
    public int con_backscroll;

    /// <summary>where next message will be printed</summary>
    public int con_current;

    /// <summary>offset in current line for next print</summary>
    public int con_x;

    /// <summary>console.c: con_text, CON_TEXTSIZE characters, con_totallines lines of con_linewidth</summary>
    public byte[] con_text = [];

    /// <summary>seconds</summary>
    public readonly Cvar con_notifytime = new("con_notifytime", "3");

    /// <summary>realtime time the line was generated for transparent notify lines</summary>
    public readonly float[] con_times = new float[NUM_CON_TIMES];

    public int con_vislines;

    public bool con_debuglog;

    public bool con_initialized;

    /// <summary>scan lines to clear for notify lines</summary>
    public int con_notifylines;

    // console.c: Con_Print's static cr
    private bool con_cr;

    // console.c: Con_Printf's static inupdate
    private bool con_inupdate;

    // console.c: Con_ToggleConsole_f
    public void Con_ToggleConsole_f()
    {
        if (key_dest == keydest_t.key_console)
        {
            if (cls.state == cactive_t.ca_connected)
            {
                key_dest = keydest_t.key_game;
                key_lines[edit_line][1] = 0; // clear any typing
                key_linepos = 1;
            }
            else
            {
                M_Menu_Main_f();
            }
        }
        else
        {
            key_dest = keydest_t.key_console;
        }

        SCR_EndLoadingPlaque();
        Array.Clear(con_times);
    }

    // console.c: Con_Clear_f
    public void Con_Clear_f()
    {
        if (con_text.Length != 0)
        {
            con_text.AsSpan().Fill((byte)' ');
        }
    }

    // console.c: Con_ClearNotify
    public void Con_ClearNotify()
    {
        for (int i = 0; i < NUM_CON_TIMES; i++)
        {
            con_times[i] = 0;
        }
    }

    // console.c: Con_MessageMode_f
    public void Con_MessageMode_f()
    {
        key_dest = keydest_t.key_message;
        team_message = false;
    }

    // console.c: Con_MessageMode2_f
    public void Con_MessageMode2_f()
    {
        key_dest = keydest_t.key_message;
        team_message = true;
    }

    // console.c: Con_CheckResize
    // If the line width has changed, reformat the buffer.
    public void Con_CheckResize()
    {
        int i, j, width, oldwidth, oldtotallines, numlines, numchars;

        width = (int)(vid.width >> 3) - 2;

        if (width == con_linewidth)
        {
            return;
        }

        if (width < 1)
        {
            // video hasn't been initialized yet
            width = 38;
            con_linewidth = width;
            con_totallines = CON_TEXTSIZE / con_linewidth;
            con_text.AsSpan().Fill((byte)' ');
        }
        else
        {
            oldwidth = con_linewidth;
            con_linewidth = width;
            oldtotallines = con_totallines;
            con_totallines = CON_TEXTSIZE / con_linewidth;
            numlines = oldtotallines;

            if (con_totallines < numlines)
            {
                numlines = con_totallines;
            }

            numchars = oldwidth;

            if (con_linewidth < numchars)
            {
                numchars = con_linewidth;
            }

            byte[] tbuf = (byte[])con_text.Clone();
            con_text.AsSpan().Fill((byte)' ');

            for (i = 0; i < numlines; i++)
            {
                for (j = 0; j < numchars; j++)
                {
                    con_text[((con_totallines - 1 - i) * con_linewidth) + j] =
                        tbuf[(((con_current - i + oldtotallines) % oldtotallines) * oldwidth) + j];
                }
            }

            Con_ClearNotify();
        }

        con_backscroll = 0;
        con_current = con_totallines - 1;
    }

    // console.c: Con_Init
    public void Con_Init()
    {
        const int MAXGAMEDIRLEN = 1000;
        const string t2 = "/qconsole.log";

        con_debuglog = COM_CheckParm("-condebug") != 0;

        if (con_debuglog)
        {
            if (com_gamedir.Length < MAXGAMEDIRLEN - t2.Length)
            {
                File.Delete(fileSystem.WritePath(t2[1..]));
            }
        }

        con_text = new byte[CON_TEXTSIZE];
        con_text.AsSpan().Fill((byte)' ');
        con_linewidth = -1;
        Con_CheckResize();

        Con_Printf("Console initialized.\n");

        // register our commands
        Cvar_RegisterVariable(con_notifytime);

        Cmd_AddCommand("toggleconsole", Con_ToggleConsole_f);
        Cmd_AddCommand("messagemode", Con_MessageMode_f);
        Cmd_AddCommand("messagemode2", Con_MessageMode2_f);
        Cmd_AddCommand("clear", Con_Clear_f);
        con_initialized = true;
    }

    // console.c: Con_Linefeed
    public void Con_Linefeed()
    {
        con_x = 0;
        con_current++;
        con_text.AsSpan(con_current % con_totallines * con_linewidth, con_linewidth).Fill((byte)' ');
    }

    // console.c: Con_Print
    // Handles cursor positioning, line wrapping, etc. All console printing must go through this in order to be logged
    // to disk. If no console is visible, the notify window will pop up.
    public void Con_Print(string txt)
    {
        int y;
        int c, l;
        int mask;
        int p = 0;

        con_backscroll = 0;

        if (Ch(txt, 0) == 1)
        {
            mask = 128; // go to colored text
            S_LocalSound("misc/talk.wav");

            // play talk wav
            p++;
        }
        else if (Ch(txt, 0) == 2)
        {
            mask = 128; // go to colored text
            p++;
        }
        else
        {
            mask = 0;
        }

        while ((c = Ch(txt, p)) != 0)
        {
            // count word length (characters of 128 and over are signed chars below ' ': they end a word)
            for (l = 0; l < con_linewidth; l++)
            {
                if (Ch(txt, p + l) <= ' ')
                {
                    break;
                }
            }

            // word wrap
            if (l != con_linewidth && (con_x + l > con_linewidth))
            {
                con_x = 0;
            }

            p++;

            if (con_cr)
            {
                con_current--;
                con_cr = false;
            }

            if (con_x == 0)
            {
                Con_Linefeed();

                // mark time for transparent overlay
                if (con_current >= 0)
                {
                    con_times[con_current % NUM_CON_TIMES] = (float)realtime;
                }
            }

            switch (c)
            {
                case '\n':
                    con_x = 0;
                    break;

                case '\r':
                    con_x = 0;
                    con_cr = true;
                    break;

                default: // display character and advance
                    y = con_current % con_totallines;
                    con_text[(y * con_linewidth) + con_x] = (byte)(c | mask);
                    con_x++;
                    if (con_x >= con_linewidth)
                    {
                        con_x = 0;
                    }

                    break;
            }
        }
    }

    // console.c: Con_DebugLog
    public static void Con_DebugLog(string file, string fmt, params ReadOnlySpan<PrintfArg> args)
    {
        try
        {
            File.AppendAllText(file, CFormat.Sprintf(fmt, args), Encoding.Latin1);
        }
        catch (IOException)
        {
            // as the C's open and write, which fail silently
        }
    }

    // console.c: Con_Printf
    // Handles cursor positioning, line wrapping, etc
    public void Con_Printf(string fmt, params ReadOnlySpan<PrintfArg> args)
    {
        string msg = CFormat.Sprintf(fmt, args);

        // also echo to debugging console
        Sys_Printf("%s", msg);

        // log all messages to file
        if (con_debuglog)
        {
            Con_DebugLog(fileSystem.WritePath("qconsole.log"), "%s", msg);
        }

        if (!con_initialized)
        {
            return;
        }

        if (cls.state == cactive_t.ca_dedicated)
        {
            return; // no graphics mode
        }

        // write it to the scrollable buffer
        Con_Print(msg);

        // update the screen if the console is displayed
        if (cls.signon != SIGNONS && !scr_disabled_for_loading)
        {
            // protect against infinite loop if something in SCR_UpdateScreen calls Con_Printf
            if (!con_inupdate)
            {
                con_inupdate = true;
                SCR_UpdateScreen();
                con_inupdate = false;
            }
        }
    }

    // console.c: Con_DPrintf
    // A Con_Printf that only shows up if the "developer" cvar is set
    public void Con_DPrintf(string fmt, params ReadOnlySpan<PrintfArg> args)
    {
        if (developer.value == 0)
        {
            return; // don't confuse non-developers with techie stuff...
        }

        Con_Printf("%s", CFormat.Sprintf(fmt, args));
    }

    // console.c: Con_SafePrintf
    // Okay to call even when the screen can't be updated
    public void Con_SafePrintf(string fmt, params ReadOnlySpan<PrintfArg> args)
    {
        string msg = CFormat.Sprintf(fmt, args);
        bool temp = scr_disabled_for_loading;
        scr_disabled_for_loading = true;
        Con_Printf("%s", msg);
        scr_disabled_for_loading = temp;
    }

    // ==============================================================================
    // DRAWING
    // ==============================================================================

    // console.c: Con_DrawInput
    // The input line scrolls horizontally if typing goes beyond the right edge
    public void Con_DrawInput()
    {
        int i;
        byte[] text;
        int start = 0;

        if (key_dest != keydest_t.key_console && !con_forcedup)
        {
            return; // don't draw anything
        }

        text = key_lines[edit_line];

        // add the cursor frame
        text[key_linepos] = (byte)(10 + ((int)(realtime * con_cursorspeed) & 1));

        // fill out remainder with spaces
        for (i = key_linepos + 1; i < con_linewidth; i++)
        {
            text[i] = (byte)' ';
        }

        // prestep if horizontally scrolling
        if (key_linepos >= con_linewidth)
        {
            start += 1 + key_linepos - con_linewidth;
        }

        // draw it
        for (i = 0; i < con_linewidth; i++)
        {
            Draw_Character((i + 1) << 3, con_vislines - 16, (sbyte)text[start + i]);
        }

        // remove cursor
        key_lines[edit_line][key_linepos] = 0;
    }

    // console.c: Con_DrawNotify
    // Draws the last few lines of output transparently over the game top
    public void Con_DrawNotify()
    {
        int x, v;
        int text;
        int i;
        float time;

        v = 0;
        for (i = con_current - NUM_CON_TIMES + 1; i <= con_current; i++)
        {
            if (i < 0)
            {
                continue;
            }

            time = con_times[i % NUM_CON_TIMES];
            if (time == 0)
            {
                continue;
            }

            time = (float)(realtime - time);
            if (time > con_notifytime.value)
            {
                continue;
            }

            text = i % con_totallines * con_linewidth;

            clearnotify = 0;
            scr_copytop = 1;

            for (x = 0; x < con_linewidth; x++)
            {
                Draw_Character((x + 1) << 3, v, (sbyte)con_text[text + x]);
            }

            v += 8;
        }

        if (key_dest == keydest_t.key_message)
        {
            clearnotify = 0;
            scr_copytop = 1;

            x = 0;

            Draw_String(8, v, "say:");
            while (chat_buffer[x] != 0)
            {
                Draw_Character((x + 5) << 3, v, (sbyte)chat_buffer[x]);
                x++;
            }

            Draw_Character((x + 5) << 3, v, 10 + ((int)(realtime * con_cursorspeed) & 1));
            v += 8;
        }

        if (v > con_notifylines)
        {
            con_notifylines = v;
        }
    }

    // console.c: Con_DrawConsole
    // Draws the console with the solid background. The typing input line at the bottom should only be drawn if typing
    // is allowed
    public void Con_DrawConsole(int lines, bool drawinput)
    {
        int i, x, y;
        int rows;
        int text;
        int j;

        if (lines <= 0)
        {
            return;
        }

        // draw the background
        Draw_ConsoleBackground(lines);

        // draw the text
        con_vislines = lines;

        rows = (lines - 16) >> 3; // rows of text to draw
        y = lines - 16 - (rows << 3); // may start slightly negative

        for (i = con_current - rows + 1; i <= con_current; i++, y += 8)
        {
            j = i - con_backscroll;
            if (j < 0)
            {
                j = 0;
            }

            text = j % con_totallines * con_linewidth;

            for (x = 0; x < con_linewidth; x++)
            {
                Draw_Character((x + 1) << 3, y, (sbyte)con_text[text + x]);
            }
        }

        // draw the input prompt, user text, and cursor if desired
        if (drawinput)
        {
            Con_DrawInput();
        }
    }
}
