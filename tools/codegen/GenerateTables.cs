// Generates C# data tables from the original C sources in reference/Quake/WinQuake, and the constants of the musl
// libm functions QuakeSharp ports (design.md §3.1) from reference/musl.
// Usage (from anywhere): dotnet run tools/codegen/GenerateTables.cs
//
// Everything this writes is a *.g.cs file; never edit those by hand: change this generator and re-run it.
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

string repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ScriptPath())!, "..", ".."));
string src = Path.Combine(repo, "reference", "Quake", "WinQuake");
const string SourceTree = "reference/Quake/WinQuake";
string musl = Path.Combine(repo, "reference", "musl", "src", "math");
const string MuslTree = "reference/musl/src/math";

Write("Common/Crc.g.cs", GenerateUShortTable("crc.c", "QuakeSharp.Core.Common", "static partial class Crc", "crctable",
    "The CCITT CRC table (polynomial 0x1021, as XMODEM)."));
Write("Common/FileSystem.g.cs", GenerateUShortTable("common.c", "QuakeSharp.Core.Common", "sealed partial class FileSystem", "pop",
    "The picture gfx/pop.lmp must hold for the registered version, as big-endian shorts."));
Write("Numerics/Libm.g.cs", GenerateLibm());
Dictionary<string, long> keyDefines = Defines(File.ReadAllText(Path.Combine(src, "keys.h")), "K_");
Write("UI/Keys.g.cs", GenerateKeys());
Write("UI/QuakeEngine.Keys.g.cs", GenerateKeyNames());
Write("Protocol.g.cs", GenerateProtocol());
Write("QuakeDef.g.cs", GenerateQuakeDef());
Write("Model/BspFile.g.cs", GenerateBspFile());
Write("Render/Anorms.g.cs", GenerateAnorms());
WriteTo("QuakeSharp.Platform.Sdl", "ScanToKey.g.cs", GenerateScanToKey());
return 0;

// keys.h: the key numbers (K_TAB, K_UPARROW...), as constants
string GenerateKeys()
{
    var sb = Header("keys.h", "QuakeSharp.Core.UI");
    sb.AppendLine("/// <summary>keys.h: the key numbers; the printable keys are their lower case characters.</summary>");
    sb.AppendLine("public static class Keys");
    sb.AppendLine("{");
    foreach ((string name, long value) in keyDefines)
    {
        sb.AppendLine($"    public const int {name} = {value};");
    }

    sb.AppendLine("}");
    return sb.ToString();
}

// protocol.h: every #define (message types svc_* and clc_*, update bits U_* and SU_*, sound bits, temp entities...)
string GenerateProtocol()
{
    var sb = Header("protocol.h", "QuakeSharp.Core");
    sb.AppendLine("/// <summary>protocol.h: the network protocol's constants, as the C defines them.</summary>");
    sb.AppendLine("public static class Protocol");
    sb.AppendLine("{");
    foreach (Match m in Regex.Matches(Source("protocol.h"), @"^\s*#define\s+(\w+)\s+(\(1<<\d+\)|\d+)\s*$", RegexOptions.Multiline))
    {
        sb.AppendLine($"    public const int {m.Groups[1].Value} = {m.Groups[2].Value.Replace("<<", " << ")};");
    }

    sb.AppendLine("}");
    return sb.ToString();
}

// quakedef.h: the numeric #defines (limits, stats, item bits...), not the build switches id386 and UNALIGNED_OK
string GenerateQuakeDef()
{
    var sb = Header("quakedef.h", "QuakeSharp.Core");
    sb.AppendLine("public static partial class QuakeDef");
    sb.AppendLine("{");
    foreach (Match m in Regex.Matches(Source("quakedef.h"), @"^\s*#define\s+(\w+)\s+(\(1<<(?:\d+|\w+|\(\d+\+\d+\))\)|0x[0-9a-fA-F]+|\d+)\s*$", RegexOptions.Multiline))
    {
        string name = m.Groups[1].Value;
        if (name is "id386" or "UNALIGNED_OK")
        {
            continue;
        }

        string value = m.Groups[2].Value.Replace("<<", " << ").Replace("+", " + ");

        // 2147483648 is unsigned in C; Quake stores it in int fields, where it wraps (rogue's RIT_SUPERHEALTH)
        if (long.TryParse(value, out long number) && number > int.MaxValue)
        {
            value = $"unchecked((int){value})";
        }

        sb.AppendLine($"    public const int {name} = {value};");
    }

    sb.AppendLine("}");
    return sb.ToString();
}

