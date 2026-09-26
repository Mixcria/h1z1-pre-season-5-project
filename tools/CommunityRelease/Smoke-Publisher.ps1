param(
    [Parameter(Mandatory = $true)][string]$Publisher,
    [Parameter(Mandatory = $true)][string]$Evidence
)
$ErrorActionPreference = 'Stop'
$Publisher = [IO.Path]::GetFullPath($Publisher)
$Evidence = [IO.Path]::GetFullPath($Evidence)
if (-not (Test-Path -LiteralPath $Publisher -PathType Leaf)) { throw 'Build CommunityRelease first and supply its DLL path.' }
if (Test-Path -LiteralPath $Evidence) { throw 'Choose a new evidence directory.' }
New-Item -ItemType Directory -Path $Evidence | Out-Null
$checks = [Collections.Generic.List[string]]::new()
$utf8 = [Text.UTF8Encoding]::new($false)

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    $checks.Add($Message)
    Write-Host "PASS: $Message"
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function WriteJson([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12), $utf8)
}
function InvokePublisher([string]$Name, [string[]]$Arguments, [bool]$Success = $true) {
    $prior = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $message = (& dotnet $Publisher @Arguments 2>&1 | Out-String)
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $prior }
    Check (($code -eq 0) -eq $Success) $Name
    Check (-not $message.Contains('PRIVATE KEY-----')) "$Name does not print private key material"
}

$private = Join-Path $Evidence 'fixture-keys/signing.pem'
$public = Join-Path $Evidence 'fixture-keys/public.pem'
InvokePublisher 'Create disposable fixture key' @('keygen', '--private-key', $private, '--public-key', $public)
$privateHash = Hash $private
$publicHash = Hash $public
InvokePublisher 'Refuse key overwrite' @('keygen', '--private-key', $private, '--public-key', $public) $false
Check (((Hash $private) -eq $privateHash) -and ((Hash $public) -eq $publicHash)) 'Existing keys remain unchanged'
$publicKey = ((Get-Content -LiteralPath $public -Raw) -replace '-----BEGIN PUBLIC KEY-----|-----END PUBLIC KEY-----|\s', '')
$otherPrivate = Join-Path $Evidence 'fixture-keys/other.pem'
InvokePublisher 'Create independent wrong-key fixture' @('keygen', '--private-key', $otherPrivate, '--public-key', (Join-Path $Evidence 'fixture-keys/other-public.pem'))

function Fixture([string]$Name, [long]$Sequence = 3, [string]$Key = $publicKey) {
    $folder = Join-Path $Evidence $Name
    New-Item -ItemType Directory -Path $folder | Out-Null
    foreach ($relative in @('Cranberry.Launcher.exe', 'runtime/Cranberry.Host.exe', 'runtime/Data/dynamicAppearance.bin',
        'package/game-manifest.json', 'package/download-host.json', 'package/gameplay.defaults.json',
        'package/environment.defaults.json', 'READ-ME.txt')) {
        $path = Join-Path $folder $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
        [IO.File]::WriteAllText($path, "fixture $relative", $utf8)
    }
    WriteJson (Join-Path $folder 'local-edition.json') ([ordered]@{
        Version = 1; ReleaseId = '0.2.0-preview.1-local-fixture'
        GameManifestSha256 = Hash (Join-Path $folder 'package/game-manifest.json')
        AppearanceSha256 = Hash (Join-Path $folder 'runtime/Data/dynamicAppearance.bin')
    })
    WriteJson (Join-Path $folder 'community-update.json') ([ordered]@{
        schema = 1; repository = 'Mixcria/h1z1-pre-season-5-project'; publicKey = $Key
        includePrereleases = $true; sequence = $Sequence; version = '0.2.0-preview.1'
    })
    return $folder
}
function Sign([string]$Name, [string]$Package, [string]$Key = $private, [long]$Sequence = 3,
    [string]$Version = '0.2.0-preview.1', [string[]]$Extra = @(), [bool]$Success = $false, [string]$Output = '') {
    if (-not $Output) { $Output = Join-Path $Evidence $Name }
    InvokePublisher $Name (@('sign', '--package', $Package, '--output', $Output, '--private-key', $Key,
        '--sequence', "$Sequence", '--version', $Version) + $Extra) $Success
}

