using System.Buffers.Binary;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Render;

namespace QuakeSharp.Core;

/// <summary>wad.h: qpic_t. The pixels that follow the header in C are <see cref="data"/>.</summary>
public sealed class QPic
{
    public int width, height;
    public byte[] data = [];

    // A qpic_t read from its bytes (width, height, then the pixels)
    public static QPic FromBytes(ReadOnlySpan<byte> bytes)
    {
        var pic = new QPic
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(bytes),
            height = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]),
        };
        pic.data = bytes.Slice(8, pic.width * pic.height).ToArray();
        return pic;
    }
}

// draw.c: this is the only file outside the refresh that touches the vid buffer. WinQuake draws 8-bit pixels
// (r_pixbytes 1): the 16-bit paths are left out.
public sealed partial class QuakeEngine
{
    // wad.h: TRANSPARENT_COLOR
    public const int TRANSPARENT_COLOR = 0xff;

    // draw.c: MAX_CACHED_PICS
    private const int MAX_CACHED_PICS = 128;

    /// <summary>draw.c: rectdesc_t</summary>
    private struct RectDesc
    {
        public VRect rect;
        public int width;
        public int height;
        public byte[] ptexbytes;
        public int rowbytes;
    }

    private RectDesc r_rectdesc;

    /// <summary>8*8 graphic characters</summary>
    public byte[] draw_chars = [];

    public QPic? draw_disc;
    public QPic? draw_backtile;

    // draw.c: menu_cachepics, menu_numcachepics (the cache never drops a picture: managed memory)
    private readonly string[] menu_cachepics_name = new string[MAX_CACHED_PICS];
    private readonly QPic?[] menu_cachepics_data = new QPic?[MAX_CACHED_PICS];
    private int menu_numcachepics;

    // wad.c: W_GetLumpName, for a lump that is a qpic_t
    public QPic W_GetLumpPic(string name) => QPic.FromBytes(wad!.Data.AsSpan(wad.GetLumpName(name)));

    // draw.c: Draw_PicFromWad
    public QPic Draw_PicFromWad(string name) => W_GetLumpPic(name);

    // draw.c: Draw_CachePic
    public QPic Draw_CachePic(string path)
    {
        int i;
        QPic? dat;

        for (i = 0; i < menu_numcachepics; i++)
        {
            if (path == menu_cachepics_name[i])
            {
                break;
            }
        }

        if (i == menu_numcachepics)
        {
            if (menu_numcachepics == MAX_CACHED_PICS)
            {
                throw Sys_Error("menu_numcachepics == MAX_CACHED_PICS");
            }

            menu_numcachepics++;
            menu_cachepics_name[i] = path;
        }

        dat = menu_cachepics_data[i];

        if (dat != null)
        {
            return dat;
        }

        // load the pic from disk
        byte[] file = COM_LoadHunkFile(path) ?? throw Sys_Error("Draw_CachePic: failed to load %s", path);
        dat = QPic.FromBytes(file);
        menu_cachepics_data[i] = dat;

        // SwapPic changes nothing on a little-endian machine
        return dat;
    }

    // draw.c: Draw_Init
    public void Draw_Init()
    {
        draw_chars = wad!.Data.AsSpan(wad.GetLumpName("conchars"), 128 * 128).ToArray();
        draw_disc = W_GetLumpPic("disc");
        draw_backtile = W_GetLumpPic("backtile");

        r_rectdesc.width = draw_backtile.width;
        r_rectdesc.height = draw_backtile.height;
        r_rectdesc.ptexbytes = draw_backtile.data;
        r_rectdesc.rowbytes = draw_backtile.width;
    }

    // draw.c: Draw_Character
    // Draws one 8*8 graphics character with 0 being transparent. It can be clipped to the top of the screen to allow
    // the console to be smoothly scrolled off.
    public void Draw_Character(int x, int y, int num)
    {
        int dest;
        int source;
        int drawline;
        int row, col;

        num &= 255;

        if (y <= -8)
        {
            return; // totally off screen
        }

        row = num >> 4;
        col = num & 15;
        source = (row << 10) + (col << 3);

        if (y < 0)
        {
            // clipped
            drawline = 8 + y;
            source -= 128 * y;
            y = 0;
        }
        else
        {
            drawline = 8;
        }

        byte[] conbuffer = vid.conbuffer;
        dest = (y * vid.conrowbytes) + x;

        while (drawline-- != 0)
        {
            for (int i = 0; i < 8; i++)
            {
                if (draw_chars[source + i] != 0)
                {
                    conbuffer[dest + i] = draw_chars[source + i];
                    enhanced?.Overlay(conbuffer, dest + i);
                }
            }

            source += 128;
            dest += vid.conrowbytes;
        }
    }

    // draw.c: Draw_String
    public void Draw_String(int x, int y, string str)
    {
        int i = 0;
        int c;
        while ((c = QString.Ch(str, i)) != 0)
        {
            Draw_Character(x, y, c);
            i++;
            x += 8;
        }
    }

