using Cranberry.Harness.Protocol;
using Cranberry.Zone;

namespace Cranberry.Harness.Replay;

/// <summary>Which link a message belonged to.</summary>
public enum CaptureLink
{
    Login,
    Gateway,
}

/// <summary>What kind of thing a signature names.</summary>
public enum SignatureKind
{
    /// <summary>A LoginUdp_14 application message.</summary>
    Login,

    /// <summary>A gateway control message (LoginRequest / LoginReply).</summary>
    GatewayControl,

    /// <summary>A tunnelled ClientProtocol message on channel 0 or 1.</summary>
    Zone,

    /// <summary>Channel 2 or 3: an opcode-free stream. Never decoded (docs/71 §1).</summary>
    Movement,

    Malformed,
}

/// <summary>
/// The unit a replay compares by. A raw byte diff of two sessions is useless — guids, timestamps
/// and item ids differ every run — so the comparison is done per <b>opcode family</b> and the
/// payload is reduced to its length. That is the resolution at which a regression is legible:
/// "the server no longer sends ProximateItemBase" and "SetCharacterEquipment grew from 476 to 604
/// bytes" are actionable; a hex diff is not.
///
/// The sub-opcode is included only for the families whose width is <b>confirmed</b>
/// (<see cref="SubOpcodeWidths"/>). Everything else compares on the base opcode alone, because a
/// guessed sub-opcode would split one family into dozens of phantom signatures — exactly what
/// happens to Synchronization, whose first two payload bytes are a timestamp.
/// </summary>
public readonly record struct PacketSignature(
    CaptureLink Link,
    SignatureKind Kind,
    byte Channel,
    int Opcode,
    int SubOpcode)
{
    public const int NoSubOpcode = -1;

    public string Name => Kind switch
    {
        SignatureKind.Login => LoginOpcodes.Name((byte)Opcode),
        SignatureKind.GatewayControl => Opcode switch
        {
            GatewayWire.OpcodeLoginRequest => "Gateway.LoginRequest",
            GatewayWire.OpcodeLoginReply => "Gateway.LoginReply",
            _ => $"Gateway.opcode{Opcode}",
        },
        SignatureKind.Movement =>
            $"ch{Channel} {(Channel == GatewayWire.ChannelPlayerMovement ? "PlayerMovement" : "ManagedMovement")}",
        SignatureKind.Zone =>
            $"ch{Channel} {ZoneOpcodes.Name((byte)Opcode) ?? $"unregistered 0x{Opcode:x2}"}{SubText}",
        _ => "malformed",
    };

    private string SubText => SubOpcode == NoSubOpcode
        ? string.Empty
        : SubOpcodeWidths.Width((byte)Opcode) == 1 ? $"::{SubOpcode:X2}" : $"::{SubOpcode:X4}";

    public override string ToString() => Name;

    /// <summary>Reads the signature of a LoginUdp_14 application message.</summary>
    public static PacketSignature ForLogin(ReadOnlySpan<byte> message) => message.Length == 0
        ? new PacketSignature(CaptureLink.Login, SignatureKind.Malformed, 0, 0, NoSubOpcode)
        : new PacketSignature(CaptureLink.Login, SignatureKind.Login, 0, message[0], NoSubOpcode);

    /// <summary>
    /// Reads the signature of an ExternalGatewayApi_3 application message. The header byte packs a
    /// five-bit opcode below a three-bit channel (<see cref="GatewayWire.SplitHeader"/>) — reading
    /// it as two nibbles puts channel-1 traffic on "channel 2" and decodes the movement streams as
    /// base opcodes, which is where the tens of thousands of phantom packets come from.
    /// </summary>
    public static PacketSignature ForGateway(ReadOnlySpan<byte> message)
    {
        if (message.Length == 0)
        {
            return new PacketSignature(CaptureLink.Gateway, SignatureKind.Malformed, 0, 0, NoSubOpcode);
        }

        (byte opcode, byte channel) = GatewayWire.SplitHeader(message[0]);

        if (channel is GatewayWire.ChannelPlayerMovement or GatewayWire.ChannelManagedMovement)
        {
            return new PacketSignature(CaptureLink.Gateway, SignatureKind.Movement, channel, opcode, NoSubOpcode);
        }

        if (opcode is not (GatewayWire.OpcodeTunnelToClient or GatewayWire.OpcodeTunnelToServer))
        {
            return new PacketSignature(CaptureLink.Gateway, SignatureKind.GatewayControl, channel, opcode, NoSubOpcode);
        }

        if (message.Length < 2)
        {
            return new PacketSignature(CaptureLink.Gateway, SignatureKind.Malformed, channel, opcode, NoSubOpcode);
        }

        byte zoneOpcode = message[1];
        int sub = NoSubOpcode;
        int width = SubOpcodeWidths.Width(zoneOpcode);
        if (width == 1 && message.Length >= 3)
        {
            sub = message[2];
        }
        else if (width == 2 && message.Length >= 4)
        {
            sub = message[2] | (message[3] << 8);
        }

        return new PacketSignature(CaptureLink.Gateway, SignatureKind.Zone, channel, zoneOpcode, sub);
    }
}

