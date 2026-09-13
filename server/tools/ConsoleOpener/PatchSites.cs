namespace Cranberry.Tools.ConsoleOpener;

/// <summary>
/// One address in the client that the opener reads, and may write.
/// </summary>
/// <param name="Name">Short name used in every log line.</param>
/// <param name="Rva">Base-relative address (VA = <see cref="PatchSites.ImageBase"/> + Rva).</param>
/// <param name="Original">The bytes the shipped binary holds here. Verified before every write.</param>
/// <param name="Patched">The bytes the opener writes. Empty for a read-only site.</param>
/// <param name="OptIn">True when the site is written only on an explicit flag.</param>
/// <param name="Purpose">Why the site exists, for the log and the README.</param>
/// <param name="Evidence">Dump / function the row was read from.</param>
public sealed record PatchSite(
    string Name,
    long Rva,
    byte[] Original,
    byte[] Patched,
    bool OptIn,
    string Purpose,
    string Evidence)
{
    /// <summary>VA of this site in the shipped image (the live address adds the real module base).</summary>
    public long Va => PatchSites.ImageBase + Rva;

    /// <summary>Offset of this site inside H1Z1.exe on disk, for a read-only byte check.</summary>
    public long FileOffset => PatchSites.FileOffset(Rva);

    /// <summary>True when this site is only ever read, never written.</summary>
    public bool ReadOnly => Patched.Length == 0;

    /// <summary>
    /// How many bytes the tool actually writes: the shortest prefix that covers every difference
    /// between <see cref="Original"/> and <see cref="Patched"/>.
    ///
    /// <para>
    /// The gate is verified as the whole two-byte instruction <c>75 1d</c> but WRITTEN as one byte
    /// (<c>75 -&gt; eb</c>), because a one-byte write is atomic and the jump displacement never
    /// changes. Only the opt-in five-byte CALL takes a wider, non-atomic write.
    /// </para>
    /// </summary>
    public int WriteLength
    {
        get
        {
            for (int i = Patched.Length - 1; i >= 0; i--)
            {
                if (i >= Original.Length || Original[i] != Patched[i])
                {
                    return i + 1;
                }
            }

            return 0;
        }
    }

    /// <summary>The exact bytes written to patch this site.</summary>
    public byte[] PatchWrite => Patched[..WriteLength];

    /// <summary>The exact bytes written to undo this site.</summary>
    public byte[] RestoreWrite => Original[..WriteLength];
}

/// <summary>
/// The patch table for H1Z1 build 0.0.118.208059 (ClientProtocol_1148), and nothing else.
///
/// <para>
/// Every row below was derived from THIS binary — never ported from the 1087 client, whose console
/// toggle sits at a completely different address. The three byte strings were read straight out of
/// <c>C:\Aug2017\Client\H1Z1.exe</c> at the file offsets below on 2026-09-02 (R5 §2.3,
/// DESIGN-dev-console.md §3.2, refute-1 §3, and once more by this lane before this file was written).
/// <c>PatchSitesTests</c> re-reads them from the exe so the table can never silently drift.
/// </para>
///
/// <para>
/// Layout arithmetic: the client's <c>.text</c> maps RVA <c>0x1000</c> to file offset <c>0x600</c>,
/// so <c>file = VA - 0x140000A00</c> — verified by reading VA <c>0x141291d8c</c> back as file
/// <c>0x129138c</c> and finding the expected <c>75 1d</c> (R5 header).
/// </para>
///
/// <para>
/// The target function is <c>FUN_141291d50</c>, the console toggle-command
/// (<c>out\ghidra-aug\devconsole-ToggleDebugConsole\</c>). It loads the game and self globals itself
/// (<c>DAT_143f696a0</c>, <c>DAT_143f69f60</c>), so a bare remote thread can call it with
/// <c>(rcx = unused, rdx = char* commandLine)</c>. The sibling key-path opener <c>FUN_140b89400</c>
/// takes the game object in <c>rcx</c> and is therefore NOT a remote-thread target — it is the
/// Tilde-key door, which the server opens instead by setting <c>SelfRecord.FlagI</c>
/// (DESIGN-dev-console.md §3.5, Door A).
/// </para>
/// </summary>
public static class PatchSites
{
    /// <summary>Preferred image base of H1Z1.exe. The live base is read from the process anyway.</summary>
    public const long ImageBase = 0x140000000L;

