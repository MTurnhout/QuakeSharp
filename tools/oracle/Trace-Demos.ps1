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
    $trace = Join-Path $OutDir "$name.trace"
    & $Oracle -basedir $base -trace $trace @arguments | Out-Null
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
