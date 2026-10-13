using System.Buffers.Binary;
using System.Text;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using static QuakeSharp.Core.Common.QString;
using static QuakeSharp.Core.Model.BspFile;
using ModelT = QuakeSharp.Core.Model.Model;

namespace QuakeSharp.Core;

// model.c: model loading and caching. Models are the only shared resource between a client and server running on
// the same machine. The cache is managed memory (design.md §3.2): an alias model, once loaded, stays loaded.
public sealed partial class QuakeEngine
{
    private const int MAX_MOD_KNOWN = 256;

    // model.c: values for model_t's needload
    private const int NL_PRESENT = 0;
    private const int NL_NEEDS_LOADED = 1;
    private const int NL_UNREFERENCED = 2;

    // r_local.h: MAXALIASVERTS; d_iface.h: MAX_LBM_HEIGHT; r_local.h: ALIAS_BASE_SIZE_RATIO
    private const int MAXALIASVERTS = 2000;
    private const int MAX_LBM_HEIGHT = 480;
    private const double ALIAS_BASE_SIZE_RATIO = 1.0 / 11.0;

    // model.c: ANIM_CYCLE
    private const int ANIM_CYCLE = 2;

    /// <summary>r_main.c: r_pixbytes (WinQuake's software renderer draws 8-bit pixels)</summary>
    public const int r_pixbytes = 1;

    public ModelT? loadmodel;

    /// <summary>for hunk tags</summary>
    public string loadname = "";

    public readonly byte[] mod_novis = new byte[MAX_MAP_LEAFS / 8];

    public readonly ModelT[] mod_known = [.. Enumerable.Range(0, MAX_MOD_KNOWN).Select(_ => new ModelT())];

    public int mod_numknown;

    // model.c: Mod_DecompressVis's static decompressed
    private readonly byte[] mod_decompressed = new byte[MAX_MAP_LEAFS / 8];

    // model.c: mod_base, the file being loaded
    private byte[] mod_base = [];

    // model.c: Mod_Init
    public void Mod_Init() => mod_novis.AsSpan().Fill(0xff);

    // model.c: Mod_Extradata
    // Caches the data if needed
    public object Mod_Extradata(ModelT mod)
    {
        object? r = mod.cache;
        if (r != null)
        {
            return r;
        }

        Mod_LoadModel(mod, true);

        return mod.cache ?? throw Sys_Error("Mod_Extradata: caching failed");
    }

    // model.c: Mod_PointInLeaf
    public static MLeaf Mod_PointInLeaf(in Vec3 p, ModelT? model)
    {
        MNodeBase node;
        float d;
        MPlane plane;

        if (model == null || model.nodes.Length == 0)
        {
            throw Sys_Error("Mod_PointInLeaf: bad model");
        }

        node = model.nodes[0];
        while (true)
        {
            if (node.contents < 0)
            {
                return (MLeaf)node;
            }

            var n = (MNode)node;
            plane = n.plane;
            d = MathLib.DotProduct(p, plane.normal) - plane.dist;
            if (d > 0)
            {
                node = n.children[0];
            }
            else
            {
                node = n.children[1];
            }
        }
    }

    // model.c: Mod_DecompressVis
    public byte[] Mod_DecompressVis(int @in, ModelT model)
    {
        int c;
        int @out;
        int row;
        byte[] vis = model.visdata!;

        row = (model.numleafs + 7) >> 3;
        @out = 0;

        if (@in < 0)
        {
            // no vis info, so make all visible
            while (row != 0)
            {
                mod_decompressed[@out++] = 0xff;
                row--;
            }

            return mod_decompressed;
        }

        do
        {
            if (vis[@in] != 0)
            {
                mod_decompressed[@out++] = vis[@in++];
                continue;
            }

            c = vis[@in + 1];
            @in += 2;
            while (c != 0)
            {
                mod_decompressed[@out++] = 0;
                c--;
            }
        }
        while (@out < row);

        return mod_decompressed;
    }

    // model.c: Mod_LeafPVS
    public byte[] Mod_LeafPVS(MLeaf leaf, ModelT model)
    {
        if (leaf == model.leafs[0])
        {
            return mod_novis;
        }

        return Mod_DecompressVis(leaf.compressed_vis, model);
    }

    // model.c: Mod_ClearAll
    public void Mod_ClearAll()
    {
        int i;
        ModelT mod;

        for (i = 0; i < mod_numknown; i++)
        {
            mod = mod_known[i];
            mod.needload = NL_UNREFERENCED;

            // FIX FOR CACHE_ALLOC ERRORS:
            if (mod.type == modtype_t.mod_sprite)
            {
                mod.cache = null;
            }
        }
    }

    // model.c: Mod_FindName
    public ModelT Mod_FindName(string name)
    {
        int i;
        ModelT mod = null!;
        ModelT? avail = null;

        if (Ch(name, 0) == 0)
        {
            throw Sys_Error("Mod_ForName: NULL name");
        }

        // search the currently loaded models
        for (i = 0; i < mod_numknown; i++)
        {
            mod = mod_known[i];
            if (Q_strcmp(mod.name, name) == 0)
            {
                break;
            }

            if (mod.needload == NL_UNREFERENCED)
            {
                if (avail == null || mod.type != modtype_t.mod_alias)
                {
                    avail = mod;
                }
            }
        }

        if (i == mod_numknown)
        {
            if (mod_numknown == MAX_MOD_KNOWN)
            {
                if (avail != null)
                {
                    mod = avail;
                    if (mod.type == modtype_t.mod_alias)
                    {
                        if (mod.cache != null)
                        {
                            mod.cache = null; // Cache_Free
                        }
                    }
                }
                else
                {
                    throw Sys_Error("mod_numknown == MAX_MOD_KNOWN");
                }
            }
            else
            {
                mod = mod_known[mod_numknown];
                mod_numknown++;
            }

            mod.name = name;
            mod.needload = NL_NEEDS_LOADED;
        }

        return mod;
    }

    // model.c: Mod_TouchModel
    public void Mod_TouchModel(string name)
    {
        ModelT mod;

        mod = Mod_FindName(name);

        // (if present, an alias model's Cache_Check: nothing to touch in managed memory)
        _ = mod;
    }

