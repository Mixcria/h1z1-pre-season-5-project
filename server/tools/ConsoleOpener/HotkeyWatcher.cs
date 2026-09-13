using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cranberry.Tools.ConsoleOpener;

/// <summary>
/// The resident hotkey loop — the owner's habit from 1087, re-expressed for 208059.
///
/// <para>
/// This is a <c>GetAsyncKeyState</c> poll, NOT a client key binding: the client's own
/// <c>ToggleDebugConsole</c> action is bound to Tilde/Kanji and marked <c>unbindable</c>
/// (<c>InputProfile_Default.xml:7</c>), and it is gated on the same admin/self-flag test this tool
/// patches. F8 belongs to the watcher, so it works whatever the client thinks about keys.
/// </para>
///
/// <para>
/// One watcher at a time. Two of them fight over the same key and each fires its own remote thread,
/// which toggles the console twice and looks exactly like "F8 does nothing" (the owner's 1087
/// lesson).
/// </para>
/// </summary>
public sealed class HotkeyWatcher
{
    /// <summary>Machine-wide single-instance name.</summary>
    public const string MutexName = @"Global\Cranberry-ConsoleOpener";

    /// <summary>How often the key is sampled, in milliseconds.</summary>
    public const int PollMs = 20;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private readonly BuildGate _gate;
    private readonly int _pid;
    private readonly int _key;
    private readonly string _toggleArgument;
    private readonly bool _nopSpectate;

    /// <param name="gate">The build gate; a refusal is logged and retried, never bypassed.</param>
    /// <param name="pid">A pinned process id, or -1 to search for H1Z1.exe.</param>
    /// <param name="key">Virtual-key code to poll (0x77 = F8).</param>
    /// <param name="toggleArgument">The string handed to the toggle in <c>rdx</c>; usually empty.</param>
    /// <param name="nopSpectate">Opt-in: also NOP the <c>Command.Spectate</c> send.</param>
    public HotkeyWatcher(BuildGate gate, int pid, int key, string toggleArgument, bool nopSpectate)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _pid = pid;
        _key = key;
        _toggleArgument = toggleArgument ?? throw new ArgumentNullException(nameof(toggleArgument));
        _nopSpectate = nopSpectate;
    }

    /// <summary>
    /// Run until Ctrl+C. Survives client restarts: waits for H1Z1.exe, re-verifies and re-patches
    /// every new session, re-acquires when the client comes back.
    /// </summary>
    /// <returns>0 when the watcher ran, 1 when another watcher already owns the hotkey.</returns>
    public int Run()
    {
        using var only = new Mutex(true, MutexName, out bool mine);

        if (!mine)
        {
            StatusLog.Error("another ConsoleOpener watcher is already running — leaving it alone. "
                + "Close that window first if you meant to change the key.");
            return 1;
        }

        StatusLog.Info($"console hotkey watcher — press key 0x{_key:X2} in game to open/close the "
            + "console. Ctrl+C to stop. Waiting for H1Z1.exe...");

        int lastPid = -1;

        while (true)
        {
            Process? process = ClientProcess.Find(_pid, out string missing);

            if (process is null)
            {
                if (lastPid != -1)
                {
                    StatusLog.Info($"client gone ({missing}) — waiting for it to come back...");
                    lastPid = -1;
                }

                Thread.Sleep(1000);
                continue;
            }

            if (process.Id != lastPid)
            {
                lastPid = process.Id;
                StatusLog.Info($"H1Z1.exe appeared — pid {process.Id}.");
            }

            if (!_gate.IsKnownBuild(process, out string why))
            {
                StatusLog.Error($"REFUSING to patch pid {process.Id} — {why}. Pass --any-build "
                    + "--i-re-derived-the-rvas only after re-deriving every RVA against this binary.");
                Thread.Sleep(15000);
                continue;
            }

            using ClientProcess? client = ClientProcess.Attach(process, out string failure);

            if (client is null)
            {
                StatusLog.Error(failure);
                Thread.Sleep(3000);
                continue;
            }

            if (!client.PrologueMatches(out string prologue))
            {
                StatusLog.Error(prologue);
                Thread.Sleep(15000);
                continue;
            }

            if (!client.OpenGate(_nopSpectate))
            {
                Thread.Sleep(5000);
                continue;
            }

            if (!client.AllocatePage(_toggleArgument, out string allocation))
            {
                StatusLog.Error(allocation);
                Thread.Sleep(3000);
                continue;
            }

            StatusLog.Info($"READY — attached to pid {process.Id}, {why}. Press key 0x{_key:X2} in "
                + "game to open the console.");

            Poll(client);

            StatusLog.Info("client closed — waiting for it to come back...");
            lastPid = -1;
        }
    }

    /// <summary>
    /// Rising-edge poll. Every fire also makes the client send
    /// <c>Command.Spectate 09 0x510 "ObserverCamera"</c> unless <c>--no-spectate-packet</c> was
    /// given; Cranberry logs that at Debug and ignores it (design §1.5).
    /// </summary>
    private void Poll(ClientProcess client)
    {
        bool down = false;

        while (!client.Process.HasExited)
        {
            bool now = (GetAsyncKeyState(_key) & 0x8000) != 0;

            if (now && !down)
            {
                if (client.Toggle(out string failure))
                {
                    StatusLog.Info("toggled");
                }
                else
                {
                    StatusLog.Error(failure);
                }
            }

            down = now;
            Thread.Sleep(PollMs);
        }
    }
}
