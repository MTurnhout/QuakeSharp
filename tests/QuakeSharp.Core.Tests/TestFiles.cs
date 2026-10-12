using System.Buffers.Binary;
using System.Text;

namespace QuakeSharp.Core.Tests;

/// <summary>Writes small PAK and WAD2 files for the tests, in a temporary folder deleted on dispose.</summary>
internal sealed class TestFiles : IDisposable
{
    public TestFiles()
    {
        Root = Path.Combine(Path.GetTempPath(), "quakesharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Write(string relative, byte[] data)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>A pack file (common.c: dpackheader_t, then the files, then the dpackfile_t directory).</summary>
    public static byte[] Pak(params (string Name, byte[] Data)[] files)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[12]);
        var positions = new List<int>();
        foreach ((_, byte[] data) in files)
        {
            positions.Add((int)stream.Position);
            stream.Write(data);
        }

        int dirofs = (int)stream.Position;
        for (int i = 0; i < files.Length; i++)
        {
            var entry = new byte[64];
            Encoding.Latin1.GetBytes(files[i].Name).CopyTo(entry, 0);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(56), positions[i]);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(60), files[i].Data.Length);
            stream.Write(entry);
        }

        byte[] pak = stream.ToArray();
        "PACK"u8.CopyTo(pak);
        BinaryPrimitives.WriteInt32LittleEndian(pak.AsSpan(4), dirofs);
        BinaryPrimitives.WriteInt32LittleEndian(pak.AsSpan(8), files.Length * 64);
        return pak;
    }

    /// <summary>A WAD2 file (wad.h: wadinfo_t, the lumps, then the lumpinfo_t directory).</summary>
    public static byte[] Wad2(params (string Name, sbyte Type, byte[] Data)[] lumps)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[12]);
        var positions = new List<int>();
        foreach ((_, _, byte[] data) in lumps)
        {
            positions.Add((int)stream.Position);
            stream.Write(data);
        }

        int infotableofs = (int)stream.Position;
        for (int i = 0; i < lumps.Length; i++)
        {
            var entry = new byte[32];
            BinaryPrimitives.WriteInt32LittleEndian(entry, positions[i]);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(4), lumps[i].Data.Length);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(8), lumps[i].Data.Length);
            entry[12] = (byte)lumps[i].Type;
            Encoding.Latin1.GetBytes(lumps[i].Name).CopyTo(entry, 16);
            stream.Write(entry);
        }

        byte[] wad = stream.ToArray();
        "WAD2"u8.CopyTo(wad);
        BinaryPrimitives.WriteInt32LittleEndian(wad.AsSpan(4), lumps.Length);
        BinaryPrimitives.WriteInt32LittleEndian(wad.AsSpan(8), infotableofs);
        return wad;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
