using System.Diagnostics;
using System.Globalization;
using QuakeSharp.Core;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Diagnostics;
using QuakeSharp.Core.Net;
using QuakeSharp.Core.Platform;
using QuakeSharp.Platform.Sdl;
using QuakeSharp.Render.Vulkan;

// sys_win.c: WinMain. The engine runs Host_Init and then Host_Frame on the real clock, showing each frame it draws and
// playing its sound and the CD's music from the user's track files (design.md §7.2); closing the window quits. It
// starts as Winquake.exe does, in the first full-screen mode (design.md decision 50); -window (WinQuake's
// -startwindowed) starts in a window. -noaspect shows square pixels, -vkvalidation turns on the Vulkan validation
// layer, -quitafter <seconds> quits by itself.
//
// -recordevents <file> (design.md decision 34) runs as a trace does instead: a virtual clock of 1/64 s steps, paced to
// real time; the sound device's position following that clock, as the oracle's; the oracle's virtual CD (the track
// files only heard); no joystick; no config.cfg (a fresh user folder, thrown away); in a window in mode 0
// (-startwindowed -noautostretch, as the traces); and writes the keys and the mouse as -traceevents lines, so the game
// replays the same in the oracle and in quakesharp-tools trace (with -traceevents <file> -oracleframes <the frames it
// reports>, from the same command line).
string[] switches = args.Contains("-recordevents") ? ["-startwindowed", "-noautostretch"]
    : args.Contains("-window") ? ["-startwindowed"]
    : [];
var commandLine = new CommandLine(["quakesharp", .. switches, .. args]);
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
    // a window until the engine sets its first mode
    using var window = new SdlWindow("QuakeSharp", 640, 480, fullscreen: false);
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
    using var cd = new FileCdDrive(CdTracks.Find(".", commandLine));
    using var joysticks = new SdlJoysticks();
    // the virtual machine's Winsock, alone on its LAN, until the real network's comes (design.md §11.5, step 4)
    var winsock = new VirtualWinsock(1, () => (long)(system.FloatTime() * 64));
    var engine = new QuakeEngine(new QuakeParms(".", commandLine) { userdir = userdir }, system, window, sound,
        new WindowVideo(window, presenter), cd, mouse: window, joysticks: joysticks, winsock: winsock);

    // WM_ACTIVATE: DirectSound silences the game while another program has the focus; AppActivate clears the buffer
    window.Activated = (active, minimized) =>
    {
        sound.Muted = !active;
        engine.WM_ACTIVATE(active, minimized);
    };

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
    var system = new VirtualSystem(Console.Write, Step);
    int frames = 0;
    using var recorder = new EventRecorder(window, path, () => frames);
    using var sound = new SdlSoundDma(clock: () => system.Time);
    using var cd = new ListeningCdDrive(CdTracks.Find(".", commandLine));
    // the window takes the mouse as in a game; no joystick, as the oracle has none
    var engine = new QuakeEngine(new QuakeParms(".", commandLine) { userdir = userdir }, system, recorder, sound,
        new WindowVideo(window, presenter), cd, mouse: window, winsock: new VirtualWinsock(1, () => system.Steps));

    // silent while another program has the focus, but without AppActivate's clears: the oracle never loses the focus
    window.Activated = (active, _) => sound.Muted = !active;
    var clock = Stopwatch.StartNew();

    try
    {
        engine.Host_Init();
        double oldtime = system.Time;
        while (true)
        {
            // the next frame's time on the virtual clock, when the real one gets there (after a frame that waited for
            // the network, the clock is ahead: design.md §11.5)
            double wait = (system.Time + Step) - clock.Elapsed.TotalSeconds;
            if (wait > 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds(wait));
            }

            system.Step();
            double newtime = system.Time;
            engine.Host_Frame((float)(newtime - oldtime));
            oldtime = newtime;
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

/// <summary>vid_win.c's screen: the frames through the presenter, the modes on the window.</summary>
internal sealed class WindowVideo(SdlWindow window, VulkanPresenter presenter) : IVideoOutput
{
    // the frame last presented, which a rectangle (the loading disc) is shown over, and when
    private readonly byte[] shownPalette = new byte[768];
    private byte[] shown = [];
    private int shownWidth, shownHeight;
    private long shownAt;

    public void Present(ReadOnlySpan<byte> pixels, int width, int height, int rowBytes, ReadOnlySpan<byte> palette)
    {
        if (shown.Length != width * height)
        {
            shown = new byte[width * height];
        }

        shownWidth = width;
        shownHeight = height;
        for (int y = 0; y < height; y++)
        {
            pixels.Slice(y * rowBytes, width).CopyTo(shown.AsSpan(y * width, width));
        }

        palette[..768].CopyTo(shownPalette);
        presenter.Present(pixels, width, height, rowBytes, palette);
        shownAt = Stopwatch.GetTimestamp();
    }

    public void PresentRect(ReadOnlySpan<byte> pixels, int rowBytes, int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || x + width > shownWidth || y + height > shownHeight)
        {
            return;
        }

        for (int row = y; row < y + height; row++)
        {
            pixels.Slice((row * rowBytes) + x, width).CopyTo(shown.AsSpan((row * shownWidth) + x, width));
        }

        // a file's read is short: shown at most at the display's pace, so loading never waits for its refresh
        if (Stopwatch.GetElapsedTime(shownAt) < TimeSpan.FromSeconds(1.0 / 60))
        {
            return;
        }

        presenter.Present(shown, shownWidth, shownHeight, shownWidth, shownPalette);
        shownAt = Stopwatch.GetTimestamp();
    }

    public void SetMode(int width, int height, bool fullscreen) => window.SetMode(width, height, fullscreen);
}

/// <summary>sys_win.c's Sys_Printf and Sys_FloatTime: the console's text on stdout, the time from a stopwatch.</summary>
internal sealed class RealSystem : ISystem
{
    private readonly Stopwatch clock = Stopwatch.StartNew();

    public void Printf(string text) => Console.Write(text);

    public double FloatTime() => clock.Elapsed.TotalSeconds;
}
