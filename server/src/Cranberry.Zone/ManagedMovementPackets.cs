using Cranberry.Protocol;

namespace Cranberry.Zone;

/// <summary>
/// Channel-3 movement for an object simulated by this client. The August parachute proves the
/// shape live: <c>0x90; packed-u32 transientId; movement record</c>. Keeping it separate from the
/// local-player channel-2 record prevents either gateway channel from being mistaken for normal
/// base-opcode traffic.
/// </summary>
public sealed record ClientManagedMovementUpdate(
    uint TransientId,
    ClientMovementUpdate Movement)
{
    public const byte Opcode = ZoneOpcodes.PlayerUpdateManagedPosition;

    public static ClientManagedMovementUpdate Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        byte opcode = reader.ReadByte();
        if (opcode != Opcode)
        {
            throw new PacketFormatException(
                $"Expected managed-movement opcode 0x{Opcode:X2}, got 0x{opcode:X2}.");
        }

        uint transientId = ReadPackedUnsigned(ref reader);
        ClientMovementUpdate movement = ClientMovementUpdate.Parse(reader.ReadRest());
        return new ClientManagedMovementUpdate(transientId, movement);
    }

    /// <summary>Re-emits the exact movement record after its opcode and transient id.</summary>
    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        ClientVarInt.Write(writer, TransientId);
        writer.WriteRaw(Movement.Payload.Span);
    }

    private static uint ReadPackedUnsigned(ref PacketReader reader)
    {
        byte first = reader.ReadByte();
        int extra = first & 0x03;
        uint packed = first;
        for (int i = 1; i <= extra; i++)
        {
            packed |= (uint)reader.ReadByte() << (i * 8);
        }

        return packed >> 2;
    }
}

/// <summary>
/// <c>ClientUpdate.ManagedObjectResponseControl</c> (base 0x11, u16 sub 0x0039):
/// <c>u8 control; u64 objectGuid</c>. The false form removes the object from the client's managed
/// map and invokes the actor's release-control virtual. It must be sent while the actor still
/// exists, before <c>Character.RemovePlayer</c> removes a landed parachute.
/// </summary>
public sealed record ManagedObjectResponseControl(bool Control, ulong ObjectGuid)
{
    public const byte Family = ZoneOpcodes.ClientUpdateBase;
    public const ushort SubOpcode = 0x0039;
    public const int Length = 12;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Family);
        writer.WriteUInt16(SubOpcode);
        writer.WriteBool(Control);
        writer.WriteUInt64(ObjectGuid);
    }
}
