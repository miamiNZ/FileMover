# Prints the <Version> from src/FileMover.csproj (used by the Makefile).
$csproj = Join-Path (Split-Path $PSScriptRoot -Parent) "src\FileMover.csproj"
([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