$package = Fixture 'valid-package'
InvokePublisher 'Create complete inventory' @('inventory', '--package', $package)
$inventoryPath = Join-Path $package 'community-files.json'
$inventoryHash = Hash $inventoryPath
$files = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
[string[]]$sortedPaths = @($files | ForEach-Object { $_.path })
[Array]::Sort($sortedPaths, [StringComparer]::Ordinal)
Check (($sortedPaths -join "`n") -ceq (($files | ForEach-Object { $_.path }) -join "`n")) 'Inventory paths use ordinal sorting'
Check ($files.Count -eq ((Get-ChildItem -LiteralPath $package -File -Recurse).Count - 1)) 'Inventory covers every file except itself'
foreach ($file in $files) {
    $path = Join-Path $package $file.path
    Check (((Hash $path) -ceq $file.sha256) -and ((Get-Item -LiteralPath $path).Length -eq $file.size)) "Inventory identity: $($file.path)"
}
InvokePublisher 'Refuse inventory overwrite' @('inventory', '--package', $package) $false
Check ((Hash $inventoryPath) -ceq $inventoryHash) 'Existing inventory is unchanged'
Sign 'signed-release' $package -Success $true
$output = Join-Path $Evidence 'signed-release'
$release = Get-Content -LiteralPath (Join-Path $output 'community-release.json') -Raw | ConvertFrom-Json
$archive = Join-Path $output 'Cranberry-Local-update.zip'
Check (($release.sha256 -ceq (Hash $archive)) -and ($release.size -eq (Get-Item -LiteralPath $archive).Length) -and
    ($release.filesSha256 -ceq $inventoryHash)) 'Signed metadata hashes actual archive and inventory bytes'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    Check ($zip.Entries.Count -eq ($files.Count + 1)) 'Flat update archive contains the exact file count'
    foreach ($entry in $zip.Entries) {
        $expectedPath = Join-Path $package $entry.FullName
        Check (Test-Path -LiteralPath $expectedPath -PathType Leaf) "Archive path has no enclosing directory: $($entry.FullName)"
        $stream = $entry.Open()
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
        finally { $stream.Dispose(); $algorithm.Dispose() }
        Check ($hash -ceq (Hash $expectedPath)) "Archive content: $($entry.FullName)"
    }
}
finally { $zip.Dispose() }
Check ((Hash $inventoryPath) -ceq $inventoryHash) 'Signing leaves the input inventory unchanged'
Sign 'Refuse-existing-output' $package -Output $output
Sign 'Refuse-wrong-private-key' $package -Key $otherPrivate
Sign 'Refuse-mismatched-sequence' $package -Sequence 4
Sign 'Refuse-mismatched-version' $package -Version '0.2.0-preview.2'
Sign 'Refuse-newer-bootstrap' $package -Extra @('--minimum-updater-version', '2')
Sign 'Refuse-incompatible-data-schema' $package -Extra @('--data-schema', '2')
Sign 'Refuse-nested-output' $package -Output (Join-Path $package 'nested-output')
$changed = Fixture 'changed-package'
InvokePublisher 'Inventory before mutation' @('inventory', '--package', $changed)
[IO.File]::WriteAllText((Join-Path $changed 'Cranberry.Launcher.exe'), 'changed', $utf8)
Sign 'Refuse-changed-file' $changed
$added = Fixture 'added-package'
InvokePublisher 'Inventory before unlisted file' @('inventory', '--package', $added)
[IO.File]::WriteAllText((Join-Path $added 'unlisted.txt'), 'extra', $utf8)
Sign 'Refuse-unlisted-file' $added
foreach ($test in @(@('private-key', 'mistake.pem'), @('state', 'state/account.json'), @('profile', 'launcher.json'),
    @('game', 'Game/H1Z1.exe'), @('accounts', 'data/local-accounts.json'))) {
    $folder = Fixture ('forbidden-' + $test[0])
    $path = Join-Path $folder $test[1]
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, 'private fixture', $utf8)
    InvokePublisher ('Refuse packaged ' + $test[0]) @('inventory', '--package', $folder) $false
}
$badHash = Fixture 'bad-hash'
[IO.File]::WriteAllText((Join-Path $badHash 'runtime/Data/dynamicAppearance.bin'), 'modified', $utf8)
InvokePublisher 'Refuse stale local edition content identity' @('inventory', '--package', $badHash) $false
$developer = Fixture 'developer-package' -Sequence 0 -Key ''
InvokePublisher 'Developer inventory needs no signing key' @('inventory', '--package', $developer)
Sign 'Refuse-signing-developer-identity' $developer
WriteJson (Join-Path $Evidence 'results.json') ([ordered]@{
    completedUtc = [DateTimeOffset]::UtcNow.ToString('O'); publisherSha256 = Hash $Publisher
    checks = $checks.ToArray(); realExecutablesLaunched = $false; networkUsed = $false
    keyNote = 'Disposable fixture keys only, stored outside packages. No release authority generated.'
})
Write-Host "All $($checks.Count) publisher checks passed. Nothing was published."
# GitHub's PowerShell wrapper propagates LASTEXITCODE. The final publisher call
# intentionally fails; all expected exit codes were asserted before reaching here.
$global:LASTEXITCODE = 0
