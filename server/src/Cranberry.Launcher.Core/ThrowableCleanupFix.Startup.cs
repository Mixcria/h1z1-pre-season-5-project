using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cranberry.Launcher.Core;

public static partial class ThrowableCleanupFix
{
    public static async Task ApplyAfterStartup(Process game, string directory, Action<string> log)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable(DisableVariable) == "0") return;
        try
        {
            if (!Environment.Is64BitProcess) throw new InvalidOperationException("The throwable cleanup helper requires a 64-bit launcher.");
            _ = game.SafeHandle;
            string expected = GameInstaller.SafePath(directory, "H1Z1.exe");
            using (var disk = File.OpenRead(expected)) BinocularScopeFix.VerifyDisk(disk);
            long started = new DateTimeOffset(game.StartTime.ToUniversalTime()).ToUnixTimeSeconds();
            using var handle = OpenProcess(0x410, false, game.Id);
            if (handle.IsInvalid) throw NativeError();
            var elapsed = Stopwatch.StartNew();
            (long Player, long Controller, long Reticle)? previous = null;
            TimeSpan stableSince = default;
            while (elapsed.Elapsed < TimeSpan.FromMinutes(30))
            {
                if (game.HasExited) return;
                long image = 0;
                (long Player, long Controller, long Reticle)? ready = null;
                try
                {
                    var module = game.MainModule;
                    if (module is not null && string.Equals(Path.GetFullPath(module.FileName), expected, StringComparison.OrdinalIgnoreCase))
                    {
                        image = module.BaseAddress.ToInt64();
                        string path = Path.Combine(directory, "Logs", "H1Z1 KOTK PlayClient (Live).log");
                        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        input.Seek(Math.Max(0, input.Length - 2 * 1024 * 1024), SeekOrigin.Begin);
                        using var reader = new StreamReader(input);
                        if (CameraOnlyLook.IsRunningLog(await reader.ReadToEndAsync().ConfigureAwait(false), started))
                            ready = BinocularScopeFix.ReadyIdentity((a, n) => Read(handle, a, n), image);
                    }
                }
                catch (Exception ex) when (ex is IOException or Win32Exception or InvalidDataException) { }
                if (ready is null || previous != ready) { previous = ready; stableSince = elapsed.Elapsed; }
                else if (elapsed.Elapsed - stableSince >= TimeSpan.FromSeconds(3))
                {
                    using var disk = File.OpenRead(expected);
                    BinocularScopeFix.VerifyDisk(disk);
                    if (Install(game, image))
                    {
                        log("Authoritatively retired throwables now release pending-NPC retention. Original executable retained on disk.");
                        return;
                    }
                }
                await Task.Delay(1000).ConfigureAwait(false);
            }
            log("Throwable cleanup helper timed out waiting for an initialized client.");
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException
            or InvalidDataException or UnauthorizedAccessException or AggregateException)
        {
            log("Throwable cleanup helper could not complete: " + ex.Message);
        }
    }

    private static bool Install(Process game, long image)
    {
        using var mutex = new Mutex(false, @"Local\Cranberry.ThrowableCleanupFix." + game.Id);
        bool owned;
        try { owned = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) return false;
        try
        {
            using var write = OpenProcess(0xC38, false, game.Id); // query/read/write/VM operation/suspend
            if (write.IsInvalid) throw NativeError();
            if (!GetProcessTimes(write, out long created, out _, out _, out _) || game.HasExited
                || created != game.StartTime.ToFileTimeUtc()) throw new IOException("The launched game identity changed.");
            if (Inspect(Read(write, image + FunctionRva, FunctionSize), image, (a, n) => Read(write, a, n)) != 0) return true;

            nint stub = AllocateNear(write, image + FunctionRva);
            bool retainStub = false;
            try
            {
                byte[] code = BuildStub(image, (long)stub);
                Write(write, stub, code);
                if (!Read(write, (long)stub, code.Length).AsSpan().SequenceEqual(code)) throw new IOException("Throwable cleanup filter readback failed.");
                if (!VirtualProtectEx(write, stub, 4096, 0x20, out _) || !FlushInstructionCache(write, stub, (nuint)code.Length)) throw NativeError();

                // A seven-byte detour must not be written while a client thread can execute it.
                // Suspend only this retained game instance, check every thread's RIP, and resume
                // in finally. If one is in the replaced instruction, retry on the next poll.
                int status = NtSuspendProcess(write);
                if (status < 0) throw new IOException($"Could not pause throwable cleanup update ({status:X8}).");
                try
                {
                    long entryAddress = image + FunctionRva + PatchIndex;
                    if (ThreadInEntry(game, entryAddress)) return false;
                    if (Inspect(Read(write, image + FunctionRva, FunctionSize), image, (a, n) => Read(write, a, n)) != 0) return true;
                    byte[] entry = BuildEntry(image, (long)stub);
                    if (!VirtualProtectEx(write, (nint)entryAddress, 7, 0x40, out uint protection)) throw NativeError();
                    // Retain executable storage even if OS cleanup fails after the detour was written.
                    retainStub = true;
                    try
                    {
                        try
                        {
                            Write(write, (nint)entryAddress, entry);
                            if (Inspect(Read(write, image + FunctionRva, FunctionSize), image, (a, n) => Read(write, a, n)) != (long)stub)
                                throw new IOException("Throwable cleanup detour verification failed.");
                        }
                        catch
                        {
                            // The process is suspended and the predecessor was checked above.
                            Write(write, (nint)entryAddress, OriginalEntry.ToArray());
                            if (Inspect(Read(write, image + FunctionRva, FunctionSize), image, (a, n) => Read(write, a, n)) != 0)
                                throw new IOException("Throwable cleanup detour rollback failed verification.");
                            retainStub = false;
                            throw;
                        }
                    }
                    finally
                    {
                        bool restored = VirtualProtectEx(write, (nint)entryAddress, 7, protection, out _)
                            || VirtualProtectEx(write, (nint)entryAddress, 7, protection, out _);
                        bool flushed = FlushInstructionCache(write, (nint)entryAddress, 7);
                        if (!restored || !flushed) throw new IOException("Throwable cleanup page protection/cache cleanup failed.");
                    }
                }
                finally
                {
                    int resumed = NtResumeProcess(write);
                    if (resumed < 0 && NtResumeProcess(write) < 0) throw new IOException($"Could not resume game after throwable cleanup update ({resumed:X8}).");
                }
                return true;
            }
            finally { if (!retainStub) VirtualFreeEx(write, stub, 0, 0x8000); }
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static bool ThreadInEntry(Process game, long entry)
    {
        game.Refresh();
        foreach (ProcessThread thread in game.Threads)
        {
            using var handle = OpenThread(0x8, false, thread.Id);
            if (handle.IsInvalid) throw NativeError();
            nint storage = Marshal.AllocHGlobal(1248);
            try
            {
                nint context = (nint)(((long)storage + 15) & ~15L);
                Marshal.Copy(new byte[1232], 0, context, 1232);
                Marshal.WriteInt32(context, 48, 0x100001); // AMD64 CONTEXT_CONTROL
                if (!GetThreadContext(handle, context)) throw NativeError();
                long rip = Marshal.ReadInt64(context, 248);
                if (rip >= entry && rip < entry + 7) return true;
            }
            finally { Marshal.FreeHGlobal(storage); }
        }
        return false;
    }

    private static nint AllocateNear(SafeProcessHandle handle, long function)
    {
        long aligned = function & ~65535L;
        for (long distance = 0x10000; distance < 0x70000000; distance += 0x10000)
            foreach (long candidate in new[] { aligned + distance, aligned - distance })
            {
                if (candidate < 0x10000 || candidate >= (1L << 47) - 4096) continue;
                nint address = VirtualAllocEx(handle, (nint)candidate, 4096, 0x3000, 0x04);
                if (address != 0) return address;
            }
        throw new IOException("No nearby memory is available for the throwable cleanup filter.");
    }

    private static Win32Exception NativeError() => new(Marshal.GetLastWin32Error());
    private static byte[] Read(SafeProcessHandle handle, long address, int size)
    {
        if (address < 0x10000 || address >= (1L << 47) - size || size is < 1 or > FunctionSize) throw new InvalidDataException("Invalid bounded throwable cleanup read.");
        byte[] bytes = new byte[size];
        if (!ReadProcessMemory(handle, (nint)address, bytes, (nuint)size, out nuint count) || count != (nuint)size) throw NativeError();
        return bytes;
    }
    private static void Write(SafeProcessHandle handle, nint address, byte[] bytes)
    {
        if (!WriteProcessMemory(handle, address, bytes, (nuint)bytes.Length, out nuint count) || count != (nuint)bytes.Length) throw NativeError();
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, [Out] byte[] bytes, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] bytes, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAllocEx(SafeProcessHandle process, nint address, nuint size, uint allocation, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(SafeProcessHandle process, nint address, nuint size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualProtectEx(SafeProcessHandle process, nint address, nuint size, uint protect, out uint previous);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushInstructionCache(SafeProcessHandle process, nint address, nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeWaitHandle OpenThread(uint access, bool inherit, int threadId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadContext(SafeWaitHandle thread, nint context);
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(SafeProcessHandle process);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(SafeProcessHandle process);
}
