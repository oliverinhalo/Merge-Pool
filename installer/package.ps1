<#
.SYNOPSIS
  Builds the update package that MergePool's own updater downloads.

.DESCRIPTION
  Publishes the service, UI and updater into one folder and zips it. The archive is exactly what
  goes into versions\{version} on a machine: the in-app updater unpacks it beside the running
  version and repoints the 'current' link at it, so an update never overwrites anything and never
  runs a second setup program.

  The installer is for the first install. This is for every one after that.

.EXAMPLE
  .\installer\package.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $Version,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $Version) {
    $props = Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw
    if ($props -match '<VersionPrefix>([^<]+)</VersionPrefix>') {
        $Version = $Matches[1]
    }
    else {
        throw 'Could not read VersionPrefix from Directory.Build.props; pass -Version explicitly.'
    }
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts\package'
}

$payload = Join-Path $repoRoot "artifacts\package-staging\$Version"

Write-Host "Packaging MergePool $Version ($Configuration, $Runtime)."

foreach ($path in @($payload, $OutputDirectory)) {
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}

# All three publish into the same folder: that is the shape versions\{version} has on disk, and
# the updater looks for MergePool.Service.exe at its root to decide the archive is a real release.
$projects = @(
    'src\MergePool.Service\MergePool.Service.csproj'
    'src\MergePool.Ui\MergePool.Ui.csproj'
    'src\MergePool.Updater\MergePool.Updater.csproj'
)

foreach ($project in $projects) {
    Write-Host "  publish $project"
    dotnet publish (Join-Path $repoRoot $project) `
        -c $Configuration -r $Runtime --self-contained false `
        -p:Version=$Version -o $payload
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $project."
    }
}

Copy-Item (Join-Path $repoRoot 'CHANGELOG.md') $payload -Force

foreach ($required in @('MergePool.Service.exe', 'MergePool.Updater.exe', 'MergePool.exe')) {
    if (-not (Test-Path (Join-Path $payload $required))) {
        throw "The package is missing $required, so MergePool could not update itself with it."
    }
}

$archive = Join-Path $OutputDirectory "MergePool-$Version-windows-x64.zip"
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $archive -CompressionLevel Optimal

$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $archive -Leaf)" | Out-File (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii

Write-Host ""
Write-Host "  $archive"
Write-Host "  sha256 $hash"
