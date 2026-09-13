using Cranberry.Transport;

namespace Cranberry.Tests.Transport;

public sealed class ReliabilityStageDiagnosticsTests
{
    [Fact]
    public void ReorderedHeldDatagramsCountOnceAndDeliverInOrderWithoutChangingPayloads()
    {
        var d = new SoeDiagnosticCollector();
        var received = new List<byte[]>();
        var inbound = new InboundChannel((bytes, length) => received.Add(bytes[..length])) { Diagnostics = d };
        inbound.Accept(1, [2], false); inbound.Accept(1, [2], false);
        Assert.Equal(1, inbound.HeldCount); Assert.Equal(1, inbound.HeldBytes);
        inbound.Accept(0, [1], false); inbound.Accept(0, [1], false);
        Assert.Equal(new byte[] { 1, 2 }, received.SelectMany(x => x));
        Assert.Equal(0, inbound.HeldBytes);
        var counters = d.TakeCounters();
        Assert.Equal(2, counters[nameof(SoeDiagnosticCounter.ReliableAhead)]);
        Assert.Equal(1, counters[nameof(SoeDiagnosticCounter.ReliableAheadDuplicate)]);
        Assert.Equal(1, counters[nameof(SoeDiagnosticCounter.ReliableBehind)]);
        Assert.Equal(1, d.ReliableHeldWait.TakeSnapshot().Count);
        inbound.Close();
    }

    [Fact]
    public void BundleFragmentAndAckMeasurementsDistinguishOriginalsFromRepairs()
    {
        var diagnostics = new SoeDiagnosticCollector();
        var outbound = new OutboundChannel(new(), _ => { }) { Diagnostics = diagnostics };
        outbound.SendBuffered([1, 2], null, 0);
        outbound.SendBuffered([3, 4], null, 0);
        outbound.Send(new byte[2048], null, 0);
        Assert.Equal(6, outbound.PendingCount); // One bundle and five fragments.
        outbound.Acknowledge(6, 20); // Future ACK cannot acknowledge queued state.
        outbound.Tick(300);
        outbound.Acknowledge(5, 350); // Retransmitted cumulative ACK cannot train RTT.
        outbound.Acknowledge(5, 400); // Duplicate ACK.
        outbound.Send([5], null, 400);
        outbound.Acknowledge(6, 480);
        var counters = diagnostics.TakeCounters();
        Assert.Equal(4, counters[nameof(SoeDiagnosticCounter.AcksReceived)]);
        Assert.Equal(2, counters[nameof(SoeDiagnosticCounter.AcksIgnored)]);
        Assert.Equal(2, counters[nameof(SoeDiagnosticCounter.AcksAdvanced)]);
        Assert.Equal(7, counters[nameof(SoeDiagnosticCounter.AckedDatagrams)]);
        Assert.Equal(1, counters[nameof(SoeDiagnosticCounter.RoundTripSamplesTaken)]);
        Assert.Equal(1, counters[nameof(SoeDiagnosticCounter.RoundTripSamplesExcluded)]);
        var bundles = diagnostics.BufferedMessagesPerDatagram.TakeSnapshot();
        Assert.Equal(1, bundles.Count);
        Assert.Equal(2, bundles.Max);
        var fragments = diagnostics.FragmentsPerMessage.TakeSnapshot();
        Assert.Equal(1, fragments.Count);
        Assert.Equal(5, fragments.Max);
        Assert.Equal(7, diagnostics.ReliableFirstTransmitWait.TakeSnapshot().Count);
        Assert.Equal(0, outbound.PendingCount);
        outbound.Close();
    }

    [Fact]
    public void FirstTransmitTimingWaitsForWindowAndResendsDoNotDoubleCount()
    {
        var d = new SoeDiagnosticCollector();
        var wire = new List<byte[]>();
        var channel = new OutboundChannel(SessionSettings.WithSeed(1), bytes => wire.Add(bytes.ToArray()))
            { Diagnostics = d, SendWindow = 1 };
        long now = Environment.TickCount64;
        channel.Send([1], null, now); channel.Send([2], null, now);
        Assert.Single(wire);
        Assert.Equal(1, d.ReliableFirstTransmitWait.TakeSnapshot().Count);
        channel.Tick(now + 400);
        Assert.Equal(0, d.ReliableFirstTransmitWait.TakeSnapshot().Count);
        channel.Acknowledge(0, now + 450);
        Assert.Equal(1, d.ReliableFirstTransmitWait.TakeSnapshot().Count);
        Assert.Equal(0, channel.QueuedCount);
        channel.Close();
    }
}
