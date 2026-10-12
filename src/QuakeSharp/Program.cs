using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using QuakeSharp.Core;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Platform;
using QuakeSharp.Platform.Sdl;
using QuakeSharp.Render.Vulkan;

// sys_win.c: WinMain. The engine runs Host_Init and then Host_Frame on the real clock. Until the renderer is ported
// (milestone 6) the window shows gfx/conback.lmp and the console's text goes to stdout; keys reach the engine (type
// "echo hello" and Enter). Closing the window quits. -window opens a window, -noaspect shows square pixels,
// -vkvalidation turns on the Vulkan validation layer, -quitafter <seconds> quits by itself.
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

    // what the game writes goes to the user's folder (design.md §9)
    string userdir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuakeSharp");
    var system = new RealSystem();
    var engine = new QuakeEngine(new QuakeParms(".", commandLine) { userdir = userdir }, system, window);

    int quitAfter = commandLine.CheckParm("-quitafter");
    double quitTime = quitAfter != 0 && quitAfter < commandLine.Argc - 1
        ? double.Parse(commandLine.Argv(quitAfter + 1), CultureInfo.InvariantCulture)
        : double.MaxValue;

    try
    {
        engine.Host_Init();

        byte[] conback = engine.COM_LoadHunkFile("gfx/conback.lmp") ?? throw new QuakeException("Couldn't load gfx/conback.lmp");
        int width = BinaryPrimitives.ReadInt32LittleEndian(conback);
        int height = BinaryPrimitives.ReadInt32LittleEndian(conback.AsSpan(4));
        byte[] frame = conback.AsSpan(8, width * height).ToArray();

        // sys_win.c: WinMain's loop
        double oldtime = system.FloatTime();
        while (true)
        {
            double newtime = system.FloatTime();
            engine.Host_Frame((float)(newtime - oldtime));
            oldtime = newtime;

            presenter.Present(frame, width, height, width, engine.host_basepal);

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

/// <summary>sys_win.c's Sys_Printf and Sys_FloatTime: the console's text on stdout, the time from a stopwatch.</summary>
internal sealed class RealSystem : ISystem
{
    private readonly Stopwatch clock = Stopwatch.StartNew();

    public void Printf(string text) => Console.Write(text);

    public double FloatTime() => clock.Elapsed.TotalSeconds;
}
