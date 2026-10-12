<#
.SYNOPSIS
Builds the reference programs QuakeSharp is checked against (design.md §10) into a work folder outside the
repository.

.DESCRIPTION
numerics.exe: musl's libm and the C runtime's rand(), for the numerics tests' expected values.

Everything is built with clang-cl (LLVM, with Visual Studio's C++ tools for the Windows SDK and C runtime) for 32-bit
x86 with SSE2 arithmetic, so every float operation is rounded to single precision (FLT_EVAL_METHOD 0) as in
QuakeSharp, with no contraction into fused multiply-adds, and the C runtime linked statically.

.PARAMETER WorkDir
The folder to build in, outside the repository.
#>
param(
    [Parameter(Mandatory = $true)] [string] $WorkDir
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$musl = Join-Path $repo 'reference\musl\src\math'
$shim = Join-Path $PSScriptRoot 'musl'

$clang = (Get-Command clang-cl -ErrorAction SilentlyContinue).Source
if (-not $clang) {
    $clang = 'C:\Program Files\LLVM\bin\clang-cl.exe'
}
if (-not (Test-Path $clang)) {
    throw 'clang-cl not found: install LLVM (and Visual Studio with the C++ tools)'
}

New-Item -ItemType Directory -Force $WorkDir | Out-Null
$WorkDir = (Resolve-Path $WorkDir).Path
if (($WorkDir + '\').StartsWith($repo.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "$WorkDir is inside the repository; build the oracle elsewhere"
}

# 32-bit x86, SSE2 arithmetic, no FMA contraction, static C runtime, musl's names (musl_names.h) everywhere
$common = @(
    '--target=i686-pc-windows-msvc', '-O2', '-MT', '/clang:-mfpmath=sse', '/clang:-msse2', '/clang:-ffp-contract=off',
    '-FImusl_names.h', "-I$shim", '-nologo', '-Wno-everything'
)

function Compile([string] $source, [string] $objDir, [string[]] $extra = @()) {
    New-Item -ItemType Directory -Force $objDir | Out-Null
    $obj = Join-Path $objDir ([IO.Path]::GetFileNameWithoutExtension($source) + '.obj')
    & $clang @common @extra -c $source "-Fo$obj"
    if ($LASTEXITCODE -ne 0) { throw "compiling $source failed" }
    return $obj
}

function Link([string] $exe, [string[]] $objects) {
    & $clang --target=i686-pc-windows-msvc -fuse-ld=lld -MT @objects "-Fe$exe" -nologo
    if ($LASTEXITCODE -ne 0) { throw "linking $exe failed" }
    Write-Host "built $exe"
}

# musl's libm: the functions QuakeSharp ports and what they call
$muslSources = @(
    'sin.c', 'cos.c', 'tan.c', 'atan.c', 'atan2.c', 'pow.c', 'pow_data.c', 'exp_data.c', 'scalbn.c',
    '__sin.c', '__cos.c', '__tan.c', '__rem_pio2.c', '__rem_pio2_large.c',
    '__math_xflow.c', '__math_oflow.c', '__math_uflow.c', '__math_invalid.c'
)
$muslObjects = foreach ($s in $muslSources) {
    Compile (Join-Path $musl $s) (Join-Path $WorkDir 'obj\musl') @('/clang:-std=c11')
}

# numerics.exe
$numerics = Compile (Join-Path $PSScriptRoot 'numerics.c') (Join-Path $WorkDir 'obj\numerics') @('/clang:-std=c11')
Link (Join-Path $WorkDir 'numerics.exe') (@($numerics) + $muslObjects)
