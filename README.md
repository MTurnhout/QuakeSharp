# QuakeSharp

A faithful C# port of id Software's Quake, translated from the original source release
([id-Software/Quake](https://github.com/id-Software/Quake), `WinQuake/`, v1.09), in the spirit of DoomSharp.

- **Faithful output:** WinQuake's software renderer, QuakeC virtual machine, physics and sound mixer, ported
  function by function and checked frame by frame against the original C code built as a reference.
- **Modern repository:** .NET 10, no global state, tests, cross-platform via SDL3, presented with Vulkan.

See [design.md](design.md) for the full design and milestones.

> **Status:** milestone 1 (files and data): the Quake file system, pack files and gfx.wad are ported, and game
> data is identified by MD5. Nothing is playable yet.

## Game data

QuakeSharp contains no game data. You need your own Quake: `id1/pak0.pak` and `id1/pak1.pak` from an original
release (1.06 or later). The 2021 rerelease ships modified data in its `rerelease/` folder; the Steam install keeps
the original PAKs in `id1/`.

## Building

Requires the .NET 10 SDK, and the [Vulkan SDK](https://vulkan.lunarg.com/) (`VULKAN_SDK` set) for the shaders.

```bash
git clone --recurse-submodules <repo-url>
dotnet build QuakeSharp.slnx
dotnet run --project src/QuakeSharp
```

## Developer tools

```bash
dotnet run --project tools/QuakeSharp.Tools -- identify path/to/Quake
```

```bash
dotnet run --project tools/QuakeSharp.Tools -- files path/to/Quake
```

```bash
dotnet run --project tools/QuakeSharp.Tools -- dump-graphics path/to/Quake path/to/output
```

`identify` names the PAK files it knows by MD5; `files` shows the search path and every file in it, as the engine
sees them (engine switches such as `-game <dir>`, `-rogue` or `-hipnotic` may follow); `extract <quake-dir> <file>
<out>` copies one file out; `dump-graphics` writes the palette, colormap, pictures, gfx.wad, map textures, model skins
and sprite frames as PNG. Write the output outside the repository (the tools refuse to write inside it): extracted
game data must never be committed.

## Testing

```bash
dotnet test --solution QuakeSharp.slnx
```

Fidelity tests need the original game data in `QUAKESHARP_QUAKE_DIR` and reference traces in
`QUAKESHARP_TRACE_DIR`; without them they skip.

## License

GPL-2.0, as the original Quake source release. See [LICENSE](LICENSE).
Quake is a trademark of id Software. This project is not affiliated with or endorsed by id Software.
