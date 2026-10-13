namespace QuakeSharp.Fidelity.Tests;

/// <summary>Every trace in the faithful mode: <see cref="TraceTests"/>' runs as they are.</summary>
public sealed class DemoTests : TraceTests
{
    protected override bool Enhanced => false;
}
