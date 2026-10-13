namespace QuakeSharp.Core.Enhanced;

/// <summary>
/// A texture pack's replacements made ready for the GPU (design.md §11.7, decisions 69 and 70): RGBA as world.slang
/// reads a texture's colours (the fullbright weight in alpha; a sky's front layer's transparency in alpha) with a full
/// mip chain. Read and decoded off the game thread (the renderer's loader), never through the game's files.
/// </summary>
public static class EnhancedReplacements
{
    /// <summary>
    /// A world texture's replacement for the BSP <paramref name="map"/> (its base name): null if the pack has none or
    /// it does not decode. A lit texture's fullbright weight is its _glow's (or _luma's) where the pack has one: the
    /// glow's brightest channel, the colour the replacement's dimmed by it plus the glow; otherwise the game's
    /// fullbright texels (palette 224-255), scaled to the replacement's size nearest, drawn unlit (decision 70).
    /// A sky keeps its front layer's alpha, its back layer opaque. Only level 0 for a sky (its shader reads texels).
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
                    int p = texture.Pixels[((y * texture.Height / h) * texture.Width) + (x * texture.Width / w)];
                    rgba[i + 3] = (byte)(p >= EnhancedTextures.Fullbright ? 255 : 0);
                }
            }
        }

        return EnhancedTextures.Halve(rgba, w, h);
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
