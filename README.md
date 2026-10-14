# QuakeSharp

A faithful C# port of id Software's Quake, translated from the original source release
([id-Software/Quake](https://github.com/id-Software/Quake), `WinQuake/`, v1.09), in the spirit of DoomSharp.

- **Faithful output:** WinQuake's software renderer, QuakeC virtual machine, physics and sound mixer, ported
  function by function and checked frame by frame against the original C code built as a reference.
- **Modern repository:** .NET 10, no global state, tests, cross-platform via SDL3, presented with Vulkan.

See [design.md](design.md) for the full design and milestones.

> **Status:** milestones 0-11 done: the Quake file system, the C library's maths functions (from musl)
> and `rand()`, the host, the commands, cvars, console text and keys, the client (demo playback, models, particles,
> the view, the sound mixer), the software renderer, the console, the status bar and the menus, the QuakeC VM and its
> builtins, the server (physics, monsters, the world), the host's server commands, the network layer and WinQuake's
> LAN drivers (loopback, the datagram protocol over TCP/IP, the dedicated server), the sound and the CD, savegames, demo recording,
> `config.cfg`, the mouse and joystick, WinQuake's video modes and both mission packs are ported and checked against
> WinQuake 1.09's own C, built
> headless as a reference oracle that traces every frame: the three demos at three video modes, the attract loop, a
> scripted tour of the menus and scripted games (a new game through the start map, the server's commands and fights,
> an intermission, an episode's finale, the network menus starting a deathmatch, a tour of every map) and a game
> played by hand and recorded in QuakeSharp match it on every frame, pixel for pixel and edict for edict, sample for
> sample; the files the game writes (config.cfg, savegames, demos, screenshots) are the oracle's byte for byte, and
> load back in sync. The mission packs' demos, tours, saves and endings match the same way, and so does the rest of
> the UI. Games between several hosts, a lossy LAN and a dedicated server match it on every frame for every host. The
> executable plays with the keyboard and the mouse, in every mode, with either mission pack, and over a real network
> with Winquake.exe 1.09 and QuakeSpasm, on Windows and Linux.

## Playing

A release (see the repository's Releases) comes in two packages per platform. The `-shareware` one plays at once:
unpack it and run QuakeSharp. It carries id Software's shareware Quake 1.06, `quake106.zip`, untouched and under id's
own licence; QuakeSharp unpacks the first episode from it the first time it runs. The other is QuakeSharp alone: to
play the full game, run it from your Quake folder or copy it there.

## Game data

The repository contains no game data. You need your own Quake: `id1/pak0.pak` and `id1/pak1.pak` from an original
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

Like WinQuake, QuakeSharp looks for `id1/` in the current directory (`-basedir <dir>` names another); without one
there, in its own folder, and then for the shareware's `quake106.zip` beside it (as a release has it):

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

Multiplayer is WinQuake's TCP/IP over UDP (NetQuake's protocol 15, port 26000, `-udpport` for another), on Windows and
Linux alike: QuakeSharp plays with Winquake.exe 1.09 and with QuakeSpasm, as server and as client (QuakeSpasm 0.96.3's
server sends a signon too large for any 1.09 client, Winquake.exe's or QuakeSharp's). As WinQuake: a server on the same
machine is not listed by the search and is joined by the machine's address, not 127.0.0.1; a server keeps one player
per address; on Linux, where the machine's name often resolves to 127.0.1.1, `-ip <address>` gives the real one.

`-recordevents <file>` records a game for the fidelity tests: QuakeSharp then runs as the oracle's traces do (a
virtual clock of 1/64 s steps paced to real time, no `config.cfg`) and writes every key as a `-traceevents` line; on
quitting it prints the frame count. A network game is recorded too: what arrived from the network goes to
`<file>.dgrams` beside it, for the replay's `-tracedatagrams`. The oracle (`quake-oracle.exe -traceevents <file> -oracleframes <n>`, from its
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

## Texture packs

The enhanced mode (Scroll Lock, or `-enhanced`) can draw **replacement textures** for the world, the skies and the
alias models' skins, and normal and gloss maps that the dynamic lights (muzzle flashes, rockets, explosions) light per
pixel. QuakeSharp ships none and runs no upscaler: a pack is made on your own machine from your own game, or taken from
one made for QuakeSpasm or DarkPlaces, whose names it uses. Packs apply in the true-colour look only
(`enhanced_lighting 0`); the colormap look and the faithful mode never use them, and without a pack nothing changes.

**Where packs go.** Loose PNG or TGA files under `textures/` and `progs/` in a game directory: first in the user folder
(`%APPDATA%\QuakeSharp\id1\`, `~/.config/QuakeSharp/id1/`), then in the Quake folder's (`id1/`; a mission pack's own
directory comes before `id1`'s). Not in PAK or zip files, not DDS. Names are matched without regard to case.

| What | File |
|---|---|
| A world texture | `textures/<map>/<name>`, else `textures/<name>` (`*` in a name is `#`: `#water0`) |
| Fullbright texels, normals, gloss | `<name>_glow` (or `_luma`), `<name>_norm`, `<name>_gloss` |
| The sky | `textures/sky4` (the BSP's two layers side by side) |
| An alias skin | `progs/<model>.mdl_<skin>`, with the same layers |
| Players' colours | `progs/player.mdl_<skin>_pants`, `_shirt` (grey layers; both are needed) |

A replacement may be any multiple of the game's size. The tools below write and check these names.

**Making one.** The `quakesharp-tools` commands (see Developer tools) write only outside the repository:

```bash
quakesharp-tools export-textures path/to/Quake export -tiled
realesrgan-ncnn-vulkan -i export/<folder> -o upscaled/<folder> -n realesrgan-x4plus   # each folder; any upscaler will do
quakesharp-tools texture-pack-finish export upscaled path/to/Quake/id1
quakesharp-tools texture-pack-materials path/to/Quake/id1
quakesharp-tools texture-pack-check path/to/Quake path/to/Quake/id1
```

`export-textures` writes every texture a pack can replace under its pack name (`-tiled` repeats world textures 3×3 so
an upscaler sees their edges as they repeat); `texture-pack-finish` crops the tiling again and keeps the game's colour
at a coarse scale under the upscale's detail, as upscalers darken Quake's dark textures; `texture-pack-materials`
derives `_norm` and `_gloss` from the colours for an upscaler that makes colours only (an AI material generator's maps
work as well; a layer a pack has is kept); `texture-pack-check` lists what the pack replaces and lacks and any file
that does not decode or whose size is not a multiple of the game's. Real-ESRGAN's x4plus model draws metal, wood and
signs sharply; its anime models do not suit Quake. Ground and rock textures are redrawn rather than sharpened, a
matter of taste.

**Settings** (`quakesharp.cfg` in the user folder, read at start):

| Setting | Default | |
|---|---|---|
| `enhanced_textures` | 1 | a pack's textures where it has them |
| `enhanced_materials` | 1 | a pack's normal and gloss maps, lit by the dynamic lights (world surfaces only) |
| `enhanced_lighting` | 0 | 0 true colour with overbright (packs apply), 1 the colormap's 64 levels (they do not) |

A pack is read on a loader thread: after a level starts, a few of its surfaces may show the game's textures for a
moment.

## Mouse and controller

The faithful mode (Scroll Lock off, or `-faithful`) plays as WinQuake does: in a window the mouse works only after
Options → Use Mouse (`_windowed_mouse`), looking up and down needs `+mlook` (`\` by default), and a controller needs
`joystick 1` and bindings. The enhanced mode (Scroll Lock, or `-enhanced`) adds a modern set on top, writing nothing to
`config.cfg`; keys you have bound yourself always keep your binding.

- **Mouse:** taken by the window as soon as it has the focus (let go while paused), free look, the wheel changes
  weapon. Left button fires, right button walks forward, middle button holds `+mlook`, as the game binds them. Setting:
  `enhanced_mouse`.
- **Running:** the default; the speed key (Shift) walks. Setting: `enhanced_alwaysrun`.
- **Controller** (any gamepad SDL knows, plugged in at any time): left stick walks and strafes, right stick looks; right
  trigger fires, A jumps, B swims down, X swims up, Y centres the view, the bumpers and the D-pad's left and right
  change weapon, Back shows the scores, Start opens the menu. In the menus the D-pad or left stick moves, A selects, B
  goes back. Settings: `enhanced_gamepad`, `enhanced_gamepad_deadzone` (0.15) and `enhanced_gamepad_sensitivity` (1). The
  keys are `JOY1`-`JOY4`, `AUX5`-`AUX12` and `AUX29`-`AUX32`, so `bind AUX12 "impulse 7"` changes what the right trigger
  does.

## Enhanced mode's extras

Besides the renderer, texture packs and input above, the enhanced mode has these, all in `quakesharp.cfg` and none of
them reaching the game:

- **Look:** `enhanced_2d` (on) rounds the staircase edges of the status bar, menus and console text;
  `enhanced_bloom` (0; 0.5 is soft) adds a glow round bright parts of the view, from `enhanced_bloom_threshold` (0.7);
  `enhanced_vignette` (0; up to 1) darkens the corners; `enhanced_fxaa` (off) smooths edges further. With
  `enhanced_filter 1` the sky is filtered as the textures are.
- **Menus:** the mouse pointer shows in the menus: point to choose, click to enter, right-click to go back, click a
  slider's left or right half to move it.
- **Window:** Alt+Enter switches between a window and full screen; the window's size and place are kept for the next
  run. Print Screen saves the frame as PNG in the user folder's `screenshots`. `enhanced_showfps 1` shows the frame rate
  in the title.
- **Sound:** `enhanced_reverb` (0; 0.2 is subtle) adds a room's echo.
- **Controller:** it rumbles when you are hurt (`enhanced_gamepad_rumble`).

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
