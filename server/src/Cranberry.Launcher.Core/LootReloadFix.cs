using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Cranberry.Launcher.Core;

/// <summary>
/// Keeps August's ordinary F interaction responsive during and between reloads. All
/// edits are verified against complete native functions and changed only after
/// the launched client's world is initialized. Firing keeps its native cancellation.
/// Provenance: tools/client/loot-during-reload-live.py, FUN_1411c6d70.
/// </summary>
public static partial class LootReloadFix
{
    public const long FunctionRva = 0x11C6D70;
    public const int FunctionSize = 468;
    public const int CancelIndex = 0x15C, AcknowledgementIndex = 0x10C;
    public const string FunctionSha256 = "A4250571C76AC95A827B1FA1CBEEC04C6107C0B62E86E4122D13C6CD5A14D769";
    private const string ExeSha256 = "D949D39F45074F2B223257477803A8858B4970242C6963DF9A213A169D8929DD";

    // 0 = original, 1 = cancellation fixed, 2 = cancellation and F acknowledgement fixed.
    // The existing local v3 helper has the same function as 2; its throttle is elsewhere.
    public static int Classify(ReadOnlySpan<byte> function)
    {
        if (function.Length != FunctionSize
            || function[CancelIndex] is not (0x77 or 0xEB)
            || function[AcknowledgementIndex] is not (0x75 or 0xEB)
            || function[CancelIndex] == 0x77 && function[AcknowledgementIndex] == 0xEB)
            throw new InvalidDataException("Unrecognized August interaction function.");
        byte[] original = function.ToArray();
        original[CancelIndex] = 0x77;
        original[AcknowledgementIndex] = 0x75;
        if (Convert.ToHexString(SHA256.HashData(original)) != FunctionSha256)
            throw new InvalidDataException("August interaction code differs from the reviewed build.");
        return function[AcknowledgementIndex] == 0xEB ? 2 : function[CancelIndex] == 0xEB ? 1 : 0;
    }

