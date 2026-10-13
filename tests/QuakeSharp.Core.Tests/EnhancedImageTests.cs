using System.Buffers.Binary;
using System.IO.Compression;
using QuakeSharp.Core.Enhanced;

namespace QuakeSharp.Core.Tests;

/// <summary>
/// A texture pack's PNG and TGA decoders (design.md §11.7). The files in Images/ were written by ffmpeg from a 13x7
/// RGBA picture (odd sizes, so Adam7's passes end part way), each beside ffmpeg's own decoding of it as raw RGBA
/// (<c>.rgba</c>); the kinds ffmpeg does not write (2- and 4-bit samples, tRNS, a bottom-left TGA) are made here.
/// </summary>
public class EnhancedImageTests
{
    public static TheoryData<string> ReferenceFiles() => [.. Files()];

    private static string[] Files() =>
    [
        .. Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Images"))
            .Where(path => Path.GetExtension(path) is ".png" or ".tga")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)!,
    ];

    [Theory]
    [MemberData(nameof(ReferenceFiles))]
    public void DecodesAsFfmpegDoes(string file)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Images", file);
        byte[] expected = File.ReadAllBytes(path + ".rgba");

        EnhancedImage image = EnhancedImage.Load(path);

        Assert.Equal(13, image.Width);
        Assert.Equal(7, image.Height);
        // 16-bit samples are taken by their high byte and 5-bit ones scaled by 255/31, ffmpeg rounds: within 1
        int tolerance = file.Contains("16") || file.Contains("48") || file.Contains("64") || file.Contains("555") ? 1 : 0;
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(Math.Abs(expected[i] - image.Rgba[i]) <= tolerance, $"{file}: byte {i} is {image.Rgba[i]}, ffmpeg's {expected[i]}");
        }
    }

    [Fact]
    public void ThereAreReferenceFilesOfEveryKind()
    {
        string[] files = Files();

        Assert.Contains("png_adam7_pal8.png", files);
        Assert.Contains("png_pred_paeth.png", files);
        Assert.Contains("png_monob.png", files);
        Assert.Contains("tga_rle_pal8.tga", files);
        Assert.Contains("tga_rle_bgra.tga", files);
        Assert.True(files.Length >= 25);
    }

    [Fact]
    public void TwoAndFourBitPalettesWithTransparency()
    {
        byte[] palette = [0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255];
        // 2 bits: the indices 0 1 2 3 2 in a row of 5 (the last byte's low bits unused)
        byte[] png = Png(5, 1, 2, 3, [[0b00_01_10_11, 0b10_000000]], palette, trns: [0, 128]);

        EnhancedImage image = EnhancedImage.DecodePng(png);

        Assert.Equal<byte>([0, 0, 0, 0, 255, 0, 0, 128, 0, 255, 0, 255, 0, 0, 255, 255, 0, 255, 0, 255], image.Rgba);

        byte[] four = Png(3, 1, 4, 3, [[0x21, 0x00]], palette, trns: null);
        Assert.Equal<byte>([0, 255, 0, 255, 255, 0, 0, 255, 0, 0, 0, 255], EnhancedImage.DecodePng(four).Rgba);
    }

    [Fact]
    public void GreyAndColourKeysAreTransparent()
    {
        // 4-bit grey: 15 (white), 5 (the key), 0
        byte[] grey = Png(3, 1, 4, 0, [[0xF5, 0x00]], null, trns: [0, 5]);
        Assert.Equal<byte>([255, 255, 255, 255, 85, 85, 85, 0, 0, 0, 0, 255], EnhancedImage.DecodePng(grey).Rgba);

        byte[] rgb = Png(2, 1, 8, 2, [[1, 2, 3, 4, 5, 6]], null, trns: [0, 4, 0, 5, 0, 6]);
        Assert.Equal<byte>([1, 2, 3, 255, 4, 5, 6, 0], EnhancedImage.DecodePng(rgb).Rgba);
    }

    [Fact]
    public void ABrokenPngIsRefused()
    {
        byte[] png = Png(1, 1, 8, 0, [[7]], null, trns: null);
        Assert.Equal<byte>([7, 7, 7, 255], EnhancedImage.DecodePng(png).Rgba);

        byte[] badCrc = (byte[])png.Clone();
        badCrc[^20] ^= 1; // in the IDAT
        Assert.Throws<InvalidDataException>(() => EnhancedImage.DecodePng(badCrc));
        Assert.Throws<InvalidDataException>(() => EnhancedImage.DecodePng(png.AsSpan(0, png.Length - 12)));
        Assert.Throws<InvalidDataException>(() => EnhancedImage.DecodePng("not a png file"u8));
    }

    [Fact]
    public void ATgaFromTheBottomLeftOrRight()
    {
        // 2x2, 24 bits, rows bottom first: blue red / green white
        byte[] pixels = [255, 0, 0, 0, 0, 255, 0, 255, 0, 255, 255, 255];
        byte[] expected = [0, 255, 0, 255, 255, 255, 255, 255, 0, 0, 255, 255, 255, 0, 0, 255];
        Assert.Equal(expected, EnhancedImage.DecodeTga(Tga(2, 2, 2, 24, 0, pixels)).Rgba);

        // right to left as well: each row reversed
        byte[] mirrored = [255, 255, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 0, 0, 255, 255];
        Assert.Equal(mirrored, EnhancedImage.DecodeTga(Tga(2, 2, 2, 24, 0x10, pixels)).Rgba);
    }

    [Fact]
    public void A16BitTgasTopBitIsAlphaOnlyWhenTheHeaderSaysSo()
    {
        byte[] pixels = [0x1F, 0x00, 0xE0, 0xFF]; // blue, alpha bit 0; red and green, alpha bit 1
        Assert.Equal<byte>([0, 0, 255, 0, 255, 255, 0, 255], EnhancedImage.DecodeTga(Tga(2, 1, 2, 16, 0x21, pixels)).Rgba);
        Assert.Equal<byte>([0, 0, 255, 255, 255, 255, 0, 255], EnhancedImage.DecodeTga(Tga(2, 1, 2, 16, 0x20, pixels)).Rgba);
    }

    [Fact]
    public void ABrokenTgaIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => EnhancedImage.DecodeTga(Tga(2, 2, 2, 24, 0x20, new byte[11])));
        Assert.Throws<InvalidDataException>(() => EnhancedImage.DecodeTga(Tga(2, 2, 7, 24, 0x20, new byte[12])));
        Assert.Throws<InvalidDataException>(() => EnhancedImage.DecodeTga(new byte[10]));
    }

    // A PNG of unfiltered rows (filter byte 0 added), its IDAT compressed as one
    private static byte[] Png(int width, int height, int depth, int colorType, byte[][] rows, byte[]? palette, byte[]? trns)
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = (byte)depth;
        ihdr[9] = (byte)colorType;
        Chunk(stream, "IHDR", ihdr);
        if (palette != null)
        {
            Chunk(stream, "PLTE", palette);
        }

        if (trns != null)
        {
            Chunk(stream, "tRNS", trns);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            foreach (byte[] row in rows)
            {
                zlib.WriteByte(0);
                zlib.Write(row);
            }
        }

        Chunk(stream, "IDAT", compressed.ToArray());
        Chunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        stream.Write(buffer);
        byte[] typed = [.. System.Text.Encoding.ASCII.GetBytes(type), .. data];
        stream.Write(typed);
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in typed)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc ^ 0xFFFFFFFFu);
        stream.Write(buffer);
    }

    // A TGA without an ID or colour map
    private static byte[] Tga(int width, int height, int type, int bits, int descriptor, byte[] pixels)
    {
        var header = new byte[18];
        header[2] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), (ushort)height);
        header[16] = (byte)bits;
        header[17] = (byte)descriptor;
        return [.. header, .. pixels];
    }
}
