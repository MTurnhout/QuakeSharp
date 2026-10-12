using System.Globalization;

namespace QuakeSharp.Core.Common;

/// <summary>
/// A file opened with fopen (name, "r") as the Microsoft C runtime reads it in text mode (CR LF becomes LF, Ctrl-Z ends
/// the file), with the fgetc, feof and fscanf conversions host_cmd.c's Host_Loadgame_f uses (%i, %s, %f and white
/// space).
/// </summary>
public sealed class CStdioReader
{
    private readonly byte[] data;
    private int pos;
    private bool eof;

    public CStdioReader(byte[] data)
    {
        this.data = data;
    }

    // the next character without taking it, -1 at the end
    private int Peek()
    {
        if (pos >= data.Length || data[pos] == 0x1a)
        {
            return -1;
        }

        if (data[pos] == '\r' && pos + 1 < data.Length && data[pos + 1] == '\n')
        {
            return '\n';
        }

        return data[pos];
    }

    private void Advance() => pos += data[pos] == '\r' && pos + 1 < data.Length && data[pos + 1] == '\n' ? 2 : 1;

    /// <summary>fgetc: the next character, or -1 (EOF), which sets the end-of-file flag.</summary>
    public int Getc()
    {
        int c = Peek();
        if (c < 0)
        {
            eof = true;
            return -1;
        }

        Advance();
        return c;
    }

    /// <summary>feof: whether a read has reached the end.</summary>
    public bool Feof => eof;

    private static bool IsSpace(int c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    /// <summary>A white-space directive of a format ("\n"): any white space, none needed.</summary>
    public void SkipSpace()
    {
        int c;
        while ((c = Peek()) >= 0 && IsSpace(c))
        {
            Advance();
        }

        if (c < 0)
        {
            eof = true;
        }
    }

    /// <summary>%s: white space, then the characters up to the next white space; null at the end of the file.</summary>
    public string? ScanString()
    {
        SkipSpace();
        if (Peek() < 0)
        {
            return null;
        }

        var sb = new System.Text.StringBuilder();
        int c;
        while ((c = Peek()) >= 0 && !IsSpace(c))
        {
            sb.Append((char)c);
            Advance();
        }

        if (c < 0)
        {
            eof = true;
        }

        return sb.ToString();
    }

    /// <summary>%i: white space, a sign, then a decimal, octal (0...) or hexadecimal (0x...) number; null if none.</summary>
    public int? ScanInt()
    {
        SkipSpace();
        var sb = new System.Text.StringBuilder();
        bool negative = false;
        if (Peek() is '-' or '+')
        {
            negative = Peek() == '-';
            Advance();
        }

        int radix = 10;
        long value = 0;
        int digits = 0;
        if (Peek() == '0')
        {
            Advance();
            digits++;
            radix = 8;
            if ((Peek() | 0x20) == 'x')
            {
                Advance();
                radix = 16;
            }
        }

        while (true)
        {
            int c = Peek();
            int d = c is >= '0' and <= '9' ? c - '0' : (c | 0x20) is >= 'a' and <= 'f' ? (c | 0x20) - 'a' + 10 : 99;
            if (d >= radix)
            {
                break;
            }

            value = unchecked((value * radix) + d);
            digits++;
            Advance();
        }

        if (Peek() < 0)
        {
            eof = true;
        }

        if (digits == 0)
        {
            return null;
        }

        return unchecked((int)(negative ? -value : value));
    }

    /// <summary>%f: white space, then the longest prefix of the characters that is a floating point number,
    /// correctly rounded to a float; null if none.</summary>
    public float? ScanFloat()
    {
        SkipSpace();
        var sb = new System.Text.StringBuilder();
        int c;
        if ((c = Peek()) is '-' or '+')
        {
            sb.Append((char)c);
            Advance();
        }

        int digits = 0;
        while ((c = Peek()) is >= '0' and <= '9')
        {
            sb.Append((char)c);
            Advance();
            digits++;
        }

        if (Peek() == '.')
        {
            sb.Append('.');
            Advance();
            while ((c = Peek()) is >= '0' and <= '9')
            {
                sb.Append((char)c);
                Advance();
                digits++;
            }
        }

        if (digits == 0)
        {
            return null;
        }

        if ((Peek() | 0x20) == 'e')
        {
            sb.Append('e');
            Advance();
            if ((c = Peek()) is '-' or '+')
            {
                sb.Append((char)c);
                Advance();
            }

            while ((c = Peek()) is >= '0' and <= '9')
            {
                sb.Append((char)c);
                Advance();
            }
        }

        if (Peek() < 0)
        {
            eof = true;
        }

        string s = sb.ToString();
        if (s.EndsWith('e') || s.EndsWith("e-", StringComparison.Ordinal) || s.EndsWith("e+", StringComparison.Ordinal))
        {
            s = s[..s.LastIndexOf('e')];
        }

        return float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
