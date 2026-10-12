using QuakeSharp.Core.Diagnostics;

namespace QuakeSharp.Fidelity.Tests;

/// <summary>
/// The files a run writes (design.md §11.3): tools/oracle/Trace-Demos.ps1 keeps what each oracle run left in its id1
/// in &lt;name&gt;.files beside the trace; QuakeSharp's runs write into the user folder's id1.
/// </summary>
internal static class RunFiles
{
    /// <summary>Copies files an earlier oracle run wrote ("&lt;run&gt;/&lt;file&gt;") into the user folder's id1, as
    /// Trace-Demos.ps1 copies them into the oracle's.</summary>
    public static void CopyInputs(string userdir, params string[] inputs)
    {
        string id1 = Path.Combine(userdir, "id1");
        Directory.CreateDirectory(id1);
        foreach (string input in inputs)
        {
            int slash = input.IndexOf('/', StringComparison.Ordinal);
            string source = TraceLocator.RequireFile(Path.Combine(input[..slash] + ".files", input[(slash + 1)..]));
            File.Copy(source, Path.Combine(id1, input[(slash + 1)..]));
        }
    }

    /// <summary>The files QuakeSharp's run left in the user folder's id1 are those the oracle's run of
    /// <paramref name="trace"/> left: the same names, the same bytes.</summary>
    public static void AssertSame(string trace, string userdir)
    {
        string theirs = TraceLocator.RequireDirectory(Path.ChangeExtension(trace, ".files"));
        IReadOnlyList<string> differences = FileCompare.Folders(theirs, Path.Combine(userdir, "id1"));
        Assert.True(differences.Count == 0, string.Join("\n", differences));
    }
}
