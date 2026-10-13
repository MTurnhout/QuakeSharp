using QuakeSharp.Core.Diagnostics;

namespace QuakeSharp.Fidelity.Tests;

/// <summary>
/// The files a run writes (design.md §11.3): tools/oracle/Trace-Demos.ps1 keeps what each oracle run left in its game
/// directory (id1, or the mission pack's) in &lt;name&gt;.files beside the trace; QuakeSharp's runs write into the user
/// folder's namesake of it.
/// </summary>
internal static class RunFiles
{
    /// <summary>Copies files an earlier oracle run wrote ("&lt;run&gt;/&lt;file&gt;") into the user folder's id1, as
    /// Trace-Demos.ps1 copies them into the oracle's.</summary>
    public static void CopyInputs(string userdir, params string[] inputs) => CopyInputsInto(userdir, "id1", inputs);

    /// <summary>The same into the user folder's <paramref name="game"/> directory.</summary>
    public static void CopyInputsInto(string userdir, string game, params string[] inputs)
    {
        string dir = Path.Combine(userdir, game);
        Directory.CreateDirectory(dir);
        foreach (string input in inputs)
        {
            int slash = input.IndexOf('/', StringComparison.Ordinal);
            string source = TraceLocator.RequireFile(Path.Combine(input[..slash] + ".files", input[(slash + 1)..]));
            File.Copy(source, Path.Combine(dir, input[(slash + 1)..]));
        }
    }

    /// <summary>The files QuakeSharp's run left in the user folder's <paramref name="game"/> directory are those the
    /// oracle's run of <paramref name="trace"/> left: the same names, the same bytes.</summary>
    public static void AssertSame(string trace, string userdir, string game = "id1")
    {
        string theirs = TraceLocator.RequireDirectory(Path.ChangeExtension(trace, ".files"));
        IReadOnlyList<string> differences = FileCompare.Folders(theirs, Path.Combine(userdir, game));
        Assert.True(differences.Count == 0, string.Join("\n", differences));
    }
}
