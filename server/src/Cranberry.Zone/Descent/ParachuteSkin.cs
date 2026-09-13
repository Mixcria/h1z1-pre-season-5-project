using System.Globalization;
using Cranberry.Zone.Inventory;

namespace Cranberry.Zone.Descent;

/// <summary>
/// The three parachute skins the August client ships, and the shader-parameter group each one
/// resolves to — AUDIT-parachute R22 / G6, docs/115 §6.
/// <para>
/// <b>Every number here is the client's own.</b> <c>VehicleSkinVehicles.txt</c> row 13 gives vehicle
/// 13 (the parachute) <c>NPC_PROXY_ID 2133</c> and the wardrobe static view
/// <c>kotkappearancevehiclesparachute</c>; <c>VehicleSkinMods.txt</c> rows <b>11 / 12 / 13</b> point
/// vehicle 13, <b>mod point 1</b>, at items <b>4055 / 4056 / 4057</b>; and
/// <c>ClientItemDefinitions</c> gives all three the code factory
/// <c>VehicleSkinShaderParameterGroupId</c> with the group id in <b>PARAM1</b> — <b>484</b> Green,
/// <b>492</b> Blue, <b>491</b> Tan. (The eponymous <c>SHADER_PARAMETER_GROUP_ID</c> column is 0 on
/// all three rows; the group lives in PARAM1. And the pairing is not monotonic: Blue 4056 is 492,
/// Tan 4057 is 491.) Locale carries "Green / Blue / Tan Parachute" and
/// <i>"This will apply an alternate appearance to your starting Parachute."</i>
/// </para>
/// <para>
/// <b>What is proven and what is not.</b> That these three skins exist for this vehicle, and what
/// shader group each carries, is <c>[P]</c> from the client's own sheets — this type is that table
/// and nothing more. <b>How a vehicle skin reaches the client is <c>[U]</c></b>: the self-schema for
/// the <c>0xd7</c> parser (<c>out/ghidra-aug/parachute/selfschema-FUN_140a2dd10.md:105-113</c>)
/// names the tail's two unassigned dwords at <c>+0x1c8</c> and <c>+0x1cc</c> and its trailing string
/// at <c>+0x360</c> but says nothing about their meaning — docs/12 §6 has listed them as open since
/// wave 1 — and their consumer, vehicle vtable+0x280 = <c>FUN_140e6e8b0</c>, is not in the dump.
/// Meanwhile the client registers a whole unimplemented family for exactly this,
/// <c>ZoneOpcodes.VehicleSkinBase = 0xf2</c>, whose sub-opcodes are undereived (its row in
/// <c>out/registrations-1148.md:238</c> has an empty subs column). So the carrier is a research
/// task, and until it is answered the server dresses no chute: see
/// <c>ZoneOptions.ParachuteShaderParameterGroupId</c>, which is 0 by default and writes nothing.
/// </para>
/// </summary>
public static class ParachuteSkin
{
    /// <summary>The vehicle the three skins are keyed to — <c>Vehicles.txt</c> row 13, the parachute.</summary>
    public const uint VehicleId = 13;

    /// <summary><c>VehicleSkinMods</c>' mod point for all three rows.</summary>
    public const uint ModPoint = 1;

    /// <summary>The client's wardrobe static view for the parachute, <c>VehicleSkinVehicles</c> row 13.</summary>
    public const string StaticViewLocation = "kotkappearancevehiclesparachute";

    /// <summary>Item 4055, "Green Parachute" — <c>VehicleSkinMods</c> row 11.</summary>
    public const uint GreenItemId = 4055;

    /// <summary>Item 4056, "Blue Parachute" — <c>VehicleSkinMods</c> row 12.</summary>
    public const uint BlueItemId = 4056;

    /// <summary>Item 4057, "Tan Parachute" — <c>VehicleSkinMods</c> row 13.</summary>
    public const uint TanItemId = 4057;

    /// <summary>The three item ids, in <c>VehicleSkinMods</c> row order.</summary>
    public static IReadOnlyList<uint> ItemIds { get; } = [GreenItemId, BlueItemId, TanItemId];

    /// <summary>
    /// True when <paramref name="itemId"/> is one of the client's three parachute skins — checked
    /// against the item table's own code factory rather than against this file's constants, so a
    /// regenerated <c>InventoryItemFacts</c> cannot silently disagree with it.
    /// </summary>
    public static bool IsParachuteSkin(uint itemId) =>
        ItemIds.Contains(itemId)
        && InventoryItemFacts.TryGet(itemId, out InventoryItemFact fact)
        && fact.CodeFactory == ItemCodeFactory.VehicleSkinShaderParameterGroupId;

    /// <summary>
    /// The shader-parameter group <paramref name="itemId"/> dresses the canopy in — the client's own
    /// <c>ClientItemDefinitions.PARAM1</c>: 484 Green, 492 Blue, 491 Tan. <c>0</c> for anything that
    /// is not one of the three, which is the "default parachute" answer.
    /// </summary>
    public static uint ShaderParameterGroupFor(uint itemId) =>
        IsParachuteSkin(itemId) && InventoryItemFacts.TryGet(itemId, out InventoryItemFact fact)
            ? fact.Param1
            : 0u;

    /// <summary>
    /// The item id named by <paramref name="text"/>: a decimal item id (4055/4056/4057), or one of
    /// the colour names Green / Blue / Tan, case-insensitively. <c>0</c> — the default canopy — for
    /// null, empty, "0", "off", "none", "default" and anything unrecognised. <b>Never throws</b>: a
    /// typo in a launch script must not be able to stop a host.
    /// </summary>
    public static uint Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0u;
        }

        string trimmed = text.Trim();
        if (uint.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out uint id))
        {
            return IsParachuteSkin(id) ? id : 0u;
        }

        return trimmed.ToLowerInvariant() switch
        {
            "green" => GreenItemId,
            "blue" => BlueItemId,
            "tan" => TanItemId,
            _ => 0u,
        };
    }

    /// <summary>
    /// The client's own name for <paramref name="itemId"/> ("Green Parachute", …) or
    /// "default parachute" for 0 — for the boot line and the drop log.
    /// </summary>
    public static string NameOf(uint itemId) => itemId switch
    {
        GreenItemId => "Green Parachute (4055, shader group 484)",
        BlueItemId => "Blue Parachute (4056, shader group 492)",
        TanItemId => "Tan Parachute (4057, shader group 491)",
        _ => "the default parachute",
    };
}
