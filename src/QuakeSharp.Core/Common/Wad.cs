using System.Buffers.Binary;
using System.Text;

namespace QuakeSharp.Core.Common;

/// <summary>
/// wad.c: a WAD2 file (gfx.wad) loaded whole, as wad_base, with its lump directory (wad_numlumps, wad_lumps).
/// Lumps are addressed by their offset in <see cref="Data"/>, which C reads through pointers into wad_base.
/// </summary>
public sealed class Wad
{
    // wad.h: lump types
    public const int CMP_NONE = 0;
    public const int CMP_LZSS = 1;

    public const int TYP_NONE = 0;
    public const int TYP_LABEL = 1;

    public const int TYP_LUMPY = 64; // 64 + grab command number
    public const int TYP_PALETTE = 64;
    public const int TYP_QTEX = 65;
    public const int TYP_QPIC = 66;
    public const int TYP_SOUND = 67;
    public const int TYP_MIPTEX = 68;

    // sizeof(wadinfo_t), sizeof(lumpinfo_t)
    private const int WadInfoSize = 12;
    private const int LumpInfoSize = 32;

    private Wad(byte[] data, LumpInfo[] lumps)
    {
        Data = data;
        Lumps = lumps;
    }

    /// <summary>wad_base: the whole file, followed by the 0 byte COM_LoadFile appends.</summary>
    public byte[] Data { get; }

    /// <summary>wad_lumps, wad_numlumps</summary>
    public IReadOnlyList<LumpInfo> Lumps { get; }

    // wad.c: W_LoadWadFile
    public static Wad LoadWadFile(FileSystem fileSystem, string filename)
    {
        byte[] wad_base = fileSystem.LoadFile(filename) ?? throw new QuakeException($"W_LoadWadFile: couldn't load {filename}");

        if (wad_base.Length < WadInfoSize
            || wad_base[0] != 'W' || wad_base[1] != 'A' || wad_base[2] != 'D' || wad_base[3] != '2')
        {
            throw new QuakeException($"Wad file {filename} doesn't have WAD2 id\n");
        }

        int wad_numlumps = BinaryPrimitives.ReadInt32LittleEndian(wad_base.AsSpan(4));
        int infotableofs = BinaryPrimitives.ReadInt32LittleEndian(wad_base.AsSpan(8));

        var lumps = new LumpInfo[wad_numlumps];
        for (int i = 0; i < wad_numlumps; i++)
        {
            ReadOnlySpan<byte> lump_p = wad_base.AsSpan(infotableofs + (i * LumpInfoSize), LumpInfoSize);
            lumps[i] = new LumpInfo(
                BinaryPrimitives.ReadInt32LittleEndian(lump_p),
                BinaryPrimitives.ReadInt32LittleEndian(lump_p[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(lump_p[8..]),
                (sbyte)lump_p[12],
                (sbyte)lump_p[13],
                CleanupName(Encoding.Latin1.GetString(lump_p.Slice(16, 16))));

            // SwapPic changes nothing on a little-endian machine
        }

        return new Wad(wad_base, lumps);
    }

    // wad.c: W_CleanupName
    // Lowercases name and pads with zeros to the length of lumpinfo_t->name (here: cut at the first 0).
    public static string CleanupName(string @in)
    {
        var @out = new StringBuilder(16);
        for (int i = 0; i < 16 && i < @in.Length; i++)
        {
            char c = @in[i];
            if (c == '\0')
            {
                break;
            }

            if (c is >= 'A' and <= 'Z')
            {
                c = (char)(c + ('a' - 'A'));
            }

            @out.Append(c);
        }

        return @out.ToString();
    }

    // wad.c: W_GetLumpinfo
    public LumpInfo GetLumpinfo(string name)
    {
        string clean = CleanupName(name);

        foreach (LumpInfo lump_p in Lumps)
        {
            if (clean == lump_p.Name)
            {
                return lump_p;
            }
        }

        throw new QuakeException($"W_GetLumpinfo: {name} not found");
    }

    // wad.c: W_GetLumpName (the lump's offset in Data)
    public int GetLumpName(string name) => GetLumpinfo(name).FilePos;

    // wad.c: W_GetLumpNum (the lump's offset in Data). The C also accepts num == wad_numlumps, one past the
    // directory; nothing calls it.
    public int GetLumpNum(int num)
    {
        if (num < 0 || num >= Lumps.Count)
        {
            throw new QuakeException($"W_GetLumpNum: bad number: {num}");
        }

        return Lumps[num].FilePos;
    }

    /// <summary>wad.h: lumpinfo_t, with its name cleaned up as W_LoadWadFile leaves it.</summary>
    public sealed record LumpInfo(int FilePos, int DiskSize, int Size, sbyte Type, sbyte Compression, string Name);
}
