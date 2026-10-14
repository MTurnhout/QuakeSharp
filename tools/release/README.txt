QuakeSharp
==========

A faithful C# port of id Software's Quake (WinQuake 1.09), with an optional enhanced mode.
Source, documentation and licence: https://github.com/MTurnhout/QuakeSharp

Playing
-------

Run QuakeSharp (QuakeSharp.exe on Windows). It needs a graphics card with Vulkan 1.3
(on Linux, your distribution's Vulkan driver, such as mesa-vulkan-drivers). Keep the
files of this folder together: the libraries beside the executable are part of it.

- -window plays in a window instead of the whole screen.
- Scroll Lock switches between the faithful mode (WinQuake as it was) and the enhanced
  mode (a modern renderer, mouse look, controller support and more).
- Settings, saved games and screenshots go to %APPDATA%\QuakeSharp (~/.config/QuakeSharp).
- Multiplayer on Windows: when Windows asks, allow QuakeSharp through the firewall on
  private networks, on every PC that plays. Until then a connection is accepted and
  nothing more happens. Windows asks again for a copy in another folder.

The game's files
----------------

QuakeSharp looks for the game in this order:

1. id1/ in the folder you start it from (or -basedir <folder>): your own Quake.
2. id1/ beside the executable: QuakeSharp copied into a Quake folder.
3. quake106.zip beside the executable: the shareware episode, unpacked the first time
   into %APPDATA%\QuakeSharp\shareware (~/.config/QuakeSharp/shareware on Linux).
   Nothing else needs setting up.

For the full game, run QuakeSharp from your Quake folder (the one holding id1/pak0.pak
and id1/pak1.pak), or copy QuakeSharp's files there.

The shareware
-------------

quake106.zip is id Software's shareware Quake 1.06, exactly as id released it. It is
not QuakeSharp's and is not covered by QuakeSharp's licence: it is given away free of
charge, whole, under id's own licence (SLICNSE.TXT, inside it and unpacked beside the
game on first run), which you agree to by playing it. Quake is a trademark of id
Software. QuakeSharp is not affiliated with or endorsed by id Software.

QuakeSharp itself is free software under the GNU General Public License, version 2
(LICENSE).
