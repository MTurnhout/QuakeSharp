using System.Buffers.Binary;
using System.IO.Compression;

namespace QuakeSharp.Core.Enhanced;

/// <summary>Minimal 8-bit PNG encoder over .NET's ZLibStream (no external dependencies): the tools' images and the enhanced mode's screenshots.</summary>
public static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static void WriteRgba(string path, int width, int height, ReadOnlySpan<byte> rgba)
    {
        using var file = File.Create(path);
        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // colour type: RGBA
        WriteChunk(file, "IHDR"u8, ihdr);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            int stride = width * 4;
            for (int y = 0; y < height; y++)
            {
                zlib.WriteByte(0); // filter: none
                zlib.Write(rgba.Slice(y * stride, stride));
            }
        }

        WriteChunk(file, "IDAT"u8, compressed.ToArray());
        WriteChunk(file, "IEND"u8, []);
    }

    /// <summary>Writes palette indices through a 768-byte RGB palette; <paramref name="opaque"/> null means fully opaque.</summary>
    public static void WriteIndexed(string path, int width, int height, ReadOnlySpan<byte> indices, ReadOnlySpan<byte> palette, ReadOnlySpan<bool> opaque = default)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            int c = indices[i] * 3;
            rgba[(i * 4) + 0] = palette[c];
            rgba[(i * 4) + 1] = palette[c + 1];
            rgba[(i * 4) + 2] = palette[c + 2];
            rgba[(i * 4) + 3] = opaque.IsEmpty || opaque[i] ? (byte)255 : (byte)0;
        }

        WriteRgba(path, width, height, rgba);
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        stream.Write(buffer);
        stream.Write(type);
        stream.Write(data);

        uint crc = Crc(0xFFFFFFFFu, type);
        crc = Crc(crc, data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        stream.Write(buffer);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
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
}
