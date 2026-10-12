using System.Buffers.Binary;
using System.Text;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Numerics;
using static QuakeSharp.Core.Common.QString;

namespace QuakeSharp.Core;

// common.c: message IO, size buffers, COM_Parse
public sealed partial class QuakeEngine
{
    /// <summary>common.c: com_token, the token COM_Parse found last</summary>
    public string com_token = "";

    /// <summary>common.c: msg_readcount</summary>
    public int msg_readcount;

    /// <summary>common.c: msg_badread, set if a read goes beyond end of message</summary>
    public bool msg_badread;

    // ==========================================================================
    // MESSAGE IO FUNCTIONS: handles byte ordering and avoids alignment errors
    // ==========================================================================

    // common.c: MSG_WriteChar
    public void MSG_WriteChar(SizeBuf sb, int c)
    {
        int buf = SZ_GetSpace(sb, 1);
        sb.data[buf] = (byte)c;
    }

    // common.c: MSG_WriteByte
    public void MSG_WriteByte(SizeBuf sb, int c)
    {
        int buf = SZ_GetSpace(sb, 1);
        sb.data[buf] = (byte)c;
    }

    // common.c: MSG_WriteShort
    public void MSG_WriteShort(SizeBuf sb, int c)
    {
        int buf = SZ_GetSpace(sb, 2);
        sb.data[buf] = (byte)(c & 0xff);
        sb.data[buf + 1] = (byte)(c >> 8);
    }

    // common.c: MSG_WriteLong
    public void MSG_WriteLong(SizeBuf sb, int c)
    {
        int buf = SZ_GetSpace(sb, 4);
        sb.data[buf] = (byte)(c & 0xff);
        sb.data[buf + 1] = (byte)((c >> 8) & 0xff);
        sb.data[buf + 2] = (byte)((c >> 16) & 0xff);
        sb.data[buf + 3] = (byte)(c >> 24);
    }

    // common.c: MSG_WriteFloat
    public void MSG_WriteFloat(SizeBuf sb, float f)
    {
        Span<byte> dat = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(dat, f);
        SZ_Write(sb, dat);
    }

    // common.c: MSG_WriteString (with its terminating 0; null writes just the 0)
    public void MSG_WriteString(SizeBuf sb, string? s)
    {
        if (s == null)
        {
            SZ_Write(sb, [0]);
        }
        else
        {
            SZ_Write(sb, CString(s));
        }
    }

    // common.c: MSG_WriteCoord
    public void MSG_WriteCoord(SizeBuf sb, float f) => MSG_WriteShort(sb, X86.FtoI(f * 8));

    // common.c: MSG_WriteAngle (the angle is truncated to whole degrees before it is scaled)
    public void MSG_WriteAngle(SizeBuf sb, float f) => MSG_WriteByte(sb, (X86.FtoI(f) * 256 / 360) & 255);

    // common.c: MSG_BeginReading
    public void MSG_BeginReading()
    {
        msg_readcount = 0;
        msg_badread = false;
    }

    // common.c: MSG_ReadChar (returns -1 and sets msg_badread if no more characters are available)
    public int MSG_ReadChar()
    {
        int c;

        if (msg_readcount + 1 > net_message.cursize)
        {
            msg_badread = true;
            return -1;
        }

        c = (sbyte)net_message.data[msg_readcount];
        msg_readcount++;

        return c;
    }

    // common.c: MSG_ReadByte
    public int MSG_ReadByte()
    {
        int c;

        if (msg_readcount + 1 > net_message.cursize)
        {
            msg_badread = true;
            return -1;
        }

        c = net_message.data[msg_readcount];
        msg_readcount++;

        return c;
    }

    // common.c: MSG_ReadShort
    public int MSG_ReadShort()
    {
        int c;

        if (msg_readcount + 2 > net_message.cursize)
        {
            msg_badread = true;
            return -1;
        }

        c = (short)(net_message.data[msg_readcount] + (net_message.data[msg_readcount + 1] << 8));

        msg_readcount += 2;

        return c;
    }

    // common.c: MSG_ReadLong
    public int MSG_ReadLong()
    {
        int c;

        if (msg_readcount + 4 > net_message.cursize)
        {
            msg_badread = true;
            return -1;
        }

        c = net_message.data[msg_readcount]
            + (net_message.data[msg_readcount + 1] << 8)
            + (net_message.data[msg_readcount + 2] << 16)
            + (net_message.data[msg_readcount + 3] << 24);

        msg_readcount += 4;

        return c;
    }

