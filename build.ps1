# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Builds, tests and packages Fieldkit.
.EXAMPLE
  ./build.ps1            # Release build + tests + selftest
  ./build.ps1 -Publish   # also a self-contained single-file exe in artifacts/
  ./build.ps1 -Installer # -Publish plus the installer (needs Inno Setup 6)
  ./build.ps1 -Parity    # regenerate docs/PARITY.md
#>
param(
    [switch]$Publish,
    [switch]$Installer,
    [switch]$Parity,
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$env:FIELDKIT_DATA_DIR = Join-Path ([IO.Path]::GetTempPath()) 'fieldkit-build'
$exe = Join-Path $root 'src/Fieldkit/bin/Release/net8.0-windows10.0.19041.0/Fieldkit.exe'

dotnet build "$root/Fieldkit.sln" -c Release
if ($LASTEXITCODE) { exit $LASTEXITCODE }

dotnet test "$root/Fieldkit.sln" -c Release --no-build
if ($LASTEXITCODE) { exit $LASTEXITCODE }

$self = Start-Process -FilePath $exe -ArgumentList '--selftest' -Wait -PassThru
if ($self.ExitCode -ne 0) { Write-Error 'Selftest failed (see fieldkit.log in FIELDKIT_DATA_DIR)'; exit 1 }
Write-Host 'Selftest passed.'

# The same check CI makes: docs/PARITY.md must match what the app generates (run ./build.ps1 -Parity to refresh it).
$generated = Join-Path ([IO.Path]::GetTempPath()) 'parity.generated.md'
Start-Process -FilePath $exe -ArgumentList '--write-parity', $generated -Wait | Out-Null
$fresh = (Get-Content $generated -Raw) -replace "`r", ''
$committed = (Get-Content (Join-Path $root 'docs/PARITY.md') -Raw) -replace "`r", ''
if (-not $Parity -and $fresh -ne $committed) { Write-Error 'docs/PARITY.md is stale: run ./build.ps1 -Parity'; exit 1 }

if ($Parity) {
    Start-Process -FilePath $exe -ArgumentList '--write-parity', (Join-Path $root 'docs/PARITY.md') -Wait | Out-Null
    Write-Host 'docs/PARITY.md regenerated.'
}

if ($Publish -or $Installer) {
    $out = Join-Path $root "artifacts/$Runtime"
    # Not compressed: a compressed single-file app unpacks its assemblies into memory and keeps them
    # there (about 240 MB idle instead of about 40 MB) and starts twice as slowly. Releases are zipped instead.
    dotnet publish "$root/src/Fieldkit/Fieldkit.csproj" -c Release -r $Runtime --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:PublishReadyToRun=false -p:DebugType=none -o $out
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    Write-Host "Published to $out"
}

if ($Installer) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { Write-Error 'Inno Setup 6 is not installed (winget install JRSoftware.InnoSetup)'; exit 1 }
    $version = ([xml](Get-Content "$root/src/Fieldkit/Fieldkit.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    & $iscc /Q "/DAppVersion=$version" "/DArch=$($Runtime.Substring(4))" "$root/installer/Fieldkit.iss"
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    Write-Host "Installer: artifacts/Fieldkit-$version-setup-$($Runtime.Substring(4)).exe"
}