    /// <summary>VA minus this equals the file offset (.text RVA 0x1000 maps to file 0x600).</summary>
    public const long FileOffsetBias = 0x140000A00L;

    /// <summary>Base-relative address of the console toggle-command <c>FUN_141291d50</c>.</summary>
    public const long ToggleRva = 0x1291d50;

    /// <summary>Size of the remote page: the command string, then the stub.</summary>
    public const int PageSize = 256;

    /// <summary>Offset of the stub inside the remote page.</summary>
    public const int StubOffset = 0x10;

    /// <summary>
    /// The first eight bytes of <c>FUN_141291d50</c> (<c>push rbp/rsi/rdi/r14/r15</c>), read back out
    /// of the LIVE process before any remote thread is fired: the disk hash proves which file was
    /// loaded, this proves nothing has moved in memory since. The owner's 1087 tool verified six
    /// bytes; these eight are a superset and match this build.
    /// </summary>
    public static readonly byte[] TogglePrologue = [0x40, 0x55, 0x56, 0x57, 0x41, 0x56, 0x41, 0x57];

    /// <summary>
    /// The permission gate inside <c>FUN_141291d50</c>. Disasm (R5 §2.2):
    /// <code>
    /// 141291d84  call [rax+0x98]             ; admin-cvar check -> al
    /// 141291d8c  jne  141291dab              ; &lt;-- THIS BYTE PAIR (75 1d): admin -> proceed
    /// 141291d8e  mov  rax,[143f69f60]        ; else the self player object
    /// 141291d9e  cmp  byte [rax+0x106b9],0   ; SelfRecord.FlagI
    /// 141291da5  je   14129201b              ;   0 -> return, console stays shut
    /// </code>
    /// Turning the conditional jump into an unconditional one (<c>75 -&gt; eb</c>) takes the "admin"
    /// branch every time, so the console opens without the flag and without the admin cvar. One byte,
    /// and a one-byte write is atomic.
    /// </summary>
    public static readonly PatchSite Gate = new(
        Name: "gate",
        Rva: 0x1291d8c,
        Original: [0x75, 0x1D],
        Patched: [0xEB, 0x1D],
        OptIn: false,
        Purpose: "force the admin branch of FUN_141291d50 so the toggle opens the console",
        Evidence: "R5 §2.2/§2.3; disasm of FUN_141291d50; bytes read at file 0x129138c");

    /// <summary>
    /// The <c>call</c> that makes every toggle send <c>Command.Spectate 09 0x510 "ObserverCamera"</c>
    /// to the server (<c>FUN_141291d50_141291d50.c:100-104</c>: <c>local_8f0 = 9; local_8e8 = 0x510;
    /// PTR_s_ObserverCamera</c>, handed to the c2s sender <c>FUN_141265760</c> through the thunk at
    /// <c>0x14003d325</c>).
    ///
    /// <para>
    /// On retail this is what flipped the player into spectator, because the SERVER answered the
    /// packet. Cranberry never answers it — the zone hook matches it and logs one Debug line
    /// (DESIGN-dev-console.md §1.5) — so NOPping this call buys nothing against our own server and
    /// costs one more write under BattlEye. Hence <see cref="PatchSite.OptIn"/> = true: written only
    /// with <c>--no-spectate-packet</c>.
    /// </para>
    ///
    /// <para>
    /// Five bytes is NOT an atomic write. The owner's 2026-08-23 rule stands: never mutate a hot
    /// CALL — a five-byte WriteProcessMemory into the hot Pipeline::CheckRequirements site faulted a
    /// client at a non-module RIP that day. This CALL is cold: <c>FUN_141291d50</c> runs only when the
    /// console toggles, which is exactly when this tool is not writing.
    /// </para>
    /// </summary>
    public static readonly PatchSite SpectateCall = new(
        Name: "spectate-call",
        Rva: 0x1291faf,
        Original: [0xE8, 0x71, 0xB3, 0xDA, 0xFE],
        Patched: [0x90, 0x90, 0x90, 0x90, 0x90],
        OptIn: true,
        Purpose: "stop the toggle sending Command.Spectate 09 0x510 \"ObserverCamera\" (opt-in)",
        Evidence: "DESIGN-dev-console.md §1.5/§3.2; FUN_141291d50:100-104; bytes read at file 0x12915af");

