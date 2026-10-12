using QuakeSharp.Core;
using QuakeSharp.Tools;

// Developer tools. Run without arguments for usage.
try
{
    return args switch
    {
        ["identify", .. var targets] when targets.Length > 0 => Commands.Identify(targets),
        ["files", var baseDir, .. var engineArgs] => Commands.Files(baseDir, engineArgs),
        ["extract", var baseDir, var file, var output, .. var engineArgs] => Commands.Extract(baseDir, file, output, engineArgs),
        ["dump-graphics", var baseDir, var outDir, .. var engineArgs] => Commands.DumpGraphics(baseDir, outDir, engineArgs),
        _ => Usage(),
    };
}
catch (QuakeException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("""
        usage:
          quakesharp-tools identify <file-or-directory>...  identify game data by MD5
          quakesharp-tools files <quake-dir> [engine args]  show the search path and every file it finds
                                                            (engine args: -game <dir>, -rogue, -hipnotic, -path ...)
          quakesharp-tools extract <quake-dir> <file> <out> [engine args]
                                                            copy a file found through the search path
          quakesharp-tools dump-graphics <quake-dir> <out-dir> [engine args]
                                                            write the palette, colormap, pictures, gfx.wad, map
                                                            textures, model skins and sprite frames as PNG

        Game data and everything extracted from it must stay outside the repository; the tools refuse to write
        inside it.
        """);
    return 1;
}
