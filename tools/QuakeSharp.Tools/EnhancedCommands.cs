using System.Globalization;
using QuakeSharp.Core;
using QuakeSharp.Core.Diagnostics;
using QuakeSharp.Core.Enhanced;
using QuakeSharp.Render.Vulkan;

namespace QuakeSharp.Tools;

internal static class EnhancedCommands
{
    // The comparison of design.md §11.6 (needs a GPU): enhanced-shot <quake-dir> <out-dir> <frames> [trace args] runs a
    // trace's command line as `trace` does, and at each frame of the comma-separated list draws the enhanced frame
    // offscreen at faithful settings (the frame's own size and shape, nearest texels with the BSP's mips, the colormap's
    // lighting, no MSAA, no widening) beside the software frame, and once more at the defaults, four times the size. It
    // writes <frame>.software.png, <frame>.enhanced.png, <frame>.diff.png (the view's differing pixels in red over the
    // software frame in grey) and <frame>.default.png, and prints the share of the view's pixels that are the same
    // colour, and that are within 24 of it on every channel. <frames> "warp" instead lists the runs of frames whose view
    // is warped as under water (r_dowarp), to choose frames from; "dlights" those with dynamic lights. -pack <game-dir> draws the default frame with a texture
    // pack's textures (design.md §11.7; the faithful comparison never uses them).
    // The default frame is drawn at quakesharp.cfg's defaults; -set <name> <value> (any number of times) changes one of them,
    // and -lighting, -textures, -materials <value> are short for -set enhanced_lighting and the like; -vkvalidation turns on
    // the Vulkan validation layer.
    public static int Shot(string baseDir, string outDir, string frameList, string[] args)
    {
        int packArg = Array.IndexOf(args, "-pack");
        string? packDir = packArg >= 0 && packArg + 1 < args.Length ? args[packArg + 1] : null;
        args = packArg >= 0 ? [.. args[..packArg], .. args[Math.Min(args.Length, packArg + 2)..]] : args;
        var settings = new EnhancedSettings();
        args = TakeSettings(args, settings);
        bool validation = args.Contains("-vkvalidation");
        args = [.. args.Where(a => a != "-vkvalidation")];
        Commands.RefuseRepository(outDir);
        Directory.CreateDirectory(outDir);
        bool scanWarp = frameList == "warp", scanDlights = frameList == "dlights";
        var wanted = scanWarp || scanDlights ? [] : new HashSet<int>(frameList.Split(',').Select(f => int.Parse(f, CultureInfo.InvariantCulture)));
        int last = scanWarp || scanDlights ? int.MaxValue : wanted.Max();
        int warpFrom = 0, dlightsFrom = 0;

        string userDir = Path.Combine(Path.GetTempPath(), "quakesharp-trace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDir);
        try
        {
            var run = new TraceRun(baseDir, userDir, args);
            var capture = new EnhancedCapture(run.Engine);
            run.Engine.enhanced = capture;

            using VulkanPresenter presenter = VulkanPresenter.Headless(enableValidation: validation, Console.Error.WriteLine);
            if (packDir != null)
            {
                presenter.TexturePack = new EnhancedTexturePack([packDir]);
            }

            var faithful =new EnhancedOptions(Scale: 1, Msaa: 1, Widescreen: false, Filter: 0, Mipmaps: 0, Anisotropy: 1, Lighting: 1);
            EnhancedOptions defaults = EnhancedOptions.FromSettings(settings);

            int frame = 0;
            foreach (IReadOnlyList<string> _ in run.Frames(quitAfterDemo: new Core.Common.CommandLine(["quake", .. args]).CheckParm("-tracequitafterdemo") != 0))
            {
                frame++;
                if (scanWarp && capture.Latest is { } seen && seen.Warp != (warpFrom != 0))
                {
                    if (warpFrom != 0)
                    {
                        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"warped: frames {warpFrom}-{frame - 1}"));
                    }

                    warpFrom = seen.Warp ? frame : 0;
                }

                if (scanDlights && capture.Latest is { } lit && lit.Dlights.Length > 0 != (dlightsFrom != 0))
                {
                    if (dlightsFrom != 0)
                    {
                        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"dlights: frames {dlightsFrom}-{frame - 1}"));
                    }

                    dlightsFrom = lit.Dlights.Length > 0 ? frame : 0;
                }

                if (wanted.Contains(frame) && capture.Latest is { } shot)
                {
                    presenter.SquarePixels = true;
                    byte[] enhanced = presenter.RenderEnhanced(shot, faithful, shot.Width, shot.Height);
                    presenter.SquarePixels = false;
                    byte[] look = presenter.RenderEnhanced(shot, defaults, shot.Width * 4, shot.Width * 3);
                    if (packDir != null)
                    {
                        // the models first drawn just now asked for their replacements: drawn again with them
                        presenter.LoadTexturePack();
                        look = presenter.RenderEnhanced(shot, defaults, shot.Width * 4, shot.Width * 3);
                    }
                    Compare(outDir, frame, shot, enhanced, look);
                }