    // draw.c: Draw_Pic
    public void Draw_Pic(int x, int y, QPic pic)
    {
        int dest, source;
        int v;

        if ((x < 0) ||
            (x + pic.width > vid.width) ||
            (y < 0) ||
            (y + pic.height > vid.height))
        {
            throw Sys_Error("Draw_Pic: bad coordinates");
        }

        source = 0;
        dest = vid.bufferoffset + (y * (int)vid.rowbytes) + x;

        for (v = 0; v < pic.height; v++)
        {
            Array.Copy(pic.data, source, vid.buffer, dest, pic.width);
            enhanced?.Overlay(vid.buffer, dest, pic.width);
            dest += (int)vid.rowbytes;
            source += pic.width;
        }
    }

    // draw.c: Draw_TransPic
    public void Draw_TransPic(int x, int y, QPic pic)
    {
        int dest, source;
        byte tbyte;
        int v, u;

        if (x < 0 || (uint)(x + pic.width) > vid.width || y < 0 ||
            (uint)(y + pic.height) > vid.height)
        {
            throw Sys_Error("Draw_TransPic: bad coordinates");
        }

        source = 0;
        dest = vid.bufferoffset + (y * (int)vid.rowbytes) + x;

        // (the C unwinds the loop when the width is a multiple of 8: the same pixels)
        for (v = 0; v < pic.height; v++)
        {
            for (u = 0; u < pic.width; u++)
            {
                if ((tbyte = pic.data[source + u]) != TRANSPARENT_COLOR)
                {
                    vid.buffer[dest + u] = tbyte;
                    enhanced?.Overlay(vid.buffer, dest + u);
                }
            }

            dest += (int)vid.rowbytes;
            source += pic.width;
        }
    }

    // draw.c: Draw_TransPicTranslate
    public void Draw_TransPicTranslate(int x, int y, QPic pic, byte[] translation)
    {
        int dest, source;
        byte tbyte;
        int v, u;

        if (x < 0 || (uint)(x + pic.width) > vid.width || y < 0 ||
            (uint)(y + pic.height) > vid.height)
        {
            throw Sys_Error("Draw_TransPic: bad coordinates");
        }

        source = 0;
        dest = vid.bufferoffset + (y * (int)vid.rowbytes) + x;

        // (the C unwinds the loop when the width is a multiple of 8: the same pixels)
        for (v = 0; v < pic.height; v++)
        {
            for (u = 0; u < pic.width; u++)
            {
                if ((tbyte = pic.data[source + u]) != TRANSPARENT_COLOR)
                {
                    vid.buffer[dest + u] = translation[tbyte];
                    enhanced?.Overlay(vid.buffer, dest + u);
                }
            }

            dest += (int)vid.rowbytes;
            source += pic.width;
        }
    }

    // draw.c: Draw_CharToConback
    public void Draw_CharToConback(int num, byte[] destbuf, int dest)
    {
        int row, col;
        int source;
        int drawline;
        int x;

        row = num >> 4;
        col = num & 15;
        source = (row << 10) + (col << 3);

        drawline = 8;

        while (drawline-- != 0)
        {
            for (x = 0; x < 8; x++)
            {
                if (draw_chars[source + x] != 0)
                {
                    destbuf[dest + x] = (byte)(0x60 + draw_chars[source + x]);
                }
            }

            source += 128;
            dest += 320;
        }
    }

    // draw.c: Draw_ConsoleBackground
    public void Draw_ConsoleBackground(int lines)
    {
        int x, y, v;
        int src, dest;
        int f, fstep;
        QPic conback;
        string ver;

        conback = Draw_CachePic("gfx/conback.lmp");

        // hack the version number directly into the pic
        ver = CFormat.Sprintf("(WinQuake) %4.2f", (float)QuakeDef.VERSION);
        dest = (320 * 186) + 320 - 11 - (8 * ver.Length);

        for (x = 0; x < ver.Length; x++)
        {
            Draw_CharToConback(ver[x], conback.data, dest + (x << 3));
        }

        // draw the pic
        byte[] conbuffer = vid.conbuffer;
        dest = 0;

        for (y = 0; y < lines; y++, dest += vid.conrowbytes)
        {
            v = (int)((vid.conheight - lines + y) * 200 / vid.conheight);
            src = v * 320;
            if (vid.conwidth == 320)
            {
                Array.Copy(conback.data, src, conbuffer, dest, vid.conwidth);
                enhanced?.Overlay(conbuffer, dest, (int)vid.conwidth);
            }
            else
            {
                f = 0;
                fstep = (int)(320 * 0x10000 / vid.conwidth);
                for (x = 0; x < vid.conwidth; x += 4)
                {
                    conbuffer[dest + x] = conback.data[src + (f >> 16)];
                    f += fstep;
                    conbuffer[dest + x + 1] = conback.data[src + (f >> 16)];
                    f += fstep;
                    conbuffer[dest + x + 2] = conback.data[src + (f >> 16)];
                    f += fstep;
                    conbuffer[dest + x + 3] = conback.data[src + (f >> 16)];
                    f += fstep;
                }

                enhanced?.Overlay(conbuffer, dest, (int)vid.conwidth);
            }
        }
    }

