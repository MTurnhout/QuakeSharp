using System.Globalization;
using QuakeSharp.Core;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Diagnostics;

namespace QuakeSharp.Tools;

internal static class TraceCommands
{
    // trace <quake-dir> <out.trace> [args]: QuakeSharp's trace of a run, the oracle's switches read as it reads them
    // (-tracequitafterdemo, -oracleframes <n>, -oraclevid <w> <h>, -oraclestep <n>, -tracedump <frame>)
    public static int Trace(string baseDir, string output, string[] args)
    {
        var commandLine = new CommandLine(["quake", .. args]);
        int frames = Option(commandLine, "-oracleframes", int.MaxValue);
        int fps = Option(commandLine, "-oraclestep", 64);
        int width = Option(commandLine, "-oraclevid", 320);
        int height = commandLine.CheckParm("-oraclevid") is int v && v != 0 && v < commandLine.Argc - 2
            ? QString.Q_atoi(commandLine.Argv(v + 2))
            : 240;
        bool quitAfterDemo = commandLine.CheckParm("-tracequitafterdemo") != 0;
        var dumps = new HashSet<int>();
        for (int i = 1; i < commandLine.Argc - 1; i++)
        {
            if (commandLine.Argv(i) == "-tracedump")
            {
                dumps.Add(QString.Q_atoi(commandLine.Argv(i + 1)));
            }
        }

        string userDir = Path.Combine(Path.GetTempPath(), "quakesharp-trace", Guid.NewGuid().ToString("N"));
        try
        {
            var run = new TraceRun(baseDir, userDir, args, width, height, fps);
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
                }
            }
            catch (QuakeException e)
            {
                writer.WriteLine($"Err {e.Message}");
                Console.Error.WriteLine($"Sys_Error: {e.Message}");
            }

            Console.WriteLine($"{output}: {count} frames");
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
