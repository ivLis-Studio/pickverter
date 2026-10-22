#requires -Version 7
<#
  Everything CI runs, on a Windows machine:
    scripts/test.ps1                 # strings, unit tests, glyphs, Windows self test
    scripts/test.ps1 -Snapshots out  # …plus PNG renders of every surface into out\
#>
param(
    [string] $Snapshots,
    [string] $Languages = "en,ko,ja,de,ru,zh-Hans",
    [switch] $Screens
)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root
$env:PYTHONUTF8 = "1"
python scripts/check-i18n.py
if ($LASTEXITCODE) { throw "strings" }
dotnet build Pickverter.slnx -c Release
if ($LASTEXITCODE) { throw "build failed" }
dotnet test --project tests/Pickverter.Core.Tests/Pickverter.Core.Tests.csproj -c Release
if ($LASTEXITCODE) { throw "unit tests failed" }
$exe = Join-Path $Root "src\Pickverter\bin\Release\net10.0-windows10.0.19041.0\Pickverter.exe"
function Cli([string[]] $arguments) {
    & $exe cli @arguments | Out-Host
    if ($LASTEXITCODE) { throw "cli $($arguments -join ' ') failed ($LASTEXITCODE)" }
}
Cli @("glyphs")
Cli @("selftest", (Join-Path ([IO.Path]::GetTempPath()) "pickverter-selftest"))
if ($Snapshots) {
    Cli @("snapshot", $Snapshots, "--lang", $Languages, "--theme", "light,dark")
    if ($Screens) { Cli @("screenshot", (Join-Path $Snapshots "screens")) }
}
Write-Host "✓ all checks passed"

