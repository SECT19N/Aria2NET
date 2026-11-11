# Builds a self-contained, unpackaged release and packs it with Velopack (https://velopack.io).
# Used by the release workflow and runnable locally:
#   pwsh -ExecutionPolicy Bypass -File tools/package.ps1 -Rid win-x64 -Version 0.8.0
# Output: artifacts/<Rid>/ with the installer (SECT19N.Aria2NET-<Rid>-Setup.exe), the portable zip (...-Portable.zip), the full package
# (...-full.nupkg), delta packages when an earlier release was downloaded first, and the feed files (releases.<Rid>.json, RELEASES-<Rid>).
# Each processor has its own Velopack channel (named like the Rid), so an installed copy only ever updates to a package for its own processor.
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Rid = 'win-x64',

    # SemVer, e.g. 0.8.0 or 1.0.0-rc.1 (no leading "v").
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$OutDir = (Join-Path $PSScriptRoot '..\artifacts'),

    # Fetch the packages of earlier releases first so Velopack can build delta packages (needs $env:VPK_TOKEN). A failure (for example
    # when there is no earlier release for this channel yet) is not an error: the release just has no deltas.
    [switch]$DownloadPrevious
)
$ErrorActionPreference = 'Stop'

# The package id is permanent: installed copies look for updates under it, and Velopack installs to %LocalAppData%\<id>.
# It must differ from the data folder (%LocalAppData%\Aria2NET), or uninstalling would delete the user's data.
$packId = 'SECT19N.Aria2NET'
$repoUrl = 'https://github.com/SECT19N/Aria2NET'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$platform = @{ 'win-x64' = 'x64'; 'win-arm64' = 'ARM64' }[$Rid]

& (Join-Path $PSScriptRoot 'get-aria2.ps1')

# Optional: the country column in the torrent details. A failed download must not stop a release.
try { & (Join-Path $PSScriptRoot 'get-geoip.ps1') } catch { Write-Warning "GeoIP database not bundled: $($_.Exception.Message)" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$stage = Join-Path $OutDir "stage-$Rid"
$releases = Join-Path $OutDir $Rid
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $releases | Out-Null

# PublishProfile is cleared because the csproj points at a profile file that does not exist.
dotnet publish (Join-Path $root 'Aria2NET\Aria2NET.csproj') -c Release -r $Rid "-p:Platform=$platform" -p:PublishProfile= "-p:Version=$Version" --self-contained true -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# The app cannot start without these, so fail the build instead of shipping a broken package.
foreach ($required in 'Aria2NET.exe', 'Aria2NET.pri', 'MainWindow.xbf', 'tools\aria2\aria2c.exe', 'Assets\AppIcon.ico', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.md') {
    if (-not (Test-Path (Join-Path $stage $required))) { throw "Published output is missing $required" }
}

Push-Location $root
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed ($LASTEXITCODE)" }

    if ($DownloadPrevious) {
        dotnet vpk download github --repoUrl $repoUrl --channel $Rid --outputDir $releases --pre
        if ($LASTEXITCODE -ne 0) { Write-Warning "No earlier packages for $Rid were downloaded; this release will have no delta packages." }
        $global:LASTEXITCODE = 0
    }

    dotnet vpk pack --packId $packId --packVersion $Version --packDir $stage --mainExe Aria2NET.exe --packTitle Aria2NET --packAuthors SECT19N `
        --icon (Join-Path $root 'Aria2NET\Assets\AppIcon.ico') --runtime $Rid --channel $Rid --outputDir $releases
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)" }
} finally {
    Pop-Location
}
Remove-Item $stage -Recurse -Force

# The workflow and people look for these, so a pack that silently left one out must fail here.
foreach ($expected in "$packId-$Rid-Setup.exe", "$packId-$Rid-Portable.zip", "$packId-$Version-$Rid-full.nupkg", "releases.$Rid.json") {
    if (-not (Test-Path (Join-Path $releases $expected))) { throw "vpk pack did not create $expected" }
}

Write-Host "Packed $releases"