    /// <summary>Full-function verification and rollback around the two permitted byte writes.</summary>
    public static bool ApplyVerified(Func<byte[]> read, Action<int, byte, byte> write, bool restore = false)
    {
        byte[] before = read();
        int state = Classify(before);
        if (state == (restore ? 0 : 2)) return false;
        byte[] expected = before.ToArray();
        var attempted = new List<int>();
        int[] sites = restore ? [AcknowledgementIndex, CancelIndex] : [CancelIndex, AcknowledgementIndex];
        void Verify(byte[] bytes)
        {
            if (!read().AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("Interaction function changed during verification.");
        }
        try
        {
            foreach (int index in sites)
            {
                byte target = restore ? (index == CancelIndex ? (byte)0x77 : (byte)0x75) : (byte)0xEB;
                if (expected[index] == target) continue;
                Verify(expected);
                attempted.Add(index); // Include writers that mutate then report cleanup failure.
                write(index, expected[index], target);
                expected[index] = target;
                Verify(expected);
            }
            return true;
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (int index in attempted.AsEnumerable().Reverse())
            {
                try
                {
                    byte[] current = read();
                    Classify(current);
                    if (current[index] != before[index]) write(index, current[index], before[index]);
                }
                catch (Exception rollback) { errors.Add(rollback); }
            }
            try { Verify(before); }
            catch (Exception rollback) { errors.Add(rollback); }
            if (errors.Count > 1) throw new AggregateException("Interaction update and rollback failed.", errors);
            throw;
        }
    }

    public static async Task ApplyAfterStartup(Process game, string directory, Action<string> log)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            // Retain the launch handle before any wait; a reused PID cannot identify this game.
            _ = game.SafeHandle;
            string expectedPath = GameInstaller.SafePath(directory, "H1Z1.exe");
            using (var disk = File.OpenRead(expectedPath))
                if (disk.Length != 72_818_304 || Convert.ToHexString(await SHA256.HashDataAsync(disk).ConfigureAwait(false)) != ExeSha256)
                    throw new InvalidDataException("Reload interaction requires the verified August executable.");
            long started = new DateTimeOffset(game.StartTime.ToUniversalTime()).ToUnixTimeSeconds();
            using var process = OpenProcess(0x410, false, game.Id);
            if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            long created = CreationTime(process);
            var elapsed = Stopwatch.StartNew();
            (long Player, long Controller, long Reticle)? previous = null;
            TimeSpan stableSince = default;
            while (elapsed.Elapsed < TimeSpan.FromMinutes(30))
            {
                if (game.HasExited) return;
                (long Player, long Controller, long Reticle)? ready = null;
                long image = 0;
                try
                {
                    var module = game.MainModule;
                    if (module is not null && string.Equals(Path.GetFullPath(module.FileName), expectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        image = module.BaseAddress.ToInt64();
                        string path = Path.Combine(directory, "Logs", "H1Z1 KOTK PlayClient (Live).log");
                        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        input.Seek(Math.Max(0, input.Length - 2 * 1024 * 1024), SeekOrigin.Begin);
                        using var reader = new StreamReader(input, Encoding.UTF8);
                        if (CameraOnlyLook.IsRunningLog(await reader.ReadToEndAsync().ConfigureAwait(false), started))
                            ready = ReadyIdentity(process, image);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or Win32Exception) { }
                if (ready is null || ready != previous) { previous = ready; stableSince = elapsed.Elapsed; }
                else if (elapsed.Elapsed - stableSince >= TimeSpan.FromSeconds(3))
                {
                    using var mutex = new Mutex(false, @"Local\Cranberry.LootReload." + game.Id);
                    bool owned;
                    try { owned = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned) throw new IOException("Another reload helper is updating this client.");
                    try
                    {
                        using var write = OpenProcess(0x438, false, game.Id);
                        if (write.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                        if (game.HasExited || CreationTime(write) != created || ReadyIdentity(write, image) != ready)
                            throw new IOException("The launched client changed before the reload update.");
                        var protections = new Dictionary<long, uint>();
                        bool changed;
                        try
                        {
                            changed = ApplyAllVerified((rva, size) => Read(write, image + rva, size),
                                (rva, index, from, to) => WriteByte(write, image, rva, index, from, to, protections));
                        }
                        finally
                        {
                            foreach (var page in protections)
                                if (!VirtualProtectEx(write, (nint)page.Key, 4096, page.Value, out _)
                                    || !FlushInstructionCache(write, (nint)page.Key, 4096))
                                    throw new IOException("Could not restore the interaction page protection/cache.");
                        }
                        log(changed ? "Immediate F interaction and looting during reload enabled; firing still cancels reload."
                            : "Immediate F interaction and looting during reload are already enabled.");
                        return;
                    }
                    finally { mutex.ReleaseMutex(); }
                }
                await Task.Delay(1000).ConfigureAwait(false);
            }
            log("Reload interaction timed out waiting for the game world.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or Win32Exception
            or InvalidOperationException or UnauthorizedAccessException or AggregateException)
        {
            log("Reload interaction could not complete: " + ex.Message);
        }
    }

    private static (long Player, long Controller, long Reticle)? ReadyIdentity(SafeProcessHandle handle, long image)
    {
        long client = Pointer(handle, image + 0x3F696A0);
        long manager = Pointer(handle, image + 0x3F69430);
        long reticle = Pointer(handle, image + 0x3F6A200);
        if (client == 0 || manager == 0 || reticle == 0) return null;
        long player = Pointer(handle, manager + 0x1948), controller = Pointer(handle, client + 0x321A0);
        if (player == 0 || controller == 0 || Pointer(handle, player) != image + 0x31DDDC0
            || (Pointer(handle, controller) != image + 0x315A458 && Pointer(handle, controller) != image + 0x315AEC0)) return null;
        long datasource = Pointer(handle, reticle + 0x88);
        return datasource != 0 && Pointer(handle, datasource) == image + 0x3250158 ? (player, controller, reticle) : null;
    }

    private static long CreationTime(SafeProcessHandle handle)
    {
        if (!GetProcessTimes(handle, out long created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return created;
    }

    private static long Pointer(SafeProcessHandle handle, long address) => BitConverter.ToInt64(Read(handle, address, 8));

    private static byte[] Read(SafeProcessHandle handle, long address, int size)
    {
        if (address < 0x10000 || address >= (1L << 47) - size || size is < 1 or > FunctionSize)
            throw new InvalidDataException("Invalid bounded client read.");
        var bytes = new byte[size];
        if (!ReadProcessMemory(handle, (nint)address, bytes, (nuint)size, out nuint read) || read != (nuint)size)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return bytes;
    }

    private static void WriteByte(SafeProcessHandle handle, long image, long rva, int index, byte from, byte to,
        Dictionary<long, uint> protections)
    {
        var site = Sites.SingleOrDefault(s => s.Rva == rva && s.Index == index)
            ?? throw new InvalidDataException("Unexpected interaction patch location.");
        if (!((from == site.Original && to == site.Patched) || (from == site.Patched && to == site.Original))
            || Read(handle, image + rva + index, 1)[0] != from)
            throw new InvalidDataException("Unexpected reload patch transition.");
        nint address = (nint)(image + rva + index);
        if (!VirtualProtectEx(handle, address, 1, 0x40, out uint protection)) throw new Win32Exception(Marshal.GetLastWin32Error());
        long page = (long)address & ~4095L;
        protections.TryAdd(page, protection);
        protection = protections[page]; // Retain the original protection across a failed write/rollback.
        Exception? failure = null;
        try
        {
            if (!WriteProcessMemory(handle, address, [to], 1, out nuint count) || count != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            bool protectedAgain = VirtualProtectEx(handle, address, 1, protection, out _)
                || VirtualProtectEx(handle, address, 1, protection, out _);
            bool flushed = FlushInstructionCache(handle, address, 1);
            if (protectedAgain) protections.Remove(page);
            if (!protectedAgain || !flushed) failure = new IOException("Reload patch memory protection/cache cleanup failed.", failure);
        }
        if (failure is not null) throw failure;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, [Out] byte[] bytes, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] bytes, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualProtectEx(SafeProcessHandle process, nint address, nuint size, uint protect, out uint previous);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushInstructionCache(SafeProcessHandle process, nint address, nuint size);
}
