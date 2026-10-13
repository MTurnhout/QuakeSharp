using System.Text;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Enhanced;
using QuakeSharp.Core.GameData;
using QuakeSharp.Core.Model;

namespace QuakeSharp.Tools;

/// <summary>Texture packs (design.md §11.7): the game's textures written under a pack's names, for a pack's maker.</summary>
internal static class TextureCommands
{
    /// <summary>
    /// Every texture a pack can replace, as PNG under its pack name (decision 68): the BSPs' textures (once under
    /// textures/ per name, under textures/&lt;map&gt;/ where a BSP's differs from the first seen), the skies with the
    /// front layer's index 0 as alpha, every alias model's skins; with each lit texture its fullbright texels on black
    /// (_glow) where it has any; and textures.txt, what was written and where it is used.
    /// </summary>
    public static int ExportTextures(string baseDir, string outDir, string[] engineArgs)
    {
        Commands.RefuseRepository(outDir);
        FileSystem fs = Commands.Init(baseDir, engineArgs);
        Directory.CreateDirectory(outDir);
        byte[] palette = Commands.Load(fs, "gfx/palette.lmp")[..768];
        List<string> names = [.. fs.ListFiles()];
        var written = new SortedDictionary<string, Entry>(StringComparer.Ordinal);

        // each name's different textures, with the BSPs that have them, in the order first seen
        var variants = new Dictionary<string, List<Variant>>(StringComparer.Ordinal);
        foreach (string bspName in names.Where(n => n.EndsWith(".bsp", StringComparison.Ordinal)))
        {
            string map = Com.FileBase(bspName);
            foreach ((string name, int width, int height, byte[] pixels) in BspTextures(Commands.Load(fs, bspName)))
            {
                List<Variant> seen = variants.TryGetValue(name, out var l) ? l : variants[name] = [];
                Variant? same = seen.Find(v => v.Width == width && v.Height == height && v.Pixels.AsSpan().SequenceEqual(pixels));
                if (same == null)
                {
                    seen.Add(same = new Variant(width, height, pixels));
                }

                same.Maps.Add(map);
            }
        }

        // the one most BSPs have under textures/, every other under textures/<map>/ of each BSP that has it
        foreach ((string name, List<Variant> found) in variants)
        {
            Variant shared = found.MaxBy(v => v.Maps.Count)!;
            foreach (Variant v in found)
            {
                IEnumerable<(string File, string[] Users)> files = v == shared
                    ? [(EnhancedPackNames.SharedTexture(name), [.. v.Maps])]
                    : v.Maps.Select(m => (EnhancedPackNames.MapTexture(m, name), new[] { m }));
                foreach ((string file, string[] users) in files)
                {
                    WriteTexture(outDir, file, name, v.Width, v.Height, v.Pixels, palette, written, users);
                }
            }
        }

        foreach (string model in names.Where(n => n.EndsWith(".mdl", StringComparison.Ordinal)))
        {
            foreach ((int skin, int frame, int width, int height, byte[] pixels) in Skins(Commands.Load(fs, model)))
            {
                string file = EnhancedPackNames.Skin(model, skin, frame);
                WriteTexture(outDir, file, file, width, height, pixels, palette, written, [model]);
            }
        }

        var list = new StringBuilder();
        list.Append("# QuakeSharp's texture export (design.md §11.7): file, size, what uses it\n");
        foreach ((string file, Entry entry) in written)
        {
            list.Append($"{file}.png {entry.Width}x{entry.Height} {string.Join(' ', entry.Users)}\n");
        }

        File.WriteAllText(Path.Combine(outDir, "textures.txt"), list.ToString());
        Console.WriteLine($"wrote {written.Count} textures to {outDir} (textures.txt lists them)");
        return 0;
    }

    private sealed record Entry(int Width, int Height, string[] Users);

    private sealed class Variant(int width, int height, byte[] pixels)
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public byte[] Pixels { get; } = pixels;

