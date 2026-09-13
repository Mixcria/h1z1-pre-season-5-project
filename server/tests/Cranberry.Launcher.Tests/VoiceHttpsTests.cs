using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cranberry.Launcher.Core;
using Cranberry.Launcher.Core.Voice;
using Cranberry.Launcher.Service;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.World;

namespace Cranberry.Launcher.Tests;

public sealed class VoiceHttpsTests
{
    private sealed class PausedUi : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _work = new();
        private volatile bool _released;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_work)
            {
                if (!_released) { _work.Add((callback, state)); return; }
            }
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
        public Task Connect(ProximityVoiceClient client, LauncherSettings settings, string token, CancellationToken ct)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            new Thread(() =>
            {
                SetSynchronizationContext(this);
                try
                {
                    var connecting = client.Connect(settings, token, ct);
                    while (!connecting.IsCompleted)
                        if (_work.TryTake(out var action, 20)) action.Callback(action.State);
                    connecting.GetAwaiter().GetResult();
                    done.SetResult();
                    // Stop pumping: simulate a launcher UI busy for the rest of the voice test.
                }
                catch (Exception ex) { done.SetException(ex); }
            }) { IsBackground = true }.Start();
            return done.Task;
        }
        public void Dispose()
        {
            lock (_work)
            {
                _released = true;
                while (_work.TryTake(out var action)) ThreadPool.QueueUserWorkItem(_ => action.Callback(action.State));
            }
        }
    }

    private sealed class Recorder : ITransportLog, IPacketRecorder, IPeerSink
    {
        public bool IsOpen => true;
        public void Send(byte[] packet) { }
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes) { }
    }
    private sealed class World
    {
        public readonly object Gate = new();
        public ZoneService Zone { get; }
        private readonly Recorder _log = new();
        public World() => Zone = new ZoneService(_log, _log, new GatewayTicketRegistry(), new ZoneOptions())
            { Post = action => { lock (Gate) action(); } };
        public object Add(string account, ulong guid, float x)
        {
            // This fixture supplies only the authoritative game projection. Socket authentication
            // is exercised independently through the real HTTPS register / voice endpoints below.
            Type stateType = typeof(ZoneService).GetNestedType("GatewaySessionState", BindingFlags.NonPublic)!;
            object state = Activator.CreateInstance(stateType, nonPublic: true)!;
            Set(state, "AccountId", account); Set(state, "Guid", guid); Set(state, "Authenticated", true);
            Set(state, "Hitpoints", 10000u); Set(state, "Match", "InMatch");
            var peer = new PeerSession(guid, _log) { Position = new(x, 0, 0), MatchId = 1, InMatch = true };
            peer.SetPose([]); Set(state, "Peer", peer);
            var connection = (SoeConnection)RuntimeHelpers.GetUninitializedObject(typeof(SoeConnection));
            object sessions = typeof(ZoneService).GetField("_accountSessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Zone)!;
            lock (Gate) sessions.GetType().GetMethod("TryAdd")!.Invoke(sessions, [connection, state]);
            return state;
        }
        public void Set(object state, string name, object value)
        {
            lock (Gate)
            {
                var property = state.GetType().GetProperty(name)!;
                property.SetValue(state, property.PropertyType.IsEnum ? Enum.Parse(property.PropertyType, (string)value) : value);
            }
        }
    }
    private static async Task Eventually(Func<bool> check, CancellationToken ct)
    { while (!check()) await Task.Delay(20, ct); }

    [Fact]
    public async Task DeadIdleVoiceSocketReleasesTheAccountWhileAHealthyIdleSocketSurvives()
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-voice-heartbeat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var options = new LauncherHostOptions { Port = port };
        File.WriteAllText(Path.Combine(root, "launcher-host.json"), JsonSerializer.Serialize(options));
        var world = new World();
        try
        {
            await using var host = new LauncherHost(root, new LocalAccountDirectory("owner"), world.Zone, 20042, 20043);
            await host.StartAsync();
            var settings = new LauncherSettings { ServerUrl = $"https://127.0.0.1:{port}/", CertificateSha256 = host.CertificateSha256 };
            using var http = LauncherConnection.CreateHttp(settings);
            async Task<AuthSession> Register(string name)
            {
                using var response = await http.PostAsJsonAsync("api/register", new Credentials(name, "voice-fixture-password", options.JoinCode));
                response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<AuthSession>())!;
            }
            var alice = await Register("Alice"); var bob = await Register("Bobby");
            await using var healthy = new ProximityVoiceClient(); await healthy.Connect(settings, alice.Token);
            using var stalled = new System.Net.WebSockets.ClientWebSocket();
            stalled.Options.SetRequestHeader("Authorization", "Bearer " + bob.Token);
            stalled.Options.KeepAliveInterval = TimeSpan.Zero;
            stalled.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                LauncherConnection.ValidateCertificate(certificate, errors, settings.CertificateSha256);
            await stalled.ConnectAsync(new Uri($"wss://127.0.0.1:{port}/api/voice/proximity"), CancellationToken.None);
            // Never receive: this peer cannot process the server's Ping or answer its nonce.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await Task.Delay(46000, timeout.Token);
            bool released = false;
            while (!released)
            {
                await using var replacement = new ProximityVoiceClient();
                try { await replacement.Connect(settings, bob.Token, timeout.Token); released = true; }
                catch (System.Net.WebSockets.WebSocketException) { await Task.Delay(500, timeout.Token); }
            }
            Assert.True(healthy.Connected); Assert.False(healthy.Completion.IsCompleted);
            Assert.False(healthy.CanTalk); // Heartbeats do not grant menu accounts voice permission.
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RealPinnedTlsAuthenticatesVoiceRoutesOpusAndStopsAtDeathMenuAndMute()
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-voice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var options = new LauncherHostOptions { Port = port };
        File.WriteAllText(Path.Combine(root, "launcher-host.json"), JsonSerializer.Serialize(options));
        var world = new World();
        try
        {
            await using var host = new LauncherHost(root, new LocalAccountDirectory("owner"), world.Zone, 20042, 20043);
            await host.StartAsync();
            var settings = new LauncherSettings { ServerUrl = $"https://localhost:{port}/", CertificateSha256 = host.CertificateSha256 };
            using var http = LauncherConnection.CreateHttp(settings);
            async Task<AuthSession> Register(string name)
            {
                using var response = await http.PostAsJsonAsync("api/register", new Credentials(name, "voice-fixture-password", options.JoinCode));
                response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<AuthSession>())!;
            }
            AuthSession alice = await Register("Alice"), bob = await Register("Bobby");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var invalid = new ProximityVoiceClient();
            await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(() => invalid.Connect(settings, new string('0', 64), timeout.Token));
            await using var wrongPin = new ProximityVoiceClient();
            await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(() => wrongPin.Connect(settings with { CertificateSha256 = new('0', 64) }, alice.Token, timeout.Token));
            await using var a = new ProximityVoiceClient(); await using var b = new ProximityVoiceClient();
            using var pausedUi = new PausedUi();
            await pausedUi.Connect(a, settings, alice.Token, timeout.Token);
            await b.Connect(settings, bob.Token, timeout.Token);
            Assert.False(a.CanTalk); Assert.False(a.Submit(VoiceTests.Tone()));
            await using var duplicate = new ProximityVoiceClient();
            await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(() => duplicate.Connect(settings, alice.Token, timeout.Token));
            object aliceState = world.Add(alice.AccountId, 1, 0), bobState = world.Add(bob.AccountId, 2, 3);
            await Eventually(() => a.CanTalk && b.CanTalk, timeout.Token);
            Assert.False(a.Submit(VoiceTests.Tone())); // Permission alone never opens a microphone or transmits.
            a.SetTransmitting(true);
            Assert.True(a.Submit(VoiceTests.Tone()));
            await Task.Delay(20, timeout.Token); Assert.True(a.Submit(VoiceTests.Tone(1)));
            await Eventually(() => b.Mixer.BufferedSpeakers > 0, timeout.Token);
            Assert.Equal(0, a.Mixer.BufferedSpeakers);
            b.SetDeafened(true); Assert.False(b.CanTalk); Assert.Equal(0, b.Mixer.BufferedSpeakers);
            b.SetDeafened(false);
            await Eventually(() => b.CanTalk, timeout.Token);
            world.Set(aliceState, "Hitpoints", 0u);
            await Eventually(() => !a.CanTalk, timeout.Token);
            Assert.False(a.Submit(VoiceTests.Tone()));
            world.Set(aliceState, "Hitpoints", 10000u);
            world.Set(aliceState, "Match", "Menu");
            await Task.Delay(250, timeout.Token); Assert.False(a.CanTalk);
            world.Set(aliceState, "Match", "InMatch");
            await Eventually(() => a.CanTalk, timeout.Token);
            world.Set(bobState, "Authenticated", false);
            await Eventually(() => !b.CanTalk, timeout.Token);
        }
        finally { Directory.Delete(root, true); }
    }
}
