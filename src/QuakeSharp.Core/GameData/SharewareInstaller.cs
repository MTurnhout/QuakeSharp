using System.IO.Compression;
using System.Security.Cryptography;

namespace QuakeSharp.Core.GameData;

/// <summary>
/// The shareware made ready to play from its own, untouched archive (design.md §11.10, decision 78): id's
/// <c>quake106.zip</c> is distributed whole, as its licence asks (SLICNSE.TXT, section 6), and QuakeSharp takes
/// <c>id1/pak0.pak</c> out of it the first time, from the LHA archive <c>resource.1</c> inside, into a folder of the
/// user's. Nothing of id's is changed.
/// </summary>
public static class SharewareInstaller
{
    /// <summary>The shareware 1.06 archive's MD5 (the Internet Archive's Quake_802, decision 53).</summary>
    public const string ZipMd5 = "8cee4d03ee092909fdb6a4f84f0c1357";

    /// <summary>The archive's name beside the executable.</summary>
    public const string ZipName = "quake106.zip";

    /// <summary>
    /// <paramref name="baseDir"/>/id1/pak0.pak made from <paramref name="zipPath"/> if it is not there yet: the pak
    /// unpacked, checked against the catalogue's MD5 and written whole (to a temporary name first), with the
    /// licence's text beside it. True when the folder has the pak afterwards.
    /// </summary>
    public static bool Install(string zipPath, string baseDir)
    {
        string pak = Path.Combine(baseDir, "id1", "pak0.pak");
        if (File.Exists(pak))
        {
            return true;
        }

        byte[] resource;
        using (ZipArchive zip = ZipFile.OpenRead(zipPath))
        {
            ZipArchiveEntry entry = zip.GetEntry("resource.1") ?? throw new InvalidDataException($"{zipPath} has no resource.1");
            using Stream stream = entry.Open();
            resource = new byte[entry.Length];
            stream.ReadExactly(resource);
        }

        var archive = new LhaArchive(resource);
        LhaArchive.Entry pakEntry = archive.Find(@"ID1\PAK0.PAK") ?? throw new InvalidDataException($"{zipPath} has no id1/pak0.pak");
        byte[] data = archive.Extract(pakEntry);
        string md5 = Convert.ToHexStringLower(MD5.HashData(data));
        if (GameDataCatalog.IdentifyByHash(md5)?.RelativePath != "id1/pak0.pak")
        {
            throw new InvalidDataException($"{zipPath}'s pak0.pak is not the shareware's (MD5 {md5})");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(pak)!);
        foreach (string text in (string[])["SLICNSE.TXT", "LICINFO.TXT", "README.TXT"])
        {
            if (archive.Find(text) is { } licence)
            {
                File.WriteAllBytes(Path.Combine(baseDir, text.ToLowerInvariant()), archive.Extract(licence));
            }
        }

        string temporary = pak + ".part";
        File.WriteAllBytes(temporary, data);
        File.Move(temporary, pak, overwrite: true);
        return true;
    }
}
