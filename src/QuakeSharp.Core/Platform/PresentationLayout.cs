namespace QuakeSharp.Core.Platform;

/// <summary>Where a frame goes in the window (design.md §6.4).</summary>
public static class PresentationLayout
{
    /// <summary>
    /// The frame's rectangle of a window: its display aspect (4:3, as on the monitors Quake was made for and as
    /// vid.aspect assumes; or the frame's own shape with square pixels), as large as fits, centred.
    /// </summary>
    public static (int X, int Y, int Width, int Height) FrameRect(int frameWidth, int frameHeight, int windowWidth, int windowHeight, bool squarePixels)
    {
        double aspect = squarePixels ? (double)frameWidth / frameHeight : 4.0 / 3.0;
        int w = windowWidth, h = windowHeight;
        if ((double)w / h > aspect)
        {
            w = (int)Math.Max(1, Math.Round(h * aspect));
        }
        else
        {
            h = (int)Math.Max(1, Math.Round(w / aspect));
        }

        return ((windowWidth - w) / 2, (windowHeight - h) / 2, w, h);
    }

    /// <summary>
    /// The smallest whole number of times each pixel is repeated across and down so the frame covers the rectangle
    /// (the first pass, nearest neighbour; the second filters it down to the rectangle).
    /// </summary>
    public static int IntegerScale(int frameWidth, int frameHeight, int rectWidth, int rectHeight) =>
        Math.Max(1, (int)Math.Ceiling(Math.Max((double)rectWidth / frameWidth, (double)rectHeight / frameHeight)));
}
