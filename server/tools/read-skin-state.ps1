param(
    [int] $ProcessId = 0,
    [switch] $Watch,
    [int] $IntervalMilliseconds = 250
)

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public sealed class ProcessMemoryReader : IDisposable
{
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr process, UIntPtr address, byte[] buffer, UIntPtr size, out UIntPtr bytesRead);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private IntPtr handle;

    public ProcessMemoryReader(int processId)
    {
        handle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, processId);
        if (handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private byte[] Read(ulong address, int size)
    {
        var bytes = new byte[size];
        UIntPtr read;
        if (!ReadProcessMemory(handle, (UIntPtr)address, bytes, (UIntPtr)size, out read)
            || read.ToUInt64() != (ulong)size)
            throw new Win32Exception(
                Marshal.GetLastWin32Error(), string.Format("ReadProcessMemory 0x{0:X}", address));
        return bytes;
    }

    public byte ReadByte(ulong address) { return Read(address, 1)[0]; }
    public uint ReadUInt32(ulong address) { return BitConverter.ToUInt32(Read(address, 4), 0); }
    public ulong ReadUInt64(ulong address) { return BitConverter.ToUInt64(Read(address, 8), 0); }

    public void Dispose()
    {
        if (handle != IntPtr.Zero) CloseHandle(handle);
        handle = IntPtr.Zero;
    }
}
'@

if ($ProcessId -eq 0) {
    $game = Get-Process -Name H1Z1 -ErrorAction Stop | Select-Object -First 1
} else {
    $game = Get-Process -Id $ProcessId -ErrorAction Stop
}

$moduleBase = [uint64]$game.MainModule.BaseAddress.ToInt64()
$clientGlobal = $moduleBase + 0x3F69F60

function Resolve-SkinCategory([ProcessMemoryReader] $memory, [uint64] $definitions, [uint32] $category) {
    if ($definitions -eq 0) { return @(0, 0) }
    $prototypeNode = $memory.ReadUInt64($definitions + 0x4B8 + (8 * ($category % 0x7F)))
    for ($guard = 0; $prototypeNode -ne 0 -and $guard -lt 10000; $guard++) {
        if ($memory.ReadUInt32($prototypeNode + 0x18) -eq $category) { break }
        $prototypeNode = $memory.ReadUInt64($prototypeNode + 0x20)
    }
    if ($prototypeNode -eq 0) { return @(0, 0) }

    $slotKey = $memory.ReadUInt32($prototypeNode)
    $slotDefinition = $memory.ReadUInt64($definitions + 0x60 + (8 * ($slotKey % 0x7F)))
    for ($guard = 0; $slotDefinition -ne 0 -and $guard -lt 10000; $guard++) {
        if ($memory.ReadUInt32($slotDefinition + 0xD8) -eq $slotKey) { break }
        $slotDefinition = $memory.ReadUInt64($slotDefinition + 0xE0)
    }
    if ($slotDefinition -eq 0) { return @(0, 0) }

    $slotItem = $memory.ReadUInt64($slotDefinition + 0x40)
    for ($guard = 0; $slotItem -ne 0 -and $guard -lt 10000; $guard++) {
        if ($memory.ReadUInt32($slotItem + 8) -eq $category) { break }
        $slotItem = $memory.ReadUInt64($slotItem + 0x20)
    }
    return @($slotDefinition, $slotItem)
}

function Resolve-Conversion([ProcessMemoryReader] $memory, [uint64] $conversions, [uint32] $accountItemId) {
    if ($conversions -eq 0) { return @(0, 0, 0) }
    $node = $memory.ReadUInt64($conversions + 0x60 + (8 * ($accountItemId % 0x101)))
    for ($guard = 0; $node -ne 0 -and $guard -lt 10000; $guard++) {
        if ($memory.ReadUInt32($node + 0x48) -eq $accountItemId) { break }
        $node = $memory.ReadUInt64($node + 0x50)
    }
    if ($node -eq 0) { return @(0, 0, 0) }
    return @($node, $memory.ReadUInt32($node + 0x10), $memory.ReadUInt32($node + 0x1C))
}

