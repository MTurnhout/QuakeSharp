<#
.SYNOPSIS
Writes the reference traces of the demos in the game data (design.md §10, tools/oracle/README.md).

.DESCRIPTION
Each trace starts from a clean game folder: the oracle's Quake folder holds only the two PAKs (copied from
-QuakeDir), and config.cfg, which the oracle writes when it quits, is removed before every run.

  <demo>.trace          demo1-3 played with +playdemo, at 320x240 (WinQuake's default windowed mode)
  <demo>.320x200.trace  the same at 320x200 (DOS Quake's mode 0)
  <demo>.640x480.trace  the same at 640x480
  attract.trace         the attract loop (no command line), for -AttractFrames frames
  attract.config.cfg    the config.cfg the oracle writes when it quits after the attract loop
  menus.trace           the attract loop with the scripted input of events/menus.events (menus, console, scores,
                        renderer cvars), at 320x240; menus.640x480.trace the same at 640x480
  <game>.trace          the scripted games of events/<game>.events (start, commands, intermission, finale, netmenus:
                        a new game from the menu, the server's commands and fights, the end of a level and of an
                        episode, the network menus starting a deathmatch), at
                        320x240; commands.640x480.trace the same at 640x480. Savegames the games write are removed
                        before each run, as config.cfg is

.PARAMETER Oracle
quake-oracle.exe, as Build-Oracle.ps1 builds it.

.PARAMETER QuakeDir
A Quake folder with the original id1/pak0.pak and id1/pak1.pak.

.PARAMETER OutDir
The folder for the traces, outside the repository.
#>
param(
    [Parameter(Mandatory = $true)] [string] $Oracle,
    [Parameter(Mandatory = $true)] [string] $QuakeDir,
    [Parameter(Mandatory = $true)] [string] $OutDir,
    [int] $AttractFrames = 20000
)

$ErrorActionPreference = 'Stop'
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

function Trace([string] $name, [string[]] $arguments) {
    Remove-Item (Join-Path $id1 'config.cfg') -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $id1 '*.sav') -ErrorAction SilentlyContinue
    $trace = Join-Path $OutDir "$name.trace"
    # from the Quake folder with WinQuake's base directory ".": the console shows "./id1/pak0.pak", as QuakeSharp's
    # traces do wherever the game data is
    Push-Location $base
    try { & $Oracle -trace $trace @arguments | Out-Null } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "the oracle failed on $name" }
    $frames = (Select-String -Path $trace -Pattern '^F ' | Measure-Object).Count
    Write-Host "$name.trace: $frames frames"
}

foreach ($demo in 'demo1', 'demo2', 'demo3') {
    Trace $demo @('-tracequitafterdemo', '+playdemo', $demo)
    Trace "$demo.320x200" @('-oraclevid', '320', '200', '-tracequitafterdemo', '+playdemo', $demo)
    Trace "$demo.640x480" @('-oraclevid', '640', '480', '-tracequitafterdemo', '+playdemo', $demo)
}
Trace 'attract' @('-oracleframes', "$AttractFrames")

# the config.cfg the oracle wrote when it quit the attract loop: the bindings and archived cvars after quake.rc
Copy-Item (Join-Path $id1 'config.cfg') (Join-Path $OutDir 'attract.config.cfg')
Write-Host 'attract.config.cfg'

# the menus, the console and the renderer's cvars, played over the attract loop
$events = Join-Path $PSScriptRoot 'events\menus.events'
Trace 'menus' @('-traceevents', $events, '-oracleframes', '3700')
Trace 'menus.640x480' @('-oraclevid', '640', '480', '-traceevents', $events, '-oracleframes', '3700')

# the scripted games (design.md §11.1): the frames each runs for are in GameTests too
$games = [ordered]@{ 'start' = 2000; 'commands' = 3800; 'intermission' = 1900; 'finale' = 3100; 'netmenus' = 3000 }
foreach ($game in $games.Keys) {
    $events = Join-Path $PSScriptRoot "events\$game.events"
    Trace $game @('-traceevents', $events, '-oracleframes', "$($games[$game])")
}
$events = Join-Path $PSScriptRoot 'events\commands.events'
Trace 'commands.640x480' @('-oraclevid', '640', '480', '-traceevents', $events, '-oracleframes', '3800')
