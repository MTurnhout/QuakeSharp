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
