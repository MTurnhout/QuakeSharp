namespace QuakeSharp.Core.Enhanced;

/// <summary>
/// A texture pack's replacements made ready for the GPU (design.md §11.7, decisions 69 and 70): RGBA as world.slang
/// and alias.slang read a texture's colours (the fullbright weight in alpha; a sky's front layer's transparency in
/// alpha) with a full mip chain. Read and decoded off the game thread (the renderer's loader), never through the game's
/// files.
/// </summary>
public static class EnhancedReplacements
{
    /// <summary>
    /// A world texture's replacement for the BSP <paramref name="map"/> (its base name): null if the pack has none or
    /// it does not decode. A lit texture's fullbright weight is as <see cref="Fullbright"/> gives it. A sky keeps its
    /// front layer's alpha, its back layer opaque. Only level 0 for a sky (its shader reads texels).
    /// </summary>
    public static List<(int Width, int Height, byte[] Rgba)>? World(EnhancedTexturePack pack, string map, EnhancedTexture texture)
    {
        if (pack.FindWorld(map, texture.Name) is not (string name, string file) || Load(file) is not EnhancedImage image)
        {
            return null;
        }

        int w = image.Width, h = image.Height;
        byte[] rgba = (byte[])image.Rgba.Clone();
        if (EnhancedPackNames.IsSky(texture.Name))
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = w / 2; x < w; x++)
                {
                    rgba[(((y * w) + x) * 4) + 3] = 255;
                }
            }

            return [(w, h, rgba)];
        }

        EnhancedImage? glow = EnhancedPackNames.IsTurbulent(texture.Name) || pack.FindLayer(name, EnhancedPackNames.Glow) is not string glowFile
            ? null
            : Load(glowFile);
        Fullbright(rgba, w, h, glow, texture.Pixels, texture.Width, texture.Height);
        return EnhancedTextures.Halve(rgba, w, h);
    }

    /// <summary>An alias skin's layers in a pack (its colours, and its _glow, _shirt and _pants where it has them):
    /// null if the pack has no colours for it or they do not decode.</summary>
    public static EnhancedSkinLayers? SkinLayers(EnhancedTexturePack pack, string model, int skin, int frame)
    {
        string name = EnhancedPackNames.Skin(model, skin, frame);
        if (pack.Find(name) is not string file || Load(file) is not EnhancedImage colors)
        {
            return null;
        }

        EnhancedImage? Layer(string layer) => pack.FindLayer(name, layer) is string f ? Load(f) : null;
        return new EnhancedSkinLayers
        {
            Colors = colors,
            Glow = Layer(EnhancedPackNames.Glow),
            Shirt = Layer(EnhancedPackNames.Shirt),
            Pants = Layer(EnhancedPackNames.Pants),
        };
    }

    /// <summary>
    /// An alias skin's replacement as an entity shows it (decision 69): the colours, plus the shirt's and the pants'
    /// greyscale layers as colours of the player's rows <paramref name="top"/> and <paramref name="bottom"/> (the rows'
    /// first indices; -1 not translated, the skin's own ranges then) as <see cref="Ramp"/> finds them, the fullbright
    /// weight as <see cref="Fullbright"/> gives it. Null
    /// for an entity drawn with a colour translation when the pack lacks either layer: it is drawn with the game's skin.
    /// </summary>
    public static List<(int Width, int Height, byte[] Rgba)>? Skin(EnhancedSkinLayers layers, byte[] gameSkin, int gameWidth,
        int gameHeight, byte[] basePalette, int top, int bottom)
    {
        if ((top >= 0 || bottom >= 0) && (layers.Shirt == null || layers.Pants == null))
        {
            return null;
        }

        EnhancedImage colors = layers.Colors;
        int w = colors.Width, h = colors.Height;
        byte[] rgba = (byte[])colors.Rgba.Clone();
        foreach ((EnhancedImage? mask, int range, int row) in (ReadOnlySpan<(EnhancedImage?, int, int)>)
            [(layers.Shirt, EnhancedPackNames.ShirtRange, top), (layers.Pants, EnhancedPackNames.PantsRange, bottom)])
        {
            if (mask == null)
            {
                continue;
            }

            byte[][] ramp = Ramp(basePalette, range, row);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = ((y * w) + x) * 4;
                    int m = (((y * mask.Height / h) * mask.Width) + (x * mask.Width / w)) * 4;
                    byte[] color = ramp[(mask.Rgba[m] + mask.Rgba[m + 1] + mask.Rgba[m + 2]) / 3];
                    for (int c = 0; c < 3; c++)
                    {
                        rgba[i + c] = (byte)Math.Min(255, rgba[i + c] + color[c]);
                    }
                }
            }
        }

        Fullbright(rgba, w, h, layers.Glow, gameSkin, gameWidth, gameHeight);
        return EnhancedTextures.Halve(rgba, w, h);
    }

    /// <summary>
    /// A shirt's or pants' layer's grey (0-255) as a colour: the grey is a texel's brightness against the range's
    /// brightest entry (palette 31, 111; the export's masks are so), so it finds a place on the range's ramp of the base
    /// palette, and the colour is the player's row's at that place (CL_NewTranslation's index), between two entries
    /// where it falls between them; darker than the ramp's darkest entry, that entry darkened to black (a layer's
    /// edges). So the colours keep the ramp's hues, which change along it (the pants' from brown to yellow), as the
    /// game's do; DarkPlaces multiplies the grey with one colour instead.
    /// </summary>
    private static byte[][] Ramp(byte[] palette, int range, int row)
    {
        double Brightness(int i) => (0.299 * palette[i * 3]) + (0.587 * palette[(i * 3) + 1]) + (0.114 * palette[(i * 3) + 2]);
        double reference = Math.Max(Brightness(range + 15), 1);
        double[] at = [.. Enumerable.Range(0, 16).Select(j => 255 * Brightness(range + j) / reference)];
        double[] Color(int j) => [.. Enumerable.Range(0, 3).Select(c => (double)palette[(Translate(range + j, range, row) * 3) + c])];
        int darkest = Array.IndexOf(at, at.Min()), brightest = Array.IndexOf(at, at.Max());
        var ramp = new byte[256][];
        for (int g = 0; g < 256; g++)
        {
            double[] color = Color(brightest);
            if (g <= at[darkest])
            {
                color = [.. Color(darkest).Select(v => at[darkest] > 0 ? v * g / at[darkest] : 0)];
            }
            else
            {
                for (int j = 0; j < 15; j++)
                {
                    double lo = Math.Min(at[j], at[j + 1]), hi = Math.Max(at[j], at[j + 1]);
                    if (g >= lo && g <= hi && hi > lo)
                    {
                        double f = (g - at[j]) / (at[j + 1] - at[j]);
                        double[] a = Color(j), b = Color(j + 1);
                        color = [.. Enumerable.Range(0, 3).Select(c => a[c] + ((b[c] - a[c]) * f))];
                        break;
                    }
                }
            }

            ramp[g] = [.. color.Select(v => (byte)Math.Clamp(Math.Round(v), 0, 255))];
        }

        return ramp;
    }

    /// <summary>cl_parse.c: CL_NewTranslation: where a range's index goes for a player's row (the row's first index;
    /// rows from 128 up reversed; -1 not translated).</summary>
    public static int Translate(int index, int range, int row) =>
        row < 0 ? index : row < 128 ? row + (index - range) : row + 15 - (index - range);

    /// <summary>
    /// The fullbright weight in alpha (decision 70): from a _glow (or _luma) where the pack has one, the glow's
    /// brightest channel, the colour the replacement's dimmed by it plus the glow (a glowing texel is the glow's colour,
    /// unlit); otherwise the game's fullbright texels (palette 224-255) scaled to the replacement's size nearest.
    /// </summary>
    private static void Fullbright(byte[] rgba, int w, int h, EnhancedImage? glow, byte[] gamePixels, int gameWidth, int gameHeight)
    {
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = ((y * w) + x) * 4;
                if (glow != null)
                {
                    int g = (((y * glow.Height / h) * glow.Width) + (x * glow.Width / w)) * 4;
                    int a = Math.Max(glow.Rgba[g], Math.Max(glow.Rgba[g + 1], glow.Rgba[g + 2]));
                    for (int c = 0; c < 3; c++)
                    {
                        rgba[i + c] = (byte)Math.Min(255, (rgba[i + c] * (255 - a) / 255) + glow.Rgba[g + c]);
                    }

                    rgba[i + 3] = (byte)a;
                }
                else
                {
                    int p = gamePixels[((y * gameHeight / h) * gameWidth) + (x * gameWidth / w)];
                    rgba[i + 3] = (byte)(p >= EnhancedTextures.Fullbright ? 255 : 0);
                }
            }
        }
    }

    private static EnhancedImage? Load(string file)
    {
        try
        {
            return EnhancedImage.Load(file);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>An alias skin's layers in a texture pack (design.md §11.7): its colours, and its glow, shirt and pants
/// where the pack has them.</summary>
public sealed class EnhancedSkinLayers
{
    public required EnhancedImage Colors { get; init; }

    public EnhancedImage? Glow { get; init; }

    public EnhancedImage? Shirt { get; init; }

    public EnhancedImage? Pants { get; init; }
}
