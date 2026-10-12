using QuakeSharp.Core.Common;
using static QuakeSharp.Core.Model.BspFile;

namespace QuakeSharp.Core.Model;

// model.h: the in-memory models. C's pointers into a model's arrays become references to their elements (planes,
// nodes, leafs, textures, texinfo) or indices where C does pointer arithmetic on them (marksurfaces, visdata,
// lightdata: -1 for NULL).

/// <summary>model.h: modtype_t</summary>
public enum modtype_t
{
    mod_brush,
    mod_sprite,
    mod_alias,
}

/// <summary>
/// model.h: texture_t. The pixels that follow the structure in C are <see cref="pixels"/>; <see cref="offsets"/>
/// are the four mip levels' offsets in it.
/// </summary>
public sealed class Texture
{
    public string name = "";
    public uint width, height;

    /// <summary>total tenths in sequence ( 0 = no)</summary>
    public int anim_total;

    /// <summary>time for this frame min &lt;=time&lt; max</summary>
    public int anim_min, anim_max;

    /// <summary>in the animation sequence</summary>
    public Texture? anim_next;

    /// <summary>bmodels in frmae 1 use these</summary>
    public Texture? alternate_anims;

    /// <summary>four mip maps stored (offsets in <see cref="pixels"/>)</summary>
    public readonly uint[] offsets = new uint[MIPLEVELS];

    public byte[] pixels = [];
}

/// <summary>model.h: medge_t</summary>
public struct MEdge
{
    public ushort v0, v1;
    public uint cachededgeoffset;
}

/// <summary>model.h: mtexinfo_t</summary>
public sealed class MTexinfo
{
    public Mat2x4 vecs;
    public float mipadjust;
    public Texture texture = null!;
    public int flags;
}

/// <summary>model.h: msurface_t</summary>
public sealed class MSurface
{
    // model.h: the surface flags
    public const int SURF_PLANEBACK = 2;
    public const int SURF_DRAWSKY = 4;
    public const int SURF_DRAWSPRITE = 8;
    public const int SURF_DRAWTURB = 0x10;
    public const int SURF_DRAWTILED = 0x20;
    public const int SURF_DRAWBACKGROUND = 0x40;

    /// <summary>should be drawn when node is crossed</summary>
    public int visframe;

    public int dlightframe;
    public int dlightbits;

    public MPlane plane = null!;
    public int flags;

    /// <summary>look up in model->surfedges[], negative numbers are backwards edges</summary>
    public int firstedge;

    public int numedges;

    // surface generation data
    public readonly Render.SurfCache?[] cachespots = new Render.SurfCache?[MIPLEVELS];

    public readonly short[] texturemins = new short[2];
    public readonly short[] extents = new short[2];
    public MTexinfo texinfo = null!;

    // lighting info
    public readonly byte[] styles = new byte[MAXLIGHTMAPS];

    /// <summary>[numstyles*surfsize]: an offset in the model's lightdata, -1 for none</summary>
    public int samples;

    /// <summary>the lightdata of the model the surface belongs to, which samples is an offset in</summary>
    public byte[]? lightdata;
}

/// <summary>What model.h's mnode_t and mleaf_t have in common (a node's children can be either).</summary>
public abstract class MNodeBase
{
    /// <summary>0 for a node, to differentiate from leafs; a leaf's negative contents number</summary>
    public int contents;

    /// <summary>node needs to be traversed if current</summary>
    public int visframe;

    /// <summary>for bounding box culling</summary>
    public readonly short[] minmaxs = new short[6];

    public MNode? parent;
}

/// <summary>model.h: mnode_t</summary>
public sealed class MNode : MNodeBase
{
    /// <summary>The node's index in its model's nodes (C's pointer difference).</summary>
    public int index;

    public MPlane plane = null!;
    public readonly MNodeBase[] children = new MNodeBase[2];
    public ushort firstsurface;
    public ushort numsurfaces;
}

/// <summary>model.h: mleaf_t</summary>
public sealed class MLeaf : MNodeBase
{
    /// <summary>The leaf's index in its model's leafs (C's pointer difference).</summary>
    public int index;

