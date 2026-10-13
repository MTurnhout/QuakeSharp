namespace QuakeSharp.Core.Enhanced;

/// <summary>
/// Material maps derived from a texture's colours (design.md §11.7, step 5): what the tools' texture-pack-materials
/// writes for a pack whose upscaler made colours only. A <see cref="EnhancedPackNames.Norm"/> map from the colours'
/// brightness taken as height, a <see cref="EnhancedPackNames.Gloss"/> map from their brightness and want of colour.
/// Normals are in tangent space as world.slang reads them: x along the image's right, y along its up (OpenGL's way),
/// z out of the surface, each as 0-255 for -1 to 1.
/// </summary>
public static class EnhancedMaterials
{
    /// <summary>
    /// A normal map of <paramref name="rgba"/>: the brightness (Rec. 601's weights) as height, smoothed by a Gaussian of
    /// <paramref name="sigma"/> texels (0: none), its slope by a Sobel filter, steepened by <paramref name="strength"/>
    /// (the height's 0-1 per texel). <paramref name="wrap"/>: the texture repeats (a world texture), so the edges read
    /// the other side; otherwise they are clamped. Alpha 255.
    /// </summary>
    public static byte[] Normals(byte[] rgba, int width, int height, double strength, double sigma, bool wrap)
    {
        float[] h = new float[width * height];
        for (int i = 0; i < h.Length; i++)
        {
            h[i] = (float)Brightness(rgba, i);
        }

        if (sigma > 0)
        {
            h = Blur(h, width, height, sigma, wrap);
        }

        int At(int x, int y)
        {
            x = wrap ? ((x % width) + width) % width : Math.Clamp(x, 0, width - 1);
            y = wrap ? ((y % height) + height) % height : Math.Clamp(y, 0, height - 1);
            return (y * width) + x;
        }

        byte[] normals = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float a = h[At(x - 1, y - 1)], b = h[At(x, y - 1)], c = h[At(x + 1, y - 1)];
                float d = h[At(x - 1, y)], f = h[At(x + 1, y)];
                float g = h[At(x - 1, y + 1)], k = h[At(x, y + 1)], l = h[At(x + 1, y + 1)];
                // the slope per texel rightwards and downwards (Sobel's weights add to 8 for a slope of 1)
                double dx = ((c + (2 * f) + l) - (a + (2 * d) + g)) / 8.0;
                double dy = ((g + (2 * k) + l) - (a + (2 * b) + c)) / 8.0;
                // a surface rising rightwards faces left; rising downwards (the image's y), it faces up
                double nx = -dx * strength, ny = dy * strength, nz = 1;
                double length = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
                int o = ((y * width) + x) * 4;
                normals[o] = Encode(nx / length);
                normals[o + 1] = Encode(ny / length);
                normals[o + 2] = Encode(nz / length);
                normals[o + 3] = 255;
            }
        }

        return normals;
    }

    /// <summary>
    /// A gloss map of <paramref name="rgba"/> (grey, alpha 255): bright texels without much colour shine (metal,
    /// panels, wet stone), dark or coloured ones less (wood, rust, earth): <paramref name="scale"/> times the brightness
    /// times one less the saturation (HSV's), squared, up to 255.
    /// </summary>
    public static byte[] Gloss(byte[] rgba, int width, int height, double scale)
    {
        byte[] gloss = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            int r = rgba[i * 4], g = rgba[(i * 4) + 1], b = rgba[(i * 4) + 2];
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double saturation = max == 0 ? 0 : (max - min) / (double)max;
            double grey = scale * Brightness(rgba, i) * (1 - saturation) * (1 - saturation);
            byte v = (byte)Math.Clamp(Math.Round(grey * 255), 0, 255);
            gloss[i * 4] = gloss[(i * 4) + 1] = gloss[(i * 4) + 2] = v;
            gloss[(i * 4) + 3] = 255;
        }

        return gloss;
    }

    /// <summary>
    /// A world texture's material for world.slang (decision 71): RGB its pack's _norm, alpha its _gloss's brightest
    /// channel, at the normals' size (the gloss's texels scaled to it, nearest), with a full mip chain; a flat normal
    /// where the pack has only a _gloss, no gloss where it has only a _norm. Null where it has neither, where the
    /// texture itself is not replaced (a layer needs its texture), for the sky and turbulent textures (unlit), or where
    /// the files do not decode.
    /// </summary>
    public static List<(int Width, int Height, byte[] Rgba)>? World(EnhancedTexturePack pack, string map, EnhancedTexture texture)
    {
        if (EnhancedPackNames.IsSky(texture.Name) || EnhancedPackNames.IsTurbulent(texture.Name)
            || pack.FindWorld(map, texture.Name) is not (string name, _))
        {
            return null;
        }

        EnhancedImage? norm = pack.FindLayer(name, EnhancedPackNames.Norm) is string n ? EnhancedReplacements.Load(n) : null;
        EnhancedImage? gloss = pack.FindLayer(name, EnhancedPackNames.Gloss) is string g ? EnhancedReplacements.Load(g) : null;
        if (norm == null && gloss == null)
        {
            return null;
        }

        int w = norm?.Width ?? gloss!.Width, h = norm?.Height ?? gloss!.Height;
        byte[] rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = ((y * w) + x) * 4;
                if (norm != null)
                {
                    rgba[o] = norm.Rgba[o];
                    rgba[o + 1] = norm.Rgba[o + 1];
                    rgba[o + 2] = norm.Rgba[o + 2];
                }
                else
                {
                    (rgba[o], rgba[o + 1], rgba[o + 2]) = (128, 128, 255);
                }

                if (gloss != null)
                {
                    int s = (((y * gloss.Height / h) * gloss.Width) + (x * gloss.Width / w)) * 4;
                    rgba[o + 3] = Math.Max(gloss.Rgba[s], Math.Max(gloss.Rgba[s + 1], gloss.Rgba[s + 2]));
                }
            }
        }

        return EnhancedTextures.Halve(rgba, w, h);
    }

    private static double Brightness(byte[] rgba, int i) =>
        ((0.299 * rgba[i * 4]) + (0.587 * rgba[(i * 4) + 1]) + (0.114 * rgba[(i * 4) + 2])) / 255.0;

    private static byte Encode(double v) => (byte)Math.Clamp(Math.Round((v * 127.5) + 127.5), 0, 255);

    // A separable Gaussian, the edges wrapped or clamped
    private static float[] Blur(float[] source, int w, int h, double sigma, bool wrap)
    {
        int radius = (int)Math.Ceiling(3 * sigma);
        var kernel = new float[(2 * radius) + 1];
        float sum = 0;
        for (int i = -radius; i <= radius; i++)
        {
            kernel[i + radius] = (float)Math.Exp(-(i * i) / (2 * sigma * sigma));
            sum += kernel[i + radius];
        }

        for (int i = 0; i < kernel.Length; i++)
        {
            kernel[i] /= sum;
        }

        int Index(int v, int size) => wrap ? ((v % size) + size) % size : Math.Clamp(v, 0, size - 1);
        var across = new float[w * h];
        var result = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float v = 0;
                for (int i = -radius; i <= radius; i++)
                {
                    v += kernel[i + radius] * source[(y * w) + Index(x + i, w)];
                }

                across[(y * w) + x] = v;
            }
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float v = 0;
                for (int i = -radius; i <= radius; i++)
                {
                    v += kernel[i + radius] * across[(Index(y + i, h) * w) + x];
                }

                result[(y * w) + x] = v;
            }
        }

        return result;
    }
}
