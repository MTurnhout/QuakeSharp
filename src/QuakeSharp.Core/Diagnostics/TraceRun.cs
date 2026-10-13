using QuakeSharp.Core.Common;
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
    private readonly double step;
    private readonly TraceWriter writer;

    /// <param name="gameDir">The Quake folder (holding id1/).</param>
    /// <param name="userDir">Where the game writes (config.cfg...): an empty folder, as the oracle's runs start
    /// without config.cfg.</param>
    /// <param name="args">The command line after the program's name (e.g. +playdemo demo1).</param>
    /// <param name="width">Mode 0's size (-oraclevid).</param>
    /// <param name="height">Mode 0's size (-oraclevid).</param>
    /// <param name="framesPerSecond">The clock's steps per second (-oraclestep).</param>
    public TraceRun(string gameDir, string userDir, IEnumerable<string> args, int width = 320, int height = 240,
        int framesPerSecond = 64, Action<string>? print = null)
    {
        system = new VirtualSystem(print);
        step = 1.0 / framesPerSecond;

        // in a window, in mode 0, as Trace-Demos.ps1 runs the oracle (design.md decision 50)
        var commandLine = new CommandLine(["quake", "-startwindowed", "-noautostretch", .. args]);

        // -traceevents <file>: the scripted input, read relative to the current folder as the oracle reads it
        int e = commandLine.CheckParm("-traceevents");
        TraceEvents? events = e != 0 && e < commandLine.Argc - 1
            ? new TraceEvents(commandLine.Argv(e + 1), () => writer!.Frame)
            : null;

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
            cd: new VirtualCdDrive());
        writer = new TraceWriter(Engine, framesPerSecond);
        if (events != null)
        {
            events.Engine = Engine;
        }
    }

    public QuakeEngine Engine { get; }

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
    /// config.cfg).
    /// </summary>
    public IEnumerable<IReadOnlyList<string>> Frames(bool quitAfterDemo, int maxFrames = int.MaxValue)
    {
        bool wasPlaying = false;

        Engine.Host_Init();
        while (true)
        {
            if (writer.Frame >= maxFrames)
            {
                _ = Engine.Sys_Quit();
                yield break;
            }

            system.Time += step;
            try
            {
                Engine.Host_Frame((float)step);
            }
            catch (QuitException)
            {
                yield break;
            }

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
