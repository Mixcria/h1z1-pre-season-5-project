using System.Runtime.InteropServices;

namespace Cranberry.Launcher;

/// <summary>A desktop keyboard hook scoped to the launched game. No game process injection.</summary>
internal sealed class GameOverlayHotkey : IDisposable
{
    private readonly Hook _callback;
    private nint _handle;
    private bool _tabHeld;
    private bool _polledPress;
    private readonly System.Windows.Forms.Timer _fallback = new() { Interval = 20 };
    private delegate nint Hook(int code, nint message, nint data);

    public GameOverlayHotkey(int processId, GameInput keys, Action toggle)
    {
        long lastToggle = long.MinValue;
        void Toggle()
        {
            long now = Environment.TickCount64;
            if (lastToggle != long.MinValue && now - lastToggle < 250) return;
            lastToggle = now; toggle();
        }
        _callback = (code, message, data) =>
        {
            if (code >= 0 && Marshal.ReadInt32(data) == 0x09)
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out uint foreground);
                bool down = message == 0x100 || message == 0x104;
                bool up = message == 0x101 || message == 0x105;
                if (_tabHeld && up) { _tabHeld = _polledPress = false; return 1; }
                if (foreground == processId && down && (_tabHeld || (GetAsyncKeyState(0x10) & 0x8000) != 0))
                {
                    if (!_tabHeld) { _tabHeld = true; Toggle(); }
                    return 1;
                }
            }
            return CallNextHookEx(_handle, code, message, data);
        };
        _handle = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
        // Windows can silently remove a low-level hook after a busy UI thread.
        // Poll the same physical chord as a fallback; hook-delivered presses remain
        // latched until key-up so the consumed Tab cannot toggle twice.
        _fallback.Tick += (_, _) =>
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint foreground);
            bool shift = keys.Down(0x10);
            bool tab = keys.Down(0x09);
            if (foreground != processId || !shift || (_polledPress && !tab))
            { _tabHeld = _polledPress = false; return; }
            if (tab && !_tabHeld) { _tabHeld = _polledPress = true; Toggle(); }
        };
        _fallback.Start();
    }

    public void Dispose() { _fallback.Stop(); _fallback.Dispose(); if (_handle != 0) { UnhookWindowsHookEx(_handle); _handle = 0; } }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, Hook callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
