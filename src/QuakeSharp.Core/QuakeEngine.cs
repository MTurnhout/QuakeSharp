using QuakeSharp.Core.Common;
using QuakeSharp.Core.Input;
using QuakeSharp.Core.Platform;
using QuakeSharp.Core.Sound;

namespace QuakeSharp.Core;

/// <summary>quakedef.h: quakeparms_t, what the system driver gives the engine.</summary>
public sealed class QuakeParms
{
    public QuakeParms(string basedir, CommandLine argv)
    {
        this.basedir = basedir;
        this.argv = argv;
    }

    public string basedir { get; }

    /// <summary>for development over ISDN lines (not supported: the file system ignores it)</summary>
    public string? cachedir { get; init; }

    /// <summary>com_argc and com_argv</summary>
    public CommandLine argv { get; }

    /// <summary>
    /// The folder for what the game writes (config.cfg, savegames, demos...), searched before the game directories'
    /// own loose files; null: the game directories themselves, as WinQuake (design.md §9).
    /// </summary>
    public string? userdir { get; init; }

    /// <summary>The folder relative paths (basedir ".") start from; null: the process's current folder.</summary>
    public string? workdir { get; init; }

    /// <summary>Mode 0's size: vid_win.c's first windowed mode, 320x240 (the oracle's -oraclevid).</summary>
    public int vidwidth { get; init; } = 320;

    public int vidheight { get; init; } = 240;

    /// <summary>WinQuake takes 16 MB on any machine with 32 MB or more (sys_win.c: MAXIMUM_WIN_MEMORY).</summary>
    public int memsize { get; set; } = 0x1000000;
}

/// <summary>
/// The engine: what WinQuake's C keeps in global variables, owned by one instance (design.md §6.2 rule 2). It is a
/// partial class with a file for each C module (Common/QuakeEngine.Cmd.cs is cmd.c, ...), and its members keep the C's
/// names (Cmd_AddCommand, cvar_vars, cls), which were unique in C's one namespace: every line greps against the
/// reference.
/// </summary>
public sealed partial class QuakeEngine
{
    /// <param name="mouse">The platform's part of the mouse while in_win.c has it (none: the traces').</param>
    /// <param name="joysticks">The platform's joysticks (none: winmm without any, as the oracle's).</param>
    /// <param name="winsock">The Winsock net_wins.c and net_wipx.c load (none: a machine without wsock32.dll).</param>
    public QuakeEngine(QuakeParms parms, ISystem system, IInputEvents? input = null, ISoundDma? sound = null,
        IVideoOutput? video = null, ICdDrive? cd = null, IMouseCapture? mouse = null, IJoystickDevice? joysticks = null,
        Net.IWinsock? winsock = null)
    {
        host_parms = parms;
        this.system = system;
        events = input;
        this.input = new VirtualInput(mouse, joysticks);
        windowProcedure = new WindowProcedure(this);
        sounddma = sound;
        cddrive = cd;
        this.video = video;
        this.winsock = winsock;
        com_argv = parms.argv;

        // sys_win.c: WinMain, after COM_InitArgv
        isDedicated = COM_CheckParm("-dedicated") != 0;
        edgetables =
        [
            new(false, 1, r_p0, r_p2, null, 2, r_p0, r_p1, r_p2),
            new(false, 2, r_p1, r_p0, r_p2, 1, r_p1, r_p2, null),
            new(true, 1, r_p0, r_p2, null, 1, r_p1, r_p2, null),
            new(false, 1, r_p1, r_p0, null, 2, r_p1, r_p2, r_p0),
            new(false, 2, r_p0, r_p2, r_p1, 1, r_p0, r_p1, null),
            new(false, 1, r_p2, r_p1, null, 1, r_p2, r_p0, null),
            new(false, 1, r_p2, r_p1, null, 2, r_p2, r_p0, r_p1),
            new(false, 2, r_p2, r_p1, r_p0, 1, r_p2, r_p0, null),
            new(false, 1, r_p1, r_p0, null, 1, r_p1, r_p2, null),
            new(true, 1, r_p2, r_p1, null, 1, r_p0, r_p1, null),
            new(true, 1, r_p1, r_p0, null, 1, r_p2, r_p0, null),
            new(false, 1, r_p0, r_p2, null, 1, r_p0, r_p1, null),
        ];
        // net_win.c's drivers, and net_main.c's and net_dgrm.c's poll procedures, which the C initializes statically
        NetWin_Drivers();
        pr_builtins = PR_Builtins();
        pr_numbuiltins = pr_builtins.Length;
        slistSendProcedure = new(Slist_Send);
        slistPollProcedure = new(Slist_Poll);
        testPollProcedure = new(Test_Poll);
        test2PollProcedure = new(Test2_Poll);

        fileSystem = new FileSystem(parms.argv, text => Con_Printf("%s", text), text => Sys_Printf("%s", text))
        {
            UserDir = parms.userdir,
            WorkDir = parms.workdir,
            BeginDisc = Draw_BeginDisc,
            EndDisc = Draw_EndDisc,
        };
    }

