# Validates a release version, writes it into Directory.Build.props and decides whether it is a pre-release.
#   pwsh -File tools/prepare-release.ps1 -Version 0.4.0
# Rules: MAJOR.MINOR.PATCH[-suffix] (no leading "v"); MAJOR.MINOR.PATCH must not be lower than the current <Version>;
# the tag v<Version> must not exist yet. Pre-release when there is a suffix (-beta.1, -rc.1, ...) or MAJOR is 0.
# When run by GitHub Actions, writes version=, tag= and prerelease= to $GITHUB_OUTPUT.
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [string]$PropsFile = (Join-Path $PSScriptRoot '..\Directory.Build.props'),

    # Check only; leave the props file as it is.
    [switch]$NoWrite
)
$ErrorActionPreference = 'Stop'

$m = [regex]::Match($Version, '^(?<core>(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+))(?<suffix>-[0-9A-Za-z.-]+)?$')
if (-not $m.Success) {
    throw "Version '$Version' must look like 1.2.3 or 1.2.3-rc.1 (no leading v)"
}

$text = [System.IO.File]::ReadAllText($PropsFile)
$current = [regex]::Match($text, '<Version>(?<v>[^<]+)</Version>')
if (-not $current.Success) { throw "No <Version> element in $PropsFile" }
$declared = $current.Groups['v'].Value.Trim()

if ([version]$m.Groups['core'].Value -lt [version]($declared -replace '-.*$', '')) {
    throw "Version $Version is lower than the current $declared in Directory.Build.props"
}

$tag = "v$Version"
git rev-parse -q --verify "refs/tags/$tag" 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { throw "Tag $tag already exists" }

# The suffix is only for the tag/release; Directory.Build.props keeps the plain MAJOR.MINOR.PATCH.
$core = $m.Groups['core'].Value
if (-not $NoWrite -and $declared -ne $core) {
    $updated = $text.Substring(0, $current.Index) + "<Version>$core</Version>" + $text.Substring($current.Index + $current.Length)
    [System.IO.File]::WriteAllText($PropsFile, $updated, (New-Object System.Text.UTF8Encoding($false)))
}

$prerelease = ($m.Groups['suffix'].Success -or $m.Groups['major'].Value -eq '0').ToString().ToLowerInvariant()
Write-Host "version=$Version tag=$tag prerelease=$prerelease (Directory.Build.props: $declared -> $core)"
if ($env:GITHUB_OUTPUT) {
    "version=$Version" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "tag=$tag" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "prerelease=$prerelease" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}
