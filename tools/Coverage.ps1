<#
.SYNOPSIS
Measures what the fidelity tests reach of QuakeSharp.Core (design.md §11.4 step 2, decision 46).

.DESCRIPTION
Runs QuakeSharp.Fidelity.Tests with coverlet (the runner's --coverlet option) over QuakeSharp.Core, its Diagnostics
(the tests' own trace writer) left out, and writes into -OutDir the cobertura report and coverage.md: line coverage per
source file, the methods never entered, and the lines left unreached in the others. Needs the game data and the traces
(QUAKESHARP_QUAKE_DIR, QUAKESHARP_TRACE_DIR); without them the tests skip and nothing is reached.

.PARAMETER OutDir
The folder for the reports, outside the repository.
#>
param(
    [Parameter(Mandatory = $true)] [string] $OutDir
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
if (($OutDir + '\').StartsWith($repo.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "$OutDir is inside the repository; the reports stay outside it"
}
if (-not $env:QUAKESHARP_QUAKE_DIR -or -not $env:QUAKESHARP_TRACE_DIR) {
    throw 'QUAKESHARP_QUAKE_DIR and QUAKESHARP_TRACE_DIR are needed: without them the fidelity tests skip'
}

Get-ChildItem $OutDir -Filter 'coverage.cobertura*.xml' | Remove-Item
& dotnet test --project (Join-Path $repo 'tests\QuakeSharp.Fidelity.Tests') -c Release --results-directory $OutDir `
    --coverlet --coverlet-include '[QuakeSharp.Core]*' --coverlet-exclude '[QuakeSharp.Core]QuakeSharp.Core.Diagnostics.*' `
    --coverlet-single-hit --coverlet-output-format cobertura
if ($LASTEXITCODE -ne 0) { throw 'the fidelity tests failed' }
$cobertura = Get-ChildItem $OutDir -Filter 'coverage.cobertura*.xml' | Select-Object -First 1
if (-not $cobertura) { throw "no cobertura report in $OutDir" }

# by source file: each method's lines, and those never reached
[xml] $x = Get-Content $cobertura.FullName -Raw
$files = @{}
foreach ($class in $x.coverage.packages.package.classes.class) {
    $file = $class.filename -replace '^.*QuakeSharp\.Core[\\/]', '' -replace '\\', '/'
    if (-not $files[$file]) { $files[$file] = [System.Collections.Generic.List[object]]::new() }
    foreach ($m in $class.methods.method) {
        $lines = @($m.lines.line | Where-Object { $_ })
        if ($lines.Count -eq 0) { continue }
        $files[$file].Add([pscustomobject]@{
            Name = $m.name
            Total = $lines.Count
            Missed = @($lines | Where-Object { $_.hits -eq '0' } | ForEach-Object { [int] $_.number })
            First = [int] $lines[0].number
        })
    }
}

# 3,4,5,9 -> "3-5,9"
function Ranges([int[]] $numbers) {
    $ranges = @(); $start = $null; $previous = $null
    foreach ($n in ($numbers | Sort-Object)) {
        if ($null -eq $start) { $start = $n }
        elseif ($n -ne $previous + 1) { $ranges += $(if ($start -eq $previous) { "$start" } else { "$start-$previous" }); $start = $n }
        $previous = $n
    }
    if ($null -ne $start) { $ranges += $(if ($start -eq $previous) { "$start" } else { "$start-$previous" }) }
    $ranges -join ','
}

$report = [System.Text.StringBuilder]::new()
[void] $report.AppendLine("# What the fidelity tests reach of QuakeSharp.Core`n")
[void] $report.AppendLine("Lines $($x.coverage.'lines-covered') of $($x.coverage.'lines-valid') ($([Math]::Round(100 * [double] $x.coverage.'line-rate', 1))%), branches $([Math]::Round(100 * [double] $x.coverage.'branch-rate', 1))%.`n")
[void] $report.AppendLine("| File | Lines | Unreached | Reached % |`n|---|---|---|---|")
foreach ($file in ($files.Keys | Sort-Object)) {
    $total = ($files[$file] | Measure-Object Total -Sum).Sum
    $missed = ($files[$file] | ForEach-Object { $_.Missed.Count } | Measure-Object -Sum).Sum
    [void] $report.AppendLine("| $file | $total | $missed | $(if ($total) { [Math]::Round(100 * ($total - $missed) / $total, 1) } else { 100 }) |")
}
foreach ($file in ($files.Keys | Sort-Object)) {
    $never = @($files[$file] | Where-Object { $_.Missed.Count -eq $_.Total } | Sort-Object First)
    $partly = @($files[$file] | Where-Object { $_.Missed.Count -gt 0 -and $_.Missed.Count -lt $_.Total } | Sort-Object First)
    if (-not $never -and -not $partly) { continue }
    [void] $report.AppendLine("`n## $file`n")
    if ($never) { [void] $report.AppendLine('Never entered: ' + (($never | ForEach-Object { "$($_.Name) ($($_.First))" }) -join ', ') + "`n") }
    foreach ($m in $partly) { [void] $report.AppendLine("- $($m.Name): lines $(Ranges $m.Missed) ($($m.Missed.Count) of $($m.Total))") }
}
$md = Join-Path $OutDir 'coverage.md'
[IO.File]::WriteAllText($md, $report.ToString())
Write-Host "$md`: lines $($x.coverage.'lines-covered') of $($x.coverage.'lines-valid') reached"
