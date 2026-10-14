using QuakeSharp.Core.Enhanced;
using Xunit;

namespace QuakeSharp.Core.Tests;

// The enhanced mode's reverb (design.md §11.9)
public class EnhancedReverbTests
{
    [Fact]
    public void SilenceStaysSilent()
    {
        var reverb = new EnhancedReverb(1f);
        for (int i = 0; i < 5000; i++)
        {
            Assert.Equal((0f, 0f), reverb.Process(0, 0));
        }
    }

    [Fact]
    public void AnImpulseIsHeardAgainAfterTheShortestCombAndDiesAway()
    {
        var reverb = new EnhancedReverb(1f);
        Assert.Equal((1f, 1f), reverb.Process(1, 1)); // the dry sound at once, the room not yet
        float early = 0, late = 0;
        for (int i = 1; i < 44100 * 4; i++)
        {
            (float l, float r) = reverb.Process(0, 0);
            if (i < 1116)
            {
                Assert.Equal(0f, l); // nothing before the shortest comb's delay
            }

            if (i < 44100)
            {
                early = Math.Max(early, Math.Abs(l) + Math.Abs(r));
            }
            else if (i > 44100 * 3)
            {
                late = Math.Max(late, Math.Abs(l) + Math.Abs(r));
            }
        }

        Assert.True(early > 0.001f, $"early {early}");
        Assert.True(late < early / 20, $"late {late}, early {early}");
    }

    [Fact]
    public void TheTwoSidesDiffer()
    {
        var reverb = new EnhancedReverb(1f);
        reverb.Process(1, 1);
        bool differ = false;
        for (int i = 0; i < 3000; i++)
        {
            (float l, float r) = reverb.Process(0, 0);
            differ |= l != r;
        }

        Assert.True(differ);
    }

    [Fact]
    public void WithoutWetTheSoundIsDry()
    {
        var reverb = new EnhancedReverb(0f);
        reverb.Process(1, 1);
        for (int i = 0; i < 3000; i++)
        {
            Assert.Equal((0.25f, -0.5f), reverb.Process(0.25f, -0.5f));
        }
    }

    [Fact]
    public void ClearEndsTheTail()
    {
        var reverb = new EnhancedReverb(1f);
        for (int i = 0; i < 2000; i++)
        {
            reverb.Process(1, 1);
        }

        reverb.Clear();
        Assert.Equal((0f, 0f), reverb.Process(0, 0));
    }
}
