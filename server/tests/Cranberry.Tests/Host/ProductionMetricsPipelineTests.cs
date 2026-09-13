using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Cranberry.Host;
using Cranberry.Host.Config;
using Cranberry.Transport;
using Cranberry.Zone;

namespace Cranberry.Tests.Host;

public sealed class ProductionMetricsPipelineTests
{
    [Fact]
    public async Task LiveListenersAndCollectorWriteMeasuredPrivateJsonOutsideTheOwnerThreads()
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-production-pipeline", Guid.NewGuid().ToString("N"));
        var defaults = CranberryConfig.Defaults();
        var config = defaults with
        {
            Root = defaults.Root with { Path = root },
            Metrics = new() { Enabled = true, IntervalMs = 1000, MaxSessions = 2, NodeId = "pipeline-test" },
        };
        var log = new SilentLog();
        var zone = new ZoneService(log, new Recorder()) { DiagnosticsEnabled = true };
        using var login = new SoeListener(new(IPAddress.Loopback, 0), new Echo(), log, new() { EnableDiagnostics = true });
        using var gateway = new SoeListener(new(IPAddress.Loopback, 0), zone, log, new() { EnableDiagnostics = true });
        zone.Post = gateway.Post;
        login.Start(); gateway.Start();
        ProductionMetrics? metrics = ProductionMetrics.TryStart(config, login, gateway, zone, log);
        Assert.NotNull(metrics);
        await using (metrics)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            byte[] request = [0, 1, 0, 0, 0, 3, 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 2, 0, (byte)'T', 0];
            await client.SendAsync(request, login.LocalEndPoint, deadline.Token);
            Assert.Equal((byte)SoeOpcode.SessionReply, (await client.ReceiveAsync(deadline.Token)).Buffer[1]);
            await client.SendAsync(new byte[] { 0x46, 0xAA }, login.LocalEndPoint, deadline.Token);
            Assert.Equal(new byte[] { 0x46, 0xAA }, (await client.ReceiveAsync(deadline.Token)).Buffer[4..]);
            // The collector sees deliberately observable posted work without changing SOE work budgets.
            gateway.Post(() => Thread.Sleep(25));
            string? capture = null;
            while (capture is null)
            {
                await Task.Delay(50, deadline.Token);
                capture = Directory.GetFiles(Path.Combine(root, "metrics"), "*.jsonl")
                    .FirstOrDefault(path => new FileInfo(path).Length > 0);
            }
            string line;
            using (var input = new FileStream(capture, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(input)) line = (await reader.ReadLineAsync(deadline.Token))!;
            using var json = JsonDocument.Parse(line);
            var row = json.RootElement;
            Assert.Equal("production-window", row.GetProperty("type").GetString());
            Assert.Equal("ok", row.GetProperty("login").GetProperty("status").GetString());
            Assert.Equal("ok", row.GetProperty("gateway").GetProperty("status").GetString());
            var transport = row.GetProperty("login").GetProperty("data");
            Assert.True(transport.GetProperty("windowSeconds").GetDouble() > 0);
            Assert.Equal(1, transport.GetProperty("counters").GetProperty("applicationMessagesReceived").GetInt64());
            var gatewayData = row.GetProperty("gateway").GetProperty("data");
            Assert.True(gatewayData.GetProperty("gameplay").GetProperty("enabled").GetBoolean());
            Assert.True(gatewayData.GetProperty("transport").GetProperty("timings").GetProperty("postedWork")
                .GetProperty("maxMs").GetDouble() >= 20);
            Assert.True(row.GetProperty("process").GetProperty("rssBytes").GetInt64() > 0);
            Assert.DoesNotContain(root, line);
            Assert.DoesNotContain("127.0.0.1", line);
            Assert.DoesNotContain("credential-canary", line);

            // Optional explicit evidence destination; ordinary unit tests leave only their unique temp run.
            string? evidence = Environment.GetEnvironmentVariable("CRANBERRY_METRICS_TEST_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                Directory.CreateDirectory(evidence);
                string target = Path.Combine(evidence, $"pipeline-smoke-{Guid.NewGuid():N}.jsonl");
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                await output.WriteAsync(System.Text.Encoding.UTF8.GetBytes(line + "\n"), deadline.Token);
            }
        }
    }

    private sealed class Echo : ISoeService
    {
        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) => SessionDecision.Clear;
        public void OnConnected(SoeConnection connection) { }
        public void OnDisconnected(SoeConnection connection, DisconnectCause cause) { }
        public void OnMessage(SoeConnection connection, Span<byte> message) => connection.Send(message);
    }
    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }
    private sealed class Recorder : IPacketRecorder
    {
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
    }
}
