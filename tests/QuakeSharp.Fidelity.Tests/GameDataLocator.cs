using System.Collections.Concurrent;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.GameData;

namespace QuakeSharp.Fidelity.Tests;

/// <summary>
/// Finds the original Quake data through QUAKESHARP_QUAKE_DIR: one Quake folder (holding id1/) or several separated
/// by the path separator. Tests skip when it is not set or holds no original data.
/// </summary>
internal static class GameDataLocator
{
    private static readonly Lazy<string?> BaseDir = new(Find);

    private static readonly ConcurrentDictionary<string, Lazy<string?>> PackDirs = new(StringComparer.Ordinal);

    private static readonly Lazy<string?> SharewareDir = new(() => CopyOriginals("paks-shareware", ["id1/pak0.pak"]));

    /// <summary>The Quake folder whose id1/pak0.pak and id1/pak1.pak are the original registered 1.06 files.</summary>
    public static string RequireBaseDir()
    {
        string? dir = BaseDir.Value;
        Assert.SkipUnless(dir != null,
            "Set QUAKESHARP_QUAKE_DIR to a Quake folder with the original id1/pak0.pak and id1/pak1.pak.");
        return dir!;
    }

    /// <summary>
    /// A Quake folder holding only the original PAKs of id1 and of a mission pack (<paramref name="pack"/>: hipnotic,
    /// rogue), as the oracle's (design.md decision 45): the installations' pack folders hold a loose config.cfg that
    /// quake.rc would execute. The PAKs are copied once into a folder under the temporary folder.
    /// </summary>
    public static string RequirePackBaseDir(string pack)
    {
        string? dir = PackDirs.GetOrAdd(pack, p => new Lazy<string?>(() => FindPack(p))).Value;
        Assert.SkipUnless(dir != null,
            $"Set QUAKESHARP_QUAKE_DIR to a Quake folder with the original id1/pak0.pak, id1/pak1.pak and {pack}/pak0.pak.");
        return dir!;
    }

    /// <summary>
    /// A Quake folder holding only the original id1/pak0.pak: the shareware's, which is the registered one's first PAK
    /// (design.md decision 48), from quake106.zip in CI or from a registered installation. It is copied once into a
    /// folder under the temporary folder.
    /// </summary>
    public static string RequireSharewareBaseDir()
    {
        string? dir = SharewareDir.Value;
        Assert.SkipUnless(dir != null,
            "Set QUAKESHARP_QUAKE_DIR to a Quake folder with the original id1/pak0.pak (the shareware's or the registered).");
        return dir!;
    }

    /// <summary>
    /// The shareware's own archive, quake106.zip, through QUAKESHARP_SHAREWARE_ZIP (CI keeps the one it downloads): the
    /// file whose MD5 is the Internet Archive's (design.md decision 53).
    /// </summary>
    public static string RequireSharewareZip()
    {
        string? zip = Environment.GetEnvironmentVariable("QUAKESHARP_SHAREWARE_ZIP");
        bool found = false;
        if (!string.IsNullOrEmpty(zip) && File.Exists(zip))
        {
            using FileStream stream = File.OpenRead(zip);
            found = GameDataCatalog.ComputeMd5(stream) == SharewareInstaller.ZipMd5;
        }

        Assert.SkipUnless(found, "Set QUAKESHARP_SHAREWARE_ZIP to the shareware's quake106.zip.");
        return zip!;
    }

    private static IEnumerable<string> Settings()
    {
        string? setting = Environment.GetEnvironmentVariable("QUAKESHARP_QUAKE_DIR");
        return string.IsNullOrEmpty(setting) ? [] : setting.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? Find() =>
        Settings().FirstOrDefault(dir => IsOriginal(dir, "id1/pak0.pak") && IsOriginal(dir, "id1/pak1.pak"));

    private static string? FindPack(string pack) =>
        CopyOriginals("paks-" + pack, ["id1/pak0.pak", "id1/pak1.pak", $"{pack}/pak0.pak"]);

    // The original paks of the first setting that holds them all, copied alone into a folder under the temporary folder
    private static string? CopyOriginals(string name, string[] paks)
    {
        string? source = Settings().FirstOrDefault(dir => paks.All(pak => IsOriginal(dir, pak)));
        if (source == null)
        {
            return null;
        }

        string target = Path.Combine(Path.GetTempPath(), "quakesharp-tests", name);
        foreach (string pak in paks)
        {
            var from = new FileInfo(HostPath.Resolve(Path.Combine(source, pak))!);
            var to = new FileInfo(Path.Combine(target, pak));
            if (!to.Exists || to.Length != from.Length || to.LastWriteTimeUtc != from.LastWriteTimeUtc)
            {
                Directory.CreateDirectory(to.DirectoryName!);
                from.CopyTo(to.FullName, overwrite: true);
            }
        }

        return target;
    }

    private static bool IsOriginal(string dir, string relative) =>
        HostPath.Resolve(Path.Combine(dir, relative)) is string path
        && File.Exists(path)
        && GameDataCatalog.IdentifyFile(path) is { Original: true } file
        && file.RelativePath == relative;
}
