param(
    [Parameter(Mandatory = $true)][string]$Package,
    [Parameter(Mandatory = $true)][string]$Publisher,
    [Parameter(Mandatory = $true)][string]$Evidence
)
$ErrorActionPreference = 'Stop'
$Package = [IO.Path]::GetFullPath($Package).TrimEnd([IO.Path]::DirectorySeparatorChar)
$Publisher = [IO.Path]::GetFullPath($Publisher)
$Evidence = [IO.Path]::GetFullPath($Evidence)
if (-not (Test-Path -LiteralPath $Publisher -PathType Leaf)) { throw 'Build CommunityRelease first and supply its DLL path.' }
if (-not (Test-Path -LiteralPath $Package -PathType Container)) { throw 'Supply a built local package.' }
if (Test-Path -LiteralPath $Evidence) { throw 'Choose a new GUI smoke evidence directory.' }
if ($Evidence.StartsWith($Package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Smoke evidence and fixture keys must be outside the input package.'
}
$inventory = Get-Content -LiteralPath (Join-Path $Package 'community-files.json') -Raw | ConvertFrom-Json
if ($inventory.Count -lt 1 -or $inventory.Count -ge 20000) { throw 'Invalid source package inventory.' }
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
# Use only the recorded immutable application files. Never follow a source junction or copy user state.
foreach ($file in $inventory) {
    $relative = [string]$file.path
    if (-not $relative -or $relative.Contains('\') -or $relative.Contains(':') -or [IO.Path]::IsPathRooted($relative) -or
        @($relative.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0 -or
        $relative -eq 'community-files.json' -or -not $names.Add($relative)) { throw 'Unsafe source inventory path.' }
    $source = [IO.Path]::GetFullPath((Join-Path $Package $relative))
    if (-not $source.StartsWith($Package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Source inventory path escapes the package.'
    }
    $current = $source
    while ($current) {
        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Source package links are forbidden.' }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    if ((Get-Item -LiteralPath $source).Length -ne $file.size -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -cne $file.sha256) {
        throw "Source package identity changed: $relative"
    }
}
New-Item -ItemType Directory -Path $Evidence | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
function InvokePublisher([string[]]$Arguments) {
    & dotnet $Publisher @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Offline fixture publisher failed.' }
}
$private = Join-Path $Evidence 'fixture-keys/private.pem'
$public = Join-Path $Evidence 'fixture-keys/public.pem'
InvokePublisher @('keygen', '--private-key', $private, '--public-key', $public)
$publicKey = ((Get-Content -LiteralPath $public -Raw) -replace '-----BEGIN PUBLIC KEY-----|-----END PUBLIC KEY-----|\s', '')
$folders = @()
for ($sequence = 1; $sequence -le 3; $sequence++) {
    $folder = Join-Path $Evidence (@('seed', 'good', 'broken')[$sequence - 1])
    New-Item -ItemType Directory -Path $folder | Out-Null
    foreach ($file in $inventory) {
        $target = Join-Path $folder $file.path
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $Package $file.path) -Destination $target
    }
    $settings = [ordered]@{
        schema = 1; repository = 'Mixcria/h1z1-pre-season-5-project'; publicKey = $publicKey
        includePrereleases = $true; sequence = $sequence; version = "0.2.0-gui-smoke.$sequence"
    }
    [IO.File]::WriteAllText((Join-Path $folder 'community-update.json'), ($settings | ConvertTo-Json), $utf8)
    if ($sequence -eq 3) {
        # Only this synthetic copy is broken. Its inventory/signature still describe its exact bytes.
        [IO.File]::WriteAllText((Join-Path $folder 'runtime/Cranberry.Host.exe'), 'GUI smoke fixture: intentionally not a PE executable.', $utf8)
    }
    InvokePublisher @('inventory', '--package', $folder)
    $folders += $folder
}
$goodRelease = Join-Path $Evidence 'signed-good'
$brokenRelease = Join-Path $Evidence 'signed-broken'
InvokePublisher @('sign', '--package', $folders[1], '--output', $goodRelease, '--private-key', $private,
    '--sequence', '2', '--version', '0.2.0-gui-smoke.2')
InvokePublisher @('sign', '--package', $folders[2], '--output', $brokenRelease, '--private-key', $private,
    '--sequence', '3', '--version', '0.2.0-gui-smoke.3')
$result = [ordered]@{
    schema = 1; createdUtc = [DateTimeOffset]::UtcNow.ToString('O'); sourcePackage = $Package
    seedPackage = $folders[0]; goodPackage = $folders[1]; brokenPackage = $folders[2]
    goodReleaseDirectory = $goodRelease; brokenReleaseDirectory = $brokenRelease
    publicKey = $publicKey; repository = 'Mixcria/h1z1-pre-season-5-project'; includePrereleases = $true
    fixtureOnly = $true; note = 'Synthetic GUI updater fixtures signed with a disposable key. Never publish these packages.'
}
[IO.File]::WriteAllText((Join-Path $Evidence 'fixtures.json'), ($result | ConvertTo-Json -Depth 4), $utf8)
Write-Host "GUI smoke fixtures prepared: $(Join-Path $Evidence 'fixtures.json'). Nothing was published."
