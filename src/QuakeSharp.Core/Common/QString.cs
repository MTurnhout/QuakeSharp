namespace QuakeSharp.Core.Common;

/// <summary>
/// The string functions of common.c. A string is read as the C reads its char *: up to its end (or an embedded 0),
/// each character a signed char.
/// </summary>
public static class QString
{
    // common.c: Q_atoi
    public static int Q_atoi(string str)
    {
        int val;
        int sign;
        int c;
        int p = 0;

        if (Ch(str, p) == '-')
        {
            sign = -1;
            p++;
        }
        else
        {
            sign = 1;
        }

        val = 0;

        // check for hex
        if (Ch(str, p) == '0' && (Ch(str, p + 1) == 'x' || Ch(str, p + 1) == 'X'))
        {
            p += 2;
            while (true)
            {
                c = Ch(str, p++);
                if (c is >= '0' and <= '9')
                {
                    val = (val << 4) + c - '0';
                }
                else if (c is >= 'a' and <= 'f')
                {
                    val = (val << 4) + c - 'a' + 10;
                }
                else if (c is >= 'A' and <= 'F')
                {
                    val = (val << 4) + c - 'A' + 10;
                }
                else
                {
                    return val * sign;
                }
            }
        }

        // check for character
        if (Ch(str, p) == '\'')
        {
            return sign * Ch(str, p + 1);
        }

        // assume decimal
        while (true)
        {
            c = Ch(str, p++);
            if (c is < '0' or > '9')
            {
                return val * sign;
            }

            val = (val * 10) + c - '0';
        }
    }

    // The C runtime's atoi (some of Quake's code calls it rather than Q_atoi): white space, a sign, decimal digits; the
    // Microsoft C runtime gives INT_MAX or INT_MIN when the number is out of range.
    public static int atoi(string str)
    {
        int p = 0;
        long val = 0;
        bool negative = false;

        while (Ch(str, p) is ' ' or '\t' or '\n' or '\v' or '\f' or '\r')
        {
            p++;
        }

        if (Ch(str, p) is '-' or '+')
        {
            negative = Ch(str, p) == '-';
            p++;
        }

        while (Ch(str, p) is >= '0' and <= '9')
        {
            val = (val * 10) + (Ch(str, p) - '0');
            if (val > 1L << 32)
            {
                val = 1L << 32; // out of range either way
            }

            p++;
        }

        if (negative)
        {
            val = -val;
        }

        return val > int.MaxValue ? int.MaxValue : val < int.MinValue ? int.MinValue : (int)val;
    }

    // common.c: Q_atof
    public static float Q_atof(string str)
    {
        double val;
        int sign;
        int c;
        int @decimal, total;
        int p = 0;

        if (Ch(str, p) == '-')
        {
            sign = -1;
            p++;
        }
        else
        {
            sign = 1;
        }

        val = 0;

        // check for hex
        if (Ch(str, p) == '0' && (Ch(str, p + 1) == 'x' || Ch(str, p + 1) == 'X'))
        {
            p += 2;
            while (true)
            {
                c = Ch(str, p++);
                if (c is >= '0' and <= '9')
                {
                    val = (val * 16) + c - '0';
                }
                else if (c is >= 'a' and <= 'f')
                {
                    val = (val * 16) + c - 'a' + 10;
                }
                else if (c is >= 'A' and <= 'F')
                {
                    val = (val * 16) + c - 'A' + 10;
                }
                else
                {
                    return (float)(val * sign);
                }
            }
        }

        // check for character
        if (Ch(str, p) == '\'')
        {
            return sign * Ch(str, p + 1);
        }

        // assume decimal
        @decimal = -1;
        total = 0;
        while (true)
        {
            c = Ch(str, p++);
            if (c == '.')
            {
                @decimal = total;
                continue;
            }

            if (c is < '0' or > '9')
            {
                break;
            }

            val = (val * 10) + c - '0';
            total++;
        }

        if (@decimal == -1)
        {
            return (float)(val * sign);
        }

        while (total > @decimal)
        {
            val /= 10;
            total--;
        }

        return (float)(val * sign);
    }

    // common.c: Q_strcmp (0 if the strings are equal, -1 if not)
    public static int Q_strcmp(string s1, string s2)
    {
        for (int i = 0; ; i++)
        {
            if (Ch(s1, i) != Ch(s2, i))
            {
                return -1; // strings not equal
            }

            if (Ch(s1, i) == 0)
            {
                return 0; // strings are equal
            }
        }
    }

    // common.c: Q_strncmp
    public static int Q_strncmp(string s1, string s2, int count)
    {
        for (int i = 0; ; i++)
        {
            if (count-- == 0)
            {
                return 0;
            }

            if (Ch(s1, i) != Ch(s2, i))
            {
                return -1; // strings not equal
            }

            if (Ch(s1, i) == 0)
            {
                return 0; // strings are equal
            }
        }
    }

    // common.c: Q_strncasecmp
    public static int Q_strncasecmp(string s1, string s2, int n)
    {
        int c1, c2;

        for (int i = 0; ; i++)
        {
            c1 = Ch(s1, i);
            c2 = Ch(s2, i);

            if (n-- == 0)
            {
                return 0; // strings are equal until end point
            }

            if (c1 != c2)
            {
                if (c1 is >= 'a' and <= 'z')
                {
                    c1 -= 'a' - 'A';
                }

                if (c2 is >= 'a' and <= 'z')
                {
                    c2 -= 'a' - 'A';
                }

                if (c1 != c2)
                {
                    return -1; // strings not equal
                }
            }

            if (c1 == 0)
            {
                return 0; // strings are equal
            }
        }
    }

    // common.c: Q_strcasecmp
    public static int Q_strcasecmp(string s1, string s2) => Q_strncasecmp(s1, s2, 99999);

    // common.c: Q_strlen (up to an embedded 0)
    public static int Q_strlen(string str)
    {
        int end = str.IndexOf('\0', StringComparison.Ordinal);
        return end < 0 ? str.Length : end;
    }

    /// <summary>The C string's character at <paramref name="i"/> as a signed char: 0 at and after its end.</summary>
    public static int Ch(string s, int i)
    {
        if (i >= s.Length)
        {
            return 0;
        }

        return (sbyte)s[i];
    }
}
