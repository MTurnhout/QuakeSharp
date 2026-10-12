using QuakeSharp.Core.Platform;

namespace QuakeSharp.Core.Tests;

public class PresentationLayoutTests
{
    [Theory]
    [InlineData(320, 200, 960, 720, false, 0, 0, 960, 720)] // 4:3 fills a 4:3 window
    [InlineData(320, 200, 3840, 2160, false, 480, 0, 2880, 2160)] // pillarboxed on 16:9
    [InlineData(320, 240, 1000, 1000, false, 0, 125, 1000, 750)] // letterboxed in a square
    [InlineData(320, 200, 3840, 2160, true, 192, 0, 3456, 2160)] // square pixels: 16:10
    public void FrameRect(int fw, int fh, int ww, int wh, bool square, int x, int y, int w, int h) =>
        Assert.Equal((x, y, w, h), PresentationLayout.FrameRect(fw, fh, ww, wh, square));

    [Theory]
    [InlineData(320, 200, 960, 720, 4)] // 720 / 200 = 3.6 rows per pixel: 4
    [InlineData(320, 240, 960, 720, 3)] // exactly 3
    [InlineData(320, 200, 2880, 2160, 11)]
    [InlineData(640, 480, 320, 240, 1)] // smaller than the frame: 1, filtered down
    public void IntegerScaleCoversTheRect(int fw, int fh, int rw, int rh, int k) =>
        Assert.Equal(k, PresentationLayout.IntegerScale(fw, fh, rw, rh));
}
