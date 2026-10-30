# Downloads the pinned aria2 release, verifies its SHA-256, and extracts it to tools/aria2/.
# Run from anywhere:  pwsh -ExecutionPolicy Bypass -File tools/get-aria2.ps1
$ErrorActionPreference = 'Stop'

$version = '1.37.0'
$url     = "https://github.com/aria2/aria2/releases/download/release-$version/aria2-$version-win-64bit-build1.zip"
# Pinned on first download (trust on first use); update together with $version.
$sha256  = '67d015301eef0b612191212d564c5bb0a14b5b9c4796b76454276a4d28d9b288'

$dest = Join-Path $PSScriptRoot 'aria2'
if (Test-Path (Join-Path $dest 'aria2c.exe')) {
    Write-Host "aria2c.exe already present in $dest"
    return
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("aria2-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $zip = Join-Path $work 'aria2.zip'
    Invoke-WebRequest -Uri $url -OutFile $zip

    $actual = (Get-FileHash -Algorithm SHA256 -Path $zip).Hash.ToLowerInvariant()
    if ($actual -ne $sha256) {
        throw "SHA-256 mismatch. Expected $sha256 but got $actual"
    }

    $extract = Join-Path $work 'x'
    Expand-Archive -Path $zip -DestinationPath $extract
    $src = Join-Path $extract "aria2-$version-win-64bit-build1"

    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    foreach ($f in 'aria2c.exe', 'COPYING', 'LICENSE.OpenSSL') {
        Copy-Item (Join-Path $src $f) $dest -Force
    }
    Write-Host "aria2 $version installed to $dest"
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
