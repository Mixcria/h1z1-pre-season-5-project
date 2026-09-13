<#
.SYNOPSIS
    Verifies the current Cranberry worktree without starting, stopping, or changing a live host.

.DESCRIPTION
    This is the offline hand-off gate for a Cranberry change:

      1. Records the source/worktree and Debug-artifact freshness before and after verification.
      2. Builds the solution, then runs the full offline test suite from those exact build outputs.
      3. Runs the capture archive's fatal-packet regression guard and summarizes one capture
         (the newest wire capture unless -Capture is supplied).
      4. Writes a new, timestamped JSON report under out\verification. Existing reports and
         every file under captures\ are read only; this script never starts/stops a host or client.

    Live harness tests are deliberately suppressed by default even if CRANBERRY_HARNESS_LIVE is set
    in the calling shell. They are an explicit, separately prepared experiment; use
    -IncludeLiveHarness only when a suitable host is already running and that environment variable
    has deliberately been set to 1.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File C:\Aug2017\Server\tools\verify-current.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File C:\Aug2017\Server\tools\verify-current.ps1 `
        -Capture wire-20260831-101011.txt

.EXAMPLE
    # Inspect a capture and write a freshness report, without compiling or running tests.
    powershell -ExecutionPolicy Bypass -File C:\Aug2017\Server\tools\verify-current.ps1 `
        -SkipBuild -SkipTests -Capture 20260831-101011
#>
[CmdletBinding()]
param(
    [string] $RepositoryRoot,
    [string] $CaptureRoot,
    # A full path, a wire-*.txt file name, or a capture stamp such as 20260831-101011.
    [string] $Capture,
    [switch] $SkipBuild,
    [switch] $SkipTests,
    [switch] $SkipCaptureSummary,
    # Opt-in only. The caller must also deliberately set CRANBERRY_HARNESS_LIVE=1.
    [switch] $IncludeLiveHarness
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Status {
    param([string] $Message)
    Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss.fff'), $Message)
}

function Get-RelativePath {
    param(
        [string] $Path,
        [string] $BasePath
    )

    $base = [System.IO.Path]::GetFullPath($BasePath).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $full = [System.IO.Path]::GetFullPath($Path)
    if ($full.StartsWith($base, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $full.Substring($base.Length)
    }

    return $full
}

function Get-SourceFiles {
    param(
        [string[]] $Roots,
        [string] $Repository,
        [switch] $IncludeSolution
    )

    $files = New-Object System.Collections.Generic.List[System.IO.FileInfo]
    foreach ($root in $Roots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) {
            continue
        }

        Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
            $_.FullName -notmatch '\\(bin|obj)\\' -and
            $_.Extension -in @('.cs', '.csproj', '.props', '.targets', '.json', '.resx')
        } | ForEach-Object { [void] $files.Add($_) }
    }

    foreach ($name in @('global.json', 'Directory.Build.props', 'Directory.Build.targets', 'NuGet.Config')) {
        $candidate = Join-Path $Repository $name
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            [void] $files.Add((Get-Item -LiteralPath $candidate))
        }
    }

    if ($IncludeSolution) {
        $solution = Join-Path $Repository 'Cranberry.slnx'
        if (Test-Path -LiteralPath $solution -PathType Leaf) {
            [void] $files.Add((Get-Item -LiteralPath $solution))
        }
    }

    return @($files | Sort-Object FullName -Unique)
}

function Get-SourceSnapshot {
    param(
        [string[]] $Roots,
        [string] $Repository,
        [switch] $IncludeSolution
    )

    $files = @(Get-SourceFiles -Roots $Roots -Repository $Repository -IncludeSolution:$IncludeSolution)
    $latest = $files | Sort-Object LastWriteTimeUtc, FullName | Select-Object -Last 1
    $manifest = @(
        $files | ForEach-Object {
            "{0}|{1}|{2}" -f (Get-RelativePath -Path $_.FullName -BasePath $Repository), $_.Length, $_.LastWriteTimeUtc.Ticks
        }
    ) -join "`n"
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $fingerprint = ([System.BitConverter]::ToString($sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($manifest)))).Replace('-', '')
    } finally {
        $sha256.Dispose()
    }

    return [pscustomobject] [ordered]@{
        file_count = $files.Count
        newest_file = if ($latest) { Get-RelativePath -Path $latest.FullName -BasePath $Repository } else { $null }
        newest_write_utc = if ($latest) { $latest.LastWriteTimeUtc.ToString('o') } else { $null }
        metadata_fingerprint_sha256 = $fingerprint
    }
}

