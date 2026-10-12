using System.Globalization;
using System.Numerics;
using System.Text;

namespace QuakeSharp.Core.Common;

/// <summary>An argument of <see cref="CFormat.Sprintf"/>: an int, a double or a string, as C's varargs carry them.</summary>
public readonly struct PrintfArg
{
    private readonly long integer;
    private readonly double real;
    private readonly string? text;
    private readonly Kind kind;

    private PrintfArg(long integer, double real, string? text, Kind kind)
    {
        this.integer = integer;
        this.real = real;
        this.text = text;
        this.kind = kind;
    }

    private enum Kind
    {
        Integer,
        Real,
        Text,
    }

    public static implicit operator PrintfArg(int value) => new(value, 0, null, Kind.Integer);

    public static implicit operator PrintfArg(uint value) => new(value, 0, null, Kind.Integer);

    public static implicit operator PrintfArg(char value) => new(value, 0, null, Kind.Integer);

    // a float argument is promoted to double, as in C
    public static implicit operator PrintfArg(double value) => new(0, value, null, Kind.Real);

    public static implicit operator PrintfArg(string? value) => new(0, 0, value, Kind.Text);

    internal int Int => kind == Kind.Integer ? (int)integer : throw new FormatException("printf: an integer was expected");

    internal double Double => kind == Kind.Real ? real : throw new FormatException("printf: a double was expected");

    internal string? String => kind == Kind.Text ? text : throw new FormatException("printf: a string was expected");
}

/// <summary>
/// The C library's sprintf, for the conversions Quake uses (d i u x X c s f p and %%, with the flags - + space 0 #,
/// width and precision, * included), giving what the Microsoft C runtime gives: %f exactly rounded from the double's
/// exact value, halfway cases to even. Strings hold one byte per character (Latin-1).
/// </summary>
public static class CFormat
{
    public static string Sprintf(string format, params ReadOnlySpan<PrintfArg> args)
    {
        var sb = new StringBuilder(format.Length + 16);
        int arg = 0;
        int i = 0;
        while (i < format.Length)
        {
            char ch = format[i++];
            if (ch != '%')
            {
                sb.Append(ch);
                continue;
            }

            if (i < format.Length && format[i] == '%')
            {
                sb.Append('%');
                i++;
                continue;
            }

            // flags
            bool left = false, plus = false, space = false, zero = false, alternate = false;
            for (; i < format.Length; i++)
            {
                switch (format[i])
                {
                    case '-': left = true; continue;
                    case '+': plus = true; continue;
                    case ' ': space = true; continue;
                    case '0': zero = true; continue;
                    case '#': alternate = true; continue;
                }

                break;
            }

            // width
            int width = 0;
            if (i < format.Length && format[i] == '*')
            {
                width = args[arg++].Int;
                if (width < 0)
                {
                    left = true;
                    width = -width;
                }

                i++;
            }
            else
            {
                while (i < format.Length && char.IsAsciiDigit(format[i]))
                {
                    width = (width * 10) + (format[i++] - '0');
                }
            }

            // precision
            int precision = -1;
            if (i < format.Length && format[i] == '.')
            {
                i++;
                precision = 0;
                if (i < format.Length && format[i] == '*')
                {
                    precision = args[arg++].Int;
                    i++;
                }
                else
                {
                    while (i < format.Length && char.IsAsciiDigit(format[i]))
                    {
                        precision = (precision * 10) + (format[i++] - '0');
                    }
                }
            }

            // length modifiers change nothing for these arguments
            while (i < format.Length && format[i] is 'h' or 'l' or 'L')
            {
                i++;
            }

            if (i >= format.Length)
            {
                break;
            }

            char conversion = format[i++];
            string body;
            string sign = "";
            switch (conversion)
            {
                case 'd':
                case 'i':
                {
                    long v = args[arg++].Int;
                    sign = v < 0 ? "-" : plus ? "+" : space ? " " : "";
                    body = Digits((ulong)Math.Abs(v), 10, false, precision);
                    break;
                }

                case 'u':
                    body = Digits((uint)args[arg++].Int, 10, false, precision);
                    break;

                case 'x':
                case 'X':
                {
                    uint v = (uint)args[arg++].Int;
                    body = Digits(v, 16, conversion == 'X', precision);
                    if (alternate && v != 0)
                    {
                        sign = conversion == 'X' ? "0X" : "0x";
                    }

                    break;
                }

                case 'p':
                    // a 32-bit pointer: eight upper case hex digits
                    body = ((uint)args[arg++].Int).ToString("X8", CultureInfo.InvariantCulture);
                    break;

                case 'c':
                    body = ((char)(byte)args[arg++].Int).ToString();
                    zero = false;
                    break;

                case 's':
                {
                    string s = args[arg++].String ?? "(null)";
                    int end = s.IndexOf('\0', StringComparison.Ordinal);
                    if (end >= 0)
                    {
                        s = s[..end];
                    }

                    body = precision >= 0 && precision < s.Length ? s[..precision] : s;
                    zero = false;
                    break;
                }

                case 'f':
                case 'F':
                {
                    double v = args[arg++].Double;
                    sign = double.IsNegative(v) ? "-" : plus ? "+" : space ? " " : "";
                    if (double.IsNaN(v))
                    {
                        // the C runtime's default NaN (0/0) has its sign bit set
                        body = double.IsNegative(v) ? "nan(ind)" : "nan";
                        zero = false;
                    }
                    else if (double.IsInfinity(v))
                    {
                        body = "inf";
                        zero = false;
                    }
                    else
                    {
                        body = Fixed(Math.Abs(v), precision < 0 ? 6 : precision, alternate);
                    }

                    break;
                }

                default:
                    throw new FormatException($"printf: %{conversion} is not supported");
            }

            // precision for integers turns off the 0 flag, as in C
            if (precision >= 0 && conversion is 'd' or 'i' or 'u' or 'x' or 'X')
            {
                zero = false;
            }

            int padding = width - sign.Length - body.Length;
            if (padding <= 0)
            {
                sb.Append(sign).Append(body);
            }
            else if (left)
            {
                sb.Append(sign).Append(body).Append(' ', padding);
            }
            else if (zero)
            {
                sb.Append(sign).Append('0', padding).Append(body);
            }
            else
            {
                sb.Append(' ', padding).Append(sign).Append(body);
            }
        }

        return sb.ToString();
    }

