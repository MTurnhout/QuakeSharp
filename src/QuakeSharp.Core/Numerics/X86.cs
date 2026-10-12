namespace QuakeSharp.Core.Numerics;

/// <summary>
/// Conversions whose C result is undefined out of range, with the result the oracle's x86 code gives (design.md §3.1).
/// </summary>
public static class X86
{
    /// <summary>
    /// C's (int) of a float or double: truncation, as cvttss2si/cvttsd2si do; out of range or NaN gives 0x80000000
    /// (the "integer indefinite"), where .NET would saturate.
    /// </summary>
    public static int FtoI(double d) => d is >= -2147483648.0 and < 2147483648.0 ? (int)d : int.MinValue;
}
