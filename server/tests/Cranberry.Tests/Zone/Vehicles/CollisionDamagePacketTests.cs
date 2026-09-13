using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

/// <summary>
/// <b>The 44-byte <c>8e 01 Collision.Damage</c> body, pinned against the client's own bytes.</b>
///
/// <para>
/// docs/97 D155 called this body UNDERIVED and <c>HandleCollisionReport</c>'s own comment said no
/// 1148 reader had been recovered. Both were right about the decompiler and wrong about the wire:
/// the client's zone receive dispatcher <c>FUN_140af3950</c> has 164 cases and <b>no
/// <c>case 0x8e</c></b>, and <c>cCollisionPacketIdDamage</c> is referenced by exactly one function
/// in the whole image - the name registrar <c>FUN_1413d1490</c>, which attaches no reader and no
/// writer factory. The packet is send-only, so there was never anything on the receive side to
/// decompile. Only the wire could answer.
/// </para>
/// <para>
/// <b>These are the 47 records it answered with</b>, recovered verbatim from Cranberry's own host
/// logs (<c>C:\Aug2017\logs\host-*.log</c>, decoded to
/// <c>C:\Aug2017\out\audit\8e-samples.txt</c>). Every one is exactly 44 bytes and every one
/// parses cleanly. The round-trip assertion is what makes this a derivation rather than a reading:
/// a layout that dropped or reordered a field would still "parse", but it could not rebuild the
/// client's own bytes.
/// </para>
/// <para>D29: this proves what the client sent, never what it meant by it.</para>
/// </summary>
public sealed class CollisionDamagePacketTests
{
    /// <summary>The 47 live records, in the order they were recovered.</summary>
    private static readonly string[] LiveSamples =
    [
        "8E010001100000000000000110000000000000000000000601000002000000DD0001C2EA32FD43AD128C4300",
        "8E010001100000000000000110000000000000000000000700000002000000DD0001C2E932FD43AD128C4300",
        "8E010001100000000000000110000000000000000000000E00000002000000DD0001C2E932FD43AD128C4300",
        "8E010001100000000000000110000000000000000000001001000002000000DD0001C2EA32FD43AD128C4300",
        "8E010001100000000000000110000000000000000000001600000002000000DD0001C2E932FD43AD128C4300",
        "8E010001100000000000000110000000000000000000001B01000002000000DD0001C2EA32FD43AD128C4300",
        "8E010001100000000000000110000000000000000000001E00000002000000DD0001C2E932FD43AD128C4300",
        "8E010001100000000000000110000000000000000000002501000002000000DD0001C2EA32FD43AD128C4300",
        "8E010001100000000000000110000000000000000000002600000002000000DD0001C2E932FD43AD128C4300",
        "8E010001100000000000000110000000000000000000002E00000002000000DD0001C2E932FD43AD128C4300",
        "8E010001100000000000000110000000000000000000002F01000002000000DD0001C2EA32FD43AD128C4300",
        "8E010001100000000000000110000000000000000000003500000002000000DD0001C2E932FD43AD128C4300",
        "8E010001100000000000000110000000000000000000003A01000002000000DD0001C2EA32FD43AD128C4300",
        "8E010001100000000000000110000000000000000000003D00000002000000DD0001C2E932FD43AD128C4300",
        "8E010001100000000000000110000000000000000000004500000002000000DD0001C2E932FD43AD128C4300",
        "8E010001100000000000000110000000000000000000004D00000002000000DD0001C2E932FD43AD128C4300",
        "8E010001100000000000000110000000000000000000005500000002000000DD0001C2E932FD43AD128C4300",
        "8E010001100000000000000110000000000000000000005C00000002000000DD0001C2E932FD43AD128C4300",
        "8E010001100000000000000110000000000000000000006400000002000000DD0001C2E932FD43AD128C4300",
        "8E010001100000000000000110000000000000000000006C00000002000000DD0001C2E932FD43AD128C4300",
        "8E010001100000000000000110000000000000000000007400000002000000DD0001C2E932FD43AD128C4300",
        "8E010001100000000000000110000000000000000000007C00000002000000DD0001C2E932FD43AD128C4300",
        "8E010001100000000000000110000000000000000000008400000002000000DD0001C2E932FD43AD128C4300",
        "8E010001100000000000000110000000000000000000008B00000002000000DD0001C2E932FD43AD128C4300",
        "8E010001100000000000000110000000000000000000009300000002000000DD0001C2E932FD43AD128C4300",
        "8E010001100000000000000110000000000000000000009B00000002000000DD0001C2E932FD43AD128C4300",
        "8E01000110000000000000011000000000000000000000A300000002000000DD0001C2E932FD43AD128C4300",
        "8E01000110000000000000011000000000000000000000AB00000002000000DD0001C2E932FD43AD128C4300",
        "8E01000110000000000000011000000000000000000000B200000002000000DD0001C2E932FD43AD128C4300",
        "8E01000110000000000000011000000000000000000000BD00000002000000DD0001C2E932FD43AD128C4300",
        "8E01000110000000000000011000000000000000000000C700000002000000DD0001C2E932FD43AD128C4300",
        "8E01000110000000000000011000000000000000000000D200000002000000DD0001C2E932FD43AD128C4300",
        "8E01000110000000000000011000000000000000000000DC00000002000000DD0001C2E932FD43AD128C4300",
        "8E01000110000000000000011000000000000000000000E600000002000000DD0001C2EA32FD43AD128C4300",
        "8E01000110000000000000011000000000000000000000F100000002000000DD0001C2EA32FD43AD128C4300",
        "8E01000110000000000000011000000000000000000000FB00000002000000DD0001C2EA32FD43AD128C4300",
        "8E010004100000000000000410000000000000000000002371000002000000DE171C45018A803DC45F374500",
        "8E010004100000000000000410000000000000000000004000000002000000713DFCC10A379E4371DD8B4300",
        "8E010004100000000000000410000000000000000000004000000002000000713DFCC1EA37FD435CFD8B4300",
        "8E010004100000000000000410000000000000000000004000000002000000713DFCC1EB36FD435CDD8B4300",
        "8E010004100000000000000410000000000000000000004000000002000000713DFCC1EB37FD435CFD8B4300",
        "8E0100041000000000000004100000000000000000000042000000020000007BD469C30038FD433DE098C500",
        "8E010004100000000000000410000000000000000000008DA6000004000000605EADC4006030C27040104500",
        "8E010004100000000000000410000000000000000000008F6100000200000052FE1EC506371843C52F4AC500",
        "8E01000410000000000000041000000000000000000000AB0000000400000082FCF04457892242760906C500",
        "8E01000410000000000000041000000000000000000000C21000000400000000004842002008420000484200",
        "8E010005100000000000000510000000000000000000004000000002000000713DFCC1EA37FD435CFD8B4300",
    ];

    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    [Fact]
    public void EverySampleIsExactlyFortyFourBytes()
    {
        Assert.Equal(47, LiveSamples.Length);
        Assert.All(LiveSamples, hex => Assert.Equal(CollisionDamageReport.Length, hex.Length / 2));
        Assert.Equal(44, CollisionDamageReport.Length);
    }

