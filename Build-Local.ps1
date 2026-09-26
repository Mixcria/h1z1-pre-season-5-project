param(
    [string]$Output,
    [string]$Version,
    [switch]$Zip,
    [long]$UpdateSequence = 0,
    [string]$UpdatePublicKeyFile,
    [switch]$StableUpdates
)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
if (-not $Version) { $Version = (Get-Content -LiteralPath (Join-Path $repo 'VERSION') -Raw).Trim() }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw 'Use a version such as 0.1.0-preview.1.' }
if ($UpdateSequence -lt 0 -or $UpdateSequence -gt 999999999999) { throw 'UpdateSequence must be zero (developer build) or a positive release sequence below one trillion.' }
if ($UpdateSequence -gt 0 -and -not $UpdatePublicKeyFile) { throw 'Player updates require an explicit dedicated community public key file.' }
$updatePublicKey = ''
if ($UpdatePublicKeyFile) {
    $publicPem = Get-Content -LiteralPath $UpdatePublicKeyFile -Raw
    if ($publicPem -notmatch '\A-----BEGIN PUBLIC KEY-----\s+([A-Za-z0-9+/=\r\n]+)\s+-----END PUBLIC KEY-----\s*\z') {
        throw 'UpdatePublicKeyFile must contain only a public SPKI PEM created by CommunityRelease keygen.'
    }
    $updatePublicKey = $Matches[1] -replace '\s', ''
    [void][Convert]::FromBase64String($updatePublicKey)
}
if (-not $Output) { $Output = Join-Path $repo 'artifacts\Cranberry-Local' }
$Output = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $Output) { throw 'Choose a new output directory to keep existing releases intact.' }
$artifacts = $Output + '-build'
dotnet publish (Join-Path $repo 'server\src\Cranberry.Host\Cranberry.Host.csproj') -c Release -r win-x64 --self-contained true --artifacts-path (Join-Path $artifacts 'server') -o (Join-Path $Output 'runtime') -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Server publish failed.' }
dotnet publish (Join-Path $repo 'launcher\src\Cranberry.Launcher\Cranberry.Launcher.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:LauncherRelease=0 --artifacts-path (Join-Path $artifacts 'launcher') -o $Output -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Launcher publish failed.' }
Copy-Item -LiteralPath (Join-Path $repo 'package') -Destination (Join-Path $Output 'package') -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'compatibility\dynamicAppearance.bin') -Destination (Join-Path $Output 'runtime\Data\dynamicAppearance.bin')
Copy-Item -LiteralPath (Join-Path $repo 'PLAYER-GUIDE.txt') -Destination (Join-Path $Output 'READ-ME.txt')
Copy-Item -LiteralPath (Join-Path $repo 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $Output 'THIRD-PARTY-NOTICES.md')
$baseline = Get-Content -LiteralPath (Join-Path $repo 'provenance\source-baseline.json') -Raw | ConvertFrom-Json
$definition = [ordered]@{
    Version = 1
    ReleaseId = $Version + '-local-' + $baseline.sourceId
    GameManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $Output 'package\game-manifest.json') -Algorithm SHA256).Hash
    AppearanceSha256 = (Get-FileHash -LiteralPath (Join-Path $Output 'runtime\Data\dynamicAppearance.bin') -Algorithm SHA256).Hash
}
[IO.File]::WriteAllText((Join-Path $Output 'local-edition.json'), ($definition | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
$update = [ordered]@{
    schema = 1
    repository = 'Mixcria/h1z1-pre-season-5-project'
    publicKey = $updatePublicKey
    includePrereleases = -not $StableUpdates
    sequence = $UpdateSequence
    version = $Version
}
[IO.File]::WriteAllText((Join-Path $Output 'community-update.json'), ($update | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
dotnet run --project (Join-Path $repo 'tools\CommunityRelease\CommunityRelease.csproj') -c Release --artifacts-path (Join-Path $artifacts 'publisher') -- inventory --package $Output
if ($LASTEXITCODE -ne 0) { throw 'Package inventory and community update configuration validation failed.' }
if ($Zip) {
    $zipPath = $Output + '.zip'
    if (Test-Path -LiteralPath $zipPath) { throw 'Release ZIP already exists.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($Output, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $true)
    Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
}
Write-Host "Local edition built: $Output"
