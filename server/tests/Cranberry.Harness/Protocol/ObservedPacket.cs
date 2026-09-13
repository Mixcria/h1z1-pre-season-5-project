using Cranberry.Harness.Wire;
using Cranberry.Zone;

namespace Cranberry.Harness.Protocol;

/// <summary>What layer a message belongs to.</summary>
public enum ObservedKind
{
    /// <summary>A LoginUdp_14 application message; byte 0 is a login opcode.</summary>
    Login,

    /// <summary>A gateway control message (LoginRequest / LoginReply), not a tunnel.</summary>
    GatewayControl,

    /// <summary>A tunnelled ClientProtocol message on channel 0 or 1; byte 0 is a zone base opcode.</summary>
    ZoneTunnel,

    /// <summary>Channel 2 or 3: an opcode-free movement stream. Never decoded (docs/71 §1).</summary>
    MovementStream,

    Unknown,
}

/// <summary>
/// A parsed view of one application message, enough to assert on and enough to name in a failure
/// report. The rule from docs/71 §1 is enforced here rather than left to callers: channels 2 and 3
/// carry no opcode, so their bytes are kept opaque. Decoding their first byte as a base opcode
/// manufactures thousands of phantom packets.
/// </summary>
public sealed record ObservedPacket(
    ObservedKind Kind,
    byte[] Bytes,
    byte Channel,
    byte GatewayOpcode,
    byte? ZoneOpcode,
    ushort? SubOpcodeU16,
    byte? SubOpcodeU8,
    string Name,
    ReadOnlyMemory<byte> Payload)
{
    /// <summary>True when this is a tunnelled zone message with the given base opcode.</summary>
    public bool IsZone(byte opcode) => Kind == ObservedKind.ZoneTunnel && ZoneOpcode == opcode;

    /// <summary>True when this is a tunnelled zone message with the given base and u16 sub-opcode.</summary>
    public bool IsZone(byte opcode, ushort subOpcode) =>
        IsZone(opcode) && SubOpcodeU16 == subOpcode;

    public override string ToString() => $"{Name} ({Bytes.Length} B)";

    /// <summary>Parses a LoginUdp_14 application message.</summary>
    public static ObservedPacket ParseLogin(byte[] message)
    {
        if (message.Length == 0)
        {
            return Empty(ObservedKind.Login, message, "empty login message");
        }

        byte opcode = message[0];
        return new ObservedPacket(
            ObservedKind.Login,
            message,
            Channel: 0,
            GatewayOpcode: 0,
            ZoneOpcode: opcode,
            SubOpcodeU16: null,
            SubOpcodeU8: null,
            Name: LoginOpcodes.Name(opcode),
            Payload: message.AsMemory(1));
    }

    /// <summary>Parses an ExternalGatewayApi_3 application message.</summary>
    public static ObservedPacket ParseGateway(byte[] message)
    {
        if (message.Length == 0)
        {
            return Empty(ObservedKind.Unknown, message, "empty gateway message");
        }

        (byte opcode, byte channel) = GatewayWire.SplitHeader(message[0]);

        if (channel is GatewayWire.ChannelPlayerMovement or GatewayWire.ChannelManagedMovement)
        {
            string stream = channel == GatewayWire.ChannelPlayerMovement ? "PlayerMovement" : "ManagedMovement";
            return new ObservedPacket(
                ObservedKind.MovementStream, message, channel, opcode,
                ZoneOpcode: null, SubOpcodeU16: null, SubOpcodeU8: null,
                Name: $"ch{channel} {stream}",
                Payload: message.AsMemory(1));
        }

        if (opcode is not (GatewayWire.OpcodeTunnelToClient or GatewayWire.OpcodeTunnelToServer))
        {
            string name = opcode switch
            {
                GatewayWire.OpcodeLoginRequest => "Gateway.LoginRequest",
                GatewayWire.OpcodeLoginReply => "Gateway.LoginReply",
                _ => $"Gateway.opcode{opcode}",
            };
            return new ObservedPacket(
                ObservedKind.GatewayControl, message, channel, opcode,
                ZoneOpcode: null, SubOpcodeU16: null, SubOpcodeU8: null,
                Name: name, Payload: message.AsMemory(1));
        }

        if (message.Length < 2)
        {
            return Empty(ObservedKind.Unknown, message, $"ch{channel} tunnel with no payload");
        }

        byte zoneOpcode = message[1];
        ushort? subU16 = message.Length >= 4 ? (ushort)(message[2] | (message[3] << 8)) : null;
        byte? subU8 = message.Length >= 3 ? message[2] : null;
        string baseName = ZoneOpcodes.Name(zoneOpcode) ?? $"unregistered 0x{zoneOpcode:x2}";
        string sub = ZoneSubOpcodes.Describe(zoneOpcode, message.AsSpan(2));
        string display = sub.Length == 0 ? baseName : $"{baseName}::{sub}";

        return new ObservedPacket(
            ObservedKind.ZoneTunnel, message, channel, opcode,
            zoneOpcode, subU16, subU8,
            Name: $"ch{channel} {display}",
            Payload: message.AsMemory(1));
    }

    private static ObservedPacket Empty(ObservedKind kind, byte[] message, string name) =>
        new(kind, message, 0, 0, null, null, null, name, ReadOnlyMemory<byte>.Empty);
}

