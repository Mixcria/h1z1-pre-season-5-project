using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Cranberry.Launcher.Core;

/// <summary>
/// Unsuccessful experiment retained for native diagnostics. Automatic activation
/// was removed after the September 8 playtest; the owner deferred Alt-look changes.
/// August camera-driven HeadLookBlendWeight: one checked displacement byte makes
/// its existing MOVSS read the nearby 0.0f instead of 1.0f. Camera, locomotion,
/// weapon aiming and packet code, and the executable on disk, are untouched.
/// This disables the head-look animation blend; visual aiming still needs a playtest.
/// See docs/news-hotbar-freelook-20260908.md for native evidence and playtest limits.
/// </summary>
public static class CameraOnlyLook
{
    public const long FunctionRva = 0xC69200;
    public const int FunctionSize = 0x660;
    public const int PatchIndex = 0x5F6;
    public const byte Original = 0x8E, Patched = 0x7A;
    public const string FunctionSha256 = "D87893D8EB0E129E625A4C826A5714F89D5F387C1B137AAC122204D917124DC0";
    private const string ExeSha256 = "D949D39F45074F2B223257477803A8858B4970242C6963DF9A213A169D8929DD";

    public static bool IsPatched(ReadOnlySpan<byte> function)
    {
        if (function.Length != FunctionSize || function[PatchIndex] is not (Original or Patched))
            throw new InvalidDataException("Unrecognized August head-look function.");
        byte[] normalized = function.ToArray();
        normalized[PatchIndex] = Original;
        if (Convert.ToHexString(SHA256.HashData(normalized)) != FunctionSha256)
            throw new InvalidDataException("August head-look function changed; no patch applied.");
        return function[PatchIndex] == Patched;
    }

    public static bool IsRunningLog(string text, long startedUnix)
    {
        string? last = null;
        foreach (string line in text.Split('\n'))
        {
            if (!line.Contains("TransitionClientRunState:")) continue;
            string[] fields = line.Split('\t');
            if (fields.Length >= 4 && long.TryParse(fields[3], out long start)
                && Math.Abs((double)start - startedUnix) <= 2)
            {
                int at = line.LastIndexOf("newState=", StringComparison.Ordinal);
                last = at < 0 ? null : line[(at + 9)..].Trim();
            }
        }
        return last == "cClientRunStateRunning";
    }

