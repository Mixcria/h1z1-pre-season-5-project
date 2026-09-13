// ConsoleOpener — opens the August 2017 H1Z1 KOTK client's own debug console so Cranberry's
// developer console / mod menu can be typed into.
//
// ── WHY THIS EXISTS ───────────────────────────────────────────────────────────────────────────
// The console pane is the client's own (ConsoleWindow.gfx, Lua `Console`, Main.wndConsole). Nothing
// server-side can draw it, and no server packet opens it: the only two doors are
//
//   Door A  the server sets SelfRecord.FlagI (self-record field 122 = client byte +0x106b9) for an
//           Owner session, after which the client's own Tilde binding passes the permission gate.
//           Zero tooling, zero BattlEye exposure — try this FIRST (CRANBERRY_CONSOLE_SELF_FLAG=1).
//   Door B  this tool: patch that permission gate open in the running process and call the console
//           toggle function directly. This is the owner's F8 habit from the 1087 client, re-derived
//           for build 208059 — every address below came from THIS binary.
//
// The gate lives inside FUN_141291d50, the console toggle-command:
//
//   141291d84  call [rax+0x98]             ; admin-cvar check
//   141291d8c  jne  141291dab              ; <-- the gate (75 1d -> eb 1d)
//   141291d9e  cmp  byte [rax+0x106b9],0   ; else SelfRecord.FlagI
//
// FUN_141291d50(rcx = unused, rdx = char* command) loads the game/self globals itself, so a bare
// remote thread can call it. That is what CreateRemoteThread does here.
//
// ── WHAT THIS TOOL NEVER DOES ─────────────────────────────────────────────────────────────────
//   * it never writes a file inside C:\Aug2017\Client — the exe is opened read-only, to hash it.
//     On-disk patching is refused by BattlEye on this client class; in-memory was tolerated on 1087
//     and is UNPROVEN on 1148 (the first click answers that).
//   * it never touches a process that is not H1Z1.exe, and never a build whose size + SHA-256 are
//     not in BuildGate.KnownBuilds.
//   * it never writes a site whose current bytes are not exactly what the shipped binary holds.
//   * it never mutates a hot CALL. The one five-byte write is opt-in, into a cold function.
//
// ── MODES ─────────────────────────────────────────────────────────────────────────────────────
//   resident hotkey (what the owner wants):  dotnet ConsoleOpener.dll --watch
//   one-shot toggle:                         dotnet ConsoleOpener.dll
//   check, change nothing:                   dotnet ConsoleOpener.dll --verify
//   undo every write:                        dotnet ConsoleOpener.dll --restore
//
// Design: C:\Aug2017\out\devconsole-20260901\DESIGN-dev-console.md §3 (Lane D); evidence in R5 §2.

using System.Diagnostics;

namespace Cranberry.Tools.ConsoleOpener;

/// <summary>Entry point: argument parsing and the four modes.</summary>
public static class Program
{
    /// <summary>Default hotkey: F8, the key the owner already uses on the 1087 client.</summary>
    public const int DefaultKey = 0x77;

    private const int ExitOk = 0;
    private const int ExitRefused = 1;
    private const int ExitUsage = 2;

