namespace QuakeSharp.Fidelity.Tests;

/// <summary>
/// Finds the oracle's traces through QUAKESHARP_TRACE_DIR (tools/oracle/Trace-Demos.ps1 writes them). Tests skip when
/// it is not set or lacks the file they need.
/// </summary>
internal static class TraceLocator
{
    public static string RequireFile(string name)
    {
        string? dir = Environment.GetEnvironmentVariable("QUAKESHARP_TRACE_DIR");
        string? path = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, name);
        Assert.SkipUnless(path != null && File.Exists(path),
            $"Set QUAKESHARP_TRACE_DIR to the oracle's traces (tools/oracle/Trace-Demos.ps1 writes {name}).");
        return path!;
    }
}
