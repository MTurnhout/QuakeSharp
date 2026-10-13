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

function Link([string] $exe, [string[]] $objects, [string[]] $options = @()) {
    # 4217: cd_win.c, in_win.c and the network drivers import functions from winmm, user32, kernel32 and wsock32 that
    # src/cd_oracle.c, src/in_oracle.c and src/winsock_oracle.c define instead, as intended
    & $clang --target=i686-pc-windows-msvc -fuse-ld=lld -MT @objects "-Fe$exe" -nologo /link /ignore:4217 @options
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

# quake-oracle.exe: WinQuake 1.09's C with the oracle's platform layer (src/) instead of the Windows drivers. The
# sources are copied into the work folder and quake-oracle.patch applied: with ORACLE defined, quakedef.h and
# quakeasm.h leave __i386__ undefined, so id386 is 0 (the C code, never the assembly). The network drivers are
# net_win.c's, their Winsock src/winsock_oracle.c's virtual machine instead of wsock32 (net_wins.c and net_wipx.c load
# it with LoadLibrary and GetProcAddress, renamed for the three files so the rest of the program keeps kernel32's);
# cd_win.c's MCI commands go to src/cd_oracle.c's virtual drive instead of winmm, and in_win.c's mouse and joystick
# calls to src/in_oracle.c's virtual screen without a joystick instead of user32 and winmm (DirectInput's headers as
# DirectX 3's, which in_win.c was written for).
$quake = Join-Path $WorkDir 'quake-src'
if (Test-Path $quake) { Remove-Item -Recurse -Force $quake }
Copy-Item -Recurse (Join-Path $repo 'reference\Quake\WinQuake') $quake
git -C $quake apply --ignore-whitespace (Join-Path $PSScriptRoot 'quake-oracle.patch')
if ($LASTEXITCODE -ne 0) { throw 'applying quake-oracle.patch failed' }
$oracleSrc = Join-Path $PSScriptRoot 'src'
$quakeFlags = @(
    '/clang:-std=gnu89', '/clang:-fcommon', '-DORACLE', '-U__i386__', '-D_CRT_SECURE_NO_WARNINGS', '-D_CRT_NONSTDC_NO_WARNINGS',
    '-DDIRECTINPUT_VERSION=0x0300',
    "-I$oracleSrc", "-I$quake", "-I$(Join-Path $quake 'scitech\include')", '-FIoracle.h'
)
$quakeSources = @(
    'cl_demo.c', 'cl_input.c', 'cl_main.c', 'cl_parse.c', 'cl_tent.c', 'chase.c', 'cmd.c', 'common.c', 'console.c',
    'crc.c', 'cvar.c', 'draw.c', 'd_edge.c', 'd_fill.c', 'd_init.c', 'd_modech.c', 'd_part.c', 'd_polyse.c', 'd_scan.c',
    'd_sky.c', 'd_sprite.c', 'd_surf.c', 'd_vars.c', 'd_zpoint.c', 'host.c', 'host_cmd.c', 'keys.c', 'menu.c',
    'mathlib.c', 'model.c', 'net_dgrm.c', 'net_loop.c', 'net_main.c', 'net_vcr.c', 'nonintel.c', 'pr_cmds.c',
    'pr_edict.c', 'pr_exec.c', 'r_aclip.c', 'r_alias.c', 'r_bsp.c', 'r_light.c', 'r_draw.c', 'r_efrag.c', 'r_edge.c',
    'r_misc.c', 'r_main.c', 'r_sky.c', 'r_sprite.c', 'r_surf.c', 'r_part.c', 'r_vars.c', 'screen.c', 'sbar.c',
    'sv_main.c', 'sv_phys.c', 'sv_move.c', 'sv_user.c', 'zone.c', 'view.c', 'wad.c', 'world.c',
    'snd_dma.c', 'snd_mem.c', 'snd_mix.c', 'in_win.c', 'cd_win.c'
)
$quakeObjects = foreach ($s in $quakeSources) {
    Compile (Join-Path $quake $s) (Join-Path $WorkDir 'obj\quake') $quakeFlags
}
$quakeObjects += foreach ($s in 'net_win.c', 'net_wins.c', 'net_wipx.c') {
    # net_errno.h: their local named errno
    Compile (Join-Path $quake $s) (Join-Path $WorkDir 'obj\quake') ($quakeFlags + '-FInet_errno.h' +
        '-DLoadLibraryA=Oracle_LoadLibraryA' + '-DGetProcAddress=Oracle_GetProcAddress')
}
$quakeObjects += foreach ($s in (Get-ChildItem (Join-Path $oracleSrc '*.c'))) {
    # the oracle's own files must declare what they call: a float argument to an undeclared function is passed as a double
    Compile $s.FullName (Join-Path $WorkDir 'obj\oracle') ($quakeFlags + '/clang:-Werror=implicit-function-declaration')
}
# no wsock32.lib: what the network code takes from it (net_dgrm.c's inet_addr and inet_ntoa too) is the virtual
# machine's; dxguid.lib: the DirectInput GUIDs in in_win.c's mouse data format. /include: lld drops a function that
# only its synthesized import (4217) refers to, so the renamed LoadLibraryA and GetProcAddress are kept by name
Link (Join-Path $WorkDir 'quake-oracle.exe') ($quakeObjects + $muslObjects + 'dxguid.lib') @(
    '/include:_Oracle_LoadLibraryA@4', '/include:_Oracle_GetProcAddress@8')
