using Cranberry.Harness.Protocol;
using Cranberry.Harness.Replay;

namespace Cranberry.Harness.Tests;

public sealed class CaptureSessionIdentityTests : IDisposable
{
    private readonly string _path = Path.GetTempFileName();
    private const string Peer = "127.0.0.1:12345";
    private const string OtherPeer = "127.0.0.1:12346";

    public void Dispose() => File.Delete(_path);

    private static string Line(int milliseconds, string direction, string value,
        string protocol = AugustClient.GatewayProtocolName, string remote = Peer) =>
        $"00:00:00.{milliseconds:D3} | {remote} | {protocol} | {direction} | 2 | {value}";

    private IReadOnlyList<CaptureSession> Read(params string[] lines)
    {
        File.WriteAllLines(_path, lines);
        return CaptureSessionReader.ReadSessions(_path);
    }

    [Fact]
    public void SameEndpointDifferentProtocolsRemainSeparate()
    {
        var sessions = Read(
            Line(0, "session-request", "crc=3 id=1 udp=512", AugustClient.LoginProtocolName),
            Line(1, "session-request", "crc=3 id=1 udp=512"),
            Line(2, "c2s", "0102", AugustClient.LoginProtocolName),
            Line(3, "s2c", "4003"));
        Assert.Equal(2, sessions.Count);
        Assert.Equal(CaptureLink.Login, sessions[0].Link);
        Assert.Equal(CaptureLink.Gateway, sessions[1].Link);
        Assert.Equal(new byte[] { 1, 2 }, Assert.Single(sessions[0].Messages).Bytes);
        Assert.Equal(new byte[] { 0x40, 3 }, Assert.Single(sessions[1].Messages).Bytes);
    }

    [Fact]
    public void InterleavedPeersWithEqualRequestIdsRemainSeparate()
    {
        var sessions = Read(
            Line(0, "session-request", "id=2"),
            Line(1, "session-request", "id=2", remote: OtherPeer),
            Line(2, "c2s", "4001"),
            Line(3, "c2s", "4002", remote: OtherPeer),
            Line(4, "s2c", "4003"));
        Assert.Equal(2, sessions.Count);
        Assert.Equal(Peer, sessions[0].Remote);
        Assert.Equal(OtherPeer, sessions[1].Remote);
        Assert.Equal(2, sessions[0].Messages.Count);
        Assert.Single(sessions[1].Messages);
        Assert.Equal(TimeSpan.FromMilliseconds(4), sessions[0].EndedAt);
        Assert.Equal(TimeSpan.FromMilliseconds(3), sessions[1].EndedAt);
    }

