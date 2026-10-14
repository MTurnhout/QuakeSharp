using System.Text;
using QuakeSharp.Core.Client;
using static QuakeSharp.Core.Common.QString;
using static QuakeSharp.Core.UI.Keys;

namespace QuakeSharp.Core;

/// <summary>keys.h: keydest_t, where key events go</summary>
public enum keydest_t
{
    key_game,
    key_console,
    key_message,
    key_menu,
}

// keys.c: key up events are sent even if in console mode
public sealed partial class QuakeEngine
{
    public const int MAXCMDLINE = 256;

    /// <summary>keys.c: key_lines, the console's input line and its history (C strings of up to 255 characters)</summary>
    public readonly byte[][] key_lines = [.. Enumerable.Range(0, 32).Select(_ => new byte[MAXCMDLINE])];

    public int key_linepos;
    public bool shift_down;
    public int key_lastpress;

    public int edit_line;
    public int history_line;

    public keydest_t key_dest;

    /// <summary>incremented every key event</summary>
    public int key_count;

    /// <summary>keys.c: keybindings (null: never bound)</summary>
    public readonly string?[] keybindings = new string?[256];

    /// <summary>if true, can't be rebound while in console</summary>
    public readonly bool[] consolekeys = new bool[256];

    /// <summary>if true, can't be rebound while in menu</summary>
    public readonly bool[] menubound = new bool[256];

    /// <summary>key to map to if shift held down in console</summary>
    public readonly int[] keyshift = new int[256];

    /// <summary>if > 1, it is autorepeating</summary>
    public readonly int[] key_repeats = new int[256];

    public readonly bool[] keydown = new bool[256];

    /// <summary>keys.c: chat_buffer (a C string of up to 31 characters)</summary>
    public readonly byte[] chat_buffer = new byte[32];

    public bool team_message;

    // keys.c: Key_Message's static chat_bufferlen
    private int chat_bufferlen;

    // ==========================================================================
    // LINE TYPING INTO THE CONSOLE
    // ==========================================================================

    // keys.c: Key_Console
    // Interactive line editing and console scrollback
    public void Key_Console(int key)
    {
        string? cmd;

        if (key == K_ENTER)
        {
            Cbuf_AddText(KeyLine(edit_line, 1)); // skip the >
            Cbuf_AddText("\n");
            Con_Printf("%s\n", KeyLine(edit_line, 0));
            edit_line = (edit_line + 1) & 31;
            history_line = edit_line;
            key_lines[edit_line][0] = (byte)']';
            key_linepos = 1;
            if (cls.state == cactive_t.ca_disconnected)
            {
                SCR_UpdateScreen(); // force an update, because the command may take some time
            }

            return;
        }

        if (key == K_TAB)
        {
            // command completion
            cmd = Cmd_CompleteCommand(KeyLine(edit_line, 1));
            if (cmd == null)
            {
                cmd = Cvar_CompleteVariable(KeyLine(edit_line, 1));
            }

            if (cmd != null)
            {
                SetKeyLine(edit_line, 1, cmd);
                key_linepos = Q_strlen(cmd) + 1;
                key_lines[edit_line][key_linepos] = (byte)' ';
                key_linepos++;
                key_lines[edit_line][key_linepos] = 0;
                return;
            }
        }

        if (key == K_BACKSPACE || key == K_LEFTARROW)
        {
            if (key_linepos > 1)
            {
                key_linepos--;
            }

            return;
        }

        if (key == K_UPARROW)
        {
            do
            {
                history_line = (history_line - 1) & 31;
            }
            while (history_line != edit_line && key_lines[history_line][1] == 0);

            if (history_line == edit_line)
            {
                history_line = (edit_line + 1) & 31;
            }

            SetKeyLine(edit_line, 0, KeyLine(history_line, 0));
            key_linepos = Q_strlen(KeyLine(edit_line, 0));
            return;
        }

        if (key == K_DOWNARROW)
        {
            if (history_line == edit_line)
            {
                return;
            }

            do
            {
                history_line = (history_line + 1) & 31;
            }
            while (history_line != edit_line && key_lines[history_line][1] == 0);

            if (history_line == edit_line)
            {
                key_lines[edit_line][0] = (byte)']';
                key_linepos = 1;
            }
            else
            {
                SetKeyLine(edit_line, 0, KeyLine(history_line, 0));
                key_linepos = Q_strlen(KeyLine(edit_line, 0));
            }

            return;
        }

        if (key == K_PGUP || key == K_MWHEELUP)
        {
            con_backscroll += 2;
            if (con_backscroll > con_totallines - (int)(vid.height >> 3) - 1)
            {
                con_backscroll = con_totallines - (int)(vid.height >> 3) - 1;
            }

            return;
        }

        if (key == K_PGDN || key == K_MWHEELDOWN)
        {
            con_backscroll -= 2;
            if (con_backscroll < 0)
            {
                con_backscroll = 0;
            }

            return;
        }

        if (key == K_HOME)
        {
            con_backscroll = con_totallines - (int)(vid.height >> 3) - 1;
            return;
        }

        if (key == K_END)
        {
            con_backscroll = 0;
            return;
        }

        if (key is < 32 or > 127)
        {
            return; // non printable
        }

        if (key_linepos < MAXCMDLINE - 1)
        {
            key_lines[edit_line][key_linepos] = (byte)key;
            key_linepos++;
            key_lines[edit_line][key_linepos] = 0;
        }
    }

