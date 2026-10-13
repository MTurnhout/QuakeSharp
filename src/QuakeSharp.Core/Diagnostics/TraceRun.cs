using QuakeSharp.Core.Common;
using QuakeSharp.Core.Net;
using QuakeSharp.Core.Platform;
using QuakeSharp.Core.Sound;

namespace QuakeSharp.Core.Diagnostics;

/// <summary>
/// Runs the engine as the oracle runs WinQuake (tools/oracle/src/sys_oracle.c's main): headless, on a virtual clock
/// that moves one step before each frame, with the oracle's virtual sound device and scripted input (-traceevents), from
/// the game folder with the base directory "."; and writes the oracle's trace lines after each frame.
/// </summary>
public sealed class TraceRun
{
    private readonly VirtualSystem system;
    private readonly TraceWriter writer;
    private readonly EnhancedCheck? enhancedCheck;

    /// <param name="gameDir">The Quake folder (holding id1/).</param>
    /// <param name="userDir">Where the game writes (config.cfg...): an empty folder, as the oracle's runs start
    /// without config.cfg.</param>
    /// <param name="args">The command line after the program's name (e.g. +playdemo demo1).</param>
    /// <param name="width">Mode 0's size (-oraclevid).</param>
    /// <param name="height">Mode 0's size (-oraclevid).</param>
    /// <param name="framesPerSecond">The clock's steps per second (-oraclestep).</param>
    /// <param name="enhanced">With the enhanced mode's capture switched on and off along the way, every frame taken
    /// and interpolated, its guard on (design.md §11.6: what EnhancedModeChangesNothing checks).</param>
    public TraceRun(string gameDir, string userDir, IEnumerable<string> args, int width = 320, int height = 240,
        int framesPerSecond = 64, Action<string>? print = null, bool enhanced = false)
    {
        system = new VirtualSystem(print, 1.0 / framesPerSecond);

        // in a window, in mode 0, as Trace-Demos.ps1 runs the oracle (design.md decision 50)
        var commandLine = new CommandLine(["quake", "-startwindowed", "-noautostretch", .. args]);

        // -traceevents <file>: the scripted input, read relative to the current folder as the oracle reads it
        int e = commandLine.CheckParm("-traceevents");
        TraceEvents? events = e != 0 && e < commandLine.Argc - 1
            ? new TraceEvents(commandLine.Argv(e + 1), () => writer!.Frame)
            : null;

        // the virtual machine's Winsock (winsock_oracle.c): -oraclehost <n>, and the other hosts' datagrams from
        // -tracedatagrams <file>
        int h = commandLine.CheckParm("-oraclehost");
        Winsock = new VirtualWinsock(h != 0 && h < commandLine.Argc - 1 ? Common.QString.Q_atoi(commandLine.Argv(h + 1)) : 1,
            () => system.Steps);
        int d = commandLine.CheckParm("-tracedatagrams");
        if (d != 0 && d < commandLine.Argc - 1)
        {
            Winsock.ArriveFromFile(commandLine.Argv(d + 1));
        }

        Engine = new QuakeEngine(
            new QuakeParms(".", commandLine)
            {
                workdir = gameDir,
                userdir = userDir,
                vidwidth = width,
                vidheight = height,
            },
            system,
            input: events,
            sound: new VirtualSoundDma(system),
            cd: new VirtualCdDrive(),
            winsock: Winsock);
        writer = new TraceWriter(Engine, framesPerSecond);
        if (enhanced)
        {
            enhancedCheck = new EnhancedCheck(Engine, system);
            enhancedCheck.BeforeFrame(1);
        }
        if (events != null)
        {
            events.Engine = Engine;
            events.Machine = system;
        }
    }

    public QuakeEngine Engine { get; }

    /// <summary>The virtual machine's Winsock: its number on the virtual LAN, what it sends and receives.</summary>
    public VirtualWinsock Winsock { get; }

    /// <summary>The virtual clock (its steps run ahead of the frames after a frame that waited).</summary>
    public VirtualSystem System => system;

    /// <summary>
    /// The game directory a run with <paramref name="args"/> writes into, as COM_InitFilesystem picks com_gamedir: id1,
    /// then rogue (-rogue), hipnotic (-hipnotic) and -game's, the last added winning.
    /// </summary>
    public static string GameDirectory(IReadOnlyList<string> args)
    {
        var commandLine = new CommandLine(["quake", .. args]);
        string game = FileSystem.GAMENAME;
        if (commandLine.CheckParm("-rogue") != 0)
        {
            game = "rogue";
        }

        if (commandLine.CheckParm("-hipnotic") != 0)
        {
            game = "hipnotic";
        }

        int i = commandLine.CheckParm("-game");
        if (i != 0 && i < commandLine.Argc - 1)
        {
            game = commandLine.Argv(i + 1);
        }

        return game;
    }

    /// <summary>The trace's lines (and the oracle's -tracedump).</summary>
    public TraceWriter Writer => writer;

    /// <summary>
    /// Host_Init, then frames until the engine quits, <paramref name="maxFrames"/> have run (-oracleframes) or, with
    /// <paramref name="quitAfterDemo"/>, the demo that was playing has ended (-tracequitafterdemo); the trace's
    /// lines of each frame. The last two end with Sys_Quit, as the oracle's run does (Host_Shutdown writes
    /// config.cfg). A dedicated server (-dedicated) runs a frame once sys_ticrate has passed, as sys_win.c's loop:
    /// its Sys_Sleep moves the clock.
    /// </summary>
    public IEnumerable<IReadOnlyList<string>> Frames(bool quitAfterDemo, int maxFrames = int.MaxValue)
    {
        bool wasPlaying = false;

        Engine.Sys_Printf("Host_Init\n");
        Engine.Host_Init();
        double oldtime = system.Time;
        while (true)
        {
            double time, newtime;
            if (Engine.isDedicated)
            {
                if (writer.Frame >= maxFrames)
                {
                    _ = Engine.Sys_Quit();
                    yield break;
                }

                // sys_win.c's loop for a dedicated server: a frame once sys_ticrate has passed, sleeping until then
                newtime = Engine.Sys_FloatTime();
                time = newtime - oldtime;

                while (time < Engine.sys_ticrate.value)
                {
                    Engine.Sys_Sleep();
                    newtime = Engine.Sys_FloatTime();
                    time = newtime - oldtime;
                }
            }
            else
            {
                // games between hosts give the turn to the others before the clock moves
                system.Waiting?.Invoke();
                if (writer.Frame >= maxFrames)
                {
                    _ = Engine.Sys_Quit();
                    yield break;
                }

                // sys_win.c's loop: the time since the last frame began, a step, or more after a frame that waited
                system.Step();
                newtime = system.Time;
                time = newtime - oldtime;
            }

            enhancedCheck?.BeforeFrame(writer.Frame + 1);
            try
            {
                Engine.Host_Frame((float)time);
            }
            catch (QuitException)
            {
                yield break;
            }

            oldtime = newtime;
            yield return writer.FrameLines();

            if (Engine.cls.demoplayback)
            {
                wasPlaying = true;
            }
            else if (quitAfterDemo && wasPlaying)
            {
                _ = Engine.Sys_Quit();
                yield break;
            }
        }
    }
}
