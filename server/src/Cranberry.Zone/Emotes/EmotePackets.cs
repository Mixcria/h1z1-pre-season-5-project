using System.Buffers.Binary;
using Cranberry.Protocol;

namespace Cranberry.Zone.Emotes;

public readonly record struct EmoteRequest(bool IsStart, uint AnimationId);

/// <summary>
/// August 1148 AnimationBase playback. The native client resolves the animation definition ID
/// into its EmoteType variable and Emote/EmoteExit requests; the wire does not carry EmoteType.
/// See docs/emotes-20260906.md and native FUN_141476e70/FUN_141476f80.
/// </summary>
public static class EmotePackets
{
    public const byte Opcode = 0xf7;
    public const byte RequestStartSubOpcode = 1;
    public const byte RequestStopSubOpcode = 2;
    public const byte StartSubOpcode = 3;
    public const byte StopSubOpcode = 4;
    public const int RequestLength = 6;
    public const int PlaybackLength = 14;
    public const int ItemRowLength = 20;

    /// <summary>
    /// A manager/collection emote map is u32 count followed by keyed entries. Native
    /// FUN_140a55700 reads the slot key, then FUN_140a38d30 reads the sixteen-byte row.
    /// Default items use instance zero; FUN_1421f6fd0 validates only slot and definition.
    /// </summary>
    public static void WriteItems(PacketWriter writer, IReadOnlyList<AugustEmote> emotes)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(emotes);
        writer.WriteInt32(emotes.Count);
        foreach (AugustEmote emote in emotes)
        {
            writer.WriteUInt32(emote.SlotId); // hash-map key
            writer.WriteUInt32(emote.SlotId);
            writer.WriteUInt64(0); // built-in default, not an owned account item instance
            writer.WriteUInt32(emote.ItemDefinitionId);
        }
    }

    /// <summary>
    /// Client requests contain only an animation definition ID, never a character GUID.
    /// The authenticated connection supplies the actor. Native writers are FUN_140cc9ea0
    /// and FUN_140cc9f90. Catalog/ownership checks belong to the request handler.
    /// </summary>
    public static bool TryParseRequest(ReadOnlySpan<byte> payload, out EmoteRequest request)
    {
        request = default;
        if (payload.Length != RequestLength || payload[0] != Opcode ||
            payload[1] is not (RequestStartSubOpcode or RequestStopSubOpcode))
        {
            return false;
        }

        uint animationId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(2, 4));
        if (animationId is 0 or > int.MaxValue)
        {
            return false;
        }

        request = new EmoteRequest(payload[1] == RequestStartSubOpcode, animationId);
        return true;
    }

    public static byte[] Start(ulong characterGuid, uint animationId) =>
        WritePlayback(StartSubOpcode, characterGuid, animationId);

    public static byte[] Stop(ulong characterGuid, uint animationId) =>
        WritePlayback(StopSubOpcode, characterGuid, animationId);

    private static byte[] WritePlayback(byte subOpcode, ulong characterGuid, uint animationId)
    {
        var payload = new byte[PlaybackLength];
        payload[0] = Opcode;
        payload[1] = subOpcode;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(2, 8), characterGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(10, 4), animationId);
        return payload;
    }
}