function Get-ArtifactState {
    param(
        [string] $Name,
        [string] $Path,
        [object] $InputSnapshot
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject] [ordered]@{
            name = $Name
            path = $Path
            exists = $false
            status = 'missing'
            length = $null
            write_utc = $null
            newest_input_utc = $InputSnapshot.newest_write_utc
        }
    }

    $file = Get-Item -LiteralPath $Path
    $inputTime = if ($InputSnapshot.newest_write_utc) { [DateTime]::Parse($InputSnapshot.newest_write_utc).ToUniversalTime() } else { [DateTime]::MinValue }
    $status = if ($file.LastWriteTimeUtc -ge $inputTime) { 'fresh' } else { 'stale' }
    return [pscustomobject] [ordered]@{
        name = $Name
        path = $Path
        exists = $true
        status = $status
        length = $file.Length
        write_utc = $file.LastWriteTimeUtc.ToString('o')
        newest_input_utc = $InputSnapshot.newest_write_utc
    }
}

function Get-ArtifactSnapshot {
    param([string] $Repository)

    $src = Get-SourceSnapshot -Repository $Repository -Roots @((Join-Path $Repository 'src'))
    $unit = Get-SourceSnapshot -Repository $Repository -Roots @(
        (Join-Path $Repository 'src'),
        (Join-Path $Repository 'tests\Cranberry.Tests')
    )
    $harness = Get-SourceSnapshot -Repository $Repository -Roots @(
        (Join-Path $Repository 'src\Cranberry.Transport'),
        (Join-Path $Repository 'tests\Cranberry.Harness'),
        (Join-Path $Repository 'tests\Cranberry.Harness.Tests')
    )

    $hostZone = Join-Path $Repository 'src\Cranberry.Host\bin\Debug\net10.0\Cranberry.Zone.dll'
    $zone = Join-Path $Repository 'src\Cranberry.Zone\bin\Debug\net10.0\Cranberry.Zone.dll'
    $copyStatus = 'missing'
    if ((Test-Path -LiteralPath $hostZone -PathType Leaf) -and (Test-Path -LiteralPath $zone -PathType Leaf)) {
        $hostZoneFile = Get-Item -LiteralPath $hostZone
        $zoneFile = Get-Item -LiteralPath $zone
        $copyStatus = if ($hostZoneFile.LastWriteTimeUtc -ge $zoneFile.LastWriteTimeUtc) { 'fresh' } else { 'stale'
        }
    }

    return [pscustomobject] [ordered]@{
        inputs = [ordered]@{
            source = $src
            unit_tests = $unit
            harness_tests = $harness
        }
        artifacts = @(
            Get-ArtifactState -Name 'host executable' -Path (Join-Path $Repository 'src\Cranberry.Host\bin\Debug\net10.0\Cranberry.Host.exe') -InputSnapshot $src
            Get-ArtifactState -Name 'host Zone copy' -Path $hostZone -InputSnapshot $src
            Get-ArtifactState -Name 'Zone assembly' -Path $zone -InputSnapshot $src
            Get-ArtifactState -Name 'unit-test assembly' -Path (Join-Path $Repository 'tests\Cranberry.Tests\bin\Debug\net10.0\Cranberry.Tests.dll') -InputSnapshot $unit
            Get-ArtifactState -Name 'harness-test assembly' -Path (Join-Path $Repository 'tests\Cranberry.Harness.Tests\bin\Debug\net10.0\Cranberry.Harness.Tests.dll') -InputSnapshot $harness
        )
        host_zone_copy_status = $copyStatus
    }
}

