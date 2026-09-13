using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cranberry.Tools.ConsoleOpener;

/// <summary>What the bytes at a <see cref="PatchSite"/> currently say.</summary>
public enum SiteState
{
    /// <summary>The site could not be read (process gone, or no rights).</summary>
    Unreadable,

    /// <summary>Exactly the shipped bytes — not patched.</summary>
    Original,

    /// <summary>Exactly the bytes this tool writes — already patched.</summary>
    Patched,

    /// <summary>Neither: a different build, or something else has modified the client.</summary>
    Unexpected,
}

/// <summary>
/// The live client: find it, open it, read and write its memory, and call the console toggle in it.
///
/// <para>
/// Everything here is in-memory only. The exe on disk is opened for hashing and nothing else
/// (<see cref="BuildGate"/>) — on-disk patching is refused by BattlEye on this client class, and the
/// in-memory OpenProcess/VirtualProtectEx/WriteProcessMemory/CreateRemoteThread route is the one the
/// owner's 1087 tool proved tolerable there. Whether BattlEye on 208059 tolerates it too is the first
/// click; see the README.
/// </para>
/// </summary>
public sealed class ClientProcess : IDisposable
{
    /// <summary>The client's process name, without the extension.</summary>
    public const string ProcessName = "H1Z1";

    /// <summary>The module the RVAs are relative to.</summary>
    public const string ModuleName = "H1Z1.exe";

