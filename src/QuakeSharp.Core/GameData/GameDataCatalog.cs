using System.Security.Cryptography;

namespace QuakeSharp.Core.GameData;

/// <summary>A game data file QuakeSharp knows by its MD5.</summary>
/// <param name="Description">What it is.</param>
/// <param name="RelativePath">Where it lives under the Quake folder.</param>
/// <param name="Md5">Its MD5, lower case.</param>
/// <param name="Original">Whether it is confirmed as the original release, valid for fidelity tests.</param>
public sealed record GameDataFile(string Description, string RelativePath, string Md5, bool Original);

/// <summary>Identifies game data by MD5 (design.md §2).</summary>
public static class GameDataCatalog
{
    public static IReadOnlyList<GameDataFile> Known { get; } =
    [
        new("Quake registered 1.06, pak0", "id1/pak0.pak", "5906e5998fc3d896ddaf5e6a62e03abb", true),
        new("Quake registered 1.06, pak1", "id1/pak1.pak", "d76b3e5678f0b64ac74ce5e340e6a685", true),
        new("Mission Pack 1: Scourge of Armagon", "hipnotic/pak0.pak", "0ab83681aaf841c4320269e02941a14a", true),
        new("Mission Pack 2: Dissolution of Eternity (the mission packs' CD of 1998, Steam)", "rogue/pak0.pak",
            "c38a4e04219c317cd1b02f386bdfe11f", true),
        new("Mission Pack 2: Dissolution of Eternity (its own CD)", "rogue/pak0.pak",
            "f8898a483b131de21581656c94f3c1a4", true),
        new("Mission Pack 2: Dissolution of Eternity (GOG.com)", "rogue/pak0.pak",
            "f71428763229bdb373f00a547296efe3", true),
    ];

    public static GameDataFile? IdentifyByHash(string md5) =>
        Known.FirstOrDefault(k => string.Equals(k.Md5, md5, StringComparison.OrdinalIgnoreCase));

    public static GameDataFile? IdentifyFile(string path)
    {
        using var stream = File.OpenRead(path);
        return IdentifyByHash(ComputeMd5(stream));
    }

    public static string ComputeMd5(Stream stream) => Convert.ToHexStringLower(MD5.HashData(stream));
}
