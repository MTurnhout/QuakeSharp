using System.Globalization;
using System.Text.RegularExpressions;

namespace QuakeSharp.Core.Tests;

public partial class QuakeDefTests
{
    [Fact]
    public void VersionMatchesReference()
    {
        string value = Define("quakedef.h", "VERSION");
        Assert.Equal(QuakeDef.VERSION, double.Parse(value, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ProtocolVersionMatchesReference()
    {
        string value = Define("protocol.h", "PROTOCOL_VERSION");
        Assert.Equal(QuakeDef.PROTOCOL_VERSION, int.Parse(value, CultureInfo.InvariantCulture));
    }

    private static string Define(string header, string name)
    {
        foreach (string line in File.ReadLines(ReferenceSource.WinQuakeFile(header)))
        {
            Match match = DefineLine().Match(line);
            if (match.Success && match.Groups[1].Value == name)
            {
                return match.Groups[2].Value;
            }
        }

        throw new InvalidOperationException($"#define {name} not found in {header}");
    }

    [GeneratedRegex(@"^#define\s+(\w+)\s+([^\s/]+)")]
    private static partial Regex DefineLine();
}
