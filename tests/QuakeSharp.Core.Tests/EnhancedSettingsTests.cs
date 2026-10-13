using QuakeSharp.Core.Enhanced;

namespace QuakeSharp.Core.Tests;

/// <summary>quakesharp.cfg, the enhanced mode's own settings (design.md decision 61).</summary>
public class EnhancedSettingsTests
{
    [Fact]
    public void TheDefaultsAreTheDecisions()
    {
        var settings = new EnhancedSettings();

        Assert.False(settings.Enhanced);
        Assert.Equal(4, settings.Number("enhanced_msaa"));
        Assert.Equal(0, settings.Number("enhanced_lighting"));
        Assert.Equal(1, settings.Number("enhanced_interpolate"));
        Assert.Equal(0, settings.Number("enhanced_lerpmodels"));
        Assert.Equal(0, settings.Number("enhanced_lerpmove"));
        Assert.Equal(0, settings.Number("enhanced_3d_sfx"));
    }

    [Fact]
    public void WhatIsWrittenReadsBack()
    {
        var settings = new EnhancedSettings { Enhanced = true };
        settings["enhanced_scale"] = "1.5";

        EnhancedSettings read = EnhancedSettings.Parse(settings.Write());

        Assert.True(read.Enhanced);
        Assert.Equal(1.5f, read.Number("enhanced_scale"));
        Assert.Equal(settings.Write(), read.Write());
    }

    [Fact]
    public void UnknownNamesCommentsAndBadLinesAreLeftOut()
    {
        EnhancedSettings read = EnhancedSettings.Parse(
            "// a comment\r\nenhanced 1 // on\r\nbogus \"3\"\r\nenhanced_msaa\r\n\r\nenhanced_filter \"1\"\n");

        Assert.True(read.Enhanced);
        Assert.Equal(4, read.Number("enhanced_msaa"));
        Assert.Equal(1, read.Number("enhanced_filter"));
        Assert.Throws<ArgumentException>(() => read["bogus"] = "1");
    }
}
