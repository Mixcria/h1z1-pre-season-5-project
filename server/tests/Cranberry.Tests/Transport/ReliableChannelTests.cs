using System.Buffers.Binary;
using Cranberry.Transport;

namespace Cranberry.Tests.Transport;

/// <summary>Drives an OutboundChannel into an InboundChannel through an in-memory link that can reorder, drop, and duplicate.</summary>
public class ReliableChannelTests
{
    private static readonly SessionSettings Settings = new() { UdpLength = 512 };

    private sealed class Link
    {
        public List<byte[]> Sent { get; } = new();
        public List<byte[]> Delivered { get; } = new();
        public OutboundChannel Outbound { get; }
        public InboundChannel Inbound { get; }
        public Rc4Cipher? Encrypt { get; }
        public Rc4Cipher? Decrypt { get; }

        public Link(byte[]? key = null, int sendWindow = 32)
        {
            if (key is not null)
            {
                Encrypt = new Rc4Cipher(key);
                Decrypt = new Rc4Cipher(key);
            }

            Outbound = new OutboundChannel(Settings, d => Sent.Add(d.ToArray())) { SendWindow = sendWindow };
            Inbound = new InboundChannel((buffer, length) =>
            {
                byte[] message = buffer.AsSpan(0, length).ToArray();
                Decrypt?.Transform(message);
                Delivered.Add(message);
            });
        }

        public void Send(byte[] message, long now = 0) => Outbound.Send(message, Encrypt, now);

        public void Receive(byte[] datagram)
        {
            var opcode = (SoeOpcode)BinaryPrimitives.ReadUInt16BigEndian(datagram);
            ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(2));
            Inbound.Accept(sequence, datagram.AsSpan(4), opcode == SoeOpcode.DataFragment);
        }

