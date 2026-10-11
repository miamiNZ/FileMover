# Removes build outputs, publish/, dist/ and test results.
$root = Split-Path $PSScriptRoot -Parent
'publish', 'dist', 'TestResults', 'src\bin', 'src\obj', 'tests\FileMover.Tests\bin', 'tests\FileMover.Tests\obj' |
    ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path $_ } | Remove-Item -Recurse -Force
