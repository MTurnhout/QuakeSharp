using System.Globalization;
using System.Text;

namespace QuakeSharp.Core.Enhanced;

/// <summary>
/// The enhanced mode's settings (design.md decision 61): QuakeSharp's own <c>quakesharp.cfg</c> in the user folder,
/// lines of <c>name "value"</c> as config.cfg's, read and written by the executable and never Quake's cvars, so the
/// game's commands, cvars and config.cfg stay WinQuake's. The defaults are decisions 62-64's.
/// </summary>
public sealed class EnhancedSettings
{
    /// <summary>The settings and their defaults, in the file's order.</summary>
    public static readonly IReadOnlyList<(string Name, string Default, string Description)> All =
    [
        ("enhanced", "0", "1: the enhanced mode (Scroll Lock switches it; -enhanced, -faithful for a run)"),
        ("enhanced_scale", "1", "the 3D's resolution as a share of the window's (0.25-2)"),
        ("enhanced_msaa", "4", "samples per pixel: 1, 2, 4 or 8"),
        ("enhanced_filter", "0", "textures: 0 nearest, 1 linear"),
        ("enhanced_mipmaps", "1", "textures' mip levels: 0 the BSP's own four, 1 full chains made from their colours"),
        ("enhanced_anisotropy", "16", "anisotropic filtering, 1 (off) to 16"),
        ("enhanced_lighting", "0", "0 true colour with overbright, 1 the colormap's 64 levels (the software's look)"),
        ("enhanced_particles", "0", "0 square, 1 round"),
        ("enhanced_widescreen", "1", "1: the view widened to the window's shape (the 2D stays 4:3)"),
        ("enhanced_vsync", "1", "1: frames at the display's pace"),
        ("enhanced_maxfps", "0", "the most frames a second without vsync (0: no limit)"),
        ("enhanced_interpolate", "1", "1: frames between the game's (at most 1/72 s behind)"),
        ("enhanced_lerpmodels", "0", "1: alias models' poses blended"),
        ("enhanced_lerpmove", "0", "1: monsters moving in steps smoothed"),
        ("enhanced_sound", "1", "1: the sound mixed at the device's rate"),
        ("enhanced_3d_sfx", "0", "1: sounds placed by direction"),
    ];

    private readonly Dictionary<string, string> values = All.ToDictionary(s => s.Name, s => s.Default, StringComparer.Ordinal);

    /// <summary>A setting's value, as written.</summary>
    public string this[string name]
    {
        get => values[name];
        set
        {
            if (!values.ContainsKey(name))
            {
                throw new ArgumentException($"no enhanced setting {name}", nameof(name));
            }

            values[name] = value;
        }
    }

    /// <summary>The enhanced mode itself.</summary>
    public bool Enhanced
    {
        get => Number("enhanced") != 0;
        set => values["enhanced"] = value ? "1" : "0";
    }

    /// <summary>A setting's value as a number (0 where it is not one).</summary>
    public float Number(string name) =>
        float.TryParse(values[name], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0;

    /// <summary>The settings of a file's text: <c>name "value"</c> or <c>name value</c> lines; <c>//</c> starts a
    /// comment; names it does not know and lines it cannot read are left out.</summary>
    public static EnhancedSettings Parse(string text)
    {
        var settings = new EnhancedSettings();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw;
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
            {
                line = line[..comment];
            }

            line = line.Trim();
            int space = line.IndexOfAny([' ', '\t']);
            if (space <= 0)
            {
                continue;
            }

            string name = line[..space];
            string value = line[space..].Trim().Trim('"');
            if (settings.values.ContainsKey(name))
            {
                settings.values[name] = value;
            }
        }

        return settings;
    }

    /// <summary>The file's text: every setting with its value and what it does.</summary>
    public string Write()
    {
        var text = new StringBuilder();
        text.Append("// QuakeSharp's enhanced mode (design.md §11.6); Quake's own settings are in config.cfg\n");
        foreach ((string name, _, string description) in All)
        {
            text.Append(CultureInfo.InvariantCulture, $"{name} \"{values[name]}\" // {description}\n");
        }

        return text.ToString();
    }

    /// <summary>The settings in <paramref name="path"/>; the defaults where it does not exist or cannot be read.</summary>
    public static EnhancedSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : new EnhancedSettings();
        }
        catch (IOException)
        {
            return new EnhancedSettings();
        }
    }

    /// <summary>Writes the settings to <paramref name="path"/>.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Write());
    }
}
