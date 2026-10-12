using System.Text;
using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using static QuakeSharp.Core.Protocol;
using static QuakeSharp.Core.Common.QString;

namespace QuakeSharp.Core;

/// <summary>cmd.h: cmd_source_t, where a command came from.</summary>
public enum cmd_source_t
{
    /// <summary>came in over a net connection as a clc_stringcmd; host_client will be valid during this state</summary>
    src_client,

    /// <summary>from the command buffer</summary>
    src_command,
}

// cmd.c: Quake script command processing module
public sealed partial class QuakeEngine
{
    private const int MAX_ALIAS_NAME = 32;
    private const int MAX_ARGS = 80;

    /// <summary>cmd.c: cmd_alias, the aliases, newest first</summary>
    internal CmdAlias? cmd_alias;

    /// <summary>cmd.c: cmd_wait</summary>
    private bool cmd_wait;

    // cmd.c: Cbuf_Execute's line buffer, char line[1024]
    private byte[] cbuf_line = new byte[1024];

    /// <summary>cmd.c: cmd_text, the command buffer (8 KB)</summary>
    public SizeBuf cmd_text = null!;

    private int cmd_argc;
    private readonly string[] cmd_argv = new string[MAX_ARGS];
    private string? cmd_args;

    /// <summary>cmd.c: cmd_source</summary>
    public cmd_source_t cmd_source;

    /// <summary>cmd.c: cmd_functions, the commands, newest first</summary>
    private CmdFunction? cmd_functions;

    // cmd.c: Cmd_Wait_f
    // Causes execution of the remainder of the command buffer to be delayed until next frame. This allows commands
    // like: bind g "impulse 5 ; +attack ; wait ; -attack ; impulse 2"
    private void Cmd_Wait_f() => cmd_wait = true;

    // ==========================================================================
    // COMMAND BUFFER
    // ==========================================================================

    // cmd.c: Cbuf_Init
    public void Cbuf_Init() => cmd_text = new SizeBuf(8192); // space for commands and script files

    // cmd.c: Cbuf_AddText
    // Adds command text at the end of the buffer
    public void Cbuf_AddText(string text)
    {
        byte[] bytes = CString(text);
        int l = bytes.Length - 1;
        if (cmd_text.cursize + l >= cmd_text.maxsize)
        {
            Con_Printf("Cbuf_AddText: overflow\n");
            return;
        }

        SZ_Write(cmd_text, bytes.AsSpan(0, l));
    }

    // cmd.c: Cbuf_InsertText
    // Adds command text immediately after the current command. Adds a \n to the text
    public void Cbuf_InsertText(string text)
    {
        byte[]? temp;
        int templen;

        // copy off any commands still remaining in the exec buffer
        templen = cmd_text.cursize;
        if (templen != 0)
        {
            temp = cmd_text.data.AsSpan(0, templen).ToArray();
            SZ_Clear(cmd_text);
        }
        else
        {
            temp = null;
        }

        // add the entire text of the file
        Cbuf_AddText(text);

        // add the copied off data
        if (temp != null)
        {
            SZ_Write(cmd_text, temp);
        }
    }

    // cmd.c: Cbuf_Execute
    public void Cbuf_Execute()
    {
        int i;
        int quotes;

        while (cmd_text.cursize != 0)
        {
            // find a \n or ; line break
            byte[] text = cmd_text.data;
            quotes = 0;
            for (i = 0; i < cmd_text.cursize; i++)
            {
                if (text[i] == '"')
                {
                    quotes++;
                }

                if ((quotes & 1) == 0 && text[i] == ';')
                {
                    break; // don't break if inside a quoted string
                }

                if (text[i] == '\n')
                {
                    break;
                }
            }

            // char line[1024]: the line is copied over what the buffer held before and ended with a 0. It is passed
            // whole, because COM_Parse reads on past the 0 inside a quoted string the line ends in, into what longer
            // lines left there (design.md §3.2). In C the buffer is on the stack, so between frames other code can
            // have written over it; here it keeps what the last line left. A line of 1024 characters or more overruns
            // the C's buffer; here the buffer grows.
            if (i >= cbuf_line.Length)
            {
                Array.Resize(ref cbuf_line, i + 1);
            }

            Array.Copy(text, cbuf_line, i);
            cbuf_line[i] = 0;
            string line = Encoding.Latin1.GetString(cbuf_line);

            // delete the text from the command buffer and move remaining commands down
            // this is necessary because commands (exec, alias) can insert data at the
            // beginning of the text buffer
            if (i == cmd_text.cursize)
            {
                cmd_text.cursize = 0;
            }
            else
            {
                i++;
                cmd_text.cursize -= i;
                Array.Copy(text, i, text, 0, cmd_text.cursize);
            }

            // execute the command line
            Cmd_ExecuteString(line, cmd_source_t.src_command);

            if (cmd_wait)
            {
                // skip out while text still remains in buffer, leaving it
                // for next frame
                cmd_wait = false;
                break;
            }
        }
    }

    // ==========================================================================
    // SCRIPT COMMANDS
    // ==========================================================================

