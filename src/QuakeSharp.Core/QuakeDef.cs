namespace QuakeSharp.Core;

/// <summary>Version constants of the engine QuakeSharp ports.</summary>
public static partial class QuakeDef
{
    // quakedef.h: VERSION
    public const double VERSION = 1.09;

    // protocol.h: PROTOCOL_VERSION
    public const int PROTOCOL_VERSION = 15;

    // server.h: the entity effects (edict->v.effects)
    public const int EF_BRIGHTFIELD = 1;
    public const int EF_MUZZLEFLASH = 2;
    public const int EF_BRIGHTLIGHT = 4;
    public const int EF_DIMLIGHT = 8;

    // render.h: TOP_RANGE (soldier uniform colors), BOTTOM_RANGE
    public const int TOP_RANGE = 16;
    public const int BOTTOM_RANGE = 96;
}
