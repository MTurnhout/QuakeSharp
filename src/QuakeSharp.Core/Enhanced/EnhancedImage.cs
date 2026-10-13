using System.Buffers.Binary;
using System.IO.Compression;

namespace QuakeSharp.Core.Enhanced;

/// <summary>
/// A texture pack's image as 8-bit RGBA, top row first (design.md §11.7): PNG and TGA decoded by QuakeSharp's own code
/// (decision 72; PNG's zlib through .NET's <see cref="ZLibStream"/>).
/// </summary>
public sealed class EnhancedImage(int width, int height, byte[] rgba)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public byte[] Rgba { get; } = rgba;

    /// <summary>The most pixels an image may have (8192x8192).</summary>
    public const long MaxPixels = 1L << 26;

    /// <summary>A file by its extension (.png or .tga, any case); <see cref="InvalidDataException"/> if it does not
    /// decode.</summary>
    public static EnhancedImage Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => DecodePng(data),
            ".tga" => DecodeTga(data),
            _ => throw new InvalidDataException($"{path}: not a PNG or TGA file"),
        };
    }

    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>A PNG file: every colour type and bit depth, Adam7 interlacing, tRNS; 16-bit samples by their high
    /// byte.</summary>
    public static EnhancedImage DecodePng(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || !data[..8].SequenceEqual(PngSignature))
        {
            throw new InvalidDataException("not a PNG file");
        }

        int width = 0, height = 0, depth = 0, colorType = -1, interlace = 0;
        byte[]? palette = null, transparency = null;
        using var idat = new MemoryStream();
        int p = 8;
        while (true)
        {
            if (p + 12 > data.Length)
            {
                throw new InvalidDataException("PNG ends before IEND");
            }

            int length = BinaryPrimitives.ReadInt32BigEndian(data[p..]);
            if (length < 0 || p + 12L + length > data.Length)
            {
                throw new InvalidDataException("PNG chunk runs past the end");
            }

            ReadOnlySpan<byte> type = data.Slice(p + 4, 4);
            ReadOnlySpan<byte> body = data.Slice(p + 8, length);
            if (Crc(data.Slice(p + 4, length + 4)) != BinaryPrimitives.ReadUInt32BigEndian(data[(p + 8 + length)..]))
            {
                throw new InvalidDataException("PNG chunk's CRC is wrong");
            }

            p += 12 + length;
            if (type.SequenceEqual("IHDR"u8) && length >= 13)
            {
                width = BinaryPrimitives.ReadInt32BigEndian(body);
                height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                depth = body[8];
                colorType = body[9];
                interlace = body[12];
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                palette = body.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                transparency = body.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(body);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }
        }

        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        bool depthValid = colorType switch
        {
            0 => depth is 1 or 2 or 4 or 8 or 16,
            3 => depth is 1 or 2 or 4 or 8,
            _ => depth is 8 or 16,
        };
        if (channels == 0 || !depthValid || width <= 0 || height <= 0 || (long)width * height > MaxPixels || interlace > 1)
        {
            throw new InvalidDataException($"PNG of an unsupported kind ({width}x{height}, colour type {colorType}, depth {depth})");
        }

        if (colorType == 3 && palette == null)
        {
            throw new InvalidDataException("PNG with a palette but no PLTE");
        }

        byte[] raw;
        idat.Position = 0;
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
        using (var inflated = new MemoryStream())
        {
            zlib.CopyTo(inflated);
            raw = inflated.ToArray();
        }

        var rgba = new byte[width * height * 4];
        var png = new PngFormat(colorType, depth, channels, palette, transparency);
        int offset = 0;
        if (interlace == 0)
        {
            png.Pass(raw, ref offset, width, height, rgba, width, 0, 0, 1, 1);
        }
        else
        {
            // Adam7: seven passes over the image, each a smaller image of every few pixels
            ReadOnlySpan<int> x0 = [0, 4, 0, 2, 0, 1, 0], y0 = [0, 0, 4, 0, 2, 0, 1];
            ReadOnlySpan<int> dx = [8, 8, 4, 4, 2, 2, 1], dy = [8, 8, 8, 4, 4, 2, 2];
            for (int pass = 0; pass < 7; pass++)
            {
                int w = (width - x0[pass] + dx[pass] - 1) / dx[pass];
                int h = (height - y0[pass] + dy[pass] - 1) / dy[pass];
                if (w > 0 && h > 0)
                {
                    png.Pass(raw, ref offset, w, h, rgba, width, x0[pass], y0[pass], dx[pass], dy[pass]);
                }
            }
        }

        return new EnhancedImage(width, height, rgba);
    }

    private sealed class PngFormat(int colorType, int depth, int channels, byte[]? palette, byte[]? transparency)
    {
        // One (sub)image: its rows unfiltered, each pixel put at (x0 + x * dx, y0 + y * dy) of the image
        public void Pass(byte[] raw, ref int offset, int w, int h, byte[] rgba, int width, int x0, int y0, int dx, int dy)
        {
            int bits = channels * depth;
            int stride = ((w * bits) + 7) / 8;
            int bpp = Math.Max(1, bits / 8);
            var previous = new byte[stride];
            var row = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                if (offset + 1 + stride > raw.Length)
                {
                    throw new InvalidDataException("PNG's image data ends early");
                }

                int filter = raw[offset];
                raw.AsSpan(offset + 1, stride).CopyTo(row);
                offset += 1 + stride;
                Unfilter(filter, row, previous, bpp);
                for (int x = 0; x < w; x++)
                {
                    Pixel(row, x, rgba.AsSpan((((y0 + (y * dy)) * width) + x0 + (x * dx)) * 4, 4));
                }

                (previous, row) = (row, previous);
            }
        }

        private static void Unfilter(int filter, byte[] row, byte[] previous, int bpp)
        {
            for (int i = 0; i < row.Length; i++)
            {
                int a = i >= bpp ? row[i - bpp] : 0;
                int b = previous[i];
                int c = i >= bpp ? previous[i - bpp] : 0;
                row[i] += filter switch
                {
                    0 => 0,
                    1 => (byte)a,
                    2 => (byte)b,
                    3 => (byte)((a + b) / 2),
                    4 => (byte)Paeth(a, b, c),
                    _ => throw new InvalidDataException($"PNG filter {filter}"),
                };
            }
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        // A sample: 1, 2 and 4 bits from the byte's high bits first; 16 bits by the high byte; raw is the sample
        // before scaling (for tRNS's key and the palette's index)
        private int Sample(byte[] row, int index, out int raw)
        {
            switch (depth)
            {
                case 8:
                    return raw = row[index];
                case 16:
                    raw = (row[index * 2] << 8) | row[(index * 2) + 1];
                    return row[index * 2];
                default:
                    int bit = index * depth;
                    raw = (row[bit >> 3] >> (8 - depth - (bit & 7))) & ((1 << depth) - 1);
                    return raw * 255 / ((1 << depth) - 1);
            }
        }

        private void Pixel(byte[] row, int x, Span<byte> o)
        {
            int i = x * channels;
            switch (colorType)
            {
                case 0:
                {
                    int g = Sample(row, i, out int key);
                    o[0] = o[1] = o[2] = (byte)g;
                    o[3] = transparency is { Length: >= 2 } && key == BinaryPrimitives.ReadUInt16BigEndian(transparency) ? (byte)0 : (byte)255;
                    break;
                }

                case 2:
                {
                    o[0] = (byte)Sample(row, i, out int r);
                    o[1] = (byte)Sample(row, i + 1, out int g);
                    o[2] = (byte)Sample(row, i + 2, out int b);
                    o[3] = transparency is { Length: >= 6 }
                        && r == BinaryPrimitives.ReadUInt16BigEndian(transparency)
                        && g == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2))
                        && b == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4)) ? (byte)0 : (byte)255;
                    break;
                }

                case 3:
                {
                    Sample(row, i, out int index);
                    if ((index * 3) + 2 >= palette!.Length)
                    {
                        throw new InvalidDataException("PNG index past its palette");
                    }

                    o[0] = palette[index * 3];
                    o[1] = palette[(index * 3) + 1];
                    o[2] = palette[(index * 3) + 2];
                    o[3] = transparency != null && index < transparency.Length ? transparency[index] : (byte)255;
                    break;
                }

                case 4:
                    o[0] = o[1] = o[2] = (byte)Sample(row, i, out _);
                    o[3] = (byte)Sample(row, i + 1, out _);
                    break;

                default:
                    for (int c = 0; c < 4; c++)
                    {
                        o[c] = (byte)Sample(row, i + c, out _);
                    }

                    break;
            }
        }
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    /// <summary>
    /// A TGA file: colour-mapped (types 1, 9), true colour (2, 10) and grey (3, 11), raw or run-length encoded; 8, 15,
    /// 16, 24 and 32 bits a pixel; either origin. 16-bit pixels' top bit is alpha only when the header gives the
    /// image an alpha bit.
    /// </summary>
    public static EnhancedImage DecodeTga(ReadOnlySpan<byte> data)
    {
        if (data.Length < 18)
        {
            throw new InvalidDataException("TGA shorter than its header");
        }

        int idLength = data[0];
        int colorMapType = data[1];
        int imageType = data[2];
        int mapFirst = BinaryPrimitives.ReadUInt16LittleEndian(data[3..]);
        int mapLength = BinaryPrimitives.ReadUInt16LittleEndian(data[5..]);
        int mapBits = data[7];
        int width = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
        int bits = data[16];
        int descriptor = data[17];
        int alphaBits = descriptor & 15;
        bool rle = imageType is 9 or 10 or 11;
        int kind = imageType & 7; // 1 colour-mapped, 2 true colour, 3 grey

        bool valid = kind switch
        {
            1 => colorMapType == 1 && bits == 8 && mapBits is 15 or 16 or 24 or 32,
            2 => bits is 15 or 16 or 24 or 32,
            3 => bits == 8,
            _ => false,
        };
        if (!valid || imageType is not (1 or 2 or 3 or 9 or 10 or 11) || width == 0 || height == 0 || (long)width * height > MaxPixels)
        {
            throw new InvalidDataException($"TGA of an unsupported kind (type {imageType}, {bits} bits)");
        }

        int p = 18 + idLength;
        byte[]? map = null;
        if (colorMapType == 1)
        {
            int entry = (mapBits + 7) / 8;
            if (p + (mapLength * entry) > data.Length)
            {
                throw new InvalidDataException("TGA's colour map runs past the end");
            }

            if (kind == 1)
            {
                map = new byte[mapLength * 4];
                for (int i = 0; i < mapLength; i++)
                {
                    Color(data.Slice(p + (i * entry), entry), mapBits, alphaBits, map.AsSpan(i * 4, 4));
                }
            }

            p += mapLength * entry;
        }

        int size = (bits + 7) / 8;
        var rgba = new byte[width * height * 4];
        Span<byte> pixel = stackalloc byte[4];
        int count = width * height;
        int n = 0;
        while (n < count)
        {
            int run = 1;
            bool repeat = false;
            if (rle)
            {
                if (p >= data.Length)
                {
                    throw new InvalidDataException("TGA's image data ends early");
                }

                run = (data[p] & 0x7f) + 1;
                repeat = (data[p] & 0x80) != 0;
                p++;
            }

            for (int i = 0; i < run && n < count; i++, n++)
            {
                if (i == 0 || !repeat)
                {
                    if (p + size > data.Length)
                    {
                        throw new InvalidDataException("TGA's image data ends early");
                    }

                    ReadOnlySpan<byte> source = data.Slice(p, size);
                    p += size;
                    if (kind == 1)
                    {
                        int index = source[0] - mapFirst;
                        if (index < 0 || index >= mapLength)
                        {
                            throw new InvalidDataException("TGA index outside its colour map");
                        }

                        map.AsSpan(index * 4, 4).CopyTo(pixel);
                    }
                    else if (kind == 3)
                    {
                        pixel[0] = pixel[1] = pixel[2] = source[0];
                        pixel[3] = 255;
                    }
                    else
                    {
                        Color(source, bits, alphaBits, pixel);
                    }
                }

                // the image's first pixel is its bottom left unless the descriptor says top (bit 5) or right (bit 4)
                int x = n % width, y = n / width;
                if ((descriptor & 0x10) != 0)
                {
                    x = width - 1 - x;
                }

                if ((descriptor & 0x20) == 0)
                {
                    y = height - 1 - y;
                }

                pixel.CopyTo(rgba.AsSpan(((y * width) + x) * 4, 4));
            }
        }

        return new EnhancedImage(width, height, rgba);
    }

    // A TGA colour, BGR(A) or 5-5-5 with the top bit alpha when the image has one
    private static void Color(ReadOnlySpan<byte> source, int bits, int alphaBits, Span<byte> o)
    {
        if (bits is 15 or 16)
        {
            int v = source[0] | (source[1] << 8);
            o[0] = (byte)(((v >> 10) & 31) * 255 / 31);
            o[1] = (byte)(((v >> 5) & 31) * 255 / 31);
            o[2] = (byte)((v & 31) * 255 / 31);
            o[3] = bits == 16 && alphaBits > 0 && (v & 0x8000) == 0 ? (byte)0 : (byte)255;
            return;
        }

        o[0] = source[2];
        o[1] = source[1];
        o[2] = source[0];
        o[3] = bits == 32 ? source[3] : (byte)255;
    }
}
