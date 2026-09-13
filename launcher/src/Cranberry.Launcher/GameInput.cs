using System.Runtime.InteropServices;
using Cranberry.Launcher.Core.Voice;

namespace Cranberry.Launcher;

/// <summary>Raw hardware buttons supplement legacy key polling while the launched game has focus.</summary>
internal sealed class GameInput : NativeWindow, IDisposable
{
    private readonly int _processId;
    private readonly PhysicalGameKeys _keys = new();
    private readonly byte[] _packet = new byte[64];
    private readonly System.Windows.Forms.Timer _pulse = new() { Interval = 20 };
    private long _lastPulse;
    private bool _registered;
    public GameInput(int processId)
    {
        _processId = processId;
        CreateHandle(new CreateParams { Parent = new nint(-3), Caption = "Cranberry game input" });
        RawDevice[] devices = [new(1, 2, 0x2100, Handle), new(1, 6, 0x2100, Handle)];
        _registered = RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf<RawDevice>());
        _lastPulse = Environment.TickCount64;
        _pulse.Tick += (_, _) => { Interlocked.Exchange(ref _lastPulse, Environment.TickCount64); if (!GameFocused) _keys.Clear(); };
        _pulse.Start();
    }
    public bool GameFocused { get { GetWindowThreadProcessId(GetForegroundWindow(), out uint id); return id == _processId; } }
    public bool Responsive => Environment.TickCount64 - Interlocked.Read(ref _lastPulse) < 150;
    public bool Down(int key) => GameFocused && Responsive && (_keys.Down(key) || (GetAsyncKeyState(key) & 0x8000) != 0);
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0xfe) _keys.Clear(); // Device removal must not leave push-to-talk held.
        if (message.Msg == 0xff)
        {
            if (!GameFocused) _keys.Clear();
            else
            {
                uint size = (uint)_packet.Length;
                int header = 8 + 2 * IntPtr.Size;
                uint read = GetRawInputData(message.LParam, 0x10000003, _packet, ref size, (uint)header);
                if (read <= _packet.Length) _keys.ReadRawPacket(_packet.AsSpan(0, (int)read), header);
            }
        }
        base.WndProc(ref message);
    }
    public void Dispose()
    {
        _pulse.Stop(); _pulse.Dispose(); _keys.Clear();
        if (_registered)
        {
            RawDevice[] devices = [new(1, 2, 1, 0), new(1, 6, 1, 0)];
            RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf<RawDevice>()); _registered = false;
        }
        DestroyHandle();
    }
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RawDevice(ushort page, ushort usage, uint flags, nint target)
    { public readonly ushort Page = page, Usage = usage; public readonly uint Flags = flags; public readonly nint Target = target; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint input, uint command, [Out] byte[] data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
