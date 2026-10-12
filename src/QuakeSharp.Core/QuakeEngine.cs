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

    /// <summary>The video mode's size (WinQuake's default window is 320x240; vid_win.c's modes, milestone 6).</summary>
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
    public QuakeEngine(QuakeParms parms, ISystem system, IInputEvents? input = null, ISoundDma? sound = null,
        IVideoOutput? video = null, ICdDrive? cd = null, IMouseCapture? mouse = null, IJoystickDevice? joysticks = null)
    {
        host_parms = parms;
        this.system = system;
        events = input;
        this.input = new VirtualInput(mouse, joysticks);
        windowProcedure = new WindowProcedure(this);
        sounddma = sound;
        cddrive = cd;
        this.video = video;
        com_argv = parms.argv;
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
        // net_win.c's drivers (the datagram driver over the virtual TCP/IP and IPX drivers, decision 33, §3.4) and
        // net_main.c's and net_dgrm.c's poll procedures, which the C initializes statically
        net_drivers =
        [
            new()
            {
                name = "Loopback", Init = Loop_Init, Listen = Loop_Listen, SearchForHosts = Loop_SearchForHosts,
                Connect = Loop_Connect, CheckNewConnections = Loop_CheckNewConnections, QGetMessage = Loop_GetMessage,
                QSendMessage = Loop_SendMessage, SendUnreliableMessage = Loop_SendUnreliableMessage,
                CanSendMessage = Loop_CanSendMessage, CanSendUnreliableMessage = Loop_CanSendUnreliableMessage,
                Close = Loop_Close, Shutdown = Loop_Shutdown,
            },
            new()
            {
                name = "Datagram", Init = Datagram_Init, Listen = Datagram_Listen, SearchForHosts = Datagram_SearchForHosts,
                Connect = Datagram_Connect, CheckNewConnections = Datagram_CheckNewConnections,
                QGetMessage = Datagram_GetMessage, QSendMessage = Datagram_SendMessage,
                SendUnreliableMessage = Datagram_SendUnreliableMessage, CanSendMessage = Datagram_CanSendMessage,
                CanSendUnreliableMessage = Datagram_CanSendUnreliableMessage, Close = Datagram_Close,
                Shutdown = Datagram_Shutdown,
            },
        ];
        net_numdrivers = net_drivers.Length;
        net_landrivers = [new Net.VirtualLanDriver(this), new Net.VirtualIpxDriver(this)];
        net_numlandrivers = net_landrivers.Length;
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
    private readonly Numerics.CRand crand = new();
    private readonly FileSystem fileSystem;

    // common.c: COM_CheckParm
    public int COM_CheckParm(string parm) => com_argv.CheckParm(parm);

    // sys_win.c: Sys_Error
    public static QuakeException Sys_Error(string fmt, params ReadOnlySpan<PrintfArg> args) =>
        new(CFormat.Sprintf(fmt, args));

    // sys_win.c: Sys_Printf
    public void Sys_Printf(string fmt, params ReadOnlySpan<PrintfArg> args) => system.Printf(CFormat.Sprintf(fmt, args));

    // sys_win.c: Sys_FloatTime
    public double Sys_FloatTime() => system.FloatTime();

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