    /// <summary>The toggle entry itself: verified before every fire, never written.</summary>
    public static readonly PatchSite ToggleEntry = new(
        Name: "toggle-entry",
        Rva: ToggleRva,
        Original: TogglePrologue,
        Patched: [],
        OptIn: false,
        Purpose: "CreateRemoteThread target FUN_141291d50; verified in memory before every fire",
        Evidence: "R5 §2.1/§2.3; bytes read at file 0x1291350");

    /// <summary>Every row, in the order the README prints them.</summary>
    public static readonly PatchSite[] All = [ToggleEntry, Gate, SpectateCall];

    /// <summary>Offset of a base-relative address inside H1Z1.exe on disk.</summary>
    public static long FileOffset(long rva) => ImageBase + rva - FileOffsetBias;

    /// <summary>
    /// The 256-byte remote page: a NUL-terminated command string at <c>[0x00]</c> and, at
    /// <c>[0x10]</c>, an x64 stub calling <c>FUN_141291d50(rcx = 0, rdx = &amp;string)</c>.
    ///
    /// <code>
    /// sub rsp,0x28 ; xor rcx,rcx ; mov rdx,pageVa ; mov rax,toggleVa ; call rax ; add rsp,0x28 ; ret
    /// </code>
    ///
    /// <para>
    /// The shadow space plus the 8-byte adjustment keeps the ABI's 16-byte stack alignment: a thread
    /// entry point starts with <c>rsp % 16 == 8</c>, <c>sub rsp,0x28</c> makes it 0, and the
    /// <c>call</c>'s pushed return address hands the toggle the 8 it expects.
    /// </para>
    ///
    /// <para>
    /// <paramref name="commandLine"/> is empty for a plain toggle. <c>FUN_141291d50:58-85</c>
    /// tokenises it, skips a leading <c>version</c> token, parses the next token as an int and stores
    /// it at <c>game+0x64e9</c> when positive — that is all <c>--toggle-arg</c> can do. The string is
    /// NOT a console line and cannot type a command.
    /// </para>
    /// </summary>
    /// <param name="commandLine">ASCII command string, or "" to just toggle.</param>
    /// <param name="pageVa">Address the page will live at inside the client.</param>
    /// <param name="toggleVa">Live address of <c>FUN_141291d50</c>.</param>
    public static byte[] BuildRemotePage(string commandLine, long pageVa, long toggleVa)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        if (commandLine.Length >= StubOffset)
        {
            throw new ArgumentException(
                $"the toggle argument must be shorter than {StubOffset} bytes", nameof(commandLine));
        }

        byte[] page = new byte[PageSize];
        System.Text.Encoding.ASCII.GetBytes(commandLine).CopyTo(page, 0);
        // the rest of the page stays zero, so the string is NUL-terminated for free

        int p = StubOffset;
        page[p++] = 0x48; page[p++] = 0x83; page[p++] = 0xEC; page[p++] = 0x28;   // sub rsp,0x28
        page[p++] = 0x48; page[p++] = 0x31; page[p++] = 0xC9;                     // xor rcx,rcx
        page[p++] = 0x48; page[p++] = 0xBA;                                       // mov rdx,imm64
        BitConverter.GetBytes(pageVa).CopyTo(page, p); p += 8;
        page[p++] = 0x48; page[p++] = 0xB8;                                       // mov rax,imm64
        BitConverter.GetBytes(toggleVa).CopyTo(page, p); p += 8;
        page[p++] = 0xFF; page[p++] = 0xD0;                                       // call rax
        page[p++] = 0x48; page[p++] = 0x83; page[p++] = 0xC4; page[p++] = 0x28;   // add rsp,0x28
        page[p] = 0xC3;                                                           // ret

        return page;
    }
}
