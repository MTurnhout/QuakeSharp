namespace QuakeSharp.Core.Tests;

/// <summary>Finds files of the id Quake source in the reference/Quake submodule.</summary>
internal static class ReferenceSource
{
    public static string WinQuakeFile(string name) => Path.Combine(RepositoryRoot(), "reference", "Quake", "WinQuake", name);

    public static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QuakeSharp.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("QuakeSharp.slnx not found above the test directory");
    }
}
