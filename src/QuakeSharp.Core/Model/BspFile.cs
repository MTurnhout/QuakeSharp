namespace QuakeSharp.Core.Model;

/// <summary>bspfile.h: the on-disk format of a map (.bsp).</summary>
public static class BspFile
{
    public const int BSPVERSION = 29;

    // lump_t indices of dheader_t
    public const int LUMP_ENTITIES = 0;
    public const int LUMP_PLANES = 1;
    public const int LUMP_TEXTURES = 2;
    public const int LUMP_VERTEXES = 3;
    public const int LUMP_VISIBILITY = 4;
    public const int LUMP_NODES = 5;
    public const int LUMP_TEXINFO = 6;
    public const int LUMP_FACES = 7;
    public const int LUMP_LIGHTING = 8;
    public const int LUMP_CLIPNODES = 9;
    public const int LUMP_LEAFS = 10;
    public const int LUMP_MARKSURFACES = 11;
    public const int LUMP_EDGES = 12;
    public const int LUMP_SURFEDGES = 13;
    public const int LUMP_MODELS = 14;
    public const int HEADER_LUMPS = 15;

    // miptex_t: char name[16]; unsigned width, height; unsigned offsets[MIPLEVELS]
    public const int MIPLEVELS = 4;
    public const int MiptexSize = 16 + 4 + 4 + (4 * MIPLEVELS);
}