// bspfile.h: the numeric #defines (limits, lumps, plane types, contents, ambient sounds...)
string GenerateBspFile()
{
    var sb = Header("bspfile.h", "QuakeSharp.Core.Model");
    sb.AppendLine("public static partial class BspFile");
    sb.AppendLine("{");
    foreach (Match m in Regex.Matches(Source("bspfile.h"), @"^\s*#define\s+(\w+)\s+(-?\d+|0x[0-9a-fA-F]+)\s*$", RegexOptions.Multiline))
    {
        sb.AppendLine($"    public const int {m.Groups[1].Value} = {m.Groups[2].Value};");
    }

    sb.AppendLine("}");
    return sb.ToString();
}

// anorms.h (included by r_alias.c): r_avertexnormals, the 162 vertex normals of the alias models. The C's double
// literals initialize floats, so each is rounded from its double, as the compiler does.
string GenerateAnorms()
{
    string c = Source("anorms.h");
    var numbers = Regex.Matches(c, @"-?\d+\.\d+").Select(m => m.Value).ToList();
    if (numbers.Count != 162 * 3)
    {
        throw new InvalidOperationException($"anorms.h: {numbers.Count} numbers, not {162 * 3}");
    }

    var sb = Header("anorms.h", "QuakeSharp.Core");
    sb.AppendLine("public sealed partial class QuakeEngine");
    sb.AppendLine("{");
    sb.AppendLine("    // r_alias.c: NUMVERTEXNORMALS");
    sb.AppendLine("    public const int NUMVERTEXNORMALS = 162;");
    sb.AppendLine();
    sb.AppendLine("    /// <summary>r_alias.c: r_avertexnormals[NUMVERTEXNORMALS][3], row by row.</summary>");
    sb.AppendLine("    public static readonly float[] r_avertexnormals =");
    sb.AppendLine("    [");
    foreach (string[] row in numbers.Chunk(3))
    {
        sb.AppendLine("        " + string.Join(" ", row.Select(v => $"(float){v},")));
    }

    sb.AppendLine("    ];");
    sb.AppendLine("}");
    return sb.ToString();
}

// keys.c: keynames, the names of the keys that are not one printable character
string GenerateKeyNames()
{
    string c = Source("keys.c");
    var sb = Header("keys.c", "QuakeSharp.Core");
    sb.AppendLine("using static QuakeSharp.Core.UI.Keys;");
    sb.AppendLine();
    sb.AppendLine("public sealed partial class QuakeEngine");
    sb.AppendLine("{");
    sb.AppendLine("    /// <summary>keys.c: keynames, in order (the C's list ends with {NULL,0}).</summary>");
    sb.AppendLine("    private static readonly (string name, int keynum)[] keynames =");
    sb.AppendLine("    [");
    foreach (Match m in Regex.Matches(InitializerBody(c, "keynames"), @"\{\s*""([^""]*)""\s*,\s*('(?:\\.|[^'\\])'|K_\w+)\s*\}"))
    {
        sb.AppendLine($"        (\"{m.Groups[1].Value}\", {m.Groups[2].Value}),");
    }

    sb.AppendLine("    ];");
    sb.AppendLine("}");
    return sb.ToString();
}

// vid_win.c: scantokey, PC scan codes (set 1) to key numbers
string GenerateScanToKey()
{
    string c = StripComments(File.ReadAllText(Path.Combine(src, "vid_win.c")));
    // the values are character literals (',' among them), key names and numbers
    var values = Regex.Matches(InitializerBody(c, "scantokey"), @"'(?:\\.|[^'\\])'|K_\w+|\d+").Select(m => m.Value).Select(v =>
        v.StartsWith('\'') ? CharLiteral(v)
        : v.StartsWith("K_", StringComparison.Ordinal) ? keyDefines[v]
        : long.Parse(v, CultureInfo.InvariantCulture)).ToList();
    if (values.Count != 128)
    {
        throw new InvalidOperationException($"scantokey has {values.Count} entries");
    }

    var sb = Header("vid_win.c", "QuakeSharp.Platform.Sdl");
    sb.AppendLine("internal static partial class KeyTranslation");
    sb.AppendLine("{");
    sb.AppendLine("    /// <summary>vid_win.c: scantokey, the key number of each PC scan code (set 1, without the e0 prefix).</summary>");
    sb.AppendLine("    internal static readonly byte[] scantokey =");
    sb.AppendLine("    [");
    foreach (long[] row in values.Chunk(8))
    {
        sb.AppendLine("        " + string.Join(" ", row.Select(v => $"{v},")));
    }

    sb.AppendLine("    ];");
    sb.AppendLine("}");
    return sb.ToString();

    static long CharLiteral(string v)
    {
        string inner = v[1..^1];
        return inner.Length == 2 && inner[0] == '\\' ? inner[1]
            : inner.Length == 1 ? inner[0]
            : throw new InvalidOperationException($"unsupported character literal {v}");
    }
}

