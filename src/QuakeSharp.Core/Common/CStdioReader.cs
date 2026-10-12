using System.Globalization;

namespace QuakeSharp.Core.Common;

/// <summary>
/// A file opened with fopen (name, "r") as the Microsoft C runtime reads it in text mode (CR LF becomes LF, Ctrl-Z ends
/// the file), with the fgetc, feof and fscanf conversions host_cmd.c's Host_Loadgame_f uses (%i, %s, %f and white
/// space).
/// </summary>
/// <remarks>
/// The numeric conversions (%f, %i) read each character into a signed char's int, so a 0xff byte is EOF to them: a number
/// ends there (or fails, if it is the first character after the white space), and pushing it back does nothing, so it
/// is taken. %s, white space and fgetc take it as the character 255.
/// </remarks>
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

    // a 0xff byte, which the numeric conversions take for EOF
    private bool AtFF => pos < data.Length && data[pos] == 0xff;

    // a numeric conversion stops at the next character and pushes it back: at the end of the file that sets the
    // end-of-file flag, and a 0xff is taken
    private void NumberEnd()
    {
        if (Peek() < 0)
        {
            eof = true;
        }
        else if (AtFF)
        {
            Advance();
        }
    }

    // the white space before a numeric conversion; false if the file ends there or a 0xff follows (taken), which fails
    // the conversion
    private bool NumberStart()
    {
        SkipWhite();
        if (Peek() < 0)
        {
            return false;
        }

        if (AtFF)
        {
            Advance();
            return false;
        }

        return true;
    }

    // white space, up to the next character
    private void SkipWhite()
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

    /// <summary>A white-space directive of a format ("\n"): any white space, none needed.</summary>
    public void SkipSpace() => SkipWhite();

    /// <summary>fscanf (f, "%i\n", &amp;v): v is assigned and the white space after it read only if a number is
    /// read.</summary>
    public void Scanf(ref int v)
    {
        if (ScanInt() is int value)
        {
            v = value;
            SkipSpace();
        }
    }

    /// <summary>fscanf (f, "%f\n", &amp;v) into a float.</summary>
    public void Scanf(ref float v)
    {
        if (ScanFloat() is float value)
        {
            v = value;
            SkipSpace();
        }
    }

    /// <summary>fscanf (f, "%s\n", v).</summary>
    public void Scanf(ref string v)
    {
        if (ScanString() is string value)
        {
            v = value;
            SkipSpace();
        }
    }

    /// <summary>%s (%79s with a width): white space, then the characters up to the next white space, at most
    /// <paramref name="width"/>; null at the end of the file.</summary>
    public string? ScanString(int width = int.MaxValue)
    {
        SkipWhite();
        if (Peek() < 0)
        {
            return null;
        }

        var sb = new System.Text.StringBuilder();
        int c;
        while (sb.Length < width && (c = Peek()) >= 0 && !IsSpace(c))
        {
            sb.Append((char)c);
            Advance();
        }

        if (sb.Length < width && Peek() < 0)
        {
            eof = true;
        }

        return sb.ToString();
    }

    /// <summary>%i: white space, a sign, then a decimal, octal (0...) or hexadecimal (0x and a digit at least) number,
    /// read as the Universal CRT reads it: into a 64-bit value that saturates when it overflows, of which the int gets
    /// the low 32 bits; null if none.</summary>
    public int? ScanInt()
    {
        if (!NumberStart())
        {
            return null;
        }

        bool negative = false;
        if (Peek() is '-' or '+')
        {
            negative = Peek() == '-';
            Advance();
        }

        int radix = 10;
        ulong value = 0;
        bool overflow = false;
        int digits = 0;
        if (Peek() == '0')
        {
            Advance();
            radix = 8;
            digits++;
            if (AtFF)
            {
                Advance(); // read to look for an x, and lost
            }
            else if ((Peek() | 0x20) == 'x')
            {
                Advance();
                radix = 16;
                digits--;
            }
        }

        while (true)
        {
            int d = Digit(Peek());
            if (d >= radix)
            {
                break;
            }

            if (value > (ulong.MaxValue - (ulong)d) / (ulong)radix)
            {
                overflow = true;
            }
            else
            {
                value = (value * (ulong)radix) + (ulong)d;
            }

            digits++;
            Advance();
        }

        NumberEnd();
        if (digits == 0)
        {
            return null;
        }

        long result = negative
            ? overflow || value > 1UL << 63 ? long.MinValue : unchecked(-(long)value)
            : overflow || value > long.MaxValue ? long.MaxValue : (long)value;
        return unchecked((int)result);
    }

    /// <summary>%f: white space, a sign, then decimal digits with a point and an "e" exponent, or "0x" and hexadecimal
    /// digits with a point and a "p" exponent, as the Universal CRT reads them: a digit at least, and an exponent with
    /// digits (an "e" or "p" without them fails the conversion); correctly rounded to a float (a hexadecimal number
    /// through a double: the same unless it has more than 53 significant bits); null if none.</summary>
    public float? ScanFloat()
    {
        if (!NumberStart())
        {
            return null;
        }

        var sb = new System.Text.StringBuilder();
        bool negative = false;
        bool hex = false;
        int c;
        if ((c = Peek()) is '-' or '+')
        {
            negative = c == '-';
            Advance();
        }

        int digits = 0;
        if (Peek() == '0')
        {
            Advance();
            if (AtFF)
            {
                Advance(); // read to look for an x, and lost
                sb.Append('0');
                digits++;
            }
            else if ((Peek() | 0x20) == 'x')
            {
                Advance();
                hex = true;
            }
            else
            {
                sb.Append('0');
                digits++;
            }
        }

        int radix = hex ? 16 : 10;
        while (Digit(c = Peek()) < radix)
        {
            sb.Append((char)c);
            Advance();
            digits++;
        }

        if (Peek() == '.')
        {
            sb.Append('.');
            Advance();
            while (Digit(c = Peek()) < radix)
            {
                sb.Append((char)c);
                Advance();
                digits++;
            }
        }

        bool valid = digits > 0;
        if (valid && (Peek() | 0x20) == (hex ? 'p' : 'e'))
        {
            sb.Append(hex ? 'p' : 'e');
            Advance();
            if ((c = Peek()) is '-' or '+')
            {
                sb.Append((char)c);
                Advance();
            }

            int exponentDigits = 0;
            while ((c = Peek()) is >= '0' and <= '9')
            {
                sb.Append((char)c);
                Advance();
                exponentDigits++;
            }

            valid = exponentDigits > 0;
        }

        bool ff = AtFF;
        NumberEnd();
        if (ff && AtFF)
        {
            Advance(); // the float's parser reads one character more after a 0xff, and loses it too if it is one
        }

        if (!valid)
        {
            return null;
        }

        float value = hex
            ? (float)QString.HexFloat(sb.ToString(), 0)
            : float.Parse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);
        return negative ? -value : value;
    }

    // a digit's value in bases up to 16, 99 for anything else
    private static int Digit(int c) =>
        c is >= '0' and <= '9' ? c - '0' : (c | 0x20) is >= 'a' and <= 'f' ? (c | 0x20) - 'a' + 10 : 99;
}
