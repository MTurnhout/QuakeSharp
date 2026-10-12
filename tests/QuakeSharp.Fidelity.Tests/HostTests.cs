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
        string reference = TraceLocator.RequireFile("attract.files/config.cfg");

        var console = new StringBuilder();
        var system = new VirtualSystem(t => console.Append(t));
        var engine = new QuakeEngine(new QuakeParms(baseDir, new CommandLine(["quake"])) { userdir = userdir }, system,
            cd: new Core.Sound.VirtualCdDrive());
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

        // config.cfg: the bindings default.cfg made and the archived cvars, in text mode, byte for byte as the oracle
        // wrote it when it quit the attract loop
        Assert.Null(Core.Diagnostics.FileCompare.Files("config.cfg", File.ReadAllBytes(reference),
            File.ReadAllBytes(Path.Combine(userdir, "id1", "config.cfg"))));
    }
}
