namespace QuakeSharp.Core.Enhanced;

/// <summary>
/// A texture pack's file names (design.md §11.7, decision 68): DarkPlaces' names, which packs made for it and for
/// QuakeSpasm use. Each is a path in a game directory with '/' and without an extension (a pack's file is PNG or TGA).
/// </summary>
public static class EnhancedPackNames
{
    /// <summary>Layers beside a texture: its fullbright texels (<see cref="Glow"/>, or Tenebrae's
    /// <see cref="Luma"/>), its normals, its gloss, and the players' colours' masks.</summary>
    public const string Glow = "_glow";

    public const string Luma = "_luma";
    public const string Norm = "_norm";
    public const string Gloss = "_gloss";
    public const string Pants = "_pants";
    public const string Shirt = "_shirt";

    /// <summary>The extensions a pack's file may have, in the order they are looked for.</summary>
    public static readonly string[] Extensions = [".png", ".tga"];

    /// <summary>A BSP texture's name in a file name: '*' (water, slime, lava, teleporters) as '#'.</summary>
    public static string TextureFile(string name) => name.Replace('*', '#');

    /// <summary>A world texture's names, in the order they are looked for: the map's own (<paramref name="map"/>,
    /// the BSP's base name, e.g. "e1m1"), then every map's.</summary>
    public static string[] WorldTexture(string map, string name) =>
        [$"textures/{map}/{TextureFile(name)}", $"textures/{TextureFile(name)}"];

    /// <summary>A map's own texture (a texture differing from another map's of the same name).</summary>
    public static string MapTexture(string map, string name) => $"textures/{map}/{TextureFile(name)}";

    /// <summary>A texture every map shares.</summary>
    public static string SharedTexture(string name) => $"textures/{TextureFile(name)}";

    /// <summary>An alias model's skin: <paramref name="model"/> as the game names it ("progs/player.mdl"), the skin's
    /// number, and for a skin group the frame's.</summary>
    public static string Skin(string model, int skin, int frame = -1) =>
        frame < 0 ? $"{model}_{skin}" : $"{model}_{skin}_{frame}";

    /// <summary>Whether a BSP texture is a sky (model.c: Mod_LoadTextures' "sky" prefix).</summary>
    public static bool IsSky(string name) => name.StartsWith("sky", StringComparison.Ordinal);

    /// <summary>Whether a BSP texture is turbulent (model.c: '*', drawn unlit by d_scan.c).</summary>
    public static bool IsTurbulent(string name) => name.StartsWith('*');
}
