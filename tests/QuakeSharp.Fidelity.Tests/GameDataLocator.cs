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

    private static IEnumerable<string> Settings()
    {
        string? setting = Environment.GetEnvironmentVariable("QUAKESHARP_QUAKE_DIR");
        return string.IsNullOrEmpty(setting) ? [] : setting.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? Find() =>
        Settings().FirstOrDefault(dir => IsOriginal(dir, "id1/pak0.pak") && IsOriginal(dir, "id1/pak1.pak"));

    private static string? FindPack(string pack)
    {
        string[] paks = ["id1/pak0.pak", "id1/pak1.pak", $"{pack}/pak0.pak"];
        string? source = Settings().FirstOrDefault(dir => paks.All(pak => IsOriginal(dir, pak)));
        if (source == null)
        {
            return null;
        }

        string target = Path.Combine(Path.GetTempPath(), "quakesharp-tests", "paks-" + pack);
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
