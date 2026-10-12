using System.Globalization;
using System.Text;
using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Platform;

namespace QuakeSharp.Core.Tests;

/// <summary>
/// The command, cvar, console and key code against WinQuake's own: the scenarios of tools/oracle/units, run as
/// tools/oracle/src/scenarios.c runs them, must give the same transcript (everything printed and the state dumps).
/// The expected hashes are what "quake-oracle -oraclescenario &lt;file&gt;" prints; "-print" prints the oracle's
/// transcript, to diff with this test's (written next to the test assembly when they differ).
/// </summary>
public class ScenarioTests
{
    [Theory]
    [InlineData("commands.scenario", "5c2f40ab9112e662")]
    [InlineData("console.scenario", "94b014503b93a579")]
    public void MatchesWinQuake(string scenario, string expected)
    {
        string transcript = Run(Path.Combine(ReferenceSource.RepositoryRoot(), "tools", "oracle", "units", scenario));
        byte[] bytes = Encoding.Latin1.GetBytes(transcript);
        ulong hash = 0xcbf29ce484222325;
        foreach (byte b in bytes)
        {
            hash ^= b;
            hash *= 0x100000001b3;
        }

        string actual = hash.ToString("x16", CultureInfo.InvariantCulture);
        if (actual != expected)
        {
            File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, scenario + ".transcript"), bytes);
        }

        Assert.Equal(expected, actual);
    }

    // scenarios.c: Oracle_Scenario
    private static string Run(string path)
    {
        var transcript = new StringBuilder();
        var engine = new QuakeEngine(new QuakeParms(".", new CommandLine(["quake"])), new VirtualSystem(t => transcript.Append(t)));

        // scenarios.c: Init, Host_Init's order
        engine.Memory_Init();
        engine.Cbuf_Init();
        engine.Cmd_Init();
        engine.Cvar_RegisterVariable(engine.developer);
        engine.Cvar_RegisterVariable(engine.skill);
        engine.Cvar_RegisterVariable(engine.deathmatch);
        engine.Cvar_RegisterVariable(engine.teamplay);
        engine.Cvar_RegisterVariable(new Cvar("unit_archive", "1.5", archive: true));
        engine.Cvar_RegisterVariable(new Cvar("unit_server", "0", server: true));
        engine.Cvar_RegisterVariable(new Cvar("unit_value", "0x10"));
        engine.Key_Init();
        engine.scr_disabled_for_loading = true;
        engine.cls.state = cactive_t.ca_disconnected;
        engine.Con_Init();

        // scenarios.c: Run
        foreach (string raw in File.ReadLines(path, Encoding.Latin1))
        {
            string line = raw.TrimEnd('\r', '\n');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] words = line.Split(' ');
            if (line.StartsWith("text ", StringComparison.Ordinal))
            {
                engine.Cbuf_AddText(Unescape(line[5..]));
            }
            else if (line == "execute")
            {
                engine.Cbuf_Execute();
            }
            else if (words[0] == "key")
            {
                engine.Key_Event(int.Parse(words[1], CultureInfo.InvariantCulture), words[2] != "0");
            }
            else if (words[0] == "dest")
            {
                engine.key_dest = (keydest_t)int.Parse(words[1], CultureInfo.InvariantCulture);
            }
            else if (words[0] == "state")
            {
                engine.cls.state = (cactive_t)int.Parse(words[1], CultureInfo.InvariantCulture);
            }
            else if (words[0] == "vid")
            {
                engine.vid.width = uint.Parse(words[1], CultureInfo.InvariantCulture);
                engine.vid.height = uint.Parse(words[2], CultureInfo.InvariantCulture);
                engine.Con_CheckResize();
            }
            else if (words[0] == "time")
            {
                engine.realtime = double.Parse(words[1], CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("print ", StringComparison.Ordinal))
            {
                engine.Con_Printf("%s", Unescape(line[6..]));
            }
            else if (line == "dump")
            {
                Dump(engine, transcript);
            }
            else
            {
                throw new InvalidOperationException($"{path}: unknown action {line}");
            }
        }

        return transcript.ToString();
    }

    // scenarios.c: Dump
    private static void Dump(QuakeEngine e, StringBuilder o)
    {
        void Out(string fmt, params ReadOnlySpan<PrintfArg> args) => o.Append(CFormat.Sprintf(fmt, args));

        Out("== dump\n");
        Out("con current %d x %d backscroll %d totallines %d linewidth %d\n", e.con_current, e.con_x, e.con_backscroll, e.con_totallines, e.con_linewidth);
        Out("times %08x %08x %08x %08x\n", Bits(e.con_times[0]), Bits(e.con_times[1]), Bits(e.con_times[2]), Bits(e.con_times[3]));
        for (int i = e.con_current - 11; i <= e.con_current; i++)
        {
            if (i < 0)
            {
                continue;
            }

            string line = Encoding.Latin1.GetString(e.con_text, i % e.con_totallines * e.con_linewidth, e.con_linewidth);
            Out("line %d |%s|\n", i, line);
        }

        Out("keys dest %d edit %d history %d pos %d shift %d count %d |%s|\n", (int)e.key_dest, e.edit_line, e.history_line,
            e.key_linepos, e.shift_down ? 1 : 0, e.key_count, QuakeEngine.CStringOf(e.key_lines[e.edit_line], 0));
        Out("chat |%s| team %d\n", QuakeEngine.CStringOf(e.chat_buffer, 0), e.team_message ? 1 : 0);
        Out("cbuf %d |", e.cmd_text.cursize);
        o.Append(Encoding.Latin1.GetString(e.cmd_text.data, 0, e.cmd_text.cursize));
        Out("|\n");
        Out("args %d", e.Cmd_Argc());
        for (int i = 0; i < e.Cmd_Argc(); i++)
        {
            Out(" |%s|", e.Cmd_Argv(i));
        }

        Out("\n");
        for (Cvar? v = e.cvar_vars; v != null; v = v.next)
        {
            Out("cvar %s |%s| %08x %d %d\n", v.name, v.@string, Bits(v.value), v.archive ? 1 : 0, v.server ? 1 : 0);
        }

        for (QuakeEngine.CmdAlias? a = e.cmd_alias; a != null; a = a.next)
        {
            Out("alias %s |%s|\n", a.name, a.value);
        }

        e.Key_WriteBindings(o);
        e.Cvar_WriteVariables(o);
    }

    private static int Bits(float f) => BitConverter.SingleToInt32Bits(f);

    // scenarios.c: Unescape (\n \r \xHH, and a backslash before anything else is that character)
    private static string Unescape(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                switch (s[i])
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'x':
                        sb.Append((char)int.Parse(s.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 2;
                        break;
                    default: sb.Append(s[i]); break;
                }
            }
            else
            {
                sb.Append(s[i]);
            }
        }

        return sb.ToString();
    }
}
