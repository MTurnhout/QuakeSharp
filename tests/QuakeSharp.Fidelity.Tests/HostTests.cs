using System.Text;
using QuakeSharp.Core;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Platform;

namespace QuakeSharp.Fidelity.Tests;

/// <summary>The host on the original data (milestone 4).</summary>
public sealed class HostTests : IDisposable
{
    private readonly string userdir = Path.Combine(Path.GetTempPath(), "quakesharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(userdir))
        {
            Directory.Delete(userdir, recursive: true);
        }
    }

    [Fact]
    public void QuakeRcRunsAndTheBindingsAreWinQuakes()
    {
        string baseDir = GameDataLocator.RequireBaseDir();
        string reference = TraceLocator.RequireFile("attract.config.cfg");

        var console = new StringBuilder();
        var system = new VirtualSystem(t => console.Append(t));
        var engine = new QuakeEngine(new QuakeParms(baseDir, new CommandLine(["quake"])) { userdir = userdir }, system);
        engine.Host_Init();
        for (int frame = 1; frame <= 10; frame++)
        {
            system.Time = frame / 64.0;
            engine.Host_Frame(1f / 64);
        }

        // quit: Host_Shutdown writes config.cfg (the exception it returns would end the program)
        _ = engine.Sys_Quit();

        string text = console.ToString();
        Assert.Contains("execing quake.rc\n", text, StringComparison.Ordinal);
        Assert.Contains("execing default.cfg\n", text, StringComparison.Ordinal);
        Assert.Contains("Playing registered version.\n", text, StringComparison.Ordinal);

        // config.cfg: WinQuake's text mode lines; the bindings default.cfg made are the oracle's, line for line (its
        // archived cvars come from modules not all ported yet)
        string[] ours = File.ReadAllText(Path.Combine(userdir, "id1", "config.cfg"), Encoding.Latin1).Split("\r\n");
        string[] theirs = File.ReadAllText(reference, Encoding.Latin1).Split("\r\n");
        string[] binds = [.. theirs.Where(l => l.StartsWith("bind ", StringComparison.Ordinal))];
        Assert.NotEmpty(binds);
        Assert.Equal(binds, ours.Where(l => l.StartsWith("bind ", StringComparison.Ordinal)));
        Assert.All(ours.Where(l => l.Length > 0), l => Assert.Contains(l, theirs));
    }
}
