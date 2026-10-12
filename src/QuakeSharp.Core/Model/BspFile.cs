namespace QuakeSharp.Core.Model;

/// <summary>bspfile.h: the on-disk format of a map (.bsp). The #defines are generated (BspFile.g.cs).</summary>
public static partial class BspFile
{
    // sizeof the on-disk structures
    public const int DheaderSize = 4 + (8 * HEADER_LUMPS);
    public const int DmodelSize = (9 * 4) + (4 * MAX_MAP_HULLS) + (3 * 4);
    public const int DvertexSize = 12;
    public const int DplaneSize = 20;
    public const int DnodeSize = 24;
    public const int DclipnodeSize = 8;
    public const int TexinfoSize = 40;
    public const int DedgeSize = 4;
    public const int DfaceSize = 20;
    public const int DleafSize = 28;

    // miptex_t: char name[16]; unsigned width, height; unsigned offsets[MIPLEVELS]
    public const int MiptexSize = 16 + 4 + 4 + (4 * MIPLEVELS);
}