    private const uint ProcessAll = 0x1F0FFF;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageExecuteReadWrite = 0x40;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAllocEx(nint proc, nint addr, nuint size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(nint proc, nint addr, nuint size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(nint proc, nint addr, byte[] buffer, nuint size, out nuint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint proc, nint addr, byte[] buffer, nuint size, out nuint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(nint proc, nint addr, nuint size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(nint proc, nint addr, nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateRemoteThread(nint proc, nint attributes, nuint stack, nint start, nint parameter, uint flags, out uint tid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    private readonly nint _handle;
    private nint _page;
    private bool _disposed;

    private ClientProcess(Process process, nint handle, nint moduleBase)
    {
        Process = process;
        _handle = handle;
        ModuleBase = moduleBase;
    }

    /// <summary>The client process this instance is attached to.</summary>
    public Process Process { get; }

    /// <summary>
    /// The REAL load address of H1Z1.exe, read on every attach. <c>DYNAMIC_BASE</c> is set on this
    /// exe, so the preferred base <c>0x140000000</c> is never assumed.
    /// </summary>
    public nint ModuleBase { get; }

    /// <summary>True once <see cref="Toggle"/> can be called without re-allocating.</summary>
    public bool PageReady => _page != 0;

    /// <summary>Live address of a base-relative address.</summary>
    public nint Address(long rva) => ModuleBase + (nint)rva;

    /// <summary>
    /// Find the running client. Pass a pid to pin one process; the module name is checked either way,
    /// so <c>--pid</c> can never aim this tool at something that is not H1Z1.
    /// </summary>
    public static Process? Find(int pid, out string failure)
    {
        Process? process;

        try
        {
            process = pid >= 0 ? Process.GetProcessById(pid) : Process.GetProcessesByName(ProcessName).FirstOrDefault();
        }
        catch (Exception ex)
        {
            failure = pid >= 0 ? $"no process with pid {pid}: {ex.Message}" : $"cannot enumerate processes: {ex.Message}";
            return null;
        }

        if (process is null || process.HasExited)
        {
            failure = pid >= 0 ? $"pid {pid} is not running" : "no H1Z1.exe running";
            return null;
        }

        string module;

        try
        {
            module = process.MainModule?.ModuleName ?? "";
        }
        catch (Exception ex)
        {
            failure = $"cannot read the main module of pid {process.Id}: {ex.Message} "
                + "(a 64-bit Administrator shell is required)";
            return null;
        }

        if (!string.Equals(module, ModuleName, StringComparison.OrdinalIgnoreCase))
        {
            failure = $"pid {process.Id} is \"{module}\", not {ModuleName} — refusing to touch it";
            return null;
        }

        failure = "";
        return process;
    }

    /// <summary>
    /// Open the process for read/write. Failure is almost always rights: the message says so, and
    /// the watcher retries every three seconds rather than dying.
    /// </summary>
    public static ClientProcess? Attach(Process process, out string failure)
    {
        ArgumentNullException.ThrowIfNull(process);

        nint moduleBase;

        try
        {
            moduleBase = process.MainModule!.BaseAddress;
        }
        catch (Exception ex)
        {
            failure = $"cannot read the base address of pid {process.Id}: {ex.Message}";
            return null;
        }

        nint handle = OpenProcess(ProcessAll, false, process.Id);

        if (handle == 0)
        {
            failure = $"cannot open pid {process.Id}: win32 error {Marshal.GetLastWin32Error()} "
                + "— run this window as Administrator";
            return null;
        }

        failure = "";
        return new ClientProcess(process, handle, moduleBase);
    }

    /// <summary>Read <paramref name="count"/> bytes at a base-relative address.</summary>
    public bool TryRead(long rva, int count, out byte[] bytes)
    {
        bytes = new byte[count];
        return ReadProcessMemory(_handle, Address(rva), bytes, (nuint)count, out nuint read) && read == (nuint)count;
    }

    /// <summary>
    /// Write bytes at a base-relative address, flipping the page writable and back around the write
    /// and flushing the instruction cache afterwards.
    /// </summary>
    public bool TryWrite(long rva, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        nint address = Address(rva);

        if (!VirtualProtectEx(_handle, address, (nuint)bytes.Length, PageExecuteReadWrite, out uint old))
        {
            return false;
        }

        bool ok = WriteProcessMemory(_handle, address, bytes, (nuint)bytes.Length, out nuint written)
            && written == (nuint)bytes.Length;

        VirtualProtectEx(_handle, address, (nuint)bytes.Length, old, out _);
        FlushInstructionCache(_handle, address, (nuint)bytes.Length);
        return ok;
    }

    /// <summary>What the bytes at a site currently say. Reads only.</summary>
    public SiteState Inspect(PatchSite site, out byte[] actual)
    {
        ArgumentNullException.ThrowIfNull(site);

        int width = Math.Max(site.Original.Length, site.Patched.Length);

        if (!TryRead(site.Rva, width, out actual))
        {
            return SiteState.Unreadable;
        }

        if (actual.AsSpan().SequenceEqual(site.Original))
        {
            return SiteState.Original;
        }

        if (site.Patched.Length > 0 && actual.AsSpan()[..site.Patched.Length].SequenceEqual(site.Patched))
        {
            return SiteState.Patched;
        }

        return SiteState.Unexpected;
    }

    /// <summary>
    /// Read the toggle function's first bytes out of the LIVE process. The disk hash proves which
    /// file was loaded; this proves nothing has moved in memory before a remote thread jumps into it.
    /// </summary>
    public bool PrologueMatches(out string detail)
    {
        if (!TryRead(PatchSites.ToggleRva, PatchSites.TogglePrologue.Length, out byte[] actual))
        {
            detail = $"cannot read the toggle prologue at 0x{Address(PatchSites.ToggleRva):x}: "
                + $"win32 error {Marshal.GetLastWin32Error()}";
            return false;
        }

        if (actual.AsSpan().SequenceEqual(PatchSites.TogglePrologue))
        {
            detail = "";
            return true;
        }

        detail = $"the toggle function at 0x{Address(PatchSites.ToggleRva):x} starts with "
            + $"{Convert.ToHexString(actual)}, expected {Convert.ToHexString(PatchSites.TogglePrologue)} "
            + "— NOT firing a remote thread into it";
        return false;
    }

    /// <summary>
    /// Allocate the remote page holding the command string and the calling stub. Called once per
    /// attach; the watcher re-uses it for every keypress.
    /// </summary>
    public bool AllocatePage(string commandLine, out string failure)
    {
        if (_page != 0)
        {
            failure = "";
            return true;
        }

        nint page = VirtualAllocEx(_handle, 0, PatchSites.PageSize, MemCommit | MemReserve, PageExecuteReadWrite);

        if (page == 0)
        {
            failure = $"VirtualAllocEx failed: win32 error {Marshal.GetLastWin32Error()}";
            return false;
        }

        byte[] bytes = PatchSites.BuildRemotePage(commandLine, page, Address(PatchSites.ToggleRva));

        if (!WriteProcessMemory(_handle, page, bytes, PatchSites.PageSize, out _))
        {
            failure = $"WriteProcessMemory(page) failed: win32 error {Marshal.GetLastWin32Error()}";
            VirtualFreeEx(_handle, page, 0, MemRelease);
            return false;
        }

        _page = page;
        failure = "";
        return true;
    }

    /// <summary>
    /// Call <c>FUN_141291d50</c> once — the console opens or closes. The prologue is re-verified
    /// immediately before the thread is created.
    /// </summary>
    public bool Toggle(out string failure)
    {
        if (_page == 0)
        {
            failure = "no remote page allocated";
            return false;
        }

        if (!PrologueMatches(out string prologue))
        {
            failure = prologue;
            return false;
        }

        nint thread = CreateRemoteThread(_handle, 0, 0, _page + PatchSites.StubOffset, 0, 0, out _);

        if (thread == 0)
        {
            failure = $"CreateRemoteThread failed: win32 error {Marshal.GetLastWin32Error()}";
            return false;
        }

        WaitForSingleObject(thread, 5000);
        CloseHandle(thread);
        failure = "";
        return true;
    }

    /// <summary>
    /// Open the console gate in memory, and — only when <paramref name="nopSpectate"/> is set — NOP
    /// the <c>Command.Spectate</c> send.
    ///
    /// <para>
    /// Order matters and is the design's (§3.3): EVERY site is inspected before the FIRST write, so a
    /// second site whose bytes are wrong aborts the run without the client having been touched at
    /// all. If the optional five-byte write still fails after the gate was written, the gate is put
    /// back, because a half-patched client is the one state nobody can diagnose later.
    /// </para>
    /// </summary>
    public bool OpenGate(bool nopSpectate)
    {
        SiteState gate = Inspect(PatchSites.Gate, out byte[] gateBytes);

        if (gate is SiteState.Unreadable)
        {
            StatusLog.Error($"cannot read the gate byte at 0x{Address(PatchSites.Gate.Rva):x}: "
                + $"win32 error {Marshal.GetLastWin32Error()}");
            return false;
        }

        if (gate is SiteState.Unexpected)
        {
            StatusLog.Error($"UNEXPECTED bytes {Convert.ToHexString(gateBytes)} at the gate "
                + $"0x{Address(PatchSites.Gate.Rva):x} (expected {Convert.ToHexString(PatchSites.Gate.Original)} "
                + $"or {Convert.ToHexString(PatchSites.Gate.Patched)}) — wrong build, or something else "
                + "has already modified this client. Aborting without writing anything.");
            return false;
        }

        SiteState spectate = SiteState.Original;
        byte[] spectateBytes = [];

        if (nopSpectate)
        {
            spectate = Inspect(PatchSites.SpectateCall, out spectateBytes);

            if (spectate is SiteState.Unreadable or SiteState.Unexpected)
            {
                StatusLog.Error($"REFUSING — the Command.Spectate call at "
                    + $"0x{Address(PatchSites.SpectateCall.Rva):x} reads "
                    + $"{(spectate is SiteState.Unreadable ? "nothing" : Convert.ToHexString(spectateBytes))}, "
                    + $"expected {Convert.ToHexString(PatchSites.SpectateCall.Original)}. "
                    + "The gate was NOT touched.");
                return false;
            }
        }

        bool wroteGate = false;

        if (gate is SiteState.Original)
        {
            if (!TryWrite(PatchSites.Gate.Rva, PatchSites.Gate.PatchWrite))
            {
                StatusLog.Error($"writing the gate byte failed: win32 error {Marshal.GetLastWin32Error()}");
                return false;
            }

            wroteGate = true;
            StatusLog.Info($"PATCH OK — gate opened at 0x{Address(PatchSites.Gate.Rva):x} (0x75 -> 0xEB).");
        }
        else
        {
            StatusLog.Info($"PATCH OK — gate already open at 0x{Address(PatchSites.Gate.Rva):x}.");
        }

        if (!nopSpectate)
        {
            return true;
        }

        if (spectate is SiteState.Patched)
        {
            StatusLog.Info($"Command.Spectate send already NOPed at 0x{Address(PatchSites.SpectateCall.Rva):x}.");
            return true;
        }

        if (TryWrite(PatchSites.SpectateCall.Rva, PatchSites.SpectateCall.PatchWrite))
        {
            StatusLog.Info($"Command.Spectate send NOPed at 0x{Address(PatchSites.SpectateCall.Rva):x} "
                + "(the toggle no longer sends 09 0x510 \"ObserverCamera\").");
            return true;
        }

        StatusLog.Error($"writing the Command.Spectate NOP failed: win32 error {Marshal.GetLastWin32Error()}");

        if (wroteGate && TryWrite(PatchSites.Gate.Rva, PatchSites.Gate.RestoreWrite))
        {
            StatusLog.Info("the gate byte this run wrote has been put back; the client is untouched.");
        }

        return false;
    }

    /// <summary>
    /// Put every written site back to the bytes the shipped binary holds — but only after reading
    /// back exactly the bytes this tool writes. A site holding anything else is left alone and
    /// reported: restoring bytes we did not write is how you corrupt someone else's patch.
    /// </summary>
    public bool RestoreSites()
    {
        bool ok = true;

        foreach (PatchSite site in PatchSites.All)
        {
            if (site.ReadOnly)
            {
                continue;
            }

            SiteState state = Inspect(site, out byte[] actual);
            nint address = Address(site.Rva);

            switch (state)
            {
                case SiteState.Patched when TryWrite(site.Rva, site.RestoreWrite):
                    StatusLog.Info($"RESTORED {site.Name} at 0x{address:x} "
                        + $"({Convert.ToHexString(site.Patched)} -> {Convert.ToHexString(site.Original)}).");
                    break;

                case SiteState.Patched:
                    StatusLog.Error($"restoring {site.Name} at 0x{address:x} failed: "
                        + $"win32 error {Marshal.GetLastWin32Error()}");
                    ok = false;
                    break;

                case SiteState.Original:
                    StatusLog.Info($"{site.Name} at 0x{address:x} is already the original "
                        + $"{Convert.ToHexString(site.Original)} — nothing to do.");
                    break;

                case SiteState.Unreadable:
                    StatusLog.Error($"cannot read {site.Name} at 0x{address:x}: "
                        + $"win32 error {Marshal.GetLastWin32Error()}");
                    ok = false;
                    break;

                default:
                    StatusLog.Error($"REFUSING to restore {site.Name} at 0x{address:x}: it holds "
                        + $"{Convert.ToHexString(actual)}, which is neither this tool's "
                        + $"{Convert.ToHexString(site.Patched)} nor the original "
                        + $"{Convert.ToHexString(site.Original)}. Left untouched.");
                    ok = false;
                    break;
            }
        }

        return ok;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_page != 0)
        {
            VirtualFreeEx(_handle, _page, 0, MemRelease);
            _page = 0;
        }

        if (_handle != 0)
        {
            CloseHandle(_handle);
        }
    }
}
