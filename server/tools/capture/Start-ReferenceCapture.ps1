# Record a reference client session before connecting. Run in a terminal; Ctrl+C stops dumpcap.
# A new directory is created for every run. Existing evidence is never overwritten.
[CmdletBinding()]
param(
    [string]$ClientExe = 'C:\Games\ROTK\H1Z1.exe',
    [string[]]$ServerAddress,
    [string[]]$Interface,
    [ValidateRange(1,65535)][int[]]$Port,
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Label = 'rotk',
    [ValidateRange(1,14400)][int]$DurationSeconds = 1800,
    [ValidateRange(1,4096)][int]$MaxMiB = 1024,
    [string]$OutputRoot = 'C:\Aug2017\captures\reference',
    [string]$Dumpcap = 'C:\Program Files\Wireshark\dumpcap.exe',
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-JsonFile($Path, $Value) {
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Get-BinaryIdentity([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    return [ordered]@{
        path = $item.FullName
        fileVersion = $item.VersionInfo.FileVersion
        productVersion = $item.VersionInfo.ProductVersion
        bytes = $item.Length
        sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

if (-not (Test-Path -LiteralPath $Dumpcap -PathType Leaf)) { throw "Missing dumpcap: $Dumpcap" }
$client = Get-BinaryIdentity $ClientExe
$source = 'explicit ServerAddress argument'
if (-not $ServerAddress) {
    # Read only the routing entry. Do not copy the client's settings or launcher credentials.
    $configPath = Join-Path (Split-Path -Parent $client.path) 'ClientConfig.ini'
    $serverLine = @(Get-Content -LiteralPath $configPath | Where-Object { $_ -match '^\s*Server\s*=' })
    if ($serverLine.Count -ne 1) { throw 'Expected one Server= entry; pass -ServerAddress explicitly.' }
    $addresses = foreach ($endpoint in (($serverLine[0] -split '=',2)[1] -split ';')) {
        $endpoint = $endpoint.Trim()
        if ($endpoint -match '^\[([^\]]+)\]:\d+$') { $Matches[1] }
        elseif ($endpoint -match '^([^:]+):\d+$') { $Matches[1] }
        elseif ($endpoint) { $endpoint }
    }
    $ServerAddress = @($addresses | Sort-Object -Unique)
    $source = "$configPath Server= (configured addresses; live routing not yet verified)"
}
if (-not $ServerAddress) { throw 'No server address found.' }
$ServerAddress = @(foreach ($address in $ServerAddress) {
    $parsedAddress = $null
    if (-not [Net.IPAddress]::TryParse($address, [ref]$parsedAddress)) {
        throw "Expected an IP address, got '$address'. Resolve the server hostname before capturing."
    }
    $parsedAddress.ToString()
})
$ServerAddress = @($ServerAddress | Sort-Object -Unique)

if (-not $Interface) {
    $Interface = @(foreach ($address in $ServerAddress) {
        if ([Net.IPAddress]::IsLoopback([Net.IPAddress]::Parse($address))) {
            '\Device\NPF_Loopback'
        } else {
            $route = @(Find-NetRoute -RemoteIPAddress $address)
            $indexes = @($route | Select-Object -ExpandProperty InterfaceIndex -Unique)
            if ($indexes.Count -ne 1) { throw "Cannot choose a route to $address; pass -Interface." }
            $adapter = Get-NetAdapter -InterfaceIndex $indexes[0]
            $adapterGuid = $adapter.InterfaceGuid.ToString().Trim('{}')
            "\Device\NPF_{$adapterGuid}"
        }
    })
    $Interface = @($Interface | Sort-Object -Unique)
}
$knownInterfaces = @(& $Dumpcap -D)
if ($LASTEXITCODE -ne 0) { throw 'dumpcap could not enumerate interfaces.' }
foreach ($device in $Interface) {
    if (-not @($knownInterfaces | Where-Object { ($_ -split ' ')[1] -eq $device }).Count) {
        throw "Capture interface unavailable: $device. Run dumpcap -D to list device paths."
    }
}
$filter = 'udp and (' + (($ServerAddress | ForEach-Object { "host $_" }) -join ' or ') + ')'
if ($Port) { $filter += ' and (' + (($Port | ForEach-Object { "port $_" }) -join ' or ') + ')' }
foreach ($device in $Interface) {
    $null = & $Dumpcap -i $device -f $filter -d
    if ($LASTEXITCODE -ne 0) { throw "Capture filter failed validation on $device." }
}

$runId = $Label + '-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$null = New-Item -ItemType Directory -Path $OutputRoot -Force
$runDir = (New-Item -ItemType Directory -Path (Join-Path $OutputRoot $runId)).FullName
$capturePath = Join-Path $runDir 'traffic.pcapng'
$manifestPath = Join-Path $runDir 'session.json'
$eventsPath = Join-Path $runDir 'events.jsonl'
$arguments = @('-p','-s','0','-B','64','-f',$filter)
foreach ($device in $Interface) { $arguments += @('-i',$device) }
$arguments += @('-a',"duration:$DurationSeconds",'-a',('filesize:' + ($MaxMiB * 1024)),'-q','-w',$capturePath)
$manifest = [ordered]@{
    schemaVersion = 1
    runId = $runId
    createdUtc = [DateTimeOffset]::UtcNow.ToString('o')
    status = 'prepared'
    referenceClient = $client
    serverAddresses = $ServerAddress
    addressSource = $source
    captureInterfaces = $Interface
    captureFilter = $filter
    durationLimitSeconds = $DurationSeconds
    sizeLimitMiB = $MaxMiB
    captureFile = 'traffic.pcapng'
    dumpcapVersion = (@(& $Dumpcap -v)[0])
    dumpcapArguments = $arguments
    clientAlreadyRunning = [bool]@(Get-Process -Name H1Z1 -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $client.path }).Count
    protocolStatus = 'unidentified; raw UDP is not decoded application messages'
}
Write-JsonFile $manifestPath $manifest
$event = [ordered]@{ utc = [DateTimeOffset]::UtcNow.ToString('o'); kind = 'setup'; text = 'Capture prepared; no gameplay observation recorded.' }
$event | ConvertTo-Json -Compress | Set-Content -LiteralPath $eventsPath -Encoding UTF8
Write-Host "Run directory: $runDir"
Write-Host "Client build: $($client.fileVersion)"
Write-Host "Filter: $filter"
Write-Host "Interfaces: $($Interface -join ', ')"
if ($PrepareOnly) { Write-Host 'Prepared only; capture has not started.'; return }

$manifest.status = 'capture-invoked'
$manifest['captureInvokedUtc'] = [DateTimeOffset]::UtcNow.ToString('o')
Write-JsonFile $manifestPath $manifest
Write-Host "Starting capture. Connect only after dumpcap reports the capture file in dumpcap.stderr.log."
Write-Host "Stops after $DurationSeconds seconds or $MaxMiB MiB; Ctrl+C stops it earlier."
try {
    # Windows PowerShell represents native stderr status lines as ErrorRecords.
    # dumpcap logs normal capture startup there; judge failure by its exit code.
    $ErrorActionPreference = 'Continue'
    & $Dumpcap @arguments 2>&1 | ForEach-Object {
        $statusLine = $_.ToString()
        Add-Content -LiteralPath (Join-Path $runDir 'dumpcap.stderr.log') -Value $statusLine -Encoding UTF8
        Write-Host $statusLine
    }
    $ErrorActionPreference = 'Stop'
    $manifest['dumpcapExitCode'] = $LASTEXITCODE
    $manifest.status = if ($LASTEXITCODE -eq 0) { 'capture-ended' } else { 'capture-failed' }
} finally {
    $ErrorActionPreference = 'Stop'
    $manifest['endedUtc'] = [DateTimeOffset]::UtcNow.ToString('o')
    if ($manifest.status -eq 'capture-invoked') { $manifest.status = 'capture-interrupted; inspect file and dumpcap log' }
    if (Test-Path -LiteralPath $capturePath) {
        $manifest['captureBytes'] = (Get-Item -LiteralPath $capturePath).Length
        $manifest['captureSha256'] = (Get-FileHash -LiteralPath $capturePath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    Write-JsonFile $manifestPath $manifest
    Write-Host "Evidence saved: $runDir"
}
if ($LASTEXITCODE -ne 0) { throw 'dumpcap failed; inspect dumpcap.stderr.log.' }
