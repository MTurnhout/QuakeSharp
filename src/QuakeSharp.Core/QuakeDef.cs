namespace QuakeSharp.Core;

/// <summary>Version constants of the engine QuakeSharp ports.</summary>
public static partial class QuakeDef
{
    // quakedef.h: VERSION
    public const double VERSION = 1.09;

    // protocol.h: PROTOCOL_VERSION
    public const int PROTOCOL_VERSION = 15;

    // quakedef.h: ON_EPSILON (point on plane side epsilon)
    public const double ON_EPSILON = 0.1;

    // render.h: TOP_RANGE (soldier uniform colors), BOTTOM_RANGE
    public const int TOP_RANGE = 16;
    public const int BOTTOM_RANGE = 96;
}
