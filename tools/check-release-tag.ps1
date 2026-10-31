# Validates a release tag against Directory.Build.props and decides whether it is a pre-release.
#   pwsh -File tools/check-release-tag.ps1 -Tag v0.3.0
# Rules: the tag is vMAJOR.MINOR.PATCH[-suffix]; MAJOR.MINOR.PATCH must equal <Version> in Directory.Build.props.
# Pre-release when the tag has a suffix (-beta.1, -rc.1, ...) or MAJOR is 0.
# When run by GitHub Actions, writes version= and prerelease= to $GITHUB_OUTPUT.
param(
    [Parameter(Mandatory)]
    [string]$Tag,

    [string]$PropsFile = (Join-Path $PSScriptRoot '..\Directory.Build.props')
)
$ErrorActionPreference = 'Stop'

$m = [regex]::Match($Tag, '^v(?<version>(?<major>\d+)\.\d+\.\d+(?<suffix>-[0-9A-Za-z.-]+)?)$')
if (-not $m.Success) {
    throw "Tag '$Tag' must look like v1.2.3 or v1.2.3-rc.1"
}

$declared = (Select-Xml -Path $PropsFile -XPath '//Version').Node.InnerText.Trim()
$tagCore = $m.Groups['version'].Value -replace '-.*$', ''
if ($tagCore -ne $declared) {
    throw "Tag $Tag does not match <Version>$declared</Version> in Directory.Build.props. Bump the version, commit, then tag."
}

$version = $m.Groups['version'].Value
$prerelease = $m.Groups['suffix'].Success -or $m.Groups['major'].Value -eq '0'

Write-Host "version=$version prerelease=$($prerelease.ToString().ToLowerInvariant())"
if ($env:GITHUB_OUTPUT) {
    "version=$version" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "prerelease=$($prerelease.ToString().ToLowerInvariant())" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}