    /// <summary>an offset in the model's visdata, -1 for none</summary>
    public int compressed_vis;

    public Efrag? efrags;

    /// <summary>an index in the model's marksurfaces</summary>
    public int firstmarksurface;

    public int nummarksurfaces;

    /// <summary>BSP sequence number for leaf's contents</summary>
    public int key;

    public readonly byte[] ambient_sound_level = new byte[NUM_AMBIENTS];
}

/// <summary>bspfile.h: dclipnode_t</summary>
public struct DClipnode
{
    public int planenum;

    /// <summary>negative numbers are contents</summary>
    public short children0, children1;
}

/// <summary>bspfile.h: dmodel_t</summary>
public sealed class DModel
{
    public Vec3 mins, maxs;
    public Vec3 origin;
    public readonly int[] headnode = new int[MAX_MAP_HULLS];

    /// <summary>not including the solid leaf 0</summary>
    public int visleafs;

    public int firstface, numfaces;
}

/// <summary>model.h: hull_t (a value, as model_t holds them: a submodel's copy changes its own)</summary>
public struct Hull
{
    public DClipnode[] clipnodes;
    public MPlane[] planes;
    public int firstclipnode;
    public int lastclipnode;
    public Vec3 clip_mins;
    public Vec3 clip_maxs;
}

/// <summary>render.h: efrag_t</summary>
public sealed class Efrag
{
    public MLeaf? leaf;
    public Efrag? leafnext;
    public Client.Entity? entity;
    public Efrag? entnext;
}

/// <summary>model.h: mspriteframe_t</summary>
public sealed class MSpriteFrame
{
    public int width;
    public int height;
    public float up, down, left, right;
    public byte[] pixels = [];
}

/// <summary>model.h: mspritegroup_t</summary>
public sealed class MSpriteGroup
{
    public int numframes;
    public float[] intervals = [];
    public MSpriteFrame[] frames = [];
}

/// <summary>model.h: mspriteframedesc_t (frameptr is an <see cref="MSpriteFrame"/> or, for a group, an
/// <see cref="MSpriteGroup"/>, as C casts it)</summary>
public struct MSpriteFrameDesc
{
    public spriteframetype_t type;
    public object frameptr;
}

/// <summary>model.h: msprite_t</summary>
public sealed class MSprite
{
    public int type;
    public int maxwidth;
    public int maxheight;
    public int numframes;
    public float beamlength;
    public MSpriteFrameDesc[] frames = [];
}

/// <summary>model.h: maliasframedesc_t (frame is the pose, a <see cref="TriVertX"/> array, or for a group an
/// <see cref="MAliasGroup"/>, where C keeps their offsets)</summary>
public struct MAliasFrameDesc
{
    public aliasframetype_t type;
    public TriVertX bboxmin;
    public TriVertX bboxmax;
    public object frame;
    public string name;
}

/// <summary>model.h: maliasskindesc_t (skin is the pixels, or for a group an <see cref="MAliasSkinGroup"/>)</summary>
public struct MAliasSkinDesc
{
    public aliasskintype_t type;
    public object skin;
}

/// <summary>model.h: maliasgroupframedesc_t</summary>
public struct MAliasGroupFrameDesc
{
    public TriVertX bboxmin;
    public TriVertX bboxmax;
    public TriVertX[] frame;
}

/// <summary>model.h: maliasgroup_t</summary>
public sealed class MAliasGroup
{
    public int numframes;
    public float[] intervals = [];
    public MAliasGroupFrameDesc[] frames = [];
}

/// <summary>model.h: maliasskingroup_t</summary>
public sealed class MAliasSkinGroup
{
    public int numskins;
    public float[] intervals = [];
    public MAliasSkinDesc[] skindescs = [];
}

/// <summary>model.h: mtriangle_t</summary>
public struct MTriangle
{
    public int facesfront;
    public Int3 vertindex;
}

/// <summary>model.h: aliashdr_t, with its parts as arrays where C keeps their offsets.</summary>
public sealed class AliasHdr
{
    public Mdl model = null!;
    public StVert[] stverts = [];
    public MAliasSkinDesc[] skindesc = [];
    public MTriangle[] triangles = [];
    public MAliasFrameDesc[] frames = [];
}

