# Reference oracle

QuakeSharp is checked against WinQuake 1.09's own C code, built headless: both play the same demo or scripted game
and write a trace, and the traces must agree on every frame. See design.md §10.

| File | Purpose |
|---|---|
| `Build-Oracle.ps1` | Builds `numerics.exe` and `quake-oracle.exe` in a work folder outside the repository (clang-cl, 32-bit x86, SSE2 arithmetic, no FMA contraction, static C runtime) |
| `Trace-Demos.ps1` | Writes the reference traces of the demos and the attract loop |
| `numerics.c` | musl's libm and the C runtime's `rand()` on fixed input sets: the expected values of `LibmTests` and `RandTests` |
| `src/scenarios.c`, `units/*.scenario` | `quake-oracle.exe -oraclescenario <file> [-print]`: a scenario of commands, key events and prints run on WinQuake's command, cvar, console and key code; prints the hash of its transcript (what was printed and the state dumps), or with `-print` the transcript. `ScenarioTests` runs the same files |
| `src/units.c` | `quake-oracle.exe -oracleunits`: WinQuake's own functions (`Q_atoi`, `Q_atof`, printf formatting, `mathlib.c`...) and the C runtime's that read savegames (`atof`, `fscanf`'s `%f`, `%i` and `%s` on a file in text mode) on fixed input sets, the expected values of `OracleUnitTests`; `-oracleunits <set> <n>` prints a set's first results (and inputs) |
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
the executable) and empties its `id1` of everything else before every run. What a run leaves there (`config.cfg`,
which the oracle writes when it quits, savegames, demos, screenshots, `qconsole.log`) is kept in `<name>.files`
beside the trace, for the tests to compare with QuakeSharp's (design.md §11.3); a run can start with files an
earlier run wrote. `-Only <names>` writes only those traces. It writes
`demo1-3.trace` (320x240, WinQuake's default windowed mode), `.320x200.trace` (DOS Quake's mode 0) and
`.640x480.trace`, `attract.trace` (the attract loop, 20,000 frames), `attract.config.trace` (600 frames of it with
the `config.cfg` the menus' run wrote), and `menus.trace` and `menus.640x480.trace`: 3,700 frames of the attract loop with
the scripted keys and commands of `events/menus.events` (every menu but the network ones, the console, the scores,
the help pages and the renderer's cvars), and the scripted games of `events/`: `start.trace` (a new game from the
menu, the start map's normal skill hall and episode 1's slipgate), `commands.trace` and `commands.640x480.trace` (the
server's commands, fights, a save and a load, a restart and a level change), `intermission.trace` (e1m1's exit and
the intermission), `finale.trace` (e1m7's exit and the episode's finale) and `netmenus.trace` (the network menus over
the virtual TCP/IP driver: a search, the LAN settings, the game options starting a three-player deathmatch, the setup
menu's name and colours sent to it), `tour.trace` (every map of the four episodes, the end and the six deathmatch
maps in turn, 600 frames each, in god mode with all the weapons, shooting while turning and moving; the routes through
walls are flown with `noclip`, planned from the edicts' positions in `-tracedump`), and `e1.trace`: a game played by
hand in QuakeSharp and recorded with `-recordevents`, from the menu through e1m1 into e1m2, ending on the quit screen
(24,500 frames), and `sound.trace`: the sound and CD commands and cvars, the menu's volume sliders, a game's
pause and level changes (3,500 frames), also traced with `-nosound -nocdaudio` (`sound.nosound.trace`).
The files the game writes (milestone 9): `saves.trace` (a save on every map of the episodes, the end and the
deathmatch maps, the save and load menus, the refusals: 17,705 frames) and `loads.trace` (each of its saves loaded
in a fresh run and played on: 12,905 frames); `record.trace` (with `-condebug`: demos recorded through a level change
and an intermission, with a forced CD track, and cut by the end of the run, two screenshots), then each of its demos
played back in a fresh run (`record.d1-d3.trace`) and `d1` as a timedemo (`record.timedemo.trace`).
The rest of the UI (milestone 10, step 6): `console.trace` (typing at the console: completion, editing, the history,
the line's limit, scrolling back, auto-repeat; the console in a game, `messagemode` and `messagemode2`,
`con_notifytime`, `clear`, `unbind`, `wait`: 3,300 frames), `screen.trace` and `screen.640x480.trace` (the view's sizes
and their bounds, the pause picture, the turtle on `host_framerate`'s long frames, `lcd_x`'s two views, a level that
fails to load, then `Host_Error`: 4,980 frames), `sbar.trace` and `sbar.640x480.trace` (the faces with the powerups,
their flashes, numbers past three digits and negative, e1m7's rune, a deathmatch's scores at each size and its
intermission, coop's frags: 5,013 frames), `menuwalk.trace` (every menu's cursor past both ends, the options' sliders
past their bounds, binding cancelled, a new game during a game answered no then yes, the save menu's refusals, the
setup, LAN and game options' bounds, the quit menu, ending in its yes: 4,899 frames), `hostcmds.trace` (the commands no
other run uses, their usage lines and refusals, a deathmatch's refused cheats: 8,880 frames) and `rdebug.trace` (the
renderer's debug cvars and commands with `-surfcachesize 160`, `pointfile` reading `events/e1m1.pts`, put in the
run's `maps`, `chase_active`, quad rockets: 1,787 frames).
The shareware (milestone 10, step 7): `shareware.demo1-3.trace`, demo1-3 from a second Quake folder beside the
executable, `quake-shareware`, whose `id1` holds only `pak0.pak` (the shareware's is that file, design.md decision 48).
They are `demo1-3.trace` byte for byte; `SharewareTests` keeps the lines of their last frames (`tail -17`), for CI,
which has no traces: if they change, copy the new ones into the test.

With the mission packs in `-QuakeDir` (`hipnotic/pak0.pak`, `rogue/pak0.pak`; only those PAKs are copied, never the
packs' loose `config.cfg`, design.md decision 45), it also runs `-hipnotic` and `-rogue`, in their own game folders,
whose files are kept: their demos (`hipdemo1-4.trace`, `roguedemo1-3.trace`) and attract loops (`hipattract.trace`,
`rogueattract.trace`: their own `quake.rc`, once around); the tours (`hiptour.trace`, 18,000 frames, and
`roguetour.trace`, 17,000: on every map of the pack a noclip route to its items, planned from the oracle's
`-tracedump` of each map, then the pack's weapons fired and a save named after the map) and those saves loaded in a fresh
run and played on (`hiploads.trace`, `rogueloads.trace`), ending in the load menu; the menus (`hipmenus.trace`,
`roguemenus.trace`: Single Player's new game, then the game options' teamplay values, every episode and level, and a
game they start: rogue's capture the flag, then its one-flag, three-team and tag modes with the scores); and each
pack's end (`hipend.trace`: Armagon killed and the finale; `rogueend.trace`: the four buttons, the time machine
destroyed and the finale).

## How the oracle is built

- **WinQuake's C, not its assembly.** The sources are copied and `quake-oracle.patch` applied: with `ORACLE` defined,
  `quakedef.h` and `quakeasm.h` no longer define `__i386__` for `_M_IX86`, so `id386` is 0 and the C versions of the
  assembly routines are used, as the Linux and non-Intel builds do. `_WIN32` stays defined: the code is WinQuake's
  (its menus, its console stamp "(WinQuake) 1.09", S_Init from the video driver).
- **Its other changes** (`quake-oracle.patch`, each under `ORACLE`): fixed substitutes for memory the C reads without
  owning it (design.md §3.3: `R_AliasDrawModel`'s `finalverts` start zeroed; `COM_FileBase` stops at the start of a
  name without `/`, where it read the memory before it; `soundinfo` prints 0 for the sound buffer's address, which
  moves with every build, `mcache` 1 or 0 for a model's cache address, and `r_drawflat` colours a surface by its place
  in the frame's list instead of its address; `CL_GetMessage`'s `f`, copied into the view angles when a demo ends, starts at 0;
  `ED_ParseEpair`'s `string`, which a vector of fewer than three numbers is split past the copy into, starts zeroed: a
  rogue map's one-number `mangle`), and three
hooks for the traces that change nothing:
  `NET_SendMessage` and `NET_SendUnreliableMessage` pass every message to `Trace_Message`, world.c's
  `Oracle_AreaLinks` lists the area nodes' edicts, and cd_win.c's `Oracle_CDState` gives its static state.
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
- `vid_oracle.c`: `vid_win.c` with the machine taken out (design.md decision 44), every frame in memory. Kept: the
  mode list, `VID_SetMode` and its windowed and full-screen modes, the `vid_*` commands, the Video menu, the first
  frame's switch to `_vid_default_mode_win`, the buffer's locking (in the full-screen modes locking sets `vid.buffer`
  and `vid.rowbytes`), the cvars, the remapping of colormap entries 0 to the palette's closest other black (GDI cannot
  remap index 0; this changes the frames' pixel values, not their colours), S_Init between the hidden first mode set
  and the real one, the surface cache, `ClearAllStates`, `AppActivate`'s part, and what it does with the mouse. The
  machine is the one Winquake.exe was audited on: a 1920x1080 desktop, the windowed modes 320x240 (`-oraclevid`'s size),
  640x480 and 800x600 (stretched), and DirectDraw's 8-bit full-screen modes 320x240, 640x480, 800x600 and 1024x768, one
  page each (drawn in a memory DC). The window's client area is at the screen's top left and gets the focus when first
  shown. The loading disc is drawn into the frame and taken out again around every file's read (`D_BeginDirectRect`,
  `D_EndDirectRect`), with nothing shown: the frame is the same afterwards. Not part of it: `-dibonly` and the
  full-screen DIB modes, the window's position, minimizing, and an 8-bit desktop's palette. The frame's and the
  z-buffer's memory start after 64 bytes of padding: a sprite clipped to the view's left edge can write one pixel and
  one depth value before them (design.md §3.3), which land there rather than in the heap's bookkeeping.
  `Trace-Demos.ps1` starts every run with `-startwindowed -noautostretch`: in mode 0.
- `snd_oracle.c`: a 64 KB, 16-bit stereo, 11025 Hz buffer standing in for DirectSound's, whose play position follows
  the virtual clock. The mixer then paints the same samples at the same frames in every run, which the game depends on:
  `S_StartSound` draws from `rand()` when an identical sound has not been painted yet. It prints what snd_win.c's
  DirectSound path prints the first time (`Set primary sound buffer format: yes`, `Using secondary sound buffer`, the
  format, `DirectSound initialized`), as Winquake.exe 1.09 does on today's Windows.
- `cd_oracle.c`: the MCI commands of WinQuake's `cd_win.c` (compiled unchanged) go to a virtual drive holding Quake's
  disc, track 1 data and tracks 2-11 audio, instead of winmm (design.md decision 37). With no window procedure no MCI
  notification arrives, so no track ends. The console says "CD Audio Initialized" and the `cd` command works.
- `in_oracle.c`: the Win32 calls of WinQuake's `in_win.c` (compiled unchanged) go to a virtual machine instead of
  user32 and winmm (design.md decision 43): a 1920x1080 screen whose cursor `-traceevents` moves (`@mouse`) and whose
  buttons it presses (`@buttons`), each delivered as the window procedure gets it, the cursor confined to the screen or
  to `ClipCursor`'s rectangle (its right and bottom edges excluded); winmm's 16 joystick slots, each unplugged, so the
  console says "joystick not found -- no valid joysticks (a7)" as on Windows without one; the system's mouse
  parameters, Windows' defaults. DirectInput (`-dinput`) is not part of it.
- `net_oracle.c`: WinQuake's network drivers (`net_win.c`'s list: loopback, then datagram, `net_dgrm.c`) over its two
  LAN drivers: net_wins.c without sockets (design.md decision 33): TCP/IP is available (the menus offer it and a game
  started from them runs over loopback), the address is 127.0.0.1 once listening, the console says "Winsock TCP/IP
  Initialized", no name or address resolves and no packet arrives; and net_wipx.c as today's Windows starts it, without
  IPX: "WIPX_Init: Unable to open control socket" (design.md §3.4).
- `trace.c`: the traces and the scripted input.

## Switches

| Switch | |
|---|---|
| `-trace <file>` | Write the trace |
| `-tracequitafterdemo` | Quit when the demo playing ends |
| `-oracleframes <n>` | Quit after n frames |
| `-traceshot <frame>` | Also write that frame (any number of times) as `<trace>.<frame>.shot`: width, height (32-bit), the palette, the pixels; `quakesharp-tools shot2png` makes a PNG of it |
| `-tracedump <frame>` | Also write that frame's state (any number of times) field by field as `<trace>.<frame>.dump`, floats as their bits: the client's entities, then the server (`server` line), the QuakeC globals and every edict's header and fields by name (`global`, `edict` lines, strings quoted with `\xHH` escapes), the messages sent during the frame in hexadecimal (`message <sender> <reliable> <result> <size> <bytes>`), the sound (`sound`: the mixer's times, `channel` lines, `painted <from> <to> <bytes>`: the samples painted during the frame from the device's buffer, in hexadecimal) and the CD (`cd`: cd_win.c's state, `cdvolume` as its bits, the remap table, the drive's) and the mouse and joystick (`input`: the `I` line's fields by name); `quakesharp-tools trace` writes the same for QuakeSharp, to find the field behind a differing line |
| `-traceevents <file>` | Scripted input: lines of `<frame> <console text>`, added to the command buffer before that frame runs (`+forward`, `-forward`, `impulse 8`...), `<frame> @key <key number> <1 or 0>`, a key going down or up (`Key_Event`, as the window procedure posts it), `<frame> @mouse <dx> <dy>`, the mouse moved (the virtual cursor, then `IN_MouseEvent` as for `WM_MOUSEMOVE`), or `<frame> @buttons <state>`, the mouse buttons held (1 left, 2 right, 4 middle; `IN_MouseEvent`). Each line is taken before it is delivered, so a key that waits for the next (`SCR_ModalMessage`'s loop calls `Sys_SendKeyEvents`) gets the lines after it of the same frame: the answer must be in the frame of the key that asks |
| `-oraclevid <w> <h>` | Mode 0's size, the first windowed mode (320x200 to 1280x1024; 320x240 by default) |
| `-oraclestep <n>` | Frames of 1/n s |
| `-oracleverbose` | Print `Sys_Printf` (the console) to stdout |

## The trace format

One line `H 6 <width> <height> <frames per second>`, then for every frame, after `Host_Frame`:

| Line | Fields |
|---|---|
| `F <frame> <host_framecount> <realtime> <seed> <calls>` | The frame number (from 1), `host_framecount`, `realtime` as the hexadecimal bits of the double, the `rand()` state (`holdrand`) and the number of `rand()` calls so far |
| `C <hash>` | The client state: `cls` and `cl` (see `Client` in `src/trace.c` for the fields and their order) |
| `E <hash>` | The entities as the client keeps them: `cl_entities` up to `cl.num_entities`, the static entities, the view model, the visible list without the static entities |
| `D <hash>` | What the renderer writes into the entities (`visframe`, `dlightframe`, `dlightbits`, `trivial_accept`, `topnode`: a node's index, or `0x10000 +` a leaf's) of `cl_entities`, the statics, the view model and the temp entities, and the whole visible list (`R_StoreEfrags` adds the static entities to it while drawing) |
| `X <hash> <particles>` | The effects: temp entities, beams, dynamic lights, light styles and `d_lightstylevalue`, the active particles (and their number) |
| `S <hash>` | The sound: `paintedtime`, `soundtime`, `total_channels` and the channels (sound, volumes, end, position, entity and channel, origin, attenuation): what decides `S_StartSound`'s `rand()` draws |
| `T <hash>` | The console's text: `con_linewidth`, `con_totallines`, `con_current`, `con_x`, `con_times` and the whole `con_text` |
| `R <hash>` | The view: `r_refdef`'s rectangle, origin, angles and field of view |
| `V <frame> <palette>` | The mode (`vid_modenum`, `modestate`, `vid.width`, `vid.height`, `vid.rowbytes`) and the frame (the 8-bit buffer, row by row), then the palette last passed to `VID_SetPalette` |
| `G <hash>` | The server: `sv.active`, `paused`, `loadgame`, and with an active server `time`, `lastcheck`, `lastchecktime`, `name`, `modelname`, the world model, the model precaches and models, the sound precaches, the light styles, `num_edicts`, `max_edicts`, `state` and the `datagram`, `reliable_datagram` and `signon` buffers (overflowed, size, bytes); then `svs` (`maxclients`, `maxclientslimit`, `serverflags`, `changelevel_issued`) and every client slot below `maxclients` (see `Server` in `src/trace.c`) |
| `Q <hash>` | The QuakeC globals: the slots the progs' global definitions cover (not the compiler's temporaries, the return value or the parameters), strings by content |
| `N <hash> <edicts>` | The edicts up to `sv.num_edicts`: `free`, `num_leafs` and the leaf numbers, the baseline, `freetime`, and the slots the field definitions cover, strings by content |
| `W <hash>` | The area nodes' trigger and solid lists, as edict numbers in their order (-1 after each list) |
| `M <hash> <messages>` | The messages sent during the frame through `NET_SendMessage` and `NET_SendUnreliableMessage`: the sender (-1 the client, n the server to client n), reliable or not, the driver's result, the size and the bytes |
| `P <hash>` | The sound device's buffer, all of it (`shm->buffer`, 64 KB): the samples painted, `S_ClearBuffer`'s clears and what stays from the laps before; nothing without a started sound system |
| `A <hash>` | The CD: cd_win.c's `initialized`, `enabled`, `cdValid`, `playing`, `wasPlaying`, `playLooping`, `cdvolume`, `playTrack`, `maxTrack` and `remap`, then the virtual drive's state (open, door open, stopped, playing or paused, track) |
| `I <hash>` | The mouse and the joystick: in_win.c's state (`mouseactive`, `mouseinitialized`, `mouseactivatetoggle`, `mouseshowtoggle`, `mouseparmsvalid`, `restore_spi`, `dinput`, `dinput_acquired`, `mstate_di`, `mouse_buttons`, `mouse_oldbuttonstate`, `current_pos`, `mx_accum`, `my_accum`, `mouse_x`, `mouse_y`, `old_mouse_x`, `old_mouse_y`, `joy_avail`, `joy_advancedinit`, `joy_haspov`, `joy_id`, `joy_flags`, `joy_numbuttons`, `joy_oldbuttonstate`, `joy_oldpovstate`, `dwAxisMap`, `dwControlMap`), vid_win.c's `windowed_mouse`, then the virtual machine's (the cursor, the clip rectangle, the buttons, the display counter, the capture, the mouse parameters) |
| `Err <text>` | `Sys_Error`'s message, last |

The client's lines (`F`, `C`, `E`, `X`, `S`, `T`, `R` and the palette) do not depend on the renderer; `D` and the frame
are the renderer's; `G`, `Q`, `N` and `W` the server's (without an active server `G` hashes the flags and `svs`, the
others nothing), `M` both; `P` the mixer's and `A` the CD's (trace format 5, milestone 8), `I` the mouse's and the joystick's (trace format 6, milestone 10). `Trace-Demos.ps1` runs the oracle from its Quake folder with WinQuake's base directory "." (the
console shows `./id1/pak0.pak`), as QuakeSharp's traces run wherever the game data is.

Hashes are 64-bit FNV-1a over the fields' bytes: `int` and `float` as 4 little-endian bytes, `double` 8, vectors three
floats, strings their bytes and then 4 zero bytes, a model its name, a colormap 0 (the normal one), 1 + n (player n's
translation) or -1, an entity pointer its array and index (`cl_entities` index, `0x10000 +` static, `0x20000 +` temp,
`0x30000` the view model). A QuakeC string is hashed by its contents: its offset from `pr_strings` depends on the
oracle's addresses (design.md §11.1).
