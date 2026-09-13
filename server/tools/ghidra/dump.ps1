<#
.SYNOPSIS
    Lock-protected headless Ghidra decompile dump (one Ghidra at a time per project).

.DESCRIPTION
    Wraps analyzeHeadless + DumpClassMethods.java. Several agents may want decompiles at once,
    but the Ghidra project C:\Aug2017\ghidra takes a single lock, so this script serialises
    them with an atomic directory lock (C:\Aug2017\ghidra\headless.lock), waiting up to
    -WaitMinutes. Targets use the DumpClassMethods syntax:
        '@140b055c0'            callees of one function (depth = -Depth)
        '@140b055c0+140a1ff40'  several roots
        'callers:140b055c0'     callers
        'refs:143f696d0'        code referencing an address
        'SomeNeedle'            symbol / string needle
    Output: one .c per function under -Out, plus index.txt with the call tree.

.EXAMPLE
    powershell -File C:\Aug2017\Server\tools\ghidra\dump.ps1 -Targets '@140b055c0' -Out C:\Aug2017\out\ghidra-aug\my-topic -Depth 2
#>
param(
    [Parameter(Mandatory = $true)] [string] $Targets,
    [Parameter(Mandatory = $true)] [string] $Out,
    [int] $Depth = 1,
    [int] $WaitMinutes = 20,
    [string] $Ghidra = 'C:\Ghidra\ghidra_12.1.2_PUBLIC\support\analyzeHeadless.bat',
    [string] $Project = 'C:\Aug2017\ghidra'
)

$ErrorActionPreference = 'Stop'

# -Out must be absolute. DumpClassMethods.java does 'new File(args[1], folder)', which the
# Ghidra JVM resolves against its own working directory (the repository root), so a relative
# -Out silently creates stray needle-named directories there instead of writing the dump
# (S1 6, S7 0.5 -- six such directories were removed on 2026-09-02).
if (-not [System.IO.Path]::IsPathRooted($Out)) {
    throw "-Out must be an absolute path (got '$Out'). A relative -Out is resolved against the Ghidra JVM's working directory and leaves stray directories at the repository root; pass a rooted path such as C:\Aug2017\out\ghidra-aug\<topic>."
}

$lock = Join-Path $Project 'headless.lock'
$deadline = (Get-Date).AddMinutes($WaitMinutes)
while ($true) {
    try {
        [System.IO.Directory]::CreateDirectory($lock) | Out-Null
        # CreateDirectory succeeds silently on an existing directory; test ownership by a marker.
        $marker = Join-Path $lock 'owner.txt'
        if (-not (Test-Path $marker)) {
            Set-Content -Path $marker -Value "$PID $(Get-Date -Format s) $Targets"
            Start-Sleep -Milliseconds 200
            if ((Get-Content $marker | Select-Object -First 1) -like "$PID *") { break }
        } else {
            # Stale lock (owner gone)?
            $ownerPid = (Get-Content $marker | Select-Object -First 1).Split(' ')[0]
            if (-not (Get-Process -Id $ownerPid -ErrorAction SilentlyContinue)) {
                Remove-Item -Recurse -Force $lock
                continue
            }
        }
    } catch { }
    if ((Get-Date) -gt $deadline) { throw "could not acquire $lock within $WaitMinutes min" }
    Start-Sleep -Seconds 5
}

try {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $log = Join-Path (Split-Path $Out -Parent) ("headless-" + (Split-Path $Out -Leaf) + "-$stamp.log")
    & $Ghidra $Project aug -process H1Z1.exe -noanalysis -readOnly `
        -scriptPath 'C:\Aug2017\Server\tools\ghidra' `
        -postScript DumpClassMethods.java $Targets $Out $Depth *> $log
    Write-Host "[dump] $Targets -> $Out (depth $Depth); log $log"
    Get-ChildItem $Out -Directory | ForEach-Object { Write-Host "[dump]   $($_.Name): $((Get-ChildItem $_.FullName -Filter *.c).Count) functions" }
} finally {
    Remove-Item -Recurse -Force $lock -ErrorAction SilentlyContinue
}