    // model.c: Mod_LoadModel
    // Loads a model into the cache
    public ModelT? Mod_LoadModel(ModelT mod, bool crash)
    {
        byte[]? buf;

        if (mod.type == modtype_t.mod_alias)
        {
            if (mod.cache != null)
            {
                mod.needload = NL_PRESENT;
                return mod;
            }
        }
        else
        {
            if (mod.needload == NL_PRESENT)
            {
                return mod;
            }
        }

        // because the world is so huge, load it one piece at a time

        // load the file
        buf = COM_LoadStackFile(mod.name);
        if (buf == null)
        {
            if (crash)
            {
                throw Sys_Error("Mod_NumForName: %s not found", mod.name);
            }

            return null;
        }

        // allocate a new model
        loadname = Com.FileBase(mod.name);

        loadmodel = mod;

        // fill it in

        // call the apropriate loader
        mod.needload = NL_PRESENT;

        switch (BinaryPrimitives.ReadInt32LittleEndian(buf))
        {
            case ModelGen.IDPOLYHEADER:
                Mod_LoadAliasModel(mod, buf);
                break;

            case SpriteGen.IDSPRITEHEADER:
                Mod_LoadSpriteModel(mod, buf);
                break;

            default:
                Mod_LoadBrushModel(mod, buf);
                break;
        }

        return mod;
    }

    // model.c: Mod_ForName
    // Loads in a model for the given name
    public ModelT? Mod_ForName(string name, bool crash)
    {
        ModelT mod;

        mod = Mod_FindName(name);

        return Mod_LoadModel(mod, crash);
    }

    // ===============================================================================
    // BRUSHMODEL LOADING
    // ===============================================================================

    private int LumpOfs(int lump) => I32(mod_base, 4 + (8 * lump));

    private int LumpLen(int lump) => I32(mod_base, 4 + (8 * lump) + 4);