    // cmd.c: Cmd_StuffCmds_f
    // Adds command line parameters as script statements. Commands lead with a +, and continue until a - or another +
    // quake +prog jctest.qp +cmd amlev1
    // quake -nosound +cmd amlev1
    private void Cmd_StuffCmds_f()
    {
        int i, j;
        int s;

        if (Cmd_Argc() != 1)
        {
            Con_Printf("stuffcmds : execute command line parameters\n");
            return;
        }

        // build the combined string to parse from
        s = 0;
        for (i = 1; i < com_argv.Argc; i++)
        {
            s += Q_strlen(com_argv.Argv(i)) + 1;
        }

        if (s == 0)
        {
            return;
        }

        var textBuilder = new StringBuilder(s);
        for (i = 1; i < com_argv.Argc; i++)
        {
            textBuilder.Append(com_argv.Argv(i).AsSpan(0, Q_strlen(com_argv.Argv(i))));
            if (i != com_argv.Argc - 1)
            {
                textBuilder.Append(' ');
            }
        }

        string text = textBuilder.ToString();

        // pull out the commands
        var build = new StringBuilder();

        for (i = 0; i < s - 1; i++)
        {
            if (Ch(text, i) == '+')
            {
                i++;
                for (j = i; Ch(text, j) != '+' && Ch(text, j) != '-' && Ch(text, j) != 0; j++)
                {
                }

                build.Append(text, i, j - i);
                build.Append('\n');
                i = j - 1;
            }
        }

        if (build.Length != 0)
        {
            Cbuf_InsertText(build.ToString());
        }
    }

    // cmd.c: Cmd_Exec_f
    private void Cmd_Exec_f()
    {
        if (Cmd_Argc() != 2)
        {
            Con_Printf("exec <filename> : execute a script file\n");
            return;
        }

        byte[]? f = COM_LoadHunkFile(Cmd_Argv(1));
        if (f == null)
        {
            Con_Printf("couldn't exec %s\n", Cmd_Argv(1));
            return;
        }

        Con_Printf("execing %s\n", Cmd_Argv(1));

        // the file as a C string: up to its first 0 (COM_LoadFile appends one)
        Cbuf_InsertText(Encoding.Latin1.GetString(f, 0, Array.IndexOf(f, (byte)0)));
    }

    // cmd.c: Cmd_Echo_f
    // Just prints the rest of the line to the console
    private void Cmd_Echo_f()
    {
        for (int i = 1; i < Cmd_Argc(); i++)
        {
            Con_Printf("%s ", Cmd_Argv(i));
        }

        Con_Printf("\n");
    }

    // cmd.c: Cmd_Alias_f
    // Creates a new command that executes a command string (possibly ; seperated)
    private void Cmd_Alias_f()
    {
        CmdAlias? a;
        int i, c;
        string s;

        if (Cmd_Argc() == 1)
        {
            Con_Printf("Current alias commands:\n");
            for (a = cmd_alias; a != null; a = a.next)
            {
                Con_Printf("%s : %s\n", a.name, a.value);
            }

            return;
        }

        s = Cmd_Argv(1);
        if (Q_strlen(s) >= MAX_ALIAS_NAME)
        {
            Con_Printf("Alias name is too long\n");
            return;
        }

        // if the alias allready exists, reuse it
        for (a = cmd_alias; a != null; a = a.next)
        {
            if (Q_strcmp(s, a.name) == 0)
            {
                break;
            }
        }

        if (a == null)
        {
            a = new CmdAlias { next = cmd_alias };
            cmd_alias = a;
        }

        a.name = s;

        // copy the rest of the command line
        var cmd = new StringBuilder(); // start out with a null string
        c = Cmd_Argc();
        for (i = 2; i < c; i++)
        {
            cmd.Append(Cmd_Argv(i));
            if (i != c)
            {
                cmd.Append(' ');
            }
        }

        cmd.Append('\n');

        a.value = cmd.ToString();
    }

    // ==========================================================================
    // COMMAND EXECUTION
    // ==========================================================================

    // cmd.c: Cmd_Init
    public void Cmd_Init()
    {
        // register our commands
        Cmd_AddCommand("stuffcmds", Cmd_StuffCmds_f);
        Cmd_AddCommand("exec", Cmd_Exec_f);
        Cmd_AddCommand("echo", Cmd_Echo_f);
        Cmd_AddCommand("alias", Cmd_Alias_f);
        Cmd_AddCommand("cmd", Cmd_ForwardToServer);
        Cmd_AddCommand("wait", Cmd_Wait_f);
    }

    // cmd.c: Cmd_Argc
    public int Cmd_Argc() => cmd_argc;

    // cmd.c: Cmd_Argv
    public string Cmd_Argv(int arg)
    {
        if ((uint)arg >= cmd_argc)
        {
            return "";
        }

        return cmd_argv[arg];
    }

    // cmd.c: Cmd_Args (the rest of the line after the command, null if it has no arguments)
    public string? Cmd_Args() => cmd_args;

