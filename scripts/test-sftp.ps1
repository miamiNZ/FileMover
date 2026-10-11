# Starts a throwaway OpenSSH server in Docker, runs the full test suite against it
# (including SftpIntegrationTests), then removes the container. PowerShell 5.1 compatible.
param(
    [string]$Configuration = "Release",
    [int]$Port = 2222
)

$ErrorActionPreference = "Stop"
$root      = Split-Path $PSScriptRoot -Parent
$container = "filemover-sftp-test"
$user      = "fmtest"
# Random per-run password; the container only listens on localhost and is deleted afterwards.
$pass      = [guid]::NewGuid().ToString("N")

function Remove-TestContainer {
    if (docker ps -aq --filter "name=^$container$") { docker rm -f $container | Out-Null }
}

Remove-TestContainer
docker run -d --rm --name $container -p "127.0.0.1:${Port}:2222" `
    -e PUID=1000 -e PGID=1000 -e "USER_NAME=$user" -e "USER_PASSWORD=$pass" -e PASSWORD_ACCESS=true `
    lscr.io/linuxserver/openssh-server:latest | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not start the SFTP container (is Docker running?)" }

try {
    Write-Host "Waiting for SSH on 127.0.0.1:$Port..." -ForegroundColor Cyan
    $deadline = (Get-Date).AddSeconds(90)
    do {
        Start-Sleep -Seconds 2
        $ErrorActionPreference = "Continue"   # sshd logs to stderr; don't treat that as a failure
        $ready = (docker logs $container 2>&1 | Out-String) -match "ls.io-init] done"
        $ErrorActionPreference = "Stop"
    } until ($ready -or (Get-Date) -gt $deadline)
    if (-not $ready) { throw "SSH server did not become ready in time." }

    $env:FILEMOVER_SFTP_HOST = "localhost"
    $env:FILEMOVER_SFTP_PORT = "$Port"
    $env:FILEMOVER_SFTP_USER = $user
    $env:FILEMOVER_SFTP_PASS = $pass
    $env:FILEMOVER_SFTP_ROOT = "/tmp"

    dotnet test --solution (Join-Path $root "FileMover.slnx") -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit $LASTEXITCODE)" }
}
finally {
    Remove-Item Env:FILEMOVER_SFTP_* -ErrorAction SilentlyContinue
    Remove-TestContainer
}
