namespace QuakeSharp.Core.Model;

/// <summary>spritegn.h: the on-disk format of a sprite (.spr).</summary>
public static class SpriteGen
{
    public const int SPRITE_VERSION = 1;

    // little-endian "IDSP"
    public const int IDSPRITEHEADER = ('P' << 24) + ('S' << 16) + ('D' << 8) + 'I';

    // sizeof(dsprite_t), sizeof(dspriteframe_t)
    public const int DspriteSize = 36;
    public const int DspriteframeSize = 16;
}

/// <summary>spritegn.h: spriteframetype_t</summary>
public enum SpriteFrameType
{
    SPR_SINGLE = 0,
    SPR_GROUP,
}