    /// <summary>Parse the arguments and run one mode.</summary>
    public static int Main(string[] args)
    {
        bool watch = false;
        bool verify = false;
        bool restore = false;
        bool anyBuild = false;
        bool reDerived = false;
        bool nopSpectate = false;
        int pid = -1;
        int key = DefaultKey;
        string toggleArgument = "";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--watch":
                    watch = true;
                    break;

                case "--verify":
                    verify = true;
                    break;

                case "--restore":
                    restore = true;
                    break;

                case "--no-spectate-packet":
                    nopSpectate = true;
                    break;

                case "--any-build":
                    anyBuild = true;
                    break;

                case "--i-re-derived-the-rvas":
                    reDerived = true;
                    break;

                case "--pid" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out pid))
                    {
                        Console.Error.WriteLine($"--pid wants a process id, not \"{args[i]}\"");
                        return ExitUsage;
                    }

                    break;

                case "--key" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], System.Globalization.NumberStyles.HexNumber, null, out key))
                    {
                        Console.Error.WriteLine($"--key wants a hex virtual-key code, not \"{args[i]}\"");
                        return ExitUsage;
                    }

                    break;

                case "--log" when i + 1 < args.Length:
                    StatusLog.UseFile(args[++i]);
                    break;

                case "--toggle-arg" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out int toggleValue))
                    {
                        Console.Error.WriteLine(
                            $"--toggle-arg wants an integer, not \"{args[i]}\". The client's toggle "
                            + "parses its argument as [version] <int> and stores a positive value at "
                            + "game+0x64e9; it cannot type a console line.");
                        return ExitUsage;
                    }

                    toggleArgument = toggleValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;

                case "--help":
                case "-h":
                case "/?":
                    Usage();
                    return ExitOk;

                default:
                    Console.Error.WriteLine($"unknown argument \"{args[i]}\"");
                    Usage();
                    return ExitUsage;
            }
        }

        int modes = (watch ? 1 : 0) + (verify ? 1 : 0) + (restore ? 1 : 0);

        if (modes > 1)
        {
            Console.Error.WriteLine("--watch, --verify and --restore are three different modes; pick one");
            return ExitUsage;
        }

        if (anyBuild && !reDerived)
        {
            StatusLog.Error(
                "REFUSING --any-build. Every RVA in this tool was derived from ONE binary "
                + $"({BuildGate.KnownBuilds[0].Description}); writing 0xEB into the middle of a "
                + "different build's instruction is how you get a client that crashes in a way "
                + "nobody can debug. Re-derive the RVAs against the new binary first, add its size "
                + "and SHA-256 to BuildGate.KnownBuilds, and only then pass "
                + "--any-build --i-re-derived-the-rvas.");
            return ExitRefused;
        }

        var gate = new BuildGate(anyBuild);

        StatusLog.Info($"ConsoleOpener starting — mode={(watch ? "watch" : verify ? "verify" : restore ? "restore" : "one-shot")}"
            + $" key=0x{key:X2} spectateNop={nopSpectate} anyBuild={anyBuild}");

        if (verify)
        {
            return Verify(gate, pid);
        }

        if (restore)
        {
            return Restore(gate, pid);
        }

        if (watch)
        {
            return new HotkeyWatcher(gate, pid, key, toggleArgument, nopSpectate).Run();
        }

        return ToggleOnce(gate, pid, toggleArgument, nopSpectate);
    }

    /// <summary>
    /// <c>--verify</c>: is a client running, is it the build these RVAs came from, and what do the
    /// patch sites currently say? Changes NOTHING, so it is safe at any time — including while a
    /// click-test is live. It reads two code sites and no globals: the client's UI event sink
    /// (<c>DAT_1451d2650</c>) is a "UI is up" precondition, not a gate, and is deliberately not read.
    /// </summary>
    private static int Verify(BuildGate gate, int pid)
    {
        Process? process = ClientProcess.Find(pid, out string missing);

        if (process is null)
        {
            StatusLog.Info(pid < 0 ? "VERIFY: no H1Z1.exe running." : $"VERIFY: {missing}.");
            return ExitRefused;
        }

        if (!gate.IsKnownBuild(process, out string why))
        {
            StatusLog.Error($"VERIFY: pid {process.Id} is NOT a recognised build — {why}");
            return ExitRefused;
        }

        using ClientProcess? client = ClientProcess.Attach(process, out string failure);

        if (client is null)
        {
            StatusLog.Error($"VERIFY: {failure}");
            return ExitRefused;
        }

        StatusLog.Info($"VERIFY: pid {process.Id}, base 0x{client.ModuleBase:x} — {why}");

        SiteState gateState = SiteState.Unreadable;

        foreach (PatchSite site in PatchSites.All)
        {
            SiteState state = client.Inspect(site, out byte[] actual);
            nint address = client.Address(site.Rva);

            string verdict = state switch
            {
                SiteState.Patched => $"PATCHED ({Convert.ToHexString(actual)})",
                SiteState.Original when site.ReadOnly => $"verified ({Convert.ToHexString(actual)})",
                SiteState.Original => $"not yet patched ({Convert.ToHexString(actual)})",
                SiteState.Unreadable => "UNREADABLE",
                _ => $"UNEXPECTED bytes {Convert.ToHexString(actual)} "
                    + $"(expected {Convert.ToHexString(site.Original)})",
            };

            StatusLog.Info($"VERIFY:   {site.Name,-13} 0x{address:x}  {verdict}");

            if (site == PatchSites.Gate)
            {
                gateState = state;
            }
        }

        StatusLog.Info(gateState switch
        {
            SiteState.Patched => "VERIFY: the console gate is OPEN — the hotkey will open the console.",
            SiteState.Original => "VERIFY: the console gate is still closed — run --watch (as Administrator) to open it.",
            _ => "VERIFY: the console gate could not be judged; do NOT patch this client.",
        });

        return gateState is SiteState.Patched ? ExitOk : ExitRefused;
    }

    /// <summary><c>--restore</c>: put every site this tool writes back to the shipped bytes.</summary>
    private static int Restore(BuildGate gate, int pid)
    {
        Process? process = ClientProcess.Find(pid, out string missing);

        if (process is null)
        {
            StatusLog.Info($"RESTORE: {(pid < 0 ? "no H1Z1.exe running" : missing)} — nothing to restore.");
            return ExitRefused;
        }

        if (!gate.IsKnownBuild(process, out string why))
        {
            StatusLog.Error($"RESTORE: pid {process.Id} is NOT a recognised build — {why}");
            return ExitRefused;
        }

        using ClientProcess? client = ClientProcess.Attach(process, out string failure);

        if (client is null)
        {
            StatusLog.Error($"RESTORE: {failure}");
            return ExitRefused;
        }

        StatusLog.Info($"RESTORE: pid {process.Id}, base 0x{client.ModuleBase:x} — {why}");
        return client.RestoreSites() ? ExitOk : ExitRefused;
    }

    /// <summary>Default mode: patch the gate if needed, then toggle the console once.</summary>
    private static int ToggleOnce(BuildGate gate, int pid, string toggleArgument, bool nopSpectate)
    {
        Process? process = ClientProcess.Find(pid, out string missing);

        if (process is null)
        {
            StatusLog.Error($"{missing} — launch the client and get in-world first.");
            return ExitRefused;
        }

        if (!gate.IsKnownBuild(process, out string why))
        {
            StatusLog.Error($"REFUSING to patch pid {process.Id} — {why}");
            return ExitRefused;
        }

        using ClientProcess? client = ClientProcess.Attach(process, out string failure);

        if (client is null)
        {
            StatusLog.Error(failure);
            return ExitRefused;
        }

        StatusLog.Info($"H1Z1.exe pid={process.Id} base=0x{client.ModuleBase:x} "
            + $"toggle=0x{client.Address(PatchSites.ToggleRva):x} — {why}");

        if (!client.PrologueMatches(out string prologue))
        {
            StatusLog.Error(prologue);
            return ExitRefused;
        }

        if (!client.OpenGate(nopSpectate))
        {
            return ExitRefused;
        }

        if (!client.AllocatePage(toggleArgument, out string allocation))
        {
            StatusLog.Error(allocation);
            return ExitRefused;
        }

        if (!client.Toggle(out string toggleFailure))
        {
            StatusLog.Error(toggleFailure);
            return ExitRefused;
        }

        StatusLog.Info(toggleArgument.Length > 0
            ? $"toggled the debug console (toggle argument \"{toggleArgument}\")."
            : "toggled the debug console.");
        return ExitOk;
    }

    private static void Usage()
    {
        Console.WriteLine(
            "ConsoleOpener — opens the August 2017 H1Z1 client's debug console by an in-memory patch.\n"
            + "\n"
            + "  (no arguments)           patch the gate if needed and toggle the console once\n"
            + "  --watch                  resident hotkey watcher — this is what you want\n"
            + "  --key <hex VK>           hotkey, default 77 (F8). Insert=2D, Home=24, F9=78\n"
            + "  --verify                 report the build and every patch site; change nothing\n"
            + "  --restore                write every patched site back to its original bytes\n"
            + "  --no-spectate-packet     also NOP the toggle's Command.Spectate 09 0x510 send.\n"
            + "                           Cranberry ignores that packet, so this is OFF by default:\n"
            + "                           one write fewer for BattlEye to look at\n"
            + "  --toggle-arg <int>       hand the toggle an integer argument ([version] <int> ->\n"
            + "                           game+0x64e9). It CANNOT type a console line\n"
            + "  --pid <n>                target one process instead of searching for H1Z1.exe\n"
            + "                           (the module name is still checked)\n"
            + "  --log <path>             append every status line to a file (never truncated)\n"
            + "  --any-build              refused on its own; needs --i-re-derived-the-rvas too\n"
            + "\n"
            + "Exit codes: 0 ok, 1 refused or failed, 2 bad usage.\n"
            + "Run it in an Administrator window: OpenProcess on the client needs it.\n"
            + "Try the server-side door first — CRANBERRY_CONSOLE_SELF_FLAG=1, then Tilde in game.");
    }
}
