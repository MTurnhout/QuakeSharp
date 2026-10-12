using QuakeSharp.Core.Common;

namespace QuakeSharp.Core;

// common.c: the file system functions, through FileSystem
public sealed partial class QuakeEngine
{
    /// <summary>common.c: com_gamedir</summary>
    public string com_gamedir => fileSystem.GameDir;

    /// <summary>common.c: com_filesize</summary>
    public int com_filesize => fileSystem.FileSize;

    /// <summary>The file system (common.c's search path), for the code that reads files.</summary>
    public FileSystem FileSystem => fileSystem;

    // common.c: COM_LoadHunkFile, COM_LoadTempFile, COM_LoadStackFile (all the same here: a managed buffer with a 0
    // appended)
    public byte[]? COM_LoadHunkFile(string path) => fileSystem.LoadFile(path);

    public byte[]? COM_LoadTempFile(string path) => fileSystem.LoadFile(path);

    // common.c: COM_WriteFile
    public void COM_WriteFile(string filename, ReadOnlySpan<byte> data) => fileSystem.WriteFile(filename, data);
}
