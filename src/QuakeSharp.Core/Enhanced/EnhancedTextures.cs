namespace QuakeSharp.Core.Enhanced;

/// <summary>The enhanced mode's texture colours (design.md §11.6): what the GPU filters in true colour.</summary>
public static class EnhancedTextures
{
    /// <summary>The first fullbright colour (vid.fullbright: the palette's last 32 are never lit).</summary>
    public const int Fullbright = 256 - 32;

    /// <summary>
    /// A BSP texture's colours through the base palette as RGBA (alpha: 255 for a fullbright texel, 0 for a lit one),
    /// level by level: <paramref name="fullChain"/>, a chain halving down to 1x1 from the full-size texels, each texel
    /// the average of the four below it (decision 62's default); otherwise the BSP's own four mip levels.
    /// </summary>
    public static List<(int Width, int Height, byte[] Rgba)> ColorMips(EnhancedTexture texture, byte[] basePalette, bool fullChain)
    {
        var levels = new List<(int, int, byte[])>();
        if (!fullChain)
        {
            for (int m = 0; m < 4; m++)
            {
                int w = Math.Max(1, texture.Width >> m), h = Math.Max(1, texture.Height >> m);
                levels.Add((w, h, Colors(texture.Pixels.AsSpan(texture.MipOffsets[m], w * h), basePalette)));
            }

            return levels;
        }

        int width = texture.Width, height = texture.Height;
        byte[] rgba = Colors(texture.Pixels.AsSpan(0, width * height), basePalette);
        levels.Add((width, height, rgba));
        while (width > 1 || height > 1)
        {
            int nw = Math.Max(1, width / 2), nh = Math.Max(1, height / 2);
            byte[] next = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
            {
                for (int x = 0; x < nw; x++)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        int x0 = Math.Min(2 * x, width - 1), x1 = Math.Min((2 * x) + 1, width - 1);
                        int y0 = Math.Min(2 * y, height - 1), y1 = Math.Min((2 * y) + 1, height - 1);
                        int sum = rgba[(((y0 * width) + x0) * 4) + c] + rgba[(((y0 * width) + x1) * 4) + c]
                            + rgba[(((y1 * width) + x0) * 4) + c] + rgba[(((y1 * width) + x1) * 4) + c];
                        next[(((y * nw) + x) * 4) + c] = (byte)((sum + 2) / 4);
                    }
                }
            }

            levels.Add((nw, nh, next));
            (width, height, rgba) = (nw, nh, next);
        }

        return levels;
    }

    private static byte[] Colors(ReadOnlySpan<byte> indices, byte[] basePalette)
    {
        byte[] rgba = new byte[indices.Length * 4];
        for (int i = 0; i < indices.Length; i++)
        {
            int p = indices[i];
            if (basePalette.Length >= 768)
            {
                rgba[i * 4] = basePalette[p * 3];
                rgba[(i * 4) + 1] = basePalette[(p * 3) + 1];
                rgba[(i * 4) + 2] = basePalette[(p * 3) + 2];
            }

            rgba[(i * 4) + 3] = (byte)(p >= Fullbright ? 255 : 0);
        }

        return rgba;
    }
}
