using System.Runtime.InteropServices;

/// <summary>
/// The console on Windows (design.md §11.10): the executable is a GUI program (WinExe), so a double-click opens the game's
/// window alone, without a console before it. Its text still reaches a console where there is one to read it: a
/// terminal it was started from (attached to), or a dedicated server's own console window (made for it, as it reads
/// commands typed at it), unless its input and output go to pipes or files, which need nothing.
/// </summary>
internal static partial class WindowsConsole
{
    private const int AttachParentProcess = -1;
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const uint FileTypeChar = 2;

    public static void Prepare(bool dedicated)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        bool redirected = IsRedirected(StdOutputHandle) && (!dedicated || IsRedirected(StdInputHandle));
        if (redirected)
        {
            return;
        }

        // a dedicated server reads what is typed at it, so it has a console of its own (sharing a terminal's would have
        // the terminal's shell read the same keys); anything else writes into the terminal it was started from, if any
        if (dedicated ? AllocConsole() : AttachConsole(AttachParentProcess))
        {
            var output = new StreamWriter(new FileStream("CONOUT$", FileMode.Open, FileAccess.Write)) { AutoFlush = true };
            Console.SetOut(output);
            Console.SetError(output);
        }
    }

    // a standard handle that is a pipe or a file (a GUI program started from a console has none)
    private static bool IsRedirected(int which)
    {
        nint handle = GetStdHandle(which);
        return handle != 0 && handle != -1 && GetFileType(handle) is not (0 or FileTypeChar);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int which);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetFileType(nint handle);
}
