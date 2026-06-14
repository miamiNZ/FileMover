param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$out = Join-Path (Join-Path $PSScriptRoot "publish") $Runtime

Write-Host "Building FileMover  runtime=$Runtime  config=$Configuration" -ForegroundColor Cyan

dotnet publish "$PSScriptRoot\src\FileMover.csproj" `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $out

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}

$exe = Get-ChildItem $out -Filter "FileMover*" | Select-Object -First 1
Write-Host "Output: $($exe.FullName)  ($([math]::Round($exe.Length/1MB, 1)) MB)" -ForegroundColor Green
