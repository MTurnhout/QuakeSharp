using System.Buffers.Binary;
using System.Text;
using QuakeSharp.Core;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.GameData;
using QuakeSharp.Core.Model;

namespace QuakeSharp.Tools;

internal static class Commands
{
    public static int Identify(string[] targets)
    {
        foreach (string target in targets)
        {
            IEnumerable<string> paths = Directory.Exists(target)
                ? Directory.EnumerateFiles(target, "*.pak", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive })
                : [target];
            foreach (string path in paths)
            {
                GameDataFile? known = GameDataCatalog.IdentifyFile(path);
                string what = known == null ? "unknown" : known.Original ? known.Description : $"{known.Description} [not for fidelity tests]";
                Console.WriteLine($"{path}: {what}");
            }
        }

        return 0;
    }

    public static int Files(string baseDir, string[] engineArgs)
    {
        FileSystem fs = Init(baseDir, engineArgs, Console.Write);
        fs.Path_f();
        foreach (string name in fs.ListFiles())
        {
            fs.FindFile(name, out Stream? stream);
            stream?.Dispose();
            Console.WriteLine($"{fs.FileSize,10} {name}");
        }

        return 0;
    }

    public static int Extract(string baseDir, string file, string output, string[] engineArgs)
    {
        RefuseRepository(output);
        FileSystem fs = Init(baseDir, engineArgs);
        byte[] data = fs.LoadFile(file) ?? throw new QuakeException($"can't find {file}");
        File.WriteAllBytes(output, data.AsSpan(0, fs.FileSize));
        Console.WriteLine($"wrote {output} ({fs.FileSize} bytes)");
        return 0;
    }

    public static int DumpGraphics(string baseDir, string outDir, string[] engineArgs)
    {
        RefuseRepository(outDir);
        FileSystem fs = Init(baseDir, engineArgs);
        Directory.CreateDirectory(outDir);
        int written = 0;

        byte[] palette = Load(fs, "gfx/palette.lmp")[..768];
        written += WritePalette(Path.Combine(outDir, "palette.png"), palette);

        byte[] colormap = Load(fs, "gfx/colormap.lmp");
        PngWriter.WriteIndexed(Path.Combine(outDir, "colormap.png"), 256, 64, colormap.AsSpan(0, 256 * 64), palette);
        written++;

        List<string> names = [.. fs.ListFiles()];

        // the pictures in gfx/ (qpic_t: width, height, then the pixels; 255 is transparent)
        foreach (string name in names.Where(n => n.StartsWith("gfx/", StringComparison.Ordinal) && n.EndsWith(".lmp", StringComparison.Ordinal)))
        {
            byte[] data = Load(fs, name);
            if (QPic(data, 0, fs.FileSize) is (int w, int h))
            {
                WriteTransparent(Path.Combine(outDir, "gfx", FileName(Com.FileBase(name)) + ".png"), w, h, data.AsSpan(8, w * h), palette, 255);
                written++;
            }
        }

        // gfx.wad
        Wad wad = Wad.LoadWadFile(fs, "gfx.wad");
        foreach (Wad.LumpInfo lump in wad.Lumps)
        {
            string path = Path.Combine(outDir, "wad", FileName(lump.Name) + ".png");
            if (lump.Name == "conchars")
            {
                // draw.c: Draw_Character draws a 128x128 raw block, colour 0 transparent
                WriteTransparent(path, 128, 128, wad.Data.AsSpan(lump.FilePos, 128 * 128), palette, 0);
                written++;
            }
            else if (lump.Type == Wad.TYP_QPIC && QPic(wad.Data, lump.FilePos, lump.Size) is (int w, int h))
            {
                WriteTransparent(path, w, h, wad.Data.AsSpan(lump.FilePos + 8, w * h), palette, 255);
                written++;
            }
            else if (lump.Type == Wad.TYP_PALETTE)
            {
                written += WritePalette(path, wad.Data.AsSpan(lump.FilePos, 768));
            }
        }

        var textures = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in names.Where(n => n.StartsWith("maps/", StringComparison.Ordinal) && n.EndsWith(".bsp", StringComparison.Ordinal)))
        {
            written += DumpTextures(Load(fs, name), Path.Combine(outDir, "textures"), palette, textures);
        }

        foreach (string name in names.Where(n => n.EndsWith(".mdl", StringComparison.Ordinal)))
        {
            written += DumpSkins(Load(fs, name), Path.Combine(outDir, "skins"), Com.FileBase(name), palette);
        }

        foreach (string name in names.Where(n => n.EndsWith(".spr", StringComparison.Ordinal)))
        {
            written += DumpSprite(Load(fs, name), Path.Combine(outDir, "sprites"), Com.FileBase(name), palette);
        }

        Console.WriteLine($"wrote {written} images to {outDir}");
        return 0;
    }

    // tools/oracle/src/trace.c: Shot (width, height, the palette, the pixels)
    public static int ShotToPng(string shot, string output)
    {
        RefuseRepository(output);
        byte[] data = File.ReadAllBytes(shot);
        int width = I32(data, 0);
        int height = I32(data, 4);
        PngWriter.WriteIndexed(output, width, height, data.AsSpan(8 + 768, width * height), data.AsSpan(8, 768));
        Console.WriteLine($"wrote {output} ({width}x{height})");
        return 0;
    }

    // bspfile.h: the textures lump (dmiptexlump_t) and its miptex_t; mip level 0 of each, once per name
    private static int DumpTextures(byte[] bsp, string dir, byte[] palette, HashSet<string> done)
    {
        Directory.CreateDirectory(dir);
        if (I32(bsp, 0) != BspFile.BSPVERSION)
        {
            return 0;
        }

        int lump = I32(bsp, 4 + (8 * BspFile.LUMP_TEXTURES));
        int nummiptex = I32(bsp, lump);
        int written = 0;
        for (int i = 0; i < nummiptex; i++)
        {
            int dataofs = I32(bsp, lump + 4 + (4 * i));
            if (dataofs == -1)
            {
                continue;
            }

            int mt = lump + dataofs;
            string name = CString(bsp.AsSpan(mt, 16));
            int width = I32(bsp, mt + 16);
            int height = I32(bsp, mt + 20);
            int offset0 = I32(bsp, mt + 24);
            if (done.Add(name))
            {
                PngWriter.WriteIndexed(Path.Combine(dir, FileName(name) + ".png"), width, height, bsp.AsSpan(mt + offset0, width * height), palette);
                written++;
            }
        }

        return written;
    }

    // modelgen.h: mdl_t, then each skin: daliasskintype_t and one picture, or a group of them
    private static int DumpSkins(byte[] mdl, string dir, string model, byte[] palette)
    {
        Directory.CreateDirectory(dir);
        if (I32(mdl, 0) != ModelGen.IDPOLYHEADER || I32(mdl, 4) != ModelGen.ALIAS_VERSION)
        {
            return 0;
        }

        int numskins = I32(mdl, 48);
        int width = I32(mdl, 52);
        int height = I32(mdl, 56);
        int size = width * height;
        int p = ModelGen.MdlSize;
        int written = 0;
        for (int i = 0; i < numskins; i++)
        {
            var type = (AliasSkinType)I32(mdl, p);
            p += 4;
            if (type == AliasSkinType.ALIAS_SKIN_SINGLE)
            {
                PngWriter.WriteIndexed(Path.Combine(dir, $"{FileName(model)}_{i}.png"), width, height, mdl.AsSpan(p, size), palette);
                p += size;
                written++;
            }
            else
            {
                int count = I32(mdl, p);
                p += 4 + (4 * count); // daliasskingroup_t, then the intervals
                for (int j = 0; j < count; j++)
                {
                    PngWriter.WriteIndexed(Path.Combine(dir, $"{FileName(model)}_{i}_{j}.png"), width, height, mdl.AsSpan(p, size), palette);
                    p += size;
                    written++;
                }
            }
        }

        return written;
    }

    // spritegn.h: dsprite_t, then each frame: dspriteframetype_t and one dspriteframe_t, or a group of them
    private static int DumpSprite(byte[] spr, string dir, string sprite, byte[] palette)
    {
        Directory.CreateDirectory(dir);
        if (I32(spr, 0) != SpriteGen.IDSPRITEHEADER || I32(spr, 4) != SpriteGen.SPRITE_VERSION)
        {
            return 0;
        }

        int numframes = I32(spr, 24);
        int p = SpriteGen.DspriteSize;
        int written = 0;
        for (int i = 0; i < numframes; i++)
        {
            var type = (SpriteFrameType)I32(spr, p);
            p += 4;
            int count = 1;
            string suffix = $"{i}";
            if (type != SpriteFrameType.SPR_SINGLE)
            {
                count = I32(spr, p);
                p += 4 + (4 * count); // dspritegroup_t, then the intervals
            }

            for (int j = 0; j < count; j++)
            {
                int width = I32(spr, p + 8);
                int height = I32(spr, p + 12);
                p += SpriteGen.DspriteframeSize;
                string name = type == SpriteFrameType.SPR_SINGLE ? suffix : $"{suffix}_{j}";
                WriteTransparent(Path.Combine(dir, $"{FileName(sprite)}_{name}.png"), width, height, spr.AsSpan(p, width * height), palette, 255);
                p += width * height;
                written++;
            }
        }

        return written;
    }

    // wad.h: qpic_t, if the data holds one
    private static (int Width, int Height)? QPic(byte[] data, int offset, int length)
    {
        if (length < 8)
        {
            return null;
        }

        int width = I32(data, offset);
        int height = I32(data, offset + 4);
        return width > 0 && height > 0 && 8L + ((long)width * height) <= length ? (width, height) : null;
    }

    private static int WritePalette(string path, ReadOnlySpan<byte> palette)
    {
        // 16 x 16 swatches of 8 x 8 pixels
        var indices = new byte[128 * 128];
        for (int y = 0; y < 128; y++)
        {
            for (int x = 0; x < 128; x++)
            {
                indices[(y * 128) + x] = (byte)(((y / 8) * 16) + (x / 8));
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PngWriter.WriteIndexed(path, 128, 128, indices, palette);
        return 1;
    }

    private static void WriteTransparent(string path, int width, int height, ReadOnlySpan<byte> pixels, ReadOnlySpan<byte> palette, byte transparent)
    {
        var opaque = new bool[width * height];
        for (int i = 0; i < opaque.Length; i++)
        {
            opaque[i] = pixels[i] != transparent;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PngWriter.WriteIndexed(path, width, height, pixels, palette, opaque);
    }

    private static FileSystem Init(string baseDir, string[] engineArgs, Action<string>? conPrintf = null)
    {
        var fs = new FileSystem(new CommandLine(["quake", .. engineArgs]), conPrintf ?? (_ => { }), _ => { });
        fs.InitFilesystem(baseDir);
        return fs;
    }

    private static byte[] Load(FileSystem fs, string name) => fs.LoadFile(name) ?? throw new QuakeException($"can't find {name}");

    private static int I32(byte[] data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));

    private static string CString(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? field : field[..end]);
    }

    // A name safe for a file: '*' (water, slime and lava textures) becomes '#', other invalid characters '_'
    private static string FileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        char[] invalid = Path.GetInvalidFileNameChars();
        foreach (char c in name.Replace('*', '#'))
        {
            sb.Append(invalid.Contains(c) || c < ' ' || c > '~' ? '_' : c);
        }

        return sb.ToString();
    }

    // Extracted game data must never land in the repository.
    private static void RefuseRepository(string output)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(output)); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QuakeSharp.slnx")))
            {
                throw new QuakeException($"{output} is inside the QuakeSharp repository; write game data elsewhere");
            }
        }
    }
}