    // ==========================================================================

    // keys.c: Key_Message
    public void Key_Message(int key)
    {
        if (key == K_ENTER)
        {
            if (team_message)
            {
                Cbuf_AddText("say_team \"");
            }
            else
            {
                Cbuf_AddText("say \"");
            }

            Cbuf_AddText(CStringOf(chat_buffer, 0));
            Cbuf_AddText("\"\n");

            key_dest = keydest_t.key_game;
            chat_bufferlen = 0;
            chat_buffer[0] = 0;
            return;
        }

        if (key == K_ESCAPE)
        {
            key_dest = keydest_t.key_game;
            chat_bufferlen = 0;
            chat_buffer[0] = 0;
            return;
        }

        if (key is < 32 or > 127)
        {
            return; // non printable
        }

        if (key == K_BACKSPACE)
        {
            if (chat_bufferlen != 0)
            {
                chat_bufferlen--;
                chat_buffer[chat_bufferlen] = 0;
            }

            return;
        }

        if (chat_bufferlen == 31)
        {
            return; // all full
        }

        chat_buffer[chat_bufferlen++] = (byte)key;
        chat_buffer[chat_bufferlen] = 0;
    }

    // ==========================================================================

    // keys.c: Key_StringToKeynum
    // Returns a key number to be used to index keybindings[] by looking at the given string. Single ascii characters
    // return themselves, while the K_* names are matched up.
    public static int Key_StringToKeynum(string? str)
    {
        if (str == null || Ch(str, 0) == 0)
        {
            return -1;
        }

        if (Ch(str, 1) == 0)
        {
            return Ch(str, 0);
        }

        foreach ((string name, int keynum) in keynames)
        {
            if (Q_strcasecmp(str, name) == 0)
            {
                return keynum;
            }
        }

        return -1;
    }

    // keys.c: Key_KeynumToString
    // Returns a string (either a single ascii char, or a K_* name) for the given keynum.
    public static string Key_KeynumToString(int keynum)
    {
        if (keynum == -1)
        {
            return "<KEY NOT FOUND>";
        }

        if (keynum is > 32 and < 127)
        {
            // printable ascii
            return ((char)keynum).ToString();
        }

        foreach ((string name, int number) in keynames)
        {
            if (keynum == number)
            {
                return name;
            }
        }

        return "<UNKNOWN KEYNUM>";
    }

    // keys.c: Key_SetBinding. A one-character key name of 128 or over is a negative signed char: the C writes before
    // keybindings[]; here it binds nothing.
    public void Key_SetBinding(int keynum, string binding)
    {
        if (keynum < 0)
        {
            return;
        }

        // allocate memory for new binding
        keybindings[keynum] = binding[..Q_strlen(binding)];
    }

    // keys.c: Key_Unbind_f
    private void Key_Unbind_f()
    {
        int b;

        if (Cmd_Argc() != 2)
        {
            Con_Printf("unbind <key> : remove commands from a key\n");
            return;
        }

        b = Key_StringToKeynum(Cmd_Argv(1));
        if (b == -1)
        {
            Con_Printf("\"%s\" isn't a valid key\n", Cmd_Argv(1));
            return;
        }

        Key_SetBinding(b, "");
    }

