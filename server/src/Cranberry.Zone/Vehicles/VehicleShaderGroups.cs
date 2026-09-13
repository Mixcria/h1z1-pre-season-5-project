namespace Cranberry.Zone.Vehicles;

/// <summary>
/// The client's own shader-parameter group a parked vehicle carries in its <c>0xd7</c> body at
/// <c>+0x1a4</c> (docs/117 §B step 3). A car sent with 0 there renders white/rust; the friend's
/// Offroader carries <b>838</b> in that slot (<c>packets_1119_53544.log:2675</c> /
/// <c>packets_1118_62892.log:1726</c>, the <c>46030000</c> dword), and the owner's Z1 round 42
/// attaches it through <c>VehicleSkinMods 4^1^1^3801</c> / <c>VehicleSkinVehicles 1^…</c> —
/// <c>ClientItemDefinitions</c> row 3801's parameter value 838, keyed to <b>vehicle id 1 only</b>
/// (adopted under D53).
/// <para>
/// The OffRoader (1) uses 838; the ATV (5) uses its own August skin group 229 (item 4302).
/// PickupTruck (2) and PoliceCar (3) retain their unmodified group 0.
/// </para>
/// </summary>
public static class VehicleShaderGroups
{
    /// <summary>The OffRoader's shader-parameter group.</summary>
    public const uint OffRoaderShaderGroup = 838;

    /// <summary>The <c>+0x1a4</c> shader group for <paramref name="vehicleId"/>, or 0 when the
    /// client's data attaches no skin to it.</summary>
    public static uint For(uint vehicleId) => vehicleId switch
    {
        1 => OffRoaderShaderGroup,
        // August VehicleSkinMods item 4302 / ClientItemDefinitions.PARAM1.
        5 => 229u,
        _ => 0u,
    };
}
