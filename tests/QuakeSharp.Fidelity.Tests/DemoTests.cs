using QuakeSharp.Core.Diagnostics;

namespace QuakeSharp.Fidelity.Tests;

/// <summary>
/// The client and the renderer against the oracle (milestones 5 and 6): demo1-3 at three video modes, the attract
/// loop and the scripted menus play with the oracle's state and pictures on every frame. Every line of the oracle's trace is compared: F
/// (time, rand()), C (cls, cl), E (the entities), D (what the renderer writes into them), X (temporary entities,
/// beams, lights, light styles, particles), S (sound channels), T (console text), R (the view), V (the frame and
/// the palette), and the server's lines, G (sv, svs), Q (the QuakeC globals), N (the edicts), W (the area lists) and M
/// (the messages sent), which these runs leave as without a server.
/// </summary>
public sealed class DemoTests : IDisposable
{
    private const string Lines = "FCEDXSTRVGQNWM";

    private readonly string userdir = Path.Combine(Path.GetTempPath(), "quakesharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(userdir))
        {
            Directory.Delete(userdir, recursive: true);
        }
    }

    [Theory]
    [InlineData("demo1", 320, 240, "demo1.trace")]
    [InlineData("demo2", 320, 240, "demo2.trace")]
    [InlineData("demo3", 320, 240, "demo3.trace")]
    [InlineData("demo1", 320, 200, "demo1.320x200.trace")]
    [InlineData("demo2", 320, 200, "demo2.320x200.trace")]
    [InlineData("demo3", 320, 200, "demo3.320x200.trace")]
    [InlineData("demo1", 640, 480, "demo1.640x480.trace")]
    [InlineData("demo2", 640, 480, "demo2.640x480.trace")]
    [InlineData("demo3", 640, 480, "demo3.640x480.trace")]
    public void DemoPlaysInSyncWithTheOracle(string demo, int width, int height, string trace)
    {
        string baseDir = GameDataLocator.RequireBaseDir();
        string reference = TraceLocator.RequireFile(trace);

        var run = new TraceRun(baseDir, userdir, ["-tracequitafterdemo", "+playdemo", demo], width, height);
        AssertSameTrace(reference, run.Frames(quitAfterDemo: true));
    }

    [Fact]
    public void AttractLoopPlaysInSyncWithTheOracle()
    {
        string baseDir = GameDataLocator.RequireBaseDir();
        string reference = TraceLocator.RequireFile("attract.trace");

        var run = new TraceRun(baseDir, userdir, ["-oracleframes", "20000"]);
        AssertSameTrace(reference, run.Frames(quitAfterDemo: false, maxFrames: 20000));
    }

    // The menus, the console, the scores and the renderer's cvars, driven by tools/oracle/events/menus.events over
    // the attract loop
    [Theory]
    [InlineData(320, 240, "menus.trace")]
    [InlineData(640, 480, "menus.640x480.trace")]
    public void MenusPlayInSyncWithTheOracle(int width, int height, string trace)
    {
        string baseDir = GameDataLocator.RequireBaseDir();
        string reference = TraceLocator.RequireFile(trace);
        string events = Path.Combine(RepositoryRoot(), "tools", "oracle", "events", "menus.events");

        var run = new TraceRun(baseDir, userdir, ["-traceevents", events, "-oracleframes", "3700"], width, height);
        AssertSameTrace(reference, run.Frames(quitAfterDemo: false, maxFrames: 3700));
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QuakeSharp.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("QuakeSharp.slnx not found above the test directory");
    }

    // Compares the frames' lines with the oracle's, and the number of frames
    private static void AssertSameTrace(string reference, IEnumerable<IReadOnlyList<string>> frames)
    {
        using IEnumerator<Dictionary<string, string>> theirs = ReadFrames(reference).GetEnumerator();
        int frame = 0;
        foreach (IReadOnlyList<string> lines in frames)
        {
            frame++;
            Assert.True(theirs.MoveNext(), $"QuakeSharp ran frame {frame}; the oracle's trace ends before it");
            Dictionary<string, string> ours = Split(lines);
            foreach (char letter in Lines)
            {
                string key = letter.ToString();
                Assert.True(ours[key] == theirs.Current[key],
                    $"frame {frame}, {key}: the oracle's {theirs.Current[key]}, QuakeSharp's {ours[key]}");
            }
        }

        Assert.False(theirs.MoveNext(), $"the oracle's trace goes on after QuakeSharp's {frame} frames");
    }

    // The lines of each frame, by their letter (an F line starts a frame)
    private static IEnumerable<Dictionary<string, string>> ReadFrames(string path)
    {
        Dictionary<string, string>? current = null;
        foreach (string line in File.ReadLines(path))
        {
            (string key, string value) = SplitLine(line);
            if (key == "F")
            {
                if (current != null)
                {
                    yield return current;
                }

                current = [];
            }

            current?.Add(key, value);
        }

        if (current != null)
        {
            yield return current;
        }
    }

    private static Dictionary<string, string> Split(IReadOnlyList<string> lines) =>
        lines.Select(SplitLine).Where(l => l.Key != "H").ToDictionary(l => l.Key, l => l.Value);

    private static (string Key, string Value) SplitLine(string line)
    {
        int space = line.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (line, "") : (line[..space], line[(space + 1)..]);
    }
}
