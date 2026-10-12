using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace QuakeSharp.Core.Diagnostics;

/// <summary>
/// Compares the files a run wrote with the oracle's (design.md §11.3): the same names, the same bytes. A difference is
/// named where it helps to find it: a text file's line (config.cfg, savegames, qconsole.log), a demo's message block,
/// a PCX file's header, pixels or palette.
/// </summary>
public static class FileCompare
{
    /// <summary>The differences between two folders' files (subfolders included), each a line; none if they are
    /// the same.</summary>
    public static IReadOnlyList<string> Folders(string theirs, string ours)
    {
        var differences = new List<string>();
        SortedSet<string> theirNames = Names(theirs);
        SortedSet<string> ourNames = Names(ours);
        foreach (string name in theirNames.Except(ourNames, StringComparer.Ordinal))
        {
            differences.Add($"{name}: only the oracle's run wrote it");
        }

        foreach (string name in ourNames.Except(theirNames, StringComparer.Ordinal))
        {
            differences.Add($"{name}: only QuakeSharp's run wrote it");
        }

        foreach (string name in theirNames.Intersect(ourNames, StringComparer.Ordinal))
        {
            string? difference = Files(name, File.ReadAllBytes(Path.Combine(theirs, name)), File.ReadAllBytes(Path.Combine(ours, name)));
            if (difference != null)
            {
                differences.Add($"{name}: {difference}");
            }
        }

        return differences;
    }

    /// <summary>Where two versions of a file first differ, or null if they are the same.</summary>
    public static string? Files(string name, byte[] theirs, byte[] ours)
    {
        int length = Math.Min(theirs.Length, ours.Length);
        int at = 0;
        while (at < length && theirs[at] == ours[at])
        {
            at++;
        }

        if (at == length && theirs.Length == ours.Length)
        {
            return null;
        }

        string where = Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".dem" => DemoPlace(theirs, at),
            ".pcx" => at < 128 ? "the header" : at >= theirs.Length - 769 ? "the palette" : "the pixels",
            _ => TextPlace(theirs, ours, at),
        };
        return string.Create(CultureInfo.InvariantCulture,
            $"{theirs.Length} bytes against {ours.Length}, first difference at byte {at}, {where}");
    }

    private static SortedSet<string> Names(string folder) =>
        Directory.Exists(folder)
            ? new SortedSet<string>(
                Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')),
                StringComparer.Ordinal)
            : new SortedSet<string>(StringComparer.Ordinal);

    // a text file's line, both versions of it
    private static string TextPlace(byte[] theirs, byte[] ours, int at)
    {
        int line = 1;
        int start = 0;
        for (int i = 0; i < at && i < theirs.Length; i++)
        {
            if (theirs[i] == '\n')
            {
                line++;
                start = i + 1;
            }
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"line {line}: the oracle's \"{Line(theirs, start)}\", QuakeSharp's \"{Line(ours, start)}\"");
    }

    private static string Line(byte[] data, int start)
    {
        var sb = new StringBuilder();
        for (int i = start; i < data.Length && i < start + 200; i++)
        {
            byte b = data[i];
            if (b == '\n')
            {
                break;
            }

            sb.Append(b is >= 32 and < 127 and not (byte)'\\' ? ((char)b).ToString() : $"\\x{b:x2}");
        }

        return sb.ToString();
    }

    // a demo's place (cl_demo.c: the CD track as text and a line end, then blocks of the message's length, the view
    // angles and the message)
    private static string DemoPlace(byte[] demo, int at)
    {
        int p = Array.IndexOf(demo, (byte)'\n') + 1;
        if (p == 0 || at < p)
        {
            return "the CD track line";
        }

        for (int block = 0; p + 16 <= demo.Length; block++)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(demo.AsSpan(p));
            if (size < 0)
            {
                return string.Create(CultureInfo.InvariantCulture, $"message block {block} (from byte {p}): a size of {size}");
            }

            if (at < p + 16 + size || p + 16 + size > demo.Length)
            {
                string part = at < p + 4 ? "its size" : at < p + 16 ? "its view angles" : $"byte {at - p - 16} of the message";
                return string.Create(CultureInfo.InvariantCulture, $"message block {block} (from byte {p}, {size} bytes): {part}");
            }

            p += 16 + size;
        }

        return "after the last whole block";
    }
}
