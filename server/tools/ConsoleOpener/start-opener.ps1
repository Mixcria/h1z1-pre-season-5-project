# Starts the resident console-opener watcher: press F8 in game to open Cranberry's developer
# console. Run this in an ADMINISTRATOR PowerShell window -- OpenProcess on the client needs it.
#
#   powershell -File C:\Aug2017\Server\tools\ConsoleOpener\start-opener.ps1
#
# The status log is append-only and is never truncated or deleted; grep it for "PATCH OK" and
# "READY" to prove the console really is available this session:
#
#   Select-String -Path C:\Aug2017\logs\console-opener.log -Pattern 'PATCH OK|READY|ERROR'
#
# Try the server-side door first (no tooling, no BattlEye exposure): start the host with
# CRANBERRY_CONSOLE_SELF_FLAG=1 and press Tilde (`) in game. This script is Door B.
#
# See README.md in this directory, and DESIGN-dev-console.md section 3 / section 6.1 step 3.

[CmdletBinding()]
param(
    # Virtual-key code in hex. 77 = F8 (default), 2D = Insert, 24 = Home, 78 = F9.
    [string] $Key = '77',

    # Append-only status log.
    [string] $LogPath = 'C:\Aug2017\logs\console-opener.log',

    # Also NOP the toggle's Command.Spectate send. Off by default: Cranberry ignores that packet,
    # so leaving it alone is one write fewer for BattlEye to look at.
    [switch] $NoSpectatePacket
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'ConsoleOpener.csproj'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)

if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Warning 'This window is not elevated. OpenProcess on H1Z1.exe will fail and the watcher will retry every 3 s. Re-run it as Administrator.'
}

$logDirectory = Split-Path -Parent $LogPath

if ($logDirectory -and -not (Test-Path -LiteralPath $logDirectory)) {
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
}

$arguments = @('--watch', '--key', $Key, '--log', $LogPath)

if ($NoSpectatePacket) {
    $arguments += '--no-spectate-packet'
}

Write-Host "ConsoleOpener --watch (key 0x$Key), log $LogPath. Ctrl+C to stop."
& dotnet run --project $project -- @arguments
exit $LASTEXITCODE
