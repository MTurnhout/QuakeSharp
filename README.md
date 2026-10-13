# QuakeSharp

A faithful C# port of id Software's Quake, translated from the original source release
([id-Software/Quake](https://github.com/id-Software/Quake), `WinQuake/`, v1.09), in the spirit of DoomSharp.

- **Faithful output:** WinQuake's software renderer, QuakeC virtual machine, physics and sound mixer, ported
  function by function and checked frame by frame against the original C code built as a reference.
- **Modern repository:** .NET 10, no global state, tests, cross-platform via SDL3, presented with Vulkan.

See [design.md](design.md) for the full design and milestones.

> **Status:** milestone 11 (milestones 0-10 done): the Quake file system, the C library's maths functions (from musl)
> and `rand()`, the host, the commands, cvars, console text and keys, the client (demo playback, models, particles,
> the view, the sound mixer), the software renderer, the console, the status bar and the menus, the QuakeC VM and its
> builtins, the server (physics, monsters, the world), the host's server commands and the network layer (loopback, and
> the datagram protocol over a virtual TCP/IP driver), the sound and the CD, savegames, demo recording,
> `config.cfg`, the mouse and joystick, WinQuake's video modes and both mission packs are ported and checked against
> WinQuake 1.09's own C, built
> headless as a reference oracle that traces every frame: the three demos at three video modes, the attract loop, a
> scripted tour of the menus and scripted games (a new game through the start map, the server's commands and fights,
> an intermission, an episode's finale, the network menus starting a deathmatch, a tour of every map) and a game
> played by hand and recorded in QuakeSharp match it on every frame, pixel for pixel and edict for edict, sample for
> sample; the files the game writes (config.cfg, savegames, demos, screenshots) are the oracle's byte for byte, and
> load back in sync. The mission packs' demos, tours, saves and endings match the same way, and so does the rest of
> the UI. The executable plays with the keyboard and the mouse, in every mode, with either mission pack.

## Game data

QuakeSharp contains no game data. You need your own Quake: `id1/pak0.pak` and `id1/pak1.pak` from an original
release (1.06 or later), or the shareware's `id1/pak0.pak` alone (`quake106.zip`, the first episode). The mission
packs need their `hipnotic/pak0.pak` or `rogue/pak0.pak` too (`-hipnotic`, `-rogue`). The 2021 rerelease ships
modified data in its `rerelease/` folder; the Steam install keeps the original PAKs in `id1/`.

## Building

Requires the .NET 10 SDK, and Slang's `slangc` for the shaders: from the [Vulkan SDK](https://vulkan.lunarg.com/)
(`VULKAN_SDK` set), or any Slang release with `SLANGC` set to its `slangc`. Running needs a GPU with Vulkan 1.3.

```bash
git clone --recurse-submodules <repo-url>
dotnet build QuakeSharp.slnx
```

Like WinQuake, QuakeSharp looks for `id1/` in the current directory (`-basedir <dir>` names another):

```bash
cd path/to/Quake
dotnet run --project path/to/QuakeSharp/src/QuakeSharp -- -window
```

Closing the window quits. Settings (`config.cfg`) and everything else the game writes go to `%APPDATA%\QuakeSharp`
(`~/.config/QuakeSharp` on Linux), never into the Quake folder. `-window` opens a window instead of covering the
screen, `-noaspect` shows square pixels instead of a 4:3 picture,
`-vkvalidation` turns on the Vulkan validation layer (it comes with the Vulkan SDK).

`-dedicated [<players>]` runs a dedicated server, as WinQuake's: no window, video or sound, its console on standard
output, and commands typed at it on standard input (8 players by default, 16 at most).

`-recordevents <file>` records a game for the fidelity tests: QuakeSharp then runs as the oracle's traces do (a
virtual clock of 1/64 s steps paced to real time, no `config.cfg`) and writes every key as a `-traceevents` line; on
quitting it prints the frame count. The oracle (`quake-oracle.exe -traceevents <file> -oracleframes <n>`, from its
Quake folder) and `quakesharp-tools trace` then replay the same game, so it can be compared frame by frame. The
mouse's motion and buttons are recorded too.

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

## The reference oracle

QuakeSharp is checked against WinQuake 1.09's C code built as a headless program that writes a trace of every frame;
see [tools/oracle](tools/oracle/README.md). It needs LLVM's clang-cl and Visual Studio's C++ tools.

## Testing

```bash
dotnet test --solution QuakeSharp.slnx
```

Fidelity tests need the original game data in `QUAKESHARP_QUAKE_DIR` and reference traces in
`QUAKESHARP_TRACE_DIR`; without them they skip. The shareware smoke test needs only an original `id1/pak0.pak` there
(the shareware's, which CI downloads, or the registered one) and no traces.

## License

GPL-2.0, as the original Quake source release. See [LICENSE](LICENSE).
Quake is a trademark of id Software. This project is not affiliated with or endorsed by id Software.
