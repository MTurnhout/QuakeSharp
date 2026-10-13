namespace QuakeSharp.Core.Common;

/// <summary>
/// The command line of common.c: com_argc, com_argv, com_cmdline and the game flags set from it.
/// </summary>
public sealed class CommandLine
{
    // quakedef.h: MAX_NUM_ARGVS
    public const int MAX_NUM_ARGVS = 50;

    private const int CMDLINE_LENGTH = 256;

    private static readonly string[] safeargvs = ["-stdvid", "-nolan", "-nosound", "-nocdaudio", "-nojoy", "-nomouse", "-dibonly"];

    private const string argvdummy = " ";

    private readonly string[] largv;

    /// <summary>
    /// common.c: COM_InitArgv. <paramref name="argv"/>[0] is the program, as in C; sys_win.c gives it empty, so the
    /// cmdline cvar starts with a space whatever it holds here.
    /// </summary>
    public CommandLine(IReadOnlyList<string> argv)
    {
        // reconstitute the command line for the cmdline externally visible cvar
        var cmdline = new System.Text.StringBuilder();
        for (int j = 0; j < MAX_NUM_ARGVS && j < argv.Count; j++)
        {
            int i = 0;

            // the traces' harness switches are left out, as the oracle leaves them out (quake-oracle.patch)
            int harness = HarnessSwitch(argv, j);
            if (harness != 0)
            {
                j += harness - 1;
                continue;
            }

            string arg = j == 0 ? "" : argv[j];
            while (cmdline.Length < CMDLINE_LENGTH - 1 && i < arg.Length && arg[i] != '\0')
            {
                cmdline.Append(arg[i++]);
            }

            if (cmdline.Length < CMDLINE_LENGTH - 1)
            {
                cmdline.Append(' ');
            }
            else
            {
                break;
            }
        }

        Cmdline = cmdline.ToString();

        bool safe = false;
        var args = new List<string>();
        for (int i = 0; i < MAX_NUM_ARGVS && i < argv.Count; i++)
        {
            args.Add(argv[i]);
            if (argv[i] == "-safe")
            {
                safe = true;
            }
        }

        if (safe)
        {
            // force all the safe-mode switches
            args.AddRange(safeargvs);
        }

        Argc = args.Count;
        args.Add(argvdummy);
        largv = [.. args];

        if (CheckParm("-rogue") != 0)
        {
            rogue = true;
            standard_quake = false;
        }

        if (CheckParm("-hipnotic") != 0)
        {
            hipnotic = true;
            standard_quake = false;
        }
    }

    /// <summary>com_argc</summary>
    public int Argc { get; }

    // the traces' harness switches and how many values each takes (the oracle's Oracle_HarnessSwitch)
    private static readonly (string Name, int Values)[] harnessSwitches =
    [
        ("-trace", 1), ("-traceevents", 1), ("-tracedatagrams", 1), ("-tracedump", 1), ("-traceshot", 1),
        ("-tracequitafterdemo", 0), ("-oracleframes", 1), ("-oraclevid", 2), ("-oraclestep", 1), ("-oraclehost", 1),
        ("-oracleverbose", 0), ("-netunplug", 0),
    ];

    // how many arguments from argv[j] are a harness switch with its values, 0 for one of the game's
    private static int HarnessSwitch(IReadOnlyList<string> argv, int j)
    {
        foreach ((string name, int values) in harnessSwitches)
        {
            if (argv[j] == name)
            {
                return j + 1 + values <= argv.Count ? 1 + values : argv.Count - j;
            }
        }

        return 0;
    }

    /// <summary>com_cmdline: the arguments joined by spaces, at most 255 characters.</summary>
    public string Cmdline { get; }

    /// <summary>common.c: standard_quake, rogue, hipnotic</summary>
    public bool standard_quake { get; } = true;

    public bool rogue { get; }

    public bool hipnotic { get; }

    /// <summary>com_argv[i]; com_argv[com_argc] is " ", as in C.</summary>
    public string Argv(int i) => largv[i];

    // common.c: COM_CheckParm
    // Returns the position (1 to argc-1) in the program's argument list where the given parameter appears, or 0.
    public int CheckParm(string parm)
    {
        for (int i = 1; i < Argc; i++)
        {
            if (parm == largv[i])
            {
                return i;
            }
        }

        return 0;
    }
}
