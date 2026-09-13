using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

/// <summary>
/// <b>Every byte of the boost path</b> (docs/117 §3, AUDIT-vehicles §D-V3/§D-V4).
///
/// <para>
/// Three of these writers are exact-length gated on the client side, so each <c>Length</c> is
/// load-bearing: one byte either way and the client drops the packet in silence.
/// <c>0f 33 Character.Turbo</c>'s parser <c>FUN_140a65200</c> reads it strictly with
/// <c>param_4 = 0</c>, which is what makes 11 the only acceptable size.
/// </para>
/// <para>
/// The id table is the client's own <c>ClientEffects.txt</c>, cross-checked against the August
/// effect catalogue this project generates from the client's own
/// <c>ActorCompositeEffectDefinitions.xml</c> — so the four turbo composite tags are asserted
/// against <see cref="AugustEffectCatalog"/> rather than against a literal, and a regenerated
/// catalogue that moved one would fail here rather than in a play-test.
/// </para>
/// </summary>
public sealed class VehicleBoostPacketTests
{
    private static byte[] Bytes(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter();
        write(writer);
        return writer.Written.ToArray();
    }

    // ------------------------------------------------------------------------ the id table

    /// <summary>
    /// <c>ClientEffects.txt</c>: Turbo 90000/1111141/100023/5016, 90068/1111292/110268/319,
    /// 90069/1111294/110270/279, 90193/1111611/120654/354. Four id spaces, one key press.
    /// </summary>
    [Theory]
    [InlineData(1u, 90_000u, 100_023u, 5_016u, 1_111_141u)]
    [InlineData(2u, 90_068u, 110_268u, 319u, 1_111_292u)]
    [InlineData(3u, 90_069u, 110_270u, 279u, 1_111_294u)]
    [InlineData(5u, 90_193u, 120_654u, 354u, 1_111_611u)]
    public void TheTurboIdTableIsTheClientsOwn(
        uint vehicleId, uint clientEffect, uint serverEffect, uint composite, uint ability)
    {
        Assert.Equal(clientEffect, AugustVehicleBoostFacts.TurboClientEffect(vehicleId));
        Assert.Equal(serverEffect, AugustVehicleBoostFacts.TurboServerEffect(vehicleId));
        Assert.Equal(composite, AugustVehicleBoostFacts.TurboCompositeEffect(vehicleId));
        Assert.Equal(ability, AugustVehicleBoostFacts.TurboAbility(vehicleId));
        Assert.True(AugustVehicleBoostFacts.IsTurboClientEffect(clientEffect));
        Assert.True(AugustVehicleBoostFacts.IsTurboAbility(ability));
    }

    /// <summary>
    /// The four composite tags are real rows of the August client's own effect catalogue, and they
    /// are the <c>VEH_Engine_Boost_*</c> ones. This is the cross-check that turns the id table from
    /// a port into a client fact.
    /// </summary>
    [Theory]
    [InlineData(1u, "VEH_Engine_Boost_OffRoader")]
    [InlineData(2u, "VEH_Engine_Boost_PickupTruck")]
    [InlineData(3u, "VEH_Engine_Boost_PoliceCar")]
    [InlineData(5u, "VEH_Engine_Boost_ATV")]
    public void EveryBoostTagIsInTheAugustEffectCatalogue(uint vehicleId, string name)
    {
        uint tag = AugustVehicleBoostFacts.TurboCompositeEffect(vehicleId);
        AugustEffectDefinition? definition = AugustEffectCatalog.ById(tag);

        Assert.NotNull(definition);
        Assert.Equal(name, definition!.Value.Name);
    }

    /// <summary>
    /// A vehicle id with no turbo row answers 0 everywhere rather than falling back to the
    /// OffRoader's. The parachute is the case that matters: it has no boost and must not borrow one.
    /// </summary>
    [Fact]
    public void TheParachuteHasNoTurbo()
    {
        Assert.Equal(0u, AugustVehicleBoostFacts.TurboClientEffect(13));
        Assert.Equal(0u, AugustVehicleBoostFacts.TurboCompositeEffect(13));
        Assert.Equal(0u, AugustVehicleBoostFacts.TurboAbility(13));
    }

    // ---------------------------------------------------------------------- 0f 33 and 88 2b

