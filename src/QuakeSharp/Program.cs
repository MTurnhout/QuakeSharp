using System.Diagnostics;
using System.Globalization;
using QuakeSharp.Core;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Diagnostics;
using QuakeSharp.Core.Enhanced;
using QuakeSharp.Core.Net;
using QuakeSharp.Core.Platform;
using QuakeSharp.Platform.Sdl;
using QuakeSharp.Render.Vulkan;

// sys_win.c: WinMain. The engine runs Host_Init and then Host_Frame on the real clock, showing each frame it draws and
// playing its sound and the CD's music from the user's track files (design.md §7.2); closing the window quits. It
// starts as Winquake.exe does, in the first full-screen mode (design.md decision 50); -window (WinQuake's
// -startwindowed) starts in a window. -noaspect shows square pixels, -vkvalidation turns on the Vulkan validation
// layer, -quitafter <seconds> quits by itself. Scroll Lock switches the enhanced mode (design.md §11.6, decision 61),
// which only reads the game; quakesharp.cfg in the user folder keeps it and its settings, -enhanced and -faithful set
// it for a run.
//
// -recordevents <file> (design.md decision 34) runs as a trace does instead: a virtual clock of 1/64 s steps, paced to
// real time; the sound device's position following that clock, as the oracle's; the oracle's virtual CD (the track
// files only heard); no joystick; no config.cfg (a fresh user folder, thrown away); in a window in mode 0
// (-startwindowed -noautostretch, as the traces); and writes the keys and the mouse as -traceevents lines, so the game
// replays the same in the oracle and in quakesharp-tools trace (with -traceevents <file> -oracleframes <the frames it
// reports>, from the same command line). Its network is the traces' virtual machine with this one's name and address,
// out through the real network; what arrives is written beside the events (<file>.dgrams, for -tracedatagrams).
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
    int quitAfter = commandLine.CheckParm("-quitafter");
    double quitTime = quitAfter != 0 && quitAfter < commandLine.Argc - 1
        ? double.Parse(commandLine.Argv(quitAfter + 1), CultureInfo.InvariantCulture)
        : double.MaxValue;

    if (commandLine.CheckParm("-dedicated") != 0)
    {
        return Dedicated(commandLine, quitTime);
    }

    // a window until the engine sets its first mode
    using var window = new SdlWindow("QuakeSharp", 640, 480, fullscreen: false);
    using var presenter = new VulkanPresenter(
        new VulkanSurfaceSource(window.RequiredVulkanInstanceExtensions, window.CreateVulkanSurface, () => window.PixelSize),
        enableValidation: Debugger.IsAttached || commandLine.CheckParm("-vkvalidation") != 0,
        Console.WriteLine)
    {
        SquarePixels = commandLine.CheckParm("-noaspect") != 0,
    };

    // the enhanced mode's settings, and its renderer drawing on a thread of its own
    string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuakeSharp", "quakesharp.cfg");
    EnhancedSettings settings = EnhancedSettings.Load(settingsPath);
    bool forRun = commandLine.CheckParm("-enhanced") != 0 || commandLine.CheckParm("-faithful") != 0;
    bool enhanced = commandLine.CheckParm("-enhanced") != 0 || (commandLine.CheckParm("-faithful") == 0 && settings.Enhanced);
    using var renderer = new EnhancedRenderer(presenter, new EnhancedOptions(
        settings.Number("enhanced_scale"), (int)settings.Number("enhanced_msaa"), settings.Number("enhanced_widescreen") != 0,
        (int)settings.Number("enhanced_filter"), (int)settings.Number("enhanced_mipmaps"), settings.Number("enhanced_anisotropy"),
        (int)settings.Number("enhanced_lighting"), (int)settings.Number("enhanced_particles"))
    {
        Interpolate = settings.Number("enhanced_interpolate") != 0,
        Vsync = settings.Number("enhanced_vsync") != 0,
        MaxFps = (int)settings.Number("enhanced_maxfps"),
        LerpModels = settings.Number("enhanced_lerpmodels") != 0,
        LerpMove = settings.Number("enhanced_lerpmove") != 0,
        Textures = settings.Number("enhanced_textures") != 0,
        Materials = settings.Number("enhanced_materials") != 0,
    });
    var mode = new EnhancedMode(renderer, enhancedSound: settings.Number("enhanced_sound") != 0,
        placement: settings.Number("enhanced_3d_sfx") != 0);
    window.ScrollLock = mode.Toggle;

    try
    {
        int record = commandLine.CheckParm("-recordevents");
        return record != 0 && record < commandLine.Argc - 1
            ? Record(commandLine, commandLine.Argv(record + 1), window, presenter, mode, enhanced, quitTime)
            : Play(commandLine, window, presenter, mode, enhanced, quitTime);
    }
    finally
    {
        if (renderer.FramesDrawn > 0)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Enhanced mode: {renderer.FramesDrawn} frames drawn, {renderer.DrawingTime.TotalMilliseconds / renderer.FramesDrawn:F2} ms each"));
        }

        // the switches set it for their run only
        if (!forRun)
        {
            settings.Enhanced = mode.On;
        }

        settings.Save(settingsPath);
    }
}

