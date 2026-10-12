namespace QuakeSharp.Core.Model;

/// <summary>spritegn.h: the on-disk format of a sprite (.spr).</summary>
public static class SpriteGen
{
    public const int SPRITE_VERSION = 1;

    // little-endian "IDSP"
    public const int IDSPRITEHEADER = ('P' << 24) + ('S' << 16) + ('D' << 8) + 'I';

    // spritegn.h: the sprite types (msprite_t.type)
    public const int SPR_VP_PARALLEL_UPRIGHT = 0;
    public const int SPR_FACING_UPRIGHT = 1;
    public const int SPR_VP_PARALLEL = 2;
    public const int SPR_ORIENTED = 3;
    public const int SPR_VP_PARALLEL_ORIENTED = 4;

    // sizeof(dsprite_t), sizeof(dspriteframe_t)
    public const int DspriteSize = 36;
    public const int DspriteframeSize = 16;
}

/// <summary>spritegn.h: spriteframetype_t</summary>
public enum spriteframetype_t
{
    SPR_SINGLE = 0,
    SPR_GROUP,
}