    // keys.c: Key_Unbindall_f
    private void Key_Unbindall_f()
    {
        for (int i = 0; i < 256; i++)
        {
            if (keybindings[i] != null)
            {
                Key_SetBinding(i, "");
            }
        }
    }

    // keys.c: Key_Bind_f
    private void Key_Bind_f()
    {
        int i, c, b;

        c = Cmd_Argc();

        if (c != 2 && c != 3)
        {
            Con_Printf("bind <key> [command] : attach a command to a key\n");
            return;
        }

        b = Key_StringToKeynum(Cmd_Argv(1));
        if (b == -1)
        {
            Con_Printf("\"%s\" isn't a valid key\n", Cmd_Argv(1));
            return;
        }

        if (c == 2)
        {
            // (a negative key number reads before keybindings[] in C: nothing is bound there here)
            string? binding = b >= 0 ? keybindings[b] : null;
            if (binding != null)
            {
                Con_Printf("\"%s\" = \"%s\"\n", Cmd_Argv(1), binding);
            }
            else
            {
                Con_Printf("\"%s\" is not bound\n", Cmd_Argv(1));
            }

            return;
        }

        // copy the rest of the command line
        var cmd = new StringBuilder(); // start out with a null string
        for (i = 2; i < c; i++)
        {
            if (i > 2)
            {
                cmd.Append(' ');
            }

            cmd.Append(Cmd_Argv(i));
        }

        Key_SetBinding(b, cmd.ToString());
    }

    // keys.c: Key_WriteBindings
    // Writes lines containing "bind key value"
    public void Key_WriteBindings(StringBuilder f)
    {
        for (int i = 0; i < 256; i++)
        {
            if (keybindings[i] is string binding && binding.Length != 0)
            {
                f.Append(va("bind \"%s\" \"%s\"\n", Key_KeynumToString(i), binding));
            }
        }
    }

    // keys.c: Key_Init
    public void Key_Init()
    {
        int i;

        for (i = 0; i < 32; i++)
        {
            key_lines[i][0] = (byte)']';
            key_lines[i][1] = 0;
        }

        key_linepos = 1;

        // init ascii characters in console mode
        for (i = 32; i < 128; i++)
        {
            consolekeys[i] = true;
        }

        consolekeys[K_ENTER] = true;
        consolekeys[K_TAB] = true;
        consolekeys[K_LEFTARROW] = true;
        consolekeys[K_RIGHTARROW] = true;
        consolekeys[K_UPARROW] = true;
        consolekeys[K_DOWNARROW] = true;
        consolekeys[K_BACKSPACE] = true;
        consolekeys[K_PGUP] = true;
        consolekeys[K_PGDN] = true;
        consolekeys[K_SHIFT] = true;
        consolekeys[K_MWHEELUP] = true;
        consolekeys[K_MWHEELDOWN] = true;
        consolekeys['`'] = false;
        consolekeys['~'] = false;

        for (i = 0; i < 256; i++)
        {
            keyshift[i] = i;
        }

        for (i = 'a'; i <= 'z'; i++)
        {
            keyshift[i] = i - 'a' + 'A';
        }

        keyshift['1'] = '!';
        keyshift['2'] = '@';
        keyshift['3'] = '#';
        keyshift['4'] = '$';
        keyshift['5'] = '%';
        keyshift['6'] = '^';
        keyshift['7'] = '&';
        keyshift['8'] = '*';
        keyshift['9'] = '(';
        keyshift['0'] = ')';
        keyshift['-'] = '_';
        keyshift['='] = '+';
        keyshift[','] = '<';
        keyshift['.'] = '>';
        keyshift['/'] = '?';
        keyshift[';'] = ':';
        keyshift['\''] = '"';
        keyshift['['] = '{';
        keyshift[']'] = '}';
        keyshift['`'] = '~';
        keyshift['\\'] = '|';

        menubound[K_ESCAPE] = true;
        for (i = 0; i < 12; i++)
        {
            menubound[K_F1 + i] = true;
        }

        // register our functions
        Cmd_AddCommand("bind", Key_Bind_f);
        Cmd_AddCommand("unbind", Key_Unbind_f);
        Cmd_AddCommand("unbindall", Key_Unbindall_f);
    }