/// <summary>
/// The families whose sub-opcode width is established, and only those.
///
/// Two entries differ from <see cref="ZoneSubOpcodes"/>, which the packet journal uses for
/// labelling, and the difference is deliberate rather than a copy: this table is the comparison
/// key, so a wrong width here does not merely mislabel a report — it silently splits or merges
/// signatures and hides a regression.
/// <list type="bullet">
/// <item><b>0x94 EquipmentBase is u8, not u16.</b> Cranberry.Zone's SetCharacterEquipment and
/// UnsetCharacterEquipmentSlot both declare SubOpcode as one byte (1 and 3), and the capture bears
/// it out: <c>05 94 01 05 00 00 00 …</c> is sub 1 followed by <c>u32 profileId = 5</c>, which a u16
/// read turns into a meaningless "0x0501". docs/45 §2 names the same two bytes <c>94 01</c>.</item>
/// <item><b>0xAC ItemsBase is u8</b> — SetSkinItemManager 0x23, SetSkinItem 0x24,
/// SetCurrentSkinItemCollection 0x28 (Cranberry.Zone.AccountItemPackets). Without it the whole skin
/// family collapses into one row and the docs/32 ordering guard has nothing to count.</item>
/// </list>
/// Everything absent from the table compares on its base opcode alone. That is the safe default:
/// merging two real sub-opcodes costs resolution, inventing one costs correctness.
/// </summary>
public static class SubOpcodeWidths
{
    /// <summary>1, 2, or 0 when the family has no confirmed sub-opcode.</summary>
    public static int Width(byte opcode) => opcode switch
    {
        ZoneOpcodes.CommandBase => 2,             // 0x09 — docs/71 §14.4
        ZoneOpcodes.CharacterBase => 1,           // 0x0f — RemovePlayer sub 1 (CharacterPackets)
        ZoneOpcodes.ClientUpdateBase => 2,        // 0x11 — docs/71 §14.4
        ZoneOpcodes.InGamePurchaseBase => 2,      // 0x27
        ZoneOpcodes.LobbyGameDefinitionBase => 2, // 0x41
        ZoneOpcodes.MatchHistoryBase => 1,        // 0x67 — docs/72 §5
        ZoneOpcodes.VehicleBase => 1,             // 0x88 — sub byte then a u64 guid (docs/72 §5)
        ZoneOpcodes.EquipmentBase => 1,           // 0x94 — see the class remarks
        ZoneOpcodes.WallOfDataBase => 1,          // 0x9a
        ZoneOpcodes.LoginBase => 1,               // 0xa6 — QueueTick 0x08, QueueDone 0x04
        ZoneOpcodes.ItemsBase => 1,               // 0xac — see the class remarks
        ZoneOpcodes.SynchronizedTeleportBase => 1, // 0xe8
        ZoneOpcodes.StaticViewBase => 2,          // 0xe9
        _ => 0,
    };
}
