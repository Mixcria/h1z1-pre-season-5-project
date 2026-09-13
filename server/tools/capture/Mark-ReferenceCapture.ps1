# Append a manually supplied observation. This does not monitor input or record the screen.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$RunDirectory,
    [Parameter(Mandatory=$true)][string]$Text,
    [ValidateSet('action','observation','note')][string]$Kind = 'action'
)
$ErrorActionPreference = 'Stop'
$manifestPath = Join-Path $RunDirectory 'session.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Missing session.json in $RunDirectory" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$event = [ordered]@{
    utc = [DateTimeOffset]::UtcNow.ToString('o')
    runId = $manifest.runId
    kind = $Kind
    text = $Text
}
$event | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $RunDirectory 'events.jsonl') -Encoding UTF8
$event | ConvertTo-Json -Compress
