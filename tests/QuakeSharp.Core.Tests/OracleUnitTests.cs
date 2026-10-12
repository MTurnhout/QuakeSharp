using System.Globalization;
using System.Text;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;

namespace QuakeSharp.Core.Tests;

/// <summary>
/// The ported functions against WinQuake's own C: the same input sets as tools/oracle/src/units.c, whose output
/// (quake-oracle.exe -oracleunits) gives the expected hashes. "quake-oracle -oracleunits &lt;set&gt; &lt;n&gt;" prints the first
/// results of a set that differs.
/// </summary>
public class OracleUnitTests
{
    private const int Count = 20000;

    // units.c: sets, in order (the seed of a set is its index + 1), and the hashes quake-oracle -oracleunits prints
    private static readonly (string Name, string Hash, Action<Units> Run)[] Sets =
    [
        ("atoi", "5afdb9dd2b701d8e", u => u.Int(QString.Q_atoi(u.SomeString()))),
        ("atof", "876ad08c784adaba", u => u.Float(QString.Q_atof(u.SomeString()))),
        ("printf.float", "ba47a133b950f742", PrintfFloat),
        ("printf.int", "39f57600baad4242", PrintfInt),
        ("printf.string", "c0afb2cd6377ab57", PrintfString),
        ("anglemod", "41302c9b4dd2e828", u => u.Float(MathLib.anglemod(u.SomeFloat(100000)))),
        ("AngleVectors", "63db712d1d79f9a8", AngleVectors),
        ("VectorNormalize", "c56c6e29f6f95bf7", VectorNormalize),
        ("Length", "cbd279b57d7d279b", u => u.Float(MathLib.Length(u.SomeVec(4096)))),
        ("vectors", "9d751de32e351b34", Vectors),
        ("R_ConcatRotations", "ab84914b9e185ece", ConcatRotations),
        ("R_ConcatTransforms", "7f2a152175d9fa34", ConcatTransforms),
        ("FloorDivMod", "dfd59d4eec53b7bf", FloorDivMod),
        ("GreatestCommonDivisor", "45760a23fcd9dd6e", GreatestCommonDivisor),
        ("Invert24To16", "cb742417c927b8e0", u => u.Int(MathLib.Invert24To16(u.Below(2) != 0 ? (int)(u.Next() >> 33) : u.Below(1 << 20)))),
        ("Q_log2", "972d9e14ed4332bf", u => u.Int(MathLib.Q_log2(u.Below(2) != 0 ? (int)(u.Next() >> 33) : u.Below(5000)))),
        ("RotatePointAroundVector", "d13df261af18b47c", RotatePointAroundVector),
        ("BoxOnPlaneSide", "f9ab88f7f2b15944", BoxOnPlaneSide),
        ("crt_atof", "4b410ca668a6bba2", CrtAtof),
        ("scanf.float", "648c59f6317aa2b0", ScanfFloat),
        ("scanf.int", "42a25adaaddfc029", ScanfInt),
        ("scanf.string", "5310f81c32f1225f", ScanfString),
    ];

    private static readonly string[] FloatFormats = ["%f", "%5.1f", "%4.2f", "%4.1f", "%2.2f", "%3.1f", "%.0f", "%10.4f"];
    private static readonly string[] IntFormats = ["%i", "%d", "%3i", "%5d", "%8i", "%02i", "%2u", "%-2u", "%u", "%x", "%02x", "%c", "% i", "%-5i"];
    private static readonly string[] StringFormats = ["%s", "%-15.15s", "%-16.16s", "%8s", "%12s", "%3.1s"];

    public static TheoryData<string> SetNames => [.. Sets.Select(s => s.Name)];