    [Fact]
    public void ChangedIdStartsNewSegmentAndRetryPreservesOriginalStartAndBytes()
    {
        var sessions = Read(
            Line(0, "session-request", "crc=3 id=000000aa udp=512"),
            Line(1, "c2s", "4001"),
            Line(2, "session-request", "crc=3 id=AA udp=512"),
            Line(3, "s2c", "4002"),
            Line(4, "session-request", "crc=3 id=bb udp=512"),
            Line(5, "c2s", "4003"));
        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, s => Assert.True(s.HasSessionRequest));
        Assert.Equal(0xaaU, sessions[0].SessionId);
        Assert.Equal(0xbbU, sessions[1].SessionId);
        Assert.Equal(3U, sessions[0].CrcLength);
        Assert.Equal(512U, sessions[0].UdpLength);
        Assert.Equal(TimeSpan.Zero, sessions[0].StartedAt);
        Assert.Equal(TimeSpan.FromMilliseconds(3), sessions[0].EndedAt);
        Assert.Equal(2, sessions[0].Messages.Count);
        Assert.Equal(CaptureDirection.ServerToClient, sessions[0].Messages[1].Direction);
        Assert.Equal(new byte[] { 0x40, 2 }, sessions[0].Messages[1].Bytes);
        Assert.Equal(TimeSpan.FromMilliseconds(1), Assert.Single(sessions[1].Messages).At);
    }

    [Fact]
    public void MissingRequestPrefixIsNotAssignedToLaterKnownIdIncludingZero()
    {
        var sessions = Read(
            Line(0, "c2s", "4001"),
            Line(1, "session-request", "crc=3 id=0 udp=512"),
            Line(2, "c2s", "4002"),
            Line(3, "session-request", "id=00000000"),
            Line(4, "s2c", "4003"));
        Assert.Equal(2, sessions.Count);
        Assert.False(sessions[0].HasSessionRequest);
        Assert.True(sessions[1].HasSessionRequest);
        Assert.Equal(0U, sessions[1].SessionId);
        Assert.Single(sessions[0].Messages);
        Assert.Equal(2, sessions[1].Messages.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1), sessions[1].StartedAt);
    }

    [Fact]
    public void InvalidOrAbsentRequestIdStartsUnknownSegmentWithoutInheritingKnownIdentity()
    {
        var sessions = Read(
            Line(0, "session-request", "id=5"),
            Line(1, "session-request", "id=not-hex"),
            Line(2, "c2s", "4001"),
            Line(3, "session-request", "crc=3 udp=512"),
            Line(4, "c2s", "4002"),
            Line(5, "session-request", "id=5"),
            Line(6, "c2s", "4003"));
        Assert.Equal(4, sessions.Count);
        Assert.True(sessions[0].HasSessionRequest);
        Assert.Equal(5U, sessions[0].SessionId);
        Assert.Empty(sessions[0].Messages);
        Assert.False(sessions[1].HasSessionRequest);
        Assert.False(sessions[2].HasSessionRequest);
        Assert.Equal(0U, sessions[1].SessionId);
        Assert.Equal(0U, sessions[2].SessionId);
        Assert.Equal(new byte[] { 0x40, 1 }, Assert.Single(sessions[1].Messages).Bytes);
        Assert.Equal(new byte[] { 0x40, 2 }, Assert.Single(sessions[2].Messages).Bytes);
        Assert.True(sessions[3].HasSessionRequest);
        Assert.Equal(5U, sessions[3].SessionId);
        Assert.Equal(TimeSpan.FromMilliseconds(5), sessions[3].StartedAt);
        Assert.Equal(new byte[] { 0x40, 3 }, Assert.Single(sessions[3].Messages).Bytes);
    }

    [Fact]
    public void InvalidRequestPrefixRemainsUnknownAndRedactionsStayInTheirSegment()
    {
        var sessions = Read(
            Line(0, "session-request", "id=100000000"),
            Line(1, "c2s", "[hosted key redacted]"),
            Line(2, "session-request", "id=7"),
            Line(3, "c2s", "[private social payload redacted]"),
            Line(4, "c2s-raw@0", "FFFF"),
            Line(5, "c2s", "4009"));
        Assert.Equal(2, sessions.Count);
        Assert.False(sessions[0].HasSessionRequest);
        Assert.Empty(sessions[0].Messages);
        Assert.All(sessions, s => Assert.Equal(1, s.RedactedMessages));
        Assert.Equal(new byte[] { 0x40, 9 }, Assert.Single(sessions[1].Messages).Bytes);
        Assert.Equal(TimeSpan.FromMilliseconds(3), sessions[1].Messages[0].At);
    }

    [Fact]
    public void UnsupportedProtocolCannotBecomeGateway()
    {
        Assert.Throws<FormatException>(() => Read(Line(0, "c2s", "4001", "ExternalGatewayApi_99")));
    }

    [Theory]
    [InlineData("id=1 id=2")]
    [InlineData("id=1 id=1")]
    [InlineData("id=invalid id=1")]
    [InlineData("id=1 id=")]
    [InlineData("id=+1")]
    [InlineData("id=0x1")]
    [InlineData("id=000000001")]
    [InlineData("id=１")]
    public void AmbiguousOrNonStrictIdsAreUnknownBoundaries(string request)
    {
        var sessions = Read(Line(0, "session-request", "id=1"),
            Line(1, "session-request", request), Line(2, "c2s", "4001"),
            Line(3, "session-request", "id=1"), Line(4, "c2s", "4002"));
        Assert.Equal(3, sessions.Count);
        Assert.False(sessions[1].HasSessionRequest);
        Assert.Equal(new byte[] { 0x40, 1 }, Assert.Single(sessions[1].Messages).Bytes);
        Assert.True(sessions[2].HasSessionRequest);
        Assert.Equal(1U, sessions[2].SessionId);
    }

    [Fact]
    public void SingleAsciiHexIdAcceptsWhitespaceAndMaximumValue()
    {
        var sessions = Read(Line(0, "session-request", "crc=3\tid=FFFFFFFF\tudp=512"),
            Line(1, "session-request", "id=ffffffff"), Line(2, "c2s", "4001"));
        var session = Assert.Single(sessions);
        Assert.True(session.HasSessionRequest);
        Assert.Equal(uint.MaxValue, session.SessionId);
    }
}
