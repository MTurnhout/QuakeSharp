using System.Diagnostics;
using System.Globalization;
using QuakeSharp.Core;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Diagnostics;
using QuakeSharp.Core.Platform;
using QuakeSharp.Platform.Sdl;
using QuakeSharp.Render.Vulkan;

// sys_win.c: WinMain. The engine runs Host_Init and then Host_Frame on the real clock, showing each frame it draws and
// playing its sound; closing the window quits. -window opens a window, -noaspect shows square pixels, -vkvalidation
// turns on the Vulkan validation layer, -quitafter <seconds> quits by itself.
//
// -recordevents <file> (design.md decision 34) runs as a trace does instead: a virtual clock of 1/64 s steps, paced to
// real time; the sound device's position following that clock, as the oracle's; no config.cfg (a fresh user folder,
// thrown away); and writes the keys as -traceevents lines, so the game replays the same in the oracle and in
// quakesharp-tools trace (with -traceevents <file> -oracleframes <the frames it reports>, from the same command line).
var commandLine = new CommandLine(["quakesharp", .. args]);
try
{
    return Run(commandLine);
}
catch (QuakeException e)
{
    Console.Error.WriteLine(e.Message);
    SdlWindow.ShowError("QuakeSharp", e.Message);
    return 1;
}

static int Run(CommandLine commandLine)
{
    using var window = new SdlWindow("QuakeSharp", 960, 720, fullscreen: commandLine.CheckParm("-window") == 0);
    using var presenter = new VulkanPresenter(
        new VulkanSurfaceSource(window.RequiredVulkanInstanceExtensions, window.CreateVulkanSurface, () => window.PixelSize),
        enableValidation: Debugger.IsAttached || commandLine.CheckParm("-vkvalidation") != 0,
        Console.WriteLine)
    {
        SquarePixels = commandLine.CheckParm("-noaspect") != 0,
    };

    int quitAfter = commandLine.CheckParm("-quitafter");
    double quitTime = quitAfter != 0 && quitAfter < commandLine.Argc - 1
        ? double.Parse(commandLine.Argv(quitAfter + 1), CultureInfo.InvariantCulture)
        : double.MaxValue;

    int record = commandLine.CheckParm("-recordevents");
    return record != 0 && record < commandLine.Argc - 1
        ? Record(commandLine, commandLine.Argv(record + 1), window, presenter, quitTime)
        : Play(commandLine, window, presenter, quitTime);
}

// sys_win.c: WinMain's loop on the real clock
static int Play(CommandLine commandLine, SdlWindow window, VulkanPresenter presenter, double quitTime)
{
    // what the game writes goes to the user's folder (design.md §9)
    string userdir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuakeSharp");
    var system = new RealSystem();
    using var sound = new SdlSoundDma();
    var engine = new QuakeEngine(new QuakeParms(".", commandLine) { userdir = userdir }, system, window, sound, presenter);

    try
    {
        engine.Host_Init();

        double oldtime = system.FloatTime();
        while (true)
        {
            double newtime = system.FloatTime();
            engine.Host_Frame((float)(newtime - oldtime));
            oldtime = newtime;

            if (newtime > quitTime)
            {
                throw engine.Sys_Quit();
            }

            // WinQuake runs Host_Frame as often as it can; a millisecond's rest changes nothing it shows
            Thread.Sleep(1);
        }
    }
    catch (QuitException)
    {
        return 0;
    }
}

// -recordevents: the oracle's run (tools/oracle/src/sys_oracle.c's main), paced to real time, with the keys recorded
static int Record(CommandLine commandLine, string path, SdlWindow window, VulkanPresenter presenter, double quitTime)
{
    const int FramesPerSecond = 64;
    const double Step = 1.0 / FramesPerSecond;

    string userdir = Path.Combine(Path.GetTempPath(), "quakesharp-record", Guid.NewGuid().ToString("N"));
    var system = new VirtualSystem(Console.Write);
    int frames = 0;
    using var recorder = new EventRecorder(window, path, () => frames);
    using var sound = new SdlSoundDma(clock: () => system.Time);
    var engine = new QuakeEngine(new QuakeParms(".", commandLine) { userdir = userdir }, system, recorder, sound, presenter);
    var clock = Stopwatch.StartNew();

    try
    {
        engine.Host_Init();
        while (true)
        {
            // the next frame's time on the virtual clock, when the real one gets there
            double wait = ((frames + 1) * Step) - clock.Elapsed.TotalSeconds;
            if (wait > 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds(wait));
            }

            system.Time += Step;
            engine.Host_Frame((float)Step);
            frames++;

            if (system.Time > quitTime)
            {
                throw engine.Sys_Quit();
            }
        }
    }
    catch (QuitException)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Recorded {frames} frames to {path}: replay with -traceevents {path} -oracleframes {frames}"));
        return 0;
    }
    finally
    {
        if (Directory.Exists(userdir))
        {
            Directory.Delete(userdir, recursive: true);
        }
    }
}

/// <summary>sys_win.c's Sys_Printf and Sys_FloatTime: the console's text on stdout, the time from a stopwatch.</summary>
internal sealed class RealSystem : ISystem
{
    private readonly Stopwatch clock = Stopwatch.StartNew();

    public void Printf(string text) => Console.Write(text);

    public double FloatTime() => clock.Elapsed.TotalSeconds;
}
