using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Cranberry.Launcher.Core;

public static partial class BinocularScopeFix
{
    public const long ExeSize = 72_818_304;
    public const string ExeSha256 = "D949D39F45074F2B223257477803A8858B4970242C6963DF9A213A169D8929DD";
    public const string DisableVariable = "CRANBERRY_BINOCULAR_SCOPE_FIX";
    private const string ClientLog = "H1Z1 KOTK PlayClient (Live).log";

    /// <summary>Call off the UI thread with the Process returned by this launch; never find by name.</summary>
    public static async Task ApplyAfterStartup(Process game, string directory, Action<string> log)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Environment.GetEnvironmentVariable(DisableVariable) == "0")
        {
            log("Airborne binocular scope helper disabled.");
            return;
        }
        try
        {
            if (!Environment.Is64BitProcess) throw new InvalidOperationException("The August helper requires a 64-bit launcher.");
            _ = game.SafeHandle; // Retain the launch identity across all waits.
            string expected = GameInstaller.SafePath(directory, "H1Z1.exe");
            using (var disk = File.OpenRead(expected)) VerifyDisk(disk);
            long started = new DateTimeOffset(game.StartTime.ToUniversalTime()).ToUnixTimeSeconds();
            using var handle = OpenProcess(0x410, false, game.Id);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            long created = CreationTime(handle);
            if (created != game.StartTime.ToFileTimeUtc()) throw new IOException("The launch process identity changed.");
            var elapsed = Stopwatch.StartNew();
            (long Player, long Controller, long Reticle)? previous = null;
            TimeSpan stableSince = default;
            string logPath = Path.Combine(directory, "Logs", ClientLog);
            while (elapsed.Elapsed < TimeSpan.FromMinutes(30))
            {
                if (game.HasExited) return;
                (long Player, long Controller, long Reticle)? ready = null;
                long image = 0;
                try
                {
                    var module = game.MainModule;
                    if (module is not null && string.Equals(Path.GetFullPath(module.FileName), expected, StringComparison.OrdinalIgnoreCase))
                    {
                        image = module.BaseAddress.ToInt64();
                        if (IsRunning(logPath, started)) ready = ReadyIdentity((a, n) => Read(handle, a, n), image);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or Win32Exception) { }
                if (ready is null || ready != previous) { previous = ready; stableSince = elapsed.Elapsed; }
                else if (elapsed.Elapsed - stableSince >= TimeSpan.FromSeconds(3))
                {
                    // Shared with ScopeProcess.patch_lock in the optional Python watcher.
                    using var mutex = new Mutex(false, @"Local\Cranberry.BinocularScope." + game.Id);
                    bool owned;
                    try { owned = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned) throw new IOException("Another binocular helper is updating this client.");
                    try
                    {
                        // Keep the reverified original disk image open against replacement
                        // until all live writes and readback finish. Nothing is written to disk.
                        using var disk = File.OpenRead(expected);
                        VerifyDisk(disk);
                        using var write = OpenProcess(0x438, false, game.Id);
                        if (write.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                        if (game.HasExited || CreationTime(write) != created || !IsRunning(logPath, started)
                            || ReadyIdentity((a, n) => Read(write, a, n), image) != ready)
                            throw new IOException("The launched world changed before the scope update.");
                        var protections = new Dictionary<long, uint>();
                        bool changed;
                        try
                        {
                            changed = ApplyVerified(() => Read(write, image + FunctionRva, FunctionSize),
                                (index, from, to) => WriteByte(write, image, index, from, to, protections));
                        }
                        finally
                        {
                            var errors = new List<Exception>();
                            foreach (var page in protections)
                            {
                                bool restored = VirtualProtectEx(write, (nint)page.Key, 4096, page.Value, out _)
                                    || VirtualProtectEx(write, (nint)page.Key, 4096, page.Value, out _);
                                bool flushed = FlushInstructionCache(write, (nint)page.Key, 4096);
                                if (!restored || !flushed) errors.Add(new IOException("Scope page protection/cache cleanup failed."));
                            }
                            if (errors.Count != 0) throw new AggregateException(errors);
                        }
                        log(changed ? "Airborne binocular scope enabled; complete SecondaryFire verified, original executable retained on disk."
                            : "Airborne binocular scope is already enabled; complete SecondaryFire verified.");
                        return;
                    }
                    finally { mutex.ReleaseMutex(); }
                }
                await Task.Delay(1000).ConfigureAwait(false);
            }
            log("Airborne binocular scope timed out waiting for an initialized world; nothing changed.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or Win32Exception
            or InvalidOperationException or UnauthorizedAccessException or AggregateException)
        {
            log("Airborne binocular scope could not complete: " + ex.Message);
        }
    }

    internal static void VerifyDisk(Stream disk)
    {
        if (disk.Length != ExeSize || disk.Position != 0 || Convert.ToHexString(SHA256.HashData(disk)) != ExeSha256)
            throw new InvalidDataException("Binocular scope requires the verified original August executable.");
    }

    private static bool IsRunning(string path, long started)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        input.Seek(Math.Max(0, input.Length - 2 * 1024 * 1024), SeekOrigin.Begin);
        using var reader = new StreamReader(input, Encoding.UTF8);
        // Only the log parser is reused; the rejected HeadLookBlendWeight patch is never invoked.
        return CameraOnlyLook.IsRunningLog(reader.ReadToEnd(), started);
    }

    internal static (long Player, long Controller, long Reticle)? ReadyIdentity(Func<long, int, byte[]> read, long image)
    {
        long Pointer(long address) => BitConverter.ToInt64(read(address, 8));
        long client = Pointer(image + 0x3F696A0), manager = Pointer(image + 0x3F69430), reticle = Pointer(image + 0x3F6A200);
        if (client == 0 || manager == 0 || reticle == 0) return null;
        long player = Pointer(manager + 0x1948), controller = Pointer(client + 0x321A0);
        if (player == 0 || controller == 0 || Pointer(player) != image + 0x31DDDC0
            || (Pointer(controller) != image + 0x315A458 && Pointer(controller) != image + 0x315AEC0)) return null;
        long datasource = Pointer(reticle + 0x88);
        return datasource != 0 && Pointer(datasource) == image + 0x3250158 ? (player, controller, reticle) : null;
    }

    private static long CreationTime(SafeProcessHandle handle)
    {
        if (!GetProcessTimes(handle, out long created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return created;
    }

    private static byte[] Read(SafeProcessHandle handle, long address, int size)
    {
        if (address < 0x10000 || address >= (1L << 47) - size || size is < 1 or > FunctionSize)
            throw new InvalidDataException("Invalid bounded scope read.");
        var bytes = new byte[size];
        if (!ReadProcessMemory(handle, (nint)address, bytes, (nuint)size, out nuint count) || count != (nuint)size)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return bytes;
    }

    private static void WriteByte(SafeProcessHandle handle, long image, int index, byte from, byte to,
        Dictionary<long, uint> protections)
    {
        bool allowed = index switch
        {
            EntryIndex => (from == 0x33 && to == 0x41) || (from == 0x41 && to == 0x33),
            ReleaseIndex => (from == 0x74 && to == 0xEB) || (from == 0xEB && to == 0x74),
            _ => false,
        };
        if (!allowed || Read(handle, image + FunctionRva + index, 1)[0] != from)
            throw new InvalidDataException("Unexpected scope byte transition.");
        nint address = (nint)(image + FunctionRva + index);
        long page = (long)address & ~4095L;
        if (!VirtualProtectEx(handle, address, 1, 0x40, out uint protection)) throw new Win32Exception(Marshal.GetLastWin32Error());
        protections.TryAdd(page, protection);
        protection = protections[page]; // Keep the original across write/cleanup/rollback failures.
        Exception? failure = null;
        try
        {
            if (!WriteProcessMemory(handle, address, [to], 1, out nuint count) || count != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            bool restored = VirtualProtectEx(handle, address, 1, protection, out _)
                || VirtualProtectEx(handle, address, 1, protection, out _);
            bool flushed = FlushInstructionCache(handle, address, 1);
            if (restored && flushed) protections.Remove(page);
            if (!restored || !flushed) failure = new IOException("Scope memory protection/cache cleanup failed.", failure);
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