        public void ReceiveAll(IEnumerable<byte[]> datagrams)
        {
            foreach (byte[] d in datagrams)
            {
                Receive(d);
            }
        }
    }

    private static byte[] Pattern(int length, int seed = 1)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)(i * 31 + seed);
        }

        return data;
    }

    [Fact]
    public void ShortMessageTravelsInOneDataDatagram()
    {
        var link = new Link();
        byte[] message = Pattern(100);

        link.Send(message);

        Assert.Single(link.Sent);
        Assert.Equal((ushort)SoeOpcode.Data, BinaryPrimitives.ReadUInt16BigEndian(link.Sent[0]));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(link.Sent[0].AsSpan(2)));
        Assert.Equal(104, link.Sent[0].Length);

        link.ReceiveAll(link.Sent);
        Assert.Equal([message], link.Delivered);
        Assert.True(link.Inbound.AckPending);
        Assert.Equal(0, link.Inbound.LastInOrder);
    }

    [Fact]
    public void LongMessageIsFragmentedWithinUdpLengthAndReassembled()
    {
        var link = new Link();
        byte[] message = Pattern(5000);

        link.Send(message);

        Assert.True(link.Sent.Count > 1);
        Assert.All(link.Sent, d => Assert.True(d.Length <= 512));
        Assert.All(link.Sent, d => Assert.Equal((ushort)SoeOpcode.DataFragment, BinaryPrimitives.ReadUInt16BigEndian(d)));
        Assert.Equal(5000u, BinaryPrimitives.ReadUInt32BigEndian(link.Sent[0].AsSpan(4)));

        link.ReceiveAll(link.Sent);
        Assert.Equal([message], link.Delivered);
    }

    [Fact]
    public void ExactlyMaxPayloadIsNotFragmented()
    {
        var link = new Link();
        link.Send(Pattern(Settings.MaxReliablePayload));
        Assert.Single(link.Sent);
        Assert.Equal(512, link.Sent[0].Length);
    }

    [Fact]
    public void ReorderedDatagramsAreDeliveredInSequence()
    {
        var link = new Link();
        byte[] first = Pattern(3000, 1);
        byte[] second = Pattern(50, 2);
        byte[] third = Pattern(700, 3);
        link.Send(first);
        link.Send(second);
        link.Send(third);

        var shuffled = link.Sent.ToList();
        shuffled.Reverse();
        link.ReceiveAll(shuffled);

        Assert.Equal([first, second, third], link.Delivered);
        Assert.Equal(0, link.Inbound.HeldCount);
        Assert.Equal((ushort)(link.Sent.Count - 1), link.Inbound.LastInOrder);
    }

    [Fact]
    public void GapReportsOutOfOrderAndFillsWhenTheMissingDatagramArrives()
    {
        var link = new Link();
        link.Send(Pattern(10, 1));
        link.Send(Pattern(10, 2));

        link.Receive(link.Sent[1]);
        Assert.Empty(link.Delivered);
        Assert.Equal((ushort)1, link.Inbound.OutOfOrderPending);
        Assert.False(link.Inbound.AckPending);

        link.Receive(link.Sent[0]);
        Assert.Equal(2, link.Delivered.Count);
        Assert.Equal(1, link.Inbound.LastInOrder);
        Assert.True(link.Inbound.AckPending);
        Assert.Null(link.Inbound.OutOfOrderPending);
    }

    [Fact]
    public void DuplicateIsIgnoredButReAcknowledged()
    {
        var link = new Link();
        link.Send(Pattern(10));
        link.Receive(link.Sent[0]);
        link.Inbound.ClearSignals();

        link.Receive(link.Sent[0]);

        Assert.Single(link.Delivered);
        Assert.True(link.Inbound.AckPending);
    }

    [Fact]
    public void AcknowledgementReleasesEverythingUpToTheSequence()
    {
        var link = new Link();
        for (int i = 0; i < 5; i++)
        {
            link.Send(Pattern(10, i));
        }

        Assert.Equal(5, link.Outbound.PendingCount);
        link.Outbound.Acknowledge(2);
        Assert.Equal(2, link.Outbound.PendingCount);
        link.Outbound.Acknowledge(4);
        Assert.Equal(0, link.Outbound.PendingCount);
    }

    [Fact]
    public void FutureOrStaleAcknowledgementDoesNotReleasePendingDatagrams()
    {
        var link = new Link();
        for (int i = 0; i < 3; i++)
        {
            link.Send(Pattern(10, i));
        }

        link.Outbound.Acknowledge(100); // Never sent.
        link.Outbound.Acknowledge(ushort.MaxValue); // Immediately behind sequence zero.
        Assert.Equal(3, link.Outbound.PendingCount);

        link.Outbound.Acknowledge(1);
        Assert.Equal(1, link.Outbound.PendingCount);

        link.Outbound.Acknowledge(0); // Already acknowledged and now stale.
        Assert.Equal(1, link.Outbound.PendingCount);
    }

    [Fact]
    public void UnacknowledgedDatagramsAreResentVerbatimAfterTheInterval()
    {
        var link = new Link();
        link.Send(Pattern(10), now: 1000);
        byte[] original = link.Sent[0];

        link.Outbound.Tick(1100);
        Assert.Single(link.Sent);

        link.Outbound.Tick(1400);
        Assert.Equal(2, link.Sent.Count);
        Assert.Equal(original, link.Sent[1]);
        Assert.Equal(1, link.Outbound.DatagramsResent);
    }

    /// <summary>
    /// docs/108 (D204). The peer-loss deadline is REAL TIME, not 25 fixed rounds. The August
    /// client stops servicing its socket for the whole of a synchronous world load, and the old
    /// 25 × 300 ms = 8.4 s budget closed sessions on a client that was alive and busy — three
    /// times on 2026-09-03 alone (`logs\host-20260903-073943.log` 07:41:30, `-075246` 07:53:31,
    /// `-075709` 07:58:29). Nothing may be declared lost before
    /// <see cref="OutboundChannel.PeerSilenceLimitMs"/>, and everything must be after it.
    /// </summary>
    [Fact]
    public void PeerIsDeclaredLostOnlyAfterTheSilenceLimit()
    {
        var link = new Link();
        link.Send(Pattern(10), now: 0);

        for (long now = 1000; now <= 44_000; now += 1000)
        {
            link.Outbound.Tick(now);
            Assert.False(link.Outbound.PeerLost, $"declared the peer lost after only {now} ms");
        }

        link.Outbound.Tick(46_000);
        Assert.True(link.Outbound.PeerLost);
    }

    /// <summary>
    /// The resend interval doubles towards its ceiling while the peer is quiet, so a client
    /// stalled inside a world load is not handed its whole backlog every 300 ms — the flood that
    /// made the stall worse. Ten seconds of silence used to cost 33 resends of one datagram; it
    /// costs six now.
    /// </summary>
    [Fact]
    public void ResendsBackOffWhileThePeerStaysQuiet()
    {
        var link = new Link();
        link.Send(Pattern(10), now: 0);

        for (long now = 20; now <= 10_000; now += 20)
        {
            link.Outbound.Tick(now);
        }

        Assert.False(link.Outbound.PeerLost);
        Assert.InRange(link.Outbound.DatagramsResent, 5, 8);
    }

    /// <summary>
    /// docs/108 (D204). A message far larger than the peer's socket buffer is metered by the send
    /// window instead of being blasted at it: the zoning burst's 1.27 MB appearance table is ~2,500
    /// datagrams, and handing all of them to the socket at once is what the ground-loot slicer in
    /// ZoneService already had to work around. Nothing is dropped — the window empties as the peer
    /// acknowledges, and every fragment arrives in order.
    /// </summary>
    [Fact]
    public void ALargeMessageIsMeteredByTheSendWindowAndStillArrivesWhole()
    {
        var link = new Link();
        byte[] message = Pattern(400_000, seed: 7);
        link.Send(message, now: 0);

        Assert.True(
            link.Sent.Count <= link.Outbound.SendWindow,
            $"the whole message went to the socket at once ({link.Sent.Count} datagram(s))");
        Assert.True(link.Outbound.QueuedCount > 0, "nothing was held back for the window");

        // Deliver and acknowledge as a peer would, until the channel has drained.
        int delivered = 0;
        long now = 0;
        for (int round = 0; round < 200 && link.Outbound.PendingCount > 0; round++)
        {
            for (; delivered < link.Sent.Count; delivered++)
            {
                link.Receive(link.Sent[delivered]);
            }

            link.Outbound.Acknowledge((ushort)(delivered - 1), now);
            now += 20;
            link.Outbound.Tick(now);
        }

        Assert.Equal(0, link.Outbound.PendingCount);
        Assert.Equal(0, link.Outbound.DatagramsResent);
        Assert.Single(link.Delivered);
        Assert.Equal(message, link.Delivered[0]);
    }

    [Fact]
    public void TunnelWindowReducesBootstrapRoundTripsWithoutChangingTheReassembledMessage()
    {
        byte[] message = Pattern(900_000, seed: 9);
        long Complete(int window)
        {
            var link = new Link(sendWindow: window);
            link.Send(message, now: 0);
            int delivered = 0;
            long elapsed = 0;
            while (link.Outbound.PendingCount > 0 && elapsed < 20000)
            {
                int end = link.Sent.Count;
                Assert.InRange(end - delivered, 1, window);
                for (; delivered < end; delivered++) link.Receive(link.Sent[delivered]);
                elapsed += 100; // Delayed cumulative ACK, representative of a WAN round trip.
                link.Outbound.Acknowledge((ushort)(delivered - 1), elapsed);
                link.Outbound.Tick(elapsed);
            }
            Assert.Equal(0, link.Outbound.PendingCount);
            Assert.Equal(0, link.Outbound.DatagramsResent);
            Assert.Equal(message, Assert.Single(link.Delivered));
            return elapsed;
        }
        long udp = Complete(32), tunnel = Complete(128);
        Assert.True(tunnel <= udp * .27, $"UDP {udp} ms; tunnel {tunnel} ms");
    }

    [Fact]
    public void EncryptedMessagesOfMixedSizesSurviveFragmentationAndReordering()
    {
        byte[] key = Convert.FromBase64String("F70IaxuU8C/w7FPXY1ibXw==");
        var link = new Link(key);
        var messages = new List<byte[]> { Pattern(1, 1), Pattern(2000, 2), Pattern(508, 3), Pattern(509, 4), Pattern(77, 5), Pattern(9000, 6) };
        foreach (byte[] m in messages)
        {
            link.Send(m);
        }

        // Ciphertext must differ from plaintext on the wire.
        Assert.NotEqual(messages[1].AsSpan(0, 100).ToArray(), link.Sent[1].AsSpan(4, 100).ToArray());

        var rng = new Random(12345);
        var shuffled = link.Sent.OrderBy(_ => rng.Next()).ToList();
        link.ReceiveAll(shuffled);

        Assert.Equal(messages, link.Delivered);
    }

    [Fact]
    public void SequenceNumbersWrapAroundCleanly()
    {
        var link = new Link();
        for (int i = 0; i < 70000; i++)
        {
            link.Send([(byte)i, (byte)(i >> 8)]);
            link.Receive(link.Sent[^1]);
            link.Outbound.Acknowledge(link.Inbound.LastInOrder);
        }

        Assert.Equal(70000, link.Delivered.Count);
        Assert.Equal(0, link.Outbound.PendingCount);
        Assert.Equal((ushort)(70000 % 65536), link.Outbound.NextSequence);
    }

    [Fact]
    public void WholeDatagramInsideAFragmentGroupIsAProtocolError()
    {
        var link = new Link();
        link.Send(Pattern(3000));
        link.Send(Pattern(5));

        link.Receive(link.Sent[0]);
        byte[] whole = link.Sent[^1];
        BinaryPrimitives.WriteUInt16BigEndian(whole.AsSpan(2), 1); // pretend it is the next in sequence
        Assert.Throws<SoeProtocolException>(() => link.Receive(whole));
    }

    [Fact]
    public void DuplicateAtHeldCapacityDoesNotRejectOrReplaceTheAdmittedDatagram()
    {
        var received = new List<byte>();
        var inbound = new InboundChannel((bytes, length) => received.Add(bytes[0]));
        try
        {
            for (int i = 1; i <= InboundChannel.MaxHeldAhead; i++)
                inbound.Accept((ushort)i, [(byte)i], false);
            inbound.Accept(1, [0xff], false);
            Assert.Equal(InboundChannel.MaxHeldAhead, inbound.HeldCount);
            Assert.Throws<SoeProtocolException>(() => inbound.Accept(5000, [1], false));
            inbound.Accept(0, [0], false);
            Assert.Equal(InboundChannel.MaxHeldAhead + 1, received.Count);
            Assert.Equal(1, received[1]);
            Assert.Equal(0, inbound.HeldCount);
            Assert.Equal(0, inbound.HeldBytes);
        }
        finally { inbound.Close(); }
    }

    [Fact]
    public void ClosingInboundChannelClearsHeldAndPartiallyReassembledBuffers()
    {
        var delivered = new List<byte[]>();
        var inbound = new InboundChannel((buffer, length) =>
            delivered.Add(buffer.AsSpan(0, length).ToArray()));

        byte[] fragmentHead = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(fragmentHead, 10u);
        fragmentHead[4] = 0xAA;
        fragmentHead[5] = 0xBB;
        inbound.Accept(0, fragmentHead, isFragment: true);
        inbound.Accept(2, [0xCC], isFragment: false);

        Assert.True(inbound.ReassemblyInProgress);
        Assert.Equal(1, inbound.HeldCount);

        inbound.Close();
        inbound.Close(); // Cleanup is deliberately idempotent.

        Assert.False(inbound.ReassemblyInProgress);
        Assert.Equal(0, inbound.HeldCount);
        Assert.Empty(delivered);
    }
}
