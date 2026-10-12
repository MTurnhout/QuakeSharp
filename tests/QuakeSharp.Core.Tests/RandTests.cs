using QuakeSharp.Core.Numerics;

namespace QuakeSharp.Core.Tests;

public class RandTests
{
    [Fact]
    public void IsTheMicrosoftCRuntimesGenerator()
    {
        // tools/oracle/numerics.c: "rand 100000 64d22d70a8c211ff (41 18467 6334 26500)"
        var r = new CRand();
        ulong hash = 0xcbf29ce484222325;
        var first = new List<int>();
        for (int i = 0; i < 100000; i++)
        {
            int v = r.rand();
            if (i < 4)
            {
                first.Add(v);
            }

            hash ^= (ulong)(v & 0xff);
            hash *= 0x100000001b3;
            hash ^= (ulong)((v >> 8) & 0xff);
            hash *= 0x100000001b3;
        }

        Assert.Equal([41, 18467, 6334, 26500], first);
        Assert.Equal(0x64d22d70a8c211ffUL, hash);
    }

    [Fact]
    public void SrandRestarts()
    {
        var r = new CRand();
        int a = r.rand();
        r.rand();
        r.srand(1);
        Assert.Equal(a, r.rand());
    }
}