    private static string Digits(ulong v, int radix, bool upper, int precision)
    {
        string s = radix == 16
            ? v.ToString(upper ? "X" : "x", CultureInfo.InvariantCulture)
            : v.ToString(CultureInfo.InvariantCulture);
        if (precision == 0 && v == 0)
        {
            return "";
        }

        return precision > s.Length ? new string('0', precision - s.Length) + s : s;
    }

    // %f of a finite, non-negative double: the exact value rounded to precision decimals, halfway to even
    private static string Fixed(double v, int precision, bool alternate)
    {
        long bits = BitConverter.DoubleToInt64Bits(v);
        int exponent = (int)((bits >> 52) & 0x7ff);
        long mantissa = bits & 0xfffffffffffff;
        if (exponent == 0)
        {
            exponent = 1; // subnormal
        }
        else
        {
            mantissa |= 1L << 52;
        }

        // v = mantissa * 2^shift
        int shift = exponent - 1075;
        BigInteger scaled = new BigInteger(mantissa) * BigInteger.Pow(10, precision);
        BigInteger digits;
        if (shift >= 0)
        {
            digits = scaled << shift;
        }
        else
        {
            digits = scaled >> -shift;
            BigInteger remainder = scaled - (digits << -shift);
            BigInteger half = BigInteger.One << (-shift - 1);
            int compare = (remainder - half).Sign;
            if (compare > 0 || (compare == 0 && !digits.IsEven))
            {
                digits += 1;
            }
        }

        string s = digits.ToString(CultureInfo.InvariantCulture);
        if (precision == 0)
        {
            return alternate ? s + "." : s;
        }

        if (s.Length <= precision)
        {
            s = new string('0', precision - s.Length + 1) + s;
        }

        return string.Concat(s.AsSpan(0, s.Length - precision), ".", s.AsSpan(s.Length - precision));
    }
}