    /// <summary>
    /// <c>0f 33 Character.Turbo</c>: <c>u8 0x0f; u8 0x33; u64 guid; u8 value</c>, and the parser is
    /// strict, so 11 is the only length the client accepts.
    /// </summary>
    [Fact]
    public void CharacterTurboIsElevenBytes()
    {
        byte[] bytes = Bytes(new CharacterTurbo(0x1001, 0).WriteTo);

        Assert.Equal(CharacterTurbo.Length, bytes.Length);
        Assert.Equal(11, CharacterTurbo.Length);
        Assert.Equal(Convert.FromHexString("0F33" + "0110000000000000" + "00"), bytes);
        Assert.Equal(ZoneOpcodes.CharacterBase, CharacterTurbo.Opcode);
    }

    /// <summary>
    /// The polarity is inferred (the handler sets a bit when the byte is 0 and clears it otherwise,
    /// on a field pre-seeded to 2), so the value is a switch and the two states are complements.
    /// </summary>
    [Fact]
    public void TheTurboByteAndItsComplementAreBothCarried()
    {
        var shipped = new VehicleBoostOptions();
        Assert.Equal(0, shipped.TurboOnValue);
        Assert.Equal(1, shipped.TurboOffValue);

        var flipped = new VehicleBoostOptions { TurboOnValue = 1 };
        Assert.Equal(1, flipped.TurboOnValue);
        Assert.Equal(0, flipped.TurboOffValue);

        Assert.Equal(
            Convert.FromHexString("0F33" + "0110000000000000" + "01"),
            Bytes(new CharacterTurbo(0x1001, flipped.TurboOnValue).WriteTo));
    }

    /// <summary>
    /// <c>88 2b Vehicle.ActivateBoostFailed</c>: 10 bytes, and the server's ONLY refusal channel —
    /// the Abilities receive switch <c>FUN_140cc44b0</c> has cases 0x12-0x2b only, so a refusal sent
    /// on <c>a0 11 VehicleActivateAbilityFailed</c> is silently dropped.
    /// </summary>
    [Fact]
    public void ActivateBoostFailedIsTenBytes()
    {
        byte[] bytes = Bytes(new VehicleActivateBoostFailed(0xD000_0000_0000_0002).WriteTo);

        Assert.Equal(VehicleActivateBoostFailed.Length, bytes.Length);
        Assert.Equal(10, VehicleActivateBoostFailed.Length);
        Assert.Equal(Convert.FromHexString("882B" + "02000000000000D0"), bytes);
    }

    // -------------------------------------------------------------------- the composite tags

    [Fact]
    public void AddEffectTagCompositeEffectIsThirtyEightBytes()
    {
        byte[] bytes = Bytes(new AddEffectTagCompositeEffect(0xD000_0000_0000_0002, 5_016).WriteTo);

        Assert.Equal(AddEffectTagCompositeEffect.Length, bytes.Length);
        Assert.Equal(38, AddEffectTagCompositeEffect.Length);
        Assert.Equal(
            Convert.FromHexString(
                "0F15" + "02000000000000D0" + "98130000" + "98130000"
                + "0000000000000000" + "0000000000000000" + "98130000"),
            bytes);
    }

    [Fact]
    public void RemoveEffectTagCompositeEffectIsEighteenBytes()
    {
        byte[] bytes = Bytes(new RemoveEffectTagCompositeEffect(0xD000_0000_0000_0002, 5_016).WriteTo);

        Assert.Equal(RemoveEffectTagCompositeEffect.Length, bytes.Length);
        Assert.Equal(18, RemoveEffectTagCompositeEffect.Length);
        Assert.Equal(
            Convert.FromHexString("0F16" + "02000000000000D0" + "98130000" + "00000000"),
            bytes);
    }

    // ------------------------------------------------------------------------ 9e 01 and 9e 03

    /// <summary>
    /// The Remove form the client sends: 2 + 12 + 8 + 8 + 8 + 16 = 54 bytes, with the PLAYER first
    /// and the CAR second.
    /// </summary>
    [Fact]
    public void TheRemoveRequestIsFiftyFourBytesAndNamesThePlayerThenTheCar()
    {
        byte[] wire = Convert.FromHexString(
            "9E03"
            + "01000000" + "905F0100" + "B7860100"
            + "0500000000000071"
            + "02000000000000D0"
            + "0000000000000000"
            + "00000000" + "00000000" + "00000000" + "00000000");

        Assert.Equal(EffectRequest.RemoveLength, wire.Length);
        Assert.Equal(54, EffectRequest.RemoveLength);

        Assert.True(EffectRequest.TryParse(wire, out EffectRequest? request));
        Assert.NotNull(request);
        Assert.True(request!.IsRemove);
        Assert.Equal(90_000u, request.Head.EffectId1);
        Assert.Equal(100_023u, request.Head.EffectId2);
        Assert.Equal(0x7100_0000_0000_0005ul, request.SourceCharacterId);
        Assert.Equal(0xD000_0000_0000_0002ul, request.TargetCharacterId);
    }

