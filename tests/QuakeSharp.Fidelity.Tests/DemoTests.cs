using System.Globalization;
using QuakeSharp.Core.Diagnostics;

namespace QuakeSharp.Fidelity.Tests;

/// <summary>
/// The client, the renderer and the server against the oracle (milestones 5, 6 and 7): demo1-3 at three video modes,
/// the attract loop, the scripted menus and the scripted games play with the oracle's state and pictures on every
/// frame. Every line of the oracle's trace is compared: F
/// (time, rand()), C (cls, cl), E (the entities), D (what the renderer writes into them), X (temporary entities,
/// beams, lights, light styles, particles), S (sound channels), T (console text), R (the view), V (the frame and
/// the palette), the server's lines, G (sv, svs), Q (the QuakeC globals), N (the edicts), W (the area lists) and M
/// (the messages sent), the sound's of milestone 8, P (the sound device's buffer) and A (the CD), and I (the mouse and
/// the joystick, milestone 10). The files each run writes (config.cfg, savegames, demos, screenshots, qconsole.log)
/// are compared with the oracle's run's, byte for byte (milestone 9). The mission packs are checked the same way
/// (milestone 10).
/// </summary>
public sealed class DemoTests : IDisposable
{
    private const string Lines = "FCEDXSTRVGQNWMPAI";

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
        RunFiles.AssertSame(trace, userdir);
    }

    [Fact]
    public void AttractLoopPlaysInSyncWithTheOracle()
    {
        string baseDir = GameDataLocator.RequireBaseDir();
        string reference = TraceLocator.RequireFile("attract.trace");

        var run = new TraceRun(baseDir, userdir, ["-oracleframes", "20000"]);
        AssertSameTrace(reference, run.Frames(quitAfterDemo: false, maxFrames: 20000));
        RunFiles.AssertSame("attract.trace", userdir);
    }

    // The config.cfg the menus' run wrote (other bindings and cvars), read back by quake.rc over the attract loop
    [Fact]
    public void ConfigReadBackPlaysInSyncWithTheOracle()
    {
        string baseDir = GameDataLocator.RequireBaseDir();
        string reference = TraceLocator.RequireFile("attract.config.trace");
        RunFiles.CopyInputs(userdir, "menus/config.cfg");

        var run = new TraceRun(baseDir, userdir, ["-oracleframes", "600"]);
        AssertSameTrace(reference, run.Frames(quitAfterDemo: false, maxFrames: 600));
        RunFiles.AssertSame("attract.config.trace", userdir);
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
        RunFiles.AssertSame(trace, userdir);
    }

    // The scripted games of tools/oracle/events (design.md §11.1): a new game from the menu through the start map
    // into e1m1, the server's commands with fights, saves and level changes, e1m1's exit and intermission, e1m7's
    // exit and the episode's finale, the network menus (a search, the game options starting a deathmatch, the setup),
    // a tour of every map of the episodes, the end and the deathmatch maps, fighting in each, and a game played by hand
    // (recorded with the executable's -recordevents) from the menu through e1m1 into e1m2, ending on the quit screen,
    // and the sound's (design.md §11.2): its commands and cvars, the CD's, the menu's volume sliders, a game's pause;
    // and the mouse's (design.md §11.4): taken and released, looking and moving, its buttons and cvars, the joystick's;
    // and the video modes': every mode from the console and the Video menu, a save, a demo and a screenshot across them;
    // and the rest of the UI (design.md §11.4, step 6): typing at the console, messagemode and the notify lines;
    // screen.c's sizes, pause, turtle, lcd_x and a level that fails to load; sbar.c's powerups, sigils, deathmatch and
    // coop; every menu's keys and a new game during a game; and the commands no other run uses
    [Theory]
    [InlineData("start", 2000, 320, 240, "start.trace")]
    [InlineData("commands", 3800, 320, 240, "commands.trace")]
    [InlineData("commands", 3800, 640, 480, "commands.640x480.trace")]
    [InlineData("intermission", 1900, 320, 240, "intermission.trace")]
    [InlineData("finale", 3100, 320, 240, "finale.trace")]
    [InlineData("netmenus", 3000, 320, 240, "netmenus.trace")]
    [InlineData("tour", 22200, 320, 240, "tour.trace")]
    [InlineData("e1", 24500, 320, 240, "e1.trace")]
    [InlineData("sound", 3500, 320, 240, "sound.trace")]
    [InlineData("saves", 17705, 320, 240, "saves.trace")]
    [InlineData("mouse", 1600, 320, 240, "mouse.trace")]
    [InlineData("video", 2500, 320, 240, "video.trace")]
    [InlineData("console", 3300, 320, 240, "console.trace")]
    [InlineData("screen", 4980, 320, 240, "screen.trace")]
    [InlineData("screen", 4980, 640, 480, "screen.640x480.trace")]
    [InlineData("sbar", 5013, 320, 240, "sbar.trace")]
    [InlineData("sbar", 5013, 640, 480, "sbar.640x480.trace")]
    [InlineData("menuwalk", 5000, 320, 240, "menuwalk.trace")]
    [InlineData("hostcmds", 8880, 320, 240, "hostcmds.trace")]
    public void GamePlaysInSyncWithTheOracle(string game, int frames, int width, int height, string trace)
    {
        string baseDir = GameDataLocator.RequireBaseDir();
        string reference = TraceLocator.RequireFile(trace);
        string events = Path.Combine(RepositoryRoot(), "tools", "oracle", "events", game + ".events");

        var run = new TraceRun(baseDir, userdir, ["-traceevents", events, "-oracleframes", frames.ToString(CultureInfo.InvariantCulture)], width, height);
        AssertSameTrace(reference, run.Frames(quitAfterDemo: false, maxFrames: frames));
        RunFiles.AssertSame(trace, userdir);
    }

    // Demos recorded (design.md §11.3: through a level change and an intermission, with a forced CD track, the last cut
    // by the end of the run), two screenshots and -condebug's qconsole.log
    [Fact]
    public void RecordingWritesTheOraclesFiles()
    {
        string baseDir = GameDataLocator.RequireBaseDir();
        string reference = TraceLocator.RequireFile("record.trace");
        string events = Path.Combine(RepositoryRoot(), "tools", "oracle", "events", "record.events");

        var run = new TraceRun(baseDir, userdir, ["-condebug", "-traceevents", events, "-oracleframes", "2700"]);
        AssertSameTrace(reference, run.Frames(quitAfterDemo: false, maxFrames: 2700));
        RunFiles.AssertSame("record.trace", userdir);
    }

    // Each demo the oracle recorded, played back in a fresh run, and d1 as a timedemo
    [Theory]
    [InlineData("d1", "+playdemo", "record.d1.trace")]
    [InlineData("d2", "+playdemo", "record.d2.trace")]
    [InlineData("d3", "+playdemo", "record.d3.trace")]
    [InlineData("d1", "+timedemo", "record.timedemo.trace")]
    public void RecordedDemoPlaysInSyncWithTheOracle(string demo, string command, string trace)
    {
        string baseDir = GameDataLocator.RequireBaseDir();
        string reference = TraceLocator.RequireFile(trace);
        RunFiles.CopyInputs(userdir, $"record/{demo}.dem");

        var run = new TraceRun(baseDir, userdir, ["-tracequitafterdemo", command, demo]);
        AssertSameTrace(reference, run.Frames(quitAfterDemo: true));
        RunFiles.AssertSame(trace, userdir);
    }

    // Every savegame the saves game wrote (design.md §11.3), the oracle's files, loaded in a fresh run and played on
    [Fact]
    public void SavesLoadInSyncWithTheOracle()
    {
        string baseDir = GameDataLocator.RequireBaseDir();
        string reference = TraceLocator.RequireFile("loads.trace");
        string saves = TraceLocator.RequireDirectory("saves.files");
        RunFiles.CopyInputs(userdir, [.. Directory.EnumerateFiles(saves, "*.sav").Select(f => "saves/" + Path.GetFileName(f))]);
        string events = Path.Combine(RepositoryRoot(), "tools", "oracle", "events", "loads.events");

        var run = new TraceRun(baseDir, userdir, ["-traceevents", events, "-oracleframes", "12905"]);
        AssertSameTrace(reference, run.Frames(quitAfterDemo: false, maxFrames: 12905));
        RunFiles.AssertSame("loads.trace", userdir);
    }

    // The renderer's debug cvars and commands (design.md §11.4, step 6), with a surface cache small enough to thrash and
    // tools/oracle/events/e1m1.pts beside the map for pointfile, as Trace-Demos.ps1 puts it beside the oracle's
    [Fact]
    public void RendererDebugPlaysInSyncWithTheOracle()
    {
        string baseDir = GameDataLocator.RequireBaseDir();
        string reference = TraceLocator.RequireFile("rdebug.trace");
        string oracleEvents = Path.Combine(RepositoryRoot(), "tools", "oracle", "events");
        Directory.CreateDirectory(Path.Combine(userdir, "id1", "maps"));
        File.Copy(Path.Combine(oracleEvents, "e1m1.pts"), Path.Combine(userdir, "id1", "maps", "e1m1.pts"));

        var run = new TraceRun(baseDir, userdir,
            ["-surfcachesize", "160", "-traceevents", Path.Combine(oracleEvents, "rdebug.events"), "-oracleframes", "1787"]);
        AssertSameTrace(reference, run.Frames(quitAfterDemo: false, maxFrames: 1787));
        RunFiles.AssertSame("rdebug.trace", userdir);
    }

    // The sound game without a sound device or a CD (WinQuake's -nosound and -nocdaudio)
    [Fact]
    public void SoundGameWithoutSoundPlaysInSyncWithTheOracle()
    {
        string baseDir = GameDataLocator.RequireBaseDir();
        string reference = TraceLocator.RequireFile("sound.nosound.trace");
        string events = Path.Combine(RepositoryRoot(), "tools", "oracle", "events", "sound.events");

        var run = new TraceRun(baseDir, userdir, ["-nosound", "-nocdaudio", "-traceevents", events, "-oracleframes", "3500"]);
        AssertSameTrace(reference, run.Frames(quitAfterDemo: false, maxFrames: 3500));
        RunFiles.AssertSame("sound.nosound.trace", userdir);
    }

    // The mission packs (design.md §11.4, decision 45), from a Quake folder holding only their original PAKs, writing
    // into their own game folders: their demos and attract loops
    [Theory]
    [InlineData("hipnotic", "-tracequitafterdemo +playdemo hipdemo1", "hipdemo1.trace")]
    [InlineData("hipnotic", "-tracequitafterdemo +playdemo hipdemo2", "hipdemo2.trace")]
    [InlineData("hipnotic", "-tracequitafterdemo +playdemo hipdemo3", "hipdemo3.trace")]
    [InlineData("hipnotic", "-tracequitafterdemo +playdemo hipdemo4", "hipdemo4.trace")]
    [InlineData("hipnotic", "-oracleframes 14000", "hipattract.trace")]
    [InlineData("rogue", "-tracequitafterdemo +playdemo demo1", "roguedemo1.trace")]
    [InlineData("rogue", "-tracequitafterdemo +playdemo demo2", "roguedemo2.trace")]
    [InlineData("rogue", "-tracequitafterdemo +playdemo demo3", "roguedemo3.trace")]
    [InlineData("rogue", "-oracleframes 17000", "rogueattract.trace")]
    public void PackDemoPlaysInSyncWithTheOracle(string pack, string args, string trace)
    {
        string baseDir = GameDataLocator.RequirePackBaseDir(pack);
        string reference = TraceLocator.RequireFile(trace);
        string[] arguments = ["-" + pack, .. args.Split(' ')];
        bool quitAfterDemo = args.StartsWith("-tracequitafterdemo", StringComparison.Ordinal);

        var run = new TraceRun(baseDir, userdir, arguments);
        AssertSameTrace(reference, quitAfterDemo
            ? run.Frames(quitAfterDemo: true)
            : run.Frames(quitAfterDemo: false, maxFrames: int.Parse(args.Split(' ')[1], CultureInfo.InvariantCulture)));
        RunFiles.AssertSame(trace, userdir, pack);
    }

    // The mission packs' scripted games: a tour of every map with the pack's items, weapons and a save on each, the
    // menus' episodes and levels and the games they start (rogue's team modes), and each pack's end; then the tour's
    // saves, each loaded in a fresh run and played on
    [Theory]
    [InlineData("hipnotic", "hiptour", 18000)]
    [InlineData("hipnotic", "hipmenus", 2344)]
    [InlineData("hipnotic", "hipend", 9000)]
    [InlineData("hipnotic", "hiploads", 5700)]
    [InlineData("rogue", "roguetour", 17000)]
    [InlineData("rogue", "roguemenus", 3843)]
    [InlineData("rogue", "rogueend", 7000)]
    [InlineData("rogue", "rogueloads", 5400)]
    public void PackGamePlaysInSyncWithTheOracle(string pack, string game, int frames)
    {
        string baseDir = GameDataLocator.RequirePackBaseDir(pack);
        string reference = TraceLocator.RequireFile(game + ".trace");
        if (game.EndsWith("loads", StringComparison.Ordinal))
        {
            string tour = game.Replace("loads", "tour", StringComparison.Ordinal);
            string saves = TraceLocator.RequireDirectory(tour + ".files");
            RunFiles.CopyInputsInto(userdir, pack,
                [.. Directory.EnumerateFiles(saves, "*.sav").Select(f => $"{tour}/{Path.GetFileName(f)}")]);
        }

        string events = Path.Combine(RepositoryRoot(), "tools", "oracle", "events", game + ".events");

        var run = new TraceRun(baseDir, userdir,
            ["-" + pack, "-traceevents", events, "-oracleframes", frames.ToString(CultureInfo.InvariantCulture)]);
        AssertSameTrace(reference, run.Frames(quitAfterDemo: false, maxFrames: frames));
        RunFiles.AssertSame(game + ".trace", userdir, pack);
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