function Test-TestAssembliesFresh {
    param([object] $Artifacts)

    $tests = @($Artifacts.artifacts | Where-Object { $_.name -in @('unit-test assembly', 'harness-test assembly') })
    return ($tests.Count -eq 2 -and @($tests | Where-Object { $_.status -ne 'fresh' }).Count -eq 0)
}

function Invoke-Native {
    param(
        [string] $Name,
        [string] $FilePath,
        [string[]] $Arguments
    )

    Write-Status ("running {0}: {1} {2}" -f $Name, $FilePath, ($Arguments -join ' '))
    $started = [DateTime]::UtcNow
    # A failing native command writes stderr, which PowerShell can otherwise promote to a
    # terminating NativeCommandError under the script-wide Stop preference. Capture its result
    # instead so the report still includes the capture analysis and the caller gets one outcome.
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $FilePath @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    $finished = [DateTime]::UtcNow
    foreach ($line in $output) {
        Write-Host $line
    }

    return [pscustomobject] [ordered]@{
        name = $Name
        command = "$FilePath $($Arguments -join ' ')"
        exit_code = $exitCode
        started_utc = $started.ToString('o')
        finished_utc = $finished.ToString('o')
        duration_seconds = [Math]::Round(($finished - $started).TotalSeconds, 3)
        output_tail = @($output | ForEach-Object { $_.ToString() } | Select-Object -Last 120)
    }
}

function Get-GitState {
    param([string] $Repository)

    $head = @(& git -C $Repository rev-parse --short HEAD 2>$null)
    $headExit = $LASTEXITCODE
    $status = @(& git -C $Repository status --porcelain=v1 2>$null)
    $statusExit = $LASTEXITCODE
    return [pscustomobject] [ordered]@{
        available = ($headExit -eq 0 -and $statusExit -eq 0)
        head = if ($headExit -eq 0) { ($head | Select-Object -First 1).ToString() } else { $null }
        changed_count = if ($statusExit -eq 0) { $status.Count } else { $null }
        changed_sample = if ($statusExit -eq 0) { @($status | Select-Object -First 30 | ForEach-Object { $_.ToString() }) } else { @() }
    }
}

function Resolve-Capture {
    param(
        [string] $Root,
        [string] $Requested
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return $null
    }

    if ([string]::IsNullOrWhiteSpace($Requested)) {
        return Get-ChildItem -LiteralPath $Root -Filter 'wire-*.txt' -File |
            Sort-Object LastWriteTimeUtc, Name |
            Select-Object -Last 1
    }

    $candidates = New-Object System.Collections.Generic.List[string]
    [void] $candidates.Add($Requested)
    [void] $candidates.Add((Join-Path $Root $Requested))
    if ($Requested -notmatch '\.txt$') {
        [void] $candidates.Add((Join-Path $Root ("wire-{0}.txt" -f $Requested)))
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return Get-Item -LiteralPath $candidate
        }
    }

    return $null
}

