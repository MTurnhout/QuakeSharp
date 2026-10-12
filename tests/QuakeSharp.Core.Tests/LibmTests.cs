using QuakeSharp.Core.Numerics;

namespace QuakeSharp.Core.Tests;

/// <summary>
/// The libm port against musl's C: the same input sets as tools/oracle/numerics.c, whose output (built by
/// tools/oracle/Build-Oracle.ps1) gives the expected hashes of the results' bits. Run "numerics &lt;set&gt; &lt;n&gt;"
/// for the first results of a set that differs.
/// </summary>
public class LibmTests
{
    private const int Count = 100000;

    // numerics.c: sets, in order (the seed of a set is its index + 1), and the hashes numerics.exe prints
    public static TheoryData<string, string> Sets => new()
    {
        { "sin.bits", "488074f898655bba" },
        { "sin.floats", "f66c558e30135fed" },
        { "sin.range", "05d5c17933116bbf" },
        { "cos.bits", "9903625b5dceeb03" },
        { "cos.floats", "180086cd32d7a70d" },
        { "cos.range", "89ba17fc079fdcb8" },
        { "tan.bits", "033d5e90f0eccd54" },
        { "tan.floats", "7194a2d217773a22" },
        { "tan.range", "8ca994059bb03970" },
        { "atan.bits", "d42d711f3bd5627f" },
        { "atan.floats", "f3fae0b7fe576b2a" },
        { "atan.range", "00759bef45f8d9c6" },
        { "atan2.bits", "7108bfe9e87c5733" },
        { "atan2.floats", "782a6c6c644cc40a" },
        { "atan2.range", "81517dd32238dbbb" },
        { "pow.bits", "64e61e76dcb64fe6" },
        { "pow.floats", "66accccd24918dc6" },
        { "pow.gamma", "9a7b974e15c0cae0" },
    };

    private static readonly string[] Order = [.. Sets.Select(row => row.Data.Item1)];

    [Theory]
    [MemberData(nameof(Sets))]
    public void MatchesMusl(string set, string expected)
    {
        string[] parts = set.Split('.');
        var state = new SplitMix64((ulong)Array.IndexOf(Order, set) + 1);
        Func<double, double, double> f = parts[0] switch
        {
            "sin" => (a, _) => Libm.sin(a),
            "cos" => (a, _) => Libm.cos(a),
            "tan" => (a, _) => Libm.tan(a),
            "atan" => (a, _) => Libm.atan(a),
            "atan2" => Libm.atan2,
            "pow" => Libm.pow,
            _ => throw new ArgumentException(set),
        };
        Func<double> input = parts[1] switch
        {
            "bits" => state.Bits,
            "floats" => state.FloatBits,
            "range" when parts[0] is "atan" => () => state.Uniform(-100, 100),
            "range" => () => state.Uniform(-10000, 10000),
            "gamma" => () => throw new InvalidOperationException(), // drawn in pairs below
            _ => throw new ArgumentException(set),
        };

        ulong hash = 0xcbf29ce484222325;
        for (int i = 0; i < Count; i++)
        {
            double a, b;
            if (parts[1] == "gamma")
            {
                a = state.Uniform(0, 1);
                b = state.Uniform(0.3, 3);
            }
            else
            {
                a = input();
                b = input();
            }

            ulong r = Bits(f(a, b));
            for (int j = 0; j < 8; j++)
            {
                hash ^= (r >> (j * 8)) & 0xff;
                hash *= 0x100000001b3;
            }
        }

        Assert.Equal(expected, hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1e-300)]
    [InlineData(Math.PI / 2)]
    [InlineData(3 * Math.PI / 2)]
    [InlineData(1e22)]
    [InlineData(-1e300)]
    public void TrigKeepsSignsAndIdentities(double x)
    {
        // Results close to the correctly rounded ones (musl's are within an ulp)
        Assert.Equal(Math.Sin(x), Libm.sin(x), 15);
        Assert.Equal(Math.Cos(x), Libm.cos(x), 15);
    }

    [Fact]
    public void ZerosKeepTheirSign()
    {
        foreach (double x in new[] { 0.0, -0.0 })
        {
            Assert.Equal(Bits(x), Bits(Libm.sin(x)));
            Assert.Equal(Bits(x), Bits(Libm.tan(x)));
            Assert.Equal(Bits(x), Bits(Libm.atan(x)));
        }
    }

    [Fact]
    public void SpecialValues()
    {
        Assert.True(double.IsNaN(Libm.sin(double.PositiveInfinity)));
        Assert.Equal(Math.PI, Libm.atan2(0.0, -1.0));
        Assert.Equal(-Math.PI, Libm.atan2(-0.0, -1.0));
        Assert.Equal(Math.PI / 2, Libm.atan2(1.0, 0.0));
        Assert.Equal(1.0, Libm.pow(double.NaN, 0.0));
        Assert.Equal(8.0, Libm.pow(2.0, 3.0));
        Assert.Equal(-8.0, Libm.pow(-2.0, 3.0));
        Assert.True(double.IsNaN(Libm.pow(-2.0, 0.5)));
        Assert.Equal(double.PositiveInfinity, Libm.pow(10.0, 400.0));
        Assert.Equal(0.0, Libm.pow(10.0, -400.0));
    }

    private static ulong Bits(double d) => double.IsNaN(d) ? 0x7ff8000000000000 : BitConverter.DoubleToUInt64Bits(d);

    // numerics.c: next, bits, floatbits, uniform
    private sealed class SplitMix64(ulong state)
    {
        public ulong Next()
        {
            ulong z = state += 0x9e3779b97f4a7c15;
            z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9;
            z = (z ^ (z >> 27)) * 0x94d049bb133111eb;
            return z ^ (z >> 31);
        }

        public double Bits() => BitConverter.UInt64BitsToDouble(Next());

        public double FloatBits() => BitConverter.UInt32BitsToSingle((uint)(Next() >> 32));

        public double Uniform(double lo, double hi) => lo + ((Next() >> 11) * (1.0 / 9007199254740992.0) * (hi - lo));
    }
}
