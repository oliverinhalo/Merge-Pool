<#
.SYNOPSIS
  Publishes MergePool and builds the installer.

.DESCRIPTION
  Publishes the service, UI and updater into artifacts\publish, then compiles installer\MergePool.iss
  with Inno Setup. The version used for the side-by-side install directory comes from the built
  assemblies, so the installer and the updater always agree on it.

.EXAMPLE
  .\installer\build.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot 'artifacts\publish'

if (-not $Version) {
    $props = Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw
    if ($props -match '<VersionPrefix>([^<]+)</VersionPrefix>') {
        $Version = $Matches[1]
    }
    else {
        throw 'Could not read VersionPrefix from Directory.Build.props; pass -Version explicitly.'
    }
}

Write-Host "Building MergePool $Version ($Configuration, $Runtime)."

if (Test-Path $artifacts) {
    Remove-Item $artifacts -Recurse -Force
}

$targets = @(
    @{ Project = 'src\MergePool.Service\MergePool.Service.csproj'; Output = 'service' }
    @{ Project = 'src\MergePool.Ui\MergePool.Ui.csproj';           Output = 'ui'      }
    @{ Project = 'src\MergePool.Updater\MergePool.Updater.csproj'; Output = 'updater' }
)

foreach ($target in $targets) {
    $out = Join-Path $artifacts $target.Output
    Write-Host "  publish $($target.Project) -> $out"

    dotnet publish (Join-Path $repoRoot $target.Project) `
        -c $Configuration -r $Runtime --self-contained false `
        -p:Version=$Version -o $out
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $($target.Project)."
    }
}

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw 'Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php.'
}

& $iscc "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'MergePool.iss')
if ($LASTEXITCODE -ne 0) {
    throw 'Inno Setup failed.'
}

Write-Host "Installer written to installer\Output\MergePool-$Version-setup.exe"
