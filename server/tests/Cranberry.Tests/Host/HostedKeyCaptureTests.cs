using System.Net;
using System.Text;
using Cranberry.Host;
using Cranberry.Transport;

namespace Cranberry.Tests.Host;

public sealed class HostedKeyCaptureTests
{
    [Fact]
    public void IssuedAndRedeemedKeysAreRedactedWithoutLosingOrdinaryPackets()
    {
        string path = Path.Combine(Path.GetTempPath(), "cranberry-hosted-captures", Guid.NewGuid().ToString("N"), "wire.txt");
        var endpoint = new Endpoint();
        var request = new SessionRequest(3, 1, 512, "ExternalGatewayApi_3");
        var connection = new SoeConnection(new(IPAddress.Loopback, 20043), in request, new(),
            SessionDecision.Clear, endpoint, endpoint, (_, _) => { }, 0);
        const string secret = "HGK-0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        using (var recorder = new FilePacketRecorder(path))
        {
            recorder.RecordMessage(connection, "s2c", Encoding.UTF8.GetBytes("Key (save now): " + secret));
            recorder.RecordMessage(connection, "c2s", Encoding.UTF8.GetBytes("redeem " + secret));
            // A hand-typed, case-folded copy of a live key is still the key once upper-cased.
            recorder.RecordMessage(connection, "c2s", Encoding.UTF8.GetBytes("redeem " + secret.ToLowerInvariant()));
            recorder.RecordMessage(connection, "c2s", [0x0c, 0xec, 0x08, 0x00]);
            recorder.RecordMessage(connection, "c2s", Encoding.UTF8.GetBytes("CRANBERRY_OVERLAY_V1 send|friend|id|private-body"));
            recorder.RecordMessage(connection, "s2c", Encoding.UTF8.GetBytes("@cranberry/overlay/1;H|friend;private-body"));
        }
        string capture = File.ReadAllText(path);
        Assert.DoesNotContain(secret, capture);
        Assert.DoesNotContain(Convert.ToHexString(Encoding.UTF8.GetBytes(secret)), capture);
        Assert.DoesNotContain(Convert.ToHexString(Encoding.UTF8.GetBytes(secret.ToLowerInvariant())), capture);
        Assert.Equal(3, capture.Split("[hosted key redacted]").Length - 1);
        Assert.Contains("0CEC0800", capture);
        Assert.Equal(2, capture.Split("[private social payload redacted]").Length - 1);
        Assert.DoesNotContain(Convert.ToHexString(Encoding.UTF8.GetBytes("private-body")), capture);
    }

    private sealed class Endpoint : ISoeService, ITransportLog
    {
        public SessionDecision OnSessionRequest(IPEndPoint remote, in SessionRequest request) => SessionDecision.Clear;
        public void OnConnected(SoeConnection connection) { }
        public void OnMessage(SoeConnection connection, Span<byte> message) { }
        public void OnDisconnected(SoeConnection connection, DisconnectCause cause) { }
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }
}
