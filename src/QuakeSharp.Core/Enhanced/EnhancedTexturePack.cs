namespace QuakeSharp.Core.Enhanced;

/// <summary>
/// The texture packs a game can use (design.md §11.7, decision 68): the loose files under textures/ and progs/ of
/// some game directories, the first directory's file before the next's, PNG before TGA. Names are found without regard
/// to case and with '/' on every platform. The directories are listed once, when the pack is made; the game's own file
/// reading (COM_FindFile, COM_LoadFile) is never used.
/// </summary>
public sealed class EnhancedTexturePack
{
    private static readonly string[] Folders = ["textures", "progs"];

    // the pack's names in lower case, without an extension, to their files
    private readonly Dictionary<string, string> files = new(StringComparer.Ordinal);

    /// <param name="directories">Game directories in the order they are looked in: for a mission pack its own, then
    /// id1's; each in the user folder, then in the Quake folder.</param>
    public EnhancedTexturePack(IEnumerable<string> directories)
    {
        foreach (string directory in directories)
        {
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string folder in Folders)
            {
                string? root = FindFolder(directory, folder);
                if (root == null)
                {
                    continue;
                }

                foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    string extension = Path.GetExtension(path).ToLowerInvariant();
                    int rank = Array.IndexOf(EnhancedPackNames.Extensions, extension);
                    if (rank < 0)
                    {
                        continue;
                    }

                    string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    string name = $"{folder}/{relative[..^extension.Length]}".ToLowerInvariant();
                    if (!found.TryGetValue(name, out string? other)
                        || rank < Array.IndexOf(EnhancedPackNames.Extensions, Path.GetExtension(other).ToLowerInvariant()))
                    {
                        found[name] = path;
                    }
                }
            }

            foreach ((string name, string path) in found)
            {
                files.TryAdd(name, path);
            }
        }
    }

    /// <summary>How many textures and layers the pack has.</summary>
    public int Count => files.Count;

    /// <summary>Every name the pack has (lower case, '/', no extension) with its file.</summary>
    public IReadOnlyDictionary<string, string> Files => files;

    /// <summary>The file of a pack name (<see cref="EnhancedPackNames"/>), or null.</summary>
    public string? Find(string name) => files.GetValueOrDefault(name.ToLowerInvariant());

    /// <summary>The first of some names the pack has, and its name; null if none.</summary>
    public (string Name, string File)? FindFirst(IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            if (Find(name) is string file)
            {
                return (name, file);
            }
        }

        return null;
    }

    /// <summary>A world texture of a BSP (<paramref name="map"/>, its base name): the map's own, then every map's.</summary>
    public (string Name, string File)? FindWorld(string map, string texture) =>
        FindFirst(EnhancedPackNames.WorldTexture(map, texture));

    /// <summary>A layer of a texture found (<see cref="EnhancedPackNames.Glow"/> and the others; _glow or else
    /// _luma for the fullbrights).</summary>
    public string? FindLayer(string name, string layer) =>
        Find(name + layer) ?? (layer == EnhancedPackNames.Glow ? Find(name + EnhancedPackNames.Luma) : null);

    // a folder of a directory whatever its case (Linux), the directory itself as given
    private static string? FindFolder(string directory, string folder)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        string exact = Path.Combine(directory, folder);
        if (Directory.Exists(exact))
        {
            return exact;
        }

        return Directory.EnumerateDirectories(directory)
            .FirstOrDefault(d => string.Equals(Path.GetFileName(d), folder, StringComparison.OrdinalIgnoreCase));
    }
}
