namespace QuakeSharp.Core.Video;

/// <summary>vid.h: viddef_t, the video state. The buffers are arrays with an offset where C has pointers.</summary>
public sealed class VidDef
{
    /// <summary>invisible buffer</summary>
    public byte[] buffer = [];

    /// <summary>where C's vid.buffer points in <see cref="buffer"/> (view.c's lcd_x moves it)</summary>
    public int bufferoffset;

    /// <summary>256 * VID_GRADES size</summary>
    public byte[] colormap = [];

    /// <summary>index of first fullbright color</summary>
    public int fullbright;

    /// <summary>may be > width if displayed in a window</summary>
    public uint rowbytes;

    public uint width;

    public uint height;

    /// <summary>width / height -- &lt; 0 is taller than wide</summary>
    public float aspect;

    public int numpages;

    /// <summary>if true, recalc vid-based stuff</summary>
    public int recalc_refdef;

    public byte[] conbuffer = [];

    public int conrowbytes;

    public uint conwidth;

    public uint conheight;

    public int maxwarpwidth;

    public int maxwarpheight;
}
