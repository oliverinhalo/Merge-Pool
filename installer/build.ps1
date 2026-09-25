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
    [string] $Version,
    [string] $InnoSetupPath
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

# Inno Setup can be installed per-machine or per-user, under a versioned or unversioned folder,
# so look in all of the places it actually lands rather than guessing two of them.
function Find-InnoSetup {
    param([string] $Explicit)

    if ($Explicit) {
        # Accept either ISCC.exe itself or the folder containing it.
        $candidate = if (Test-Path $Explicit -PathType Container) { Join-Path $Explicit 'ISCC.exe' } else { $Explicit }
        if (Test-Path $candidate) { return $candidate }
        throw "Inno Setup was not found at '$Explicit'."
    }

    # Already on PATH?
    $onPath = Get-Command 'iscc.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    # Where its own uninstall entry says it went, per-machine and per-user.
    $registryKeys = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
    )

    foreach ($key in $registryKeys) {
        $location = (Get-ItemProperty -Path $key -Name 'InstallLocation' -ErrorAction SilentlyContinue).InstallLocation
        if ($location) {
            $candidate = Join-Path $location 'ISCC.exe'
            if (Test-Path $candidate) { return $candidate }
        }
    }

    # The usual install directories, including per-user and unversioned ones.
    $roots = @(
        ${env:ProgramFiles(x86)}
        $env:ProgramFiles
        (Join-Path $env:LOCALAPPDATA 'Programs')
    ) | Where-Object { $_ }

    foreach ($root in $roots) {
        foreach ($name in @('Inno Setup 6', 'Inno Setup')) {
            $candidate = Join-Path (Join-Path $root $name) 'ISCC.exe'
            if (Test-Path $candidate) { return $candidate }
        }
    }

    return $null
}

$iscc = Find-InnoSetup -Explicit $InnoSetupPath

if (-not $iscc) {
    throw @'
Inno Setup 6 was not found.

Install it from https://jrsoftware.org/isdl.php, or, if it is already installed somewhere this
script did not look, point at it directly:

    .\installer\build.ps1 -InnoSetupPath "C:\Path\To\Inno Setup 6\ISCC.exe"

The published binaries in artifacts\publish are already built, so you can also just open
installer\MergePool.iss in Inno Setup and press Compile.
'@
}

Write-Host "  using Inno Setup at $iscc"


& $iscc "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'MergePool.iss')
if ($LASTEXITCODE -ne 0) {
    throw 'Inno Setup failed.'
}

Write-Host "Installer written to installer\Output\MergePool-$Version-setup.exe"
