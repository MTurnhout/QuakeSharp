namespace QuakeSharp.Core.Model;

/// <summary>modelgen.h: the on-disk format of an alias model (.mdl).</summary>
public static class ModelGen
{
    public const int ALIAS_VERSION = 6;

    // little-endian "IDPO"
    public const int IDPOLYHEADER = ('O' << 24) + ('P' << 16) + ('D' << 8) + 'I';

    // sizeof(mdl_t)
    public const int MdlSize = 84;
}

/// <summary>modelgen.h: aliasskintype_t</summary>
public enum AliasSkinType
{
    ALIAS_SKIN_SINGLE = 0,
    ALIAS_SKIN_GROUP,
}
