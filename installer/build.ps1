<#
.SYNOPSIS
    Builds the per-user MSI for UsageDock.

.DESCRIPTION
    Publishes the app self-contained as a folder (not single-file, not compressed, see
    Package.wxs for why), then compiles the MSI with WiX. Requires the WiX dotnet tool:

        dotnet tool install --global wix --version 6.0.2
        wix extension add -g WixToolset.Util.wixext/6.0.2

    WiX 6 is pinned on purpose: v7 requires accepting a maintenance fee licence.

.PARAMETER Version
    Three-part version stamped into the MSI and the binaries, for example 0.2.0.

.PARAMETER OutputDir
    Where the MSI lands. Defaults to installer\out.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [string] $OutputDir = (Join-Path $PSScriptRoot 'out')
)

$ErrorActionPreference = 'Stop'

$root       = Split-Path $PSScriptRoot -Parent
$project    = Join-Path $root 'src\UsageDock\UsageDock.csproj'
$publishDir = Join-Path $PSScriptRoot 'publish'
$msi        = Join-Path $OutputDir "UsageDock-$Version-win-x64.msi"

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force $OutputDir | Out-Null

Write-Host "Publishing $Version (self-contained folder)..."
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -o $publishDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# The debug symbols are not something an installer should ship.
Get-ChildItem $publishDir -Filter *.pdb | Remove-Item -Force

Write-Host "Building MSI..."
wix build (Join-Path $PSScriptRoot 'Package.wxs') `
    -ext WixToolset.Util.wixext `
    -d "PublishDir=$publishDir" `
    -d "Version=$Version" `
    -arch x64 `
    -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }

# WiX drops its own debug symbols next to the MSI; they are not a release artifact.
Get-ChildItem $OutputDir -Filter *.wixpdb | Remove-Item -Force

$size = [math]::Round((Get-Item $msi).Length / 1MB, 1)
Write-Host "Built $msi ($size MB)"
