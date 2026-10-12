# Reference oracle

QuakeSharp is checked against WinQuake 1.09's own C code, built headless: both play the same demo or scripted game
and write a trace, and the traces must agree on every frame. See design.md §10.

| File | Purpose |
|---|---|
| `Build-Oracle.ps1` | Builds `numerics.exe` and `quake-oracle.exe` in a work folder outside the repository (clang-cl, 32-bit x86, SSE2 arithmetic, no FMA contraction, static C runtime) |
| `Trace-Demos.ps1` | Writes the reference traces of the demos and the attract loop |
| `numerics.c` | musl's libm and the C runtime's `rand()` on fixed input sets: the expected values of `LibmTests` and `RandTests` |
| `quake-oracle.patch` | The changes to WinQuake's sources (a copy in the work folder; `reference/Quake` is never touched) |
| `src/` | The oracle's platform layer in place of WinQuake's Windows drivers, and the trace writer |
| `musl/` | Stands in for musl's internal headers so its libm builds on Windows; `musl_names.h` renames its functions `musl_*` |

Everything the oracle produces stays outside the repository: traces are derived from game data.

## Building and tracing

Needs LLVM (clang-cl) and Visual Studio's C++ tools (for the Windows SDK and the C runtime).

```bash
powershell -ExecutionPolicy Bypass -File tools/oracle/Build-Oracle.ps1 -WorkDir D:/Dev/QuakeSharp-oracle
```

```bash
powershell -ExecutionPolicy Bypass -File tools/oracle/Trace-Demos.ps1 -Oracle D:/Dev/QuakeSharp-oracle/quake-oracle.exe -QuakeDir path/to/Quake -OutDir D:/Dev/QuakeSharp-oracle/traces
```

`Trace-Demos.ps1` copies the original `id1/pak0.pak` and `id1/pak1.pak` into the oracle's own Quake folder (beside
the executable) and removes `config.cfg` before every run, since the oracle writes one when it quits. It writes
`demo1-3.trace` (320x240, WinQuake's default windowed mode), `.320x200.trace` (DOS Quake's mode 0) and
`.640x480.trace`, and `attract.trace` (the attract loop, 20,000 frames).

## How the oracle is built

- **WinQuake's C, not its assembly.** The sources are copied and `quake-oracle.patch` applied: with `ORACLE` defined,
  `quakedef.h` and `quakeasm.h` no longer define `__i386__` for `_M_IX86`, so `id386` is 0 and the C versions of the
  assembly routines are used, as the Linux and non-Intel builds do. `_WIN32` stays defined: the code is WinQuake's
  (its menus, its console stamp "(WinQuake) 1.09", S_Init from the video driver).
- **Floating point** as design.md §3.1: SSE2, every float operation rounded to single precision.
- **musl's libm**: `sin`, `cos`, `tan`, `atan`, `atan2` and `pow` are musl 1.2.5's (`reference/musl`), renamed
  `musl_*` in musl's sources and Quake's alike (`musl/musl_names.h`, force-included).
- **`rand()`** is the Microsoft C runtime's generator, implemented in `src/sys_oracle.c` (`src/oracle.h` renames it) so
  the traces can show its state; `numerics.c` checks that it gives the C runtime's sequence.
- **The build stamp**: `host.c` prints `"Exe: "__TIME__" "__DATE__` on the console, which is in the frames; `oracle.h`
  sets them to Winquake.exe 1.09's stamp, `09:30:49 Mar 21 1997`.

### The platform layer (`src/`)

- `sys_oracle.c`: `sys_null.c`'s file functions; `Sys_Error` writes an `Err` line and exits with 1; time is a
  **virtual clock**: every frame it moves one step (`-oraclestep <n>`: 1/n s, 64 by default) and `Host_Frame` runs with
  that step, so `Sys_FloatTime` and everything that reads it (the host, the network code) see the same times in every
  run. The step is a binary fraction, exact in the `float` that `Host_Frame` takes, and above 1/72 s so
  `Host_FilterTime` runs a frame every time. 16 MB of memory, as WinQuake takes on any machine with 32 MB or more
  (`-heapsize <kb>`).
- `vid_oracle.c`: `vid_win.c` with one windowed mode, rendered in memory at `-oraclevid <width> <height>` (320x240 by
  default). Kept from `vid_win.c`: its cvars and commands, the remapping of colormap entries 0 to the palette's closest
  other black (GDI cannot remap index 0; this changes the frames' pixel values, not their colours), S_Init between the
  hidden first mode set and the real one, the pixel aspect, the surface cache, `ClearAllStates` and the mode name on
  the console. The loading disc is never in the frame (WinQuake draws it on the screen and restores it).
- `snd_oracle.c`: a 64 KB, 16-bit stereo, 11025 Hz buffer standing in for DirectSound's, whose play position follows
  the virtual clock. The mixer then paints the same samples at the same frames in every run, which the game depends on:
  `S_StartSound` draws from `rand()` when an identical sound has not been painted yet.
- `in_oracle.c`, with `in_null.c` and `cd_null.c` from WinQuake: no mouse, no CD.
- `trace.c`: the traces and the scripted input.

## Switches

| Switch | |
|---|---|
| `-trace <file>` | Write the trace |
| `-tracequitafterdemo` | Quit when the demo playing ends |
| `-oracleframes <n>` | Quit after n frames |
| `-traceshot <frame>` | Also write that frame (any number of times) as `<trace>.<frame>.shot`: width, height (32-bit), the palette, the pixels; `quakesharp-tools shot2png` makes a PNG of it |
| `-traceevents <file>` | Scripted input: lines of `<frame> <console text>`, added to the command buffer before that frame runs (keys are console commands: `+forward`, `-forward`, `impulse 8`...) |
| `-oraclevid <w> <h>` | The video mode (320x200 to 1280x1024) |
| `-oraclestep <n>` | Frames of 1/n s |
| `-oracleverbose` | Print `Sys_Printf` (the console) to stdout |

## The trace format

One line `H 1 <width> <height> <frames per second>`, then for every frame, after `Host_Frame`:

| Line | Fields |
|---|---|
| `F <frame> <host_framecount> <realtime> <seed> <calls>` | The frame number (from 1), `host_framecount`, `realtime` as the hexadecimal bits of the double, the `rand()` state (`holdrand`) and the number of `rand()` calls so far |
| `C <hash>` | The client state: `cls` and `cl` (see `Client` in `src/trace.c` for the fields and their order) |
| `E <hash>` | The entities: `cl_entities` up to `cl.num_entities`, the static entities, the view model, the visible list |
| `X <hash> <particles>` | The effects: temp entities, beams, dynamic lights, light styles and `d_lightstylevalue`, the active particles (and their number) |
| `R <hash>` | The view: `r_refdef`'s rectangle, origin, angles and field of view |
| `V <frame> <palette>` | The frame (the 8-bit buffer, row by row) and the palette last passed to `VID_SetPalette` |
| `Err <text>` | `Sys_Error`'s message, last |

Hashes are 64-bit FNV-1a over the fields' bytes: `int` and `float` as 4 little-endian bytes, `double` 8, vectors three
floats, strings their bytes and then 4 zero bytes, a model its name, a colormap 0 (the normal one), 1 + n (player n's
translation) or -1, an entity pointer its array and index (`cl_entities` index, `0x10000 +` static, `0x20000 +` temp,
`0x30000` the view model).
