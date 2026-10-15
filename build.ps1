# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Builds, tests and packages Fieldkit.
.EXAMPLE
  ./build.ps1            # Release build + tests + selftest
  ./build.ps1 -Publish   # also a self-contained single-file exe in artifacts/
  ./build.ps1 -Parity    # regenerate docs/PARITY.md
#>
param(
    [switch]$Publish,
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

if ($Parity) {
    Start-Process -FilePath $exe -ArgumentList '--write-parity', (Join-Path $root 'docs/PARITY.md') -Wait | Out-Null
    Write-Host 'docs/PARITY.md regenerated.'
}

if ($Publish) {
    $out = Join-Path $root "artifacts/$Runtime"
    # Not compressed: a compressed single-file app unpacks its assemblies into memory and keeps them
    # there (about 240 MB idle instead of about 40 MB) and starts twice as slowly. Releases are zipped instead.
    dotnet publish "$root/src/Fieldkit/Fieldkit.csproj" -c Release -r $Runtime --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:PublishReadyToRun=false -p:DebugType=none -o $out
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    Write-Host "Published to $out"
}
