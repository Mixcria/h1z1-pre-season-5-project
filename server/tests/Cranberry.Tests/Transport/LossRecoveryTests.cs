using System.Buffers.Binary;
using Cranberry.Transport;

namespace Cranberry.Tests.Transport;

public sealed class LossRecoveryTests
{
    [Fact]
    public void RoundTripTracksStableAndChangingLatencyWithinBounds()
    {
        var rtt = new RoundTripEstimator();
        Assert.Equal(300, rtt.TimeoutMs(300, 100, 2400));
        rtt.Observe(-1);
        Assert.Equal(0, rtt.Samples);
        rtt.Observe(80);
        Assert.Equal(240, rtt.TimeoutMs(300, 100, 2400));
        rtt.Observe(80);
        Assert.Equal(200, rtt.TimeoutMs(300, 100, 2400));
        for (int i = 0; i < 30; i++) rtt.Observe(80);
        Assert.Equal(100, rtt.TimeoutMs(300, 100, 2400));
        rtt.Observe(500);
        Assert.InRange(rtt.TimeoutMs(300, 100, 2400), 500, 900);
        rtt.Observe(100_000);
        Assert.Equal(2400, rtt.TimeoutMs(300, 100, 2400));
    }

    [Fact]
    public void InvalidAndRetransmittedAcknowledgementsCannotTrainTheClock()
    {
        var channel = new OutboundChannel(new(), _ => { }) { SendWindow = 2 };
        channel.Send([1], null, 0);
        channel.Send([2], null, 0);
        channel.Send([3], null, 0);
        channel.Acknowledge(2, 10); // Queued, never transmitted.
        channel.Acknowledge(ushort.MaxValue, 10);
        Assert.Equal(0, channel.RoundTripSamples);
        Assert.Equal(3, channel.PendingCount);
        channel.Tick(300);
        channel.Acknowledge(1, 340);
        Assert.Equal(0, channel.RoundTripSamples); // Karn ambiguity.
        channel.Acknowledge(2, 420);
        Assert.Equal(1, channel.RoundTripSamples);
        Assert.Equal(80, channel.SmoothedRoundTripMs); // Time on wire, excluding queue time.
        channel.Acknowledge(2, 450);
        Assert.Equal(1, channel.RoundTripSamples);
        channel.Close();
    }

    [Fact]
    public void OnlyMissingEncryptedBytesAreRepairedAndDeliveryRemainsOrdered()
    {
        var sent = new List<byte[]>();
        var delivered = new List<byte[]>();
        var cipher = new Rc4Cipher([1, 2, 3, 4]);
        var decipher = new Rc4Cipher([1, 2, 3, 4]);
        var outbound = new OutboundChannel(new(), d => sent.Add(d.ToArray()));
        var inbound = new InboundChannel((bytes, length) =>
        {
            Span<byte> data = bytes.AsSpan(0, length);
            if (data.Length > 1 && data[0] == 0 && data[1] == 0) data = data[1..];
            decipher.Transform(data);
            delivered.Add(data.ToArray());
        });
        for (int i = 0; i < 8; i++) outbound.Send([(byte)i, 42], cipher, 0);
        long position = cipher.Position;
        for (int i = 1; i < 8; i++)
        {
            inbound.Accept((ushort)i, sent[i].AsSpan(4), false);
            outbound.ResendBefore((ushort)i, 60);
        }
        Assert.Empty(delivered);
        Assert.Equal(8, sent.Count); // A WAN round trip does not consume reordering grace.
        outbound.Tick(85);
        Assert.Equal(9, sent.Count); // Eight originals and only the missing head.
        Assert.Equal(sent[0], sent[8]);
        Assert.Equal(position, cipher.Position);
        Assert.Equal(8, outbound.PendingCount); // OutOfOrder never frees reliable bytes.
        inbound.Accept(0, sent[8].AsSpan(4), false);
        outbound.Acknowledge(inbound.LastInOrder, 140);
        Assert.Equal(8, delivered.Count);
        for (int i = 0; i < 8; i++) Assert.Equal(new byte[] { (byte)i, 42 }, delivered[i]);
        Assert.Equal(0, outbound.PendingCount);
        Assert.Equal(0, outbound.RoundTripSamples);
        inbound.Close(); outbound.Close();
    }