        public SortedSet<string> Maps { get; } = new(StringComparer.Ordinal);
    }

    private static void WriteTexture(string outDir, string file, string name, int width, int height, byte[] pixels, byte[] palette, SortedDictionary<string, Entry> written, string[] users)
    {
        string path = Path.Combine(outDir, file.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        written[file] = new Entry(width, height, users);

        if (EnhancedPackNames.IsSky(Path.GetFileName(name)))
        {
            // r_sky.c: the left half is the front layer, its index 0 transparent; the right half behind it
            var opaque = new bool[width * height];
            for (int i = 0; i < opaque.Length; i++)
            {
                opaque[i] = i % width >= width / 2 || pixels[i] != 0;
            }

            PngWriter.WriteIndexed(path + ".png", width, height, pixels, palette, opaque);
            return;
        }

        PngWriter.WriteIndexed(path + ".png", width, height, pixels, palette);

        // the fullbright texels (palette 224-255, never lit) on black (index 0); turbulent surfaces are all unlit
        if (EnhancedPackNames.IsTurbulent(name) || !pixels.Any(p => p >= EnhancedTextures.Fullbright))
        {
            return;
        }

        byte[] glow = [.. pixels.Select(p => p >= EnhancedTextures.Fullbright ? p : (byte)0)];
        PngWriter.WriteIndexed(path + EnhancedPackNames.Glow + ".png", width, height, glow, palette);
        written[file + EnhancedPackNames.Glow] = new Entry(width, height, users);
    }

    // bspfile.h: the textures lump (dmiptexlump_t) and its miptex_t: each texture's name and mip level 0
    private static IEnumerable<(string Name, int Width, int Height, byte[] Pixels)> BspTextures(byte[] bsp)
    {
        if (Commands.I32(bsp, 0) != BspFile.BSPVERSION)
        {
            yield break;
        }

        int lump = Commands.I32(bsp, 4 + (8 * BspFile.LUMP_TEXTURES));
        int nummiptex = Commands.I32(bsp, lump);
        for (int i = 0; i < nummiptex; i++)
        {
            int dataofs = Commands.I32(bsp, lump + 4 + (4 * i));
            if (dataofs == -1)
            {
                continue;
            }

            int mt = lump + dataofs;
            int width = Commands.I32(bsp, mt + 16);
            int height = Commands.I32(bsp, mt + 20);
            int offset0 = Commands.I32(bsp, mt + 24);
            yield return (Commands.CString(bsp.AsSpan(mt, 16)), width, height, bsp.AsSpan(mt + offset0, width * height).ToArray());
        }
    }

    // modelgen.h: mdl_t, then each skin: daliasskintype_t and one picture, or a group of them (frame -1: a single skin)
    private static IEnumerable<(int Skin, int Frame, int Width, int Height, byte[] Pixels)> Skins(byte[] mdl)
    {
        if (Commands.I32(mdl, 0) != ModelGen.IDPOLYHEADER || Commands.I32(mdl, 4) != ModelGen.ALIAS_VERSION)
        {
            yield break;
        }

        int numskins = Commands.I32(mdl, 48);
        int width = Commands.I32(mdl, 52);
        int height = Commands.I32(mdl, 56);
        int size = width * height;
        int p = ModelGen.MdlSize;
        for (int i = 0; i < numskins; i++)
        {
            var type = (aliasskintype_t)Commands.I32(mdl, p);
            p += 4;
            if (type == aliasskintype_t.ALIAS_SKIN_SINGLE)
            {
                yield return (i, -1, width, height, mdl.AsSpan(p, size).ToArray());
                p += size;
                continue;
            }

            int count = Commands.I32(mdl, p);
            p += 4 + (4 * count); // daliasskingroup_t, then the intervals
            for (int j = 0; j < count; j++)
            {
                yield return (i, j, width, height, mdl.AsSpan(p, size).ToArray());
                p += size;
            }
        }
    }
}