                if (frame >= last)
                {
                    break;
                }
            }
        }
        finally
        {
            Directory.Delete(userDir, recursive: true);
        }

        return 0;
    }

    // -set <name> <value> and its short forms taken out of the arguments into the settings
    private static string[] TakeSettings(string[] args, EnhancedSettings settings)
    {
        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string? name = args[i] switch
            {
                "-set" when i + 2 < args.Length => args[++i],
                "-lighting" or "-textures" or "-materials" when i + 1 < args.Length => "enhanced_" + args[i][1..],
                _ => null,
            };
            if (name == null)
            {
                rest.Add(args[i]);
                continue;
            }

            settings[name] = args[++i];
        }

        return [.. rest];
    }

    // enhanced-bench <quake-dir> <frame> <width>x<height> [trace args]: draws the enhanced frame of a trace's frame
    // offscreen at the defaults (4x MSAA, true colour, mip chains, anisotropy) 200 times at that size and prints the time
    // each took on average, the read back of the picture included (needs a GPU)
    public static int Bench(string baseDir, string frameText, string size, string[] args)
    {
        int wanted = int.Parse(frameText, CultureInfo.InvariantCulture);
        int width = int.Parse(size.Split('x')[0], CultureInfo.InvariantCulture);
        int height = int.Parse(size.Split('x')[1], CultureInfo.InvariantCulture);
        string userDir = Path.Combine(Path.GetTempPath(), "quakesharp-trace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDir);
        try
        {
            var run = new TraceRun(baseDir, userDir, args);
            var capture = new EnhancedCapture(run.Engine);
            run.Engine.enhanced = capture;
            using VulkanPresenter presenter = VulkanPresenter.Headless(enableValidation: false, Console.Error.WriteLine);
            int frame = 0;
            foreach (IReadOnlyList<string> _ in run.Frames(quitAfterDemo: false, maxFrames: wanted))
            {
                frame++;
            }

            EnhancedFrame shot = capture.Latest ?? throw new QuakeException("no frame");
            var options = new EnhancedOptions();
            _ = presenter.RenderEnhanced(shot, options, width, height); // models and targets made ready
            var clock = System.Diagnostics.Stopwatch.StartNew();
            const int Times = 200;
            for (int i = 0; i < Times; i++)
            {
                _ = presenter.RenderEnhanced(shot, options, width, height);
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"frame {frame} at {width}x{height}: {clock.Elapsed.TotalMilliseconds / Times:F2} ms each, read back included"));
        }
        finally
        {
            Directory.Delete(userDir, recursive: true);
        }

        return 0;
    }

    private static void Compare(string outDir, int frame, EnhancedFrame shot, byte[] enhanced, byte[] look)
    {
        int w = shot.Width, h = shot.Height;
        byte[] software = new byte[w * h * 4];
        byte[] diff = new byte[w * h * 4];
        int view = 0, same = 0, near = 0;
        for (int i = 0; i < w * h; i++)
        {
            int p = shot.Pixels[i] * 3;
            software[i * 4] = shot.Palette[p];
            software[(i * 4) + 1] = shot.Palette[p + 1];
            software[(i * 4) + 2] = shot.Palette[p + 2];
            software[(i * 4) + 3] = 255;

            int grey = (shot.Palette[p] + shot.Palette[p + 1] + shot.Palette[p + 2]) / 6;
            diff[i * 4] = diff[(i * 4) + 1] = diff[(i * 4) + 2] = (byte)grey;
            diff[(i * 4) + 3] = 255;
            if (shot.ViewDrawn && (shot.Mask[i] & EnhancedMask.View) != 0)
            {
                view++;
                int d = 0;
                for (int c = 0; c < 3; c++)
                {
                    d = Math.Max(d, Math.Abs(enhanced[(i * 4) + c] - software[(i * 4) + c]));
                }

                same += d == 0 ? 1 : 0;
                near += d <= 24 ? 1 : 0;
                if (d != 0)
                {
                    diff[i * 4] = 255;
                    diff[(i * 4) + 1] = diff[(i * 4) + 2] = (byte)(d > 24 ? 0 : 128);
                }
            }
        }

        string name = frame.ToString(CultureInfo.InvariantCulture);
        PngWriter.WriteRgba(Path.Combine(outDir, $"{name}.software.png"), w, h, software);
        PngWriter.WriteRgba(Path.Combine(outDir, $"{name}.enhanced.png"), w, h, enhanced);
        PngWriter.WriteRgba(Path.Combine(outDir, $"{name}.diff.png"), w, h, diff);
        PngWriter.WriteRgba(Path.Combine(outDir, $"{name}.default.png"), w * 4, look.Length / 4 / (w * 4), look);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"frame {frame}: view {view} pixels, same {Share(same, view)}, within 24 {Share(near, view)}"));
    }

    private static string Share(int n, int of) => of == 0 ? "-" : string.Create(CultureInfo.InvariantCulture, $"{100.0 * n / of:F1}%");
}
