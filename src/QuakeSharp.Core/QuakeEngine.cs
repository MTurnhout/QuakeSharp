using QuakeSharp.Core.Common;
using QuakeSharp.Core.Platform;

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

    /// <summary>WinQuake takes 16 MB on any machine with 32 MB or more (sys_win.c: MAXIMUM_WIN_MEMORY).</summary>
    public int memsize { get; init; } = 0x1000000;
}

/// <summary>
/// The engine: what WinQuake's C keeps in global variables, owned by one instance (design.md §6.2 rule 2). It is a
/// partial class with a file for each C module (Common/QuakeEngine.Cmd.cs is cmd.c, ...), and its members keep the C's
/// names (Cmd_AddCommand, cvar_vars, cls), which were unique in C's one namespace: every line greps against the
/// reference.
/// </summary>
public sealed partial class QuakeEngine
{
    public QuakeEngine(QuakeParms parms, ISystem system)
    {
        host_parms = parms;
        this.system = system;
        com_argv = parms.argv;
        fileSystem = new FileSystem(parms.argv, text => Con_Printf("%s", text), text => Sys_Printf("%s", text));
    }

    /// <summary>host.c: host_parms</summary>
    public QuakeParms host_parms { get; }

    /// <summary>common.c: com_argc, com_argv (and the game flags standard_quake, rogue, hipnotic)</summary>
    public CommandLine com_argv { get; }

    private readonly ISystem system;
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

    // common.c: va (does a varargs printf into a temp buffer)
    public static string va(string format, params ReadOnlySpan<PrintfArg> args) => CFormat.Sprintf(format, args);
}
