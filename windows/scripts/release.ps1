#requires -Version 7
<#
  Builds, packages and (with -Publish) uploads the Windows half of a GitHub release.
    scripts/release.ps1 -Version 0.2.0              # into dist\0.2.0
    scripts/release.ps1 -Version 0.2.0 -Publish     # …and attach to release v0.2.0 (created if missing)

  Assets: Pickverter-<v>-win-<arch>-setup.exe (people), Pickverter-<v>-win-<arch>.zip (portable + updater),
          update-windows.json (signed manifest), SHA256SUMS-windows.txt.
  Signing key for update-windows.json: PICKVERTER_UPDATE_KEY (base64) or %USERPROFILE%\.config\pickverter\update_ed25519.key.
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [switch] $Publish,
    [string[]] $Architectures = @("x64", "arm64"),
    [switch] $SkipInstaller
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.ZipFile -ErrorAction SilentlyContinue
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root
$Version = $Version.TrimStart("v")
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { throw "version must look like 1.2.3 or 1.2.3-beta.1" }
$Repo = "ivLis-Studio/pickverter"
$Tag = "v$Version"
$Out = Join-Path $Root "dist\$Version"
if (Test-Path $Out) { throw "$Out already exists — move it away first" }
New-Item -ItemType Directory $Out | Out-Null
$Build = [int](git rev-list --count HEAD)
$FileVersion = ($Version -split '-')[0] + "." + ($Build % 65535)
$NewLine = [Environment]::NewLine

$iscc = (Get-Command iscc -ErrorAction SilentlyContinue)?.Source
if (-not $iscc) { $iscc = Join-Path ([Environment]::GetFolderPath("ProgramFilesX86")) "Inno Setup 6\ISCC.exe" }
if (-not $SkipInstaller -and -not (Test-Path $iscc)) { throw "Inno Setup 6 (ISCC.exe) not found" }

$zips = [ordered]@{}
foreach ($arch in $Architectures) {
    Write-Host "▸ win-$arch"
    $publishDir = Join-Path $Out "publish-$arch\Pickverter"
    $properties = @("-p:PickverterRelease=true", "-p:Version=$Version", "-p:FileVersion=$FileVersion", "-p:InformationalVersion=$Version", "-p:DebugType=none")
    dotnet publish src/Pickverter/Pickverter.csproj -c Release -r "win-$arch" --self-contained true @properties -o $publishDir
    if ($LASTEXITCODE) { throw "publish win-$arch failed" }
    $zip = Join-Path $Out "Pickverter-$Version-win-$arch.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    $zips[$arch] = $zip
    if (-not $SkipInstaller) {
        & $iscc /Qp "/DVersion=$Version" "/DArch=$arch" "/DSource=$publishDir" "/O$Out" (Join-Path $Root "installer\Pickverter.iss")
        if ($LASTEXITCODE) { throw "installer win-$arch failed" }
    }
}

# Release notes: this version's section of CHANGELOG.md.
$notes = Join-Path $Out "notes.md"
$section = @()
$inside = $false
foreach ($line in Get-Content (Join-Path $Root "CHANGELOG.md") -Encoding utf8) {
    if ($line -match "^## \[$([regex]::Escape($Version))\]") { $inside = $true; continue }
    if ($inside -and $line -match '^## \[') { break }
    if ($inside) { $section += $line }
}
$text = ($section -join $NewLine).Trim()
if (-not $text) { $text = "Pickverter $Version for Windows" }
Set-Content $notes $text -Encoding utf8NoBOM

$cli = Join-Path $Out "publish-x64\Pickverter\Pickverter.exe"
$arguments = @("cli", "update-manifest", "--version", $Version, "--build", "$Build", "--notes", $notes,
    "--notes-url", "https://github.com/$Repo/releases/tag/$Tag", "--out", (Join-Path $Out "update-windows.json"))
foreach ($arch in $zips.Keys) {
    $name = Split-Path $zips[$arch] -Leaf
    $arguments += @("--zip-$arch", $zips[$arch], "--url-$arch", "https://github.com/$Repo/releases/download/$Tag/$name")
}
& $cli @arguments | Out-Host
if ($LASTEXITCODE) { throw "update-manifest failed" }
& $cli cli update-verify (Join-Path $Out "update-windows.json") @($zips.Values) | Out-Host
if ($LASTEXITCODE) { throw "update-verify failed" }

$assets = @(Get-ChildItem $Out -File | Where-Object { $_.Name -match '\.(zip|exe)$' })
$sums = $assets | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
Set-Content (Join-Path $Out "SHA256SUMS-windows.txt") ($sums -join $NewLine) -Encoding utf8NoBOM
$assets += Get-Item (Join-Path $Out "update-windows.json"), (Join-Path $Out "SHA256SUMS-windows.txt")
Write-Host "✓ $Out"
$assets | ForEach-Object { Write-Host ("  {0,-48} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)) }

if ($Publish) {
    $body = Join-Path $Out "release-body.md"
    $footer = Get-Content (Join-Path $Root "docs\release-footer.md") -Raw -Encoding utf8
    $windows = "<!-- windows -->" + $NewLine + "## Windows" + $NewLine + $NewLine + $text + $NewLine + $NewLine + $footer
    gh release view $Tag --repo $Repo *> $null
    if ($LASTEXITCODE -eq 0) {
        gh release upload $Tag @($assets.FullName) --repo $Repo --clobber
        if ($LASTEXITCODE) { throw "upload failed" }
        $existing = (gh release view $Tag --repo $Repo --json body --jq .body) -join $NewLine
        if (-not ($existing -match '<!-- windows -->')) {
            Set-Content $body ($existing.TrimEnd() + $NewLine + $NewLine + $windows) -Encoding utf8NoBOM
            gh release edit $Tag --repo $Repo --notes-file $body
        }
    }
    else {
        Set-Content $body $windows -Encoding utf8NoBOM
        $flags = @()
        if ($Version -match '-') { $flags += "--prerelease" }
        gh release create $Tag @($assets.FullName) --repo $Repo --title "Pickverter $Version" --notes-file $body @flags
        if ($LASTEXITCODE) { throw "release failed" }
    }
    Write-Host "✓ published $Tag"
}