/// <summary>Login opcode names. Byte 0 of a LoginUdp_14 application message (docs/03, docs/05).</summary>
public static class LoginOpcodes
{
    public const byte LoginRequest = 0x01;
    public const byte LoginReply = 0x02;
    public const byte Logout = 0x03;
    public const byte CharacterCreateRequest = 0x06;
    public const byte CharacterLoginRequest = 0x07;
    public const byte CharacterLoginReply = 0x08;
    public const byte CharacterDeleteRequest = 0x09;
    public const byte CharacterSelectInfoRequest = 0x0B;
    public const byte CharacterSelectInfoReply = 0x0C;
    public const byte ServerListRequest = 0x0D;
    public const byte ServerListReply = 0x0E;

    public static string Name(byte opcode) => opcode switch
    {
        LoginRequest => "LoginRequest",
        LoginReply => "LoginReply",
        Logout => "Logout",
        CharacterCreateRequest => "CharacterCreateRequest",
        CharacterLoginRequest => "CharacterLoginRequest",
        CharacterLoginReply => "CharacterLoginReply",
        CharacterDeleteRequest => "CharacterDeleteRequest",
        CharacterSelectInfoRequest => "CharacterSelectInfoRequest",
        CharacterSelectInfoReply => "CharacterSelectInfoReply",
        ServerListRequest => "ServerListRequest",
        ServerListReply => "ServerListReply",
        _ => $"login 0x{opcode:x2}",
    };
}

/// <summary>
/// Sub-opcode names for the handful of families the harness asserts on. Widths are the ones
/// docs/71 §14.4 records as confirmed: <c>0F</c> is u8, the <c>09</c> Command family and the
/// <c>41</c>/<c>E9</c> families are u16 little-endian. Anything not listed is left undescribed
/// rather than guessed — a wrong guess here would only ever mislabel a failure message, but a
/// mislabelled failure message is exactly what wastes a session.
/// </summary>
public static class ZoneSubOpcodes
{
    public static string Describe(byte baseOpcode, ReadOnlySpan<byte> rest)
    {
        if (rest.Length == 0)
        {
            return string.Empty;
        }

        ushort u16 = rest.Length >= 2 ? (ushort)(rest[0] | (rest[1] << 8)) : rest[0];

        return baseOpcode switch
        {
            // The Chat family's sub is a u16 as well (R2 §2: the dispatcher FUN_1412568e0 reads
            // the u16 at +1). Only the two text surfaces the console draws on are named.
            0x06 => u16 switch
            {
                0x0003 => "ConsolePrint",
                0x0005 => "ChatText",
                _ => $"chat 0x{u16:x4}",
            },
            0x09 => u16 switch
            {
                0x0007 => "InteractRequest",
                0x0008 => "InteractCancel",
                0x0015 => "PlayerSelect",
                0x0016 => "FreeInteractionNpc",
                0x002D => "InteractionString",
                0x0040 => "Command.AddWorldCommand",
                0x0042 => "Command.ExecuteCommand",
                0x0043 => "Command.ZoneExecuteCommand",
                0x0510 => "Command.Spectate",
                _ => $"cmd 0x{u16:x4}",
            },
            0x0F => rest[0] switch
            {
                0x45 => "FullCharacterDataRequest",
                _ => $"sub 0x{rest[0]:x2}",
            },
            0x11 => u16 switch
            {
                0x002F => "StartTimer",
                0x0031 => "TextAlert",
                0x0044 => "MonitorTimeDrift",
                0x0050 => "UpdateBattlEyeRegistration",
                _ => $"sub 0x{u16:x4}",
            },
            0x27 => $"purchase 0x{u16:x4}",
            0x41 => u16 == 1 ? "Request" : $"lobby 0x{u16:x4}",
            0x67 => $"MatchHistory {rest[0]:x2}",
            // The vehicle family's sub-opcode is one byte followed by a u64 guid: the recorded
            // 06 88 19 | 03 20 00 00 00 00 00 00 is AutoMount on guid 0x2003, not a u16 0x1903.
            0x88 => rest[0] switch
            {
                0x03 => "AccessType",
                0x18 => "VehicleDismiss",
                0x19 => "VehicleAutoMount",
                0x27 => "VehicleCurrentMoveMode",
                _ => $"vehicle 0x{rest[0]:x2}",
            },
            0xE9 => u16 == 1 ? "SetStaticView" : $"staticView 0x{u16:x4}",
            0x94 => u16 switch
            {
                0x0000 => "SetCharacterEquipment",
                _ => $"equipment 0x{u16:x4}",
            },
            0x9A => rest[0] switch
            {
                0x05 => "WindowEvent",
                0x06 => "Blob",
                0x0A => "Counter",
                _ => $"sub 0x{rest[0]:x2}",
            },
            0xA6 => rest[0] switch
            {
                0x04 => "QueueDone",
                0x08 => "QueueTick",
                _ => $"sub 0x{rest[0]:x2}",
            },
            0xE8 => rest[0] switch
            {
                0x02 => "ClientAck",
                0x03 => "StartWaitForTeleport",
                0x04 => "ServerAck",
                _ => $"sub 0x{rest[0]:x2}",
            },
            _ => string.Empty,
        };
    }
}