function Get-CaptureReport {
    param(
        [string] $Repository,
        [string] $Root,
        [string] $Requested,
        [switch] $SkipSummary
    )

    $allCaptures = if (Test-Path -LiteralPath $Root -PathType Container) {
        @(Get-ChildItem -LiteralPath $Root -Filter 'wire-*.txt' -File)
    } else {
        @()
    }
    $capture = Resolve-Capture -Root $Root -Requested $Requested
    if (-not $capture) {
        return [pscustomobject] [ordered]@{
            capture_root = $Root
            archive_count = $allCaptures.Count
            status = 'unavailable'
            selected = $null
            summary = $null
            changed_while_read = $null
        }
    }

    $before = Get-Item -LiteralPath $capture.FullName
    $report = [ordered]@{
        capture_root = $Root
        archive_count = $allCaptures.Count
        status = 'metadata only'
        selected = [ordered]@{
            path = $capture.FullName
            length = $before.Length
            write_utc = $before.LastWriteTimeUtc.ToString('o')
        }
        summary = $null
        changed_while_read = $false
    }

    if (-not $SkipSummary) {
        $python = Get-Command python -ErrorAction SilentlyContinue | Select-Object -First 1
        $parser = Join-Path $Repository 'tools\capture\capture.py'
        if ($python -and (Test-Path -LiteralPath $parser -PathType Leaf)) {
            $summary = Invoke-Native -Name 'capture summary' -FilePath $python.Source -Arguments @($parser, 'summary', $capture.FullName)
            $report.status = if ($summary.exit_code -eq 0) { 'summarized' } else { 'summary failed' }
            $report.summary = $summary
        } elseif (-not $python) {
            $report.status = 'python unavailable'
        } else {
            $report.status = 'capture parser unavailable'
        }
    }

    $after = Get-Item -LiteralPath $capture.FullName
    $report.changed_while_read = ($before.Length -ne $after.Length -or $before.LastWriteTimeUtc -ne $after.LastWriteTimeUtc)
    if ($report.changed_while_read) {
        $report.status = "$($report.status); capture changed while read"
    }

    return [pscustomobject] $report
}

function Write-NewReport {
    param(
        [string] $Repository,
        [object] $Report
    )

    $directory = Join-Path $Repository 'out\verification'
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    $json = $Report | ConvertTo-Json -Depth 12
    $encoding = New-Object System.Text.UTF8Encoding($false)
    $baseName = 'verify-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')

    for ($i = 0; $i -lt 100; $i++) {
        $suffix = if ($i -eq 0) { '' } else { "-$i" }
        $path = Join-Path $directory ($baseName + $suffix + '.json')
        try {
            $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
        } catch [System.IO.IOException] {
            continue
        }

        try {
            $writer = New-Object System.IO.StreamWriter($stream, $encoding)
            try {
                $writer.Write($json)
            } finally {
                $writer.Dispose()
            }
        } finally {
            # StreamWriter.Dispose owns the stream; this is only needed if its construction failed.
            if ($stream) { $stream.Dispose() }
        }

        return $path
    }

    throw "Could not allocate a new verification report under $directory"
}

$started = [DateTime]::UtcNow
$originalLiveHarness = if (Test-Path Env:\CRANBERRY_HARNESS_LIVE) { $env:CRANBERRY_HARNESS_LIVE } else { $null }
$report = [ordered]@{
    schema = 'cranberry.verify-current/v1'
    started_utc = $started.ToString('o')
    repository = $null
    worktree = $null
    source_before = $null
    source_after_build = $null
    source_after_tests = $null
    artifacts_before = $null
    artifacts_after_build = $null
    commands = @()
    capture = $null
    live_harness = [ordered]@{
        included = [bool] $IncludeLiveHarness
        inherited_value = $originalLiveHarness
    }
    outcome = $null
}

$verificationFailure = $false
$sourceChangedDuringVerification = $false
$testsWereSkippedForFreshness = $false