    // This completes after one successful write. There is no in-game polling or server work.
    public static async Task ApplyAfterStartup(Process game, string directory, Action<string> log)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            string expected = GameInstaller.SafePath(directory, "H1Z1.exe");
            using (var disk = File.OpenRead(expected))
                if (disk.Length != 72_818_304 || Convert.ToHexString(await SHA256.HashDataAsync(disk)) != ExeSha256)
                    throw new InvalidDataException("Camera-only look requires the original August executable.");
            long started = new DateTimeOffset(game.StartTime.ToUniversalTime()).ToUnixTimeSeconds();
            long image = 0;
            using var handle = OpenProcess(0x410, false, game.Id);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var elapsed = Stopwatch.StartNew();
            (long Player, long Controller)? previous = null;
            TimeSpan stableSince = default;
            while (elapsed.Elapsed < TimeSpan.FromMinutes(10))
            {
                if (game.HasExited) return;
                (long Player, long Controller)? ready = null;
                try
                {
                    var module = game.MainModule;
                    if (module is not null && string.Equals(Path.GetFullPath(module.FileName), expected, StringComparison.OrdinalIgnoreCase))
                    {
                        image = module.BaseAddress.ToInt64();
                        string path = Path.Combine(directory, "Logs", "H1Z1 KOTK PlayClient (Live).log");
                        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        input.Seek(Math.Max(0, input.Length - 2 * 1024 * 1024), SeekOrigin.Begin);
                        using var reader = new StreamReader(input, Encoding.UTF8);
                        if (IsRunningLog(await reader.ReadToEndAsync(), started))
                        {
                            long client = Pointer(handle, image + 0x3F696A0);
                            long manager = Pointer(handle, image + 0x3F69430);
                            if (client != 0 && manager != 0)
                            {
                                long player = Pointer(handle, manager + 0x1948);
                                long controller = Pointer(handle, client + 0x321A0);
                                if (player != 0 && controller != 0 && Pointer(handle, player) == image + 0x31DDDC0
                                    && (Pointer(handle, controller) == image + 0x315A458 || Pointer(handle, controller) == image + 0x315AEC0))
                                    ready = (player, controller);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or Win32Exception) { }
                if (ready is null || ready != previous) { previous = ready; stableSince = elapsed.Elapsed; }
                else if (elapsed.Elapsed - stableSince >= TimeSpan.FromSeconds(3))
                {
                    if (IsPatched(Read(handle, image + FunctionRva, FunctionSize))) { log("Camera-only look already applied."); return; }
                    if (BitConverter.ToSingle(Read(handle, image + 0x30EF074, 4)) != 0f
                        || BitConverter.ToSingle(Read(handle, image + 0x30EF088, 4)) != 1f)
                        throw new InvalidDataException("August animation constants differ.");
                    ApplyByte(game, image);
                    log("Camera-only look applied; original executable retained on disk.");
                    return;
                }
                await Task.Delay(1000);
            }
            log("Camera-only look timed out waiting for an initialized world; nothing changed.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            log("Camera-only look could not complete: " + ex.Message);
        }
    }

    private static long Pointer(SafeProcessHandle handle, long address) => BitConverter.ToInt64(Read(handle, address, 8));

    private static byte[] Read(SafeProcessHandle handle, long address, int size)
    {
        if (address < 0x10000 || address >= (1L << 47) - size || size is < 1 or > 4096)
            throw new InvalidDataException("Invalid bounded client read.");
        var bytes = new byte[size];
        if (!ReadProcessMemory(handle, (nint)address, bytes, (nuint)size, out nuint read) || read != (nuint)size)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return bytes;
    }

    private static void ApplyByte(Process game, long image)
    {
        // The optional local Python watcher uses the same lock. Both launch paths
        // may observe a new game; page-protection changes must never overlap.
        using var mutex = new Mutex(false, @"Local\Cranberry.CameraOnlyLook." + game.Id);
        bool owned;
        try { owned = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) throw new IOException("Another camera-look helper is still applying the fix.");
        try { ApplyByteLocked(game, image); }
        finally { mutex.ReleaseMutex(); }
    }

    private static void ApplyByteLocked(Process game, long image)
    {
        // The supplied Process retains its launch handle, so a recycled PID cannot pass HasExited.
        using var write = OpenProcess(0x438, false, game.Id);
        if (write.IsInvalid || game.HasExited) throw new InvalidOperationException("The launched game is no longer available.");
        if (IsPatched(Read(write, image + FunctionRva, FunctionSize))) return;
        nint address = (nint)(image + FunctionRva + PatchIndex);
        if (!VirtualProtectEx(write, address, 1, 0x40, out uint protection))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Exception? failure = null;
        try
        {
            if (!WriteProcessMemory(write, address, [Patched], 1, out nuint count) || count != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!IsPatched(Read(write, image + FunctionRva, FunctionSize)))
                throw new InvalidDataException("Head-look write failed verification.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or Win32Exception)
        {
            failure = ex;
            // Roll back only our recognized byte; never overwrite somebody else's change.
            if (IsPatched(Read(write, image + FunctionRva, FunctionSize)))
            {
                if (!WriteProcessMemory(write, address, [Original], 1, out nuint restored) || restored != 1
                    || IsPatched(Read(write, image + FunctionRva, FunctionSize)))
                    failure = new IOException("Head-look write and rollback failed verification.", ex);
            }
        }
        finally
        {
            bool protectedAgain = VirtualProtectEx(write, address, 1, protection, out _)
                || VirtualProtectEx(write, address, 1, protection, out _);
            if (!FlushInstructionCache(write, address, 1) || !protectedAgain)
                failure = new IOException("Head-look memory protection/cache cleanup failed.", failure);
        }
        if (failure is not null) throw failure;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, [Out] byte[] bytes, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] bytes, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(SafeProcessHandle process, nint address, nuint size, uint protect, out uint previous);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(SafeProcessHandle process, nint address, nuint size);
}
