namespace QuakeSharp.Fidelity.Tests;

/// <summary>
/// The enhanced mode changes nothing in the game (design.md §11.6): <see cref="TraceTests"/>' runs, every trace, with
/// the enhanced mode switched on (and off and on again along the way), a snapshot of every frame taken and the frames
/// between built, still match the oracle on every line and every file; its work reads the clock and draws rand() not
/// once, and its palette tables give the palette shown.
/// </summary>
public sealed class EnhancedModeChangesNothing : TraceTests
{
    protected override bool Enhanced => true;
}
