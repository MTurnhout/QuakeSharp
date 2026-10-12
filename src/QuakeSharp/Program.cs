using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using QuakeSharp.Core;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Sound;
using QuakeSharp.Core.UI;
using QuakeSharp.Platform.Sdl;
using QuakeSharp.Render.Vulkan;

// Milestone 3: the platform and the presentation, before there is an engine to run. Shows gfx/conback.lmp through the
// game's palette in the window (borderless over the screen; -window for a window, -noaspect for square pixels), logs
// the key events as Quake's key numbers, and plays the sound device's (silent) buffer. Escape quits.
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
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"QuakeSharp, a port of WinQuake {QuakeDef.VERSION:F2}"));

    var fileSystem = new FileSystem(commandLine, Console.Write, _ => { });
    fileSystem.InitFilesystem(".");
    byte[] palette = fileSystem.LoadFile("gfx/palette.lmp") ?? throw new QuakeException("Couldn't load gfx/palette.lmp");
    byte[] conback = fileSystem.LoadFile("gfx/conback.lmp") ?? throw new QuakeException("Couldn't load gfx/conback.lmp");

    // wad.h: qpic_t
    int width = BinaryPrimitives.ReadInt32LittleEndian(conback);
    int height = BinaryPrimitives.ReadInt32LittleEndian(conback.AsSpan(4));
    byte[] frame = conback.AsSpan(8, width * height).ToArray();

    using var window = new SdlWindow("QuakeSharp", 960, 720, fullscreen: commandLine.CheckParm("-window") == 0);
    using var presenter = new VulkanPresenter(
        new VulkanSurfaceSource(window.RequiredVulkanInstanceExtensions, window.CreateVulkanSurface, () => window.PixelSize),
        enableValidation: Debugger.IsAttached || commandLine.CheckParm("-vkvalidation") != 0,
        Console.WriteLine)
    {
        SquarePixels = commandLine.CheckParm("-noaspect") != 0,
    };

    using var sound = new SdlSoundDma();
    Dma? shm = sound.Init();
    Console.WriteLine(shm == null ? "No sound device" : $"Sound: {shm.speed} Hz, {shm.channels} channels, {shm.samplebits} bits");

    // -quitafter <seconds>: for trying it out unattended
    int quitAfter = commandLine.CheckParm("-quitafter");
    double quitTime = quitAfter != 0 && quitAfter < commandLine.Argc - 1
        ? double.Parse(commandLine.Argv(quitAfter + 1), CultureInfo.InvariantCulture)
        : double.MaxValue;

    bool running = true;
    var clock = Stopwatch.StartNew();
    double next = 0;
    while (running && clock.Elapsed.TotalSeconds < quitTime)
    {
        running = window.SendKeyEvents((key, down) =>
        {
            Console.WriteLine($"key {key} {(down ? "down" : "up")}");
            if (key == Keys.K_ESCAPE && down)
            {
                running = false;
            }
        }) && running;

        presenter.Present(frame, width, height, width, palette);

        // about 72 frames a second, Host_FilterTime's limit
        next += 1.0 / 72;
        double wait = next - clock.Elapsed.TotalSeconds;
        if (wait > 0)
        {
            Thread.Sleep(TimeSpan.FromSeconds(wait));
        }
        else
        {
            next = clock.Elapsed.TotalSeconds;
        }
    }

    sound.Shutdown();
    return 0;
}
