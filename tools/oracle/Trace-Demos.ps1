<#
.SYNOPSIS
Writes the reference traces of the demos in the game data (design.md §10, tools/oracle/README.md).

.DESCRIPTION
Each trace starts from a clean game folder: the oracle's id1 holds only the two PAKs (copied from -QuakeDir), and
sometimes files an earlier run wrote. What a run leaves in id1 besides the PAKs (config.cfg, which the oracle writes
when it quits, savegames, demos, screenshots, qconsole.log) is kept in <name>.files beside its trace.

  <demo>.trace          demo1-3 played with +playdemo, at 320x240 (WinQuake's default windowed mode)
  <demo>.320x200.trace  the same at 320x200 (DOS Quake's mode 0)
  <demo>.640x480.trace  the same at 640x480
  attract.trace         the attract loop (no command line), for -AttractFrames frames
  menus.trace           the attract loop with the scripted input of events/menus.events (menus, console, scores,
                        renderer cvars), at 320x240; menus.640x480.trace the same at 640x480
  attract.config.trace  600 frames of the attract loop with the config.cfg menus.trace's run wrote
  <game>.trace          the scripted games of events/<game>.events (start, commands, intermission, finale, netmenus:
                        a new game from the menu, the server's commands and fights, the end of a level and of an
                        episode, the network menus starting a deathmatch; tour: every map in turn, fighting; e1: a game played by hand
                        and recorded with -recordevents, e1m1 into e1m2; sound: the sound and CD commands and
                        cvars, the menu's volume sliders, a game's pause and level changes; mouse: the mouse taken and released, looking,
                        moving, its buttons and cvars, the joystick's cvars; video: every mode, from the console and the Video menu, with a
                        save, a demo and a screenshot across the changes), at
                        320x240; commands.640x480.trace the same at 640x480, sound.nosound.trace the sound game with
                        -nosound -nocdaudio; saves: a save on every map, the save and load menus, the refusals
  loads.trace           every save saves.trace's run wrote, loaded in turn and played on
  record.trace          demos recorded (events/record.events), two screenshots, with -condebug's qconsole.log;
                        record.d1-d3.trace: each demo played back, record.timedemo.trace: d1 as a timedemo

With the mission packs' hipnotic/pak0.pak and rogue/pak0.pak in -QuakeDir (only those PAKs are copied, never the
packs' loose config.cfg), run with -hipnotic and -rogue in their own game folders, whose files are kept:
  hipdemo1-4.trace, roguedemo1-3.trace    the packs' demos
  hipattract.trace, rogueattract.trace    their attract loops (their own quake.rc), once around
  hiptour.trace, roguetour.trace          every map of the pack: a noclip route to its items, then the pack's weapons
                                          fired, and a save named after the map
  hiploads.trace, rogueloads.trace        each of those saves loaded in a fresh run and played on, then the load menu
  hipmenus.trace, roguemenus.trace        a new game, the game options' teamplay, every episode and level, a game
                                          started from them (rogue: capture the flag, one flag, three teams, tag)
  hipend.trace, rogueend.trace            each pack's end: Armagon killed, the time machine destroyed; the finale

.PARAMETER Oracle
quake-oracle.exe, as Build-Oracle.ps1 builds it.

.PARAMETER QuakeDir
A Quake folder with the original id1/pak0.pak and id1/pak1.pak.

.PARAMETER OutDir
The folder for the traces, outside the repository.

.PARAMETER Only
Writes only the traces of these names (e.g. menus, commands.640x480); a run that reads the files of an earlier one
needs that one's files in -OutDir.
#>
param(
    [Parameter(Mandatory = $true)] [string] $Oracle,
    [Parameter(Mandatory = $true)] [string] $QuakeDir,
    [Parameter(Mandatory = $true)] [string] $OutDir,
    [int] $AttractFrames = 20000,
    [string[]] $Only = @()
)

$ErrorActionPreference = 'Stop'
# -Only a,b from powershell -File arrives as one string
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
if (($OutDir + '\').StartsWith($repo.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "$OutDir is inside the repository; traces are derived from game data and stay outside it"
}

# The oracle's own game folder, beside the executable: id1's two PAKs, and the mission packs' (design.md decision 45:
# only their pak0.pak, never the installation's loose config.cfg)
$Oracle = (Resolve-Path $Oracle).Path
$base = Join-Path (Split-Path $Oracle) 'quake'
function CopyPak([string] $game, [string] $pak) {
    $dir = Get-ChildItem $QuakeDir -Directory | Where-Object { $_.Name -ieq $game } | Select-Object -First 1
    $source = if ($dir) { Get-ChildItem $dir.FullName | Where-Object { $_.Name -ieq $pak } | Select-Object -First 1 }
    if (-not $source) { return $false }
    New-Item -ItemType Directory -Force (Join-Path $base $game) | Out-Null
    $target = Join-Path (Join-Path $base $game) $pak
    if (-not (Test-Path $target) -or (Get-FileHash $target).Hash -ne (Get-FileHash $source.FullName).Hash) {
        Copy-Item $source.FullName $target
    }
    return $true
}
foreach ($pak in 'pak0.pak', 'pak1.pak') {
    if (-not (CopyPak 'id1' $pak)) { throw "no id1/$pak in $QuakeDir" }
}
$packs = @('hipnotic', 'rogue' | Where-Object { CopyPak $_ 'pak0.pak' })
$gameDirs = @('id1') + $packs

# A run of the oracle: from game folders holding only the PAKs (and the files of earlier runs named in $inputs, as
# "<run>/<file>", in the run's game folder $game), writing <name>.trace; what the run's game folder holds afterwards,
# but the PAKs, is kept in <name>.files
function Trace([string] $name, [string[]] $arguments, [string[]] $inputs = @(), [string] $game = 'id1') {
    if ($Only -and $Only -notcontains $name) { return }
    foreach ($g in $gameDirs) {
        Get-ChildItem (Join-Path $base $g) | Where-Object { $_.Name -notin 'pak0.pak', 'pak1.pak' } | Remove-Item -Recurse
    }
    $gamedir = Join-Path $base $game
    foreach ($earlier in $inputs) {
        Copy-Item (Join-Path $OutDir ($earlier -replace '^([^/]+)/', '$1.files/')) $gamedir
    }
    $trace = Join-Path $OutDir "$name.trace"
    # from the Quake folder with WinQuake's base directory ".": the console shows "./id1/pak0.pak", as QuakeSharp's
    # traces do wherever the game data is; in a window, mode 0 (design.md decision 50)
    Push-Location $base
    try { & $Oracle -startwindowed -noautostretch -trace $trace @arguments | Out-Null } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "the oracle failed on $name" }
    $frames = (Select-String -Path $trace -Pattern '^F ' | Measure-Object).Count

    $files = Join-Path $OutDir "$name.files"
    if (Test-Path $files) { Remove-Item $files -Recurse }
    New-Item -ItemType Directory $files | Out-Null
    $written = @(Get-ChildItem $gamedir | Where-Object { $_.Name -notin 'pak0.pak', 'pak1.pak' })
    $written | Copy-Item -Destination $files -Recurse
    Write-Host "$name.trace: $frames frames; files: $(($written | ForEach-Object Name) -join ', ')"
}

foreach ($demo in 'demo1', 'demo2', 'demo3') {
    Trace $demo @('-tracequitafterdemo', '+playdemo', $demo)
    Trace "$demo.320x200" @('-oraclevid', '320', '200', '-tracequitafterdemo', '+playdemo', $demo)
    Trace "$demo.640x480" @('-oraclevid', '640', '480', '-tracequitafterdemo', '+playdemo', $demo)
}
Trace 'attract' @('-oracleframes', "$AttractFrames")

# the menus, the console and the renderer's cvars, played over the attract loop
$events = Join-Path $PSScriptRoot 'events\menus.events'
Trace 'menus' @('-traceevents', $events, '-oracleframes', '3700')
Trace 'menus.640x480' @('-oraclevid', '640', '480', '-traceevents', $events, '-oracleframes', '3700')
# the config.cfg the menus wrote (other bindings and cvars), read back by quake.rc over the attract loop
Trace 'attract.config' @('-oracleframes', '600') @('menus/config.cfg')

# the scripted games (design.md §11.1): the frames each runs for are in GameTests too
$games = [ordered]@{ 'start' = 2000; 'commands' = 3800; 'intermission' = 1900; 'finale' = 3100; 'netmenus' = 3000; 'tour' = 22200; 'e1' = 24500; 'sound' = 3500; 'saves' = 17705; 'mouse' = 1600; 'video' = 2500 }
foreach ($game in $games.Keys) {
    $events = Join-Path $PSScriptRoot "events\$game.events"
    Trace $game @('-traceevents', $events, '-oracleframes', "$($games[$game])")
}
$events = Join-Path $PSScriptRoot 'events\commands.events'
Trace 'commands.640x480' @('-oraclevid', '640', '480', '-traceevents', $events, '-oracleframes', '3800')
# the sound game without a sound device or a CD (WinQuake's -nosound and -nocdaudio)
$events = Join-Path $PSScriptRoot 'events\sound.events'
Trace 'sound.nosound' @('-nosound', '-nocdaudio', '-traceevents', $events, '-oracleframes', '3500')
# demos recorded (through a level change and an intermission, with a forced CD track, cut by the end of the run),
# screenshots and qconsole.log; then each demo played back in a fresh run, and once as a timedemo
$events = Join-Path $PSScriptRoot 'events\record.events'
Trace 'record' @('-condebug', '-traceevents', $events, '-oracleframes', '2700')
foreach ($demo in 'd1', 'd2', 'd3') {
    Trace "record.$demo" @('-tracequitafterdemo', '+playdemo', $demo) @("record/$demo.dem")
}
Trace 'record.timedemo' @('-tracequitafterdemo', '+timedemo', 'd1') @('record/d1.dem')
# the savegames the saves game wrote, each loaded in a fresh run and played on (design.md §11.3)
$events = Join-Path $PSScriptRoot 'events\loads.events'
if (-not $Only -or $Only -contains 'loads') {
    $saves = @(Get-ChildItem (Join-Path $OutDir 'saves.files') -Filter '*.sav' | ForEach-Object { "saves/$($_.Name)" })
    Trace 'loads' @('-traceevents', $events, '-oracleframes', '12905') $saves
}

# the mission packs (design.md §11.4, decision 45), in their own game folders: their demos and attract loops; the
# tours of every map with the packs' weapons and items, saving on each, and those saves loaded in a fresh run; the
# menus' episodes and levels and the games they start (rogue's team modes); each pack's end
$prefixes = @{ 'hipnotic' = 'hip'; 'rogue' = 'rogue' }
$demos = @{ 'hipnotic' = @('hipdemo1', 'hipdemo2', 'hipdemo3', 'hipdemo4'); 'rogue' = @('demo1', 'demo2', 'demo3') }
$attract = @{ 'hipnotic' = 14000; 'rogue' = 17000 }
$packGames = @{
    'hipnotic' = [ordered]@{ 'tour' = 18000; 'menus' = 2344; 'end' = 9000; 'loads' = 5700 }
    'rogue' = [ordered]@{ 'tour' = 17000; 'menus' = 3843; 'end' = 7000; 'loads' = 5400 }
}
foreach ($pack in $packs) {
    $p = $prefixes[$pack]
    foreach ($demo in $demos[$pack]) {
        Trace "$p$($demo -replace '^hip', '')" @("-$pack", '-tracequitafterdemo', '+playdemo', $demo) @() $pack
    }
    Trace "${p}attract" @("-$pack", '-oracleframes', "$($attract[$pack])") @() $pack
    foreach ($game in $packGames[$pack].Keys) {
        $name = "$p$game"
        if ($Only -and $Only -notcontains $name) { continue }
        $events = Join-Path $PSScriptRoot "events\$name.events"
        $inputs = @()
        if ($game -eq 'loads') {
            $inputs = @(Get-ChildItem (Join-Path $OutDir "${p}tour.files") -Filter '*.sav' | ForEach-Object { "${p}tour/$($_.Name)" })
        }
        Trace $name @("-$pack", '-traceevents', $events, '-oracleframes', "$($packGames[$pack][$game])") $inputs $pack
    }
}
