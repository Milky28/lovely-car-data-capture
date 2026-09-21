<#
.SYNOPSIS
Replays every saved capture and lists what a change to the plugin changed.

.DESCRIPTION
Runs each <car>.frames.csv in the export folder through the test runner's --replay, taking the game
and car id from the export next to it, and saves each car's profile and report to -Out. With
-Baseline, compares against an earlier run and lists every car whose colours, light rpm or blink
interval changed. Replays don't apply local overrides, so an override hides nothing here.

Typical use: run it once before a change to make the baseline, then again after with -Baseline.

.EXAMPLE
./tools/replay-all.ps1 -Out review/replay/before
./tools/replay-all.ps1 -Out review/replay/after -Baseline review/replay/before
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Out,
    [string] $Baseline,
    [string] $Root = (Join-Path $env:USERPROFILE 'OneDrive\Documents\SimHub\LovelyCarDataCapture'),
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runner = Join-Path $repo 'tests/bin/Release/net48/LovelyCarDataCapture.Tests.exe'

if (-not $NoBuild) {
    & dotnet build (Join-Path $repo 'LovelyCarDataCapture.csproj') -c Release -v q -nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
    & dotnet build (Join-Path $repo 'tests/LovelyCarDataCapture.Tests.csproj') -c Release -v q -nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
}
if (-not (Test-Path -LiteralPath $runner)) { throw "No test runner at $runner. Build the tests first." }
New-Item -ItemType Directory -Path $Out -Force | Out-Null

$captures = Get-ChildItem -LiteralPath $Root -Directory | Where-Object { $_.Name -notin @('submit') } |
    ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Filter '*.frames.csv' -File }
$skipped = @()
foreach ($capture in $captures) {
    $car = $capture.Name -replace '\.frames\.csv$', ''
    $folder = $capture.Directory.Name
    $report = Join-Path $capture.DirectoryName "$car.report.txt"
    $export = Join-Path $capture.DirectoryName "$car.json"
    # The report names the game as the plugin knows it; the folder name is only its slug.
    $game = if (Test-Path -LiteralPath $report) {
        (Select-String -LiteralPath $report -Pattern '^Game:\s+(\S+)' | Select-Object -First 1).Matches.Groups[1].Value
    }
    if (-not $game) { $skipped += "$folder/$car (no report to take the game from)"; continue }
    $carId = if (Test-Path -LiteralPath $export) { (Get-Content -LiteralPath $export -Raw | ConvertFrom-Json).carId }
    if (-not $carId) { $carId = $car }

    $name = "$folder--$car"
    $text = & $runner --replay $capture.FullName --game $game --car $carId 2>&1 | Out-String
    Set-Content -LiteralPath (Join-Path $Out "$name.report.txt") -Value $text -Encoding utf8
    $start = $text.IndexOf("`n{")
    if ($start -lt 0) { $skipped += "$folder/$car (replay produced no profile)"; continue }
    Set-Content -LiteralPath (Join-Path $Out "$name.json") -Value $text.Substring($start + 1).Trim() -Encoding utf8
}
Write-Output "Replayed $(@($captures).Count - $skipped.Count) captures into $Out."
$skipped | ForEach-Object { Write-Output "  skipped $_" }

if (-not $Baseline) { return }

function Read-Profile([string] $path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }

$changed = 0
foreach ($after in Get-ChildItem -LiteralPath $Out -Filter '*.json' -File) {
    $beforePath = Join-Path $Baseline $after.Name
    $name = $after.BaseName
    if (-not (Test-Path -LiteralPath $beforePath)) { Write-Output "$name - new, no baseline"; $changed++; continue }
    $b = Read-Profile $beforePath
    $a = Read-Profile $after.FullName
    $lines = @()
    if ($b.ledNumber -ne $a.ledNumber) { $lines += "  ledNumber $($b.ledNumber) -> $($a.ledNumber)" }
    if ($b.redlineBlinkInterval -ne $a.redlineBlinkInterval) {
        $lines += "  redlineBlinkInterval $($b.redlineBlinkInterval) -> $($a.redlineBlinkInterval)"
    }
    if (($b.ledColor -join ',') -ne ($a.ledColor -join ',')) {
        for ($i = 0; $i -lt [Math]::Max($b.ledColor.Count, $a.ledColor.Count); $i++) {
            if ($b.ledColor[$i] -ne $a.ledColor[$i]) {
                $what = if ($i -eq 0) { 'redline' } else { "LED $i" }
                $lines += "  $what colour $($b.ledColor[$i]) -> $($a.ledColor[$i])"
            }
        }
    }
    $gears = @($b.ledRpm[0].PSObject.Properties.Name) + @($a.ledRpm[0].PSObject.Properties.Name) | Select-Object -Unique
    foreach ($gear in $gears) {
        $rb = @($b.ledRpm[0].$gear); $ra = @($a.ledRpm[0].$gear)
        $diffs = for ($i = 0; $i -lt [Math]::Max($rb.Count, $ra.Count); $i++) {
            if ($rb[$i] -ne $ra[$i]) { $what = if ($i -eq 0) { 'redline' } else { "LED $i" }; "$what $($rb[$i])->$($ra[$i])" }
        }
        if ($diffs) { $lines += "  gear $gear rpm: $($diffs -join ', ')" }
    }
    if ($lines) { Write-Output $name; $lines | Write-Output; $changed++ }
}
foreach ($before in Get-ChildItem -LiteralPath $Baseline -Filter '*.json' -File) {
    if (-not (Test-Path -LiteralPath (Join-Path $Out $before.Name))) { Write-Output "$($before.BaseName) - in the baseline, not replayed now"; $changed++ }
}
Write-Output $(if ($changed) { "$changed car(s) changed." } else { 'No car changed.' })
