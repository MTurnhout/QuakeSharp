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
                        cvars, the menu's volume sliders, a game's pause and level changes), at
                        320x240; commands.640x480.trace the same at 640x480, sound.nosound.trace the sound game with
                        -nosound -nocdaudio; saves: a save on every map, the save and load menus, the refusals
  loads.trace           every save saves.trace's run wrote, loaded in turn and played on
  record.trace          demos recorded (events/record.events), two screenshots, with -condebug's qconsole.log;
                        record.d1-d3.trace: each demo played back, record.timedemo.trace: d1 as a timedemo

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

# The oracle's own game folder, beside the executable
$Oracle = (Resolve-Path $Oracle).Path
$base = Join-Path (Split-Path $Oracle) 'quake'
$id1 = Join-Path $base 'id1'
New-Item -ItemType Directory -Force $id1 | Out-Null
foreach ($pak in 'pak0.pak', 'pak1.pak') {
    $source = Get-ChildItem (Join-Path $QuakeDir 'id1') | Where-Object { $_.Name -ieq $pak } | Select-Object -First 1
    if (-not $source) { throw "no id1/$pak in $QuakeDir" }
    $target = Join-Path $id1 $pak
    if (-not (Test-Path $target) -or (Get-FileHash $target).Hash -ne (Get-FileHash $source.FullName).Hash) {
        Copy-Item $source.FullName $target
    }
}

# A run of the oracle: from an id1 holding only the PAKs (and the files of earlier runs named in $inputs, as
# "<run>/<file>"), writing <name>.trace; what id1 holds afterwards, but the PAKs, is kept in <name>.files
function Trace([string] $name, [string[]] $arguments, [string[]] $inputs = @()) {
    if ($Only -and $Only -notcontains $name) { return }
    Get-ChildItem $id1 | Where-Object { $_.Name -notin 'pak0.pak', 'pak1.pak' } | Remove-Item -Recurse
    foreach ($earlier in $inputs) {
        Copy-Item (Join-Path $OutDir ($earlier -replace '^([^/]+)/', '$1.files/')) $id1
    }
    $trace = Join-Path $OutDir "$name.trace"
    # from the Quake folder with WinQuake's base directory ".": the console shows "./id1/pak0.pak", as QuakeSharp's
    # traces do wherever the game data is
    Push-Location $base
    try { & $Oracle -trace $trace @arguments | Out-Null } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "the oracle failed on $name" }
    $frames = (Select-String -Path $trace -Pattern '^F ' | Measure-Object).Count

    $files = Join-Path $OutDir "$name.files"
    if (Test-Path $files) { Remove-Item $files -Recurse }
    New-Item -ItemType Directory $files | Out-Null
    $written = @(Get-ChildItem $id1 | Where-Object { $_.Name -notin 'pak0.pak', 'pak1.pak' })
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
$games = [ordered]@{ 'start' = 2000; 'commands' = 3800; 'intermission' = 1900; 'finale' = 3100; 'netmenus' = 3000; 'tour' = 22200; 'e1' = 24500; 'sound' = 3500; 'saves' = 17705 }
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
