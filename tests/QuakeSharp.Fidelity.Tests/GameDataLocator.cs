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

    /// <summary>The Quake folder whose id1/pak0.pak and id1/pak1.pak are the original registered 1.06 files.</summary>
    public static string RequireBaseDir()
    {
        string? dir = BaseDir.Value;
        Assert.SkipUnless(dir != null,
            "Set QUAKESHARP_QUAKE_DIR to a Quake folder with the original id1/pak0.pak and id1/pak1.pak.");
        return dir!;
    }

    private static string? Find()
    {
        string? setting = Environment.GetEnvironmentVariable("QUAKESHARP_QUAKE_DIR");
        if (string.IsNullOrEmpty(setting))
        {
            return null;
        }

        foreach (string dir in setting.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsOriginal(dir, "id1/pak0.pak") && IsOriginal(dir, "id1/pak1.pak"))
            {
                return dir;
            }
        }

        return null;
    }

    private static bool IsOriginal(string dir, string relative) =>
        HostPath.Resolve(Path.Combine(dir, relative)) is string path
        && File.Exists(path)
        && GameDataCatalog.IdentifyFile(path) is { Original: true } file
        && file.RelativePath == relative;
}