    // draw.c: R_DrawRect8
    public void R_DrawRect8(in VRect prect, int rowbytes, byte[] psrcbuf, int psrc, bool transparent)
    {
        byte t;
        int i, j, srcdelta, destdelta;
        int pdest;

        pdest = vid.bufferoffset + (prect.y * (int)vid.rowbytes) + prect.x;

        srcdelta = rowbytes - prect.width;
        destdelta = (int)vid.rowbytes - prect.width;

        if (transparent)
        {
            for (i = 0; i < prect.height; i++)
            {
                for (j = 0; j < prect.width; j++)
                {
                    t = psrcbuf[psrc];
                    if (t != TRANSPARENT_COLOR)
                    {
                        vid.buffer[pdest] = t;
                        enhanced?.Overlay(vid.buffer, pdest);
                    }

                    psrc++;
                    pdest++;
                }

                psrc += srcdelta;
                pdest += destdelta;
            }
        }
        else
        {
            for (i = 0; i < prect.height; i++)
            {
                Array.Copy(psrcbuf, psrc, vid.buffer, pdest, prect.width);
                enhanced?.Overlay(vid.buffer, pdest, prect.width);
                psrc += rowbytes;
                pdest += (int)vid.rowbytes;
            }
        }
    }

    // draw.c: Draw_TileClear
    // This repeats a 64*64 tile graphic to fill the screen around a sized down refresh window.
    public void Draw_TileClear(int x, int y, int w, int h)
    {
        int width, height, tileoffsetx, tileoffsety;
        int psrc;
        VRect vr;

        r_rectdesc.rect.x = x;
        r_rectdesc.rect.y = y;
        r_rectdesc.rect.width = w;
        r_rectdesc.rect.height = h;

        vr.y = r_rectdesc.rect.y;
        height = r_rectdesc.rect.height;

        tileoffsety = vr.y % r_rectdesc.height;

        while (height > 0)
        {
            vr.x = r_rectdesc.rect.x;
            width = r_rectdesc.rect.width;

            if (tileoffsety != 0)
            {
                vr.height = r_rectdesc.height - tileoffsety;
            }
            else
            {
                vr.height = r_rectdesc.height;
            }

            if (vr.height > height)
            {
                vr.height = height;
            }

            tileoffsetx = vr.x % r_rectdesc.width;

            while (width > 0)
            {
                if (tileoffsetx != 0)
                {
                    vr.width = r_rectdesc.width - tileoffsetx;
                }
                else
                {
                    vr.width = r_rectdesc.width;
                }

                if (vr.width > width)
                {
                    vr.width = width;
                }

                psrc = (tileoffsety * r_rectdesc.rowbytes) + tileoffsetx;

                R_DrawRect8(vr, r_rectdesc.rowbytes, r_rectdesc.ptexbytes, psrc, false);

                vr.x += vr.width;
                width -= vr.width;
                tileoffsetx = 0; // only the left tile can be left-clipped
            }

            vr.y += vr.height;
            height -= vr.height;
            tileoffsety = 0; // only the top tile can be top-clipped
        }
    }

    // draw.c: Draw_Fill
    // Fills a box of pixels with a single color
    public void Draw_Fill(int x, int y, int w, int h, int c)
    {
        int dest;
        int u, v;

        dest = vid.bufferoffset + (y * (int)vid.rowbytes) + x;
        for (v = 0; v < h; v++, dest += (int)vid.rowbytes)
        {
            for (u = 0; u < w; u++)
            {
                vid.buffer[dest + u] = (byte)c;
            }

            enhanced?.Overlay(vid.buffer, dest, w);
        }
    }

    // =============================================================================

    // draw.c: Draw_FadeScreen
    public void Draw_FadeScreen()
    {
        int x, y;
        int pbuf;

        VID_UnlockBuffer();
        S_ExtraUpdate();
        VID_LockBuffer();

        for (y = 0; y < vid.height; y++)
        {
            int t;

            pbuf = vid.bufferoffset + (int)(vid.rowbytes * y);
            t = (y & 1) << 1;

            for (x = 0; x < vid.width; x++)
            {
                if ((x & 3) != t)
                {
                    vid.buffer[pbuf + x] = 0;
                }
            }
        }

        enhanced?.Fade();

        VID_UnlockBuffer();
        S_ExtraUpdate();
        VID_LockBuffer();
    }

    // =============================================================================

    // draw.c: Draw_BeginDisc
    // Draws the little blue disc in the corner of the screen. Call before beginning any disc IO. (Before Draw_Init
    // there is no disc: the C passes draw_disc->data's address, which D_BeginDirectRect does not read without a mode.)
    public void Draw_BeginDisc() => D_BeginDirectRect((int)vid.width - 24, 0, draw_disc?.data, 24, 24);

    // draw.c: Draw_EndDisc
    // Erases the disc icon. Call after completing any disc IO
    public void Draw_EndDisc() => D_EndDirectRect((int)vid.width - 24, 0, 24, 24);
}
