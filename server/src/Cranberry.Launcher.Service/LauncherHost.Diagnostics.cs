using System.Diagnostics;

namespace Cranberry.Launcher.Service;

public sealed partial class LauncherHost
{
    private readonly LauncherDiagnostics? _diagnostics;

    /// <summary>
    /// Copies and resets aggregate counters/timings since the last capture. Connection gauges do
    /// not reset. Safe from the exporter thread; does not inspect ZoneService's mutable world state.
    /// </summary>
    public object CaptureDiagnostics()
    {
        int tunnels = _tunnels.Count;
        int voice = _voice.ConnectionCount;
        return _diagnostics?.Capture(tunnels, voice)
            ?? (object)new { Enabled = false, ActiveTunnelRoutes = tunnels, ActiveVoiceConnections = voice };
    }

    private long DiagnosticTimestamp() => _diagnostics is null ? 0 : Stopwatch.GetTimestamp();

    private void ReleaseGate(long waitStarted, long acquired)
    {
        long ended = DiagnosticTimestamp();
        _gate.Release();
        // Never take histogram locks while holding the account/store semaphore.
        if (_diagnostics is { } diagnostics)
        {
            diagnostics.GateWait.RecordTicks(acquired - waitStarted);
            diagnostics.GateHold.RecordTicks(ended - acquired);
        }
    }
}