    [Theory]
    [MemberData(nameof(SetNames))]
    public void MatchesWinQuake(string name)
    {
        int index = Array.FindIndex(Sets, s => s.Name == name);
        var units = new Units((ulong)index + 1);
        for (int i = 0; i < Count; i++)
        {
            Sets[index].Run(units);
        }

        Assert.Equal(Sets[index].Hash, units.Hash.ToString("x16", CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("%f", 0.5, "0.500000")]
    [InlineData("%.0f", 0.5, "0")] // halfway: to even
    [InlineData("%.0f", 1.5, "2")]
    [InlineData("%.0f", 2.5, "2")]
    [InlineData("%5.1f", 0.25, "  0.2")]
    [InlineData("%5.1f", 0.35, "  0.3")] // 0.35 is a little below 0.35 in binary
    [InlineData("%5.1f", -0.04, " -0.0")]
    [InlineData("%4.2f", 1.09, "1.09")]
    [InlineData("%f", -0.0, "-0.000000")]
    [InlineData("%010.3f", -3.14159, "-00003.142")]
    public void FormatsFloatsAsTheCRuntime(string format, double value, string expected) =>
        Assert.Equal(expected, CFormat.Sprintf(format, value));

    [Fact]
    public void FormatsTheRest()
    {
        Assert.Equal("  7|7  |007|-12|ff|FF|0x1f|x|(null)|ab|%", CFormat.Sprintf("%3i|%-3d|%03i|%i|%x|%X|%#x|%c|%s|%.2s|%%", 7, 7, 7, -12, 255, 255, 31, 'x', (string?)null, "abc"));
        Assert.Equal("4294967295 |   -5", CFormat.Sprintf("%-11u|%*d", -1, 5, -5));
    }

    private static void PrintfFloat(Units u)
    {
        double v = u.Below(4) switch
        {
            0 => u.Uniform(-1e6, 1e6),
            1 => (u.Below(2000000) / 1000.0) - 1000,
            2 => u.SomeFloat(100000),
            _ => (u.Below(200001) - 100000) / 8.0,
        };
        foreach (string format in FloatFormats)
        {
            u.String(CFormat.Sprintf(format, v));
        }
    }

    private static void PrintfInt(Units u)
    {
        int v = u.Below(2) != 0 ? (int)(u.Next() >> 32) : u.Below(2000) - 1000;
        for (int i = 0; i < IntFormats.Length; i++)
        {
            u.String(CFormat.Sprintf(IntFormats[i], i == 11 ? (v & 0x7f) + 32 : v));
        }
    }

    private static void PrintfString(Units u)
    {
        string s = u.SomeString();
        foreach (string format in StringFormats)
        {
            u.String(CFormat.Sprintf(format, s));
        }
    }

    private static void AngleVectors(Units u)
    {
        Vec3 angles = u.SomeVec(720);
        Vec3 f = default, r = default, up = default;
        MathLib.AngleVectors(angles, ref f, ref r, ref up);
        u.Vec(f);
        u.Vec(r);
        u.Vec(up);
    }

    private static void VectorNormalize(Units u)
    {
        Vec3 v = u.SomeVec(4096);
        if (u.Below(10) == 0)
        {
            v = default;
        }

        u.Float(MathLib.VectorNormalize(ref v));
        u.Vec(v);
    }

    private static void Vectors(Units u)
    {
        Vec3 a = u.SomeVec(4096);
        Vec3 b = u.SomeVec(4096);
        float s = u.SomeFloat(10);
        Vec3 c = default;
        MathLib.CrossProduct(a, b, ref c);
        u.Vec(c);
        MathLib.VectorMA(a, s, b, ref c);
        u.Vec(c);
        MathLib.VectorScale(a, s, ref c);
        u.Vec(c);
        u.Float(MathLib.DotProduct(a, b));
        u.Int(MathLib.VectorCompare(a, b));
        u.Int(MathLib.VectorCompare(a, a));
        MathLib.VectorInverse(ref a);
        u.Vec(a);
    }

    private static void ConcatRotations(Units u)
    {
        Mat3 a = default, b = default, c = default;
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                a[i][j] = u.SomeFloat(2);
                b[i][j] = u.SomeFloat(2);
            }
        }

        MathLib.R_ConcatRotations(a, b, ref c);
        for (int i = 0; i < 3; i++)
        {
            u.Vec(c[i]);
        }
    }

