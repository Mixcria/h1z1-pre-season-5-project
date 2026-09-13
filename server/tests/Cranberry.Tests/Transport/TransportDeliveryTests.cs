using Cranberry.Transport;

namespace Cranberry.Tests.Transport;

public sealed class TransportDeliveryTests
{
    [Fact]
    public void DuplicateAtReceiveCapacityStillAllowsLossRecovery()
    {
        int delivered = 0;
        var inbound = new InboundChannel((bytes, length) =>
        {
            Assert.Equal(1, length);
            Assert.Equal(42, bytes[0]); // A duplicate cannot replace already held ciphertext.
            delivered++;
        });
        try
        {
            for (ushort sequence = 1; sequence <= InboundChannel.MaxHeldAhead; sequence++)
                inbound.Accept(sequence, [42], false);
            inbound.Accept(1, [99], false); // A retry allocates nothing and must not disconnect.
            Assert.Equal(InboundChannel.MaxHeldAhead, inbound.HeldBytes);
            Assert.Throws<SoeProtocolException>(() => inbound.Accept(InboundChannel.MaxHeldAhead + 1, [42], false));
            inbound.Accept(0, [42], false);
            Assert.Equal(InboundChannel.MaxHeldAhead + 1, delivered);
            Assert.Equal(0, inbound.HeldBytes);
            Assert.Equal(InboundChannel.MaxHeldAhead, inbound.LastInOrder);
        }
        finally { inbound.Close(); }
    }

    [Theory]
    [InlineData(1, 32)]
    [InlineData(4, 11)]
    public void BufferedMessagesShareOneDatagramAtTheBacklogLimitWithoutAdvancingRc4OnRejection(int packetsLimit, int bytesLimit)
    {
        byte[] key = [1, 2, 3, 4];
        var cipher = new Rc4Cipher(key);
        var packets = new List<byte[]>();
        var outbound = new OutboundChannel(new(), bytes => packets.Add(bytes.ToArray()))
            { MaxPendingDatagrams = packetsLimit, MaxPendingBytes = bytesLimit };
        try
        {
            outbound.SendBuffered([42], cipher, 0);
            outbound.SendBuffered([43], cipher, 0);
            Assert.Empty(packets); // Same tick still coalesces.
            outbound.FlushBuffered(20);
            Assert.Single(packets);
            Assert.Equal(1, outbound.PendingCount);
            long position = cipher.Position;
            Assert.Throws<SoeProtocolException>(() => outbound.SendBuffered([44], cipher, 20));
            Assert.Equal(position, cipher.Position);
            outbound.Acknowledge(0, 40);
            outbound.Send([44], cipher, 40);
            Assert.Equal(2, packets.Count);
            Assert.Equal(new byte[] { 42, 43, 44 }, Decrypt(packets, key));
        }
        finally { outbound.Close(); }
    }

    private static byte[] Decrypt(List<byte[]> packets, byte[] key)
    {
        var cipher = new Rc4Cipher(key);
        var result = new List<byte>();
        foreach (byte[] packet in packets)
        {
            Span<byte> body = packet.AsSpan(4);
            if (body[0] == 0 && body[1] == 0x19)
            {
                int offset = 2;
                while (offset < body.Length)
                {
                    int count = SoeVarInt.Read(body, ref offset);
                    Add(body.Slice(offset, count));
                    offset += count;
                }
            }
            else Add(body);
        }
        return result.ToArray();

        void Add(Span<byte> bytes)
        {
            if (bytes.Length > 1 && bytes[0] == 0 && bytes[1] == 0) bytes = bytes[1..];
            cipher.Transform(bytes);
            result.AddRange(bytes.ToArray());
        }
    }
}
