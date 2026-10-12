namespace QuakeSharp.Core.Common;

/// <summary>
/// The file name helpers of common.c. Strings hold one byte per character (Latin-1), as the C strings do.
/// </summary>
public static class Com
{
    // common.c: COM_SkipPath
    public static string SkipPath(string pathname)
    {
        int last = 0;
        for (int i = 0; i < pathname.Length; i++)
        {
            if (pathname[i] == '/')
            {
                last = i + 1;
            }
        }

        return pathname[last..];
    }

    // common.c: COM_StripExtension
    public static string StripExtension(string @in)
    {
        int i = @in.IndexOf('.', StringComparison.Ordinal);
        return i < 0 ? @in : @in[..i];
    }

    // common.c: COM_FileExtension (at most 7 characters, after the first '.')
    public static string FileExtension(string @in)
    {
        int i = @in.IndexOf('.', StringComparison.Ordinal);
        if (i < 0)
        {
            return "";
        }

        string exten = @in[(i + 1)..];
        return exten.Length > 7 ? exten[..7] : exten;
    }

    // common.c: COM_FileBase
    public static string FileBase(string @in)
    {
        // s: the last '.', or the start; s2: the last '/' at or before it, or one before the start
        int s = @in.Length - 1;
        while (s > 0 && @in[s] != '.')
        {
            s--;
        }

        int s2 = s;
        while (s2 >= 0 && @in[s2] != '/')
        {
            s2--;
        }

        if (s - s2 < 2)
        {
            return "?model?";
        }

        s--;
        return @in.Substring(s2 + 1, s - s2);
    }

    // common.c: COM_DefaultExtension (extension should include the .)
    public static string DefaultExtension(string path, string extension)
    {
        // if path doesn't have a .EXT, append extension
        int src = path.Length - 1;
        while (src > 0 && path[src] != '/')
        {
            if (path[src] == '.')
            {
                return path; // it has an extension
            }

            src--;
        }

        return path + extension;
    }
}