    /// <summary>
    /// <b>The echo, byte for byte.</b> <c>unknownDword1</c> is forced to 4 and the two character ids
    /// are SWAPPED: the c2s form names the player as the effect's owner and the car as its target,
    /// the s2c form names the car as the thing the effect comes off.
    /// </summary>
    [Fact]
    public void TheEchoSwapsTheTwoIdsAndForcesDwordOneToFour()
    {
        byte[] wire = Convert.FromHexString(
            "9E03"
            + "01000000" + "905F0100" + "B7860100"
            + "0500000000000071"
            + "02000000000000D0"
            + "0000000000000000"
            + "00000000" + "00000000" + "00000000" + "00000000");

        Assert.True(EffectRequest.TryParse(wire, out EffectRequest? request));
        byte[] echo = request!.RemoveEcho();

        Assert.Equal(EffectRequest.RemoveLength, echo.Length);
        Assert.Equal(
            Convert.FromHexString(
                "9E03"
                + "04000000" + "905F0100" + "B7860100"
                + "02000000000000D0"
                + "0500000000000071"
                + "0000000000000000"
                + "00000000" + "00000000" + "00000000" + "00000000"),
            echo);
    }

    /// <summary>The Add form is shorter and still parses — only the head and the two ids are read.</summary>
    [Fact]
    public void TheAddRequestParsesFromTheMinimumForm()
    {
        byte[] wire = Convert.FromHexString(
            "9E01"
            + "01000000" + "905F0100" + "B7860100"
            + "0500000000000071"
            + "02000000000000D0");

        Assert.Equal(EffectRequest.MinimumLength, wire.Length);
        Assert.Equal(30, EffectRequest.MinimumLength);

        Assert.True(EffectRequest.TryParse(wire, out EffectRequest? request));
        Assert.True(request!.IsAdd);
        Assert.False(request.IsRemove);
        Assert.Equal(90_000u, request.Head.EffectId1);
    }

    [Fact]
    public void AShortOrForeignEffectPacketIsRefused()
    {
        Assert.False(EffectRequest.TryParse(Convert.FromHexString("9E0101000000"), out _));

        byte[] wrongBase = Convert.FromHexString(
            "9F01" + "01000000" + "905F0100" + "B7860100"
            + "0500000000000071" + "02000000000000D0");
        Assert.False(EffectRequest.TryParse(wrongBase, out _));
    }

    /// <summary>
    /// <c>0x9e</c> is <c>cPacketIdEffectsBase</c> at 1148 and <c>0x9f</c> is
    /// <c>cPacketIdRewardBuffsBase</c> — the single most load-bearing correction in the boost path,
    /// because the owner's 1087 map says the opposite.
    /// </summary>
    [Fact]
    public void TheEffectsBaseIsNineEnotNineF()
    {
        Assert.Equal(0x9e, EffectRequest.Opcode);
        Assert.Equal(ZoneOpcodes.EffectsBase, EffectRequest.Opcode);
        Assert.Equal(0x9e, ZoneOpcodes.EffectsBase);
        Assert.Equal(0xa0, ZoneOpcodes.AbilitiesBase);
    }

    // --------------------------------------------------------------------------- the state

    [Fact]
    public void ASecondPressOnALiveBoostIsNotASecondBoost()
    {
        var state = new VehicleBoostState();

        Assert.True(state.Press(1));
        Assert.False(state.Press(1));
        Assert.Equal(1, state.Presses);
        Assert.True(state.IsBoosting(1));

        Assert.True(state.Release(1));
        Assert.False(state.Release(1));
        Assert.Equal(1, state.Releases);
        Assert.False(state.IsBoosting(1));
    }

    [Fact]
    public void MotorRunEffectsAreRecognisedAndAreNotTurbo()
    {
        Assert.True(AugustVehicleBoostFacts.IsMotorRunClientEffect(90_001));
        Assert.True(AugustVehicleBoostFacts.IsMotorRunClientEffect(100_042));
        Assert.False(AugustVehicleBoostFacts.IsTurboClientEffect(90_001));
    }
}
