using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Gas;

namespace Cranberry.Tests.Zone.Gas;

// Frozen vectors of the gas wire packets. Byte expectations come from the August parsers cited in
// docs/11, docs/15 and docs/16; the unverified 11 1e layout is frozen so a live experiment can see
// exactly what changed.
public sealed class GasPacketTests
{
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    [Fact]
    public void RingIsThirtyOneBytesInTheParserOrder()
    {
        // ce 01 00 | f32x4 centre; f32 radius; u32; u32 (FUN_140bbba50 + FUN_140bafe10).
        byte[] bytes = Bytes(w => GasPackets.WriteRing(w, new Vector4(0, 0, 0, 1), 100f));
        Assert.Equal(GasPackets.RingLength, bytes.Length);
        Assert.Equal(
            Convert.FromHexString(
                "CE0100"
                + "00000000" + "00000000" + "00000000" + "0000803F"
                + "0000C842"
                + "E8030000"
                + "0000803F"),
            bytes);
    }

    [Fact]
    public void RingCarriesTheCentreAndRadiusItIsGiven()
    {
        byte[] bytes = Bytes(w => GasPackets.WriteRing(w, new Vector4(1f, 2f, 3f, 1f), 4f, 5, 6));
        Assert.Equal(
            Convert.FromHexString(
                "CE0100"
                + "0000803F" + "00000040" + "00004040" + "0000803F"
                + "00008040"
                + "05000000"
                + "06000000"),
            bytes);
    }

    [Fact]
    public void RingCarriesTheBlendTimeAndTheInertTrailingField()
    {
        // docs/18 §1 (G-07 closed): +0x54 is a blend time in ms for the client's own exponential
        // smoother FUN_140bbed00, and +0x58 is stored and never read. Neither is a deadline, so
        // the phase's "closes in" value does not appear on this packet at all.
        var settings = new GasSettings();
        var circle = new GasCircle(Vector3.Zero, 100f);
        byte[] bytes = Bytes(w => GasPackets.WriteRing(w, settings, circle));
        Assert.Equal(GasPackets.RingLength, bytes.Length);
        Assert.Equal(GasPackets.RingBlendMsDefault, BitConverter.ToUInt32(bytes, 23));
        Assert.Equal(GasPackets.RingUnusedFieldDefault, BitConverter.ToUInt32(bytes, 27));
        Assert.Equal(1000u, GasPackets.RingBlendMsDefault);
        Assert.Equal(1f, BitConverter.UInt32BitsToSingle(GasPackets.RingUnusedFieldDefault));
    }

