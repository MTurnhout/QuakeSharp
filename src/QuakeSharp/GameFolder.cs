using QuakeSharp.Core;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.GameData;

/// <summary>
/// Where the game's files are (design.md §11.10, decision 78). As WinQuake: the current folder, or -basedir's. When the
/// current folder has no id1/, the executable's own folder if it has one (so a double-click works from anywhere), and
/// otherwise the shareware: id's untouched quake106.zip beside the executable (a release's), its pak0.pak taken out the
/// first time into the user folder's <c>shareware</c>.
/// </summary>
internal static class GameFolder
{
    /// <summary>
    /// The user folder (design.md §9): %APPDATA%/QuakeSharp, ~/.config/QuakeSharp on Linux. The application data folder
    /// is made if it is missing (.NET otherwise gives an empty path for it, and the user folder would be one in the current
    /// folder).
    /// </summary>
    public static string UserFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
            "QuakeSharp");

    public static string Find(CommandLine commandLine, string userdir, Action<string> print)
    {
        if (commandLine.CheckParm("-basedir") != 0 || Directory.Exists("id1"))
        {
            return ".";
        }

        string exeDir = AppContext.BaseDirectory;
        if (Directory.Exists(Path.Combine(exeDir, "id1")))
        {
            return exeDir;
        }

        string zip = Path.Combine(exeDir, SharewareInstaller.ZipName);
        if (!File.Exists(zip))
        {
            return ".";
        }

        string shareware = Path.Combine(userdir, "shareware");
        if (!File.Exists(Path.Combine(shareware, "id1", "pak0.pak")))
        {
            print($"Unpacking the shareware's pak0.pak from {zip} into {shareware}\n");
            try
            {
                SharewareInstaller.Install(zip, shareware);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                throw new QuakeException($"The shareware could not be unpacked from {zip}: {e.Message}");
            }
        }

        return shareware;
    }
}
