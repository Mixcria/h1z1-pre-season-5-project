using Cranberry.Protocol;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Gas;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Combat;

// Frozen vectors of the two Character packets that kill a player client-side (docs/21 §2h, §2i),
// and of the wire cause codes the death screen switches on (docs/18 §3b). Every byte expectation
// comes from the August reader cited in the writer's own doc comment; both readers are
// exact-length gated, so a length that drifts is a packet the client drops in silence.
public sealed class DeathPacketTests
{
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    [Fact]
    public void StartMultiStateDeathIsTwentyOneBytesWithTheRagdollOwner()
    {
        // 0f 4f | u64 guid | u8 direction | i8 type | u8 flags | u64 owner (FUN_140c21c00:7-43).
        var packet = StartMultiStateDeath.Ragdoll(0x0011223344556677, 0x00AABBCCDDEEFF00);
        byte[] bytes = Bytes(packet.WriteTo);

        Assert.Equal(StartMultiStateDeath.LongLength, bytes.Length);
        Assert.Equal(StartMultiStateDeath.LongLength, packet.Length);
        Assert.Equal(
            Convert.FromHexString(
                "0F4F"
                + "7766554433221100"
                + "00"
                + "00"
                + "80"
                + "00FFEEDDCCBBAA00"),
            bytes);
    }

    [Fact]
    public void StartMultiStateDeathDropsTheOwnerGuidWhenBitEightyIsClear()
    {
        // The reader takes the trailing u64 only under `if (0x7f < flags)` (FUN_140c21c00:34), so
        // a cleared bit is a 13-byte packet — NOT a 21-byte one with a zero guid.
        var packet = StartMultiStateDeath.Simple(0x0011223344556677) with { RagdollOwner = 0xDEAD };
        byte[] bytes = Bytes(packet.WriteTo);

        Assert.False(packet.CarriesRagdollOwner);
        Assert.Equal(StartMultiStateDeath.ShortLength, bytes.Length);
        Assert.Equal(Convert.FromHexString("0F4F" + "7766554433221100" + "00" + "00" + "00"), bytes);
    }

    [Theory]
    [InlineData(0x00, StartMultiStateDeath.ShortLength)]
    [InlineData(0x01, StartMultiStateDeath.ShortLength)]
    [InlineData(0x7f, StartMultiStateDeath.ShortLength)]
    [InlineData(0x80, StartMultiStateDeath.LongLength)]
    [InlineData(0xff, StartMultiStateDeath.LongLength)]
    public void StartMultiStateDeathLengthFollowsBitEightyAndNotZero(byte flags, int expected)
    {
        // The owner's 1087 writer gates on `flag > 0` (Z1 ZoneCombatWire.cs:495); 1148 gates on
        // `> 0x7f`. Both agree only at 128, which is the value he actually sends — anything in
        // 1..0x7f is where the two builds disagree, so it is pinned here.
        var packet = new StartMultiStateDeath(1, Flags: flags, RagdollOwner: 2);
        Assert.Equal(expected, packet.Length);
        Assert.Equal(expected, Bytes(packet.WriteTo).Length);
    }

    [Fact]
    public void StartMultiStateDeathTypeIsSignedOnTheWire()
    {
        // FUN_140c21c00:22 sign-extends the byte into an int, so -1 is a real value and must go
        // out as 0xff rather than being clamped away.
        byte[] bytes = Bytes(new StartMultiStateDeath(1, DeathDirection: 3, DeathType: -1, Flags: 0).WriteTo);
        Assert.Equal(0x03, bytes[10]);
        Assert.Equal(0xff, bytes[11]);
    }

    [Fact]
    public void RagdollForNamesTheViewerAsTheOwner()
    {
        // Each client simulates its own ragdoll: the victim's copy names the victim, every
        // viewer's copy names that viewer (Z1 ZoneCombat.cs:2402-2427).
        const ulong Victim = 0x1111;
        const ulong Viewer = 0x2222;
        Assert.Equal(Victim, StartMultiStateDeath.RagdollFor(Victim, Victim).RagdollOwner);
        Assert.Equal(Viewer, StartMultiStateDeath.RagdollFor(Victim, Viewer).RagdollOwner);
        Assert.Equal(Victim, StartMultiStateDeath.RagdollFor(Victim, Viewer).CharacterGuid);
    }

