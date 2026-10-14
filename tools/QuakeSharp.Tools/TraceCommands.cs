using System.Globalization;
using QuakeSharp.Core;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Diagnostics;
using QuakeSharp.Core.Enhanced;

namespace QuakeSharp.Tools;

internal static class TraceCommands
{
    // trace <quake-dir> <out.trace> [args]: QuakeSharp's trace of a run, the oracle's switches read as it reads them
    // (-tracequitafterdemo, -oracleframes <n>, -oraclevid <w> <h>, -oraclestep <n>, -tracedump <frame>,
    // -traceshot <frame>); -traceinput <file>[@<path>] (any number of times, not passed on) puts a file into the user
    // folder's game directory (id1, or -rogue's, -hipnotic's), at <path> in it if given (maps/e1m1.pts), before the
    // run, as Trace-Demos.ps1 puts an earlier run's files into the oracle's. What the run leaves there is kept in
    // <out>.files, as the oracle's run's. -traceenhanced (not passed on) runs it with the enhanced mode's capture, as
    // EnhancedModeChangesNothing does (design.md §11.6)
    public static int Trace(string baseDir, string output, string[] args)
    {
        Commands.RefuseRepository(output);
        var inputs = new List<string>();
        var rest = new List<string>();
        bool enhanced = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-traceinput" && i + 1 < args.Length)
            {
                inputs.Add(args[++i]);
            }
            else if (args[i] == "-traceenhanced")
            {
                enhanced = true;
            }
            else
            {
                rest.Add(args[i]);
            }
        }

        args = [.. rest];
        var commandLine = new CommandLine(["quake", .. args]);
        int frames = Option(commandLine, "-oracleframes", int.MaxValue);
        int fps = Option(commandLine, "-oraclestep", 64);
        int width = Option(commandLine, "-oraclevid", 320);
        int height = commandLine.CheckParm("-oraclevid") is int v && v != 0 && v < commandLine.Argc - 2
            ? QString.Q_atoi(commandLine.Argv(v + 2))
            : 240;
        bool quitAfterDemo = commandLine.CheckParm("-tracequitafterdemo") != 0;
        var dumps = new HashSet<int>();
        var shots = new HashSet<int>();
        for (int i = 1; i < commandLine.Argc - 1; i++)
        {
            if (commandLine.Argv(i) == "-tracedump")
            {
                dumps.Add(QString.Q_atoi(commandLine.Argv(i + 1)));
            }

            if (commandLine.Argv(i) == "-traceshot")
            {
                shots.Add(QString.Q_atoi(commandLine.Argv(i + 1)));
            }
        }

