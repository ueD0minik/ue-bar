# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Dominik Poschinger
# Tests with short output (P3-1): a summary per test project and only the failed tests with their messages.
#   powershell -File app/build/test.ps1                  fast: without the categories Slow and Desktop
#   powershell -File app/build/test.ps1 -Full            everything (before every commit, tag and push)
#   powershell -File app/build/test.ps1 -Desktop         only the Desktop tests (real windows, real clipboard)
#   ... -Project plugins/Clipboard/tests                 only one test project (fast, or with -Full)
#   ... -Filter "FullyQualifiedName~Pin"                 an own filter on top
#   ... -Detailed                                         every test with its result (long)
#   ... -Release                                          Release build (as the CI)
#   ... -NoBuild                                          without building first (right after a build)
param([switch]$Full, [switch]$Desktop, [switch]$Detailed, [switch]$Release, [switch]$NoBuild, [string]$Project, [string]$Filter)
$ErrorActionPreference = "Stop"
$app = Split-Path -Parent $PSScriptRoot
$target = if ($Project) { Join-Path $app $Project } else { Join-Path $app "UeBar.sln" }
$config = if ($Release) { "Release" } else { "Debug" }

$filters = @()
if ($Desktop) { $filters += "Category=Desktop" }
elseif (-not $Full) { $filters += "Category!=Slow&Category!=Desktop" }
if ($Filter) { $filters += "($Filter)" }
$arguments = @("test", $target, "-c", $config, "--nologo", "-v", "q")
if ($filters.Count -gt 0) { $arguments += @("--filter", ($filters -join "&")) }
if ($Detailed) { $arguments += @("--logger", "console;verbosity=normal") }
if ($NoBuild) { $arguments += "--no-build" }

$kind = if ($Desktop) { "Desktop" } elseif ($Full) { "full" } else { "fast" }
Write-Host "Tests ($kind, $config): $(if ($Project) { $Project } else { 'all projects' })" -ForegroundColor Cyan
$clock = [Diagnostics.Stopwatch]::StartNew()
$output = & dotnet @arguments 2>&1
$code = $LASTEXITCODE
foreach ($line in $output) {
    $text = "$line"
    if ($Detailed) { Write-Host $text; continue }
    # Summaries, build errors and the details of failed tests – nothing about passed tests.
    if ($text -match "(Bestanden!|Passed!)") { Write-Host $text.Trim() -ForegroundColor Green }
    elseif ($text -match "(Fehler!|Failed!)") { Write-Host $text.Trim() -ForegroundColor Red }
    elseif ($text -match "error [A-Z]+\d+|\[FAIL\]|Fehlermeldung|Error Message|Assert\.|Expected|Actual|Erwartet|Tatsächlich|Stapelverfolgung|Stack Trace|^\s+at |abgebrochen|aborted|crashed|abgestürzt") { Write-Host $text }
}
Write-Host ("Duration: {0:N1} s" -f $clock.Elapsed.TotalSeconds)
exit $code