    [Fact]
    public void KilledByIsNineteenBytesKillerFirst()
    {
        // 0f 48 | u64 KILLER (the envelope guid) | u64 victim | u8 cheater (FUN_140c26680:18-73).
        var packet = new KilledBy(0x0011223344556677, 0x8899AABBCCDDEEFF);
        byte[] bytes = Bytes(packet.WriteTo);

        Assert.Equal(KilledBy.Length, bytes.Length);
        Assert.Equal(
            Convert.FromHexString("0F48" + "7766554433221100" + "FFEEDDCCBBAA9988" + "00"),
            bytes);
    }

    [Fact]
    public void KilledByCheaterFlagIsTheLastByte()
    {
        byte[] bytes = Bytes(new KilledBy(1, 2, IsInvulnerableCheater: true).WriteTo);
        Assert.Equal(KilledBy.Length, bytes.Length);
        Assert.Equal(0x01, bytes[^1]);
    }

    [Fact]
    public void EnvironmentalKilledByHasNoKiller()
    {
        // Gas, a fall, the match clock: killer 0 (Z1 ZoneCombat.cs:2450-2453).
        byte[] bytes = Bytes(KilledBy.Environmental(0x4242).WriteTo);
        Assert.Equal(0ul, BitConverter.ToUInt64(bytes, 2));
        Assert.Equal(0x4242ul, BitConverter.ToUInt64(bytes, 10));
    }

    [Fact]
    public void DeathOpcodesAreTheRegisteredOnes()
    {
        // registrations-1148.md:1149 (0x48 KilledBy) and :1155 (0x4f StartMultiStateDeath), both
        // under cPacketIdCharacterBase.
        Assert.Equal(0x0f, StartMultiStateDeath.Opcode);
        Assert.Equal(0x4f, StartMultiStateDeath.SubOpcode);
        Assert.Equal(0x0f, KilledBy.Opcode);
        Assert.Equal(0x48, KilledBy.SubOpcode);
    }

    [Theory]
    [InlineData(DeathCauseCode.Vehicle, 0x0du)]
    [InlineData(DeathCauseCode.Falling, 0x11u)]
    [InlineData(DeathCauseCode.Explosion, 0x23u)]
    [InlineData(DeathCauseCode.Fire, 0x3eu)]
    [InlineData(DeathCauseCode.Gas, 0x42u)]
    [InlineData(DeathCauseCode.BombingRun, 0x43u)]
    [InlineData(DeathCauseCode.EndOfMatchWinner, 0x48u)]
    [InlineData(DeathCauseCode.PlayerDisconnected, 0x49u)]
    [InlineData(DeathCauseCode.EndOfMatchRemaining, 0x4au)]
    [InlineData(DeathCauseCode.EndOfMatchExpired, 0x4bu)]
    [InlineData(DeathCauseCode.StartingAreaViolation, 0x4cu)]
    [InlineData(DeathCauseCode.GameModeKillCondition, 0x4du)]
    [InlineData(DeathCauseCode.IgnitionDetonation, 0x52u)]
    public void DeathCauseCodesAreTheClientsOwnSwitchValues(DeathCauseCode code, uint expected) =>
        Assert.Equal(expected, (uint)code);

    [Fact]
    public void DeathCauseCodeAgreesWithTheGasLanesConstants()
    {
        // The gas lane reached these numbers first as loose uints. Two spellings of one fact only
        // stay one fact if something asserts it.
        Assert.Equal(GasPackets.DeathCause.Gas, (uint)DeathCauseCode.Gas);
        Assert.Equal(GasPackets.DeathCause.Falling, (uint)DeathCauseCode.Falling);
        Assert.Equal(GasPackets.DeathCause.Vehicle, (uint)DeathCauseCode.Vehicle);
        Assert.Equal(GasPackets.DeathCause.Explosion, (uint)DeathCauseCode.Explosion);
        Assert.Equal(GasPackets.DeathCause.Fire, (uint)DeathCauseCode.Fire);
        Assert.Equal(GasPackets.DeathCause.BombingRun, (uint)DeathCauseCode.BombingRun);
        Assert.Equal(GasPackets.DeathCause.EndOfMatchWinner, (uint)DeathCauseCode.EndOfMatchWinner);
        Assert.Equal(GasPackets.DeathCause.PlayerDisconnected, (uint)DeathCauseCode.PlayerDisconnected);
        Assert.Equal(GasPackets.DeathCause.EndOfMatchRemaining, (uint)DeathCauseCode.EndOfMatchRemaining);
        Assert.Equal(GasPackets.DeathCause.EndOfMatchExpired, (uint)DeathCauseCode.EndOfMatchExpired);
        Assert.Equal(GasPackets.DeathCause.StartingAreaViolation, (uint)DeathCauseCode.StartingAreaViolation);
        Assert.Equal(GasPackets.DeathCause.GameModeKillCondition, (uint)DeathCauseCode.GameModeKillCondition);
        Assert.Equal(GasPackets.DeathCause.IgnitionDetonation, (uint)DeathCauseCode.IgnitionDetonation);
    }

