using Cranberry.Harness.Replay;

namespace Cranberry.Harness.Tests;

public sealed class CaptureRedactionTests
{
    [Fact]
    public void ExactRecorderRedactionsRetainTimingAndCountWithoutInventingPacketBytes()
    {
        WithCapture("""
            12:00:00.000 | 127.0.0.1:20043 | ExternalGatewayApi_3 | session-request | 0 | crc=0 id=1 udp=512
            12:00:00.100 | 127.0.0.1:20043 | ExternalGatewayApi_3 | c2s | 1 | 05
            12:00:00.200 | 127.0.0.1:20043 | ExternalGatewayApi_3 | s2c | 74 | [hosted key redacted]
            12:00:00.300 | 127.0.0.1:20043 | ExternalGatewayApi_3 | c2s | 99 | [private social payload redacted]
            """, path =>
        {
            var session = Assert.Single(CaptureSessionReader.ReadSessions(path));
            Assert.Equal(2, session.RedactedMessages);
            Assert.Equal(TimeSpan.FromMilliseconds(300), session.Duration);
            Assert.Equal(new byte[] { 5 }, Assert.Single(session.Messages).Bytes);
            var analysis = Assert.Single(CaptureReplay.Analyse(path));
            Assert.Equal(2, analysis.RedactedMessages);
            Assert.Contains("2 redacted gateway packet(s)", analysis.Render());
            Assert.Contains("coverage is incomplete", analysis.Render());
        });
    }

    [Theory]
    [InlineData("not hex")]
    [InlineData("[hosted key redacted]junk")]
    [InlineData("[HOSTED KEY REDACTED]")]
    [InlineData(" [private social payload redacted]")]
    [InlineData("[unknown redaction]")]
    public void OtherNonHexBodiesStillFailInsteadOfBeingHidden(string body)
    {
        WithCapture($"12:00:00.000 | 127.0.0.1:20043 | ExternalGatewayApi_3 | c2s | 74 | {body}",
            path => Assert.Throws<FormatException>(() => CaptureSessionReader.ReadSessions(path)));
    }

    private static void WithCapture(string content, Action<string> verify)
    {
        string path = Path.Combine(Path.GetTempPath(), "cranberry-redaction-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, content);
            verify(path);
        }
        finally { File.Delete(path); }
    }
}
