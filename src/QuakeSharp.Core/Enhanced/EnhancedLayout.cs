using QuakeSharp.Core.Platform;

namespace QuakeSharp.Core.Enhanced;

/// <summary>
/// Where the enhanced frame's parts go in the window (design.md §11.6): the 2D in the faithful presentation's rectangle
/// (<see cref="PresentationLayout"/>: 4:3, or square pixels, centred) with its integer scale, and the 3D view where the
/// frame's <c>scr_vrect</c> falls in it, or, widened (widescreen), across the whole width of the window in the same
/// rows when the software view spans the frame's width.
/// </summary>
public readonly record struct EnhancedLayout(
    (int X, int Y, int Width, int Height) Frame,
    int Scale,
    (int X, int Y, int Width, int Height) View,
    bool Widened)
{
    /// <summary>Whether there is a view to draw.</summary>
    public bool HasView => View.Width > 0 && View.Height > 0;

    public static EnhancedLayout Compute(EnhancedFrame frame, int windowWidth, int windowHeight, bool squarePixels, bool widescreen)
    {
        (int X, int Y, int Width, int Height) rect =
            PresentationLayout.FrameRect(frame.Width, frame.Height, windowWidth, windowHeight, squarePixels);
        int scale = PresentationLayout.IntegerScale(frame.Width, frame.Height, rect.Width, rect.Height);
        if (!frame.ViewDrawn || frame.View.Width <= 0 || frame.View.Height <= 0)
        {
            return new EnhancedLayout(rect, scale, default, false);
        }

        double sx = (double)rect.Width / frame.Width, sy = (double)rect.Height / frame.Height;
        int left = rect.X + (int)Math.Round(frame.View.X * sx);
        int right = rect.X + (int)Math.Round((frame.View.X + frame.View.Width) * sx);
        int top = rect.Y + (int)Math.Round(frame.View.Y * sy);
        int bottom = rect.Y + (int)Math.Round((frame.View.Y + frame.View.Height) * sy);
        bool widened = widescreen && frame.View.X == 0 && frame.View.Width == frame.Width && rect.Width < windowWidth;
        if (widened)
        {
            left = 0;
            right = windowWidth;
        }

        return new EnhancedLayout(rect, scale, (left, top, right - left, bottom - top), widened);
    }
}
