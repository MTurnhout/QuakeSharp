namespace QuakeSharp.Core.Common;

/// <summary>
/// Finds the files the engine names on the host's file system, matching names without regard to case where the
/// file system is case sensitive (Quake's data was made on DOS: the Steam release has id1/PAK0.PAK).
/// </summary>
public static class HostPath
{
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>The existing file or directory <paramref name="path"/> names, or null.</summary>
    public static string? Resolve(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            return path;
        }

        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? "";
        string current = root;
        foreach (string part in full[root.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(current, part);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                if (!Directory.Exists(current))
                {
                    return null;
                }

                string? match = Directory.EnumerateFileSystemEntries(current)
                    .FirstOrDefault(e => string.Equals(Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    return null;
                }

                candidate = match;
            }

            current = candidate;
        }

        return current;
    }

    /// <summary>
    /// The file to write for <paramref name="path"/>: an existing file of that name in any case, or the name in the
    /// directory it resolves to.
    /// </summary>
    public static string ResolveForWrite(string path)
    {
        if (Resolve(path) is string existing && File.Exists(existing))
        {
            return existing;
        }

        string? dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
        {
            return path;
        }

        return Path.Combine(Resolve(dir) ?? dir, Path.GetFileName(path));
    }
}