    [Fact]
    public void ReorderingWithinGraceDoesNotCauseRetransmission()
    {
        var channel = new OutboundChannel(new(), _ => { });
        for (int i = 0; i < 4; i++) channel.Send([1], null, 0);
        for (ushort i = 1; i < 4; i++) channel.ResendBefore(i, 10);
        channel.Tick(20);
        Assert.Equal(0, channel.DatagramsResent);
        channel.Acknowledge(3, 24);
        channel.Tick(300);
        Assert.Equal(0, channel.DatagramsResent);
        channel.Close();
    }

    [Fact]
    public void DuplicateAndFutureReportsCannotTriggerFastRepairOrReleaseData()
    {
        var channel = new OutboundChannel(new(), _ => { });
        for (int i = 0; i < 4; i++) channel.Send([1], null, 0);
        for (int i = 0; i < 10_000; i++) channel.ResendBefore(3, 60);
        channel.ResendBefore(5, 60);
        channel.ResendBefore(ushort.MaxValue, 60);
        Assert.Equal(0, channel.DatagramsResent);
        Assert.Equal(4, channel.PendingCount);
        channel.Tick(300);
        Assert.Equal(3, channel.DatagramsResent); // Receipt of 3 suppresses its timeout.
        channel.Close();
    }

    [Fact]
    public void ReportedHeadIsProbedWhenItsCumulativeAcknowledgementWasLost()
    {
        var sent = new List<ushort>();
        var channel = new OutboundChannel(new(), d => sent.Add(BinaryPrimitives.ReadUInt16BigEndian(d.Span[2..])));
        for (int i = 0; i < 4; i++) channel.Send([1], null, 0);
        channel.ResendBefore(3, 60);
        channel.Acknowledge(2, 80); // Received 3 too, but final ACK was lost.
        Assert.Equal(1, channel.PendingCount);
        channel.Tick(300);
        Assert.Equal((ushort)3, sent[^1]);
        Assert.Equal(1, channel.DatagramsResent);
        channel.Acknowledge(3, 380);
        Assert.Equal(0, channel.PendingCount);
        channel.Close();
    }

    [Fact]
    public void FastRepairUsesTheSameBudgetAsTimeoutAndHonoursSequenceWrap()
    {
        var channel = new OutboundChannel(new(), _ => { }) { MaxResendsPerTick = 1 };
        for (int i = 0; i < 65_534; i++)
        {
            channel.Send([1], null, i);
            channel.Acknowledge((ushort)i, i + 1);
        }
        for (int i = 0; i < 5; i++) channel.Send([1], null, 70_000);
        channel.ResendBefore(0, 70_060);
        channel.ResendBefore(1, 70_060);
        channel.ResendBefore(2, 70_060);
        channel.Tick(70_085);
        Assert.Equal(1, channel.DatagramsResent);
        Assert.Equal(5, channel.PendingCount);
        channel.Tick(70_105);
        Assert.Equal(2, channel.DatagramsResent); // Second missing sequence gets the next budget.
        channel.Acknowledge(2, 70_140);
        Assert.Equal(0, channel.PendingCount);
        channel.Close();
    }

    [Fact]
    public void AFullTunnelWindowReorderedOnAWanDoesNotCauseAnImmediateRepairBurst()
    {
        var channel = new OutboundChannel(new(), _ => { }) { SendWindow = 128 };
        for (int i = 0; i < 128; i++) channel.Send([1], null, 0);
        for (ushort i = 64; i < 128; i++) channel.ResendBefore(i, 110);
        channel.Tick(120);
        Assert.Equal(0, channel.DatagramsResent);
        channel.Acknowledge(127, 130); // The delayed first half arrived 20 ms later.
        channel.Tick(400);
        Assert.Equal(0, channel.DatagramsResent);
        Assert.Equal(0, channel.PendingCount);
        channel.Close();
    }
}