    [Fact]
    public void EverySampleParsesAndRebuildsItselfByteForByte()
    {
        foreach (string hex in LiveSamples)
        {
            byte[] bytes = Convert.FromHexString(hex);
            Assert.True(CollisionDamageReport.TryParse(bytes, out CollisionDamageReport? report));
            Assert.NotNull(report);

            // The round trip is the whole proof: a wrong field ORDER could still parse.
            Assert.Equal(bytes, Bytes(report!.WriteTo));
        }
    }

    /// <summary>
    /// The worked sample from AUDIT-vehicles s D-V1, field by field. Character and object are the
    /// same guid because standing in gas is a self-report.
    /// </summary>
    [Fact]
    public void TheWorkedSampleDecodesFieldByField()
    {
        byte[] bytes = Convert.FromHexString(
            "8E01" + "00"
            + "0110000000000000" + "0110000000000000"
            + "00000000" + "06010000" + "02000000"
            + "DD0001C2" + "EA32FD43" + "AD128C43" + "00");

        CollisionDamageReport report = CollisionDamageReport.Parse(bytes);

        Assert.Equal(0x1001ul, report.CharacterId);
        Assert.Equal(0x1001ul, report.ObjectCharacterId);
        Assert.True(report.IsSelfReport);
        Assert.Equal(262u, report.Damage);
        Assert.Equal(CollisionDamageCause.ToxicGas, report.Cause);
        Assert.Equal(-32.25084f, report.Position.X, 4);
        Assert.Equal(506.39777f, report.Position.Y, 3);
        Assert.Equal(280.14590f, report.Position.Z, 3);
        Assert.Equal(0, report.UnknownByte1);
        Assert.Equal(0u, report.UnknownDword1);
        Assert.Equal(0, report.UnknownByte2);
    }

