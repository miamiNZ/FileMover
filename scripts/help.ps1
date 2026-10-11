# Lists Makefile targets that have a "## description" comment.
$makefile = Join-Path (Split-Path $PSScriptRoot -Parent) "Makefile"
Get-Content $makefile | Select-String '^([a-zA-Z0-9_-]+):.*?## (.*)$' | ForEach-Object {
    '  {0,-16} {1}' -f $_.Matches[0].Groups[1].Value, $_.Matches[0].Groups[2].Value
}
