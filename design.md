# QuakeSharp: Design

> Status: **v0.7**. Plan agreed (§12); the smaller open decisions are in §13 with proposed defaults. Milestones 0
> (repository skeleton), 1 (files and data), 2 (the oracle and the numerics: WinQuake built headless writes traces of
> every demo; musl's libm and `rand()` ported and checked against the C), 3 (the platform: an SDL3 window showing
> 8-bit frames through Vulkan, keys as WinQuake reads them, the sound device), 4 (the host and the common code:
> the frame loop, commands, cvars, console text, keys, `mathlib.c`, printf, all checked against WinQuake's own C), 5
> (the client: demo1-3 and the attract loop play with the client state, the sound channels, the console text and the
> view the oracle's on every frame), 6 (the software renderer and the 2D, pixel-exact), 7 (the QuakeC VM and the
> server, in sync with the oracle on every scripted game and on a game played by hand), 8 (the sound: the mixer
> sample-exact and the CD's state in sync on every trace, the CD's music from the user's files) and 9 (persistence:
> config.cfg, savegames, demos, screenshots and the console log byte for byte as the oracle writes them, saves and
> demos read back in sync) done.

## 1. Goal

QuakeSharp is a faithful C# port of id Software's Quake (1996), translated from the original source release
(<https://github.com/id-Software/Quake>, `WinQuake/`), in the spirit of DoomSharp (the same approach applied to
DOOM).

Two principles drive every decision:

| | Principle | Meaning |
|---|---|---|
| **Output** | *Faithful* | Gameplay, rendering and audio behave as WinQuake 1.09's C code does. Demos play back as in the original; a game played with the same input gives the same game, frame for frame. |
| **Repository** | *Modern* | Current .NET, clean project structure, no global mutable state, tests, CI, analyzers, cross-platform. |

The rule of thumb, as in DoomSharp: **anything that changes what the player sees, hears or experiences stays as the
original. Anything the player never sees (memory management, file I/O, platform layer, build system) is
modernised.**

### Non-goals (v1)

- Source-port features: mouselook beyond the original's `+mlook`, higher limits, new protocol versions, fog,
  skyboxes, coloured lighting, `.lit` files, external textures, interpolated models. Hardware-rendered and
  higher-fidelity pictures exist only in an optional enhanced mode (§6.6) that only reads the game.
- Shipping game data. QuakeSharp contains **no** copyrighted PAK data; the user supplies their own `id1/pak0.pak`
  (shareware or registered) and `id1/pak1.pak`.
- The DOS and Windows drivers of the time (IPX, serial and modem links, DirectDraw, VESA, MGL, Sound Blaster DMA):
  their roles are taken by SDL3, Vulkan and .NET sockets.
- Bit-exact agreement with the 1996 executables' x87 arithmetic and assembly (§3.1): the fidelity target is the C
  code, evaluated with defined single-precision semantics.

## 2. Reference sources & fidelity target

| Source | Role |
|---|---|
| `id-Software/Quake` (`WinQuake/`, v1.09) | **Primary reference.** The C code we translate, module by module. Read-only git submodule `reference/Quake`. Also holds `QW/` and `qw-qc/` (QuakeWorld, later). |
| WinQuake 1.09 built from that source (§10) | **The oracle.** The C code itself, built headless with a trace patch, decides what "correct" means for every frame, state hash and sound. |
| The DOS and Windows executables (`quake.exe`, `Winquake.exe`, in the Steam install) | Behaviour reference for what the source does not show (to be audited case by case, like DoomSharp's DOS audit). |
| Chocolate Quake (Henrique194/chocolate-quake) | **Secondary reference**: a purist port that documents how the DOS releases (1.06-1.08) differ from 1.09. GPLv3: read and run only, never copied into QuakeSharp (GPL-2.0-only). |

The game code that runs on the server is QuakeC: `progs.dat` from the user's PAK0, executed by the ported VM. Its
1.06 source is not in id's repository and is not needed (the bytecode is the game); `qw-qc/` is QuakeWorld's.

### Game data

Identified by MD5 at startup, so the game mode is chosen reliably:

| File | Version | MD5 |
|---|---|---|
| `id1/pak0.pak` | Quake registered 1.06 (also in 1.08, 1.09) | `5906e5998fc3d896ddaf5e6a62e03abb` |
| `id1/pak1.pak` | Quake registered 1.06 | `d76b3e5678f0b64ac74ce5e340e6a685` |
| `hipnotic/pak0.pak` | Mission Pack 1, Scourge of Armagon (to verify) | `0ab83681aaf841c4320269e02941a14a` |
| `rogue/pak0.pak` | Mission Pack 2, Dissolution of Eternity (to verify) | `c38a4e04219c317cd1b02f386bdfe11f` |

The shareware `pak0.pak` (1.06, freely distributable as part of `quake106.zip`) will be added when CI fetches it
(§10).

**Rerelease data is not original.** The 2021 rerelease ("KEX", the Steam install's `rerelease/` folder) ships
modified maps, models and progs. It is *not* valid for fidelity tests. QuakeSharp may load it with a warning, with no
fidelity promise. Its `id1/music/track02-11.ogg` are the CD soundtrack (see §7.2).

## 3. Fidelity contract

| Area | Requirement |
|---|---|
| Server: physics, QuakeC VM, world, `host_cmd` (`sv_*`, `pr_*`, `world`) | **Bit-exact** to the oracle (§3.1): same float results, same edict memory, same `rand()` sequence, same entity order, same bugs. Proven by state traces of scripted games. |
| Client (`cl_*`, `view`, `chase`) | **Bit-exact**: message parsing, interpolation, view bob/roll/kick, palette shifts, temp entities, particles. Proven by the demo traces. |
| Network messages | Byte for byte (protocol 15): demos, loopback and UDP; QuakeSharp and other NetQuake clients and servers interoperate. |
| Software renderer (`r_*`, `d_*`) | **Pixel-exact** 8-bit framebuffer at every supported video mode, including the renderer's quirks and limits. |
| 2D: console, menus, status bar, centre prints, intermission, finale (`draw`, `screen`, `console`, `menu`, `sbar`) | **Pixel-exact.** |
| Palette & gamma | `gfx/palette.lmp`, `colormap.lmp`, `V_UpdatePalette`'s shifts and the `gamma` cvar, as the C code computes them. |
| Sound effects | **Sample-exact** mix: `snd_dma.c`, `snd_mix.c`, `snd_mem.c` ported (channels, spatialisation, resampling, the integer mixer), so the paint buffer matches the oracle's at the same output rate. |
| Music | The CD soundtrack is not in the PAKs: `CDAudio_*` calls are traced and played from the user's audio tracks (§7.2). |
| Savegames, `config.cfg`, demos | Text and bytes as the original writes them; each reads the other's files. |
| Command line, console commands, cvars | Original names, defaults and behaviour. |
| **Free to modernise** | Memory management (zone, hunk, cache become managed memory with the original limits kept where they change behaviour), file I/O, windowing, presentation (scaling, aspect), input devices, audio output device, logging, error handling, build & tests. |

### 3.1 Floating point

Quake's game state is floating point. The original executables compute with the x87 FPU (MSVC 6 for WinQuake,
DJGPP for DOS), whose intermediate precision depends on the compiler's register allocation and on the precision
control word (`Sys_SetFPCW`; the renderer switches to 24-bit precision around `R_RenderView`), plus hand-written
assembly for the hot paths (`id386`). That cannot be reproduced bit for bit in C#, so the fidelity contract is the C
code's own semantics, made deterministic and identical on both sides (decision 6):

- **Every float operation is rounded to IEEE single precision** (C's `FLT_EVAL_METHOD` 0), every double operation to
  IEEE double. This is what .NET does on x64 and ARM64 (SSE2 / NEON scalar ops), and what a C compiler does when
  targeting SSE2. C's usual arithmetic conversions (`float * 1.0` is a `double`) are kept exactly in the C#: the
  type of every literal and intermediate is chosen as in the C, never "simplified".
- **The oracle is built C-only** (`id386` 0, so the C fallbacks of every assembly routine, which the Linux/non-Intel
  builds use), for 32-bit x86 with SSE2 math (clang-cl `-m32 -msse2 -mfpmath=sse`, no FMA contraction). 32-bit
  keeps the original's pointer arithmetic (`pr_strings` offsets, edict pointers) working unchanged.
- **Transcendental functions** (`sin`, `cos`, `tan`, `atan`, `atan2`, `pow`: the only ones the C code calls;
  `sqrt`, `floor`, `ceil` and `fabs` are exact everywhere) are not correctly rounded by C runtimes, and .NET calls
  the platform's (UCRT on Windows, glibc on Linux). One implementation serves both sides: musl 1.2.5's (MIT,
  read-only submodule `reference/musl`) is ported to C# (`Libm`, its constants and tables generated from the C), and
  the oracle is linked to the same C, with the functions renamed `musl_*` so they never mix with the C runtime's.
  `tools/oracle/numerics.c` runs musl's C on 18 sets of 100,000 inputs (random bit patterns, float values, the
  ranges Quake uses) and `LibmTests` the C# on the same inputs: every result is the same, bit for bit.
- **Float to integer conversions** in C are undefined out of range; x86's `cvttss2si` gives `0x80000000`, while
  .NET 9 and later saturate. Ported casts that can go out of range use a helper with the x86 result.
- **`rand()`** is the Microsoft C runtime's (`seed * 214013 + 2531011`, bits 16-30), as WinQuake.exe and an oracle
  built against the UCRT have it. It is one generator shared by the server (QuakeC's `random()`), the client (temp
  entities, dynamic lights, `syncbase`), the particles and the menu, and `_Host_Frame` draws one every call: the
  order of calls is part of the fidelity contract, so it lives on the engine root, and the time step is
  deterministic (§6.3).
- **Formatting and parsing**: `sprintf("%5.1f")` (QuakeC's `ftos`, `vtos`), `%f` in savegames and `Q_atof` are
  ported or emulated exactly (correctly rounded decimal output, as the UCRT prints). `CFormat.Sprintf` is the C
  library's printf for the conversions Quake uses: `%f` rounds the double's exact value, halfway cases to even, which
  the oracle's UCRT confirms.

### 3.2 The C as compiled

What the C leaves to the compiler is part of the target, as the oracle's build (and WinQuake's MSVC 6 build) does it:

| What | The C | QuakeSharp |
|---|---|---|
| Order of evaluation of function arguments | Unspecified; MSVC, and clang-cl for its ABI, evaluate them right to left (the oracle's first test printed its two `rand()` arguments in that order) | Where two arguments have side effects (`rand()`, `MSG_Read*`, `COM_Parse`...), they are evaluated into temporaries in the oracle's order, last argument first |
| Calls without a prototype (C89's implicit declarations) | Arguments get the default promotions: a float is passed as a double, an int as an int, whatever the callee expects | The callee gets what it would read. Quake's software engine has one such call that matters: chase.c's `TraceLine` calls `SV_RecursiveHullCheck (hulls, 0, 0, 1, ...)`, so `p1f` is 0.0 and `p2f` is the float with the bits of the int 1 (1.4e-45), not 1.0 (only `trace.fraction` is affected; `TraceLine` uses `endpos`). The oracle's own sources are compiled with implicit declarations as errors |
| Signed `char` | `char` is signed: characters of 128 and over are negative (`Q_atoi`'s `'x` form, comparisons): `COM_Parse` and `Con_Print` take them for white space, which ends a token or a word | Strings are read through `QString.Ch`, which gives a signed char |
| A quoted string a line ends in (`COM_Parse`) | The parse stops at the line's 0 and returns the position after it, so the next token is read from what the buffer holds after the 0: `Cbuf_Execute`'s `char line[1024]` keeps what longer lines before it left (`echo "abc` after `echo with // trailing comment` also echoes `ment`) | `Cbuf_Execute` keeps its line buffer and passes it whole, so the same happens. Between frames the C's buffer is stack that other code may have written over; QuakeSharp's keeps what the last line left. A line of 1024 characters or more overruns the C's buffer; QuakeSharp's grows |
| Memory limits | The zone (48 KB: cvar strings, aliases, bindings) and the hunk (16 MB) stop the game with `Z_Malloc: failed` or `Hunk_Alloc: failed` when full | Managed memory: what would fill them runs on. Behaviour the game shows otherwise, such as which models the cache keeps, does not depend on it |
| Reading a savegame (`Host_Loadgame_f`, `M_ScanSaves`: `fopen` in text mode, `fscanf`'s `"%i\n"`, `"%f\n"`, `"%s\n"`) | The Universal CRT's: CR LF read as LF, Ctrl-Z ends the file; `%f` also reads hexadecimal numbers (`0x1.8p3`) and fails on an exponent without digits (`2e`); `%i` reads into 64 bits, saturating, and keeps the low 32. The numeric conversions take a 0xff byte for EOF: a number ends there (and fails if it starts there), and pushing it back does nothing, so it is lost (after a float, a second 0xff too). The `"\n"` runs only after a value was read | `CStdioReader`, checked against the oracle's `fscanf` on 20,000 inputs of each (`OracleUnitTests`: `scanf.float`, `scanf.int`, `scanf.string`; `crt_atof` checks `atof`) |

The DOS/Windows x87 differences are then documented, not emulated, like DoomSharp's table of DOS differences: they
show up only in the last bits of positions and angles.

### 3.3 Undefined behaviour

Where the C reads memory it does not own, QuakeSharp has a fixed substitute (§6.2 rule 11); the oracle is patched to
share it only where a trace shows the difference.

| Where | The C | QuakeSharp |
|---|---|---|
| `cl_tent.c`: `CL_UpdateTEnts` | The loop that adds a beam's lightning entities reuses the beam loop's `i`, while `b` moves on separately: after a beam that made entities, `i` starts again from 4 and `b` goes on past `cl_beams[MAX_BEAMS]` when a beam at index 4 or more was active, reading the memory that follows | The loop keeps both (it ends early as the C's does); beams past the array count as unused. demo1-3 never reach it |
| `cl_demo.c`: `CL_PlayDemo_f` | Reading the CD track line, `getc` gives EOF for ever at the end of a file without a newline: the loop does not end | The loop ends at the end of the file |
| `r_alias.c`: `R_AliasDrawModel` | The vertices behind the near plane are not projected, so their `v[0]`, `v[1]`, `v[5]` are what the stack array `finalverts` held; `R_Alias_clip_z` picks its direction of interpolation from `v[1]`, which moves a clipped vertex's `t` by one (demo3 at 640x480, frame 2760) | `finalverts` starts zeroed; the oracle is patched to zero it too (`quake-oracle.patch`) |
| `common.c`: `COM_FileBase` | For a name without `/` (`progs.dat`, which `PR_LoadProgs` loads) the loop looking for the `/` walks back past the name's start, through the memory before it, to a 0 or a `/`: with the oracle's string literals, the one before `"progs.dat"` went into `COM_LoadFile`'s `base[32]` and overran the stack | The loop stops at the start, as if a 0 were before it (the base is `progs`); the oracle is patched to do the same. The base only names the hunk's blocks |
| `net_main.c`: `NET_SendToAll` | A client slot without a connection is skipped before its two states are set, so the loop that waits for the slots reads whatever the stack held: if it reads "not done" the loop waits until `blocktime` (for ever on the virtual clock) | Such a slot is done; the oracle is patched to do the same |
| `pr_edict.c`: `ED_ParseEpair` | A vector value is copied into `char string[128]` and split at three spaces; with fewer than three numbers the split goes on past the copy's 0 into what the array held before | Past the copy the array holds zeros (the missing numbers are 0); a value of 128 characters or more, which overruns the C's array, is copied whole |
| `cl_demo.c`: `CL_GetMessage` | At the end of a demo the `fread`s of the view angles fail, and the local `f` they would have filled, uninitialised, is copied into `cl.mviewangles[0]` (a demo cut short, `record.d3`, shows it on its last frame) | `f` starts at 0 in each call; the oracle is patched to do the same |
| `host_cmd.c`: `Host_Loadgame_f` | A savegame that lacks a value leaves its variable as it was: the version, the spawn parms, the skill, the map's name and the time are uninitialised locals | They start at 0 or empty; a light style that cannot be read is the last string read, as the C's `str` keeps it |
| `pr_cmds.c`: `PF_break` | Writes to the address -4 to stop in the debugger: the program crashes | A `Sys_Error` ("break statement") |
| `snd_dma.c`: `S_SoundInfo_f` | `soundinfo` prints the DMA buffer's address (`0x%x dma buffer`): DirectSound's in WinQuake, a static array's in the oracle, moving with every build | 0; the oracle is patched to print 0 too |
| `d_edge.c`: `D_DrawSurfaces` with `r_drawflat` | A surface's flat colour is the low byte of its `msurface_t`'s address | The surface's index in the frame's surface list |

### 3.4 The executables

The oracle is the C; what the original executables do besides was audited (decision 42, milestone 10). Winquake.exe
1.09 (Steam's, `Exe: 09:30:49 Mar 21 1997`) was run from a scratch copy on Windows 11 with `-condebug` and scripted
`autoexec.cfg`s, and its console log, config.cfg and screenshots compared with QuakeSharp's runs of the same scripts
(QuakeSharp matching the oracle on every traced frame, its files stand for the oracle's):

| What | Winquake.exe | The oracle and QuakeSharp |
|---|---|---|
| IPX at start-up | `WIPX_Init: Unable to open control socket` after `Winsock TCP/IP Initialized`: net_wipx.c starts, and today's Windows has no IPX | No IPX driver, no line. **Emulated** (milestone 10, step 3): an IPX LAN driver whose start fails as Windows' does |
| The joystick at start-up | `joystick detected` on this machine (a device answers `joyGetPosEx`); without one, `joystick not found -- no valid joysticks (a7)` (`JOYERR_UNPLUGGED`) | in_null.c: no line. **Emulated** (step 3): in_win.c over a virtual winmm without a joystick, so `(a7)` |
| The CD at start-up | `CDAudio_Init: MCI_OPEN failed (266)`: this machine has no drive | The virtual disc, `CD Audio Initialized`. Documented: the machine's drive (decision 37) |
| config.cfg | With the same switches, one line more: in_win.c's archived `joystick "0"`, after the bindings | **Emulated** (step 3, with in_win.c's cvars) |
| The modes | Seven (`vid_describemodes`): windowed 320×240, 640×480 and 800×600; full screen through DirectDraw (`DDRAW8.DRV`) 320×240, 640×480, 800×600 and 1024×768. It starts full screen, in mode 3 (`_vid_default_mode_win` at the first frame); the Video menu lists them | One windowed mode, an empty Video menu. **Emulated** (step 4): the same seven modes on a virtual display |
| Menus, status bar, console | The Options and Multiplayer menus, the status bar and the console's text pixel for pixel the same, but for the menus' blinking and spinning cursors (real time) and the Options' Use Mouse, shown only in a window (Winquake.exe was full screen) | The same |
| The 3D view | `timedemo demo1` with `host_framerate 0.015625` (the oracle's 1/64 s), `-nosound -nocdaudio`, screenshots at ten frames from 60 to 900: both play the same 969 frames, the status bar and the notify lines the same; 3-19% of the frame's pixels differ, for two reasons. `rand()`: `_Host_Frame` draws one at every call, also the calls `Host_FilterTime` turns away to keep under 72 frames a second, so Winquake.exe's `rand()` follows real time ("keep the random time dependent"): particles and the muzzle flash's light radius (`200 + (rand()&31)`) differ. Rounding: single texels along surfaces differ (3-5% of the view where nothing random is in sight), the x87's precision and the `id386` assembly's span drawing | Documented (§3.1): the C, in single precision with SSE, every frame's `rand()` calls the virtual clock's |
| The mouse | From the source (vid_win.c's `VID_Update` and `VID_SetMode`, in_win.c): in a window the mouse is inactive, its moves and buttons ignored, until `_windowed_mouse` is 1 (the Options' Use Mouse); the full-screen modes activate it; `-dinput` takes DirectInput; while active, in_win.c sets the system's mouse acceleration and speed (`-noforcemparms`, `-noforcemaccel`, `-noforcemspd`) | QuakeSharp's mouse buttons always reach `Key_Event`. **Emulated** (step 3), but for the system's settings (SDL3's relative motion instead) and DirectInput (not ported) |

quake.exe is DOS Quake 1.08 (`Exe: 18:28:16 Mar 11 1997`, built with DJGPP). It was not run (decision 47); its
differences, from the source's DOS drivers (Chocolate Quake's notes add nothing per version), are documented, not
emulated (D4):

| What | quake.exe |
|---|---|
| The code | 1.08's: the source is 1.09's, and what 1.09 changed in the shared code is not documented |
| Arithmetic | The x87 and the `id386` assembly, as Winquake.exe (DJGPP's code generation instead of MSVC's) |
| `rand()` | DJGPP's C library's, not the Microsoft runtime's: every random draw differs |
| The palette | VGA sets all 256 colours: no remapping of the colormap's entries 0 (vid_win.c's, decision 14), so the frames' pixel values differ where they are 0, not their colours |
| Video | vid_dos.c: VGA's 320×200 (mode 0, the default), the Mode X modes and the VESA modes (vid_vga.c, vid_ext.c; `-stdvid`), and its own Video menu |
| Input | in_dos.c: the mouse through the DOS driver, the joystick through the game port, `-control` (an external driver) |
| Sound | snd_dos.c and snd_gus.c: Sound Blaster and Gravis Ultrasound DMA at `-sspeed` (11025 by default), with their own start-up text |
| CD | cd_audio.c over MSCDEX |
| Network | net_ser.c (serial and modem: the menus' entries are live), net_ipx.c, net_bw.c (Beame & Whiteside TCP/IP), net_mp.c (MPATH) |

## 4. Technology

| Concern | Choice | Notes |
|---|---|---|
| Runtime | **.NET 10** (LTS), C# 14 | SDK 10.0.401, pinned in `global.json`. |
| Solution format | `.slnx` | |
| Build hygiene | `Directory.Build.props`, `Directory.Packages.props` (central package management), `.editorconfig`, nullable, warnings as errors, built-in analyzers | As DoomSharp. |
| Window, input, audio | **SDL3** via `ppy.SDL3-CS` | As DoomSharp and Contrail. |
| Graphics | **Vulkan 1.3** via **Vortice.Vulkan** 3.3.0 and **Vortice.VulkanMemoryAllocator** 1.8.0 | Dynamic rendering, sync2; the setup modelled on Contrail's `Contrail.Render` (context, swapchain, shader library). |
| Shaders | **Slang** → SPIR-V with `slangc` (`SLANGC`, or the Vulkan SDK's through `VULKAN_SDK`) | Compiled by `QuakeSharp.Render.Vulkan`'s build (`shaders/*.slang`, their modules in `shaders/common`) and embedded, so players need no SDK. CI installs a pinned Slang release (2026.18), checked by SHA-256. |
| Music | **NVorbis** 0.10.5 (MIT) decodes the rerelease's Ogg Vorbis CD tracks (§7.2, decision 39) | WAV tracks through SDL3 |
| Tests | xUnit v3 on Microsoft.Testing.Platform + golden-trace tests | Fidelity tests skip without game data or traces. |
| CI | GitHub Actions, Windows + Linux: build, unit tests, generated tables up to date | No GPU needed: Core and the tests never touch Vulkan. |
| License | **GPL-2.0** | A translation of id's GPL release. Third-party code must be GPL-2-compatible. |

## 5. Repository layout

```
QuakeSharp/
├─ design.md  README.md  CLAUDE.md  LICENSE (GPL-2.0)
├─ QuakeSharp.slnx  global.json  nuget.config
├─ Directory.Build.props  Directory.Packages.props
├─ .editorconfig  .gitignore  .gitattributes        (*.pak, *.dem, *.sav ignored: never commit game data)
├─ src/
│  ├─ QuakeSharp.Core/            Engine: host, common, QuakeC VM, server, client, software renderer, 2D,
│  │                              sound mixer, network protocol. No platform or GPU dependencies.
│  ├─ QuakeSharp.Platform.Sdl/    sys_/in_/snd_ output/cd_ layer on SDL3: window, input, audio out, timing.
│  ├─ QuakeSharp.Render.Vulkan/   Presents the 8-bit framebuffer (palette, aspect, scaling); later the enhanced
│  │                              hardware renderer (§6.6).
│  └─ QuakeSharp/                 Executable: command line, composition.
├─ shaders/                       Slang sources (common/: their modules)
├─ tests/
│  ├─ QuakeSharp.Core.Tests/      Unit tests (math, libm, rand, formatting, PAK/WAD/BSP parsing, VM, mixer…)
│  ├─ QuakeSharp.Fidelity.Tests/  Demo and scenario traces against the oracle (skip without data/traces)
│  └─ QuakeSharp.Platform.Tests/  The platform layer's own logic (key translation)
├─ tools/
│  ├─ QuakeSharp.Tools/           Dev CLI: identify, list/extract PAK, dump pics/textures/palette to PNG, trace,
│  │                              trace-compare
│  ├─ codegen/                    Generates the *.g.cs tables from reference/Quake
│  └─ oracle/                     The headless WinQuake build and its trace patch (§10)
└─ reference/Quake/               id-Software/Quake, read-only submodule
```

## 6. Architecture

```mermaid
flowchart TB
    App["QuakeSharp (exe)<br/>args · composition"]
    subgraph Core["QuakeSharp.Core"]
        direction TB
        H["Host (host, host_cmd)<br/>frame loop · virtual clock"]
        C["Common (cmd, cvar, common, crc, mathlib, zone)"]
        PR["QuakeC VM (pr_*)"]
        SV["Server (sv_*, world)"]
        CL["Client (cl_*, view, chase)"]
        R["Software renderer (r_*, d_*)"]
        UI["2D (draw, screen, console, menu, sbar, keys)"]
        S["Sound (snd_dma, snd_mix, snd_mem)"]
        N["Net (net_main, net_dgrm, net_loop, net_udp)"]
    end
    subgraph Platform["QuakeSharp.Platform.Sdl"]
        V["IVideo"]
        I["IInput"]
        AO["ISoundDma"]
        CD["ICdDrive"]
    end
    VK["QuakeSharp.Render.Vulkan<br/>present 8-bit frame"]
    App --> Core
    App --> Platform
    H --> SV --> PR
    H --> CL --> R
    H --> UI
    SV <--> N <--> CL
    CL --> S --> AO
    S --> CD
    R --> V --> VK
    I --> H
```

### 6.1 Mapping original modules

Each C module becomes a C# class (or a few) in a namespace that mirrors it, so any C# can be traced to its C:

| Original | QuakeSharp | |
|---|---|---|
| `host.c`, `host_cmd.c`, `sys_*.c` (the portable parts) | `Core/Host` | `Host_Frame`, the virtual clock, server and client commands |
| `common.c`, `cmd.c`, `cvar.c`, `crc.c`, `zone.c`, `mathlib.c`, `wad.c` | `Core/Common` | File system (search paths, PAKs, `-game`), command buffer, cvars, `MSG_*`/`SZ_*`; zone/hunk/cache become managed memory (see below) |
| `model.c`, `bspfile.h`, `modelgen.h`, `spritegn.h` | `Core/Model` | BSP, alias and sprite loading (`Mod_*`) |
| `pr_exec.c`, `pr_edict.c`, `pr_cmds.c`, `progs.h`, `pr_comp.h`, `progdefs.h` | `Core/Progs` | The QuakeC VM, edicts, builtins |
| `sv_main.c`, `sv_phys.c`, `sv_move.c`, `sv_user.c`, `world.c` | `Core/Server` | |
| `cl_main.c`, `cl_parse.c`, `cl_input.c`, `cl_demo.c`, `cl_tent.c`, `view.c`, `chase.c` | `Core/Client` | |
| `r_*.c`, `d_*.c`, `r_shared.h`, `d_iface.h` | `Core/Render` | WinQuake's software renderer, C paths only |
| `draw.c`, `screen.c`, `console.c`, `menu.c`, `sbar.c`, `keys.c` | `Core/UI` | |
| `snd_dma.c`, `snd_mix.c`, `snd_mem.c`, `cd_win.c`, snd_win.c's `SNDDMA_Init` messages | `Core/Sound` | The mixer paints the buffer of an `ISoundDma` (the device); cd_win.c drives an `ICdDrive` (§7) |
| `net_main.c`, `net_dgrm.c`, `net_loop.c`, `net_udp.c`/`net_wins.c` | `Core/Net` | .NET sockets |
| `vid_win.c` (what the game sees of it) | `Core/Video` | The mode's `vid` fields, the colormap remapping, `S_Init` between the mode sets, the palette (§6.4); the frame is shown through `IVideoOutput` |
| `vid_*.c`, `in_*.c`, `snd_win.c`'s device, the CD drive, `sys_win.c` | `Platform.Sdl`, `Render.Vulkan`, exe | Rewritten against SDL3 and Vulkan |
| `tools/oracle/src/trace.c` | `Core/Diagnostics` | The oracle's trace lines from QuakeSharp's state, and a run as the oracle's (`TraceRun`: virtual clock and sound device, base directory "."), for the tests and `quakesharp-tools trace` |
| `gl_*.c` (GLQuake) | not ported in v1 | An enhanced renderer may borrow from it later (§6.6) |

**The software renderer** (`Core/Render`, a partial file per `r_*.c`/`d_*.c`) keeps the C's data layout where it
shows: the edges, surfaces and spans are arrays reused every frame (C's pointers into them are indices; a model edge's
cached edge offset is an index, compared as the C compares byte offsets); the frame, the z-buffer, the surface cache and
the textures are byte arrays with offsets where C has pointers. The surface cache is one block with the C's 32-bit
header sizes, so it fills, wraps and thrashes (the "ram" icon, on by default) where the C's does. Float to integer
conversions go through `X86.FtoI` throughout (the z-buffer and the span steps rely on the x86 result out of range).

**The file system** (`FileSystem`, common.c's second half) keeps the search path exactly: `id1`, then `-rogue` /
`-hipnotic`, then `-game`, each directory followed in front by its `pak0.pak`, `pak1.pak`… (so a directory's paks go
before its loose files), `-path`, `-proghack`, the shareware rule that loose files in subdirectories are not found,
`com_modified` from the PAK0 count (339) and directory CRC (32981, which only the original pak0 matches: pak1 always
counts as modified, as in the original), and `COM_CheckRegistered`'s `gfx/pop.lmp` test. Modernised: the hunk, zone,
cache and stack variants of `COM_LoadFile` all return a managed buffer (still with the 0 byte appended); `-cachedir`
(a developer convenience that copied files to a local disk) is left out; and file names on disk match whatever their
case (the Steam release has `id1/PAK0.PAK`; on Linux the C would not find it), while names inside pack files still
compare exactly. A file found in a pack is read through a stream on the pack, so reading past its end reads the next
file, as the C's `FILE *` does.

### 6.2 Translation rules

1. **Translate, don't redesign.** Keep the original control flow, function boundaries and names (PascalCase methods:
   `SV_Physics_Toss` → `Server.Physics_Toss`). Every ported method carries a reference comment
   (`// sv_phys.c: SV_Physics_Toss`). Refactoring only where provably behaviour-neutral. **Enum members, constants
   and table data keep their C identifiers** (`svc_temp_entity`, `MOVETYPE_TOSS`, `TE_SPIKE`, `OP_ADD_F`) so they
   grep against the reference.
2. **No global mutable state.** The original's globals (`sv`, `svs`, `cl`, `cls`, `pr_global_struct`, `r_refdef`,
   `vid`, `host_*`…) become fields of subsystem instances owned by a root `QuakeEngine`. Several engines can run
   side by side (parallel trace tests; a listen server and a remote client in one test).
3. **Floats as in the C** (§3.1): `float` stays `float`, `double` stays `double`, `vec3_t` is a `Vector3`-like
   struct of three floats with the original `VectorMA`, `DotProduct`… as static helpers that evaluate in the same
   order and precision as the C macros. No `System.Numerics` operations whose evaluation order or fused multiply-add
   could differ. `int`/`unsigned` keep their 32-bit wraparound (`unchecked`).
4. **QuakeC memory as the C lays it out.** Globals and edict fields are 32-bit slots in `int[]` blocks reinterpreted
   as float, int, string offset, entity offset or function number exactly where the C reinterprets them (`OP_IF`
   tests the bits, so -0.0 is true). Entity references are byte offsets from `sv.edicts` (`EDICT_TO_PROG`) and
   `pr_edict_size` follows the progs' field count, as in the C; string fields are offsets into a string space that
   mimics `pr_strings`. Traces hash strings by content, since the oracle's offsets depend on its heap addresses.
5. **Pointers.** Linked structures (`edict_t` areas, `entity_t`, efrags, surfaces, edges) become reference types or
   indices with the same linking; framebuffer and z-buffer pointer arithmetic becomes `Span`/index arithmetic.
   `unsafe` only in measured renderer hot paths.
6. **Function pointers** (builtins, `cmd_function_t`, model and renderer dispatch) become delegates or enums bound
   by the owning instance.
7. **Generated data.** Tables carried over from headers (`anorms.h`, `anorm_dots.h`, `adivtab.h`, `progdefs.h`'s
   field layout, the protocol constants, the menu and key tables) are generated into `*.g.cs` by `tools/codegen`
   from `reference/Quake`, committed, never edited by hand.
8. **Binary data** is read with `BinaryPrimitives` (little-endian), never by marshalling structs.
   **C strings** become .NET strings holding one byte per character (Latin-1), so the bytes survive: Quake's
   character set uses the high bit for its brown text, and names compare byte for byte as `strcmp` does.
9. **Errors.** `Sys_Error` becomes a typed `QuakeException` (fatal); `Host_Error` and its `longjmp` to
   `host_abortserver` become a `HostErrorException` caught by `Host_Frame`, keeping what the C unwinds and what it
   leaves behind.
10. **Limits** that change behaviour stay (`MAX_EDICTS` 600, `MAX_MODELS`/`MAX_SOUNDS` 256, `MAX_DATAGRAM` 1024,
    `MAX_MSGLEN` 8000, `MAX_VISEDICTS` 256, `MAX_PARTICLES` 2048, `MAX_CHANNELS` 128, the renderer's edge and surface
    counts, the VM's 32-deep stack and 2048-slot local stack…). Overflowing them does what the C does.
11. **Undefined behaviour** (reads past arrays, uninitialised memory) is documented case by case in a table as
    DoomSharp's §2 does, with a fixed substitute that the oracle is patched to share where needed. What the C leaves
    to the compiler (argument order, implicit declarations) follows the oracle's build (§3.2).
12. **Vectors.** `vec3_t` is `Vec3`, an inline array of three floats indexed `v[0]` as in C, passed by `ref`/`in`
    where C passes the pointer (so an output that is also an input aliases as in C); `float[3][3]` and `float[3][4]`
    are `Mat3` and `Mat3x4`, indexed `m[i][j]`.
13. **One engine object.** WinQuake's globals and functions are members of `QuakeEngine`, a partial class with a
    file per C module (`Common/QuakeEngine.Cmd.cs` is cmd.c), keeping the C's names (`Cmd_AddCommand`, `cvar_vars`,
    `cls`, `MSG_WriteByte`): C had one namespace, so the names are unique and every line greps against the reference.
    Structures become classes (`client_static_t` is `ClientStatic`, `cvar_t` is `Cvar`) with the C's field names;
    enums keep their typedef names (`cactive_t`, `keydest_t`). Functions of modules not ported yet are empty stand-ins
    in `QuakeEngine.Pending.cs` until their module is. Varargs functions (`Con_Printf`, `Sys_Error`, `va`) take a
    format and `PrintfArg`s, formatted by `CFormat`; `Sys_Error` returns the exception to throw.

### 6.3 Main loop and time

`Host_Frame(time)` is the unit of simulation: it filters the time, runs commands, the server frame, the client
frame, renders and mixes sound. The original reads the wall clock (`Sys_FloatTime`) and the frame rate decides how
often `Host_Frame` runs; even `rand()` is drawn per call. For reproducibility, **time comes from an `IClock`**:
the executable's is real time, the tests' and the oracle's a **virtual clock** that advances a fixed step per frame
(1/64 s by default: a binary fraction, exact in the `float` `Host_Frame` takes, and above `Host_FilterTime`'s 1/72 s
cap so every call runs a frame), and every `Sys_FloatTime` call in the engine reads it (the host, `net_*`, the
signon keepalives in `cl_parse.c`, `Host_Spawn`'s connect time). The executable renders and presents once per
`Host_Frame`, as the original.

### 6.4 Video modes and presentation

The software renderer draws into an 8-bit buffer of the current video mode's size; WinQuake supports any size up to
`MAXWIDTH`×`MAXHEIGHT` (1280×1024), with the pixel aspect in `vid.aspect` (`vid_win.c`: height over width times
320/240). QuakeSharp offers the original modes (WinQuake's 320×240 window by default, DOS's 320×200 mode 0, and the
list up to 1280×1024) through `vid_mode` and the Video Options menu; each is pixel-exact against the oracle at that
size. What `vid_win.c` does that shows is kept: it remaps the colormap's entries 0 to the palette's closest other
black (GDI cannot remap palette index 0), which changes the frames' pixel values but not their colours; `S_Init` runs
between its hidden first mode set and the real one; the mode set prints the surface cache size and the mode name; and
its cvars go into `config.cfg`. The console shows Winquake.exe 1.09's build stamp, `Exe: 09:30:49 Mar 21 1997`.

`Render.Vulkan` presents the frame: the 8-bit buffer and the 256-colour palette (the one `V_UpdatePalette` computes,
gamma and shifts included) are uploaded each frame, a fragment shader looks up the palette, and the picture is
scaled to the window: nearest-neighbour by the largest integer factor that fits, then linear to the mode's display
aspect (320×200 shown as 4:3, as on a CRT), letterboxed. Core never touches Vulkan, so the tests and CI need no GPU.

As built (`VulkanPresenter`, `PresentationLayout`): the frame's rectangle is the largest 4:3 one that fits the window
(`-noaspect`: the frame's own shape, square pixels), centred; the first pass (`palette.slang`) writes the frame
through its palette into an image k times its size, k the smallest whole number that covers the rectangle; the second
(`scale.slang`) filters that linearly into the rectangle, black around. The palette's colours pass through unchanged
(a UNORM swapchain, not sRGB: they are already what the screen shows). Two frames in flight; mailbox presentation
where the driver has it, else FIFO. The window is borderless over the whole screen by default, `-window` a window
(as DoomSharp).

### 6.5 Networking

NetQuake is client-server even in single player (a listen server and a client joined by `net_loop`). Ported as is:
`net_main.c`, `net_dgrm.c` (the datagram protocol, reliable messages, server queries) and `net_loop.c`, with
`net_udp.c`'s role on .NET UDP sockets, so QuakeSharp and other NetQuake engines (protocol 15) play together. Until
the UDP driver comes (milestone 11), the datagram driver runs on a virtual LAN driver with no sockets (decision 33).

### 6.6 Enhanced mode (later)

As DoomSharp's (its design.md §6.5): an optional layer that only reads the game, switchable at any time, the
faithful mode the default and untouched. For Quake: a Vulkan hardware renderer of the same view (GLQuake-like, at
the window's resolution and aspect, possibly interpolated between server frames), with the 8-bit software frame
still produced every frame so nothing the game reads (the renderer's `rand()` calls for particles, the client
state it updates) changes. Verified as in DoomSharp: the traces with enhanced frames drawn must still match.

## 7. Audio

### 7.1 Sound effects

`snd_dma.c` (channels, `S_StartSound`, ambient sounds, `S_Update`), `snd_mix.c` (the paint buffer, the integer
mixer with `snd_scaletable`) and `snd_mem.c` (loading WAVs, `ResampleSfx`) are ported as they are: the sample-exact
contract covers them. `shm` is the buffer of an `ISoundDma`, which the mixer paints ahead of the device's play
position, as the DMA buffer of the original. The device is snd_win.c's DirectSound path: 64 KB of 16-bit stereo at
11025 Hz (WinQuake has no switch for another rate), SDL3 converting to the device; the messages that path prints at
start-up (`Set primary sound buffer format: yes`, `Using secondary sound buffer`, the format, `DirectSound
initialized`: as on today's Windows, to confirm against Winquake.exe), printed on both sides; and no blocking of its own
(`S_BlockSound` does nothing for DirectSound: its exclusive buffer goes silent while another program has the focus,
and vid_win.c's `AppActivate` clears the buffer when the focus goes and comes back). snd_win.c's waveOut path
(`-wavonly`), `-primarysound` and `-snoforceformat` are not ported (decision 38).

The sound device is part of the game: `S_StartSound` draws from `rand()` when an identical sound started and has not
been painted yet, and how far the mixer has painted depends on the device's play position. With a real device that
is real time, as in the original; the oracle and the tests use a **virtual device** (`snd_oracle.c`, 64 KB of 16-bit
stereo at 11025 Hz as WinQuake's DirectSound buffer; `VirtualSoundDma` in Core) whose position follows the virtual
clock. So the three files came with the client (milestone 5): the traces' `S` line checks the channels every frame,
and from trace format 5 the `P` line the whole buffer the device plays (§11.2).

### 7.2 Music

Quake's music is the CD's audio tracks (2-11), played by `cd_win.c` through the drive's MCI commands. Nothing of it is
in the PAKs, and it never passes through the mixer: the drive's analogue output went to the sound card's own mixer.
cd_win.c is ported in Core as it is (the `cd` command, the track `remap`, `CDAudio_Update`'s `bgmvolume`, which is on or
off, its messages) over an `ICdDrive` that answers the MCI commands it sends, each with MCI's error code. Two drives:

- **The virtual disc** (`VirtualCdDrive` in Core; the oracle's `cd_oracle.c`): Quake's CD, track 1 data and 2-11
  audio, whose tracks play without end (no notification ever comes). The traces and `-recordevents` use it, and trace
  format 5's `A` line checks cd_win.c's state every frame.
- **The file drive** (`Platform.Sdl`): the tracks are files the user has, `music/trackNN.ogg` or `.wav` in the game
  directories, or the Steam install's `rerelease/id1/music` (the rerelease's `track02.ogg`-`track11.ogg`, or rips of
  the CD); track 1 is data and the highest track found is the last. Without files it is a drive with no disc
  (`CDAudio_Init: No CD in player.`). A track plays on its own SDL3 audio stream on the sound device, which SDL mixes
  with the game's; its end is MCI's `MCI_NOTIFY_SUCCESSFUL`, handed to `CDAudio_MessageHandler` where WinQuake's window
  procedure gets it (`Sys_SendKeyEvents`), so a looping track starts again. Under `-recordevents` the virtual disc
  decides, so the recording replays in the oracle, and the files are only heard.

The music keeps playing while another program has the focus, as in WinQuake's windowed mode: `AppActivate` only clears
the game's sound buffer (which DirectSound silences) and leaves the CD alone; only the full-screen modes' `VID_Suspend`
paused it, and QuakeSharp runs the windowed mode.

NVorbis decodes the rerelease's Ogg Vorbis files, SDL3 WAV files (decision 39).

## 8. Input

Keyboard and mouse through SDL3, posted as `Key_Event` with Quake's key numbers; bindings, `+mlook`, `in_mouse`,
`sensitivity`, `m_pitch`… as the original (`in_win.c`'s roles). A game controller works as the original joystick
support (`in_win.c`'s `joy_*` cvars and `JOY1`-`AUX32` keys) where that maps cleanly.

Keys are read as WinQuake reads them (`vid_win.c`: `MapKey`): the PC scan code of the key message (SDL's scan code
turned back into it, `KeyTranslation`) looked up in `scantokey` (generated from `vid_win.c`), without the extended-key
prefix. So keys go by position (the typing keys give the US layout's characters on any keyboard), the grey keys give
what their keypad twins do whatever NumLock says, the right-hand Ctrl and Alt are Ctrl and Alt, NumLock gives
`K_PAUSE` as Pause does, and held keys repeat as Windows repeats them. Mouse buttons are `K_MOUSE1` + button (left,
right, middle) and each wheel notch a press and release of `K_MWHEELUP` or `K_MWHEELDOWN`, as `MainWndProc` posts
them. The key numbers (`keys.h`) are generated too.

## 9. Persistence

- **Config**: `config.cfg` written by `Host_WriteConfiguration` (key bindings and archived cvars, in text mode: CR
  LF line ends, as WinQuake writes it), `autoexec.cfg`, `quake.rc`, `default.cfg` from the PAK, exactly as the
  original reads them.
- **Where files go**: what the game writes into `com_gamedir` (`config.cfg`, savegames, demos, screenshots,
  `qconsole.log`) goes to the game directory's namesake in a user folder instead (`%APPDATA%\QuakeSharp\id1` on
  Windows, `~/.config/QuakeSharp/id1` on Linux), and a game directory's loose files are looked for there first, then in
  the game directory itself; the Quake installation is only read. As DoomSharp's decision 30. (`QuakeParms.userdir`;
  without one, as the oracle runs, files go where WinQuake puts them.)
- **Savegames**: the text `.sav` files (`SAVEGAME_VERSION` 5, `ED_Write`, `%f` floats), byte for byte as the oracle
  writes them; the original's and QuakeSharp's load into each other.
- **Demos**: `record`, `playdemo`, `timedemo`; `.dem` files byte for byte as the oracle writes them.

## 10. Verification strategy

Faithfulness is measured, not assumed.

| Level | What | How |
|---|---|---|
| Unit | Math helpers, libm, `rand()`, printf formatting, PAK/WAD/BSP/MDL parsing, VM opcodes, mixer | xUnit, no game data; WinQuake's own functions on generated inputs (`quake-oracle -oracleunits`) and scenarios of commands, cvars, console text and keys (`-oraclescenario`, `tools/oracle/units`), compared by hash |
| Client | **Demo traces** | Play demo1-3 (PAK0) and demos the oracle records; compare per frame: the client state (`cl` fields, entities, temp entities, dlights, particles), the `rand()` seed and the framebuffer and palette hashes |
| Renderer & 2D | **Golden frames** | The same traces hash the whole 8-bit frame every frame, at 320×200 and larger modes; console, menus, status bar, intermission and finale reached by scripted input |
| Server | **Scenario traces** | Scripted games (`-traceevents`: per-frame key events and console commands, with the virtual clock): per frame, hashes of `sv` state, every edict's memory (strings by content), the QuakeC globals and the `rand()` seed; the network messages sent |
| Sound | **Mix traces** | The traces hash the sound channels (`S`), the device's whole buffer (`P`) and the CD's state (`A`) every frame |
| Persistence | **Files** | What each run writes (`config.cfg`, savegames, demos, screenshots, `qconsole.log`) compares byte for byte with what the oracle's run wrote; saves and demos read back in fresh runs play on in sync (§11.3) |
| Smoke | Shareware in CI | CI fetches the shareware `pak0.pak` (`quake106.zip`, checked by MD5, never committed) and checks the shareware demos against the last lines of their traces, kept in the test |

Fidelity tests find game data through `QUAKESHARP_QUAKE_DIR` (a folder with `id1/`, matched by MD5) and traces
through `QUAKESHARP_TRACE_DIR`; they skip without them.

**The oracle** (`tools/oracle`, its README has the details and the trace format): WinQuake 1.09's C sources,
copied from `reference/Quake` and patched only so `id386` is 0 (`quake-oracle.patch`), built locally with clang-cl
for 32-bit x86 with SSE2 arithmetic and musl's libm, as a headless program. Its own platform layer replaces the Windows
drivers: `vid_win.c`'s behaviour in one windowed mode rendered in memory, the virtual sound device, no input or CD,
the loopback network driver, and a system driver with the virtual clock. It writes per-frame trace lines (time and
`rand()` state, client state, entities, effects, view, frame and palette), plays scripted input (`-traceevents`)
and dumps frames (`-traceshot`) and entities (`-tracedump`). Runs are deterministic: the same command line gives the
same trace. It changes the game's behaviour only where §6.2 rule 11 needs a shared substitute for undefined memory.
`quakesharp-tools trace` writes QuakeSharp's trace of the same command line (and its dumps), `trace-compare` names
the first differing frame of each kind of line; the dumps show the first differing field. Traces are derived from
game data and stay outside the repository.

The trace's lines are split by who writes what they hash, so each milestone is checked on its own: the client's
(`F`, `C`, `E`, `X`, `S` for the sound channels, `T` for the console text, `R` and the palette) from milestone 5, the
renderer's (`D`: what it writes into the entities and the static entities it adds to the visible list; the frame)
from milestone 6.

Current result: the oracle plays demo1-3 to their ends (4,755, 4,476 and 5,269 frames at 64 per second) at 320×240,
320×200 and 640×480, the attract loop and the scripted menus at two sizes, in about 40 seconds for all twelve
traces. musl's libm and the C runtime's `rand()` match the C# ports on every one of 1.8 million values. QuakeSharp
matches every line of the oracle's traces, the renderer's `D` and the frame's pixels included, on every frame of the
nine demo traces, the 20,000 frames of the attract loop and the 3,700 frames of each menu trace (`DemoTests`). With
the server (trace format 4), it also matches every line, the server's `G`, `Q`, `N`, `W` and `M` included, of every
frame of the scripted games of §11.1: a new game through the start map (2,000 frames), the server's commands with
fights, a save, a load, a restart and a level change (3,800, at two sizes), e1m1's intermission (1,900), e1m7's finale
(3,100), the network menus starting a deathmatch (3,000), a tour of all 37 maps (22,200) and a game played by hand in
QuakeSharp and recorded with `-recordevents`, e1m1 into e1m2 (24,500). With milestone 9 every run's files are compared
too: the config.cfg each run writes when it quits, the 41 savegames of a save on every map (`saves`, 17,705 frames),
three demos, two screenshots and `qconsole.log` (`record`), byte for byte; and the files read back in fresh runs match
on every frame: the menus' config.cfg (`attract.config`), every save loaded and played on (`loads`, 12,905 frames),
each demo played back and as a timedemo.

## 11. Milestones

| # | Milestone | Done when |
|---|---|---|
| 0 ✅ | Repo skeleton | Solution, projects, build & one test green, license, README, .gitignore (`*.pak`), reference submodule, CI |
| 1 ✅ | Files & data | PAKs identified by MD5; search paths and `-game`; `gfx.wad` (WAD2), palette, colormap, pics, BSP textures, MDL skins dump to PNG through the tools |
| 2 ✅ | Oracle & numerics | The headless oracle builds and writes traces of demo1-3; the libm and `rand()` ported and tested. *Traces of the three demos at three sizes and of the attract loop; musl's six functions and `rand()` bit-exact against the C. `mathlib.c`, printf formatting and `Q_atof` move to milestone 4, the tables of the client and renderer to 5 and 6, with the code that uses them* |
| 3 ✅ | Platform & presentation | SDL3 window, Vulkan presents an 8-bit frame with the palette at 4:3 (e.g. `gfx/conback.lmp`); input events; audio device open. *The executable shows `gfx/conback.lmp` at 4:3 in a window or over a 4K screen, clean under the validation layer; keys come as WinQuake's key numbers; the sound device plays a 16-bit stereo 11025 Hz buffer through SDL3; CI builds the shaders with a pinned Slang* |
| 4 ✅ | Host & common | `Host_Frame` with the virtual clock, command buffer, cvars, console text, `MSG_*`, `quake.rc`/`default.cfg` executed; `mathlib.c`, printf formatting (`%f`, `%5.1f`...) and `Q_atof` checked against the C. *`mathlib.c`, `Q_atoi`, `Q_atof` and printf match WinQuake's C on 18 sets of 20,000 inputs; commands, cvars, console text and keys match on two scenarios; the host runs `Host_Init` and `Host_Frame` (the modules to come are stand-ins), executes `quake.rc` and `default.cfg`, and writes WinQuake's bindings to `config.cfg`. net_main.c and net_loop.c move to milestone 7, where the server uses them; host_cmd.c's commands to 5 and 7* |
| 5 ✅ | Client & demos | `cl_*` parse and interpolate; **demo1-3 play with the client state in sync on every frame** (no rendering yet). *demo1-3 at 320×240, 320×200 and 640×480 and 20,000 frames of the attract loop match the oracle's client lines on every frame: `rand()`'s state and calls, `cls`/`cl`, the entities, temp entities, beams, lights, light styles and particles, the sound channels, the console text, `r_refdef` and the palette. Ported: cl_main, cl_parse, cl_demo, cl_input, cl_tent, view, chase, r_part (`R_DrawParticles` moves them, `D_DrawParticle` waits), model.c, snd_dma/snd_mix/snd_mem (`S_StartSound` draws `rand()`), screen.c's control flow and r_main/r_misc/r_light's view setup (`R_SetupFrame`'s view leaf gives the contents' colour shift), vid_win.c's mode in Core, host_cmd.c's client commands, sv_main.c's `SV_Init`; drawing, menus, the status bar, efrags, net and the server are stand-ins. The executable plays the demo loop's sound* |
| 6 ✅ | Software renderer & 2D | `r_*`/`d_*`, `draw`, `screen`, `console`, `sbar`; **every frame of demo1-3 pixel-exact** at 320×200 and a larger mode; menus. *Ported: the whole software renderer (r_*.c, d_*.c), draw.c, the console's drawing, sbar.c and menu.c. demo1-3 at 320×240, 320×200 and 640×480, 20,000 frames of the attract loop and 3,700 frames of scripted menus (`events/menus.events`: key events `@key` and commands over the attract loop) at 320×240 and 640×480 match the oracle pixel for pixel, `D` line included (trace format 3 hashes a leaf `topnode` as `0x10000 +` its index). The network menus wait for net_main.c, the intermission and finale screens for the server to reach them (milestone 7). The executable shows the game* |
| 7 ✅ | QuakeC & server | VM, builtins, `sv_*`, `world`, `host_cmd`; **scripted games in sync with the oracle on every frame**, through the episodes' maps; playable. *Plan: §11.1. Ported: pr_edict.c, pr_exec.c, pr_cmds.c, world.c, sv_main.c, sv_phys.c, sv_move.c, sv_user.c, host.c's and host_cmd.c's server parts (save and load included), net_main.c, net_loop.c, net_dgrm.c with the virtual LAN driver, and menu.c's network menus. Trace format 4 adds the server's lines; the scripted games (`events/`: start, commands, intermission, finale, netmenus, tour) and a game played by hand (`e1`: recorded with `-recordevents`, from the menu through e1m1 into e1m2) match the oracle on every line of every frame, 60,500 frames of play in all, every map of the four episodes included; the recording matched on its first replay. The executable shows and plays the game (milestone 6's Program.cs still drew `gfx/conback.lmp` and never handed the engine its video output) and records games with `-recordevents`. More playthroughs are added as they are recorded (decision 35)* |
| 8 ✅ | Sound | Mixer sample-exact against the oracle; SDL3 output; CD tracks from files. *Plan: §11.2. Trace format 5 adds `P` (the sound device's whole buffer) and `A` (the CD); every trace (demo1-3 at three sizes, the attract loop, the menus, the scripted games, the playthrough, and `sound.events` with and without a sound device and CD) matches with them on every frame: the mixer ported with milestone 5 was already sample-exact. Ported: cd_win.c over an MCI-like `ICdDrive` (the oracle compiles it unchanged over a virtual disc), snd_win.c's start-up messages (as Winquake.exe 1.09 prints them), `AppActivate`'s sound part. The executable plays the CD's tracks from the user's files (the rerelease's Ogg Vorbis through NVorbis, or WAV) on their own SDL3 stream, is silent without the focus, and under `-recordevents` plays the virtual disc's tracks for listening only* |
| 9 ✅ | Persistence | `config.cfg`, savegames, demo recording, byte for byte; loaded games in sync. *Plan: §11.3. Each oracle run keeps the files it writes (`<name>.files`), and every trace test compares QuakeSharp's with them byte for byte: config.cfg after every run (it already matched), the 41 saves of `saves.events` (a save on every map, the menus' slots, the refusals) and `commands`' save, three demos (through a level change and an intermission, with a forced CD track, one cut by the end of the run), two screenshots and `qconsole.log`. Read back in fresh runs, in sync on every frame: the menus' config.cfg, every save (`loads.events`), every demo played back and as a timedemo. Fixed on the way: reading a savegame as the C runtime's `fscanf` does (hex floats, exponents without digits, 64-bit `%i`, a 0xff byte taken for EOF; checked on 20,000 inputs of each against the oracle), `qconsole.log` in text mode (CR LF), `CL_GetMessage`'s uninitialised `f` at a demo's end (§3.3, the oracle patched alike), and the demo file still open at the end. Winquake.exe 1.09 loads QuakeSharp's saves, and QuakeSharp loads Winquake.exe's saves and plays its demos* |
| 10 | Fidelity pass | Executable differences audited (DOS/Win), mission packs (hipnotic, rogue), remaining UI scenarios. *Plan: §11.4* |
| 11 | Networking | UDP play with other NetQuake engines; net game tests |
| Later | | Enhanced mode (Vulkan hardware renderer), QuakeWorld, release builds (single file, NativeAOT) |

### 11.1 Milestone 7: the plan

**Porting order.** Each step builds with the tests green and is committed; the stand-ins of `QuakeEngine.Pending.cs`
move out as their module comes.

1. **The oracle first**: trace format 4 (below) in `trace.c`, the message hook, `net_oracle.c` and the scripted games,
   so the oracle's traces of them are the target before the C# exists.
2. **The network and format 4**: net_main.c, net_loop.c and net_dgrm.c with the virtual LAN driver (decision 33), and
   format 4's lines in `TraceWriter`, the dumps and `trace-compare`. The datagram driver prints its message at start-up
   and the multiplayer menu offers TCP/IP, so every trace changes with the oracle's drivers: this comes first, to keep
   the demo and menu traces in sync.
3. **The VM** (`pr_comp.h`, `progs.h`, `pr_edict.c`, `pr_exec.c`): `progdefs.h`'s globals and fields generated as slot
   numbers with typed accessors (`ent.v.origin`, `pr_global_struct.self`) over the `int[]` blocks of §6.2 rule 4. The
   edicts are one block of `pr_edict_size` bytes each, with the 32-bit oracle's 96-byte `edict_t` header in front of
   the fields (its contents kept in the edict's object), so entity values are the C's byte offsets. `PR_LoadProgs`,
   `ED_*` (alloc, free, parse, print, write, `ED_LoadFromFile`), `PR_ExecuteProgram`, `PR_RunError` and the
   `profile`, `edict`, `edicts` and `edictcount` commands. Unit tests run the opcodes on small progs built in the test.
4. **world.c**: hulls, area nodes, `SV_LinkEdict` and the touch links, `SV_PointContents`, `SV_RecursiveHullCheck`
   (chase.c's stand-in goes), `SV_Move`.
5. **pr_cmds.c**: the builtins.
6. **The server**: the rest of sv_main.c, sv_phys.c, sv_move.c, sv_user.c, and host.c's server half
   (`Host_ServerFrame`, `Host_ShutdownServer`, `SV_ClientPrintf`, `SV_BroadcastPrintf`, `Host_ClientCommands`).
7. **host_cmd.c**: the server's and the QuakeC's commands (`map`, `changelevel`, `restart`, `prespawn`, `spawn`,
   `begin`, `god`, `give`, `kill`, `name`, `color`, `say`, `status`, `kick`...), `save` and `load` included (their
   files are checked byte for byte with milestone 9).
8. **menu.c's network menus**, with net_main.c's server list.
9. **The scripted games** in sync, frame by frame.
10. **Recording** (`-recordevents`, decision 34) in the executable, and the playthroughs.

**Strings.** QuakeC's strings are offsets from `pr_strings`. Offsets into the progs' own string table are the C's;
the others (`ED_NewString`'s copies on the hunk, `pr_string_temp`, a client's `name`, `sv.name`) are the oracle's
addresses, so QuakeSharp gives them offsets of its own past the table, each standing for one buffer whose current
contents are read, as the C's pointers are: an `ftos` result changes with the next `ftos`, a player's `netname`
follows the client's `name`. Offsets are never 0 and the comparisons QuakeC makes (`OP_IF`, `OP_NOT_S`, `OP_EQ_S`)
behave as the C's; the traces compare strings by content.

**Trace format 4** adds five lines after `V`, written every frame (without a server they hash its empty state):

| Line | Fields |
|---|---|
| `G <hash>` | The server: `sv` (active, paused, loadgame, time, lastcheck, lastchecktime, name, modelname, the model and sound precaches, the light styles, num_edicts, state, the `datagram`, `reliable_datagram` and `signon` buffers) and `svs` (maxclients, serverflags, changelevel_issued and every client slot: active, spawned, dropasap, privileged, sendsignon, last_message, the ping times, num_pings, the last `usercmd_t`, wishdir, the message buffer, its edict, name, colors, spawn_parms, old_frags) |
| `Q <hash>` | The QuakeC globals: every slot a global definition of the progs covers, strings by content (the compiler's temporaries, the return value and the parameters have none and are left out: they hold the oracle's addresses, and nothing reads them before writing) |
| `N <hash> <edicts>` | The edicts up to `sv.num_edicts`: the header (free, num_leafs, the leaf numbers, baseline, freetime) and every field slot, strings by content |
| `W <hash>` | The world's links: each area node's trigger and solid lists as edict numbers in their order, which decides the order of touches and clips |
| `M <hash> <messages>` | The messages sent this frame by `NET_SendMessage` and `NET_SendUnreliableMessage` (`NET_SendToAll` uses the first): the sender (the client, or the server to client n), reliable or not, the bytes |

`-tracedump` also writes the globals and the edicts by field name (the progs' definitions), floats as their bits, and
the frame's messages in hex, to find the first differing field.

**What the oracle adds**: those lines and dumps in `trace.c`; a call in `NET_SendMessage` and
`NET_SendUnreliableMessage` (`quake-oracle.patch`, under `ORACLE`, behaviour unchanged) that hashes each message for
`M`; and `src/net_oracle.c` in place of `net_none.c`: WinQuake's driver list (loopback, then datagram, `net_dgrm.c`
added to the build) with the virtual LAN driver.

**Scripted games** (`tools/oracle/events`, starting from the attract loop as `menus.events` does; `Trace-Demos.ps1`
traces each at 320×240, some also at 640×480; `GameTests` checks them):

| File | What |
|---|---|
| `start.events` | A new game from the menu, the start map's skill halls, into episode 1 by its slipgate (a level change without intermission) |
| `commands.events` | On e1m1: `god`, `notarget`, `fly`, `noclip`, `give`, `impulse 9` and `255`, the weapons fired at monsters, `kill` and the respawn, `name`, `color`, `say`, `pause`, `status`, `ping`, `viewmodel`..., `restart`, `changelevel`, `save` and `load` |
| `intermission.events` | e1m1 to its exit: the intermission screen, then e1m2 |
| `finale.events` | The end of episode 1: the finale's text |
| `netmenus.events` | The network menus: TCP/IP, the LAN settings, the game options (players, deathmatch and coop, teamplay, limits, map) starting a listen server, a deathmatch, the search and server list, the setup (name and colours sent to the server) |
| `tour.events` | Every map of the four episodes, the end and the six deathmatch maps in turn, 600 frames each: god mode, all the weapons, shooting while turning and moving (generated by a script: `changelevel` from one map to the next) |
| `e1.events` | A game played by hand in QuakeSharp and recorded with `-recordevents` (keys and mouse buttons): a new game from the menu, e1m1 played through into e1m2, then the quit screen; 24,500 frames. Playthroughs of the other episodes join as they are recorded |

**Done when** the scripted games and the playthroughs match the oracle on every line of every frame (the client's and
the renderer's lines included) and the executable plays a game.

### 11.2 Milestone 8: the plan

The mixer (snd_dma.c, snd_mix.c, snd_mem.c) was ported with the client, and the `S` line already checks the channels
and the mixer's times every frame; what it paints has never been compared. Milestone 8 adds that, the parts of
snd_win.c and cd_win.c the game shows (§7), and the music.

**Trace format 5** adds two lines after `M`, written every frame:

| Line | Fields |
|---|---|
| `P <hash>` | The sound device's buffer: all of `shm->buffer` (64 KB), what the device plays. It holds the samples painted this frame, `S_ClearBuffer`'s clears and what stays from the laps before; a difference stays in the hash until the mixer paints over it (a lap is 1.5 s), and the first differing frame is the one that counts. Without a started sound system (`-nosound`) it hashes nothing |
| `A <hash>` | The CD: cd_win.c's `initialized`, `enabled`, `cdValid`, `playing`, `wasPlaying`, `playLooping`, `cdvolume`, `playTrack`, `maxTrack` and `remap`, then the virtual drive's state (open, door, the track it plays or has paused) |

The whole buffer rather than the frame's new samples: hashing 64 KB a frame costs little (a few seconds over every
trace), and it also checks the clears and where in the buffer the samples went. `-tracedump` adds the bytes the mixer
wrote during the frame (`sound <offset> <bytes>`, from `paintedtime` at the frame's start to its end, in hexadecimal)
and the CD's fields by name; `trace-compare` knows both lines.

**What the oracle adds**: those lines and dumps in `trace.c`; `snd_oracle.c` prints snd_win.c's start-up messages
(§7.1); and `src/cd_oracle.c` in place of `cd_null.c`: WinQuake's `cd_win.c` compiled unchanged over a virtual
`mciSendCommand`, a drive holding Quake's disc (§7.2). The oracle has no window procedure, so no MCI notification ever
arrives, as on the virtual disc no track ends.

**Porting order.** Each step builds with the tests green and is committed; the oracle's traces are regenerated at
step 1 and QuakeSharp follows them.

1. **The oracle first**: format 5, the start-up messages and `cd_oracle.c`, so its traces are the target before the C#
   exists. The console text changes (the sound messages, `CD Audio Initialized`), and the CD volume slider is now on or
   off (`CDAudio_Update`), so every trace changes.
2. **snd_win.c's messages** in Core (`QuakeEngine.SndWin.cs`: `SNDDMA_Init`'s DirectSound path and `snd_firsttime`,
   over `ISoundDma`, which keeps the device's part), and `P` in `TraceWriter`, its dump and `trace-compare`. Where `P`
   differs, the mixer's port is fixed until every trace matches.
3. **cd_win.c** in Core (`QuakeEngine.CdAudio.cs`; the `CDAudio_*` stand-ins leave `QuakeEngine.Pending.cs`) over
   `ICdDrive`, the MCI commands it sends (open, set the time format, the status items, play from and to, stop, pause,
   the door, close), and `VirtualCdDrive` as `cd_oracle.c`; `A` in `TraceWriter`. Every trace matches again. Unit tests
   drive cd_win.c's port with a scripted drive for what the traces never reach: a track's end and its loop, a failure
   notification, an open that fails.
4. **A scripted game for sound**, `sound.events`: the sound commands (`play`, `playvol`, `stopsound`, `soundlist`,
   `soundinfo`), the cvars the mixer reads (`volume` through the menu and the console, which rebuilds the scale table,
   `loadas8bit`, `ambient_level`, `ambient_fade`, `_snd_mixahead`, `snd_noextraupdate`, `nosound`), every `cd`
   subcommand, the CD volume slider, a pause in a game (`svc_setpause` pauses the CD) and level changes with their
   `svc_cdtrack`. `Trace-Demos.ps1` traces it, also with `-nosound -nocdaudio`, and `DemoTests` checks both.
5. **The SDL3 output**: `AppActivate`'s part (on losing and regaining the focus, `S_ClearBuffer`, and the stream
   silent while unfocused, as DirectSound's exclusive buffer), not under `-recordevents` (the oracle never loses the
   focus); `-nosound` and `-nocdaudio` as WinQuake's; check that the device never overtakes the mixer at the
   executable's frame rates (the painted end stays ahead of the play position). *Measured: in 25 s of the attract
   loop the device overtook the mixer once, in the first `S_Update` after `Host_Init` (the device plays while the
   game loads; `S_Update_` moves `paintedtime` up, as in the C), and never while playing.*
6. **The music**: the file drive (§7.2): WAV tracks through SDL3, Ogg Vorbis through NVorbis (decision 39), each track on its own stream on the sound device, the end of a track handed to
   `CDAudio_MessageHandler` from `Sys_SendKeyEvents`; under `-recordevents` the virtual disc with the files only heard.

**Done when** every trace (the demos, the attract loop, the menus, the scripted games, the playthroughs and
`sound.events`) matches the oracle with format 5's `P` and `A` on every frame, and the executable plays the game's
sound through SDL3 and the CD's tracks from the user's files.

### 11.3 Milestone 9: the plan

The code that writes and reads the game's files came with its modules: `Host_WriteConfiguration`, `Key_WriteBindings`
and `Cvar_WriteVariables` (milestone 4), cl_demo.c's `record`, `stop`, `playdemo` and `timedemo` (5), `screenshot` and
`WritePCXfile` (6), `save`, `load` and the save and load menus (7), `-condebug`'s `qconsole.log`, and the user folder
(§9). What has been compared: config.cfg's bindings after the attract loop (`HostTests`), and a save loaded in the same
run (`commands.events`). Milestone 9 compares the files themselves, byte for byte, and reads them back in fresh runs.

**Files, compared at the end of the run** (decision 40). A trace line hashing the files every frame would cost a
demo's whole size each frame; a file's own bytes show where it differs. So the oracle's runs keep what they wrote,
QuakeSharp's runs write theirs into the user folder, and the tests compare the two sets: the same names, the same
bytes. Trace format stays 5.

| File | Written by | Compared from |
|---|---|---|
| `config.cfg` | `Host_WriteConfiguration` when the game quits (text mode) | every run: the oracle's runs end with `Sys_Quit`, at `-oracleframes` too |
| `.sav` | `Host_Savegame_f` (text mode, `%f` floats) | `commands`, `saves` |
| `.dem` | `CL_Record_f` (the CD track line), `CL_WriteDemoMessage`, `CL_Stop_f` | `record` |
| `quakeNN.pcx` | `SCR_ScreenShot_f`, `WritePCXfile` | `record` |
| `qconsole.log` | `Con_DebugLog` with `-condebug` (`open` without `O_BINARY`: text mode, so CR LF; to confirm) | `record` |

**Porting order.** Each step builds with the tests green and is committed.

1. **The oracle first.** `Trace-Demos.ps1` removes what the game writes from the oracle's `id1` before every run
   (`config.cfg`, `*.sav`, `*.dem`, `*.pcx`, `qconsole.log`), copies what the run wrote into `<name>.files/` beside its
   trace afterwards (`attract.config.cfg` becomes `attract.files/config.cfg`), and can start a run with files of an
   earlier one copied in. The scripted games of steps 3-5 come with their steps. `units.c` adds what reading a savegame converts, the C
   runtime's `atof` (`ED_ParseEpair`) and `fscanf`'s `%f` into a float and `%i` (`Host_Loadgame_f`), on `%f` prints of
   float values and on random decimal strings; `OracleUnitTests` takes the hashes. The existing traces do not change.
2. **The files in the tests.** `TraceRun` ends at the frame limit with `Sys_Quit`, as the oracle's run does, so it
   writes config.cfg. After every trace test (the demos, the attract loop, the menus, the scripted games, the
   playthrough, the sound) the user folder's files must be the oracle's; `HostTests` compares the whole config.cfg.
   `quakesharp-tools file-compare` names the first difference: the byte, a text file's line, a demo's message block.
   What differs is fixed; expected: archived cvars of modules ported after milestone 4, `qconsole.log`'s line ends.
3. **config.cfg read back.** `attract.config`: 600 frames of the attract loop with the `menus` run's config.cfg (its
   changed bindings and cvars) in place, which `quake.rc` executes; in sync on every frame, and the config.cfg it
   writes when it quits matches.
4. **Savegames.** `saves.events`, generated as `tour.events` was: every map of the four episodes, the end and the six
   deathmatch maps in turn, about 400 frames each (17,000 in all) with fights, and a `save` named after the map; the
   save menu's slots (`s0`-`s2`, the comment `SavegameComment` writes) and the load menu listing them
   (`M_ScanSaves`); the refusals (no local game, the intermission, a multiplayer game, a dead player, a relative path).
   Every `.sav` compared. Then `loads.events`, a fresh run with `saves`' files in place: each save loaded in turn (from
   the console and from the load menu) and played on for 300 frames, in sync on every frame. QuakeSharp then loads the
   oracle's files, and since the files are the same bytes, the oracle loads QuakeSharp's.
5. **Demos, screenshots and the log.** `record.events`, with `-condebug`: `record` with a map and with a CD track,
   fights, a level change and an intermission while recording, `stop`; the refusals (connected without a map, a
   relative path, `stop` without a recording); two `screenshot`s. Every `.dem`, `.pcx` and `qconsole.log` compared.
   Then each demo played back in a fresh run (`+playdemo <demo>` with `-tracequitafterdemo`, as demo1-3) and once with
   `timedemo`, in sync on every frame.
6. **The real executable** (decision 41): a scratch copy of Winquake.exe 1.09 (from the Steam install) writes a
   save, a demo, a config.cfg and a `qconsole.log`; QuakeSharp loads the save and plays on, plays the demo and reads
   the config, Winquake.exe loads a QuakeSharp save, and the line ends of its text files confirm the text mode. By
   hand, not traced: its x87 floats may differ from the oracle's in the last printed digits (§3.1). *Done: a scratch
   copy run with an `autoexec.cfg` saved on e1m1, recorded a demo on e1m2, loaded QuakeSharp's e1m3 save and saved
   it again; its saves have the same form as QuakeSharp's (CR LF, the comment, `%f`), and QuakeSharp, headless, loaded
   its save, played on and saved, and played its demo to the end.*

**Done when** every run's files (config.cfg after every trace, the saves, the demos, the screenshots, the log) match
the oracle's byte for byte; the saves and demos read back in fresh runs match on every frame; and Winquake.exe's saves
and demos load in QuakeSharp, and QuakeSharp's saves in Winquake.exe.

### 11.4 Milestone 10: the plan

Every module of WinQuake's C is ported but two drivers: in_win.c (the mouse and the joystick: `IN_Move` and the rest are
still stand-ins in `QuakeEngine.Pending.cs`, so the executable has no mouse look) and vid_win.c's modes (one mode of
the size `QuakeParms` asks for; the Video menu lists nothing, decision 30). The mission packs' code (`-hipnotic` and
`-rogue`: the search path, sbar.c's weapons and items, menu.c's episodes and levels, `give`) came with its modules, but
no trace runs it. And the oracle is the C: what Winquake.exe itself does where the oracle's platform layer stands in
has been checked only where a milestone needed it (snd_win.c's messages, the build stamp, the saves and demos).
Milestone 10 audits the executables, ports the last two drivers, traces the mission packs, and adds scenarios for
what no trace reaches yet.

**The audit** (decision 42). Winquake.exe 1.09 from a scratch copy, by hand, as §11.3's step 6; each finding is
either emulated (the oracle and QuakeSharp alike) or documented, in a new §3.4:

| What | How | Expected |
|---|---|---|
| The start-up text | `-condebug`'s `qconsole.log` against the oracle's console at the same point | in_win.c's joystick line (`joystick not found -- ...`), vid_win.c's mode messages, the network drivers' (Winsock TCP/IP with the real address, IPX), `CDAudio_Init` (no drive here) |
| `config.cfg` | Written after a run that changes nothing, against the oracle's | in_win.c's archived cvars (`joystick`...) |
| The modes | `vid_describemodes`, `vid_nummodes`, the Video menu (a `screenshot`), the mode it starts in | The three windowed modes (320×240; 640×480 and 800×600, stretched: frames of 320×240 and 400×300 shown doubled), the fullscreen modes this machine offers, `VID_Update`'s switch to `_vid_default_mode_win` at the first frame unless `-startwindowed` |
| The mouse | In a window and full screen, with `_windowed_mouse` 0 and 1 | In a window the mouse does nothing (buttons included) until `_windowed_mouse` is 1; `-dinput` off by default; in_win.c changes the system's mouse acceleration (`-noforcemparms`...) |
| The network menus | The multiplayer menu | Whether IPX is offered |
| The pixels | Frames of `timedemo demo1`-`3` (`wait` lines, then `screenshot`: a timedemo runs one demo message per frame whatever the time) against the oracle's same frames (`-traceshot`) | How many pixels the x87 arithmetic and the `id386` assembly change (§3.1: documented, not emulated; now measured) |
| quake.exe (DOS) | Its version identified; read from the source's DOS drivers (sys_dos.c, vid_dos.c, vid_vga.c, vid_ext.c, in_dos.c, snd_dos.c, snd_gus.c, cd_audio.c, net_dos.c, net_ipx.c, net_ser.c...) and Chocolate Quake's notes (read, never copied) | DJGPP's `rand()`, VGA's palette index 0 (no colormap remapping), mode 0's 320×200 and the VESA modes, the serial and modem menus, the DOS start-up text; documented only (D4, decision 47) |

**Trace format 6** adds a line after `A`, and the frame line takes the mode:

| Line | Fields |
|---|---|
| `I <hash>` | in_win.c: `mouseactive`, `mouseinitialized`, `mouse_buttons`, `mouse_oldbuttonstate`, `mx_accum`, `my_accum`, `mouse_x`, `mouse_y`, `old_mouse_x`, `old_mouse_y`, the joystick's state (`joy_avail`, `joy_advancedinit`, `joy_haspov`, `dwAxisMap`, `dwControlMap`, `joy_oldbuttonstate`, `joy_oldpovstate`) and vid_win.c's `windowed_mouse` |
| `V <frame> <palette>` | As format 5's, the frame's hash also taking the mode: `vid_modenum`, `vid.width`, `vid.height`, `vid.rowbytes`, `modestate` |

`-tracedump` writes in_win.c's fields and the mode by name; `trace-compare` knows `I`.

**Scripted mouse.** `-traceevents` takes two window-level lines besides `@key` (which stays `Key_Event`, so `e1.events`
replays as before): `<frame> @mouse <dx> <dy>`, the cursor moved (what in_win.c's `GetCursorPos` finds before the
frame), and `<frame> @buttons <state>`, a mouse button message with the buttons' state (`MainWndProc`'s
`IN_MouseEvent`). in_win.c decides what they do, as in WinQuake. `-recordevents` records the mouse as these (decision
43 extends decision 34: the oracle now has mouse motion).

**What the oracle adds**: in_win.c compiled unchanged in place of in_null.c, over a virtual Win32 in `src/in_oracle.c`
(`GetCursorPos`, `SetCursorPos`, `ClipCursor`, `ShowCursor`, `SystemParametersInfo`; winmm's joystick functions
answering as the audit shows Windows does without a joystick; DirectInput only with `-dinput`, not used); vid_win.c's
mode list, its commands and the Video menu in `vid_oracle.c` over a virtual display with the audit's modes (decision
44); an IPX LAN driver in `net_oracle.c` whose start fails as on today's Windows (§3.4); trace format 6 and the scripted mouse in `trace.c`; and `Trace-Demos.ps1` copying `hipnotic/pak0.pak` and
`rogue/pak0.pak` (never the packs' loose `config.cfg`, as id1 holds only its PAKs) and tracing them.

**Porting order.** Each step builds with the tests green and is committed; at steps 3-5 the oracle's traces change
first and QuakeSharp follows them.

1. **The audit**, above. §3.4 records it; D4 is settled with it. *Done: the IPX and joystick lines, in_win.c's
   `joystick` cvar and the seven modes are emulated in steps 3 and 4; the menus, status bar and console text were
   already the same pixel for pixel; the 3D view differs by `rand()` (Winquake.exe's follows real time) and by the
   x87's and the assembly's rounding, documented; quake.exe is 1.08, documented from the source.*
2. **What the traces reach.** A coverage run of the fidelity tests over Core (decision 46) lists, module by module, the ported
   functions and branches no trace reaches. That list is the work of step 6, and the measure of its end. Without a
   coverage tool, the list comes from reading each C module against the scenarios. *Done: `tools/Coverage.ps1`
   (coverlet.MTP, decision 46) writes the list. The fidelity tests reach 15,427 of Core's 19,954 lines (77.3%) and 65.2%
   of its branches. What they do not reach:*
   - *Play no scenario has yet: `chase_active` (chase.c never runs); `+mlook`, `+klook`, `+speed`, `+strafe`, `+use`
     and keyboard look; the powerups (their colour shifts, the face, the inventory's items); `+showscores` and the
     deathmatch overlays (never drawn); a coop game (the progs' `coop` and `teamplay` never read); most of `give` (51
     of its 67 lines); the water jump, a player stuck, a blocked pusher; Chthon's lava splash, the spawn's explosion,
     the mission packs' `TE_EXPLOSION2` and `EF_BRIGHTFIELD`; sprites clipped at the view's edges.*
   - *Console and keys: typing at the console (44 of `Key_Console`'s 68 lines: history, completion, scrolling back),
     `messagemode`, `messagemode2` and `Key_Message`, `clear`, `unbind`, and `wait` (never executed).*
   - *Screen and menus: `sizeup`, `sizedown`, the turtle, `SCR_ModalMessage` (a new game during a game), the options'
     sliders and the key bindings in part, the LAN and game options changed in part.*
   - *Commands: `quit`, `version`, `demos`, `say_team`, `tell`, `kick`, `viewmodel` and its kin, `entities`, `edict`,
     `edicts`, `edictcount`, `profile`, `flush`, `mcache`, `path`, `port`, `v_cshift`, `pointfile`, `timerefresh`,
     `host_speeds`, `r_speeds`, `r_dspeeds`, `r_timegraph`, `r_aliasstats`, `r_draworder`, `r_drawflat`.*
   - *The QuakeC builtins id1's progs never call (`vtos`, `nextent`, `WriteChar`, `WriteShort`, `WriteLong`,
     `WriteAngle`, `setspawnparms`, `error`, `objerror`, `eprint`...) and runtime errors (`PR_RunError`, `Host_Error`):
     the mission packs' progs may call some.*
   - *Unreachable in WinQuake as the oracle runs it: the serial and modem menus (no serial driver), the mixer's 8-bit and
     mono transfers (the DirectSound buffer is 16-bit stereo), the focus (`AppActivate`), a CD track's end (unit tests
     drive it), the datagram driver's connections and the server list (no other host before milestone 11), and
     functions the C never calls (`InsertLinkAfter`, `S_AmbientOff`, `S_AmbientOn`, `S_ClearPrecache`,
     `R_TransformPlane`, `D_log2`, `MaskForNum`, `Q_log2`, `W_GetLumpNum`, `D_DrawZPoint`, `D_FillRect`).*
   - *A gap found on the way: `FileSystem.BeginDisc` is never set, so `Draw_BeginDisc` never runs; it draws nothing in
     the frame (vid_win.c puts the disc on the screen), but the executable never shows the disc while loading.*
3. **in_win.c**: the oracle first (format 6, the scripted mouse, in_win.c over the virtual Win32), then
   `QuakeEngine.InWin.cs` (the stand-ins leave `QuakeEngine.Pending.cs`) over the platform's mouse and joystick (SDL3
   relative motion; a gamepad as `joyGetPosEx`'s axes and buttons where that maps cleanly, §8), with vid_win.c's part:
   `VID_Update`'s `_windowed_mouse`, `IN_ActivateMouse` in the full-screen modes, `VID_HandlePause`, the options' Use
   Mouse. Every trace changes (the joystick's line on the console, `I`) and matches again. `mouse.events`: `+mlook`,
   `sensitivity`, `m_filter`, `m_pitch`, `m_yaw`, `m_forward`, `m_side`, `lookstrafe`, `lookspring`, `freelook`, the
   options' invert mouse, `_windowed_mouse` on and off, the buttons, `force_centerview`. The executable captures the
   mouse when in_win.c activates it, and records it.
4. **vid_win.c's modes** (decision 44): the mode list, `vid_mode` (set in `VID_Update`, so on a frame boundary), the
   `vid_*` commands, the Video menu (the arrows, Enter, `T` to test a mode for 5 seconds and come back, `D` to make it
   the default), the stretched modes, `_vid_default_mode_win` at the first frame. `video.events`: the menu, every mode
   set from the menu and the console, in a game too (the z-buffer and the surface cache at the new size, the refdef
   recalculated), a save and a demo across a change. The existing traces keep their sizes (`-oraclevid`, a mode of its
   own as now). The executable's window follows the mode: a window of the mode's size for the windowed modes,
   borderless full screen for the others, the frame scaled as §6.4. D7 is settled with it.
5. **The mission packs**: the MD5s confirmed as the original releases (the "to verify" of §2 goes); with `-hipnotic`
   and `-rogue`: their demos (hipnotic's `hipdemo1`-`4`, rogue's `demo1`-`3`) and attract loops (their own `quake.rc`);
   `hiptour.events` and `roguetour.events`, generated as `tour.events`: every map of each pack with its weapons and
   items (`give`, the impulses), so sbar.c draws them; their saves on every map, read back; the single-player and
   network menus' episodes and levels; rogue's team modes (`teamplay` 4-6, the scores sbar.c draws); each pack's end.
   `GameTests` checks them; the executable plays the packs' tracks (the rerelease's `hipnotic/music`, `rogue/music`).
6. **The remaining UI**, from step 2's list, in new events files traced at 320×240 (some also at 640×480). Expected
   there: the console (scrolling back, the history, command completion, `messagemode` and `messagemode2`,
   `con_notifytime`), screen.c (the turtle, the net icon, the disc, `showpause`, the loading plaque, centre prints of
   several lines), sbar.c (the deathmatch overlays and the mini overlay at each `viewsize`, the face with the
   powerups, the sigils, keys and armours, coop's `+showscores`), menu.c (the help pages, every options item, Reset to
   defaults, binding and clearing keys, the quit messages), the renderer's debug cvars (`r_drawflat`, `r_draworder`,
   `r_speeds`, `r_timegraph`, `r_aliasstats`, `r_polymodelstats`, `r_reportsurfout`, `r_reportedgeout`, the
   `r_maxsurfs` and `r_maxedges` overflows, `r_waterwarp`, `d_subdiv16`, `chase_active`), and the server's rarer
   commands. What no input can reach in the oracle's build is listed with the reason.
7. **The shareware smoke test** (§10's last row, decision 48): CI fetches `quake106.zip`, extracts its `pak0.pak` (the
   registered one, same MD5), and plays demo1-3 against the last lines of their traces, kept in the test.

**Done when** §3.4 holds the audit (each difference emulated or documented); in_win.c and vid_win.c's modes are ported
and `QuakeEngine.Pending.cs` keeps only zone.c's `Cache_Report` and `Hunk_Check` (managed memory: nothing to do); the
mission packs' demos, attract loops, tours, saves and scripted games match the oracle on every frame, their files
included; each function step 2 found unreached is reached by a trace in sync or listed with why; and the executable
plays with the mouse, in every mode, and both mission packs.

## 12. Decision log

| # | Topic | Decision | Date |
|---|---|---|---|
| 1 | Reference source | `id-Software/Quake` as read-only submodule at `reference/Quake`; `WinQuake/` (1.09) is the code ported | 2026-10-12 |
| 2 | Stack | .NET 10 / C# 14, `.slnx`, central package management, xUnit v3 on Microsoft.Testing.Platform, GPL-2.0, as DoomSharp | 2026-10-12 |
| 3 | Platform & graphics | SDL3 (`ppy.SDL3-CS`) for window, input and audio; Vulkan 1.3 (Vortice.Vulkan 3.3.0, Vortice.VulkanMemoryAllocator 1.8.0) for presentation; Slang shaders compiled with `slangc`, as Contrail | 2026-10-12 |
| 4 | Game data | Original 1.06 PAKs identified by MD5; rerelease data excluded from fidelity tests; no game data in the repository | 2026-10-12 |
| 5 | Faithful renderer | WinQuake's software renderer ported exactly into an 8-bit frame, checked pixel for pixel; Vulkan only presents it (palette, aspect, scaling). A Vulkan hardware renderer may come later as an enhanced mode that only reads the game (§6.4, §6.6) | 2026-10-12 |
| 6 | Floating point | The fidelity target is the C code with IEEE single precision per float operation (`FLT_EVAL_METHOD` 0) on both sides, not the original executables' x87 and assembly; one libm for both, musl's `sin`/`cos`/`tan`/`atan`/`atan2`/`pow` ported to C# and linked into the oracle; the Microsoft CRT's `rand()` (§3.1) | 2026-10-12 |
| 7 | Oracle | id's own WinQuake 1.09 source built headless (clang-cl, 32-bit x86, SSE2, `id386` 0, null drivers, virtual clock) with a trace patch, in `tools/oracle`; Chocolate Quake (GPLv3) only consulted as a behavioural reference, never copied (§10) | 2026-10-12 |
| 8 | Hosting | Private GitHub repository `MTurnhout/QuakeSharp`, pushed after each commit | 2026-10-12 |
| 9 | Strings | C strings become .NET strings with one byte per character (Latin-1), keeping the high-bit characters and `strcmp` order (§6.2 rule 8) | 2026-10-12 |
| 10 | File system | Search path, pack files, registered check and `com_modified` as common.c; managed buffers for every `COM_LoadFile` variant, no `-cachedir`, file names on disk matched in any case (§6.1) | 2026-10-12 |
| 11 | musl | musl 1.2.5 as a read-only submodule (`reference/musl`, MIT): its six functions ported to C# with their constants generated, and the same C linked into the oracle under `musl_*` names; checked on 18 sets of 100,000 inputs (§3.1) | 2026-10-12 |
| 12 | Virtual time | The oracle and the tests run `Host_Frame` in steps of 1/64 s of a virtual clock that every `Sys_FloatTime` reads (§6.3) | 2026-10-12 |
| 13 | Virtual sound device | The oracle and the tests mix into a virtual DirectSound-like buffer whose play position follows the virtual clock, because `S_StartSound`'s `rand()` draw depends on what the mixer has painted (§7.1) | 2026-10-12 |
| 14 | WinQuake's video driver | What `vid_win.c` does that shows is part of the target: the colormap remapping of index 0, `S_Init` between the mode sets, its console messages and cvars, the "(WinQuake) 1.09" console stamp and Winquake.exe's build stamp (§6.4) | 2026-10-12 |
| 20 | Engine object | One `QuakeEngine` with a partial file per C module and the C's names for its members (§6.2 rule 13), instead of a class per subsystem: the modules reach into each other's globals as freely as the C does | 2026-10-12 |
| 21 | Line buffer remnants | `Cbuf_Execute`'s line buffer is kept and passed whole so `COM_Parse` reads past an unterminated quote as the C does; between frames QuakeSharp keeps what the last line left (§3.2) | 2026-10-12 |
| 22 | Memory limits | The zone's 48 KB and the hunk's 16 MB are not emulated: what would fill them runs on (§3.2) | 2026-10-12 |
| 23 | Where files go | Files the game writes go to a user folder (`%APPDATA%\QuakeSharp\<game dir>`), searched before the game directory's own loose files; the installation is only read (§9) | 2026-10-12 |
| 24 | Network VCR | net_vcr.c's recorder (`-record`, `-playback`: id's network debugging aid) is not ported | 2026-10-12 |
| 15 | Oracle build | WinQuake's sources are copied and patched in the oracle's work folder (`quake-oracle.patch`: only `id386` off so far), never in `reference/Quake`; its platform layer lives in `tools/oracle/src` (§10) | 2026-10-12 |
| 16 | Shaders in CI | The shaders are compiled at build time and embedded; CI downloads Slang 2026.18 from its GitHub release, checks its SHA-256 and caches it, rather than committing SPIR-V (§4) | 2026-10-12 |
| 17 | Presentation | Two passes, integer nearest-neighbour then linear to the 4:3 rectangle, letterboxed; a UNORM swapchain so the palette's colours pass unchanged; borderless fullscreen by default, `-window`, `-noaspect` (§6.4) | 2026-10-12 |
| 18 | Keyboard | Keys as `vid_win.c`'s `MapKey` reads them: PC scan codes through `scantokey`, without the extended prefix (so by position, the grey keys as the keypad's, NumLock as Pause) (§8) | 2026-10-12 |
| 19 | Sound device | An SDL3 audio stream of 16-bit stereo at 11025 Hz playing a 64 KB ring buffer round and round, as `snd_win.c`'s DirectSound buffer; its position is how far SDL has taken (§7.1) | 2026-10-12 |
| 25 | Trace lines by writer | Trace format 2: `E` hashes what the client keeps, `D` what the renderer writes into the entities (and the static entities it adds to the visible list), `S` the sound channels, `T` the console text; the client is checked before the renderer exists (§10) | 2026-10-12 |
| 26 | What the client needs, ported with it | The client's state depends on code beyond `cl_*`: `S_StartSound` draws `rand()` depending on what the mixer has painted, `R_DrawParticles` frees particles the client's effects then reuse, `R_SetupFrame`'s view leaf sets the contents' colour shift, `SCR_UpdateScreen` decides whether the view runs. So snd_dma/snd_mix/snd_mem, r_part and the control flow of screen.c and r_main/r_misc are ported whole now, their drawing calls stand-ins until milestone 6 | 2026-10-12 |
| 27 | The video driver in Core | vid_win.c's part that the game sees (one mode of the size `QuakeParms` asks for, its cvars and commands, the colormap remapping, `S_Init` between the mode sets, the palette, the surface cache message) is ported in Core as the oracle has it; the platform only shows `vid.buffer` through `IVideoOutput`. Its other modes come with milestone 6 (§6.4) | 2026-10-12 |
| 29 | Renderer memory | The software renderer's arrays, offsets and surface cache keep the C's layout where it shows (§6.1); undefined reads get fixed substitutes shared with the oracle (§3.3: `finalverts` zeroed) | 2026-10-12 |
| 30 | Video menu | One mode, as the oracle's: `VID_MenuDraw` lists nothing, `VID_MenuKey` goes back to the options on Escape as vid_win.c's does (oracle and QuakeSharp alike); the mode list waits for more modes (superseded by decision 44) | 2026-10-12 |
| 31 | Scripted keys | `-traceevents` lines may be `<frame> @key <key> <down>`, a key event, so menus and the console are driven as a player does; QuakeSharp's `TraceRun` reads the same files | 2026-10-12 |
| 28 | Base directory "." | The oracle's traces and QuakeSharp's run with WinQuake's base directory "." (the console shows `./id1/pak0.pak` wherever the data is); `QuakeParms.workdir` is the folder "." means, the process's current folder by default | 2026-10-12 |
| 32 | Server verification (was D5) | Scripted games: `-traceevents` files of console commands and key events on the virtual clock, written by hand or recorded from play (decision 34), run by the oracle and QuakeSharp and compared every frame with trace format 4's server lines (`G`, `Q`, `N`, `W`, `M`) besides the client's and the renderer's. Demos the oracle records wait for milestone 9, where `.dem` files are compared byte for byte (§11.1) | 2026-10-12 |
| 33 | Virtual LAN driver | net_dgrm.c comes with milestone 7, and both sides get a virtual LAN driver: net_wins.c without sockets (TCP/IP available at a fixed address, its console message, no name resolves and no packet arrives). The oracle runs WinQuake's driver list (loopback, datagram), so the network menus can be played and traced and a game they start runs over loopback; the executable uses it until milestone 11 adds the UDP driver | 2026-10-12 |
| 34 | Recorded play | `-recordevents <file>` makes the executable run as a trace does (the virtual clock, paced to real time; the virtual sound device; no `config.cfg` read) and write the key events per frame as `-traceevents` lines, keys and mouse buttons only (the oracle has no mouse motion), so a game played in QuakeSharp replays the same in the oracle | 2026-10-12 |
| 35 | Milestone 7 closed | Closed with one playthrough, `e1.events` (24,500 frames from the menu through e1m1 into e1m2), in sync on every frame with the scripted games' coverage of every map; playthroughs of more episodes are added to `Trace-Demos.ps1` and `GameTests` as they are recorded, without holding up milestone 8 | 2026-10-12 |
| 36 | Mix trace | Trace format 5's `P` hashes the sound device's whole 64 KB buffer every frame (what the device plays: the painted samples, the clears, where they went), not the paint buffer or only the frame's new samples; `-tracedump` shows the bytes painted during the frame (§11.2) | 2026-10-12 |
| 37 | CD audio | cd_win.c is ported in Core over an `ICdDrive` answering the MCI commands it sends. The oracle compiles cd_win.c unchanged over a virtual MCI drive (`cd_oracle.c`: Quake's disc, track 1 data, 2-11 audio, tracks without end) instead of cd_null.c; the traces and `-recordevents` use the same virtual disc, checked by format 5's `A` line. The executable's drive plays the user's track files on their own SDL3 stream, outside the mixer, as the CD's analogue output was (§7.2) | 2026-10-12 |
| 38 | Sound device | snd_win.c's DirectSound path is the target: 11025 Hz only (the `-sndspeed` §7.1 once named is not WinQuake's), its start-up messages printed on both sides, the focus handled as vid_win.c (clears) and DirectSound's exclusive buffer (silent while unfocused); the waveOut path (`-wavonly`), `-primarysound` and `-snoforceformat` are not ported (§7.1) | 2026-10-12 |
| 39 | Music decoder (was D6) | NVorbis 0.10.5 (NuGet, MIT, GPL-2-compatible) decodes the rerelease's Ogg Vorbis tracks; WAV tracks are SDL3's. Approved with the milestone 8 plan | 2026-10-12 |
| 40 | Comparing files | The files the game writes are compared at the end of each run, not by a trace line: `Trace-Demos.ps1` keeps what each oracle run wrote (`<name>.files/`), the tests compare QuakeSharp's user folder with it (the same names, byte for byte), and runs that read files start with the oracle's copied in (its `id1`, QuakeSharp's user folder). `TraceRun` quits at the frame limit as the oracle does, so every run's config.cfg is compared. Trace format stays 5 (§11.3) | 2026-10-12 |
| 41 | The real executable's files | Saves, demos and config.cfg cross-loaded with Winquake.exe 1.09 by hand, not traced: its x87 arithmetic may print other last digits than the oracle (§11.3) | 2026-10-12 |
| 42 | Executable audit | Winquake.exe 1.09 is audited by hand from a scratch copy (its start-up text, config.cfg, modes, mouse, menus, and timedemo frames against the oracle's pixels); quake.exe from the source's DOS drivers and Chocolate Quake's notes. Each difference is emulated on both sides or documented, in §3.4 (§11.4) | 2026-10-12 |
| 43 | Mouse and joystick | in_win.c is ported in Core and compiled unchanged in the oracle over a virtual Win32 (cursor, winmm's joystick); `-traceevents` takes window-level `@mouse <dx> <dy>` and `@buttons <state>` lines that in_win.c reads as WinQuake does, `@key` staying `Key_Event`; `-recordevents` records them, so the oracle has mouse motion (extends decision 34). Trace format 6 adds `I` (§11.4) | 2026-10-12 |
| 44 | Video modes | vid_win.c's mode list (the three windowed modes, two of them stretched, and fixed full-screen modes as Winquake.exe lists them here), `vid_mode`, its commands and the Video menu, ported in Core and copied into `vid_oracle.c`; the frame's hash takes the mode. Supersedes decision 30 (§11.4) | 2026-10-12 |
| 45 | Mission packs | Traced as id1 is: the oracle's game folders hold only the original PAKs (`hipnotic/pak0.pak`, `rogue/pak0.pak`, by MD5, never their loose config.cfg), run with `-hipnotic` and `-rogue`; their demos, attract loops, tours, saves and scripted games checked every frame (§11.4) | 2026-10-12 |
| 46 | Coverage (was D9) | `coverlet.MTP` (MIT) in `QuakeSharp.Fidelity.Tests` only, switched on by a flag for local runs with the game data, so normal runs and CI stay as fast; the report stays outside the repository. It lists what no trace reaches (milestone 10, step 2); Microsoft's `Microsoft.Testing.Extensions.CodeCoverage` is the fallback if it misbehaves | 2026-10-12 |
| 47 | quake.exe not run (was D10) | The DOS executable's differences are documented from the source's DOS drivers and Chocolate Quake's notes, not measured: nothing of DOS is emulated (D4), and DOSBox's emulated x87 and timing would measure DOSBox. Revisited only if the audit raises a question only quake.exe answers | 2026-10-12 |
| 48 | Shareware in CI (was D11) | CI downloads `quake106.zip` from a fixed mirror (chosen with the user before it is committed), checks its MD5, extracts `pak0.pak` (the registered one, same MD5) and caches it, never committed; the test keeps only the last trace lines of demo1-3 (milestone 10, step 7) | 2026-10-12 |
| 49 | Fidelity target version (was D4) | WinQuake 1.09 as the source has it, with the Microsoft CRT's `rand()`. Audited (§3.4): Winquake.exe's start-up lines (IPX, the joystick), its `joystick` cvar and its seven modes are emulated; its real-time `rand()` and its x87 and assembly rounding are documented; DOS quake.exe 1.08's differences (DJGPP's `rand()`, VGA's palette, its drivers) are documented, not emulated | 2026-10-12 |

## 13. Open decisions

To confirm with the user before the milestones they affect; the proposals are the defaults until then:

| # | Question | Proposal |
|---|---|---|
| D7 | Default video mode | 320×240 in a window scaled to the screen, as WinQuake's default mode (the fidelity target is WinQuake: decision 14); DOS's 320×200 and larger modes from the menu |
| D8 | Scope of v1 | Quake and both mission packs on the WinQuake engine; GLQuake and QuakeWorld after v1 |