        string userDir = Path.Combine(Path.GetTempPath(), "quakesharp-trace", Guid.NewGuid().ToString("N"));
        string gameDir = Path.Combine(userDir, TraceRun.GameDirectory(args));
        try
        {
            Directory.CreateDirectory(gameDir);
            foreach (string input in inputs)
            {
                int at = input.LastIndexOf('@');
                string target = Path.Combine(gameDir, at < 0 ? Path.GetFileName(input) : input[(at + 1)..]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(at < 0 ? input : input[..at], target);
            }

            var run = new TraceRun(baseDir, userDir, args, width, height, fps, enhanced: enhanced);
            using var writer = new StreamWriter(output);
            writer.NewLine = "\n";
            int count = 0;
            try
            {
                foreach (IReadOnlyList<string> lines in run.Frames(quitAfterDemo, frames))
                {
                    foreach (string line in lines)
                    {
                        writer.WriteLine(line);
                    }

                    count++;
                    if (dumps.Contains(count))
                    {
                        File.WriteAllText($"{output}.{count}.dump", run.Writer.Dump());
                    }

                    if (shots.Contains(count))
                    {
                        File.WriteAllBytes($"{output}.{count}.shot", run.Writer.Shot());
                    }
                }
            }
            catch (QuakeException e)
            {
                writer.WriteLine($"Err {e.Message}");
                Console.Error.WriteLine($"Sys_Error: {e.Message}");
            }

            Console.WriteLine($"{output}: {count} frames");

            string files = Path.ChangeExtension(output, ".files");
            if (Directory.Exists(files))
            {
                Directory.Delete(files, recursive: true);
            }

            Directory.CreateDirectory(files);
            foreach (string file in Directory.EnumerateFiles(gameDir, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(files, Path.GetRelativePath(gameDir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }
        finally
        {
            if (Directory.Exists(userDir))
            {
                Directory.Delete(userDir, recursive: true);
            }
        }

        return 0;
    }

    // netrun <quake-dir> <out-dir> <game.net>: a game between hosts (design.md §11.5): for each host n,
    // <game>.host<n>.trace, the datagrams it received (<game>.host<n>.dgrams, the oracle's -tracedatagrams), its
    // console's text (<game>.host<n>.log) and its files (<game>.host<n>.files)
    public static int NetRun(string baseDir, string outDir, string game)
    {
        Commands.RefuseRepository(outDir);
        Directory.CreateDirectory(outDir);
        string name = Path.GetFileNameWithoutExtension(game);
        NetGame netGame = NetGame.Read(game);
        IReadOnlyList<IReadOnlyList<string>> hosts = netGame.Hosts;
        string root = Path.Combine(Path.GetTempPath(), "quakesharp-netrun", Guid.NewGuid().ToString("N"));
        string[] userDirs = [.. hosts.Select((_, i) => Path.Combine(root, $"host{i + 1}"))];
        var writers = new StreamWriter[hosts.Count];
        var counts = new int[hosts.Count];
        try
        {
            for (int i = 0; i < hosts.Count; i++)
            {
                string gameDir = Path.Combine(userDirs[i], TraceRun.GameDirectory([.. hosts[i]]));
                Directory.CreateDirectory(gameDir);
                writers[i] = new StreamWriter(Path.Combine(outDir, $"{name}.host{i + 1}.trace")) { NewLine = "\n" };
            }

            var logs = hosts.Select(_ => new System.Text.StringBuilder()).ToArray();
            var run = new Core.Diagnostics.NetRun(baseDir, userDirs, netGame, (host, text) => logs[host - 1].Append(text));
            run.Run((host, lines) =>
            {
                foreach (string line in lines)
                {
                    writers[host - 1].WriteLine(line);
                }

                counts[host - 1]++;
            });

            for (int i = 0; i < hosts.Count; i++)
            {
                writers[i].Dispose();
                File.WriteAllText(Path.Combine(outDir, $"{name}.host{i + 1}.dgrams"), run.Datagrams(i + 1));
                File.WriteAllText(Path.Combine(outDir, $"{name}.host{i + 1}.log"), logs[i].ToString());
                string files = Path.Combine(outDir, $"{name}.host{i + 1}.files");
                if (Directory.Exists(files))
                {
                    Directory.Delete(files, recursive: true);
                }

                Directory.CreateDirectory(files);
                string gameDir = Path.Combine(userDirs[i], TraceRun.GameDirectory([.. hosts[i]]));
                foreach (string file in Directory.EnumerateFiles(gameDir, "*", SearchOption.AllDirectories))
                {
                    string target = Path.Combine(files, Path.GetRelativePath(gameDir, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                }

                Console.WriteLine($"{name}.host{i + 1}.trace: {counts[i]} frames");
            }
        }
        finally
        {
            foreach (StreamWriter w in writers)
            {
                w?.Dispose();
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        return 0;
    }

    // trace-compare <a.trace> <b.trace> [letters]: the first frame at which each kind of line differs
    public static int Compare(string a, string b, string letters)
    {
        Dictionary<int, Dictionary<string, string>> left = Read(a);
        Dictionary<int, Dictionary<string, string>> right = Read(b);
        int frames = Math.Min(left.Count, right.Count);
        Console.WriteLine($"{a}: {left.Count} frames; {b}: {right.Count} frames");

        bool same = left.Count == right.Count;
        foreach (char letter in letters)
        {
            string key = letter.ToString();
            int first = -1;
            int differing = 0;
            for (int frame = 1; frame <= frames; frame++)
            {
                left[frame].TryGetValue(key, out string? l);
                right[frame].TryGetValue(key, out string? r);
                if (l != r)
                {
                    differing++;
                    if (first < 0)
                    {
                        first = frame;
                    }
                }
            }

            if (first < 0)
            {
                Console.WriteLine($"{key}: the same in all {frames} frames");
            }
            else
            {
                same = false;
                left[first].TryGetValue(key, out string? l);
                right[first].TryGetValue(key, out string? r);
                Console.WriteLine($"{key}: first differs at frame {first} ({differing} frames differ): {l} | {r}");
            }
        }

        return same ? 0 : 1;
    }

    // file-compare <a> <b>: two files, or two folders' files (a run's .files), the oracle's first: where they differ
    public static int FileCompare(string a, string b)
    {
        IReadOnlyList<string> differences = Directory.Exists(a)
            ? Core.Diagnostics.FileCompare.Folders(a, b)
            : Core.Diagnostics.FileCompare.Files(Path.GetFileName(a), File.ReadAllBytes(a), File.ReadAllBytes(b)) is string d
                ? [d]
                : [];
        foreach (string difference in differences)
        {
            Console.WriteLine(difference);
        }

        Console.WriteLine(differences.Count == 0 ? "the same" : $"{differences.Count} differences");
        return differences.Count == 0 ? 0 : 1;
    }

    // shot-diff <a.shot> <b.shot> [out.png]: where two frames (-traceshot) differ; the PNG shows a, b and a with the
    // differing pixels in magenta, side by side
    public static int ShotDiff(string a, string b, string? output)
    {
        byte[] left = File.ReadAllBytes(a);
        byte[] right = File.ReadAllBytes(b);
        int width = BitConverter.ToInt32(left, 0), height = BitConverter.ToInt32(left, 4);
        if (width != BitConverter.ToInt32(right, 0) || height != BitConverter.ToInt32(right, 4))
        {
            Console.WriteLine("the frames' sizes differ");
            return 1;
        }

        const int pixels = 8 + 768;
        int differ = 0, minx = width, miny = height, maxx = -1, maxy = -1;
        byte[] panels = new byte[width * 3 * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte l = left[pixels + (y * width) + x], r = right[pixels + (y * width) + x];
                int row = y * width * 3;
                panels[row + x] = l;
                panels[row + width + x] = r;
                panels[row + (2 * width) + x] = l;
                if (l != r)
                {
                    if (differ < 10)
                    {
                        Console.WriteLine($"({x}, {y}): {l} | {r}");
                    }

                    differ++;
                    minx = Math.Min(minx, x);
                    miny = Math.Min(miny, y);
                    maxx = Math.Max(maxx, x);
                    maxy = Math.Max(maxy, y);
                    panels[row + (2 * width) + x] = 255;
                }
            }
        }

        bool samePalette = left.AsSpan(8, 768).SequenceEqual(right.AsSpan(8, 768));
        Console.WriteLine(differ == 0
            ? $"the pixels are the same{(samePalette ? "" : "; the palettes differ")}"
            : $"{differ} pixels differ, in ({minx}, {miny})-({maxx}, {maxy}){(samePalette ? "" : "; the palettes differ")}");
        if (output != null)
        {
            Commands.RefuseRepository(output);
            byte[] palette = left.AsSpan(8, 768).ToArray();
            palette[765] = 255;
            palette[766] = 0;
            palette[767] = 255;
            PngWriter.WriteIndexed(output, width * 3, height, panels, palette);
        }

        return differ == 0 ? 0 : 1;
    }

    // The lines of each frame, by their letter (an F line starts a frame; for F, the line without its letter)
    private static Dictionary<int, Dictionary<string, string>> Read(string path)
    {
        var frames = new Dictionary<int, Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        foreach (string line in File.ReadLines(path))
        {
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            string key = space < 0 ? line : line[..space];
            string value = space < 0 ? "" : line[(space + 1)..];
            if (key == "F")
            {
                current = [];
                frames[frames.Count + 1] = current;
            }

            if (current != null)
            {
                current[key] = value;
            }
        }

        return frames;
    }

    private static int Option(CommandLine commandLine, string name, int value)
    {
        int i = commandLine.CheckParm(name);
        return i != 0 && i < commandLine.Argc - 1 ? int.Parse(commandLine.Argv(i + 1), CultureInfo.InvariantCulture) : value;
    }
}
