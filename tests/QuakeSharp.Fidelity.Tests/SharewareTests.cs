using QuakeSharp.Core.Diagnostics;

namespace QuakeSharp.Fidelity.Tests;

/// <summary>
/// The shareware smoke test (design.md §10, decision 48): demo1-3 played from id1/pak0.pak alone, the shareware's,
/// run as many frames as the oracle's and end on its last frame, every line of it. CI downloads the shareware for it
/// and has no traces, so the last frames of the oracle's shareware.demo1-3.trace (tools/oracle/Trace-Demos.ps1) are
/// kept here; they are the registered data's demo1-3.trace's too, line for line.
/// </summary>
public sealed class SharewareTests : IDisposable
{
    private static readonly Dictionary<string, string> LastFrames = new(StringComparer.Ordinal)
    {
        ["demo1"] = """
            F 4755 4754 4052930000000000 6b4a185a 139043
            C 2c59db1373b20191
            E 3c168ab80ed47c2c
            D a4d2e848f72aec22
            X 020ff8d049fde1cb 82
            S d0c0a0d309d6c72b
            T c85a67efc446b525
            R 241a4b8adc1e8ea6
            V 9db9bde2056e48e1 019dfc47e24e207e
            G 915c7ed3b7ee1b2c
            Q cbf29ce484222325
            N cbf29ce484222325 0
            W cbf29ce484222325
            M cbf29ce484222325 0
            P eb05052ea5b62325
            A 15cec9bb23d45893
            I f31eab16f93a6026
            U cbf29ce484222325 0
            """,
        ["demo2"] = """
            F 4476 4475 40517c0000000000 854ecba8 56617
            C 61c765c01a18979c
            E 08706f5b2eb6e3c9
            D 28e11456113d2981
            X f36c460d1f7efd4e 12
            S ae364c8d1e2b748f
            T c7e25869ad53cd9e
            R e89cec9eec91328f
            V 942deaddee537670 019dfc47e24e207e
            G 915c7ed3b7ee1b2c
            Q cbf29ce484222325
            N cbf29ce484222325 0
            W cbf29ce484222325
            M cbf29ce484222325 0
            P eb05052ea5b62325
            A 95173324a36ac443
            I f31eab16f93a6026
            U cbf29ce484222325 0
            """,
        ["demo3"] = """
            F 5269 5268 4054950000000000 4b3c629a 300387
            C 138cb3b80d47e467
            E 882e105e35584498
            D 172a9910573dfb46
            X 81b52f60d4ccc82e 345
            S 8bdf4a05ba1fe79c
            T 1386db5fb7a66f95
            R 68cccb01d4e942e8
            V 163b9176e31df8e4 019dfc47e24e207e
            G 915c7ed3b7ee1b2c
            Q cbf29ce484222325
            N cbf29ce484222325 0
            W cbf29ce484222325
            M cbf29ce484222325 0
            P eb05052ea5b62325
            A 4459e022157a8973
            I f31eab16f93a6026
            U cbf29ce484222325 0
            """,
    };

    private readonly string userdir = Path.Combine(Path.GetTempPath(), "quakesharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(userdir))
        {
            Directory.Delete(userdir, recursive: true);
        }
    }

    [Theory]
    [InlineData("demo1")]
    [InlineData("demo2")]
    [InlineData("demo3")]
    public void DemoEndsOnTheOraclesLastFrame(string demo)
    {
        string baseDir = GameDataLocator.RequireSharewareBaseDir();

        var run = new TraceRun(baseDir, userdir, ["-tracequitafterdemo", "+playdemo", demo]);
        IReadOnlyList<string>? last = null;
        foreach (IReadOnlyList<string> lines in run.Frames(quitAfterDemo: true))
        {
            last = lines;
        }

        Assert.NotNull(last);
        Assert.Equal(LastFrames[demo].ReplaceLineEndings("\n"), string.Join('\n', last.Where(l => !l.StartsWith('H'))));
    }
}
