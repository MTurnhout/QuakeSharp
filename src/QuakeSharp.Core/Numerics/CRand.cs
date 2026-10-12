namespace QuakeSharp.Core.Numerics;

/// <summary>
/// The C runtime's rand() as WinQuake.exe and the oracle have it: the Microsoft C runtime's linear congruential
/// generator. Quake draws every random number from this one generator (the server's QuakeC, the client's effects,
/// the particles, the menu), so the engine owns one instance and the order of calls is part of the game
/// (design.md §3.1).
/// </summary>
public sealed class CRand
{
    // The Microsoft C runtime's holdrand, 1 until srand
    private uint holdrand = 1;

    /// <summary>The generator's state, for the traces.</summary>
    public uint Seed => holdrand;

    /// <summary>The number of calls so far, for the traces.</summary>
    public uint Calls { get; private set; }

    // rand: the next number, 0 to RAND_MAX (0x7fff)
    public int rand()
    {
        Calls++;
        holdrand = (holdrand * 214013) + 2531011;
        return (int)((holdrand >> 16) & 0x7fff);
    }

    // srand
    public void srand(uint seed) => holdrand = seed;
}