try {
    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
        $RepositoryRoot = Split-Path -Parent $PSScriptRoot
    }
    if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) {
        throw "Repository root does not exist: $RepositoryRoot"
    }

    $RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
    if (-not $CaptureRoot) {
        $CaptureRoot = Join-Path (Split-Path -Parent $RepositoryRoot) 'captures'
    }
    if (Test-Path -LiteralPath $CaptureRoot -PathType Container) {
        $CaptureRoot = (Resolve-Path -LiteralPath $CaptureRoot).Path
    }

    $solution = Join-Path $RepositoryRoot 'Cranberry.slnx'
    if (-not (Test-Path -LiteralPath $solution -PathType Leaf)) {
        throw "Solution not found: $solution"
    }

    Push-Location -LiteralPath $RepositoryRoot
    try {
        $report.repository = $RepositoryRoot
        $report.worktree = Get-GitState -Repository $RepositoryRoot
        $report.source_before = Get-SourceSnapshot -Repository $RepositoryRoot -Roots @(
            (Join-Path $RepositoryRoot 'src'),
            (Join-Path $RepositoryRoot 'tests')
        ) -IncludeSolution
        $report.artifacts_before = Get-ArtifactSnapshot -Repository $RepositoryRoot

        Write-Status ("worktree: HEAD={0}; changed={1}; source files={2}; newest={3} ({4})" -f `
            $report.worktree.head, $report.worktree.changed_count, $report.source_before.file_count,
            $report.source_before.newest_file, $report.source_before.newest_write_utc)
        Write-Status ("artifacts before: host Zone copy={0}; unit tests={1}; harness tests={2}" -f `
            $report.artifacts_before.host_zone_copy_status,
            ($report.artifacts_before.artifacts | Where-Object { $_.name -eq 'unit-test assembly' } | Select-Object -ExpandProperty status),
            ($report.artifacts_before.artifacts | Where-Object { $_.name -eq 'harness-test assembly' } | Select-Object -ExpandProperty status))

        if ($SkipBuild) {
            Write-Status 'build skipped by request'
        } else {
            $build = Invoke-Native -Name 'solution build' -FilePath 'dotnet' -Arguments @('build', $solution, '-nologo', '--verbosity', 'minimal')
            $report.commands += $build
            if ($build.exit_code -ne 0) {
                $verificationFailure = $true
            }
        }

        $report.source_after_build = Get-SourceSnapshot -Repository $RepositoryRoot -Roots @(
            (Join-Path $RepositoryRoot 'src'),
            (Join-Path $RepositoryRoot 'tests')
        ) -IncludeSolution
        $report.artifacts_after_build = Get-ArtifactSnapshot -Repository $RepositoryRoot
        Write-Status ("artifacts after build: host Zone copy={0}; unit tests={1}; harness tests={2}" -f `
            $report.artifacts_after_build.host_zone_copy_status,
            ($report.artifacts_after_build.artifacts | Where-Object { $_.name -eq 'unit-test assembly' } | Select-Object -ExpandProperty status),
            ($report.artifacts_after_build.artifacts | Where-Object { $_.name -eq 'harness-test assembly' } | Select-Object -ExpandProperty status))

        $buildSucceeded = $SkipBuild -or (($report.commands | Where-Object { $_.name -eq 'solution build' } | Select-Object -Last 1).exit_code -eq 0)
        if ($SkipTests) {
            Write-Status 'tests skipped by request'
        } elseif (-not $buildSucceeded) {
            $testsWereSkippedForFreshness = $true
            $verificationFailure = $true
            Write-Status 'tests not run: the current solution build failed, so --no-build could exercise stale assemblies'
        } elseif (-not (Test-TestAssembliesFresh -Artifacts $report.artifacts_after_build)) {
            $testsWereSkippedForFreshness = $true
            $verificationFailure = $true
            Write-Status 'tests not run: one or more test assemblies are missing or stale; refusing to run --no-build'
        } else {
            if (-not $IncludeLiveHarness) {
                Remove-Item Env:\CRANBERRY_HARNESS_LIVE -ErrorAction SilentlyContinue
                Write-Status 'live harness disabled for this offline verification run'
            } elseif ($env:CRANBERRY_HARNESS_LIVE -ne '1') {
                throw 'IncludeLiveHarness requires CRANBERRY_HARNESS_LIVE=1 to be set deliberately by the caller.'
            }

            $tests = Invoke-Native -Name 'offline test suite' -FilePath 'dotnet' -Arguments @('test', $solution, '--no-build', '--no-restore', '-nologo', '--verbosity', 'minimal')
            $report.commands += $tests
            if ($tests.exit_code -ne 0) {
                $verificationFailure = $true
            }

            # The full suite already includes this test. Re-running this small test with detailed
            # logging makes the archive-wide fatal-packet sweep visible in the hand-off report.
            $captureGuard = Invoke-Native -Name 'capture archive fatal guard' -FilePath 'dotnet' -Arguments @(
                'test',
                (Join-Path $RepositoryRoot 'tests\Cranberry.Harness.Tests\Cranberry.Harness.Tests.csproj'),
                '--no-build', '--no-restore', '-nologo', '--verbosity', 'minimal',
                '--filter', 'FullyQualifiedName~No_capture_outside_the_known_immutable_fixtures_carries_a_client_fatal_packet',
                '--logger', 'console;verbosity=detailed'
            )
            $report.commands += $captureGuard
            $captureGuardMatchedNoTests = ($captureGuard.output_tail -join "`n") -match 'No test matches the given testcase filter'
            if ($captureGuard.exit_code -ne 0 -or $captureGuardMatchedNoTests) {
                $verificationFailure = $true
                if ($captureGuardMatchedNoTests) {
                    Write-Status 'capture archive fatal guard matched no tests; refusing a false clean result'
                }
            }
        }

        $report.capture = Get-CaptureReport -Repository $RepositoryRoot -Root $CaptureRoot -Requested $Capture -SkipSummary:$SkipCaptureSummary
        if ($report.capture.status -eq 'unavailable') {
            Write-Status ("capture archive unavailable: {0}" -f $CaptureRoot)
            $verificationFailure = $true
        } else {
            Write-Status ("capture: {0}; archive={1}; status={2}; changed while read={3}" -f `
                $report.capture.selected.path, $report.capture.archive_count, $report.capture.status, $report.capture.changed_while_read)
            if (-not $SkipCaptureSummary -and $report.capture.status -ne 'summarized') {
                $verificationFailure = $true
                Write-Status 'capture summary did not complete; capture analysis is inconclusive'
            }
            if ($report.capture.changed_while_read) {
                $verificationFailure = $true
                Write-Status 'capture changed while it was read; choose a completed capture with -Capture before accepting this gate'
            }
        }

        $report.source_after_tests = Get-SourceSnapshot -Repository $RepositoryRoot -Roots @(
            (Join-Path $RepositoryRoot 'src'),
            (Join-Path $RepositoryRoot 'tests')
        ) -IncludeSolution
        $sourceChangedDuringVerification = (
            $report.source_before.metadata_fingerprint_sha256 -ne $report.source_after_tests.metadata_fingerprint_sha256
        )
        if ($sourceChangedDuringVerification) {
            $verificationFailure = $true
            Write-Status 'source changed during verification; build/test results are not a clean hand-off gate'
        }
    } finally {
        Pop-Location
    }
} catch {
    $verificationFailure = $true
    $report.error = $_.Exception.Message
    Write-Host "[verify] ERROR: $($_.Exception.Message)" -ForegroundColor Red
} finally {
    if ($null -eq $originalLiveHarness) {
        Remove-Item Env:\CRANBERRY_HARNESS_LIVE -ErrorAction SilentlyContinue
    } else {
        $env:CRANBERRY_HARNESS_LIVE = $originalLiveHarness
    }

    $finished = [DateTime]::UtcNow
    $report.finished_utc = $finished.ToString('o')
    $report.duration_seconds = [Math]::Round(($finished - $started).TotalSeconds, 3)
    $report.outcome = [ordered]@{
        passed = (-not $verificationFailure)
        source_changed_during_verification = $sourceChangedDuringVerification
        tests_skipped_for_freshness = $testsWereSkippedForFreshness
    }

    try {
        $reportRepository = if ($report.repository) { $report.repository } else { $RepositoryRoot }
        $reportPath = Write-NewReport -Repository $reportRepository -Report ([pscustomobject] $report)
        Write-Status "report: $reportPath"
    } catch {
        $verificationFailure = $true
        Write-Host "[verify] ERROR writing report: $($_.Exception.Message)" -ForegroundColor Red
    }
}

if ($verificationFailure) {
    Write-Host '[verify] FAILED or INCONCLUSIVE; see the report above.' -ForegroundColor Red
    exit 1
}

Write-Host '[verify] PASSED: current build/tests and capture checks completed.' -ForegroundColor Green
