namespace QuakeSharp.Core.Common;

/// <summary>common.h: sizebuf_t, a message being written or read.</summary>
public sealed class SizeBuf
{
    // common.c: SZ_Alloc (at least 256 bytes)
    public SizeBuf(int startsize)
    {
        if (startsize < 256)
        {
            startsize = 256;
        }

        data = new byte[startsize];
        maxsize = startsize;
        cursize = 0;
    }

    /// <summary>if false, do a Sys_Error</summary>
    public bool allowoverflow;

    /// <summary>set to true if the buffer size failed</summary>
    public bool overflowed;

    public byte[] data;

    public int maxsize;

    public int cursize;
}