// sys_win.c: WinMain's loop on the real clock
static int Play(CommandLine commandLine, SdlWindow window, VulkanPresenter presenter, EnhancedMode mode, bool enhanced,
    double quitTime)
{
    // what the game writes goes to the user's folder (design.md §9)
    string userdir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuakeSharp");
    var system = new RealSystem();
    using var sound = new SdlSoundDma();
    mode.Sound = sound;
    using var cd = new FileCdDrive(CdTracks.Find(".", commandLine));
    using var joysticks = new SdlJoysticks();
    // the real network (design.md §11.5)
    using var winsock = new SocketWinsock();
    var engine = new QuakeEngine(new QuakeParms(".", commandLine) { userdir = userdir }, system, window, sound,
        new WindowVideo(window, presenter, mode), cd, mouse: window, joysticks: joysticks, winsock: winsock);
    mode.Attach(engine, enhanced);

    // WM_ACTIVATE: DirectSound silences the game while another program has the focus; AppActivate clears the buffer
    window.Activated = (active, minimized) =>
    {
        sound.Muted = !active;
        engine.WM_ACTIVATE(active, minimized);
    };

    try
    {
        engine.Host_Init();
        presenter.TexturePack = TexturePack(engine, userdir, userdir);

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

// The texture packs of the game's directories (design.md §11.7, decision 68): each in the user folder, then in the
// Quake folder, a -game directory's before id1's; read by the enhanced view only. A run whose user folder is not the
// real one (-recordevents) looks in the real one's instead.
static EnhancedTexturePack? TexturePack(QuakeEngine engine, string userdir, string realUserdir)
{
    string[] directories = [.. engine.FileSystem.LooseDirectories().Select(d =>
        Path.GetFullPath(d).StartsWith(Path.GetFullPath(userdir), StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(realUserdir, Path.GetRelativePath(userdir, d))
            : d)];
    var pack = new EnhancedTexturePack(directories);
    if (pack.Count == 0)
    {
        return null;
    }

    Console.WriteLine($"Texture pack: {pack.Count} files in {string.Join(", ", directories.Where(Directory.Exists))}");
    return pack;
}

// sys_win.c: WinMain for a dedicated server (-dedicated): no window, video or sound; the console's text on standard
// output and its input from standard input, edited and echoed by Sys_ConsoleInput; a frame once sys_ticrate has
// passed, sleeping between. A Sys_Error shows in the console, which waits for Enter (up to a minute) before exiting.
static int Dedicated(CommandLine commandLine, double quitTime)
{
    string userdir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuakeSharp");
    using var console = new DedicatedConsole();
    var system = new RealSystem(console);
    using var winsock = new SocketWinsock();
    var engine = new QuakeEngine(new QuakeParms(".", commandLine) { userdir = userdir }, system, winsock: winsock);

    try
    {
        engine.Sys_Printf("Host_Init\n");
        engine.Host_Init();

        double oldtime = engine.Sys_FloatTime();
        while (true)
        {
            double newtime = engine.Sys_FloatTime();
            double time = newtime - oldtime;

            while (time < engine.sys_ticrate.value)
            {
                engine.Sys_Sleep();
                newtime = engine.Sys_FloatTime();
                time = newtime - oldtime;
            }

            engine.Host_Frame((float)time);
            oldtime = newtime;

            if (newtime > quitTime)
            {
                throw engine.Sys_Quit();
            }
        }
    }
    catch (QuitException)
    {
        return 0;
    }
    catch (QuakeException e)
    {
        engine.Sys_ErrorDedicated(e.Message, wait: true);
        return 1;
    }
}

// -recordevents: the oracle's run (tools/oracle/src/sys_oracle.c's main), paced to real time, with the keys recorded
static int Record(CommandLine commandLine, string path, SdlWindow window, VulkanPresenter presenter, EnhancedMode mode,
    bool enhanced, double quitTime)
{
    const int FramesPerSecond = 64;
    const double Step = 1.0 / FramesPerSecond;

    string userdir = Path.Combine(Path.GetTempPath(), "quakesharp-record", Guid.NewGuid().ToString("N"));
    var system = new VirtualSystem(Console.Write, Step);
    int frames = 0;
    using var recorder = new EventRecorder(window, path, () => frames);
    using var sound = new SdlSoundDma(clock: () => system.Time);
    mode.Sound = sound;
    using var cd = new ListeningCdDrive(CdTracks.Find(".", commandLine));
    // the machine as the traces' virtual one, out through the real network: what arrives at each step is written to
    // the datagrams file beside the events, with the machine's name, address, ports and lookups (design.md §11.5)
    string datagrams = Path.ChangeExtension(path, ".dgrams");
    using var network = new NetworkRecorder(datagrams);
    var winsock = new VirtualWinsock(network.Name, network.Addr, () => system.Steps, network);
    network.Winsock = winsock;
    // the window takes the mouse as in a game; no joystick, as the oracle has none
    var engine = new QuakeEngine(new QuakeParms(".", commandLine) { userdir = userdir }, system, recorder, sound,
        new WindowVideo(window, presenter, mode), cd, mouse: window, winsock: winsock);
    mode.Attach(engine, enhanced);

    // silent while another program has the focus, but without AppActivate's clears: the oracle never loses the focus
    window.Activated = (active, _) => sound.Muted = !active;
    var clock = Stopwatch.StartNew();

    // before every step of the clock, a frame's or one waited within a frame: the real clock gets there, and what the
    // real network delivered meanwhile arrives at that step
    system.Waiting = () =>
    {
        double wait = (system.Time + Step) - clock.Elapsed.TotalSeconds;
        if (wait > 0)
        {
            Thread.Sleep(TimeSpan.FromSeconds(wait));
        }

        network.Poll(system.Steps + 1);
    };

    try
    {
        engine.Sys_Printf("Host_Init\n");
        engine.Host_Init();
        presenter.TexturePack = TexturePack(engine, userdir,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuakeSharp"));
        double oldtime = system.Time;
        while (true)
        {
            system.Waiting!();
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
            $"Recorded {frames} frames to {path} and {datagrams}: replay with -traceevents {path} -tracedatagrams {datagrams} -oracleframes {frames}"));
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

/// <summary>
/// The enhanced mode switched on and off (design.md §11.6): on, the engine has a capture whose snapshots go to the
/// enhanced renderer, and the faithful frames are not shown; off, nothing of it runs.
/// </summary>
internal sealed class EnhancedMode(EnhancedRenderer renderer, SdlSoundDma? sound = null, bool enhancedSound = true,
    bool placement = false)
{
    private readonly EnhancedMixer mixer = new() { Placement = placement };
    private QuakeEngine? engine;

    public bool On { get; private set; }

    public EnhancedRenderer Renderer => renderer;

    /// <summary>The engine, before Host_Init, and whether the mode starts on.</summary>
    public void Attach(QuakeEngine e, bool on)
    {
        engine = e;
        Set(on);
    }

    /// <summary>Scroll Lock.</summary>
    public void Toggle() => Set(!On);

    private void Set(bool on)
    {
        On = on;
        if (engine == null)
        {
            return;
        }

        if (on)
        {
            var capture = new EnhancedCapture(engine) { Sound = enhancedSound ? mixer : null };
            capture.Captured += renderer.Submit;
            engine.enhanced = capture;
        }
        else
        {
            engine.enhanced = null;
            renderer.Deactivate();
        }

        // the device plays the enhanced mixer's ring while the mode has the sound
        sound?.UseEnhanced(on && enhancedSound ? mixer : null);
    }

    /// <summary>The device the enhanced sound plays on (made after the mode).</summary>
    public SdlSoundDma? Sound
    {
        get => sound;
        set => sound = value;
    }
}

/// <summary>
/// vid_win.c's screen: the frames through the presenter, the modes on the window. In the enhanced mode the enhanced
/// renderer shows the frames instead.
/// </summary>
internal sealed class WindowVideo(SdlWindow window, VulkanPresenter presenter, EnhancedMode mode) : IVideoOutput
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
        if (!mode.On)
        {
            presenter.Present(pixels, width, height, rowBytes, palette);
        }

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
        bool show = Stopwatch.GetElapsedTime(shownAt) >= TimeSpan.FromSeconds(1.0 / 60);
        if (mode.On)
        {
            mode.Renderer.DirectRect(pixels, rowBytes, x, y, width, height, show);
        }
        else if (show)
        {
            presenter.Present(shown, shownWidth, shownHeight, shownWidth, shownPalette);
        }

        if (show)
        {
            shownAt = Stopwatch.GetTimestamp();
        }
    }

    public void SetMode(int width, int height, bool fullscreen) => window.SetMode(width, height, fullscreen);
}

/// <summary>
/// sys_win.c's Sys_Printf, Sys_FloatTime and Sys_Sleep, and a dedicated server's console: the console's text on stdout
/// (a dedicated server's through its console), the time from a stopwatch.
/// </summary>
internal sealed class RealSystem(DedicatedConsole? console = null) : ISystem
{
    private readonly Stopwatch clock = Stopwatch.StartNew();

    public void Printf(string text)
    {
        if (console == null)
        {
            Console.Write(text);
        }
    }

    public double FloatTime() => clock.Elapsed.TotalSeconds;

    public void Sleep() => Thread.Sleep(1);

    public void ConsoleWrite(ReadOnlySpan<byte> text) => console?.Write(text);

    public bool ConsoleRead(out byte ch)
    {
        ch = 0;
        return console != null && console.Read(out ch);
    }
}
