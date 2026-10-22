#requires -Version 7
<#
  Builds Pickverter for Windows.
    scripts/build.ps1                     # Debug build of everything
    scripts/build.ps1 -Configuration Release
    scripts/build.ps1 -Run                # build and start the app
#>
param(
    [ValidateSet("Debug", "Release")] [string] $Configuration = "Debug",
    [switch] $Run
)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root
dotnet build Pickverter.slnx -c $Configuration
if ($LASTEXITCODE) { throw "build failed" }
$exe = Join-Path $Root "src\Pickverter\bin\$Configuration\net10.0-windows10.0.19041.0\Pickverter.exe"
Write-Host "✓ $exe"
if ($Run) { Start-Process $exe }

