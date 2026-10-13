using System.Buffers.Binary;
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
    /// black (_glow) where it has any; and textures.txt, what was written and where it is used. With <c>-tiled</c>,
    /// each world texture is written 3x3 times over (a sky's two layers each on their own), so that an upscaler sees
    /// its edges as they repeat on a wall; <see cref="FinishPack"/> crops the middle again.
    /// </summary>
    public static int ExportTextures(string baseDir, string outDir, string[] engineArgs)
    {
        Commands.RefuseRepository(outDir);
        bool tiled = engineArgs.Contains("-tiled");
        FileSystem fs = Commands.Init(baseDir, [.. engineArgs.Where(a => a != "-tiled")]);
        Directory.CreateDirectory(outDir);
        byte[] palette = Commands.Load(fs, "gfx/palette.lmp")[..768];
        var written = new SortedDictionary<string, Entry>(StringComparer.Ordinal);
        foreach (GameTexture t in Scan(fs).Textures)
        {
            if (EnhancedPackNames.PlayerModels.Any(m => t.File.StartsWith(m + "_", StringComparison.Ordinal)))
            {
                WritePlayerSkin(outDir, t, palette, written);
                continue;
            }

            WriteTexture(outDir, t.File, t.Name, t.Width, t.Height, t.Pixels, palette, written, t.Users, tiled);
        }

        var list = new StringBuilder();
        list.Append("# QuakeSharp's texture export (design.md §11.7): file, size, what uses it\n");
        if (tiled)
        {
            list.Append($"{TiledMark}: each world texture 3x3 times over (a sky's two layers each), the game's size\n");
        }
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

    /// <summary>
    /// A pack from an export (<see cref="ExportTextures"/>) and the upscales of its files (the same names under
    /// <paramref name="upscaledDir"/>): each upscale's detail with the game's colour at a coarse scale, as upscalers
    /// darken Quake's dark textures (the upscale, plus the game's texture resized and blurred, minus the upscale blurred;
    /// a Gaussian of 1.5 of the game's texels); the middle of a tiled export cropped again; the alpha the upscale's
    /// where the game's texture has any (a sky's front layer), else opaque. Written as PNG under the pack's names.
    /// </summary>
    public static int FinishPack(string exportDir, string upscaledDir, string packDir)
    {
        Commands.RefuseRepository(packDir);
        string[] lines = File.ReadAllLines(Path.Combine(exportDir, "textures.txt"));
        bool tiled = lines.Any(l => l.StartsWith(TiledMark, StringComparison.Ordinal));
        var skipped = new List<string>();
        int written = 0;
        foreach (string line in lines.Where(l => l.Length > 0 && l[0] != '#'))
        {
            string file = line[..line.IndexOf(' ')];
            string upscaled = Path.Combine(upscaledDir, file);
            if (!File.Exists(upscaled))
            {
                skipped.Add($"{file}: no upscale");
                continue;
            }

            EnhancedImage game = EnhancedImage.Load(Path.Combine(exportDir, file));
            EnhancedImage big = EnhancedImage.Load(upscaled);
            if (big.Width % game.Width != 0 || big.Height % game.Height != 0 || big.Width / game.Width != big.Height / game.Height)
            {
                skipped.Add($"{file}: {big.Width}x{big.Height} is not a whole multiple of {game.Width}x{game.Height}");
                continue;
            }

            byte[] rgba = RestoreColour(game, big, 1.5 * (big.Width / game.Width));
            (int w, int h) = (big.Width, big.Height);
            if (tiled && file.StartsWith("textures/", StringComparison.Ordinal))
            {
                (rgba, w, h) = Middle(rgba, w, h, EnhancedPackNames.IsSky(Path.GetFileNameWithoutExtension(file)) ? 2 : 1);
            }

            string path = Path.Combine(packDir, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            PngWriter.WriteRgba(path, w, h, rgba);
            written++;
        }

        foreach (string skip in skipped)
        {
            Console.WriteLine($"skipped {skip}");
        }

        Console.WriteLine($"wrote {written} textures to {packDir}; {skipped.Count} skipped");
        return 0;
    }

    /// <summary>
    /// Material maps for a pack's world textures, derived from their colours (design.md §11.7, step 5): beside each
    /// texture under textures/ (not the sky's or turbulent ones, which are unlit, nor the layers) a _norm, its
    /// brightness as height (<see cref="EnhancedMaterials.Normals"/>, wrapping as the texture repeats), and a _gloss
    /// (<see cref="EnhancedMaterials.Gloss"/>); a layer the pack has already is kept unless <c>-force</c>.
    /// </summary>
    public static int MakeMaterials(string packDir, string[] options)
    {
        Commands.RefuseRepository(packDir);
        double strength = 12, sigma = 1, gloss = 3;
        bool force = false;
        for (int i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "-strength" when i + 1 < options.Length:
                    strength = double.Parse(options[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "-smooth" when i + 1 < options.Length:
                    sigma = double.Parse(options[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "-gloss" when i + 1 < options.Length:
                    gloss = double.Parse(options[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "-force":
                    force = true;
                    break;
                default:
                    throw new Core.QuakeException($"texture-pack-materials: unknown option {options[i]}");
            }
        }

        var pack = new EnhancedTexturePack([packDir]);
        string[] layers = [EnhancedPackNames.Glow, EnhancedPackNames.Luma, EnhancedPackNames.Norm, EnhancedPackNames.Gloss,
            EnhancedPackNames.Pants, EnhancedPackNames.Shirt];
        var textures = pack.Files
            .Where(f => f.Key.StartsWith("textures/", StringComparison.Ordinal))
            .Where(f => !layers.Any(l => f.Key.EndsWith(l, StringComparison.Ordinal)))
            .Where(f => !EnhancedPackNames.IsSky(f.Key[(f.Key.LastIndexOf('/') + 1)..]) && f.Key[f.Key.LastIndexOf('/') + 1] != '#')
            .ToList();
        int written = 0, kept = 0;
        var failed = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(textures, texture =>
        {
            string stem = texture.Value[..^Path.GetExtension(texture.Value).Length];
            EnhancedImage image;
            try
            {
                image = EnhancedImage.Load(texture.Value);
            }
            catch (InvalidDataException e)
            {
                failed.Add($"{texture.Key}: {e.Message}");
                return;
            }

            foreach ((string layer, Func<byte[]> make) in (ReadOnlySpan<(string, Func<byte[]>)>)
                [(EnhancedPackNames.Norm, () => EnhancedMaterials.Normals(image.Rgba, image.Width, image.Height, strength, sigma, wrap: true)),
                 (EnhancedPackNames.Gloss, () => EnhancedMaterials.Gloss(image.Rgba, image.Width, image.Height, gloss))])
            {
                if (!force && pack.FindLayer(texture.Key, layer) != null)
                {
                    Interlocked.Increment(ref kept);
                    continue;
                }

                PngWriter.WriteRgba(stem + layer + ".png", image.Width, image.Height, make());
                Interlocked.Increment(ref written);
            }
        });

        foreach (string f in failed.Order(StringComparer.Ordinal))
        {
            Console.WriteLine($"skipped {f}");
        }

        Console.WriteLine($"wrote {written} maps for {textures.Count} textures in {packDir}; {kept} kept, {failed.Count} skipped");
        return 0;
    }

    // The upscale plus the game's texture (resized bilinearly to its size) blurred, minus the upscale blurred
    private static byte[] RestoreColour(EnhancedImage game, EnhancedImage big, double sigma)
    {
        int w = big.Width, h = big.Height, n = w * h;
        bool alpha = Enumerable.Range(0, game.Width * game.Height).Any(i => game.Rgba[(i * 4) + 3] != 255);
        var result = new byte[n * 4];
        for (int c = 0; c < 3; c++)
        {
            float[] up = new float[n], resized = new float[n];
            for (int i = 0; i < n; i++)
            {
                up[i] = big.Rgba[(i * 4) + c];
            }

            Parallel.For(0, h, y =>
            {
                double gy = Math.Clamp(((y + 0.5) * game.Height / h) - 0.5, 0, game.Height - 1);
                int y0 = (int)gy, y1 = Math.Min(y0 + 1, game.Height - 1);
                double fy = gy - y0;
                for (int x = 0; x < w; x++)
                {
                    double gx = Math.Clamp(((x + 0.5) * game.Width / w) - 0.5, 0, game.Width - 1);
                    int x0 = (int)gx, x1 = Math.Min(x0 + 1, game.Width - 1);
                    double fx = gx - x0;
                    double top = (Texel(game, x0, y0, c) * (1 - fx)) + (Texel(game, x1, y0, c) * fx);
                    double bottom = (Texel(game, x0, y1, c) * (1 - fx)) + (Texel(game, x1, y1, c) * fx);
                    resized[(y * w) + x] = (float)((top * (1 - fy)) + (bottom * fy));
                }
            });

            float[] gameBlur = Blur(resized, w, h, sigma), upBlur = Blur(up, w, h, sigma);
            for (int i = 0; i < n; i++)
            {
                result[(i * 4) + c] = (byte)Math.Clamp(Math.Round(up[i] + gameBlur[i] - upBlur[i]), 0, 255);
            }
        }

        for (int i = 0; i < n; i++)
        {
            result[(i * 4) + 3] = alpha ? big.Rgba[(i * 4) + 3] : (byte)255;
        }

        return result;
    }

    private static byte Texel(EnhancedImage image, int x, int y, int c) => image.Rgba[(((y * image.Width) + x) * 4) + c];

    // A separable Gaussian, the edges clamped
    private static float[] Blur(float[] source, int w, int h, double sigma)
    {
        int radius = (int)Math.Ceiling(3 * sigma);
        var kernel = new float[(2 * radius) + 1];
        double sum = 0;
        for (int i = -radius; i <= radius; i++)
        {
            sum += kernel[i + radius] = (float)Math.Exp(-(i * i) / (2 * sigma * sigma));
        }

        for (int i = 0; i < kernel.Length; i++)
        {
            kernel[i] = (float)(kernel[i] / sum);
        }

        var across = new float[w * h];
        var result = new float[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float v = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    v += kernel[k + radius] * source[(y * w) + Math.Clamp(x + k, 0, w - 1)];
                }

                across[(y * w) + x] = v;
            }
        });
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float v = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    v += kernel[k + radius] * across[(Math.Clamp(y + k, 0, h - 1) * w) + x];
                }

                result[(y * w) + x] = v;
            }
        });
        return result;
    }

    // The middle of an image 3x3 times over; with two layers side by side (a sky), each layer's middle
    private static (byte[] Rgba, int Width, int Height) Middle(byte[] rgba, int w, int h, int layers)
    {
        int lw = w / layers / 3, mh = h / 3;
        var middle = new byte[lw * layers * mh * 4];
        for (int layer = 0; layer < layers; layer++)
        {
            for (int y = 0; y < mh; y++)
            {
                int from = (((y + mh) * w) + (layer * (w / layers)) + lw) * 4;
                rgba.AsSpan(from, lw * 4).CopyTo(middle.AsSpan(((y * lw * layers) + (layer * lw)) * 4));
            }
        }

        return (middle, lw * layers, mh);
    }

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

    private const string TiledMark = "# tiled";

    private static void WriteTexture(string outDir, string file, string name, int width, int height, byte[] pixels, byte[] palette, SortedDictionary<string, Entry> written, string[] users, bool tiled)
    {
        string path = Path.Combine(outDir, file.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        written[file] = new Entry(width, height, users);
        bool sky = EnhancedPackNames.IsSky(Path.GetFileName(name));
        bool tile = tiled && file.StartsWith("textures/", StringComparison.Ordinal);
        int tw = tile ? width * 3 : width, th = tile ? height * 3 : height;
        T[] Tiled<T>(T[] data) => tile ? Tile(data, width, height, sky ? 2 : 1) : data;

        if (sky)
        {
            // r_sky.c: the left half is the front layer, its index 0 transparent; the right half behind it. The clear
            // texels take their opaque neighbours' colours (an upscaler would otherwise draw their black into the
            // clouds' edges)
            uint[] texels = SkyTexels(pixels, width, height, palette);
            uint[] shown = Tiled(texels);
            byte[] rgba = new byte[shown.Length * 4];
            for (int i = 0; i < shown.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(rgba.AsSpan(i * 4), shown[i]);
            }

            PngWriter.WriteRgba(path + ".png", tw, th, rgba);
            return;
        }

        PngWriter.WriteIndexed(path + ".png", tw, th, Tiled(pixels), palette);

        // the fullbright texels (palette 224-255, never lit) on black (index 0); turbulent surfaces are all unlit
        if (EnhancedPackNames.IsTurbulent(name) || !pixels.Any(p => p >= EnhancedTextures.Fullbright))
        {
            return;
        }

        byte[] glow = [.. pixels.Select(p => p >= EnhancedTextures.Fullbright ? p : (byte)0)];
        PngWriter.WriteIndexed(path + EnhancedPackNames.Glow + ".png", tw, th, Tiled(glow), palette);
        written[file + EnhancedPackNames.Glow] = new Entry(width, height, users);
    }

    // A skin of a model players show (decision 69, DarkPlaces' layers): its shirt and pants texels (cl_parse.c's ranges)
    // black, and each range a greyscale layer, _shirt and _pants, a texel's grey its colour's brightness against the
    // range's brightest (palette 31, 111), which the renderer tints with the player's colour there
    private static void WritePlayerSkin(string outDir, GameTexture t, byte[] palette, SortedDictionary<string, Entry> written)
    {
        (int Range, string Layer)[] ranges = [(EnhancedPackNames.ShirtRange, EnhancedPackNames.Shirt), (EnhancedPackNames.PantsRange, EnhancedPackNames.Pants)];
        bool InRange(byte p, int range) => p >= range && p < range + 16;
        if (!t.Pixels.Any(p => ranges.Any(r => InRange(p, r.Range))))
        {
            WriteTexture(outDir, t.File, t.Name, t.Width, t.Height, t.Pixels, palette, written, t.Users, tiled: false);
            return;
        }

        byte[] rest = [.. t.Pixels.Select(p => ranges.Any(r => InRange(p, r.Range)) ? (byte)0 : p)];
        WriteTexture(outDir, t.File, t.Name, t.Width, t.Height, rest, palette, written, t.Users, tiled: false);
        double Brightness(int i) => (0.299 * palette[i * 3]) + (0.587 * palette[(i * 3) + 1]) + (0.114 * palette[(i * 3) + 2]);
        foreach ((int range, string layer) in ranges)
        {
            byte[] rgba = new byte[t.Pixels.Length * 4];
            for (int i = 0; i < t.Pixels.Length; i++)
            {
                byte grey = InRange(t.Pixels[i], range)
                    ? (byte)Math.Min(255, Math.Round(255 * Brightness(t.Pixels[i]) / Brightness(range + 15)))
                    : (byte)0;
                (rgba[i * 4], rgba[(i * 4) + 1], rgba[(i * 4) + 2], rgba[(i * 4) + 3]) = (grey, grey, grey, 255);
            }

            string path = Path.Combine(outDir, (t.File + layer).Replace('/', Path.DirectorySeparatorChar));
            PngWriter.WriteRgba(path + ".png", t.Width, t.Height, rgba);
            written[t.File + layer] = new Entry(t.Width, t.Height, t.Users);
        }
    }

    // A sky's texels as RGBA words (red in the low byte): the front layer's index 0 clear, its clear texels the average
    // of their opaque neighbours' colours, spread outwards a ring at a time (wrapping within the layer, as it tiles)
    private static uint[] SkyTexels(byte[] pixels, int width, int height, byte[] palette)
    {
        int lw = width / 2;
        var texels = new uint[width * height];
        var known = new bool[width * height];
        for (int i = 0; i < texels.Length; i++)
        {
            int p = pixels[i] * 3;
            bool clear = i % width < lw && pixels[i] == 0;
            texels[i] = palette[p] | ((uint)palette[p + 1] << 8) | ((uint)palette[p + 2] << 16) | (clear ? 0u : 0xFF000000u);
            known[i] = !clear;
        }

        for (bool spread = true; spread;)
        {
            spread = false;
            bool[] before = (bool[])known.Clone();
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < lw; x++)
                {
                    int i = (y * width) + x;
                    if (before[i])
                    {
                        continue;
                    }

                    int r = 0, g = 0, b = 0, n = 0;
                    foreach ((int dx, int dy) in (ReadOnlySpan<(int, int)>)[(-1, 0), (1, 0), (0, -1), (0, 1)])
                    {
                        int j = ((((y + dy + height) % height) * width) + ((x + dx + lw) % lw));
                        if (before[j])
                        {
                            r += (int)(texels[j] & 0xFF);
                            g += (int)((texels[j] >> 8) & 0xFF);
                            b += (int)((texels[j] >> 16) & 0xFF);
                            n++;
                        }
                    }

                    if (n > 0)
                    {
                        texels[i] = (uint)(r / n) | ((uint)(g / n) << 8) | ((uint)(b / n) << 16);
                        known[i] = true;
                        spread = true;
                    }
                }
            }
        }

        return texels;
    }

    // An image 3x3 times over; with two layers side by side (a sky), each layer 3x3 times over on its own
    private static T[] Tile<T>(T[] data, int width, int height, int layers)
    {
        int lw = width / layers;
        var tiled = new T[width * 3 * height * 3];
        for (int y = 0; y < height * 3; y++)
        {
            for (int x = 0; x < width * 3; x++)
            {
                tiled[(y * width * 3) + x] = data[((y % height) * width) + (x / (lw * 3) * lw) + (x % lw)];
            }
        }

        return tiled;
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
