namespace QuakeSharp.Core.Platform;

/// <summary>
/// Shows the frames the engine draws: vid_win.c's VID_Update and VID_SetPalette, on whatever the platform has (design.md
/// §6.4). The engine owns the 8-bit buffer; the output copies it.
/// </summary>
public interface IVideoOutput
{
    /// <summary>
    /// Shows a frame: <paramref name="height"/> rows of <paramref name="width"/> palette indices,
    /// <paramref name="rowBytes"/> apart, through <paramref name="palette"/> (256 RGB triples, 768 bytes).
    /// </summary>
    void Present(ReadOnlySpan<byte> pixels, int width, int height, int rowBytes, ReadOnlySpan<byte> palette);

    /// <summary>
    /// vid_win.c set a mode: a window whose client area is <paramref name="width"/> by <paramref name="height"/>
    /// (a stretched mode's frame is half of it), or the whole screen in that resolution.
    /// </summary>
    void SetMode(int width, int height, bool fullscreen)
    {
    }
}
