using System.Runtime.InteropServices;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using QuakeSharp.Core.Numerics;

namespace QuakeSharp.Core.Enhanced;

/// <summary>A vertex of a brush model's surfaces: where it is, its texture coordinates in texels, its surface.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct EnhancedBrushVertex(float X, float Y, float Z, float S, float T, int Surface);

/// <summary>
/// What r_surf.c's lighting needs of a surface (design.md §11.6), for the shader to light each texel as
/// R_BuildLightMap and R_AddDynamicLights light the surface's luxels: its plane, its texture axes, where its
/// lightmaps start in the model's light data (-1: none) and their light styles (four bytes, 255 none), its flags.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct EnhancedSurface(
    float NormalX, float NormalY, float NormalZ, float Dist,
    float S0, float S1, float S2, float S3,
    float T0, float T1, float T2, float T3,
    int TextureMinS, int TextureMinT, int ExtentS, int ExtentT,
    int LightOffset, int Styles, int Flags, int Texture);

/// <summary>A BSP texture (miptex): its four mip levels of palette indices and r_surf.c's animation chain.</summary>
public sealed class EnhancedTexture
{
    public required string Name { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>The four mip levels, each after the one before (full size, half, quarter, eighth).</summary>
    public required byte[] Pixels { get; init; }

    /// <summary>Where each mip level starts in <see cref="Pixels"/>.</summary>
    public required int[] MipOffsets { get; init; }

    public int AnimTotal { get; init; }

    public int AnimMin { get; init; }

    public int AnimMax { get; init; }

    /// <summary>The next texture of the animation (an index of the model's textures; -1 none).</summary>
    public int AnimNext { get; init; } = -1;

    /// <summary>The other animation, shown while the entity's frame is not 0 (-1 none).</summary>
    public int Alternate { get; init; } = -1;
}

/// <summary>
/// A brush model (a level's world with its submodels, or a BSP item such as a health box) made ready for the GPU
/// (design.md §11.6, "Models, built once"): every surface a fan of triangles, the indices grouped by submodel and
/// texture, the surfaces' lighting records, the light data and the textures. Built on the game thread from a loaded
/// model, never written after.
/// </summary>
public sealed class EnhancedBrushModel
{
    public required EnhancedBrushVertex[] Vertices { get; init; }

    public required int[] Indices { get; init; }

    public required EnhancedSurface[] Surfaces { get; init; }

    /// <summary>The model's light data (the lightmaps' samples, 0-255).</summary>
    public required byte[] LightData { get; init; }

    public required EnhancedTexture[] Textures { get; init; }

    /// <summary>For each submodel (0 is the model itself), its draws: a texture and a range of <see cref="Indices"/>.</summary>
    public required (int Texture, int FirstIndex, int Count)[][] Submodels { get; init; }

    /// <summary>The model's own name (maps/e1m1.bsp).</summary>
    public required string Name { get; init; }

