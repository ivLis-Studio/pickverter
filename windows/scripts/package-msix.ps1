#requires -Version 7
<#
  Builds the Microsoft Store (MSIX) edition of Pickverter: one package per architecture and the bundle Partner Center takes.
    scripts/package-msix.ps1 -Version 0.3.1                                  # x64 and Arm64 into dist\msix
    scripts/package-msix.ps1 -Version 0.3.1 -Arch x64 -TestCertificate       # …plus test-signed copies to sideload

  The same build as the GitHub edition (scripts/release.ps1's publish settings): Pickverter.exe recognises the Store edition
  at run time from its package identity. Each package holds Pickverter\<the app>, Assets\<packaging\Assets PNGs>,
  AppxManifest.xml (from packaging\AppxManifest.xml) and resources.pri (makepri, so scale-, targetsize- and altform- assets
  resolve).

  Output, in -OutDir (default dist\msix):
    Pickverter-<version>.msixbundle      unsigned: the Store upload (the Store signs it)
    Pickverter-<version>-<arch>.msix     unsigned, one per architecture
    test-signed\                         -TestCertificate: the bundle and packages again, signed with a throwaway self-signed
                                         certificate whose subject is the Store publisher, and Pickverter-<version>-test.cer
                                         to trust before sideloading them (a Windows VM, never the Store)
    work-<version>\                      publish output, package layouts and the resources.pri dumps

  Needs Windows with the .NET 10 SDK and the Windows SDK (makeappx, makepri and signtool from its newest bin folder).
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [ValidateSet("x64", "arm64")] [string[]] $Arch = @("x64", "arm64"),
    [switch] $TestCertificate,
    [string] $OutDir = "dist\msix"
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# The Partner Center product (also in packaging\AppxManifest.xml).
$IdentityName = "ivLis-STUDIO.Pickverter"
$Publisher = "CN=0C33D71C-3E83-4FF3-A9B9-CA2EA49A5C77"
$FoundationNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10"
$UapNamespace = "http://schemas.microsoft.com/appx/manifest/uap/windows10"

function Invoke-Tool([string] $Tool, [string[]] $Arguments, [string] $Secret = "") {
    $shown = $Arguments -join " "
    if ($Secret) { $shown = $shown.Replace($Secret, "***") }
    Write-Host ("  {0} {1}" -f (Split-Path $Tool -Leaf), $shown)
    & $Tool @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$(Split-Path $Tool -Leaf) $($Arguments[0]) failed with exit code $LASTEXITCODE" }
}

# makeappx, makepri and signtool from the newest Windows SDK that has all three for this machine's architecture.
function Find-WindowsSdk {
    $roots = [Collections.Generic.List[string]]::new()
    try {
        $kits = Get-ItemPropertyValue -Path "HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots" -Name "KitsRoot10" -ErrorAction Stop
        if ($kits) { $roots.Add($kits) }
    }
    catch {
        Write-Verbose "No KitsRoot10 in the registry: looking in the default places."
    }
    foreach ($programFiles in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if ($programFiles) { $roots.Add((Join-Path $programFiles "Windows Kits\10")) }
    }
    $hostArchitectures = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [Runtime.InteropServices.Architecture]::Arm64) {
        @("arm64", "x64", "x86")
    }
    else {
        @("x64", "x86")
    }
    $sdks = @(foreach ($root in ($roots | Select-Object -Unique)) {
            $bin = Join-Path $root "bin"
            if (Test-Path -LiteralPath $bin) { Get-ChildItem -LiteralPath $bin -Directory | Where-Object { $_.Name -match '^10\.0\.\d+\.\d+$' } }
        })
    foreach ($sdk in ($sdks | Sort-Object { [version]$_.Name } -Descending)) {
        foreach ($hostArchitecture in $hostArchitectures) {
            $folder = Join-Path $sdk.FullName $hostArchitecture
            $tools = [ordered]@{
                MakeAppx = Join-Path $folder "makeappx.exe"
                MakePri  = Join-Path $folder "makepri.exe"
                SignTool = Join-Path $folder "signtool.exe"
            }
            if (@($tools.Values | Where-Object { -not (Test-Path -LiteralPath $_) }).Count -eq 0) {
                Write-Host "▸ Windows SDK $($sdk.Name) ($hostArchitecture)"
                return [pscustomobject]$tools
            }
        }
    }
    throw "makeappx.exe, makepri.exe and signtool.exe weren't found together in a Windows SDK (Windows Kits\10\bin\10.0.*\<arch>). Install the Windows SDK (Visual Studio Installer, or https://developer.microsoft.com/windows/downloads/windows-sdk/)."
}

