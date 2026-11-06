# Downloads the free DB-IP "IP to Country Lite" database (CC BY 4.0, https://db-ip.com) to tools/geoip/.
# The Torrents detail window uses it to show a country code next to each peer. Lookups happen on this computer only.
# It is optional: without the file the Country column is simply empty.
#   pwsh -ExecutionPolicy Bypass -File tools/get-geoip.ps1 [-Force]
param([switch]$Force)
$ErrorActionPreference = 'Stop'

$dest = Join-Path $PSScriptRoot 'geoip'
$file = Join-Path $dest 'dbip-country-lite.csv.gz'
if ((Test-Path $file) -and -not $Force) {
    Write-Host "GeoIP database already present: $file"
    return
}

# DB-IP publishes one file a month; the current month's may not exist yet, so fall back a few months.
$tmp = "$file.download"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
$date = Get-Date
for ($i = 0; $i -lt 4; $i++) {
    $stamp = $date.AddMonths(-$i).ToString('yyyy-MM')
    $url = "https://download.db-ip.com/free/dbip-country-lite-$stamp.csv.gz"
    try {
        Invoke-WebRequest -Uri $url -OutFile $tmp
    }
    catch {
        Write-Host "Not available: $url"
        continue
    }
    # Sanity check: gzip magic number, and a plausible size (the real file is several MB).
    $bytes = [IO.File]::ReadAllBytes($tmp)
    if ($bytes.Length -lt 1MB -or $bytes[0] -ne 0x1f -or $bytes[1] -ne 0x8b) {
        Remove-Item $tmp -Force
        throw "Unexpected download from $url"
    }
    Move-Item $tmp $file -Force
    Write-Host "GeoIP database ($stamp) saved to $file"
    return
}
throw 'Could not download the DB-IP country database.'