    /// <summary>host.c: host_parms</summary>
    public QuakeParms host_parms { get; }

    /// <summary>common.c: com_argc, com_argv (and the game flags standard_quake, rogue, hipnotic)</summary>
    public CommandLine com_argv { get; }

    private readonly ISystem system;
    private readonly IInputEvents? events;
    private readonly ISoundDma? sounddma;

    // the CD drive cd_win.c sends its MCI commands to (null: none, as a machine without one)
    internal readonly ICdDrive? cddrive;
    private readonly IVideoOutput? video;
    private readonly Net.IWinsock? winsock;
    private readonly Numerics.CRand crand = new();
    private readonly FileSystem fileSystem;

    // common.c: COM_CheckParm
    public int COM_CheckParm(string parm) => com_argv.CheckParm(parm);

    // sys_win.c: Sys_Error
    public static QuakeException Sys_Error(string fmt, params ReadOnlySpan<PrintfArg> args) =>
        new(CFormat.Sprintf(fmt, args));

    /// <summary>sys_win.c: isDedicated (-dedicated)</summary>
    public readonly bool isDedicated;

    // sys_win.c: Sys_Error's sc_return_on_enter; Sys_ConsoleInput's static text and len
    private bool sc_return_on_enter;
    private readonly byte[] sys_consoletext = new byte[256];
    private int sys_consolelen;

    // sys_win.c: CONSOLE_ERROR_TIMEOUT, the seconds to wait on Sys_Error running dedicated before exiting
    private const double CONSOLE_ERROR_TIMEOUT = 60.0;

    /// <summary>What a dedicated server's console is given (Sys_Printf, the echo of the keys typed), for the traces.</summary>
    public Action<byte[]>? ConsoleWritten { get; set; }

    // sys_win.c: Sys_Printf: a dedicated server's console shows it (WinQuake prints nothing otherwise); the system's
    // debugging console (the tools' logs) gets it all
    public void Sys_Printf(string fmt, params ReadOnlySpan<PrintfArg> args)
    {
        string text = CFormat.Sprintf(fmt, args);
        system.Printf(text);
        if (isDedicated)
        {
            Sys_ConsoleWrite(System.Text.Encoding.Latin1.GetBytes(text));
        }
    }

    // sys_win.c: WriteFile (houtput, ...): a dedicated server's console
    private void Sys_ConsoleWrite(byte[] text)
    {
        system.ConsoleWrite(text);
        ConsoleWritten?.Invoke(text);
    }