    [Fact]
    public void RingBlendTimeIsTunableAndNeverZero()
    {
        // The smoother divides by this field with no zero guard, so 0 is refused outright and the
        // writer additionally floors it at 1.
        var settings = new GasSettings { RingBlendMs = 250 };
        byte[] bytes = Bytes(w => GasPackets.WriteRing(w, settings, new GasCircle(Vector3.Zero, 100f)));
        Assert.Equal(250u, BitConverter.ToUInt32(bytes, 23));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GasSettings { RingBlendMs = 0 }.Validate());
    }

    [Fact]
    public void SafeZoneIsTheExistingMatchFlowWriter()
    {
        // WAVE 9 RE-BASELINE (docs/87 §7 edit 7). ce 02 now carries the owner's own vector,
        // [x, 0, z, 0], rather than [x, y, z, 1] — so the writer it equals is GameModeHud's fed
        // GasCircle.SafeZoneVector4. ce 01 is unchanged and still carries w = 1.
        var circle = new GasCircle(new Vector3(-233.83f, 506.36f, -4892.03f), 1810f);
        byte[] fromGas = Bytes(w => GasPackets.WriteSafeZone(w, circle));
        byte[] fromHud = Bytes(w => GameModeHud.WriteSafeZone(w, circle.SafeZoneVector4, circle.Radius));

        Assert.Equal(GasPackets.SafeZoneLength, fromGas.Length);
        Assert.Equal(fromHud, fromGas);
        Assert.Equal(Convert.FromHexString("CE0200"), fromGas[..3]);

        // The two vectors differ in exactly the two fields the wave-9 decision moved.
        Assert.Equal(new Vector4(-233.83f, 506.36f, -4892.03f, 1f), circle.CentreVector4);
        Assert.Equal(new Vector4(-233.83f, 0f, -4892.03f, 0f), circle.SafeZoneVector4);
    }

    [Fact]
    public void SafeZoneCentreCarriesTheOwnersVector()
    {
        // WAVE 9 (docs/87 §7 edit 7): [x, 0, z, 0]. Z1 ZoneMatch.cs:2450 writes exactly this for
        // cf 02; Cranberry wrote w = 1 until now. Nothing has ever observed either value — ce 02's
        // only consumers are two Flash data providers — so this is alignment with the build the
        // owner click-tested, not a measured correction. ce 01 keeps w = 1 (RingCarriesWOne).
        byte[] bytes = Bytes(w => GasPackets.WriteSafeZone(w, new GasCircle(Vector3.Zero, 15f)));
        Assert.Equal(
            Convert.FromHexString("CE0200" + "00000000" + "00000000" + "00000000" + "00000000" + "00007041"),
            bytes);
    }

    [Fact]
    public void DeathInfoReportsGasAsTheWireCause()
    {
        // ce 04 00 | u32 rankIndex; u8 flag; i32 len + killerName; u32 field4; u32 sourceId;
        // u32 cause (FUN_140bb0cc0). docs/18 §3b: the wire cause for gas is 0x42, not docs/15's 9.
        byte[] bytes = Bytes(w => GasPackets.DeathInfo.Gas(rankIndex: 12).WriteTo(w));
        Assert.Equal(24, bytes.Length);
        Assert.Equal(
            Convert.FromHexString(
                "CE0400"
                + "0C000000"
                + "00"
                + "00000000"
                + "00000000"
                + "00000000"
                + "42000000"),
            bytes);
    }

    [Fact]
    public void DeathInfoCarriesAKillerNameWhenThereIsOne()
    {
        byte[] bytes = Bytes(w => new GasPackets.DeathInfo(
            Rank: 3,
            Draw: true,
            Killer: "AB",
            Field4: 0,
            SourceId: 7,
            Cause: GasPackets.DeathCause.Explosion).WriteTo(w));

        // 24 + nameLen, never docs/18 §3's "27 + len": FUN_140bbb120:70 is exact-length gated.
        Assert.Equal(26, bytes.Length);
        Assert.Equal(
            bytes.Length,
            new GasPackets.DeathInfo(Killer: "AB").Length);
        Assert.Equal(Convert.FromHexString("CE0400" + "03000000" + "01" + "02000000" + "4142"), bytes[..14]);
        Assert.Equal(7u, BitConverter.ToUInt32(bytes, 18));
        Assert.Equal(GasPackets.DeathCause.Explosion, BitConverter.ToUInt32(bytes, 22));
    }

    [Fact]
    public void TheDeathCauseEnumIsTheWireOneNotTheResultsFileOne()
    {
        // docs/18 §3b, recovered from the ce 04 handler's own localisation switch
        // FUN_140bbb120:200-248. Every shared concept disagrees with the results-file enum
        // (docs/15 §5, FUN_1413f0ad0), which is why they cannot be the same value space.
        Assert.Equal(0x0du, GasPackets.DeathCause.Vehicle);
        Assert.Equal(0x11u, GasPackets.DeathCause.Falling);
        Assert.Equal(0x23u, GasPackets.DeathCause.Explosion);
        Assert.Equal(0x3eu, GasPackets.DeathCause.Fire);
        Assert.Equal(0x42u, GasPackets.DeathCause.Gas);
        Assert.Equal(0x43u, GasPackets.DeathCause.BombingRun);

        Assert.Equal(1u, GasPackets.ResultsFileCause.Disconnected);
        Assert.Equal(6u, GasPackets.ResultsFileCause.Vehicle);
        Assert.Equal(7u, GasPackets.ResultsFileCause.Explosion);
        Assert.Equal(8u, GasPackets.ResultsFileCause.Fire);
        Assert.Equal(9u, GasPackets.ResultsFileCause.ToxicGas);
        Assert.Equal(10u, GasPackets.ResultsFileCause.Falling);
        Assert.Equal(13u, GasPackets.ResultsFileCause.Spectate);
    }

    [Fact]
    public void HitpointsIsElevenBytesOfCurrentAndMaximum()
    {
        // 11 01 00 | u32 current; u32 max (FUN_140a357d0; the handler's (cur*100)/max anchors both).
        byte[] bytes = Bytes(w => new GasPackets.Hitpoints(7_500, 10_000).WriteTo(w));
        Assert.Equal(GasPackets.Hitpoints.Length, bytes.Length);
        Assert.Equal(Convert.FromHexString("110100" + "4C1D0000" + "10270000"), bytes);
    }

    [Fact]
    public void HitpointsCanReportADeadPlayer()
    {
        byte[] bytes = Bytes(w => new GasPackets.Hitpoints(0, new GasSettings().MaxHitpoints).WriteTo(w));
        Assert.Equal(Convert.FromHexString("110100" + "00000000" + "10270000"), bytes);
    }

    [Fact]
    public void DamageInfoDefaultsToTheSelfFramedReading()
    {
        // Unverified (docs/15 §4, docs/16 §4e): field order and width are proven, the semantics are
        // not. The default reading treats the reader's leading u8/u16 as this packet's own header,
        // as ce 02's self-framing reader and Hitpoints' 11-byte total both do.
        byte[] bytes = Bytes(w => GasPackets.DamageInfo.GasTick(90).WriteTo(w));
        Assert.Equal(33, bytes.Length);
        Assert.Equal(
            Convert.FromHexString(
                "111E00"
                + "00000000"
                + "00"
                + "5A000000"
                + "00000000" + "00000000" + "00000000" + "00000000" + "00000000"
                + "00"),
            bytes);
    }

    [Fact]
    public void DamageInfoCanRepeatThePrefixForTheOtherReading()
    {
        var settings = new GasSettings { DamageInfoRepeatsPrefix = true };
        byte[] bytes = Bytes(w => GasPackets.DamageInfo.GasTick(90).WriteTo(w, settings));
        Assert.Equal(36, bytes.Length);
        Assert.Equal(Convert.FromHexString("111E00" + "111E00"), bytes[..6]);
        Assert.Equal(90u, BitConverter.ToUInt32(bytes, 11));
    }

    [Fact]
    public void DamageInfoWritesEveryFieldItIsGiven()
    {
        byte[] bytes = Bytes(w => new GasPackets.DamageInfo(
            Field3: 1,
            Field4: 2,
            Field5: 3,
            Field6: 4,
            Field7: 5,
            Field8: 6,
            Field9: 7,
            Field10: 8,
            Flags: 0x0F).WriteTo(w));

        Assert.Equal(33, bytes.Length);
        Assert.Equal(1u, BitConverter.ToUInt32(bytes, 3));
        Assert.Equal(0x08, bytes[7]);                 // client varint 2 = (2 << 2) | 0
        Assert.Equal(3u, BitConverter.ToUInt32(bytes, 8));
        Assert.Equal(8u, BitConverter.ToUInt32(bytes, 28));
        Assert.Equal(0x0F, bytes[^1]);
    }

    [Fact]
    public void DamageInfoIsOffByDefaultBecauseItsFieldsAreUnproven()
    {
        var settings = new GasSettings();
        Assert.False(settings.SendDamageInfo);
        Assert.False(settings.DamageInfoRepeatsPrefix);
        Assert.Equal(GasPackets.RingBlendMsDefault, settings.RingBlendMs);
    }

    [Fact]
    public void ZoneOptionsCarriesTheGasSettings()
    {
        var options = new ZoneOptions();
        Assert.Equal(10, options.Gas.PhaseCount);
        Assert.Equal(options.SafeZoneRevealMs, options.Gas.FirstRevealDelayMs);
        Assert.Equal(10_000u, options.Gas.MaxHitpoints);
    }

    [Fact]
    public void ARevealSendsTheRingAndTheSafeZoneForTheSameCircle()
    {
        // The pair the integrator sends on GasTickEvents.RevealSafeZone (docs/23).
        var settings = new GasSettings();
        GasSchedule schedule = GasSchedule.Create(settings, 1);
        GasCircle circle = schedule.Phase(1).Target;

        byte[] ring = Bytes(w => GasPackets.WriteRing(w, settings, circle));
        byte[] safeZone = Bytes(w => GasPackets.WriteSafeZone(w, circle));

        Assert.Equal(GasPackets.RingLength, ring.Length);
        Assert.Equal(GasPackets.SafeZoneLength, safeZone.Length);
        // Wave 9: the two writers no longer produce identical bytes — ce 01 carries [x, y, z, 1]
        // and ce 02 the owner's [x, 0, z, 0] — so compare the fields that are still shared: X, Z
        // and the radius (docs/87 §7 edit 7).
        Assert.Equal(ring[3..7], safeZone[3..7]);      // f32 X
        Assert.Equal(ring[11..15], safeZone[11..15]);  // f32 Z
        Assert.Equal(ring[19..23], safeZone[19..23]);  // f32 radius
        Assert.Equal(1f, BitConverter.ToSingle(ring, 15));
        Assert.Equal(0f, BitConverter.ToSingle(safeZone, 15));
    }
}