function Resolve-ItemDefinition(
    [ProcessMemoryReader] $memory,
    [uint64] $definitions,
    [uint32] $itemDefinitionId,
    [uint32] $invalidItemDefinitionId) {
    if ($definitions -eq 0) { return @(0, 0, 0, 0) }

    $node = $memory.ReadUInt64($definitions + 0x1C8 + (8 * ($itemDefinitionId -band 0x3FF)))
    for ($guard = 0; $node -ne 0 -and $guard -lt 10000; $guard++) {
        if ($memory.ReadUInt32($node + 0x260) -eq $itemDefinitionId) { break }
        $node = $memory.ReadUInt64($node + 0x268)
    }
    if ($node -eq 0) { return @(0, 0, 0, 0) }

    $primary = $memory.ReadUInt32($node + 0x158)
    $fallback = $memory.ReadUInt32($node + 0x15C)
    $effective = if ([int32]$primary -gt [int32]$invalidItemDefinitionId) { $primary } else { $fallback }
    return @($node, $primary, $fallback, $effective)
}

function Show-State([ProcessMemoryReader] $memory) {
    $client = $memory.ReadUInt64($clientGlobal)
    if ($client -eq 0) {
        Write-Output "client=<not initialized>"
        return
    }

    $manager = $client + 0xF2F8
    $available = $client + 0xDA98
    $selected = $memory.ReadUInt32($manager)
    $current = $memory.ReadUInt32($manager + 8)
    $editorCollection = $memory.ReadUInt32($manager + 0x180)
    $slotType = $memory.ReadUInt32($manager + 0x184)
    $slotId = $memory.ReadUInt32($manager + 0x188)
    $target = $memory.ReadUInt32($manager + 0x18C)
    $gear = $memory.ReadByte($manager + 0x248)
    $weapons = $memory.ReadByte($manager + 0x249)
    $currentRows = $memory.ReadUInt32($manager + 0x48)
    $collectionCount = $memory.ReadUInt32($manager + 0x140)
    $availableCount = $memory.ReadUInt32($available + 8)
    $availableItemMapCount = $memory.ReadUInt32($available + 0x60)
    $availablePreviewMapCount = $memory.ReadUInt32($available + 0x180)
    $skinDefinitions = $memory.ReadUInt64($moduleBase + 0x3F697D0)
    $conversionDefinitions = $memory.ReadUInt64($moduleBase + 0x3F698C0)
    $itemDefinitions = $memory.ReadUInt64($moduleBase + 0x3F69EB0)
    $uiOwner = $memory.ReadUInt64($moduleBase + 0x3F69430)
    $uiContext = if ($uiOwner -ne 0) { $memory.ReadUInt64($uiOwner + 0x1948) } else { 0 }
    $invalidItemDefinitionId = $memory.ReadUInt32($moduleBase + 0x3F85B18)
    $specialItemDefinitionId = $memory.ReadUInt32($moduleBase + 0x3CDFF80)

    Write-Output ("pid={0} module=0x{1:X} client=0x{2:X} manager=0x{3:X}" -f $game.Id, $moduleBase, $client, $manager)
    Write-Output ("selected={0} current={1} editorCollection={2} slotType={3} slotId={4} target={5} gear={6} weapons={7}" -f $selected, $current, $editorCollection, $slotType, $slotId, $target, $gear, $weapons)
    Write-Output ("currentRows={0} collectionCount={1} availableBaseCount={2} availableItemMap={3} availablePreviewMap={4}" -f
        $currentRows, $collectionCount, $availableCount, $availableItemMapCount, $availablePreviewMapCount)
    Write-Output ("uiOwner=0x{0:X} uiContext=0x{1:X} itemDefinitions=0x{2:X} invalidItem={3} specialItem={4}" -f
        $uiOwner, $uiContext, $itemDefinitions, $invalidItemDefinitionId, $specialItemDefinitionId)
    if ($itemDefinitions -ne 0) {
        Write-Output ("itemDefinitions firstQword=0x{0:X}" -f $memory.ReadUInt64($itemDefinitions))
    }

    $seen = @{}
    for ($bucket = 0; $bucket -lt 5; $bucket++) {
        $node = $memory.ReadUInt64($manager + 0x148 + (8 * $bucket))
        while ($node -ne 0 -and -not $seen.ContainsKey($node)) {
            $seen[$node] = $true
            $id = $memory.ReadUInt32($node + 0x128)
            $rowCount = $memory.ReadUInt32($node + 0x40)
            $rowHead = $memory.ReadUInt64($node + 0x30)
            Write-Output ("collection[{0}] id={1} rows={2} head=0x{3:X}" -f $bucket, $id, $rowCount, $rowHead)
            if ($rowHead -ne 0) {
                $category = $memory.ReadUInt32($rowHead)
                $accountItem = $memory.ReadUInt32($rowHead + 0x10)
                $categoryResult = Resolve-SkinCategory $memory $skinDefinitions $category
                $conversionResult = Resolve-Conversion $memory $conversionDefinitions $accountItem
                $itemResult = Resolve-ItemDefinition $memory $itemDefinitions ([uint32]$conversionResult[1]) $invalidItemDefinitionId
                $reward = [uint32]$conversionResult[1]
                $bucketHead = if ($itemDefinitions -ne 0) {
                    $memory.ReadUInt64($itemDefinitions + 0x1C8 + (8 * ($reward -band 0x3FF)))
                } else { 0 }
                $bucketKeys = [System.Collections.Generic.List[uint32]]::new()
                for ($probe = $bucketHead; $probe -ne 0 -and $bucketKeys.Count -lt 8; $probe = $memory.ReadUInt64($probe + 0x268)) {
                    $bucketKeys.Add($memory.ReadUInt32($probe + 0x260))
                }
                Write-Output ("  first category={0} guid=0x{1:X} accountItem={2} flags=0x{3:X2}" -f
                    $category,
                    $memory.ReadUInt64($rowHead + 8),
                    $accountItem,
                    $memory.ReadByte($rowHead + 0x14))
                Write-Output ("  resolves slotDefinition=0x{0:X} slotItem=0x{1:X} conversion=0x{2:X} reward={3} aux={4}" -f
                    [uint64]$categoryResult[0], [uint64]$categoryResult[1],
                    [uint64]$conversionResult[0], [uint32]$conversionResult[1],
                    [uint32]$conversionResult[2])
                Write-Output ("  rewardDefinition=0x{0:X} primary={1} fallback={2} effective={3}" -f
                    [uint64]$itemResult[0], [uint32]$itemResult[1], [uint32]$itemResult[2],
                    [uint32]$itemResult[3])
                Write-Output ("  rewardBucket=0x{0:X} keys=[{1}]" -f $bucketHead, ($bucketKeys -join ','))
            }
            $node = $memory.ReadUInt64($node + 0x130)
        }
    }
}

$memory = [ProcessMemoryReader]::new($game.Id)
try {
    $lastSnapshot = $null
    do {
        try {
            $snapshot = (Show-State $memory) -join [Environment]::NewLine
            if (-not $Watch -or $snapshot -ne $lastSnapshot) {
                if ($Watch) {
                    Write-Output (Get-Date -Format 'HH:mm:ss.fff')
                }
                Write-Output $snapshot
                $lastSnapshot = $snapshot
            }
        } catch {
            Write-Output $_.Exception.Message
        }
        if ($Watch) {
            Start-Sleep -Milliseconds $IntervalMilliseconds
        }
    } while ($Watch -and -not $game.HasExited)
} finally {
    $memory.Dispose()
}