    // sys_win.c: Sys_Error's part for a dedicated server, after the error ended the frame: the error in its console,
    // then a wait for Enter, up to CONSOLE_ERROR_TIMEOUT seconds (the executable's; a trace ends at the error, as the
    // oracle's)
    public void Sys_ErrorDedicated(string text, bool wait)
    {
        if (!isDedicated)
        {
            return;
        }

        string text2 = CFormat.Sprintf("ERROR: %s\n", text);
        Sys_ConsoleWrite("\n"u8.ToArray());
        Sys_ConsoleWrite("***********************************\n"u8.ToArray());
        Sys_ConsoleWrite(System.Text.Encoding.Latin1.GetBytes(text2));
        Sys_ConsoleWrite("Press Enter to exit\n"u8.ToArray());
        Sys_ConsoleWrite("***********************************\n"u8.ToArray());

        if (!wait)
        {
            return;
        }

        double starttime = Sys_FloatTime();
        sc_return_on_enter = true; // so Enter will get us out of here

        while (Sys_ConsoleInput() == null && (Sys_FloatTime() - starttime) < CONSOLE_ERROR_TIMEOUT)
        {
        }
    }

    // sys_win.c: Sys_FloatTime
    public double Sys_FloatTime() => system.FloatTime();

    // sys_win.c: Sys_ConsoleInput: a line typed in a dedicated server's console, edited and echoed as WinQuake does
    public string? Sys_ConsoleInput()
    {
        int ch;

        if (!isDedicated)
        {
            return null;
        }

        while (true)
        {
            // GetNumberOfConsoleInputEvents, ReadConsoleInput: the next key released (AsciiChar, a CHAR: signed)
            if (!system.ConsoleRead(out byte key))
            {
                break;
            }

            ch = (sbyte)key;
            switch (ch)
            {
                case '\r':
                    Sys_ConsoleWrite("\r\n"u8.ToArray());

                    if (sys_consolelen != 0)
                    {
                        sys_consoletext[sys_consolelen] = 0;
                        sys_consolelen = 0;
                        return ConsoleText();
                    }
                    else if (sc_return_on_enter)
                    {
                        // special case to allow exiting from the error handler on Enter
                        sys_consoletext[0] = (byte)'\r';
                        sys_consolelen = 0;
                        return ConsoleText();
                    }

                    break;

                case '\b':
                    Sys_ConsoleWrite("\b \b"u8.ToArray());
                    if (sys_consolelen != 0)
                    {
                        sys_consolelen--;
                    }

                    break;

                default:
                    if (ch >= ' ')
                    {
                        Sys_ConsoleWrite([(byte)ch]);
                        sys_consoletext[sys_consolelen] = (byte)ch;
                        sys_consolelen = (sys_consolelen + 1) & 0xff;
                    }

                    break;
            }
        }

        return null;
    }

    // Sys_ConsoleInput's text, up to its 0 (or its end: the static array starts zeroed)
    private string ConsoleText()
    {
        int length = Array.IndexOf(sys_consoletext, (byte)0);
        return System.Text.Encoding.Latin1.GetString(sys_consoletext, 0, length < 0 ? sys_consoletext.Length : length);
    }

    // sys_win.c: Sys_Sleep
    public void Sys_Sleep() => system.Sleep();

    // sys_win.c: Sys_SendKeyEvents: the window's messages to vid_win.c's window procedure; closing the window quits
    // (WinQuake asks first). The CD drive's MM_MCINOTIFY messages come to the window procedure too:
    // CDAudio_MessageHandler
    public void Sys_SendKeyEvents()
    {
        if (events != null && !events.SendKeyEvents(windowProcedure))
        {
            throw Sys_Quit();
        }

        while (cddrive != null && cddrive.PeekNotify(out uint wParam, out uint lParam))
        {
            CDAudio_MessageHandler(wParam, lParam);
        }
    }

    // The C runtime's rand and srand: one generator for the whole engine (design.md §3.1)
    public int rand() => crand.rand();

    /// <summary>The number of rand calls so far, for the traces.</summary>
    public uint RandCalls => crand.Calls;

    public void srand(uint seed) => crand.srand(seed);

    /// <summary>The state of rand's generator, for the traces.</summary>
    public uint RandSeed => crand.Seed;

    // common.c: va (does a varargs printf into a temp buffer)
    public static string va(string format, params ReadOnlySpan<PrintfArg> args) => CFormat.Sprintf(format, args);
}
