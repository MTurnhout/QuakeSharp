namespace QuakeSharp.Core.Model;

/// <summary>modelgen.h: the on-disk format of an alias model (.mdl).</summary>
public static class ModelGen
{
    public const int ALIAS_VERSION = 6;

    public const int ALIAS_ONSEAM = 0x0020;

    public const int DT_FACES_FRONT = 0x0010;

    // little-endian "IDPO"
    public const int IDPOLYHEADER = ('O' << 24) + ('P' << 16) + ('D' << 8) + 'I';

    // sizeof(mdl_t), stvert_t, dtriangle_t, trivertx_t, daliasframe_t, daliasgroup_t
    public const int MdlSize = 84;
    public const int StvertSize = 12;
    public const int DtriangleSize = 16;
    public const int TrivertxSize = 4;
    public const int DaliasframeSize = 24;
    public const int DaliasgroupSize = 12;
}

/// <summary>modelgen.h (and spritegn.h): synctype_t</summary>
public enum synctype_t
{
    ST_SYNC = 0,
    ST_RAND,
}

/// <summary>modelgen.h: aliasframetype_t</summary>
public enum aliasframetype_t
{
    ALIAS_SINGLE = 0,
    ALIAS_GROUP,
}

/// <summary>modelgen.h: aliasskintype_t</summary>
public enum aliasskintype_t
{
    ALIAS_SKIN_SINGLE = 0,
    ALIAS_SKIN_GROUP,
}

/// <summary>modelgen.h: mdl_t, an alias model's header.</summary>
public sealed class Mdl
{
    public int ident;
    public int version;
    public Common.Vec3 scale;
    public Common.Vec3 scale_origin;
    public float boundingradius;
    public Common.Vec3 eyeposition;
    public int numskins;
    public int skinwidth;
    public int skinheight;
    public int numverts;
    public int numtris;
    public int numframes;
    public synctype_t synctype;
    public int flags;
    public float size;
}

/// <summary>modelgen.h: stvert_t</summary>
public struct StVert
{
    public int onseam;
    public int s;
    public int t;
}

/// <summary>modelgen.h: trivertx_t</summary>
public struct TriVertX
{
    public Common.Byte3 v;
    public byte lightnormalindex;
}
