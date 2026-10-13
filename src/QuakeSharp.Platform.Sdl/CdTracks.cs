using QuakeSharp.Core.Common;

namespace QuakeSharp.Platform.Sdl;

/// <summary>
/// The CD's audio tracks as files the user has (design.md §7.2): <c>music/trackNN.ogg</c> or <c>.wav</c> in the game
/// directories (<c>-game</c>'s, then <c>id1</c>), or the Steam install's <c>rerelease/id1/music</c> (the rerelease's
/// <c>track02.ogg</c>-<c>track11.ogg</c>). A mission pack (<c>-hipnotic</c>, <c>-rogue</c>) came on its own CD: its
/// tracks are its folder's <c>music</c> or the rerelease's <c>rerelease/hipnotic/music</c>, <c>rerelease/rogue/music</c>
/// (<c>track02.ogg</c>-<c>track09.ogg</c>), never id1's. Names on disk match in any case.
/// </summary>
public sealed class CdTracks
{
    // a CD's track numbers (cd_win.c's remap table has 100 entries)
    private const int MaxTracks = 99;

    private static readonly string[] Extensions = [".ogg", ".wav"];

    private readonly string?[] files = new string?[MaxTracks + 1];

    private CdTracks()
    {
    }

    /// <summary>The highest track there is a file for: the disc's last track (0: no disc).</summary>
    public int Count { get; private set; }

    /// <summary>The file of a track, or null.</summary>
    public string? File(int track) => track >= 1 && track <= MaxTracks ? files[track] : null;

    /// <summary>The tracks found from the Quake folder <paramref name="baseDir"/> and the command line's -game.</summary>
    public static CdTracks Find(string baseDir, CommandLine commandLine)
    {
        var dirs = new List<string>();
        int game = commandLine.CheckParm("-game");
        if (game != 0 && game < commandLine.Argc - 1)
        {
            dirs.Add(Path.Combine(baseDir, commandLine.Argv(game + 1), "music"));
        }

        // common.c adds rogue's directory, then hipnotic's: the last is the game's
        string disc = commandLine.CheckParm("-hipnotic") != 0 ? "hipnotic"
            : commandLine.CheckParm("-rogue") != 0 ? "rogue"
            : "id1";
        dirs.Add(Path.Combine(baseDir, disc, "music"));
        dirs.Add(Path.Combine(baseDir, "rerelease", disc, "music"));
        return In(dirs);
    }

    /// <summary>The tracks in these folders, the first folder with a track's file winning.</summary>
    public static CdTracks In(IEnumerable<string> dirs)
    {
        var tracks = new CdTracks();
        foreach (string dir in dirs)
        {
            Dictionary<string, string> names = Names(dir);
            for (int track = 1; track <= MaxTracks; track++)
            {
                if (tracks.files[track] != null)
                {
                    continue;
                }

                foreach (string extension in Extensions)
                {
                    if (names.TryGetValue($"track{track:00}{extension}", out string? path))
                    {
                        tracks.files[track] = path;
                        tracks.Count = Math.Max(tracks.Count, track);
                        break;
                    }
                }
            }
        }

        return tracks;
    }

    // the files of a folder by their names in lower case
    private static Dictionary<string, string> Names(string dir)
    {
        var names = new Dictionary<string, string>();
        string? actual = Existing(dir);
        if (actual != null)
        {
            foreach (string path in Directory.EnumerateFiles(actual))
            {
                names.TryAdd(Path.GetFileName(path).ToLowerInvariant(), path);
            }
        }

        return names;
    }

    // a folder's path as it is on disk, its names matched in any case, or null
    private static string? Existing(string dir)
    {
        if (Directory.Exists(dir))
        {
            return dir;
        }

        string? parent = Path.GetDirectoryName(dir);
        if (string.IsNullOrEmpty(parent) || parent == dir)
        {
            return null;
        }

        parent = Existing(parent);
        if (parent == null)
        {
            return null;
        }

        string name = Path.GetFileName(dir);
        return Directory.EnumerateDirectories(parent)
            .FirstOrDefault(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase));
    }
}
