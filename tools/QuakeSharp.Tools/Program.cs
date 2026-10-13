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
        ["shot2png", var shot, var output] => Commands.ShotToPng(shot, output),
        ["trace", var baseDir, var output, .. var engineArgs] => TraceCommands.Trace(baseDir, output, engineArgs),
        ["netrun", var baseDir, var outDir, var game] => TraceCommands.NetRun(baseDir, outDir, game),
        ["enhanced-shot", var baseDir, var outDir, var frames, .. var engineArgs] => EnhancedCommands.Shot(baseDir, outDir, frames, engineArgs),
        ["enhanced-bench", var baseDir, var frame, var size, .. var engineArgs] => EnhancedCommands.Bench(baseDir, frame, size, engineArgs),
        ["shot-diff", var a, var b] => TraceCommands.ShotDiff(a, b, null),
        ["shot-diff", var a, var b, var output] => TraceCommands.ShotDiff(a, b, output),
        ["trace-compare", var a, var b] => TraceCommands.Compare(a, b, "FCEDXSTRVGQNWMPAIUO"),
        ["trace-compare", var a, var b, var letters] => TraceCommands.Compare(a, b, letters),
        ["file-compare", var a, var b] => TraceCommands.FileCompare(a, b),
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
          quakesharp-tools shot2png <in.shot> <out.png>     a frame the oracle wrote (-traceshot) as PNG
          quakesharp-tools shot-diff <a.shot> <b.shot> [out.png]
                                                            where two frames differ (the PNG: a, b, and a with
                                                            the differing pixels in magenta)
          quakesharp-tools trace <quake-dir> <out.trace> [args]
                                                            QuakeSharp's trace of a run, as the oracle writes
                                                            it (e.g. -tracequitafterdemo +playdemo demo1);
                                                            its files in <out>.files; -traceinput <file> puts
                                                            a file into its game directory (id1, -rogue's,
                                                            -hipnotic's) first, <file>@<path> at that path
                                                            in it (maps/e1m1.pts)
          quakesharp-tools netrun <quake-dir> <out-dir> <game.net>
                                                            a game between hosts (tools/oracle/events/*.net):
                                                            each host's trace, the datagrams it received (the
                                                            oracle's -tracedatagrams) and its files
          quakesharp-tools enhanced-shot <quake-dir> <out-dir> <frame,frame...> [trace args]
                                                            the enhanced frame at faithful settings beside the
                                                            software frame, their differences and its default look
                                                            (needs a GPU)
          quakesharp-tools enhanced-bench <quake-dir> <frame> <width>x<height> [trace args]
                                                            the enhanced frame's time at that size (needs a GPU)
          quakesharp-tools trace-compare <a.trace> <b.trace> [letters]
                                                            the first frame at which each kind of line differs
                                                            (letters: which, e.g. FCEXSTR; all by default)
          quakesharp-tools file-compare <a> <b>             where two files, or two folders' files (a run's
                                                            .files: the oracle's first), differ: a text file's
                                                            line, a demo's message block

        Game data and everything extracted from it must stay outside the repository; the tools refuse to write
        inside it.
        """);
    return 1;
}