    private static int I32(byte[] b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o));

    private static short I16(byte[] b, int o) => BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o));

    private static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));

    private static float F32(byte[] b, int o) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o));

    // A char[n] field: the characters up to its first 0
    private static string CharField(byte[] b, int o, int n)
    {
        int len = b.AsSpan(o, n).IndexOf((byte)0);
        return Encoding.Latin1.GetString(b, o, len < 0 ? n : len);
    }

    // model.c: Mod_LoadTextures
    private void Mod_LoadTextures(int l)
    {
        int i, j, pixels, num, max, altmax;
        int mt;
        Texture? tx, tx2;
        var anims = new Texture?[10];
        var altanims = new Texture?[10];
        int m;
        int nummiptex;

        if (LumpLen(l) == 0)
        {
            loadmodel!.textures = null;
            return;
        }

        m = LumpOfs(l);

        nummiptex = I32(mod_base, m);

        loadmodel!.numtextures = nummiptex;
        loadmodel.textures = new Texture?[nummiptex];

        for (i = 0; i < nummiptex; i++)
        {
            int dataofs = I32(mod_base, m + 4 + (4 * i));
            if (dataofs == -1)
            {
                continue;
            }

            mt = m + dataofs;
            string mtname = CharField(mod_base, mt, 16);
            uint width = (uint)I32(mod_base, mt + 16);
            uint height = (uint)I32(mod_base, mt + 20);

            if ((width & 15) != 0 || (height & 15) != 0)
            {
                throw Sys_Error("Texture %s is not 16 aligned", mtname);
            }

            pixels = (int)(width * height / 64 * 85);
            tx = new Texture();
            loadmodel.textures[i] = tx;

            tx.name = mtname;
            tx.width = width;
            tx.height = height;
            for (j = 0; j < MIPLEVELS; j++)
            {
                // the offsets in the pixels, which immediately follow the miptex_t
                tx.offsets[j] = (uint)I32(mod_base, mt + 24 + (4 * j)) - MiptexSize;
            }

            tx.pixels = mod_base.AsSpan(mt + MiptexSize, pixels).ToArray();

            if (Q_strncmp(mtname, "sky", 3) == 0)
            {
                R_InitSky(tx);
            }
        }

        // sequence the animations
        for (i = 0; i < nummiptex; i++)
        {
            tx = loadmodel.textures[i];
            if (tx == null || Ch(tx.name, 0) != '+')
            {
                continue;
            }

            if (tx.anim_next != null)
            {
                continue; // allready sequenced
            }

            // find the number of frames in the animation
            Array.Clear(anims);
            Array.Clear(altanims);

            max = Ch(tx.name, 1);
            altmax = 0;
            if (max is >= 'a' and <= 'z')
            {
                max -= 'a' - 'A';
            }

            if (max is >= '0' and <= '9')
            {
                max -= '0';
                altmax = 0;
                anims[max] = tx;
                max++;
            }
            else if (max is >= 'A' and <= 'J')
            {
                altmax = max - 'A';
                max = 0;
                altanims[altmax] = tx;
                altmax++;
            }
            else
            {
                throw Sys_Error("Bad animating texture %s", tx.name);
            }

            for (j = i + 1; j < nummiptex; j++)
            {
                tx2 = loadmodel.textures[j];
                if (tx2 == null || Ch(tx2.name, 0) != '+')
                {
                    continue;
                }

                if (Q_strcmp(Tail(tx2.name, 2), Tail(tx.name, 2)) != 0)
                {
                    continue;
                }

                num = Ch(tx2.name, 1);
                if (num is >= 'a' and <= 'z')
                {
                    num -= 'a' - 'A';
                }

                if (num is >= '0' and <= '9')
                {
                    num -= '0';
                    anims[num] = tx2;
                    if (num + 1 > max)
                    {
                        max = num + 1;
                    }
                }
                else if (num is >= 'A' and <= 'J')
                {
                    num -= 'A';
                    altanims[num] = tx2;
                    if (num + 1 > altmax)
                    {
                        altmax = num + 1;
                    }
                }
                else
                {
                    throw Sys_Error("Bad animating texture %s", tx.name);
                }
            }

            // link them all together
            for (j = 0; j < max; j++)
            {
                tx2 = anims[j] ?? throw Sys_Error("Missing frame %i of %s", j, tx.name);
                tx2.anim_total = max * ANIM_CYCLE;
                tx2.anim_min = j * ANIM_CYCLE;
                tx2.anim_max = (j + 1) * ANIM_CYCLE;
                tx2.anim_next = anims[(j + 1) % max];
                if (altmax != 0)
                {
                    tx2.alternate_anims = altanims[0];
                }
            }

            for (j = 0; j < altmax; j++)
            {
                tx2 = altanims[j] ?? throw Sys_Error("Missing frame %i of %s", j, tx.name);
                tx2.anim_total = altmax * ANIM_CYCLE;
                tx2.anim_min = j * ANIM_CYCLE;
                tx2.anim_max = (j + 1) * ANIM_CYCLE;
                tx2.anim_next = altanims[(j + 1) % altmax];
                if (max != 0)
                {
                    tx2.alternate_anims = anims[0];
                }
            }
        }

        static string Tail(string s, int from) => s.Length > from ? s[from..] : "";
    }

    // model.c: Mod_LoadLighting
    private void Mod_LoadLighting(int l)
    {
        if (LumpLen(l) == 0)
        {
            loadmodel!.lightdata = null;
            return;
        }

        loadmodel!.lightdata = mod_base.AsSpan(LumpOfs(l), LumpLen(l)).ToArray();
    }

    // model.c: Mod_LoadVisibility
    private void Mod_LoadVisibility(int l)
    {
        if (LumpLen(l) == 0)
        {
            loadmodel!.visdata = null;
            return;
        }

        loadmodel!.visdata = mod_base.AsSpan(LumpOfs(l), LumpLen(l)).ToArray();
    }

    // model.c: Mod_LoadEntities (the C string: up to its 0)
    private void Mod_LoadEntities(int l)
    {
        if (LumpLen(l) == 0)
        {
            loadmodel!.entities = null;
            return;
        }

        loadmodel!.entities = CharField(mod_base, LumpOfs(l), LumpLen(l));
    }

    // The number of on-disk structures of size in a lump
    private int LumpCount(int l, int size)
    {
        if (LumpLen(l) % size != 0)
        {
            throw Sys_Error("MOD_LoadBmodel: funny lump size in %s", loadmodel!.name);
        }

        return LumpLen(l) / size;
    }

    // model.c: Mod_LoadVertexes
    private void Mod_LoadVertexes(int l)
    {
        int @in = LumpOfs(l);
        int i, count;

        count = LumpCount(l, DvertexSize);
        var @out = new Vec3[count];

        loadmodel!.vertexes = @out;
        loadmodel.numvertexes = count;

        for (i = 0; i < count; i++, @in += DvertexSize)
        {
            @out[i][0] = F32(mod_base, @in);
            @out[i][1] = F32(mod_base, @in + 4);
            @out[i][2] = F32(mod_base, @in + 8);
        }
    }

    // model.c: Mod_LoadSubmodels
    private void Mod_LoadSubmodels(int l)
    {
        int @in = LumpOfs(l);
        int i, j, count;

        count = LumpCount(l, DmodelSize);
        var @out = new DModel[count];

        loadmodel!.submodels = @out;
        loadmodel.numsubmodels = count;

        for (i = 0; i < count; i++, @in += DmodelSize)
        {
            var o = @out[i] = new DModel();
            for (j = 0; j < 3; j++)
            {
                // spread the mins / maxs by a pixel
                o.mins[j] = F32(mod_base, @in + (4 * j)) - 1;
                o.maxs[j] = F32(mod_base, @in + 12 + (4 * j)) + 1;
                o.origin[j] = F32(mod_base, @in + 24 + (4 * j));
            }

            for (j = 0; j < MAX_MAP_HULLS; j++)
            {
                o.headnode[j] = I32(mod_base, @in + 36 + (4 * j));
            }

            o.visleafs = I32(mod_base, @in + 52);
            o.firstface = I32(mod_base, @in + 56);
            o.numfaces = I32(mod_base, @in + 60);
        }
    }

    // model.c: Mod_LoadEdges
    private void Mod_LoadEdges(int l)
    {
        int @in = LumpOfs(l);
        int i, count;

        count = LumpCount(l, DedgeSize);
        var @out = new MEdge[count + 1];

        loadmodel!.edges = @out;
        loadmodel.numedges = count;

        for (i = 0; i < count; i++, @in += DedgeSize)
        {
            @out[i].v0 = U16(mod_base, @in);
            @out[i].v1 = U16(mod_base, @in + 2);
        }
    }

    // model.c: Mod_LoadTexinfo
    private void Mod_LoadTexinfo(int l)
    {
        int @in = LumpOfs(l);
        int i, j, count;
        int miptex;
        float len1, len2;

        count = LumpCount(l, TexinfoSize);
        var @out = new MTexinfo[count];

        loadmodel!.texinfo = @out;
        loadmodel.numtexinfo = count;

        for (i = 0; i < count; i++, @in += TexinfoSize)
        {
            var o = @out[i] = new MTexinfo();
            for (j = 0; j < 8; j++)
            {
                o.vecs[j / 4][j % 4] = F32(mod_base, @in + (4 * j));
            }

            len1 = MathLib.Length(new Vec3(o.vecs[0][0], o.vecs[0][1], o.vecs[0][2]));
            len2 = MathLib.Length(new Vec3(o.vecs[1][0], o.vecs[1][1], o.vecs[1][2]));
            len1 = (len1 + len2) / 2;
            if (len1 < 0.32)
            {
                o.mipadjust = 4;
            }
            else if (len1 < 0.49)
            {
                o.mipadjust = 3;
            }
            else if (len1 < 0.99)
            {
                o.mipadjust = 2;
            }
            else
            {
                o.mipadjust = 1;
            }

            miptex = I32(mod_base, @in + 32);
            o.flags = I32(mod_base, @in + 36);

            if (loadmodel.textures == null)
            {
                o.texture = r_notexture_mip; // checkerboard texture
                o.flags = 0;
            }
            else
            {
                if (miptex >= loadmodel.numtextures)
                {
                    throw Sys_Error("miptex >= loadmodel->numtextures");
                }

                Texture? texture = loadmodel.textures[miptex];
                if (texture == null)
                {
                    o.texture = r_notexture_mip; // texture not found
                    o.flags = 0;
                }
                else
                {
                    o.texture = texture;
                }
            }
        }
    }

    // model.c: CalcSurfaceExtents
    // Fills in s->texturemins[] and s->extents[]
    private void CalcSurfaceExtents(MSurface s)
    {
        Span<float> mins = stackalloc float[2];
        Span<float> maxs = stackalloc float[2];
        float val;
        int i, j, e;
        Vec3 v;
        MTexinfo tex;
        Span<int> bmins = stackalloc int[2];
        Span<int> bmaxs = stackalloc int[2];

        mins[0] = mins[1] = 999999;
        maxs[0] = maxs[1] = -99999;

        tex = s.texinfo;

        for (i = 0; i < s.numedges; i++)
        {
            e = loadmodel!.surfedges[s.firstedge + i];
            if (e >= 0)
            {
                v = loadmodel.vertexes[loadmodel.edges[e].v0];
            }
            else
            {
                v = loadmodel.vertexes[loadmodel.edges[-e].v1];
            }

            for (j = 0; j < 2; j++)
            {
                val = (v[0] * tex.vecs[j][0]) +
                    (v[1] * tex.vecs[j][1]) +
                    (v[2] * tex.vecs[j][2]) +
                    tex.vecs[j][3];
                if (val < mins[j])
                {
                    mins[j] = val;
                }

                if (val > maxs[j])
                {
                    maxs[j] = val;
                }
            }
        }

        for (i = 0; i < 2; i++)
        {
            bmins[i] = (int)Math.Floor(mins[i] / 16);
            bmaxs[i] = (int)Math.Ceiling(maxs[i] / 16);

            s.texturemins[i] = (short)(bmins[i] * 16);
            s.extents[i] = (short)((bmaxs[i] - bmins[i]) * 16);
            if ((tex.flags & TEX_SPECIAL) == 0 && s.extents[i] > 256)
            {
                throw Sys_Error("Bad surface extents");
            }
        }
    }

    // model.c: Mod_LoadFaces
    private void Mod_LoadFaces(int l)
    {
        int @in = LumpOfs(l);
        int i, count, surfnum;
        int planenum, side;

        count = LumpCount(l, DfaceSize);
        var @out = new MSurface[count];

        loadmodel!.surfaces = @out;
        loadmodel.numsurfaces = count;

        for (surfnum = 0; surfnum < count; surfnum++, @in += DfaceSize)
        {
            var o = @out[surfnum] = new MSurface();
            o.firstedge = I32(mod_base, @in + 4);
            o.numedges = I16(mod_base, @in + 8);
            o.flags = 0;

            planenum = I16(mod_base, @in);
            side = I16(mod_base, @in + 2);
            if (side != 0)
            {
                o.flags |= MSurface.SURF_PLANEBACK;
            }

            o.plane = loadmodel.planes[planenum];

            o.texinfo = loadmodel.texinfo[I16(mod_base, @in + 10)];

            CalcSurfaceExtents(o);

            // lighting info
            for (i = 0; i < MAXLIGHTMAPS; i++)
            {
                o.styles[i] = mod_base[@in + 12 + i];
            }

            i = I32(mod_base, @in + 16);
            if (i == -1)
            {
                o.samples = -1;
            }
            else
            {
                o.samples = i;
            }

            o.lightdata = loadmodel.lightdata;

            // set the drawing flags flag
            if (Q_strncmp(o.texinfo.texture.name, "sky", 3) == 0)
            {
                // sky
                o.flags |= MSurface.SURF_DRAWSKY | MSurface.SURF_DRAWTILED;
                continue;
            }

            if (Q_strncmp(o.texinfo.texture.name, "*", 1) == 0)
            {
                // turbulent
                o.flags |= MSurface.SURF_DRAWTURB | MSurface.SURF_DRAWTILED;
                for (i = 0; i < 2; i++)
                {
                    o.extents[i] = 16384;
                    o.texturemins[i] = -8192;
                }

                continue;
            }
        }
    }

    // model.c: Mod_SetParent
    private static void Mod_SetParent(MNodeBase node, MNode? parent)
    {
        node.parent = parent;
        if (node.contents < 0)
        {
            return;
        }

        var n = (MNode)node;
        Mod_SetParent(n.children[0], n);
        Mod_SetParent(n.children[1], n);
    }

    // model.c: Mod_LoadNodes
    private void Mod_LoadNodes(int l)
    {
        int @in = LumpOfs(l);
        int i, j, count, p;

        count = LumpCount(l, DnodeSize);
        var @out = new MNode[count];
        for (i = 0; i < count; i++)
        {
            @out[i] = new MNode { index = i };
        }

        loadmodel!.nodes = @out;
        loadmodel.numnodes = count;

        for (i = 0; i < count; i++, @in += DnodeSize)
        {
            MNode o = @out[i];
            for (j = 0; j < 3; j++)
            {
                o.minmaxs[j] = I16(mod_base, @in + 8 + (2 * j));
                o.minmaxs[3 + j] = I16(mod_base, @in + 14 + (2 * j));
            }

            p = I32(mod_base, @in);
            o.plane = loadmodel.planes[p];

            o.firstsurface = U16(mod_base, @in + 20);
            o.numsurfaces = U16(mod_base, @in + 22);

            for (j = 0; j < 2; j++)
            {
                p = I16(mod_base, @in + 4 + (2 * j));
                if (p >= 0)
                {
                    o.children[j] = loadmodel.nodes[p];
                }
                else
                {
                    o.children[j] = loadmodel.leafs[-1 - p];
                }
            }
        }

        Mod_SetParent(loadmodel.nodes[0], null); // sets nodes and leafs
    }

    // model.c: Mod_LoadLeafs
    private void Mod_LoadLeafs(int l)
    {
        int @in = LumpOfs(l);
        int i, j, count, p;

        count = LumpCount(l, DleafSize);
        var @out = new MLeaf[count];

        loadmodel!.leafs = @out;
        loadmodel.numleafs = count;

        for (i = 0; i < count; i++, @in += DleafSize)
        {
            var o = @out[i] = new MLeaf { index = i };
            for (j = 0; j < 3; j++)
            {
                o.minmaxs[j] = I16(mod_base, @in + 8 + (2 * j));
                o.minmaxs[3 + j] = I16(mod_base, @in + 14 + (2 * j));
            }

            p = I32(mod_base, @in);
            o.contents = p;

            // LittleShort: the unsigned shorts on disk are read as shorts
            o.firstmarksurface = I16(mod_base, @in + 20);
            o.nummarksurfaces = I16(mod_base, @in + 22);

            p = I32(mod_base, @in + 4);
            if (p == -1)
            {
                o.compressed_vis = -1;
            }
            else
            {
                o.compressed_vis = p;
            }

            o.efrags = null;

            for (j = 0; j < 4; j++)
            {
                o.ambient_sound_level[j] = mod_base[@in + 24 + j];
            }
        }
    }

    // model.c: Mod_LoadClipnodes
    private void Mod_LoadClipnodes(int l)
    {
        int @in = LumpOfs(l);
        int i, count;

        count = LumpCount(l, DclipnodeSize);
        var @out = new DClipnode[count];

        loadmodel!.clipnodes = @out;
        loadmodel.numclipnodes = count;

        ref Hull hull = ref loadmodel.hulls[1];
        hull.clipnodes = @out;
        hull.firstclipnode = 0;
        hull.lastclipnode = count - 1;
        hull.planes = loadmodel.planes;
        hull.clip_mins[0] = -16;
        hull.clip_mins[1] = -16;
        hull.clip_mins[2] = -24;
        hull.clip_maxs[0] = 16;
        hull.clip_maxs[1] = 16;
        hull.clip_maxs[2] = 32;

        hull = ref loadmodel.hulls[2];
        hull.clipnodes = @out;
        hull.firstclipnode = 0;
        hull.lastclipnode = count - 1;
        hull.planes = loadmodel.planes;
        hull.clip_mins[0] = -32;
        hull.clip_mins[1] = -32;
        hull.clip_mins[2] = -24;
        hull.clip_maxs[0] = 32;
        hull.clip_maxs[1] = 32;
        hull.clip_maxs[2] = 64;

        for (i = 0; i < count; i++, @in += DclipnodeSize)
        {
            @out[i].planenum = I32(mod_base, @in);
            @out[i].children0 = I16(mod_base, @in + 4);
            @out[i].children1 = I16(mod_base, @in + 6);
        }
    }

    // model.c: Mod_MakeHull0
    // Deplicate the drawing hull structure as a clipping hull
    private void Mod_MakeHull0()
    {
        int i, j, count;

        ref Hull hull = ref loadmodel!.hulls[0];

        count = loadmodel.numnodes;
        var @out = new DClipnode[count];

        hull.clipnodes = @out;
        hull.firstclipnode = 0;
        hull.lastclipnode = count - 1;
        hull.planes = loadmodel.planes;

        var planenums = new Dictionary<MPlane, int>(ReferenceEqualityComparer.Instance);
        for (i = 0; i < loadmodel.planes.Length; i++)
        {
            planenums[loadmodel.planes[i]] = i;
        }

        for (i = 0; i < count; i++)
        {
            MNode @in = loadmodel.nodes[i];
            @out[i].planenum = planenums[@in.plane];
            for (j = 0; j < 2; j++)
            {
                MNodeBase child = @in.children[j];
                short c = child.contents < 0 ? (short)child.contents : (short)((MNode)child).index;
                if (j == 0)
                {
                    @out[i].children0 = c;
                }
                else
                {
                    @out[i].children1 = c;
                }
            }
        }
    }

    // model.c: Mod_LoadMarksurfaces
    private void Mod_LoadMarksurfaces(int l)
    {
        int @in = LumpOfs(l);
        int i, j, count;

        count = LumpCount(l, 2);
        var @out = new MSurface[count];

        loadmodel!.marksurfaces = @out;
        loadmodel.nummarksurfaces = count;

        for (i = 0; i < count; i++)
        {
            j = I16(mod_base, @in + (2 * i));
            if (j >= loadmodel.numsurfaces)
            {
                throw Sys_Error("Mod_ParseMarksurfaces: bad surface number");
            }

            @out[i] = loadmodel.surfaces[j];
        }
    }

    // model.c: Mod_LoadSurfedges
    private void Mod_LoadSurfedges(int l)
    {
        int @in = LumpOfs(l);
        int i, count;

        count = LumpCount(l, 4);
        var @out = new int[count];

        loadmodel!.surfedges = @out;
        loadmodel.numsurfedges = count;

        for (i = 0; i < count; i++)
        {
            @out[i] = I32(mod_base, @in + (4 * i));
        }
    }

    // model.c: Mod_LoadPlanes (twice as many planes as the lump has, as the C allocates)
    private void Mod_LoadPlanes(int l)
    {
        int @in = LumpOfs(l);
        int i, j;
        int count;
        int bits;

        count = LumpCount(l, DplaneSize);
        var @out = new MPlane[count * 2];
        for (i = 0; i < @out.Length; i++)
        {
            @out[i] = new MPlane();
        }

        loadmodel!.planes = @out;
        loadmodel.numplanes = count;

        for (i = 0; i < count; i++, @in += DplaneSize)
        {
            MPlane o = @out[i];
            bits = 0;
            for (j = 0; j < 3; j++)
            {
                o.normal[j] = F32(mod_base, @in + (4 * j));
                if (o.normal[j] < 0)
                {
                    bits |= 1 << j;
                }
            }

            o.dist = F32(mod_base, @in + 12);
            o.type = (byte)I32(mod_base, @in + 16);
            o.signbits = (byte)bits;
        }
    }

    // model.c: RadiusFromBounds
    private static float RadiusFromBounds(in Vec3 mins, in Vec3 maxs)
    {
        int i;
        Vec3 corner = default;

        for (i = 0; i < 3; i++)
        {
            corner[i] = Math.Abs(mins[i]) > Math.Abs(maxs[i]) ? Math.Abs(mins[i]) : Math.Abs(maxs[i]);
        }

        return MathLib.Length(corner);
    }

    // model.c: Mod_LoadBrushModel
    private void Mod_LoadBrushModel(ModelT mod, byte[] buffer)
    {
        int i, j;
        DModel bm;

        loadmodel!.type = modtype_t.mod_brush;

        i = I32(buffer, 0);
        if (i != BSPVERSION)
        {
            throw Sys_Error("Mod_LoadBrushModel: %s has wrong version number (%i should be %i)", mod.name, i, BSPVERSION);
        }

        // swap all the lumps
        mod_base = buffer;

        // load into heap
        Mod_LoadVertexes(LUMP_VERTEXES);
        Mod_LoadEdges(LUMP_EDGES);
        Mod_LoadSurfedges(LUMP_SURFEDGES);
        Mod_LoadTextures(LUMP_TEXTURES);
        Mod_LoadLighting(LUMP_LIGHTING);
        Mod_LoadPlanes(LUMP_PLANES);
        Mod_LoadTexinfo(LUMP_TEXINFO);
        Mod_LoadFaces(LUMP_FACES);
        Mod_LoadMarksurfaces(LUMP_MARKSURFACES);
        Mod_LoadVisibility(LUMP_VISIBILITY);
        Mod_LoadLeafs(LUMP_LEAFS);
        Mod_LoadNodes(LUMP_NODES);
        Mod_LoadClipnodes(LUMP_CLIPNODES);
        Mod_LoadEntities(LUMP_ENTITIES);
        Mod_LoadSubmodels(LUMP_MODELS);

        Mod_MakeHull0();

        mod.numframes = 2; // regular and alternate animation
        mod.flags = 0;

        // set up the submodels (FIXME: this is confusing)
        for (i = 0; i < mod.numsubmodels; i++)
        {
            bm = mod.submodels[i];

            mod.hulls[0].firstclipnode = bm.headnode[0];
            for (j = 1; j < MAX_MAP_HULLS; j++)
            {
                mod.hulls[j].firstclipnode = bm.headnode[j];
                mod.hulls[j].lastclipnode = mod.numclipnodes - 1;
            }

            mod.firstmodelsurface = bm.firstface;
            mod.nummodelsurfaces = bm.numfaces;

            MathLib.VectorCopy(bm.maxs, ref mod.maxs);
            MathLib.VectorCopy(bm.mins, ref mod.mins);
            mod.radius = RadiusFromBounds(mod.mins, mod.maxs);

            mod.numleafs = bm.visleafs;

            if (i < mod.numsubmodels - 1)
            {
                // duplicate the basic information
                string name = CFormat.Sprintf("*%i", i + 1);
                loadmodel = Mod_FindName(name);
                loadmodel.CopyFrom(mod);
                loadmodel.name = name;
                mod = loadmodel;
            }
        }
    }

    // ==============================================================================
    // ALIAS MODELS
    // ==============================================================================

    // A trivertx_t on disk
    private static TriVertX TriVert(byte[] b, int o)
    {
        TriVertX t = default;
        t.v[0] = b[o];
        t.v[1] = b[o + 1];
        t.v[2] = b[o + 2];
        t.lightnormalindex = b[o + 3];
        return t;
    }

    // model.c: Mod_LoadAliasFrame
    private static int Mod_LoadAliasFrame(byte[] buffer, int pin, out TriVertX[] pframeindex, int numv,
        out TriVertX pbboxmin, out TriVertX pbboxmax, out string name)
    {
        TriVertX[] pframe;
        int pinframe;
        int j;
        int pdaliasframe = pin;

        name = CharField(buffer, pdaliasframe + 8, 16);

        // these are byte values, so we don't have to worry about endianness
        pbboxmin = TriVert(buffer, pdaliasframe);
        pbboxmax = TriVert(buffer, pdaliasframe + 4);

        pinframe = pdaliasframe + ModelGen.DaliasframeSize;
        pframe = new TriVertX[numv];

        pframeindex = pframe;

        for (j = 0; j < numv; j++)
        {
            // these are all byte values, so no need to deal with endianness
            pframe[j] = TriVert(buffer, pinframe + (ModelGen.TrivertxSize * j));
        }

        pinframe += numv * ModelGen.TrivertxSize;

        return pinframe;
    }

    // model.c: Mod_LoadAliasGroup
    private int Mod_LoadAliasGroup(byte[] buffer, int pin, out MAliasGroup pframeindex, int numv,
        out TriVertX pbboxmin, out TriVertX pbboxmax, out string name)
    {
        int pingroup = pin;
        MAliasGroup paliasgroup;
        int i, numframes;
        int pin_intervals;
        float[] poutintervals;
        int ptemp;

        numframes = I32(buffer, pingroup);

        paliasgroup = new MAliasGroup { frames = new MAliasGroupFrameDesc[numframes] };

        paliasgroup.numframes = numframes;

        // these are byte values, so we don't have to worry about endianness
        pbboxmin = TriVert(buffer, pingroup + 4);
        pbboxmax = TriVert(buffer, pingroup + 8);

        pframeindex = paliasgroup;

        pin_intervals = pingroup + ModelGen.DaliasgroupSize;

        poutintervals = new float[numframes];

        paliasgroup.intervals = poutintervals;

        for (i = 0; i < numframes; i++)
        {
            poutintervals[i] = F32(buffer, pin_intervals);
            if (poutintervals[i] <= 0.0)
            {
                throw Sys_Error("Mod_LoadAliasGroup: interval<=0");
            }

            pin_intervals += 4;
        }

        ptemp = pin_intervals;
        name = "";

        for (i = 0; i < numframes; i++)
        {
            ptemp = Mod_LoadAliasFrame(buffer, ptemp,
                out paliasgroup.frames[i].frame,
                numv,
                out paliasgroup.frames[i].bboxmin,
                out paliasgroup.frames[i].bboxmax,
                out name);
        }

        return ptemp;
    }

    // model.c: Mod_LoadAliasSkin
    private static int Mod_LoadAliasSkin(byte[] buffer, int pin, out byte[] pskinindex, int skinsize)
    {
        byte[] pskin;
        int pinskin;

        pskin = new byte[skinsize * r_pixbytes];
        pinskin = pin;
        pskinindex = pskin;

        // r_pixbytes is 1: the 16-bit path is for drivers WinQuake does not have
        buffer.AsSpan(pinskin, skinsize).CopyTo(pskin);

        pinskin += skinsize;

        return pinskin;
    }

    // model.c: Mod_LoadAliasSkinGroup
    private static int Mod_LoadAliasSkinGroup(byte[] buffer, int pin, out MAliasSkinGroup pskinindex, int skinsize)
    {
        int pinskingroup = pin;
        MAliasSkinGroup paliasskingroup;
        int i, numskins;
        int pinskinintervals;
        float[] poutskinintervals;
        int ptemp;

        numskins = I32(buffer, pinskingroup);

        paliasskingroup = new MAliasSkinGroup { skindescs = new MAliasSkinDesc[numskins] };

        paliasskingroup.numskins = numskins;

        pskinindex = paliasskingroup;

        pinskinintervals = pinskingroup + 4;

        poutskinintervals = new float[numskins];

        paliasskingroup.intervals = poutskinintervals;

        for (i = 0; i < numskins; i++)
        {
            poutskinintervals[i] = F32(buffer, pinskinintervals);
            if (poutskinintervals[i] <= 0)
            {
                throw Sys_Error("Mod_LoadAliasSkinGroup: interval<=0");
            }

            pinskinintervals += 4;
        }

        ptemp = pinskinintervals;

        for (i = 0; i < numskins; i++)
        {
            ptemp = Mod_LoadAliasSkin(buffer, ptemp, out byte[] skin, skinsize);
            paliasskingroup.skindescs[i].skin = skin;
        }

        return ptemp;
    }

    // model.c: Mod_LoadAliasModel
    private void Mod_LoadAliasModel(ModelT mod, byte[] buffer)
    {
        int i;
        Mdl pmodel;
        int pinmodel = 0;
        StVert[] pstverts;
        int pinstverts;
        AliasHdr pheader;
        MTriangle[] ptri;
        int pintriangles;
        int version, numframes, numskins;
        int pframetype;
        int pskintype;
        MAliasSkinDesc[] pskindesc;
        int skinsize;

        version = I32(buffer, pinmodel + 4);
        if (version != ModelGen.ALIAS_VERSION)
        {
            throw Sys_Error("%s has wrong version number (%i should be %i)", mod.name, version, ModelGen.ALIAS_VERSION);
        }

        // allocate space for a working header, plus all the data except the frames, skin and group info
        pheader = new AliasHdr();
        pmodel = new Mdl();

        mod.flags = I32(buffer, pinmodel + 76);

        // endian-adjust and copy the data, starting with the alias model header
        pmodel.boundingradius = F32(buffer, pinmodel + 32);
        pmodel.numskins = I32(buffer, pinmodel + 48);
        pmodel.skinwidth = I32(buffer, pinmodel + 52);
        pmodel.skinheight = I32(buffer, pinmodel + 56);

        if (pmodel.skinheight > MAX_LBM_HEIGHT)
        {
            throw Sys_Error("model %s has a skin taller than %d", mod.name, MAX_LBM_HEIGHT);
        }

        pmodel.numverts = I32(buffer, pinmodel + 60);

        if (pmodel.numverts <= 0)
        {
            throw Sys_Error("model %s has no vertices", mod.name);
        }

        if (pmodel.numverts > MAXALIASVERTS)
        {
            throw Sys_Error("model %s has too many vertices", mod.name);
        }

        pmodel.numtris = I32(buffer, pinmodel + 64);

        if (pmodel.numtris <= 0)
        {
            throw Sys_Error("model %s has no triangles", mod.name);
        }

        pmodel.numframes = I32(buffer, pinmodel + 68);
        pmodel.size = (float)(F32(buffer, pinmodel + 80) * ALIAS_BASE_SIZE_RATIO);
        mod.synctype = (synctype_t)I32(buffer, pinmodel + 72);
        mod.numframes = pmodel.numframes;

        for (i = 0; i < 3; i++)
        {
            pmodel.scale[i] = F32(buffer, pinmodel + 8 + (4 * i));
            pmodel.scale_origin[i] = F32(buffer, pinmodel + 20 + (4 * i));
            pmodel.eyeposition[i] = F32(buffer, pinmodel + 36 + (4 * i));
        }

        numskins = pmodel.numskins;
        numframes = pmodel.numframes;

        if ((pmodel.skinwidth & 0x03) != 0)
        {
            throw Sys_Error("Mod_LoadAliasModel: skinwidth not multiple of 4");
        }

        pheader.model = pmodel;

        // load the skins
        skinsize = pmodel.skinheight * pmodel.skinwidth;

        if (numskins < 1)
        {
            throw Sys_Error("Mod_LoadAliasModel: Invalid # of skins: %d\n", numskins);
        }

        pskintype = pinmodel + ModelGen.MdlSize;

        pskindesc = new MAliasSkinDesc[numskins];

        pheader.skindesc = pskindesc;

        for (i = 0; i < numskins; i++)
        {
            aliasskintype_t skintype;

            skintype = (aliasskintype_t)I32(buffer, pskintype);
            pskindesc[i].type = skintype;

            if (skintype == aliasskintype_t.ALIAS_SKIN_SINGLE)
            {
                pskintype = Mod_LoadAliasSkin(buffer, pskintype + 4, out byte[] skin, skinsize);
                pskindesc[i].skin = skin;
            }
            else
            {
                pskintype = Mod_LoadAliasSkinGroup(buffer, pskintype + 4, out MAliasSkinGroup group, skinsize);
                pskindesc[i].skin = group;
            }
        }

        // set base s and t vertices
        pstverts = new StVert[pmodel.numverts];
        pinstverts = pskintype;

        pheader.stverts = pstverts;

        for (i = 0; i < pmodel.numverts; i++)
        {
            int o = pinstverts + (ModelGen.StvertSize * i);
            pstverts[i].onseam = I32(buffer, o);

            // put s and t in 16.16 format
            pstverts[i].s = I32(buffer, o + 4) << 16;
            pstverts[i].t = I32(buffer, o + 8) << 16;
        }

        // set up the triangles
        ptri = new MTriangle[pmodel.numtris];
        pintriangles = pinstverts + (ModelGen.StvertSize * pmodel.numverts);

        pheader.triangles = ptri;

        for (i = 0; i < pmodel.numtris; i++)
        {
            int j;
            int o = pintriangles + (ModelGen.DtriangleSize * i);

            ptri[i].facesfront = I32(buffer, o);

            for (j = 0; j < 3; j++)
            {
                ptri[i].vertindex[j] = I32(buffer, o + 4 + (4 * j));
            }
        }

        // load the frames
        if (numframes < 1)
        {
            throw Sys_Error("Mod_LoadAliasModel: Invalid # of frames: %d\n", numframes);
        }

        pframetype = pintriangles + (ModelGen.DtriangleSize * pmodel.numtris);

        pheader.frames = new MAliasFrameDesc[numframes];

        for (i = 0; i < numframes; i++)
        {
            aliasframetype_t frametype;

            frametype = (aliasframetype_t)I32(buffer, pframetype);
            pheader.frames[i].type = frametype;

            if (frametype == aliasframetype_t.ALIAS_SINGLE)
            {
                pframetype = Mod_LoadAliasFrame(buffer, pframetype + 4,
                    out TriVertX[] frame,
                    pmodel.numverts,
                    out pheader.frames[i].bboxmin,
                    out pheader.frames[i].bboxmax,
                    out pheader.frames[i].name);
                pheader.frames[i].frame = frame;
            }
            else
            {
                pframetype = Mod_LoadAliasGroup(buffer, pframetype + 4,
                    out MAliasGroup group,
                    pmodel.numverts,
                    out pheader.frames[i].bboxmin,
                    out pheader.frames[i].bboxmax,
                    out pheader.frames[i].name);
                pheader.frames[i].frame = group;
            }
        }

        mod.type = modtype_t.mod_alias;

        // FIXME: do this right
        mod.mins[0] = mod.mins[1] = mod.mins[2] = -16;
        mod.maxs[0] = mod.maxs[1] = mod.maxs[2] = 16;

        // move the complete, relocatable alias model to the cache
        mod.cache = pheader;
    }

    // =============================================================================

    // model.c: Mod_LoadSpriteFrame
    private static int Mod_LoadSpriteFrame(byte[] buffer, int pin, out MSpriteFrame ppframe)
    {
        int pinframe;
        MSpriteFrame pspriteframe;
        int width, height, size;
        Span<int> origin = stackalloc int[2];

        pinframe = pin;

        width = I32(buffer, pinframe + 8);
        height = I32(buffer, pinframe + 12);
        size = width * height;

        pspriteframe = new MSpriteFrame { pixels = new byte[size * r_pixbytes] };

        ppframe = pspriteframe;

        pspriteframe.width = width;
        pspriteframe.height = height;
        origin[0] = I32(buffer, pinframe);
        origin[1] = I32(buffer, pinframe + 4);

        pspriteframe.up = origin[1];
        pspriteframe.down = origin[1] - height;
        pspriteframe.left = origin[0];
        pspriteframe.right = width + origin[0];

        // r_pixbytes is 1
        buffer.AsSpan(pinframe + SpriteGen.DspriteframeSize, size).CopyTo(pspriteframe.pixels);

        return pinframe + SpriteGen.DspriteframeSize + size;
    }

    // model.c: Mod_LoadSpriteGroup
    private static int Mod_LoadSpriteGroup(byte[] buffer, int pin, out MSpriteGroup ppframe)
    {
        int pingroup = pin;
        MSpriteGroup pspritegroup;
        int i, numframes;
        int pin_intervals;
        float[] poutintervals;
        int ptemp;

        numframes = I32(buffer, pingroup);

        pspritegroup = new MSpriteGroup { frames = new MSpriteFrame[numframes] };

        pspritegroup.numframes = numframes;

        ppframe = pspritegroup;

        pin_intervals = pingroup + 4;

        poutintervals = new float[numframes];

        pspritegroup.intervals = poutintervals;

        for (i = 0; i < numframes; i++)
        {
            poutintervals[i] = F32(buffer, pin_intervals);
            if (poutintervals[i] <= 0.0)
            {
                throw Sys_Error("Mod_LoadSpriteGroup: interval<=0");
            }

            pin_intervals += 4;
        }

        ptemp = pin_intervals;

        for (i = 0; i < numframes; i++)
        {
            ptemp = Mod_LoadSpriteFrame(buffer, ptemp, out pspritegroup.frames[i]);
        }

        return ptemp;
    }

    // model.c: Mod_LoadSpriteModel
    private static void Mod_LoadSpriteModel(ModelT mod, byte[] buffer)
    {
        int i;
        int version;
        int pin = 0;
        MSprite psprite;
        int numframes;
        int pframetype;

        version = I32(buffer, pin + 4);
        if (version != SpriteGen.SPRITE_VERSION)
        {
            throw Sys_Error("%s has wrong version number (%i should be %i)", mod.name, version, SpriteGen.SPRITE_VERSION);
        }

        numframes = I32(buffer, pin + 24);

        psprite = new MSprite { frames = new MSpriteFrameDesc[Math.Max(numframes, 0)] };

        mod.cache = psprite;

        psprite.type = I32(buffer, pin + 8);
        psprite.maxwidth = I32(buffer, pin + 16);
        psprite.maxheight = I32(buffer, pin + 20);
        psprite.beamlength = F32(buffer, pin + 28);
        mod.synctype = (synctype_t)I32(buffer, pin + 32);
        psprite.numframes = numframes;

        mod.mins[0] = mod.mins[1] = -psprite.maxwidth / 2;
        mod.maxs[0] = mod.maxs[1] = psprite.maxwidth / 2;
        mod.mins[2] = -psprite.maxheight / 2;
        mod.maxs[2] = psprite.maxheight / 2;

        // load the frames
        if (numframes < 1)
        {
            throw Sys_Error("Mod_LoadSpriteModel: Invalid # of frames: %d\n", numframes);
        }

        mod.numframes = numframes;
        mod.flags = 0;

        pframetype = pin + SpriteGen.DspriteSize;

        for (i = 0; i < numframes; i++)
        {
            spriteframetype_t frametype;

            frametype = (spriteframetype_t)I32(buffer, pframetype);
            psprite.frames[i].type = frametype;

            if (frametype == spriteframetype_t.SPR_SINGLE)
            {
                pframetype = Mod_LoadSpriteFrame(buffer, pframetype + 4, out MSpriteFrame frame);
                psprite.frames[i].frameptr = frame;
            }
            else
            {
                pframetype = Mod_LoadSpriteGroup(buffer, pframetype + 4, out MSpriteGroup group);
                psprite.frames[i].frameptr = group;
            }
        }

        mod.type = modtype_t.mod_sprite;
    }

    // =============================================================================

    // model.c: Mod_Print
    public void Mod_Print()
    {
        int i;
        ModelT mod;

        Con_Printf("Cached models:\n");
        for (i = 0; i < mod_numknown; i++)
        {
            mod = mod_known[i];

            // %8p: the cache's address in C, which depends on the build; 1 for a model with data, 0 without, as the
            // oracle prints it (tools/oracle/quake-oracle.patch)
            Con_Printf("%08X : %s", mod.cache != null ? 1 : 0, mod.name);
            if ((mod.needload & NL_UNREFERENCED) != 0)
            {
                Con_Printf(" (!R)");
            }

            if ((mod.needload & NL_NEEDS_LOADED) != 0)
            {
                Con_Printf(" (!P)");
            }

            Con_Printf("\n");
        }
    }
}