/// <summary>model.h: model_t</summary>
public sealed class Model
{
    // model.h: the flags (in alias models' headers)

    /// <summary>leave a trail</summary>
    public const int EF_ROCKET = 1;

    /// <summary>leave a trail</summary>
    public const int EF_GRENADE = 2;

    /// <summary>leave a trail</summary>
    public const int EF_GIB = 4;

    /// <summary>rotate (bonus items)</summary>
    public const int EF_ROTATE = 8;

    /// <summary>green split trail</summary>
    public const int EF_TRACER = 16;

    /// <summary>small blood trail</summary>
    public const int EF_ZOMGIB = 32;

    /// <summary>orange split trail + rotate</summary>
    public const int EF_TRACER2 = 64;

    /// <summary>purple trail</summary>
    public const int EF_TRACER3 = 128;

    public string name = "";

    /// <summary>bmodels and sprites don't cache normally (model.c: NL_PRESENT, NL_NEEDS_LOADED, NL_UNREFERENCED)</summary>
    public int needload;

    public modtype_t type;
    public int numframes;
    public synctype_t synctype;

    public int flags;

    // volume occupied by the model
    public Vec3 mins, maxs;
    public float radius;

    // brush model
    public int firstmodelsurface, nummodelsurfaces;

    public int numsubmodels;
    public DModel[] submodels = [];

    public int numplanes;
    public MPlane[] planes = [];

    /// <summary>number of visible leafs, not counting 0</summary>
    public int numleafs;

    public MLeaf[] leafs = [];

    public int numvertexes;
    public Vec3[] vertexes = [];

    public int numedges;
    public MEdge[] edges = [];

    public int numnodes;
    public MNode[] nodes = [];

    public int numtexinfo;
    public MTexinfo[] texinfo = [];

    public int numsurfaces;
    public MSurface[] surfaces = [];

    public int numsurfedges;
    public int[] surfedges = [];

    public int numclipnodes;
    public DClipnode[] clipnodes = [];

    public int nummarksurfaces;
    public MSurface[] marksurfaces = [];

    public Hull[] hulls = new Hull[MAX_MAP_HULLS];

    public int numtextures;
    public Texture?[]? textures;

    public byte[]? visdata;
    public byte[]? lightdata;
    public string? entities;

    /// <summary>additional model data (cache.data): an <see cref="AliasHdr"/> or an <see cref="MSprite"/></summary>
    public object? cache;

    // model.c: Mod_LoadBrushModel's *loadmodel = *mod, a submodel's copy of its map
    public void CopyFrom(Model other)
    {
        name = other.name;
        needload = other.needload;
        type = other.type;
        numframes = other.numframes;
        synctype = other.synctype;
        flags = other.flags;
        mins = other.mins;
        maxs = other.maxs;
        radius = other.radius;
        firstmodelsurface = other.firstmodelsurface;
        nummodelsurfaces = other.nummodelsurfaces;
        numsubmodels = other.numsubmodels;
        submodels = other.submodels;
        numplanes = other.numplanes;
        planes = other.planes;
        numleafs = other.numleafs;
        leafs = other.leafs;
        numvertexes = other.numvertexes;
        vertexes = other.vertexes;
        numedges = other.numedges;
        edges = other.edges;
        numnodes = other.numnodes;
        nodes = other.nodes;
        numtexinfo = other.numtexinfo;
        texinfo = other.texinfo;
        numsurfaces = other.numsurfaces;
        surfaces = other.surfaces;
        numsurfedges = other.numsurfedges;
        surfedges = other.surfedges;
        numclipnodes = other.numclipnodes;
        clipnodes = other.clipnodes;
        nummarksurfaces = other.nummarksurfaces;
        marksurfaces = other.marksurfaces;
        hulls = (Hull[])other.hulls.Clone();
        numtextures = other.numtextures;
        textures = other.textures;
        visdata = other.visdata;
        lightdata = other.lightdata;
        entities = other.entities;
        cache = other.cache;
    }
}
