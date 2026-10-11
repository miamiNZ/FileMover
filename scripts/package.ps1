# Zips each published runtime into dist/ and writes SHA256SUMS.txt. PowerShell 5.1 compatible.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string[]]$Runtimes = @("win-x64", "win-arm64")
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root "dist"

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null

$sums = @()
foreach ($rt in $Runtimes) {
    $exe = Join-Path $root "publish\$rt\FileMover.exe"
    if (-not (Test-Path $exe)) { throw "Missing $exe — run 'make publish RUNTIME=$rt' first." }

    $zip = Join-Path $dist "FileMover-$Version-$rt.zip"
    $files = @($exe) + (@("README.md", "LICENSE") | ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path $_ })
    Compress-Archive -Path $files -DestinationPath $zip
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $sums += "$hash  $(Split-Path $zip -Leaf)"
    Write-Host "Packaged $zip" -ForegroundColor Green
}

Set-Content -Path (Join-Path $dist "SHA256SUMS.txt") -Value $sums -Encoding ascii
Write-Host "Checksums written to dist\SHA256SUMS.txt" -ForegroundColor Green
