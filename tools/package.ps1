# Builds a self-contained, unpackaged release and zips it, with a SHA-256 file next to the zip.
# Used by the release workflow and runnable locally:
#   pwsh -ExecutionPolicy Bypass -File tools/package.ps1 -Rid win-x64 -Version 0.3.0
# Output: artifacts/Aria2NET-<Version>-<Rid>.zip and .zip.sha256
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Rid = 'win-x64',

    # SemVer, e.g. 0.3.0 or 1.0.0-rc.1 (no leading "v").
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$OutDir = (Join-Path $PSScriptRoot '..\artifacts')
)
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$platform = @{ 'win-x64' = 'x64'; 'win-arm64' = 'ARM64' }[$Rid]
$name = "Aria2NET-$Version-$Rid"

& (Join-Path $PSScriptRoot 'get-aria2.ps1')

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$stage = Join-Path $OutDir $name
$zip = Join-Path $OutDir "$name.zip"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

# PublishProfile is cleared because the csproj points at a profile file that does not exist.
dotnet publish (Join-Path $root 'Aria2NET\Aria2NET.csproj') -c Release -r $Rid "-p:Platform=$platform" -p:PublishProfile= "-p:Version=$Version" --self-contained true -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# The app cannot start without these, so fail the build instead of shipping a broken zip.
foreach ($required in 'Aria2NET.exe', 'Aria2NET.pri', 'MainWindow.xbf', 'tools\aria2\aria2c.exe', 'Assets\AppIcon.ico') {
    if (-not (Test-Path (Join-Path $stage $required))) { throw "Published output is missing $required" }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
Remove-Item $stage -Recurse -Force

$hash = (Get-FileHash -Algorithm SHA256 -Path $zip).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash *$name.zip" -Encoding ascii
Write-Host "Created $zip"
Write-Host "SHA-256 $hash"
