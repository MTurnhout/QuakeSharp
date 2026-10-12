using System.Runtime.CompilerServices;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;

namespace QuakeSharp.Core.Render;

// The software renderer's structures (r_shared.h, r_local.h, d_local.h, d_iface.h). C's pointers into arrays that are
// compared or offset (edges, surfaces, the surface cache, the frame and z-buffer) become indices or offsets; linked
// structures keep their links as references.

/// <summary>r_local.h: clipplane_t</summary>
public sealed class ClipPlane
{
    public Vec3 normal;
    public float dist;
    public ClipPlane? next;
    public bool leftedge;
    public bool rightedge;
}

/// <summary>r_shared.h: espan_t</summary>
public sealed class Espan
{
    public int u, v, count;
    public Espan? pnext;
}

/// <summary>r_shared.h: surf_t. Surfaces are an array; C's surface pointers are indices in it.</summary>
public sealed class Surf
{
    /// <summary>active surface stack in r_edge.c</summary>
    public Surf? next;

    /// <summary>used in r_edge.c for active surf stack</summary>
    public Surf? prev;

    /// <summary>pointer to linked list of spans to draw</summary>
    public Espan? spans;

    /// <summary>sorting key (BSP order)</summary>
    public int key;

    /// <summary>set during tracing</summary>
    public int last_u;

    /// <summary>0 = not in span, 1 = in span, -1 = in inverted span (end before start)</summary>
    public int spanstate;

    /// <summary>currentface flags</summary>
    public int flags;

    /// <summary>associated data like msurface_t</summary>
    public MSurface? data;

    public Client.Entity? entity;

    /// <summary>nearest 1/z on surface, for mipmapping</summary>
    public float nearzi;

    public bool insubmodel;
    public float d_ziorigin, d_zistepu, d_zistepv;
}

/// <summary>unsigned short[2]</summary>
[InlineArray(2)]
public struct UShort2
{
    private ushort element0;
}

/// <summary>
/// r_shared.h: edge_t. Edges are an array; C's edge pointers into it are indices (the cached edge offsets of the
/// model's edges too). The owner, a medge_t pointer, is an array of the model's edges and an index in it.
/// </summary>
public sealed class Edge
{
    public int u;
    public int u_step;
    public Edge? prev, next;
    public UShort2 surfs;
    public Edge? nextremove;
    public float nearzi;
    public MEdge[]? ownerarray;
    public int ownerindex;
}

/// <summary>r_local.h: bedge_t. Its vertices are an array of positions and an index in it (C's mvertex_t pointers).</summary>
public sealed class BEdge
{
    public Vec3[] v0array = [];
    public int v0;
    public Vec3[] v1array = [];
    public int v1;
    public BEdge? pnext;
}

/// <summary>int[6]: a finalvert_t's u, v, s, t, l, 1/z</summary>
[InlineArray(6)]
public struct Int6
{
    private int element0;
}

/// <summary>d_iface.h: finalvert_t</summary>
public struct FinalVert
{
    /// <summary>u, v, s, t, l, 1/z</summary>
    public Int6 v;

    public int flags;
    public float reserved;
}

/// <summary>r_local.h: auxvert_t</summary>
public struct AuxVert
{
    /// <summary>viewspace x, y</summary>
    public Vec3 fv;
}

/// <summary>d_iface.h: emitpoint_t</summary>
public struct EmitPoint
{
    public float u, v;
    public float s, t;
    public float zi;
}

/// <summary>d_iface.h: polyvert_t</summary>
public struct PolyVert
{
    public float u, v, zi, s, t;
}

/// <summary>d_local.h: sspan_t</summary>
public struct SSpan
{
    public int u, v, count;
}

/// <summary>r_local.h: alight_t</summary>
public sealed class ALight
{
    public int ambientlight;
    public int shadelight;
    public Vec3 plightvec;
}

/// <summary>
/// d_local.h: surfcache_t, a block of the surface cache. The cache is one byte array, as the C's memory block: a
/// block's header is at <see cref="offset"/> in it (C's pointer), its pixels (data) at <see cref="DataOffset"/>.
/// </summary>
public sealed class SurfCache
{
    /// <summary>offsetof (surfcache_t, data) and sizeof (surfcache_t) in the 32-bit C</summary>
    public const int HeaderSize = 48;

    public const int Size = 52;

    /// <summary>where the block is in the cache</summary>
    public int offset;

