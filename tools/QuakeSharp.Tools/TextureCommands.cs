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
    /// Every texture a pack can replace, as PNG under its pack name (decision 68): the BSPs' textures (of those
    /// sharing a name, the one most BSPs have under textures/, each other under textures/&lt;map&gt;/), the skies with
    /// the front layer's index 0 as alpha, every alias model's skins; with each lit texture its fullbright texels on
    /// black (_glow) where it has any; and textures.txt, what was written and where it is used.
    /// </summary>
    public static int ExportTextures(string baseDir, string outDir, string[] engineArgs)
    {
        Commands.RefuseRepository(outDir);
        FileSystem fs = Commands.Init(baseDir, engineArgs);
        Directory.CreateDirectory(outDir);
        byte[] palette = Commands.Load(fs, "gfx/palette.lmp")[..768];
        var written = new SortedDictionary<string, Entry>(StringComparer.Ordinal);
        foreach (GameTexture t in Scan(fs).Textures)
        {
            WriteTexture(outDir, t.File, t.Name, t.Width, t.Height, t.Pixels, palette, written, t.Users);
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

    /// <summary>
    /// What a pack (a game directory) replaces of the game's textures and what it lacks (with <c>-missing</c>, each
    /// one), and its problems: a map's own texture missing where the shared replacement, made from another texture,
    /// would be drawn; files no texture of the game has; sizes not a whole multiple of the game's; layers without
    /// their texture; player.mdl's skins without both _pants and _shirt (players then drawn with the game's skin,
    /// decision 69); files that do not decode.
    /// </summary>
    public static int CheckPack(string baseDir, string packDir, string[] engineArgs)
    {
        bool listMissing = engineArgs.Contains("-missing");
        FileSystem fs = Commands.Init(baseDir, [.. engineArgs.Where(a => a != "-missing")]);
        (List<GameTexture> textures, HashSet<string> known) = Scan(fs);
        var pack = new EnhancedTexturePack([packDir]);
        var problems = new List<string>();
        var missing = new List<string>();
        var images = new Dictionary<string, EnhancedImage?>(StringComparer.Ordinal);

        EnhancedImage? Image(string file)
        {
            if (!images.TryGetValue(file, out EnhancedImage? image))
            {
                try
                {
                    image = EnhancedImage.Load(file);
                }
                catch (Exception e) when (e is InvalidDataException or IOException)
                {
                    problems.Add($"does not decode: {Rel(packDir, file)} ({e.Message})");
                }

                images[file] = image;
            }

            return image;
        }

        int replaced = 0;
        foreach (GameTexture t in textures)
        {
            string? file = pack.Find(t.File);
            string shared = EnhancedPackNames.SharedTexture(t.Name);
            if (file == null && t.File.StartsWith("textures/", StringComparison.Ordinal) && t.File != shared && pack.Find(shared) != null)
            {
                problems.Add($"{t.File} missing: {t.Users[0]} would draw {shared}, made from another texture");
            }

            if (file == null)
            {
                missing.Add(t.File);
                continue;
            }

            replaced++;
            if (Image(file) is EnhancedImage image
                && (image.Width % t.Width != 0 || image.Height % t.Height != 0 || image.Width / t.Width != image.Height / t.Height))
            {
                problems.Add($"{t.File}: {image.Width}x{image.Height} is not a whole multiple of the game's {t.Width}x{t.Height}");
            }

            if (t.File.StartsWith("progs/player.mdl_", StringComparison.Ordinal)
                && (pack.Find(t.File + EnhancedPackNames.Pants) == null || pack.Find(t.File + EnhancedPackNames.Shirt) == null))
            {
                problems.Add($"{t.File}: without both {EnhancedPackNames.Pants} and {EnhancedPackNames.Shirt}, players are drawn with the game's skin");
            }
        }

        string[] layers = [EnhancedPackNames.Glow, EnhancedPackNames.Luma, EnhancedPackNames.Norm, EnhancedPackNames.Gloss, EnhancedPackNames.Pants, EnhancedPackNames.Shirt];
        foreach ((string name, string file) in pack.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            string? layer = layers.FirstOrDefault(l => name.EndsWith(l, StringComparison.Ordinal));
            string texture = layer == null ? name : name[..^layer.Length];
            if (!known.Contains(texture))
            {
                problems.Add($"not a texture of the game: {Rel(packDir, file)}");
            }
            else if (layer != null && pack.Find(texture) == null)
            {
                problems.Add($"{Rel(packDir, file)}: a layer without its texture {texture}");
            }

            Image(file);
        }

        foreach (string problem in problems)
        {
            Console.WriteLine(problem);
        }

        if (listMissing)
        {
            foreach (string name in missing)
            {
                Console.WriteLine($"missing: {name}");
            }
        }

        string more = listMissing ? "" : " (-missing lists them)";
        Console.WriteLine($"{pack.Count} files; replaces {replaced} of the game's {textures.Count} textures, {missing.Count} missing{more}; {problems.Count} problems");
        return 0;
    }

    private static string Rel(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    /// <summary>A texture of the game under its pack name: <see cref="Name"/> is the BSP's texture name, or the
    /// skin's pack name.</summary>
    private sealed record GameTexture(string File, string Name, int Width, int Height, byte[] Pixels, string[] Users);

    // The game's textures as a pack names them (of the BSPs' textures sharing a name, the one most BSPs have under
    // textures/, each other under textures/<map>/ of every BSP that has it; every alias skin), and every name a pack
    // may give them, in lower case (textures/<name> and textures/<map>/<name> for each BSP with the name; the skins)
    private static (List<GameTexture> Textures, HashSet<string> Known) Scan(FileSystem fs)
    {
        List<string> names = [.. fs.ListFiles()];
        var textures = new List<GameTexture>();
        var known = new HashSet<string>(StringComparer.Ordinal);

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
                known.UnionWith(EnhancedPackNames.WorldTexture(map, name).Select(n => n.ToLowerInvariant()));
            }
        }

        foreach ((string name, List<Variant> found) in variants)
        {
            Variant shared = found.MaxBy(v => v.Maps.Count)!;
            foreach (Variant v in found)
            {
                if (v == shared)
                {
                    textures.Add(new GameTexture(EnhancedPackNames.SharedTexture(name), name, v.Width, v.Height, v.Pixels, [.. v.Maps]));
                    continue;
                }

                textures.AddRange(v.Maps.Select(m => new GameTexture(EnhancedPackNames.MapTexture(m, name), name, v.Width, v.Height, v.Pixels, [m])));
            }
        }

        foreach (string model in names.Where(n => n.EndsWith(".mdl", StringComparison.Ordinal)))
        {
            foreach ((int skin, int frame, int width, int height, byte[] pixels) in Skins(Commands.Load(fs, model)))
            {
                string file = EnhancedPackNames.Skin(model, skin, frame);
                textures.Add(new GameTexture(file, file, width, height, pixels, [model]));
                known.Add(file.ToLowerInvariant());
            }
        }

        return (textures, known);
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