# MSIX versions have four numbers and the Store wants the fourth to be 0: -Version 0.3.1 becomes package 0.3.1.0.
if ($Version.Trim().TrimStart("v") -match '^(\d+)\.(\d+)\.(\d+)(\.0+)?$') {
    $numbers = @([int64]$Matches[1], [int64]$Matches[2], [int64]$Matches[3])
}
else {
    throw "-Version must look like 1.2.3 (a Store package can't carry a pre-release tag or a fourth number other than 0), not '$Version'"
}
if (@($numbers | Where-Object { $_ -gt 65535 }).Count -gt 0) { throw "every part of -Version must be at most 65535: $Version" }
$Version = $numbers -join "."
$PackageVersion = "$Version.0"
$Arch = @($Arch | Select-Object -Unique)

$Root = Split-Path -Parent $PSScriptRoot
$OutDir = [IO.Path]::GetFullPath([IO.Path]::Combine($Root, $OutDir))
$Work = Join-Path $OutDir "work-$Version"
$Packages = Join-Path $Work "packages"
$Bundle = Join-Path $OutDir "Pickverter-$Version.msixbundle"
$TestDir = Join-Path $OutDir "test-signed"
$planned = @($Work, $Bundle) + @($Arch | ForEach-Object { Join-Path $OutDir "Pickverter-$Version-$_.msix" })
if ($TestCertificate) { $planned += $TestDir }
foreach ($path in $planned) {
    if (Test-Path -LiteralPath $path) { throw "$path already exists — move it away first" }
}
foreach ($file in @("packaging\AppxManifest.xml", "packaging\priconfig.xml", "packaging\Assets")) {
    if (-not (Test-Path -LiteralPath (Join-Path $Root $file))) { throw "$file is missing (Assets: node scripts/make-icons.mjs)" }
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "dotnet (the .NET 10 SDK) isn't on PATH" }
$Tools = Find-WindowsSdk

# Same numbers as scripts/release.ps1: the file version carries the commit count.
$Build = 0
try {
    $count = git -C $Root rev-list --count HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $count) { $Build = [int]$count }
}
catch {
    Write-Verbose "Not a git checkout: build 0."
}
$FileVersion = "$Version." + ($Build % 65535)

