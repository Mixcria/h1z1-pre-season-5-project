using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Zone.Generated;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

public sealed class VehicleDriverControlsWireTests
{
    // Actual August client requests, wire-20260906-180440.txt, 18:08:22.411
    // (horn) and 18:09:09.323 (headlights), without the gateway's 06 prefix.
    private const string HornInit =
        "A001010000000000000005F510000D000000211000000000000001000000000000000000000038000000000000460000000000000000171338C5A21993423B5026450000803F0001";
    private const string HeadlightsInit =
        "A00101000000000000009E8601000A000000211000000000000001000000000000000000000038000000000000460000000000000000DAA336C585ED9442C0BA23450000803F0001";
    private const string HornUninit = "A0030100000005F510000D000000";
    private const string HeadlightsUninit = "A003010000009E8601000A000000";

    [Theory]
    [InlineData(HornInit, 1111301u, 13u)]
    [InlineData(HeadlightsInit, 99998u, 10u)]
    public void CapturedInitCarriesTheAbilityManagerKeyAndBothActorGuids(string hex, uint expectedAbility, uint expectedKey)
    {
        byte[] packet = Convert.FromHexString(hex);

        Assert.Equal(72, packet.Length);
        Assert.True(VehicleDriverControls.TryRead(packet, out uint ability, out uint key, out bool on,
            out ulong source, out ulong target));
        Assert.Equal(expectedAbility, ability);
        Assert.Equal(expectedKey, key);
        Assert.True(on);
        Assert.Equal(0x1021UL, source);
        Assert.Equal(0x4600_0000_0000_0038UL, target);
    }

    [Theory]
    [InlineData(HornUninit, 1111301u, 13u)]
    [InlineData(HeadlightsUninit, 99998u, 10u)]
    public void CapturedReleaseUsesTheFourteenByteUninitLayout(string hex, uint expectedAbility, uint expectedKey)
    {
        byte[] packet = Convert.FromHexString(hex);

        Assert.Equal(14, packet.Length);
        Assert.True(VehicleDriverControls.TryRead(packet, out uint ability, out uint key, out bool on,
            out ulong source, out ulong target));
        Assert.Equal(expectedAbility, ability);
        Assert.Equal(expectedKey, key);
        Assert.False(on);
        Assert.Equal(0UL, source);
        Assert.Equal(0UL, target);
    }

    [Theory]
    [InlineData(HornInit)]
    [InlineData(HeadlightsInit)]
    [InlineData(HornUninit)]
    [InlineData(HeadlightsUninit)]
    public void EveryTruncationAndTrailingDataIsRejected(string hex)
    {
        byte[] packet = Convert.FromHexString(hex);
        for (int length = 0; length < packet.Length; length++)
            Assert.False(VehicleDriverControls.TryRead(packet.AsSpan(0, length), out _, out _, out _, out _, out _));

        Assert.False(VehicleDriverControls.TryRead([.. packet, (byte)0], out _, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData(HornInit, 10)]
    [InlineData(HornUninit, 6)]
    public void OtherFamiliesDirectionsAndZeroAbilitiesCannotBecomeControlRequests(string hex, int abilityOffset)
    {
        byte[] packet = Convert.FromHexString(hex);
        packet[0] = 0xa1;
        Assert.False(VehicleDriverControls.TryRead(packet, out _, out _, out _, out _, out _));

        packet[0] = 0xa0;
        packet[1] = 0x02;
        Assert.False(VehicleDriverControls.TryRead(packet, out _, out _, out _, out _, out _));

        packet = Convert.FromHexString(hex);
        foreach (uint type in new uint[] { 0, 2, 3, 4, uint.MaxValue })
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), type);
            Assert.False(VehicleDriverControls.TryRead(packet, out _, out _, out _, out _, out _));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(abilityOffset), 0);
        Assert.False(VehicleDriverControls.TryRead(packet, out _, out _, out _, out _, out _));
    }

    [Fact]
    public void TheParserPreservesClaimedGuidsForTheServiceToAuthorize()
    {
        byte[] packet = Convert.FromHexString(HornInit);
        const ulong claimedSource = 0x8877_6655_4433_2211UL;
        const ulong claimedTarget = 0x1234_5678_9abc_def0UL;
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(18), claimedSource);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(38), claimedTarget);

        Assert.True(VehicleDriverControls.TryRead(packet, out _, out _, out _, out ulong source, out ulong target));
        Assert.Equal(claimedSource, source);
        Assert.Equal(claimedTarget, target);
    }

    [Theory]
    [InlineData(true, "A00D05F510000D000000")]
    [InlineData(false, "A00F05F510000D000000")]
    public void ActiveStateNotificationsAreNotClientRequests(bool on, string expectedHex)
    {
        byte[] packet = VehicleDriverControls.Ability(1111301, 13, on);
        Assert.Equal(Convert.FromHexString(expectedHex), packet);
        Assert.False(VehicleDriverControls.TryRead(packet, out _, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData(1u, "71F41000")]
    [InlineData(2u, "F5F41000")]
    [InlineData(3u, "F8F41000")]
    [InlineData(5u, "37F61000")]
    public void StartingTheEngineCreatesTheNativeRuntimeAndStoppingDestroysIt(uint family, string abilityHex)
    {
        var vehicle = new MatchVehicle(0x4600_0000_0000_0038UL, 1, VehicleRoster.LoadDefault().Require(family),
            new Vector3(1.25f, -2.5f, 3.75f), 0, 100000, 5000);
        byte[] start = VehicleDriverControls.StartEngineRuntime(vehicle, 0x1021);
        byte[] stop = VehicleDriverControls.StopEngineRuntime(family);

        // Native cb6980/ca86e0: type 3 initializes the runtime; type 1 is a client
        // request. The source block is 20 bytes and the target block is 32 bytes.
        byte[] expectedStart = Convert.FromHexString(
            "A0010300000000000000" + abilityHex + "0B000000" +
            "2110000000000000010000000000000000000000" +
            "38000000000000460000000000000000" +
            "0000A03F000020C0000070400000803F0001");
        Assert.Equal(72, start.Length);
        Assert.Equal(expectedStart, start);
        Assert.Equal(Convert.FromHexString("A00303000000" + abilityHex + "0B000000"), stop);
        Assert.False(VehicleDriverControls.TryRead(start, out _, out _, out _, out _, out _));
        Assert.False(VehicleDriverControls.TryRead(stop, out _, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData(1u, "VEH_Headlight_OffRoader_wShadows", "SFX_VEH_Offroader_Horn")]
    [InlineData(2u, "VEH_Headlight_PickupTruck_wShadows", "SFX_VEH_Pickup_Truck_Horn")]
    [InlineData(3u, "VEH_Headlight_PoliceCar_wShadows", "SFX_VEH_Police_Car_Horn")]
    [InlineData(5u, "VEH_Headlight_ATV_wShadows", "SFX_VEH_ATV_Horn")]
    public void LightAndHornEffectsBelongToTheMountedFamily(uint family, string headlights, string horn)
    {
        Assert.Equal(headlights, AugustEffectCatalog.ById(VehicleDriverControls.HeadlightsEffect(family))!.Value.Name);
        Assert.Equal(horn, AugustEffectCatalog.ById(VehicleDriverControls.HornEffect(family))!.Value.Name);
    }
}
