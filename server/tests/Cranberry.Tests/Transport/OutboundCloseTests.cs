using Cranberry.Transport;

namespace Cranberry.Tests.Transport;

public sealed class OutboundCloseTests
{
    [Theory]
    [InlineData("timer")]
    [InlineData("gap")]
    [InlineData("window")]
    public void TransmitMayCloseTheChannelWithoutInvalidatingAnActiveWalk(string path)
    {
        OutboundChannel? channel = null;
        bool closeOnTransmit = false;
        channel = new OutboundChannel(new SessionSettings { UdpLength = 512 }, _ =>
        {
            if (closeOnTransmit) channel!.Close();
        }) { SendWindow = 4 };
        for (int i = 0; i < 8; i++) channel.Send([42], null, 0);
        closeOnTransmit = true;
        if (path == "timer") channel.Tick(301);
        if (path == "gap")
        {
            channel.ResendBefore(1, 50);
            channel.ResendBefore(2, 50);
            channel.ResendBefore(3, 50);
            channel.Tick(75);
        }
        if (path == "window") channel.Acknowledge(0, 50);
        Assert.Equal(0, channel.PendingCount);
        Assert.Equal(0, channel.PendingBytes);
        Assert.Equal(0, channel.InFlightCount);
        channel.Tick(1000);
        channel.Close();
    }

    [Fact]
    public void ClosingOnFirstFragmentCannotLeaveTheRestQueued()
    {
        OutboundChannel? channel = null;
        int writes = 0;
        channel = new OutboundChannel(new SessionSettings { UdpLength = 512 }, _ => { writes++; channel!.Close(); });
        channel.Send(new byte[4096], null, 0);
        channel.SendBuffered([42], null, 1);
        channel.Tick(1000);
        Assert.Equal(1, writes);
        Assert.Equal(0, channel.PendingCount);
        Assert.Equal(0, channel.PendingBytes);
    }
}