    private static void ConcatTransforms(Units u)
    {
        Mat3x4 a = default, b = default, c = default;
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                a[i][j] = u.SomeFloat(2);
                b[i][j] = u.SomeFloat(2);
            }
        }

        MathLib.R_ConcatTransforms(a, b, ref c);
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                u.Float(c[i][j]);
            }
        }
    }

    private static void FloorDivMod(Units u)
    {
        double numer = u.Below(2000001) - 1000000;
        double denom = 1 + u.Below(1000);
        MathLib.FloorDivMod(numer, denom, out int q, out int r);
        u.Int(q);
        u.Int(r);
    }

    private static void GreatestCommonDivisor(Units u)
    {
        // the oracle evaluates function arguments right to left, as MSVC does
        int i2 = u.Below(1000000);
        int i1 = u.Below(1000000);
        u.Int(MathLib.GreatestCommonDivisor(i1, i2));
    }

    private static void RotatePointAroundVector(Units u)
    {
        Vec3 dir = u.SomeVec(1);
        MathLib.VectorNormalize(ref dir);
        Vec3 point = u.SomeVec(4096);
        Vec3 @out = default;
        MathLib.RotatePointAroundVector(ref @out, dir, point, u.SomeFloat(360));
        u.Vec(@out);
    }

    private static void BoxOnPlaneSide(Units u)
    {
        var p = new MPlane { normal = u.SomeVec(1) };
        p.dist = u.SomeFloat(4096);
        p.signbits = 0;
        for (int i = 0; i < 3; i++)
        {
            if (p.normal[i] < 0)
            {
                p.signbits |= (byte)(1 << i);
            }
        }

        Vec3 mins = u.SomeVec(4096);
        Vec3 maxs = u.SomeVec(4096);
        u.Int(MathLib.BoxOnPlaneSide(mins, maxs, p));
    }

    // units.c: Set_crt_atof
    private static void CrtAtof(Units u)
    {
        double d = QString.atof(u.SomeNumber());
        u.Double(d);
        u.Float((float)d);
    }

    // units.c: Set_scanf_float (fscanf "%f\n" on a file in text mode, then what is left; the "\n" only after a value)
    private static void ScanfFloat(Units u)
    {
        var f = new CStdioReader(Encoding.Latin1.GetBytes(u.SomeNumber()));
        float? v = f.ScanFloat();
        u.Int(v.HasValue ? 1 : 0);
        if (v.HasValue)
        {
            f.SkipSpace();
            u.Float(v.Value);
        }

        u.String(Rest(f));
    }

    // units.c: Set_scanf_int (fscanf "%i\n")
    private static void ScanfInt(Units u)
    {
        var f = new CStdioReader(Encoding.Latin1.GetBytes(u.SomeNumber()));
        int? v = f.ScanInt();
        u.Int(v.HasValue ? 1 : 0);
        if (v.HasValue)
        {
            f.SkipSpace();
            u.Int(v.Value);
        }

        u.String(Rest(f));
    }

    // units.c: Set_scanf_string (fscanf "%s\n")
    private static void ScanfString(Units u)
    {
        var f = new CStdioReader(Encoding.Latin1.GetBytes(u.SomeNumber()));
        string? v = f.ScanString();
        u.Int(v != null ? 1 : 0);
        if (v != null)
        {
            f.SkipSpace();
            u.String(v);
        }

        u.String(Rest(f));
    }

    private static string Rest(CStdioReader f)
    {
        var sb = new StringBuilder();
        int c;
        while ((c = f.Getc()) >= 0)
        {
            sb.Append((char)c);
        }

        return sb.ToString();
    }

    // units.c: the inputs and the hash
    private sealed class Units(ulong state)
    {
        private const string Alphabet = "0123456789000111.-x'aAfFgz \u0080°ÿ";

        public ulong Hash { get; private set; } = 0xcbf29ce484222325;

        public ulong Next()
        {
            ulong z = state += 0x9e3779b97f4a7c15;
            z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9;
            z = (z ^ (z >> 27)) * 0x94d049bb133111eb;
            return z ^ (z >> 31);
        }

        public double Uniform(double lo, double hi) => lo + ((Next() >> 11) * (1.0 / 9007199254740992.0) * (hi - lo));

        public int Below(int n) => (int)(Next() % (ulong)n);

        public float SomeFloat(double range)
        {
            switch (Below(4))
            {
                case 0:
                    return Below((int)range * 2) - (float)range;
                case 1:
                    return (Below((int)range * 2000) - ((int)range * 1000)) / 1000.0f;
                case 2:
                    float f;
                    do
                    {
                        f = BitConverter.UInt32BitsToSingle((uint)(Next() >> 32));
                    }
                    while (float.IsNaN(f) || float.IsInfinity(f));

                    return f;
                default:
                    return (float)Uniform(-range, range);
            }
        }

        public Vec3 SomeVec(double range)
        {
            Vec3 v = default;
            v[0] = SomeFloat(range);
            v[1] = SomeFloat(range);
            v[2] = SomeFloat(range);
            return v;
        }

        public string SomeString()
        {
            int length = Below(12);
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                sb.Append(Alphabet[Below(Alphabet.Length)]);
            }

            return sb.ToString();
        }

        // units.c: SomeNumber (MSVC's order: a call's arguments from the last to the first)
        public string SomeNumber()
        {
            var s = new StringBuilder();
            switch (Below(5))
            {
                case 0:
                    s.Append(CFormat.Sprintf("%f", SomeFloat(100000)));
                    break;
                case 1:
                    float f = (float)Uniform(-10000, 10000);
                    float g = BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(f) + 1);
                    s.Append(CFormat.Sprintf(Below(2) != 0 ? "%.30f" : "%.30f1", ((double)f + (double)g) / 2));
                    break;
                case 2:
                    if (Below(3) == 0)
                    {
                        s.Append(Below(2) != 0 ? "-" : "+");
                    }

                    for (int i = 0, n = Below(9); i < n; i++)
                    {
                        s.Append((char)('0' + Below(10)));
                    }

                    if (Below(2) != 0)
                    {
                        s.Append('.');
                    }

                    for (int i = 0, n = Below(9); i < n; i++)
                    {
                        s.Append((char)('0' + Below(10)));
                    }

                    if (Below(4) == 0)
                    {
                        s.Append(Below(2) != 0 ? "e" : "E");
                        if (Below(2) != 0)
                        {
                            s.Append(Below(2) != 0 ? "-" : "+");
                        }

                        for (int i = 0, n = Below(4); i < n; i++)
                        {
                            s.Append((char)('0' + Below(10)));
                        }
                    }

                    break;
                case 3:
                    s.Append(SomeString());
                    break;
                default:
                    int v = Below(2000001) - 1000000;
                    s.Append(CFormat.Sprintf(Below(2) != 0 ? "%d" : " %07d", v));
                    break;
            }

            if (Below(4) == 0)
            {
                s.Append(SomeString());
            }

            switch (Below(8))
            {
                case 0:
                    s.Append("\r\n1");
                    break;
                case 1:
                    s.Append("\u001a1");
                    break;
            }

            return s.ToString();
        }

        public void Int(int v) => Bytes(BitConverter.GetBytes(v));

        public void Double(double v) => Bytes(BitConverter.GetBytes(v));

        public void Float(float v) => Bytes(BitConverter.GetBytes(v));

        public void Vec(Vec3 v)
        {
            Float(v[0]);
            Float(v[1]);
            Float(v[2]);
        }

        public void String(string s)
        {
            Bytes(Encoding.Latin1.GetBytes(s));
            Bytes([0]);
        }

        private void Bytes(ReadOnlySpan<byte> data)
        {
            foreach (byte b in data)
            {
                Hash ^= b;
                Hash *= 0x100000001b3;
            }
        }
    }
}
