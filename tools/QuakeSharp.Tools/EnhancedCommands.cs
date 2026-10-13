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
    // colour, and that are within 24 of it on every channel.
    public static int Shot(string baseDir, string outDir, string frameList, string[] args)
    {
        Commands.RefuseRepository(outDir);
        Directory.CreateDirectory(outDir);
        var wanted = new HashSet<int>(frameList.Split(',').Select(f => int.Parse(f, CultureInfo.InvariantCulture)));
        int last = wanted.Max();

        string userDir = Path.Combine(Path.GetTempPath(), "quakesharp-trace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDir);
        try
        {
            var run = new TraceRun(baseDir, userDir, args);
            var capture = new EnhancedCapture(run.Engine);
            run.Engine.enhanced = capture;

            using VulkanPresenter presenter = VulkanPresenter.Headless(enableValidation: false, Console.Error.WriteLine);
            var faithful = new EnhancedOptions(Scale: 1, Msaa: 1, Widescreen: false, Filter: 0, Mipmaps: 0, Anisotropy: 1, Lighting: 1);
            var defaults = new EnhancedOptions();

            int frame = 0;
            foreach (IReadOnlyList<string> _ in run.Frames(quitAfterDemo: new Core.Common.CommandLine(["quake", .. args]).CheckParm("-tracequitafterdemo") != 0))
            {
                frame++;
                if (wanted.Contains(frame) && capture.Latest is { } shot)
                {
                    presenter.SquarePixels = true;
                    byte[] enhanced = presenter.RenderEnhanced(shot, faithful, shot.Width, shot.Height);
                    presenter.SquarePixels = false;
                    byte[] look = presenter.RenderEnhanced(shot, defaults, shot.Width * 4, shot.Width * 3);
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