    // keys.c: Key_Event
    // Called by the system between frames for both key up and key down events. Should NOT be called during an
    // interrupt!
    public void Key_Event(int key, bool down)
    {
        string? kb;
        string cmd;

        keydown[key] = down;

        if (!down)
        {
            key_repeats[key] = 0;
        }

        key_lastpress = key;
        key_count++;
        if (key_count <= 0)
        {
            return; // just catching keys for Con_NotifyBox
        }

        // update auto-repeat status
        if (down)
        {
            key_repeats[key]++;
            if (key != K_BACKSPACE && key != K_PAUSE && key_repeats[key] > 1)
            {
                return; // ignore most autorepeats
            }

            if (key >= 200 && KeyBinding(key) == null)
            {
                Con_Printf("%s is unbound, hit F4 to set.\n", Key_KeynumToString(key));
            }
        }

        if (key == K_SHIFT)
        {
            shift_down = down;
        }

        // handle escape specialy, so the user can never unbind it
        if (key == K_ESCAPE)
        {
            if (!down)
            {
                return;
            }

            switch (key_dest)
            {
                case keydest_t.key_message:
                    Key_Message(key);
                    break;
                case keydest_t.key_menu:
                    M_Keydown(key);
                    break;
                case keydest_t.key_game:
                case keydest_t.key_console:
                    M_ToggleMenu_f();
                    break;
                default:
                    throw Sys_Error("Bad key_dest");
            }

            return;
        }

        // key up events only generate commands if the game key binding is a button command (leading + sign). These
        // will occur even in console mode, to keep the character from continuing an action started before a console
        // switch. Button commands include the kenum as a parameter, so multiple downs can be matched with ups
        if (!down)
        {
            kb = KeyBinding(key);
            if (kb != null && Ch(kb, 0) == '+')
            {
                cmd = va("-%s %i\n", kb[1..], key);
                Cbuf_AddText(cmd);
            }

            if (keyshift[key] != key)
            {
                kb = keybindings[keyshift[key]];
                if (kb != null && Ch(kb, 0) == '+')
                {
                    cmd = va("-%s %i\n", kb[1..], key);
                    Cbuf_AddText(cmd);
                }
            }

            return;
        }

        // during demo playback, most keys bring up the main menu
        if (cls.demoplayback && down && consolekeys[key] && key_dest == keydest_t.key_game)
        {
            M_ToggleMenu_f();
            return;
        }

        // if not a consolekey, send to the interpreter no matter what mode is
        if ((key_dest == keydest_t.key_menu && menubound[key])
            || (key_dest == keydest_t.key_console && !consolekeys[key])
            || (key_dest == keydest_t.key_game && (!con_forcedup || !consolekeys[key])))
        {
            kb = KeyBinding(key);
            if (kb != null)
            {
                if (Ch(kb, 0) == '+')
                {
                    // button commands add keynum as a parm
                    cmd = va("%s %i\n", kb, key);
                    Cbuf_AddText(cmd);
                }
                else
                {
                    Cbuf_AddText(kb);
                    Cbuf_AddText("\n");
                }
            }

            return;
        }

        if (!down)
        {
            return; // other systems only care about key down events
        }

        if (shift_down)
        {
            key = keyshift[key];
        }

        switch (key_dest)
        {
            case keydest_t.key_message:
                Key_Message(key);
                break;
            case keydest_t.key_menu:
                M_Keydown(key);
                break;

            case keydest_t.key_game:
            case keydest_t.key_console:
                Key_Console(key);
                break;
            default:
                throw Sys_Error("Bad key_dest");
        }
    }

    // keys.c: Key_ClearStates
    public void Key_ClearStates()
    {
        for (int i = 0; i < 256; i++)
        {
            keydown[i] = false;
            key_repeats[i] = 0;
        }
    }

    // key_lines[line] + offset, as a C string
    private string KeyLine(int line, int offset) => CStringOf(key_lines[line], offset);

    // strcpy (key_lines[line] + offset, text)
    private void SetKeyLine(int line, int offset, string text)
    {
        byte[] bytes = CString(text);
        bytes.CopyTo(key_lines[line], offset);
    }

    /// <summary>The C string in a byte buffer from an offset: up to its first 0.</summary>
    public static string CStringOf(byte[] buffer, int offset)
    {
        int end = Array.IndexOf(buffer, (byte)0, offset);
        return Encoding.Latin1.GetString(buffer, offset, (end < 0 ? buffer.Length : end) - offset);
    }
}