// The "#define NAME number" lines of a header whose names start with prefix, in order.
static Dictionary<string, long> Defines(string header, string prefix)
{
    var defines = new Dictionary<string, long>(StringComparer.Ordinal);
    foreach (Match m in Regex.Matches(StripComments(header), $@"^\s*#define\s+({prefix}\w+)\s+(\d+)\s*$", RegexOptions.Multiline))
    {
        defines.Add(m.Groups[1].Value, long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    return defines;
}

// An array of unsigned shorts, as a static field of a partial type (its declaration, e.g. "static partial class Crc").
string GenerateUShortTable(string file, string ns, string type, string name, string summary)
{
    List<long> values = NumericArray(Source(file), name);
    var sb = Header(file, ns);
    sb.AppendLine($"public {type}");
    sb.AppendLine("{");
    sb.AppendLine($"    /// <summary>{file}: {name}. {summary}</summary>");
    sb.AppendLine($"    internal static readonly ushort[] {name} =");
    sb.AppendLine("    [");
    foreach (long[] row in values.Chunk(8))
    {
        sb.AppendLine("        " + string.Join(" ", row.Select(v => $"0x{v:x4},")));
    }

    sb.AppendLine("    ];");
    sb.AppendLine("}");
    return sb.ToString();
}

// musl's constants: every "static const double" declaration of the ported files, the integer tables of
// __rem_pio2_large.c, and the pow and exp tables, each file's in a nested class named after it (__sin.c: __sin_c).
string GenerateLibm()
{
    var sb = Header("*.c", "QuakeSharp.Core.Numerics", MuslTree);
    sb.AppendLine("public static partial class Libm");
    sb.AppendLine("{");
    foreach (string file in new[] { "__sin.c", "__cos.c", "__tan.c", "__rem_pio2.c", "__rem_pio2_large.c", "atan.c", "atan2.c" })
    {
        string c = MuslSource(file);
        sb.AppendLine($"    /// <summary>{file}</summary>");
        sb.AppendLine($"    private static class {file.Replace('.', '_')}");
        sb.AppendLine("    {");
        if (file == "__rem_pio2_large.c")
        {
            // without the long double part (#if LDBL_MAX_EXP > 1024): long double is double here
            string ipio2 = InitializerBody(c, "ipio2");
            ipio2 = ipio2[..ipio2.IndexOf("#if", StringComparison.Ordinal)];
            sb.AppendLine($"        internal static readonly int[] init_jk = [{string.Join(", ", NumericArray(c, "init_jk"))}];");
            sb.AppendLine();
            sb.AppendLine("        internal static readonly int[] ipio2 =");
            sb.AppendLine("        [");
            foreach (string[] row in Values(ipio2).Chunk(6))
            {
                sb.AppendLine("            " + string.Join(" ", row.Select(v => v + ",")));
            }

            sb.AppendLine("        ];");
            sb.AppendLine();
        }

        foreach (Match decl in Regex.Matches(c, @"static\s+const\s+double\s+(.*?);", RegexOptions.Singleline))
        {
            foreach (string item in SplitTopLevel(decl.Groups[1].Value))
            {
                var m = Regex.Match(item, @"^\s*(\w+)\s*(\[\s*\])?\s*=\s*(.*)$", RegexOptions.Singleline);
                string name = m.Groups[1].Value;
                if (m.Groups[2].Success)
                {
                    AppendDoubles(sb, name, Values(m.Groups[3].Value.Trim().TrimStart('{').TrimEnd('}')).Select(v => Evaluate(v, MuslConstant)));
                }
                else
                {
                    sb.AppendLine($"        internal const double {name} = {DoubleLiteral(Evaluate(m.Groups[3].Value, MuslConstant))};");
                }
            }
        }

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    // pow_data.c: __pow_log_data
    string pow = MuslSource("pow_data.c");
    sb.AppendLine("    /// <summary>pow_data.c: __pow_log_data (tab as three arrays: invc, logc, logctail)</summary>");
    sb.AppendLine("    private static class __pow_log_data");
    sb.AppendLine("    {");
    sb.AppendLine($"        internal const double ln2hi = {DoubleLiteral(Evaluate(Field(pow, "ln2hi"), MuslConstant))};");
    sb.AppendLine($"        internal const double ln2lo = {DoubleLiteral(Evaluate(Field(pow, "ln2lo"), MuslConstant))};");
    AppendDoubles(sb, "poly", Values(Braced(pow, "poly")).Select(v => Evaluate(v, MuslConstant)));
    var tab = Regex.Matches(Regex.Replace(Braced(pow, "tab"), @"#[^\n]*", ""), @"A\(([^)]*)\)")
        .Select(m => Values(m.Groups[1].Value).Select(v => Evaluate(v, MuslConstant)).ToArray())
        .ToList();
    AppendDoubles(sb, "invc", tab.Select(t => t[0]));
    AppendDoubles(sb, "logc", tab.Select(t => t[1]));
    AppendDoubles(sb, "logctail", tab.Select(t => t[2]));
    sb.AppendLine("    }");
    sb.AppendLine();

    // exp_data.c: __exp_data, as built with EXP_USE_TOINT_NARROW 0
    string exp = Regex.Replace(MuslSource("exp_data.c"), @"#if EXP_USE_TOINT_NARROW.*?#else(.*?)#endif", "$1", RegexOptions.Singleline);
    sb.AppendLine("    /// <summary>exp_data.c: __exp_data</summary>");
    sb.AppendLine("    private static class __exp_data");
    sb.AppendLine("    {");
    foreach (string name in new[] { "invln2N", "shift", "negln2hiN", "negln2loN" })
    {
        sb.AppendLine($"        internal const double {name} = {DoubleLiteral(Evaluate(Field(exp, name), MuslConstant))};");
    }

    AppendDoubles(sb, "poly", Values(Braced(exp, "poly")).Select(v => Evaluate(v, MuslConstant)));
    sb.AppendLine("        internal static readonly ulong[] tab =");
    sb.AppendLine("        [");
    foreach (string[] row in Values(Braced(exp, "tab")).Chunk(2))
    {
        sb.AppendLine("            " + string.Join(" ", row.Select(v => $"0x{ulong.Parse(v[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture):x16}UL,")));
    }

    sb.AppendLine("        ];");
    sb.AppendLine("    }");
    sb.AppendLine("}");
    return sb.ToString();

    // The named constants in these files' expressions (pow_data.h, exp_data.h, float.h)
    static double MuslConstant(string name) => name switch
    {
        "N" => 1 << 7, // POW_LOG_TABLE_BITS, EXP_TABLE_BITS
        "EPS" or "DBL_EPSILON" => Math.ScaleB(1.0, -52),
        _ => throw new InvalidOperationException($"unknown constant {name}"),
    };

    static void AppendDoubles(StringBuilder sb, string name, IEnumerable<double> values)
    {
        sb.AppendLine($"        internal static readonly double[] {name} =");
        sb.AppendLine("        [");
        foreach (double v in values)
        {
            sb.AppendLine($"            {DoubleLiteral(v)},");
        }

        sb.AppendLine("        ];");
    }

    // The value of a designated initializer: .name = value,
    static string Field(string c, string name) =>
        Regex.Match(c, $@"\.{name}\s*=\s*([^,{{}}]+),").Groups[1].Value;

    // The body of a designated initializer: .name = { ... }
    static string Braced(string c, string name)
    {
        int start = Regex.Match(c, $@"\.{name}\s*=\s*\{{").Index;
        start = c.IndexOf('{', start) + 1;
        int depth = 1;
        for (int i = start; ; i++)
        {
            depth += c[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return c[start..i];
            }
        }
    }

    static IEnumerable<string> Values(string body) =>
        Regex.Replace(body, @"#[^\n]*", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

string Source(string file) => StripComments(File.ReadAllText(Path.Combine(src, file)));

string MuslSource(string file) => StripComments(File.ReadAllText(Path.Combine(musl, file)));

void Write(string relative, string content) => WriteTo("QuakeSharp.Core", relative, content);

void WriteTo(string project, string relative, string content)
{
    string path = Path.Combine(repo, "src", project, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content.ReplaceLineEndings("\n"), new UTF8Encoding(false));
    Console.WriteLine($"wrote {Path.GetRelativePath(repo, path)}");
}

static string StripComments(string c)
{
    c = Regex.Replace(c, @"/\*.*?\*/", " ", RegexOptions.Singleline);
    return Regex.Replace(c, @"//[^\n]*", "");
}

static string InitializerBody(string c, string name)
{
    var m = Regex.Match(c, $@"\b{Regex.Escape(name)}\s*(\[[^\]]*\]\s*)+=\s*\{{");
    if (!m.Success)
    {
        throw new InvalidOperationException($"initializer for {name} not found");
    }

    int start = m.Index + m.Length;
    int depth = 1;
    for (int i = start; i < c.Length; i++)
    {
        depth += c[i] switch { '{' => 1, '}' => -1, _ => 0 };
        if (depth == 0)
        {
            return c[start..i];
        }
    }

    throw new InvalidOperationException($"unterminated initializer for {name}");
}

static List<long> NumericArray(string c, string name) =>
    InitializerBody(c, name).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(v => v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(v[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.Parse(v, CultureInfo.InvariantCulture))
        .ToList();

// Splits at the commas outside braces and parentheses.
static List<string> SplitTopLevel(string s)
{
    var parts = new List<string>();
    int depth = 0;
    int start = 0;
    for (int i = 0; i < s.Length; i++)
    {
        depth += s[i] switch { '{' or '(' => 1, '}' or ')' => -1, _ => 0 };
        if (s[i] == ',' && depth == 0)
        {
            parts.Add(s[start..i]);
            start = i + 1;
        }
    }

    if (s[start..].Trim().Length > 0)
    {
        parts.Add(s[start..]);
    }

    return parts;
}

// Evaluates a C constant expression of numbers (decimal and hexadecimal floating point, integers), named constants,
// unary signs, * and / and parentheses, in double precision as a C compiler folds it.
static double Evaluate(string expr, Func<string, double> constant)
{
    var tokens = Regex.Matches(expr, @"0[xX][0-9a-fA-F]*\.?[0-9a-fA-F]*(?:[pP][+-]?\d+)?[fFlL]?|\d*\.?\d+(?:[eE][+-]?\d+)?[fFlL]?|\w+|[-+*/()]")
        .Select(m => m.Value)
        .ToList();
    int pos = 0;
    double value = Product();
    if (pos != tokens.Count)
    {
        throw new InvalidOperationException($"can't evaluate {expr}");
    }

    return value;

    double Product()
    {
        double v = Unary();
        while (pos < tokens.Count && tokens[pos] is "*" or "/")
        {
            v = tokens[pos++] == "*" ? v * Unary() : v / Unary();
        }

        return v;
    }

    double Unary()
    {
        string t = tokens[pos++];
        return t switch
        {
            "-" => -Unary(),
            "+" => Unary(),
            "(" => Paren(),
            _ when char.IsDigit(t[0]) || t[0] == '.' => Number(t),
            _ => constant(t),
        };
    }

    double Paren()
    {
        double v = Product();
        pos++; // ')'
        return v;
    }
}

// A C numeric literal as a double: hexadecimal floating point is exact (musl's have at most 53 significant bits).
static double Number(string t)
{
    t = t.TrimEnd('f', 'F', 'l', 'L');
    if (!t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
    {
        return double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    var m = Regex.Match(t[2..], @"^([0-9a-fA-F]*)\.?([0-9a-fA-F]*)(?:[pP]([+-]?\d+))?$");
    string digits = m.Groups[1].Value + m.Groups[2].Value;
    int exponent = (m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0) - (4 * m.Groups[2].Value.Length);
    var mantissa = BigInteger.Parse("0" + digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    if (mantissa >= BigInteger.One << 53)
    {
        throw new InvalidOperationException($"{t} has more than 53 significant bits");
    }

    return Math.ScaleB((double)mantissa, exponent);
}

// A C# literal of exactly this double, with its bits for reference.
static string DoubleLiteral(double v)
{
    string s = v.ToString("R", CultureInfo.InvariantCulture);
    if (!s.Contains('.', StringComparison.Ordinal) && !s.Contains('E', StringComparison.Ordinal))
    {
        s += ".0";
    }

    if (BitConverter.DoubleToInt64Bits(double.Parse(s, CultureInfo.InvariantCulture)) != BitConverter.DoubleToInt64Bits(v))
    {
        throw new InvalidOperationException($"{s} does not round-trip");
    }

    return $"{s} /* 0x{BitConverter.DoubleToInt64Bits(v):x16} */";
}

static StringBuilder Header(string origin, string ns, string tree = SourceTree)
{
    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated>");
    sb.AppendLine($"//     Generated by tools/codegen/GenerateTables.cs from {tree}/{origin}.");
    sb.AppendLine("//     Do not edit by hand; change the generator and re-run it.");
    sb.AppendLine("// </auto-generated>");
    sb.AppendLine();
    sb.AppendLine("#nullable enable");
    sb.AppendLine();
    sb.AppendLine($"namespace {ns};");
    sb.AppendLine();
    return sb;
}

static string ScriptPath([CallerFilePath] string path = "") => path;
