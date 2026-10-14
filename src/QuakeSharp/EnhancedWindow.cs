using System.Diagnostics;
using System.Globalization;
using QuakeSharp.Core.Enhanced;
using QuakeSharp.Platform.Sdl;
using QuakeSharp.Render.Vulkan;

/// <summary>
/// The enhanced mode's window (design.md §11.9): Alt+Enter between a window and the whole screen, the window's place,
/// size and full screen remembered in quakesharp.cfg, the frame rate in the title, Print Screen's PNG screenshot. While
/// the mode is off the window is the game's mode's, as vid_win.c sets it, and none of this runs.
/// </summary>
internal sealed class EnhancedWindow(SdlWindow window, EnhancedSettings settings, EnhancedRenderer renderer, string userdir)
{
    private const string Title = "QuakeSharp";

    // the mode the game set last (vid_win.c's), to go back to when the mode goes off
    private (int Width, int Height, bool Fullscreen)? gameMode;
    private bool on;

    // the frame rate's count since the title last changed
    private long countedAt = Stopwatch.GetTimestamp(), countedFrames;
    private TimeSpan countedTime;
    private bool titled;

    /// <summary>The game set a mode: the window takes it, unless the enhanced mode keeps the player's.</summary>
    public void GameSetMode(int width, int height, bool fullscreen)
    {
        gameMode = (width, height, fullscreen);
        if (!(on && Apply()))
        {
            window.SetMode(width, height, fullscreen);
        }
    }

    /// <summary>The enhanced mode switched on (the player's window) or off (the game's mode's again).</summary>
    public void Set(bool enhanced)
    {
        if (on && !enhanced)
        {
            Remember();
            if (gameMode is (int w, int h, bool full))
            {
                window.SetMode(w, h, full);
            }

            if (titled)
            {
                window.Title = Title;
                titled = false;
            }
        }

        on = enhanced;
        if (on)
        {
            Apply();
        }
    }

    /// <summary>Alt+Enter: a window or the whole screen, in the enhanced mode only (false: not handled).</summary>
    public bool ToggleFullscreen()
    {
        if (!on)
        {
            return false;
        }

        bool full = !window.Fullscreen;
        if (!full && Parse(settings["enhanced_window"]) is { } geometry)
        {
            window.Fullscreen = false;
            window.Geometry = geometry;
        }
        else
        {
            window.Fullscreen = full;
        }

        settings["enhanced_fullscreen"] = full ? "1" : "0";
        return true;
    }

    /// <summary>Print Screen: the enhanced frame as PNG in the user folder's screenshots, at the window's size.</summary>
    public void Screenshot()
    {
        if (!on)
        {
            return;
        }

        (int w, int h) = window.PixelSize;
        string path = Path.Combine(userdir, "screenshots",
            $"quakesharp-{DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}.png");
        renderer.RequestScreenshot(path, w, h);
    }

    /// <summary>Between frames: the frame rate in the title once a second, while enhanced_showfps is on.</summary>
    public void Tick()
    {
        if (!on || settings.Number("enhanced_showfps") == 0)
        {
            return;
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(countedAt);
        if (elapsed < TimeSpan.FromSeconds(1))
        {
            return;
        }

        long frames = renderer.FramesDrawn - countedFrames;
        TimeSpan drawing = renderer.DrawingTime - countedTime;
        window.Title = frames > 0
            ? string.Create(CultureInfo.InvariantCulture,
                $"{Title} - {frames / elapsed.TotalSeconds:F0} fps, {drawing.TotalMilliseconds / frames:F2} ms a frame")
            : Title;
        titled = true;
        countedAt = Stopwatch.GetTimestamp();
        countedFrames = renderer.FramesDrawn;
        countedTime = renderer.DrawingTime;
    }

    /// <summary>At the end: the window's place and size kept for the next run, if the enhanced mode has it.</summary>
    public void Remember()
    {
        if (on && !window.Fullscreen)
        {
            (int x, int y, int w, int h) = window.Geometry;
            settings["enhanced_window"] = string.Create(CultureInfo.InvariantCulture, $"{x} {y} {w} {h}");
        }
    }

    // the player's window or full screen, if they chose one (false: the game's mode stays)
    private bool Apply()
    {
        int full = (int)settings.Number("enhanced_fullscreen");
        if (full == 1)
        {
            window.Fullscreen = true;
            return true;
        }

        if (Parse(settings["enhanced_window"]) is { } geometry)
        {
            window.Fullscreen = false;
            window.Geometry = geometry;
            return true;
        }

        if (full == 0)
        {
            if (gameMode is { Fullscreen: true })
            {
                window.Fullscreen = false;
                return true;
            }
        }

        return false;
    }

    // "x y width height", a window of at least 160x120
    internal static (int X, int Y, int Width, int Height)? Parse(string text)
    {
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            return null;
        }

        var numbers = new int[4];
        for (int i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return numbers[2] >= 160 && numbers[3] >= 120 ? (numbers[0], numbers[1], numbers[2], numbers[3]) : null;
    }
}