    /// <summary>The model's surfaces and textures made ready (model.c's loaded <paramref name="model"/>, read only).</summary>
    public static EnhancedBrushModel Build(Model.Model model)
    {
        // the textures, by their index in the model; the animation chains as indices
        Texture?[] source = model.textures ?? [];
        var index = new Dictionary<Texture, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] is { } t)
            {
                index[t] = i;
            }
        }

        var textures = new EnhancedTexture[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            textures[i] = source[i] is { } t ? Texture(t, index) : Missing();
        }

        // the surfaces and their triangles
        var vertices = new List<EnhancedBrushVertex>();
        var surfaces = new EnhancedSurface[model.numsurfaces];
        var fans = new (int FirstVertex, int Count)[model.numsurfaces];
        for (int i = 0; i < model.numsurfaces; i++)
        {
            MSurface surf = model.surfaces[i];
            MTexinfo tex = surf.texinfo;
            int texture = index.GetValueOrDefault(tex.texture, -1);
            fans[i] = (vertices.Count, surf.numedges);
            for (int e = 0; e < surf.numedges; e++)
            {
                int edge = model.surfedges[surf.firstedge + e];
                Vec3 v = edge >= 0 ? model.vertexes[model.edges[edge].v0] : model.vertexes[model.edges[-edge].v1];
                float s = (v[0] * tex.vecs[0][0]) + (v[1] * tex.vecs[0][1]) + (v[2] * tex.vecs[0][2]) + tex.vecs[0][3];
                float t = (v[0] * tex.vecs[1][0]) + (v[1] * tex.vecs[1][1]) + (v[2] * tex.vecs[1][2]) + tex.vecs[1][3];
                vertices.Add(new EnhancedBrushVertex(v[0], v[1], v[2], s, t, i));
            }

            int styles = surf.styles[0] | (surf.styles[1] << 8) | (surf.styles[2] << 16) | (surf.styles[3] << 24);
            surfaces[i] = new EnhancedSurface(
                surf.plane.normal[0], surf.plane.normal[1], surf.plane.normal[2], surf.plane.dist,
                tex.vecs[0][0], tex.vecs[0][1], tex.vecs[0][2], tex.vecs[0][3],
                tex.vecs[1][0], tex.vecs[1][1], tex.vecs[1][2], tex.vecs[1][3],
                surf.texturemins[0], surf.texturemins[1], surf.extents[0], surf.extents[1],
                surf.samples, styles, surf.flags, texture);
        }

        // the indices, by submodel and then texture, so each submodel draws a range per texture
        var indices = new List<int>();
        int submodelCount = Math.Max(1, model.numsubmodels);
        var submodels = new (int, int, int)[submodelCount][];
        for (int m = 0; m < submodelCount; m++)
        {
            int first = model.numsubmodels > 0 ? model.submodels[m].firstface : 0;
            int count = model.numsubmodels > 0 ? model.submodels[m].numfaces : model.numsurfaces;
            var draws = new List<(int, int, int)>();
            foreach (IGrouping<int, int> group in Enumerable.Range(first, count).GroupBy(s => surfaces[s].Texture).OrderBy(g => g.Key))
            {
                int start = indices.Count;
                foreach (int s in group)
                {
                    (int v0, int n) = fans[s];
                    for (int k = 2; k < n; k++)
                    {
                        indices.Add(v0);
                        indices.Add(v0 + k - 1);
                        indices.Add(v0 + k);
                    }
                }

                draws.Add((group.Key, start, indices.Count - start));
            }

            submodels[m] = [.. draws];
        }

        return new EnhancedBrushModel
        {
            Name = model.name,
            Vertices = [.. vertices],
            Indices = [.. indices],
            Surfaces = surfaces,
            LightData = model.lightdata is { } light ? (byte[])light.Clone() : [],
            Textures = textures,
            Submodels = submodels,
        };
    }

    /// <summary>
    /// r_surf.c: R_TextureAnimation, a read-only copy: the texture shown for <paramref name="texture"/> at
    /// <paramref name="time"/> (cl.time) on an entity of frame <paramref name="frame"/>.
    /// </summary>
    public int TextureAnimation(int texture, int frame, double time)
    {
        if (texture < 0)
        {
            return texture;
        }

        EnhancedTexture t = Textures[texture];
        if (frame != 0 && t.Alternate >= 0)
        {
            texture = t.Alternate;
            t = Textures[texture];
        }

        if (t.AnimTotal == 0)
        {
            return texture;
        }

        int relative = X86.FtoI(time * 10) % t.AnimTotal;
        for (int count = 0; (t.AnimMin > relative || t.AnimMax <= relative) && count <= 100; count++)
        {
            if (t.AnimNext < 0)
            {
                return texture; // a broken cycle: the software stops the game here; the enhanced mode shows what it has
            }

            texture = t.AnimNext;
            t = Textures[texture];
        }

        return texture;
    }

    private static EnhancedTexture Texture(Texture t, Dictionary<Texture, int> index)
    {
        int[] offsets = new int[BspFile.MIPLEVELS];
        int size = 0;
        for (int m = 0; m < BspFile.MIPLEVELS; m++)
        {
            offsets[m] = size;
            size += (int)((t.width >> m) * (t.height >> m));
        }

        byte[] pixels = new byte[size];
        for (int m = 0; m < BspFile.MIPLEVELS; m++)
        {
            int length = (int)((t.width >> m) * (t.height >> m));
            int from = (int)t.offsets[m];
            if (from >= 0 && from + length <= t.pixels.Length)
            {
                t.pixels.AsSpan(from, length).CopyTo(pixels.AsSpan(offsets[m], length));
            }
        }

        return new EnhancedTexture
        {
            Name = t.name,
            Width = (int)t.width,
            Height = (int)t.height,
            Pixels = pixels,
            MipOffsets = offsets,
            AnimTotal = t.anim_total,
            AnimMin = t.anim_min,
            AnimMax = t.anim_max,
            AnimNext = t.anim_next is { } next ? index.GetValueOrDefault(next, -1) : -1,
            Alternate = t.alternate_anims is { } alt ? index.GetValueOrDefault(alt, -1) : -1,
        };
    }

    // a texture the BSP lacks (model.c's r_notexture_mip stands in): a 16x16 checker of two greys
    private static EnhancedTexture Missing()
    {
        byte[] pixels = new byte[(16 * 16) + (8 * 8) + (4 * 4) + (2 * 2)];
        int at = 0;
        for (int m = 0; m < 4; m++)
        {
            int size = 16 >> m;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    pixels[at++] = (byte)(((x * 2 / size) ^ (y * 2 / size)) != 0 ? 0 : 0xff);
                }
            }
        }

        return new EnhancedTexture { Name = "", Width = 16, Height = 16, Pixels = pixels, MipOffsets = [0, 256, 320, 336] };
    }
}