    /// <summary>
    /// Only two causes have ever been seen: 2 forty-four times (standing in gas) and 4 three times
    /// (a fall). 0, 1 and 3 are the owner's Z1 enum adopted under D53 and are inferred, which is why
    /// the server logs them with their hex instead of acting on them.
    /// </summary>
    [Fact]
    public void OnlyGasAndFallHaveEverBeenSeenOnThisWire()
    {
        Dictionary<CollisionDamageCause, int> causes = LiveSamples
            .Select(hex => CollisionDamageReport.Parse(Convert.FromHexString(hex)).Cause)
            .GroupBy(cause => cause)
            .ToDictionary(group => group.Key, group => group.Count());

        Assert.Equal(2, causes.Count);
        Assert.Equal(44, causes[CollisionDamageCause.ToxicGas]);
        Assert.Equal(3, causes[CollisionDamageCause.FallDamage]);
    }

    /// <summary>
    /// The damage word spans 7 to 42,637 - and the top of that range is a FALL, against a 10,000
    /// health bar. That single number is why the drop gate exists: decoding this packet without it
    /// would kill every player the moment they touched the ground.
    /// </summary>
    [Fact]
    public void TheDamageWordSpansSevenToFortyTwoThousandAndTheWorstIsAFall()
    {
        uint[] damages = [.. LiveSamples.Select(
            hex => CollisionDamageReport.Parse(Convert.FromHexString(hex)).Damage)];

        Assert.Equal(7u, damages.Min());
        Assert.Equal(42_637u, damages.Max());

        CollisionDamageReport worst = LiveSamples
            .Select(hex => CollisionDamageReport.Parse(Convert.FromHexString(hex)))
            .OrderByDescending(report => report.Damage)
            .First();
        Assert.Equal(CollisionDamageCause.FallDamage, worst.Cause);
    }

    /// <summary>
    /// The three fields that are zero in all 47 samples stay named rather than skipped, so the first
    /// record that carries something in one of them is visible instead of silently discarded.
    /// </summary>
    [Fact]
    public void TheThreeUnknownFieldsAreZeroInEverySample()
    {
        foreach (string hex in LiveSamples)
        {
            CollisionDamageReport report = CollisionDamageReport.Parse(Convert.FromHexString(hex));
            Assert.Equal(0, report.UnknownByte1);
            Assert.Equal(0u, report.UnknownDword1);
            Assert.Equal(0, report.UnknownByte2);
        }
    }

    [Fact]
    public void ADifferentLengthIsRefusedRatherThanGuessedAt()
    {
        byte[] sample = Convert.FromHexString(LiveSamples[0]);

        Assert.False(CollisionDamageReport.TryParse(sample.AsSpan(0, 43), out _));
        Assert.False(CollisionDamageReport.TryParse([.. sample, (byte)0], out _));
        Assert.Throws<PacketFormatException>(
            () => CollisionDamageReport.Parse([.. sample, (byte)0]));
    }

    [Fact]
    public void AnotherOpcodeOrSubIsRefused()
    {
        byte[] sample = Convert.FromHexString(LiveSamples[0]);

        byte[] wrongOpcode = [.. sample];
        wrongOpcode[0] = 0x8f;
        Assert.False(CollisionDamageReport.TryParse(wrongOpcode, out _));

        byte[] wrongSub = [.. sample];
        wrongSub[1] = 0x02;
        Assert.False(CollisionDamageReport.TryParse(wrongSub, out _));
    }

    [Fact]
    public void TheOpcodeIsTheZoneCollisionBase()
    {
        Assert.Equal(ZoneOpcodes.CollisionBase, CollisionDamageReport.Opcode);
        Assert.Equal(0x8e, CollisionDamageReport.Opcode);
        Assert.Equal(0x01, CollisionDamageReport.SubOpcode);
    }

    /// <summary>A report the client writes for a crash names the CAR in objectCharacterId.</summary>
    [Fact]
    public void ACrashReportIsNotASelfReport()
    {
        var crash = new CollisionDamageReport(
            CharacterId: 0x1001,
            ObjectCharacterId: 0xD000_0000_0000_0002,
            Damage: 12_345,
            Cause: CollisionDamageCause.VehicleCollision,
            Position: new Vector3(1f, 2f, 3f));

        byte[] bytes = Bytes(crash.WriteTo);

        Assert.Equal(CollisionDamageReport.Length, bytes.Length);
        Assert.False(crash.IsSelfReport);
        Assert.Equal(crash, CollisionDamageReport.Parse(bytes));
    }
}