    // cmd.c: Cmd_TokenizeString
    // Parses the given string into command line tokens.
    public void Cmd_TokenizeString(string text)
    {
        int p = 0;

        // clear the args from the last string
        cmd_argc = 0;
        cmd_args = null;

        while (true)
        {
            // skip whitespace up to a /n
            while (Ch(text, p) != 0 && Ch(text, p) <= ' ' && Ch(text, p) != '\n')
            {
                p++;
            }

            if (Ch(text, p) == '\n')
            {
                // a newline seperates commands in the buffer
                break;
            }

            if (Ch(text, p) == 0)
            {
                return;
            }

            if (cmd_argc == 1)
            {
                cmd_args = text[p..Q_strlen(text)];
            }

            p = COM_Parse(text, p);
            if (p < 0)
            {
                return;
            }

            if (cmd_argc < MAX_ARGS)
            {
                cmd_argv[cmd_argc] = com_token;
                cmd_argc++;
            }
        }
    }

    // cmd.c: Cmd_AddCommand
    public void Cmd_AddCommand(string cmd_name, Action function)
    {
        if (host_initialized)
        {
            // because hunk allocation would get stomped
            throw Sys_Error("Cmd_AddCommand after host_initialized");
        }

        // fail if the command is a variable name
        if (Cvar_VariableString(cmd_name).Length != 0)
        {
            Con_Printf("Cmd_AddCommand: %s already defined as a var\n", cmd_name);
            return;
        }

        // fail if the command already exists
        for (CmdFunction? cmd = cmd_functions; cmd != null; cmd = cmd.next)
        {
            if (Q_strcmp(cmd_name, cmd.name) == 0)
            {
                Con_Printf("Cmd_AddCommand: %s already defined\n", cmd_name);
                return;
            }
        }

        cmd_functions = new CmdFunction(cmd_name, function) { next = cmd_functions };
    }

    // cmd.c: Cmd_Exists
    public bool Cmd_Exists(string cmd_name)
    {
        for (CmdFunction? cmd = cmd_functions; cmd != null; cmd = cmd.next)
        {
            if (Q_strcmp(cmd_name, cmd.name) == 0)
            {
                return true;
            }
        }

        return false;
    }

    // cmd.c: Cmd_CompleteCommand
    public string? Cmd_CompleteCommand(string partial)
    {
        int len = Q_strlen(partial);

        if (len == 0)
        {
            return null;
        }

        // check functions
        for (CmdFunction? cmd = cmd_functions; cmd != null; cmd = cmd.next)
        {
            if (Q_strncmp(partial, cmd.name, len) == 0)
            {
                return cmd.name;
            }
        }

        return null;
    }

    // cmd.c: Cmd_ExecuteString
    // A complete command line has been parsed, so try to execute it
    public void Cmd_ExecuteString(string text, cmd_source_t src)
    {
        cmd_source = src;
        Cmd_TokenizeString(text);

        // execute the command line
        if (Cmd_Argc() == 0)
        {
            return; // no tokens
        }

        // check functions
        for (CmdFunction? cmd = cmd_functions; cmd != null; cmd = cmd.next)
        {
            if (Q_strcasecmp(cmd_argv[0], cmd.name) == 0)
            {
                cmd.function();
                return;
            }
        }

        // check alias
        for (CmdAlias? a = cmd_alias; a != null; a = a.next)
        {
            if (Q_strcasecmp(cmd_argv[0], a.name) == 0)
            {
                Cbuf_InsertText(a.value);
                return;
            }
        }

        // check cvars
        if (!Cvar_Command())
        {
            Con_Printf("Unknown command \"%s\"\n", Cmd_Argv(0));
        }
    }

    // cmd.c: Cmd_ForwardToServer
    // Sends the entire command line over to the server
    public void Cmd_ForwardToServer()
    {
        if (cls.state != cactive_t.ca_connected)
        {
            Con_Printf("Can't \"%s\", not connected\n", Cmd_Argv(0));
            return;
        }

        if (cls.demoplayback)
        {
            return; // not really connected
        }

        MSG_WriteByte(cls.message, clc_stringcmd);
        if (Q_strcasecmp(Cmd_Argv(0), "cmd") != 0)
        {
            SZ_Print(cls.message, Cmd_Argv(0));
            SZ_Print(cls.message, " ");
        }

        if (Cmd_Argc() > 1)
        {
            SZ_Print(cls.message, Cmd_Args()!);
        }
        else
        {
            SZ_Print(cls.message, "\n");
        }
    }

    // cmd.c: Cmd_CheckParm
    // Returns the position (1 to argc-1) in the command's argument list where the given parameter apears, or 0 if not
    // present
    public int Cmd_CheckParm(string parm)
    {
        for (int i = 1; i < Cmd_Argc(); i++)
        {
            if (Q_strcasecmp(parm, Cmd_Argv(i)) == 0)
            {
                return i;
            }
        }

        return 0;
    }

    // cmd.c: cmdalias_t
    internal sealed class CmdAlias
    {
        public CmdAlias? next;
        public string name = "";
        public string value = "";
    }

    // cmd.c: cmd_function_t
    private sealed class CmdFunction(string name, Action function)
    {
        public CmdFunction? next;
        public readonly string name = name;
        public readonly Action function = function;
    }
}
