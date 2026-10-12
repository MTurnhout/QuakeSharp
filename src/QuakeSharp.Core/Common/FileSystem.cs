using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace QuakeSharp.Core.Common;

/// <summary>
/// The Quake file system of common.c: the search path of game directories and pack files, and finding and loading
/// files through it.
/// </summary>
/// <remarks>
/// Modernised where the player sees no difference: COM_LoadFile's hunk, zone, cache and stack variants all return a
/// managed buffer; the developers' -cachedir copying is left out; and file names on disk are matched without regard
/// to case where the file system is case sensitive (the Steam release's files are upper case: id1/PAK0.PAK), while
/// names inside pack files still compare exactly, as in C.
///
/// With a <see cref="UserDir"/>, what the game writes into its game directory (config.cfg, savegames, demos,
/// screenshots, qconsole.log) goes to the game directory's namesake under it instead, and a game directory's loose
/// files are looked for there first, then in the game directory itself (design.md §9).
/// </remarks>
public sealed partial class FileSystem
{
    // quakedef.h: GAMENAME
    public const string GAMENAME = "id1";

    private const int MAX_FILES_IN_PACK = 2048;

    // common.c: if a packfile directory differs from this, it is assumed to be hacked
    private const int PAK0_COUNT = 339;
    private const int PAK0_CRC = 32981;

    // sizeof(dpackheader_t), sizeof(dpackfile_t)
    private const int PackHeaderSize = 12;
    private const int PackFileSize = 64;

    private readonly CommandLine commandLine;
    private readonly Action<string> conPrintf;
    private readonly Action<string> sysPrintf;

    // common.c: com_searchpaths
    private SearchPath? com_searchpaths;

    /// <param name="commandLine">com_argv</param>
    /// <param name="conPrintf">console.c: Con_Printf</param>
    /// <param name="sysPrintf">sys_win.c: Sys_Printf</param>
    public FileSystem(CommandLine commandLine, Action<string> conPrintf, Action<string> sysPrintf)
    {
        this.commandLine = commandLine;
        this.conPrintf = conPrintf;
        this.sysPrintf = sysPrintf;
    }

    /// <summary>common.c: com_gamedir</summary>
    public string GameDir { get; private set; } = "";

    /// <summary>
    /// The folder that takes the game's writes and is searched before each game directory's own loose files: its
    /// namesake under this folder (id1, a -game directory...). Null: the game directory itself, as in WinQuake.
    /// </summary>
    public string? UserDir { get; init; }

    /// <summary>
    /// The folder the game's relative paths start from, as the process's current folder does for WinQuake (null:
    /// the process's own), so a game in another folder can be run with the base directory "." as WinQuake is.
    /// </summary>
    public string? WorkDir { get; init; }

    // A path the game names, on the host
    private string OnHost(string path) => WorkDir == null || Path.IsPathRooted(path) ? path : Path.Combine(WorkDir, path);