Push-Location $Root
try {
    New-Item -ItemType Directory -Path $Packages -Force | Out-Null
    $template = [IO.File]::ReadAllText((Join-Path $Root "packaging\AppxManifest.xml"))
    foreach ($architecture in $Arch) {
        Write-Host "▸ win-$architecture"
        $stage = Join-Path $Work "stage-$architecture"
        $app = Join-Path $stage "Pickverter"

        # The app, published exactly like the GitHub edition's release build.
        $properties = @("-p:PickverterRelease=true", "-p:Version=$Version", "-p:FileVersion=$FileVersion", "-p:InformationalVersion=$Version", "-p:DebugType=none")
        dotnet publish src/Pickverter/Pickverter.csproj -c Release -r "win-$architecture" --self-contained true @properties -o $app
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish win-$architecture failed with exit code $LASTEXITCODE" }
        $exe = Join-Path $app "Pickverter.exe"
        if (-not (Test-Path -LiteralPath $exe)) { throw "dotnet publish win-$architecture made no Pickverter.exe" }
        $product = [string](Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
        if ($product.Split("+")[0] -ne $Version) { throw "Pickverter.exe says version '$product', expected $Version" }

        # Logos and tiles: makepri indexes Assets\ only.
        Copy-Item -LiteralPath (Join-Path $Root "packaging\Assets") -Destination (Join-Path $stage "Assets") -Recurse

        # The manifest for this package.
        $text = $template.Replace("{{Version}}", $PackageVersion).Replace("{{Architecture}}", $architecture)
        if ($text.Contains("{{")) { throw "packaging\AppxManifest.xml has a placeholder this script doesn't fill" }
        $manifest = [xml]$text
        $names = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
        $names.AddNamespace("m", $FoundationNamespace)
        $names.AddNamespace("uap", $UapNamespace)
        $identity = $manifest.SelectSingleNode("/m:Package/m:Identity", $names)
        if ($null -eq $identity) { throw "packaging\AppxManifest.xml has no Package/Identity" }
        $expected = [ordered]@{ Name = $IdentityName; Publisher = $Publisher; Version = $PackageVersion; ProcessorArchitecture = $architecture }
        foreach ($key in $expected.Keys) {
            if ($identity.GetAttribute($key) -ne $expected[$key]) { throw "AppxManifest.xml Identity $key is '$($identity.GetAttribute($key))', expected '$($expected[$key])'" }
        }
        $application = $manifest.SelectSingleNode("/m:Package/m:Applications/m:Application", $names)
        $visual = $manifest.SelectSingleNode("/m:Package/m:Applications/m:Application/uap:VisualElements", $names)
        $tile = $manifest.SelectSingleNode("/m:Package/m:Applications/m:Application/uap:VisualElements/uap:DefaultTile", $names)
        if ($null -eq $application -or $null -eq $visual -or $null -eq $tile) { throw "AppxManifest.xml lacks Application, VisualElements or DefaultTile" }
        if (-not (Test-Path -LiteralPath (Join-Path $stage $application.GetAttribute("Executable")))) { throw "AppxManifest.xml starts $($application.GetAttribute("Executable")), which isn't in the package" }
        $images = @(
            $manifest.SelectSingleNode("/m:Package/m:Properties/m:Logo", $names).InnerText
            $visual.GetAttribute("Square150x150Logo")
            $visual.GetAttribute("Square44x44Logo")
            $tile.GetAttribute("Wide310x150Logo")
            $tile.GetAttribute("Square71x71Logo")
            $tile.GetAttribute("Square310x310Logo")
        )
        foreach ($image in $images) {
            $stem = [IO.Path]::GetFileNameWithoutExtension((Split-Path $image -Leaf))
            $folder = Join-Path $stage (Split-Path $image -Parent)
            if (@(Get-ChildItem -LiteralPath $folder -File | Where-Object { $_.Name -like "$stem.*.png" -or $_.Name -eq "$stem.png" }).Count -eq 0) {
                throw "no image for $image in packaging\Assets (node scripts/make-icons.mjs)"
            }
        }
        [IO.File]::WriteAllText((Join-Path $stage "AppxManifest.xml"), $text, [Text.UTF8Encoding]::new($false))

        # resources.pri: Assets\Square44x44Logo.png and friends resolve to their qualified files.
        $pri = Join-Path $stage "resources.pri"
        Invoke-Tool $Tools.MakePri @("new", "/pr", $stage, "/cf", (Join-Path $Root "packaging\priconfig.xml"), "/mn", (Join-Path $stage "AppxManifest.xml"), "/of", $pri, "/o")
        $dump = Join-Path $Work "resources-$architecture.xml"
        Invoke-Tool $Tools.MakePri @("dump", "/if", $pri, "/of", $dump, "/dt", "detailed", "/o")
        $indexed = [IO.File]::ReadAllText($dump).Replace("/", "\").ToLowerInvariant()
        $missing = @(Get-ChildItem -LiteralPath (Join-Path $stage "Assets") -Filter "*.png" -File | Where-Object { -not $indexed.Contains("assets\" + $_.Name.ToLowerInvariant()) })
        if ($missing.Count -gt 0) { throw "resources.pri lacks $($missing.Count) of the assets, e.g. $($missing[0].Name) (see $dump)" }

        Invoke-Tool $Tools.MakeAppx @("pack", "/d", $stage, "/p", (Join-Path $Packages "Pickverter-$Version-$architecture.msix"), "/o")
    }

    # The Store upload: every architecture in one bundle.
    Write-Host "▸ bundle"
    Invoke-Tool $Tools.MakeAppx @("bundle", "/d", $Packages, "/p", $Bundle, "/bv", $PackageVersion, "/o")
    foreach ($architecture in $Arch) { Copy-Item -LiteralPath (Join-Path $Packages "Pickverter-$Version-$architecture.msix") -Destination $OutDir }

    if ($TestCertificate) {
        # Copies signed with a throwaway certificate whose subject is the Store publisher (Windows only installs signed
        # packages). Made in memory: nothing goes into a certificate store; the private key only lives in work\ during signing.
        Write-Host "▸ test-signed copies"
        $signed = Join-Path $Work "packages-test"
        New-Item -ItemType Directory -Path $signed, $TestDir -Force | Out-Null
        $rsa = [Security.Cryptography.RSA]::Create(2048)
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new($Publisher, $rsa,
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
        $usages = [Security.Cryptography.OidCollection]::new()
        $usages.Add([Security.Cryptography.Oid]::new("1.3.6.1.5.5.7.3.3", "Code Signing")) | Out-Null
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usages, $false))
        $certificate = $request.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddDays(90))
        $password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
        $pfx = Join-Path $Work "test-signing.pfx"
        [IO.File]::WriteAllBytes($pfx, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password))
        try {
            foreach ($architecture in $Arch) {
                $copy = Join-Path $signed "Pickverter-$Version-$architecture.msix"
                Copy-Item -LiteralPath (Join-Path $Packages "Pickverter-$Version-$architecture.msix") -Destination $copy
                Invoke-Tool $Tools.SignTool @("sign", "/fd", "SHA256", "/f", $pfx, "/p", $password, $copy) -Secret $password
                Copy-Item -LiteralPath $copy -Destination $TestDir
            }
            $testBundle = Join-Path $TestDir "Pickverter-$Version.msixbundle"
            Invoke-Tool $Tools.MakeAppx @("bundle", "/d", $signed, "/p", $testBundle, "/bv", $PackageVersion, "/o")
            Invoke-Tool $Tools.SignTool @("sign", "/fd", "SHA256", "/f", $pfx, "/p", $password, $testBundle) -Secret $password
            [IO.File]::WriteAllBytes((Join-Path $TestDir "Pickverter-$Version-test.cer"),
                $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        }
        finally {
            Remove-Item -LiteralPath $pfx -Force -ErrorAction SilentlyContinue
            $certificate.Dispose()
            $rsa.Dispose()
        }
    }
}
finally {
    Pop-Location
}

Write-Host "✓ $OutDir"
$made = @(Get-ChildItem -LiteralPath $OutDir -File) + @(if ($TestCertificate) { Get-ChildItem -LiteralPath $TestDir -File })
foreach ($file in $made) {
    Write-Host ("  {0,-52} {1,8:N1} MB" -f [IO.Path]::GetRelativePath($OutDir, $file.FullName), ($file.Length / 1MB))
}
Write-Host "  Partner Center › Packages: upload Pickverter-$Version.msixbundle (unsigned; the Store signs it)."
if ($TestCertificate) {
    Write-Host "  Sideloading: trust test-signed\Pickverter-$Version-test.cer (Local Machine › Trusted People), then open test-signed\Pickverter-$Version.msixbundle."
}
