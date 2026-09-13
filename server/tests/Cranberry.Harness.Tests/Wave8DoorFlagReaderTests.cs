using Cranberry.Harness.Verification;

namespace Cranberry.Harness.Tests;

/// <summary>
/// The harness's reader for the spawn record's flag byte (docs/68 F2, docs/79 §4 E14 D7).
/// <para>
/// Built by hand from the derivation rather than round-tripped against <c>LightweightEntityBody</c>,
/// because this project deliberately does not reference the server: the harness has to be able to
/// contradict Cranberry, and a reader that borrowed the writer's arithmetic could not.
/// </para>
/// </summary>
public sealed class Wave8DoorFlagReaderTests
{
    private const byte AddLightweightNpc = 0xd6;

    /// <summary>
    /// docs/68 §1's hexdump: <c>+0x1b0 / +0x1b1 / +0x1b2</c> are record bytes 134/135/136 for a
    /// three-byte transient varint, i.e. 67 bytes back from the end of a 202-byte body — and 133 in
    /// the 200-byte one-byte-varint case the same document annotates.
    /// </summary>
    [Theory]
    [InlineData(200, 133)]
    [InlineData(201, 134)]
    [InlineData(202, 135)]
    public void TheFlagByteIsSixtySevenBytesBackFromTheEnd(int length, int expectedOffset)
    {
        byte[] payload = new byte[length];
        payload[0] = AddLightweightNpc;
        payload[expectedOffset] = 0x20;

        Assert.Equal(0x20, VerificationPackets.SpawnFlagsOf(payload));
    }

    /// <summary>A record with the wave-7 zeros reads back as zero — the "nothing is solid" state.</summary>
    [Fact]
    public void AWaveSevenRecordReadsBackAsZero()
    {
        byte[] payload = new byte[202];
        payload[0] = AddLightweightNpc;

        Assert.Equal(0x00, VerificationPackets.SpawnFlagsOf(payload));
    }

    /// <summary>
    /// Anything that is not a lightweight spawn reads 0 rather than a byte from the middle of some
    /// other packet, so a probe can sweep the whole ledger without pre-filtering.
    /// </summary>
    [Theory]
    [InlineData(0x0f)]
    [InlineData(0x09)]
    public void ANonSpawnPacketReadsZero(byte opcode)
    {
        byte[] payload = new byte[202];
        payload[0] = opcode;
        payload[135] = 0x20;

        Assert.Equal(0x00, VerificationPackets.SpawnFlagsOf(payload));
    }

    /// <summary>A truncated payload cannot be indexed from the end; it reads 0 instead of throwing.</summary>
    [Fact]
    public void AShortPayloadReadsZero()
    {
        Assert.Equal(0x00, VerificationPackets.SpawnFlagsOf([AddLightweightNpc, 0x01, 0x02]));
    }
}