    // common.c: MSG_ReadFloat (no check for the end of the message: it reads on in the buffer)
    public float MSG_ReadFloat()
    {
        float f = BinaryPrimitives.ReadSingleLittleEndian(net_message.data.AsSpan(msg_readcount, 4));
        msg_readcount += 4;
        return f;
    }

    // common.c: MSG_ReadString (at most 2047 characters, into a static buffer in C)
    public string MSG_ReadString()
    {
        var @string = new StringBuilder();
        int l, c;

        l = 0;
        do
        {
            c = MSG_ReadChar();
            if (c == -1 || c == 0)
            {
                break;
            }

            @string.Append((char)(byte)c);
            l++;
        }
        while (l < 2048 - 1);

        return @string.ToString();
    }

    // common.c: MSG_ReadCoord
    public float MSG_ReadCoord() => (float)(MSG_ReadShort() * (1.0 / 8));

    // common.c: MSG_ReadAngle
    public float MSG_ReadAngle() => (float)(MSG_ReadChar() * (360.0 / 256));

    // ==========================================================================

    // common.c: SZ_Clear
    public static void SZ_Clear(SizeBuf buf) => buf.cursize = 0;

    // common.c: SZ_GetSpace: the offset of length bytes added to the buffer
    public int SZ_GetSpace(SizeBuf buf, int length)
    {
        int data;

        if (buf.cursize + length > buf.maxsize)
        {
            if (!buf.allowoverflow)
            {
                throw Sys_Error("SZ_GetSpace: overflow without allowoverflow set");
            }

            if (length > buf.maxsize)
            {
                throw Sys_Error("SZ_GetSpace: %i is > full buffer size", length);
            }

            buf.overflowed = true;
            Con_Printf("SZ_GetSpace: overflow");
            SZ_Clear(buf);
        }

        data = buf.cursize;
        buf.cursize += length;

        return data;
    }

    // common.c: SZ_Write
    public void SZ_Write(SizeBuf buf, ReadOnlySpan<byte> data) => data.CopyTo(buf.data.AsSpan(SZ_GetSpace(buf, data.Length)));

    // common.c: SZ_Print (strcats onto the sizebuf). The C reads the byte before an empty buffer's data; nothing
    // prints into an empty one.
    public void SZ_Print(SizeBuf buf, string data)
    {
        byte[] text = CString(data);
        int len = text.Length;

        if (buf.cursize == 0 || buf.data[buf.cursize - 1] != 0)
        {
            text.CopyTo(buf.data, SZ_GetSpace(buf, len)); // no trailing 0
        }
        else
        {
            text.CopyTo(buf.data, SZ_GetSpace(buf, len - 1) - 1); // write over trailing 0
        }
    }

    // ==========================================================================

    // common.c: COM_Parse
    // Parse a token out of a string into com_token: the position after it, or -1 (NULL) at the end of the data. A
    // quoted string the data ends inside returns the position past the end, as C does (the next parse finds nothing).
    public int COM_Parse(string? data, int pos)
    {
        int c;
        var token = new StringBuilder();

        com_token = "";

        if (data == null || pos < 0)
        {
            return -1;
        }

        // skip whitespace
        while (true)
        {
            while ((c = Ch(data, pos)) <= ' ')
            {
                if (c == 0)
                {
                    return -1; // end of file;
                }

                pos++;
            }

            // skip // comments
            if (c == '/' && Ch(data, pos + 1) == '/')
            {
                while (Ch(data, pos) != 0 && Ch(data, pos) != '\n')
                {
                    pos++;
                }

                continue;
            }

            break;
        }

        // handle quoted strings specially
        if (c == '\"')
        {
            pos++;
            while (true)
            {
                c = Ch(data, pos++);
                if (c == '\"' || c == 0)
                {
                    com_token = token.ToString();
                    return pos;
                }

                token.Append((char)(byte)c);
            }
        }

        // parse single characters
        if (c is '{' or '}' or ')' or '(' or '\'' or ':')
        {
            token.Append((char)(byte)c);
            com_token = token.ToString();
            return pos + 1;
        }

        // parse a regular word
        do
        {
            token.Append((char)(byte)c);
            pos++;
            c = Ch(data, pos);
            if (c is '{' or '}' or ')' or '(' or '\'' or ':')
            {
                break;
            }
        }
        while (c > 32);

        com_token = token.ToString();
        return pos;
    }

    /// <summary>A string's bytes and its terminating 0, as C copies a char *.</summary>
    public static byte[] CString(string s)
    {
        int end = s.IndexOf('\0', StringComparison.Ordinal);
        byte[] bytes = new byte[(end < 0 ? s.Length : end) + 1];
        Encoding.Latin1.GetBytes(s.AsSpan(0, bytes.Length - 1), bytes);
        return bytes;
    }
}