    [Fact]
    public void DeathCauseCodeIsNotTheResultsFileEnum()
    {
        // docs/15 §5 vs docs/18 §3b: two different value spaces that disagree on every shared
        // concept. Asserting the disagreement is what stops the older table being re-adopted.
        Assert.NotEqual(GasPackets.ResultsFileCause.ToxicGas, (uint)DeathCauseCode.Gas);
        Assert.NotEqual(GasPackets.ResultsFileCause.Falling, (uint)DeathCauseCode.Falling);
        Assert.NotEqual(GasPackets.ResultsFileCause.Vehicle, (uint)DeathCauseCode.Vehicle);
        Assert.NotEqual(GasPackets.ResultsFileCause.Explosion, (uint)DeathCauseCode.Explosion);
        Assert.NotEqual(GasPackets.ResultsFileCause.Fire, (uint)DeathCauseCode.Fire);
    }

    [Theory]
    [InlineData(DamageCause.ToxicGas, DeathCauseCode.Gas)]
    [InlineData(DamageCause.Bullet, DeathCauseCode.GameModeKillCondition)]
    [InlineData(DamageCause.Melee, DeathCauseCode.GameModeKillCondition)]
    [InlineData(DamageCause.Falling, DeathCauseCode.Falling)]
    [InlineData(DamageCause.Vehicle, DeathCauseCode.Vehicle)]
    [InlineData(DamageCause.Explosion, DeathCauseCode.Explosion)]
    [InlineData(DamageCause.BombingRun, DeathCauseCode.BombingRun)]
    [InlineData(DamageCause.Fire, DeathCauseCode.Fire)]
    [InlineData(DamageCause.Disconnected, DeathCauseCode.PlayerDisconnected)]
    [InlineData(DamageCause.Unknown, DeathCauseCode.Environment)]
    public void ServerCausesMapOntoWireCodes(DamageCause cause, DeathCauseCode expected) =>
        Assert.Equal(expected, DeathCauseCodes.For(cause));

    [Fact]
    public void EnvironmentIsTheSwitchesOwnDefaultAndNotAValueItNames()
    {
        // FUN_140bbb120:205-208. 0 is deliberately outside the enumerated codes: it is what the
        // handler falls through to, so sending it is not a guess.
        Assert.Equal(0u, (uint)DeathCauseCode.Environment);
        Assert.Equal("UI.Results.Rank.Environment", DeathCauseCodes.RankKey(DeathCauseCode.Environment));
        Assert.Equal("UI.Results.Rank.Environment", DeathCauseCodes.RankKey(DeathCauseCode.Vehicle));
        Assert.Equal("UI.Results.Rank.Environment", DeathCauseCodes.RankKey(DeathCauseCode.Explosion));
        Assert.Equal("UI.Results.Rank.Gas", DeathCauseCodes.RankKey(DeathCauseCode.Gas));
    }

    [Fact]
    public void OnlyVehicleAndExplosionSubstituteASourceName()
    {
        // FUN_140bbb120:102-152 — the only two literals in that block.
        Assert.Equal("vehicle", DeathCauseCodes.SourceName(DeathCauseCode.Vehicle));
        Assert.Equal("explosion", DeathCauseCodes.SourceName(DeathCauseCode.Explosion));
        Assert.Null(DeathCauseCodes.SourceName(DeathCauseCode.Gas));
        Assert.Null(DeathCauseCodes.SourceName(DeathCauseCode.GameModeKillCondition));
    }
}
