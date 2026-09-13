using System.Buffers.Binary;
using System.Net;
using Cranberry.Launcher.Core;

namespace Cranberry.Launcher.Tests;

public sealed class TunnelSessionRouteTests
{
    private static byte[] Request(uint session)
    {
        byte[] bytes = [0, 1, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 2, 0, (byte)'T', 0];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(6), session);
        return bytes;
    }
    private static byte[] Reply(uint session)
    {
        byte[] bytes = [0, 2, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(2), session);
        return bytes;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReloginDiscardsOldFramesUntilItsOwnSessionReply(bool reusePort)
    {
        var route = new TunnelSessionRoute();
        var previous = new IPEndPoint(IPAddress.Loopback, 12001);
        var next = new IPEndPoint(IPAddress.Loopback, reusePort ? 12001 : 12002);
        Assert.True(route.AcceptClient(previous, Request(100)));
        Assert.Equal(previous, route.ServerDestination(Reply(100)));
        Assert.Equal(previous, route.ServerDestination([0, 9, 0, 1, 42]));
        Assert.True(route.AcceptClient(next, Request(101)));
        Assert.Null(route.ServerDestination([0, 9, 0, 2, 43]));
        Assert.Null(route.ServerDestination(Reply(100)));
        Assert.Null(route.ServerDestination([0, 2]));
        Assert.Equal(next, route.ServerDestination(Reply(101)));
        Assert.Equal(next, route.ServerDestination([0, 9, 0, 0, 44]));
    }

    [Fact]
    public void UnrelatedLocalSocketCannotChangeDestinationWithoutASessionRequest()
    {
        var route = new TunnelSessionRoute();
        var game = new IPEndPoint(IPAddress.Loopback, 12001);
        var other = new IPEndPoint(IPAddress.Loopback, 12002);
        Assert.True(route.AcceptClient(game, Request(42)));
        Assert.Equal(game, route.ServerDestination(Reply(42)));
        Assert.False(route.AcceptClient(other, [0, 1]));
        Assert.False(route.AcceptClient(other, [0, 9, 0, 0, 99]));
        Assert.Equal(game, route.ServerDestination([0, 9, 0, 0, 42]));
    }
}
