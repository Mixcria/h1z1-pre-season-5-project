param(
    [Parameter(Mandatory=$true)][string]$ServerDll,
    [string]$ClientDll,
    [Parameter(Mandatory=$true)][string]$Output,
    [int]$Bots = 150,
    [int]$Seconds = 60,
    [int]$CombatSeconds = 0,
    [ValidateRange(1,20)][int]$MatchCycles = 1,
    [int]$MenuBots = 0,
    [switch]$MenuOnly,
    [switch]$Tls,
    [switch]$Voice,
    [switch]$WireAudit,
    [ValidateSet('Default','Workstation','Server')][string]$ServerGcMode = 'Default',
    [ValidateSet('Default','Workstation','Server')][string]$ClientGcMode = 'Default',
    [int]$DelayMs = 0,
    [int]$JitterMs = 0,
    [double]$Loss = 0,
    [switch]$SlowClient,
    [switch]$ReliableMovement
)
$ErrorActionPreference = 'Stop'
$ServerDll = (Resolve-Path -LiteralPath $ServerDll).Path
if (-not $ClientDll) { $ClientDll = $ServerDll }
$ClientDll = (Resolve-Path -LiteralPath $ClientDll).Path
$Output = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $Output) { throw 'Use a fresh output directory to retain prior results.' }
New-Item -ItemType Directory -Path $Output | Out-Null
function BinaryManifest([string]$entry) {
    $directory = Split-Path -Parent $entry
    @(Get-ChildItem -LiteralPath $directory -File | Where-Object {
        $_.Name -like 'Cranberry.*.dll' -or $_.Name -eq 'Cranberry.NetworkBots.runtimeconfig.json'
    } | Sort-Object Name | ForEach-Object {
        [pscustomobject]@{ name=$_.Name; bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
[pscustomobject]@{
    startedUtc=[DateTime]::UtcNow.ToString('o'); serverDll=$ServerDll; clientDll=$ClientDll
    bots=$Bots; menuBots=$MenuBots; seconds=$Seconds; combatSeconds=$CombatSeconds; matchCycles=$MatchCycles
    menuOnly=[bool]$MenuOnly; tls=[bool]$Tls; voice=[bool]$Voice; wireAudit=[bool]$WireAudit; serverGcMode=$ServerGcMode; clientGcMode=$ClientGcMode
    delayMs=$DelayMs; jitterMs=$JitterMs; loss=$Loss; slowClient=[bool]$SlowClient
    reliableMovement=[bool]$ReliableMovement; serverBinaries=(BinaryManifest $ServerDll); clientBinaries=(BinaryManifest $ClientDll)
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Output 'run-input.json')
$serverOutput = Join-Path $Output 'server'
$clientOutput = Join-Path $Output 'clients'
New-Item -ItemType Directory -Path $serverOutput | Out-Null
$dotnet = (Get-Command dotnet).Source
$hostArguments = '"{0}" --server-only --bots {1} --seconds {2} --output "{3}"' -f $ServerDll,$Bots,$Seconds,$serverOutput
$hostArguments += ' --menu-bots {0}' -f $MenuBots
$hostArguments += ' --combat-seconds {0}' -f $CombatSeconds
$hostArguments += ' --match-cycles {0}' -f $MatchCycles
if ($Tls) { $hostArguments += ' --tls-fixture' }
if ($WireAudit) { $hostArguments += ' --wire-audit' }
$previousServerGc = [Environment]::GetEnvironmentVariable('DOTNET_gcServer', 'Process')
try {
    # Only the fixture inherits this override. Keep the load generator's GC unchanged
    # so a runtime comparison does not silently change both ends of the connection.
    if ($ServerGcMode -ne 'Default') {
        [Environment]::SetEnvironmentVariable('DOTNET_gcServer', $(if ($ServerGcMode -eq 'Server') { '1' } else { '0' }), 'Process')
    }
    $fixture = Start-Process -FilePath $dotnet -ArgumentList $hostArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $Output 'fixture.stdout.log') -RedirectStandardError (Join-Path $Output 'fixture.stderr.log')
}
finally {
    [Environment]::SetEnvironmentVariable('DOTNET_gcServer', $previousServerGc, 'Process')
}
# Retain the handle before it exits so Windows PowerShell can retrieve its exit code later.
$null = $fixture.Handle
$resultCode = 1
try {
    $endpoint = Join-Path $serverOutput 'endpoint.json'
    $readyDeadline = [DateTime]::UtcNow.AddSeconds(30 + $(if ($Tls) { ($MenuBots + $Bots) * 2 } else { 0 }))
    while (-not (Test-Path -LiteralPath $endpoint)) {
        if ($fixture.HasExited) { throw 'Fixture server exited before it became ready.' }
        if ([DateTime]::UtcNow -gt $readyDeadline) { throw 'Fixture endpoint was not published before the provisioning deadline.' }
        Start-Sleep -Milliseconds 100
    }
    $clientArguments = @($ClientDll,'--fixture',$endpoint,'--bots',"$Bots",'--seconds',"$Seconds",'--output',$clientOutput,'--delay-ms',"$DelayMs",'--jitter-ms',"$JitterMs",'--loss',$Loss.ToString([Globalization.CultureInfo]::InvariantCulture))
    $clientArguments += @('--menu-bots',"$MenuBots")
    $clientArguments += @('--combat-seconds',"$CombatSeconds")
    $clientArguments += @('--match-cycles',"$MatchCycles")
    if ($MenuOnly) { $clientArguments += '--menu-only' }
    if ($Voice) { $clientArguments += '--voice-load' }
    if ($WireAudit) { $clientArguments += '--wire-audit' }
    if (-not $ReliableMovement) { $clientArguments += '--unreliable-movement' }
    if ($SlowClient) { $clientArguments += '--slow-client' }
    $previousClientGc = [Environment]::GetEnvironmentVariable('DOTNET_gcServer', 'Process')
    try {
        if ($ClientGcMode -ne 'Default') {
            [Environment]::SetEnvironmentVariable('DOTNET_gcServer', $(if ($ClientGcMode -eq 'Server') { '1' } else { '0' }), 'Process')
        }
        & $dotnet @clientArguments 2>&1 | Tee-Object -FilePath (Join-Path $Output 'scenario.log')
        $resultCode = $LASTEXITCODE
    }
    finally { [Environment]::SetEnvironmentVariable('DOTNET_gcServer', $previousClientGc, 'Process') }
}
finally {
    Set-Content -LiteralPath (Join-Path $serverOutput 'stop.request') -Value stop
    $forcedStop = -not $fixture.WaitForExit(20000)
    if ($forcedStop) { $fixture.Kill(); $fixture.WaitForExit(); $resultCode = 1 }
    if ($fixture.ExitCode -ne 0) { $resultCode = 1 }
    $profilePath = Join-Path $serverOutput 'server-profile.json'
    if (Test-Path -LiteralPath $profilePath) {
        $profile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
        if (@($profile.errors).Count -gt 0) { $resultCode = 1 }
    }
    [pscustomobject]@{ serverPid=$fixture.Id; serverExitCode=$fixture.ExitCode; forcedServerStop=$forcedStop; clientExitCode=$resultCode; completedUtc=[DateTime]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'completion.json')
}
exit $resultCode