    /// <summary>Where a file the game writes into com_gamedir goes: the game directory's folder under
    /// <see cref="UserDir"/>, or com_gamedir itself.</summary>
    public string WritePath(string filename)
    {
        string dir = UserDirFor(GameDir) ?? GameDir;
        string path = $"{dir}/{filename}";
        if (UserDir != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(OnHost(path)))!);
        }

        return HostPath.ResolveForWrite(OnHost(path));
    }

    /// <summary>
    /// A file the game opens in com_gamedir with fopen (a savegame): from where <see cref="WritePath"/> writes it
    /// first, then com_gamedir itself; null if neither has it.
    /// </summary>
    public byte[]? ReadWritten(string filename)
    {
        foreach (string? dir in new[] { UserDirFor(GameDir), GameDir })
        {
            if (dir == null)
            {
                continue;
            }

            string? path = HostPath.Resolve(OnHost($"{dir}/{filename}"));
            if (path != null && File.Exists(path))
            {
                try
                {
                    return File.ReadAllBytes(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    // A game directory's namesake under UserDir
    private string? UserDirFor(string dir) =>
        UserDir == null || dir.Length == 0 ? null : Path.Combine(UserDir, Path.GetFileName(dir.TrimEnd('/', '\\')));

    /// <summary>common.c: com_modified, set true if using non-id files</summary>
    public bool Modified { get; private set; }

    /// <summary>common.c: static_registered (true until <see cref="CheckRegistered"/> runs)</summary>
    public bool StaticRegistered { get; private set; } = true;

    /// <summary>common.c: proghack</summary>
    public bool ProgHack { get; private set; }

    /// <summary>common.c: com_filesize, the length of the file last found (-1 if none)</summary>
    public int FileSize { get; private set; }

    /// <summary>draw.c: Draw_BeginDisc and Draw_EndDisc, around every file COM_LoadFile reads.</summary>
    public Action? BeginDisc { get; set; }

    public Action? EndDisc { get; set; }

    // common.c: COM_InitFilesystem
    public void InitFilesystem(string hostBaseDir)
    {
        // -basedir <path>
        // Overrides the system supplied base directory (under GAMENAME)
        string basedir;
        int i = commandLine.CheckParm("-basedir");
        if (i != 0 && i < commandLine.Argc - 1)
        {
            basedir = commandLine.Argv(i + 1);
        }
        else
        {
            basedir = hostBaseDir;
        }

        if (basedir.Length > 0 && (basedir[^1] == '\\' || basedir[^1] == '/'))
        {
            basedir = basedir[..^1];
        }

        // -cachedir is not supported (see the remarks)

        // start up with GAMENAME by default (id1)
        AddGameDirectory($"{basedir}/{GAMENAME}");

        if (commandLine.CheckParm("-rogue") != 0)
        {
            AddGameDirectory($"{basedir}/rogue");
        }

        if (commandLine.CheckParm("-hipnotic") != 0)
        {
            AddGameDirectory($"{basedir}/hipnotic");
        }

        // -game <gamedir>
        // Adds basedir/gamedir as an override game
        i = commandLine.CheckParm("-game");
        if (i != 0 && i < commandLine.Argc - 1)
        {
            Modified = true;
            AddGameDirectory($"{basedir}/{commandLine.Argv(i + 1)}");
        }

        // -path <dir or packfile> [<dir or packfile>] ...
        // Fully specifies the exact search path, overriding the generated one
        i = commandLine.CheckParm("-path");
        if (i != 0)
        {
            Modified = true;
            com_searchpaths = null;
            while (++i < commandLine.Argc)
            {
                string arg = commandLine.Argv(i);
                if (arg.Length == 0 || arg[0] == '+' || arg[0] == '-')
                {
                    break;
                }

                var search = new SearchPath();
                if (Com.FileExtension(arg) == "pak")
                {
                    search.Pack = LoadPackFile(arg) ?? throw new QuakeException($"Couldn't load packfile: {arg}");
                }
                else
                {
                    search.FileName = arg;
                }

                search.Next = com_searchpaths;
                com_searchpaths = search;
            }
        }

        if (commandLine.CheckParm("-proghack") != 0)
        {
            ProgHack = true;
        }
    }

    // common.c: COM_AddGameDirectory
    // Sets com_gamedir, adds the directory to the head of the path, then loads and adds pak0.pak pak1.pak ...
    public void AddGameDirectory(string dir)
    {
        GameDir = dir;

        // add the directory to the search path
        com_searchpaths = new SearchPath { FileName = dir, Next = com_searchpaths };

        // add any pak files in the format pak0.pak pak1.pak, ...
        for (int i = 0; ; i++)
        {
            Pack? pak = LoadPackFile(string.Create(CultureInfo.InvariantCulture, $"{dir}/pak{i}.pak"));
            if (pak == null)
            {
                break;
            }

            com_searchpaths = new SearchPath { Pack = pak, Next = com_searchpaths };
        }
    }

    // common.c: COM_LoadPackFile
    // Takes an explicit (not game tree related) path to a pak file. Loads the header and directory.
    private Pack? LoadPackFile(string packfile)
    {
        string? path = HostPath.Resolve(OnHost(packfile));
        if (path == null || !File.Exists(path))
        {
            return null;
        }

        using var packhandle = File.OpenHandle(path);
        Span<byte> header = stackalloc byte[PackHeaderSize];
        RandomAccess.Read(packhandle, header, 0);
        if (header[0] != 'P' || header[1] != 'A' || header[2] != 'C' || header[3] != 'K')
        {
            throw new QuakeException($"{packfile} is not a packfile");
        }

        int dirofs = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        int dirlen = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);

        int numpackfiles = dirlen / PackFileSize;

        if (numpackfiles > MAX_FILES_IN_PACK)
        {
            throw new QuakeException($"{packfile} has {numpackfiles} files");
        }

        if (numpackfiles != PAK0_COUNT)
        {
            Modified = true; // not the original file
        }

        // the C reads the directory into a buffer of MAX_FILES_IN_PACK entries
        var info = new byte[MAX_FILES_IN_PACK * PackFileSize];
        int dirread = Math.Max(0, Math.Min(dirlen, info.Length));
        RandomAccess.Read(packhandle, info.AsSpan(0, dirread), dirofs);

        // crc the directory to check for modifications
        Crc.Init(out ushort crc);
        for (int i = 0; i < dirlen; i++)
        {
            Crc.ProcessByte(ref crc, info[i]);
        }

        if (crc != PAK0_CRC)
        {
            Modified = true;
        }

        // parse the directory
        var newfiles = new PackFile[Math.Max(0, numpackfiles)];
        for (int i = 0; i < newfiles.Length; i++)
        {
            ReadOnlySpan<byte> entry = info.AsSpan(i * PackFileSize, PackFileSize);
            newfiles[i] = new PackFile(
                CString(entry[..56]),
                BinaryPrimitives.ReadInt32LittleEndian(entry[56..]),
                BinaryPrimitives.ReadInt32LittleEndian(entry[60..]));
        }

        var pack = new Pack(packfile, path, newfiles);

        conPrintf($"Added packfile {packfile} ({numpackfiles} files)\n");
        return pack;
    }

    // common.c: COM_FindFile
    // Finds the file in the search path. Sets com_filesize and returns a stream positioned at the start of the file
    // (inside a pak file, reading on reads the files after it, as in C), or -1 and null.
    public int FindFile(string filename, out Stream? file)
    {
        // search through the path, one element at a time
        SearchPath? search = com_searchpaths;
        if (ProgHack)
        {
            // gross hack to use quake 1 progs with quake 2 maps
            if (filename == "progs.dat")
            {
                search = search!.Next;
            }
        }

        for (; search != null; search = search.Next)
        {
            // is the element a pak file?
            if (search.Pack != null)
            {
                // look through all the pak file elements
                Pack pak = search.Pack;
                foreach (PackFile entry in pak.Files)
                {
                    if (entry.Name == filename)
                    {
                        // found it!
                        sysPrintf($"PackFile: {pak.FileName} : {filename}\n");
                        var stream = new FileStream(pak.HostPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        stream.Seek(entry.FilePos, SeekOrigin.Begin);
                        file = stream;
                        FileSize = entry.FileLen;
                        return FileSize;
                    }
                }
            }
            else
            {
                // check a file in the directory tree
                if (!StaticRegistered)
                {
                    // if not a registered version, don't ever go beyond base
                    if (filename.Contains('/', StringComparison.Ordinal) || filename.Contains('\\', StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                string netpath = $"{search.FileName}/{filename}";
                string? path = UserDirFor(search.FileName) is string user ? HostPath.Resolve($"{user}/{filename}") : null;
                if (path == null || !File.Exists(path))
                {
                    path = HostPath.Resolve(OnHost(netpath));
                }

                if (path == null || !File.Exists(path))
                {
                    continue;
                }

                sysPrintf($"FindFile: {netpath}\n");
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                file = stream;
                FileSize = (int)stream.Length;
                return FileSize;
            }
        }

        sysPrintf($"FindFile: can't find {filename}\n");

        file = null;
        FileSize = -1;
        return -1;
    }

    // common.c: COM_LoadFile
    // Filenames are relative to the quake directory. Always appends a 0 byte. The hunk, temp, zone, cache and stack
    // variants (COM_LoadHunkFile, COM_LoadTempFile, COM_LoadCacheFile, COM_LoadStackFile) are all this.
    public byte[]? LoadFile(string path)
    {
        // look for it in the filesystem or pack files
        int len = FindFile(path, out Stream? h);
        if (h == null)
        {
            return null;
        }

        var buf = new byte[len + 1];

        BeginDisc?.Invoke();
        using (h)
        {
            h.ReadAtLeast(buf.AsSpan(0, len), len, throwOnEndOfStream: false);
        }

        EndDisc?.Invoke();

        return buf;
    }

    // common.c: COM_WriteFile
    // The filename will be prefixed by the current game directory.
    public void WriteFile(string filename, ReadOnlySpan<byte> data)
    {
        string name = $"{GameDir}/{filename}";

        FileStream handle;
        try
        {
            handle = new FileStream(WritePath(filename), FileMode.Create, FileAccess.Write);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            sysPrintf($"COM_WriteFile: failed on {name}\n");
            return;
        }

        sysPrintf($"COM_WriteFile: {name}\n");
        using (handle)
        {
            handle.Write(data);
        }
    }

    // common.c: COM_CheckRegistered
    // Looks for the pop.lmp file and verifies it. Returns whether the registered version is playing; the caller sets
    // the "registered" and "cmdline" cvars. Stops with an error if an alternate game was attempted to be started
    // without being registered.
    public bool CheckRegistered()
    {
        FindFile("gfx/pop.lmp", out Stream? h);
        StaticRegistered = false;

        if (h == null)
        {
            conPrintf("Playing shareware version.\n");
            if (Modified)
            {
                throw new QuakeException("You must have the registered version to use modified games");
            }

            return false;
        }

        // unsigned short check[128], read whole: inside a pak file a short pop.lmp reads on into the next file
        var check = new byte[256];
        using (h)
        {
            h.ReadAtLeast(check, check.Length, throwOnEndOfStream: false);
        }

        for (int i = 0; i < 128; i++)
        {
            if (pop[i] != BinaryPrimitives.ReadUInt16BigEndian(check.AsSpan(i * 2)))
            {
                throw new QuakeException("Corrupted data file.");
            }
        }

        StaticRegistered = true;
        conPrintf("Playing registered version.\n");
        return true;
    }

    // common.c: COM_Path_f
    public void Path_f()
    {
        conPrintf("Current search path:\n");
        for (SearchPath? s = com_searchpaths; s != null; s = s.Next)
        {
            if (s.Pack != null)
            {
                conPrintf($"{s.Pack.FileName} ({s.Pack.Files.Length} files)\n");
            }
            else
            {
                conPrintf($"{s.FileName}\n");
            }
        }
    }

    /// <summary>
    /// Every file name the search path can find, each once, in the order the search path finds them (for tools; the
    /// directories' files are listed with '/' separators).
    /// </summary>
    public IEnumerable<string> ListFiles()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (SearchPath? s = com_searchpaths; s != null; s = s.Next)
        {
            IEnumerable<string> names = s.Pack != null
                ? s.Pack.Files.Select(f => f.Name)
                : HostPath.Resolve(OnHost(s.FileName)) is string dir && Directory.Exists(dir)
                    ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                        .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
                    : [];
            foreach (string name in names)
            {
                if (seen.Add(name))
                {
                    yield return name;
                }
            }
        }
    }

    // A C string from a fixed-size field: up to its terminating 0, one character per byte.
    private static string CString(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? field : field[..end]);
    }

    // common.c: packfile_t
    private sealed record PackFile(string Name, int FilePos, int FileLen);

    // common.c: pack_t (filename as the engine named it; HostPath the file it found). The C keeps the file open;
    // each file found here opens its own stream.
    private sealed record Pack(string FileName, string HostPath, PackFile[] Files);

    // common.c: searchpath_t (only one of filename / pack will be used)
    private sealed class SearchPath
    {
        public string FileName { get; set; } = "";

        public Pack? Pack { get; set; }

        public SearchPath? Next { get; set; }
    }
}