    public SurfCache? next;

    /// <summary>NULL is an empty chunk of memory: the surface whose cachespots[ownermip] points here</summary>
    public MSurface? owner;

    public int ownermip;

    /// <summary>checked for strobe flush</summary>
    public readonly int[] lightadj = new int[BspFile.MAXLIGHTMAPS];

    public int dlight;

    /// <summary>including header</summary>
    public int size;

    public uint width;

    /// <summary>DEBUG only needed for debug</summary>
    public uint height;

    public float mipscale;

    /// <summary>checked for animating textures</summary>
    public Texture? texture;

    public int DataOffset => offset + HeaderSize;
}

/// <summary>d_iface.h: drawsurf_t</summary>
public sealed class DrawSurf
{
    /// <summary>destination for generated surface (an offset in the surface cache)</summary>
    public int surfdat;

    /// <summary>destination logical width in bytes</summary>
    public int rowbytes;

    /// <summary>description for surface to generate</summary>
    public MSurface surf = null!;

    /// <summary>adjust for lightmap levels for dynamic lighting</summary>
    public readonly int[] lightadj = new int[BspFile.MAXLIGHTMAPS];

    /// <summary>corrected for animating textures</summary>
    public Texture texture = null!;

    /// <summary>mipmapped ratio of surface texels / world pixels</summary>
    public int surfmip;

    /// <summary>in mipmapped texels</summary>
    public int surfwidth;

    /// <summary>in mipmapped texels</summary>
    public int surfheight;
}

/// <summary>
/// d_iface.h: affinetridesc_t. The triangles and vertices are arrays with a start (C's pointers into the model's
/// triangles or a local one, and into the projected vertices or the clipped ones).
/// </summary>
public sealed class AffineTriDesc
{
    public byte[] pskin = [];
    public int skinwidth;
    public int skinheight;
    public MTriangle[] ptriangles = [];
    public int ptrianglesstart;
    public FinalVert[] pfinalverts = [];
    public int numtriangles;
    public bool drawtype;
    public int seamfixupX16;
}

/// <summary>d_iface.h: spritedesc_t</summary>
public sealed class SpriteDesc
{
    public int nump;

    /// <summary>there's room for an extra element at [nump], if the driver wants to duplicate element [0] at element
    /// [nump] to avoid dealing with wrapping</summary>
    public EmitPoint[] pverts = [];

    public MSpriteFrame pspriteframe = null!;

    /// <summary>in worldspace</summary>
    public Vec3 vup, vright, vpn;

    public float nearzi;
}

/// <summary>d_iface.h: zpointdesc_t</summary>
public struct ZPointDesc
{
    public int u, v;
    public float zi;
    public int color;
}

/// <summary>d_iface.h: polydesc_t</summary>
public sealed class PolyDesc
{
    public int numverts;
    public float nearzi;
    public MSurface? pcurrentface;
    public PolyVert[] pverts = [];
}

/// <summary>r_local.h: btofpoly_t</summary>
public struct BtofPoly
{
    public int clipflags;
    public MSurface psurf;
}

/// <summary>
/// d_polyse.c: spanpackage_t. The destination, z and texel pointers are offsets in the view buffer, the z-buffer and
/// the skin.
/// </summary>
public struct SpanPackage
{
    public int pdest;
    public int pz;
    public int count;
    public int ptex;
    public int sfrac, tfrac, light, zi;
}

/// <summary>d_polyse.c: edgetable</summary>
public sealed class EdgeTable(bool isflattop, int numleftedges, int[] pleftedgevert0, int[] pleftedgevert1,
    int[]? pleftedgevert2, int numrightedges, int[] prightedgevert0, int[] prightedgevert1, int[]? prightedgevert2)
{
    public readonly bool isflattop = isflattop;
    public readonly int numleftedges = numleftedges;
    public readonly int[] pleftedgevert0 = pleftedgevert0;
    public readonly int[] pleftedgevert1 = pleftedgevert1;
    public readonly int[]? pleftedgevert2 = pleftedgevert2;
    public readonly int numrightedges = numrightedges;
    public readonly int[] prightedgevert0 = prightedgevert0;
    public readonly int[] prightedgevert1 = prightedgevert1;
    public readonly int[]? prightedgevert2 = prightedgevert2;
}

/// <summary>r_sprite.c: vec5_t</summary>
[InlineArray(5)]
public struct Vec5
{
    private float element0;
}
