using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Protocol;

namespace Cranberry.Zone.World;

/// <summary>
/// August d9, read by 140b02170 / 140a3b2a0. Promotion creates the remote weapon
/// manager (140c52750) and clears the pending bit that otherwise rejects ALL 82/15
/// packets in 140b07010. See docs/remote-gunfire-20260913.md.
/// </summary>
public static class FullCharacterPackets
{
    public static byte[] Promote(PeerCharacterRecord peer,
        IReadOnlyList<CharacterEquipmentAttachment>? attachments = null,
        ReadOnlySpan<byte> movementRecord = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        Combat.RemoteWeaponPackets.GuardOwner(peer.TransientId);
        attachments ??= [];
        using var blob = new PacketWriter(1024);
        ClientVarInt.Write(blob, peer.TransientId);
        blob.WriteUInt32(0); // +04
        blob.WriteUInt32(0); // +08
        blob.WriteUInt32(0); // +0c
        blob.WriteUInt32(1); // +10, initial character state
        blob.WriteInt32(attachments.Count);
        foreach (var attachment in attachments) attachment.WriteTo(blob);
        blob.WriteString(attachments.FirstOrDefault(a => a.SlotId == 15)?.ModelName ?? "");
        blob.WriteString(attachments.FirstOrDefault(a => a.SlotId == 27)?.ModelName ?? "");
        Zeros(blob, 3); // +fc, +100, +f8
        for (int i = 0; i < 6; i++) blob.WriteString("");
        blob.WriteUInt32(0); // +104
        Zeros(blob, 3); // 140a40170: inline +108..+110
        blob.WriteInt32(0); // effect tags, 140a5a9d0
        Zeros(blob, 5); // +138..+148
        blob.WriteUInt32(3); // +14c: August MaterialTypes.txt, FLESH
        blob.WriteByte(0); // +1ac
        blob.WriteByte(0); // +1ad
        blob.WriteByte(0); // +1ae
        Zeros(blob, 2); // +1b0, +1b4 (August has both)

        EmptyBlock(blob, 1); // +160 locks: 140a56280
        EmptyBlock(blob, 1); // +170 resources: 140a56700
        EmptyBlock(blob, 2); // +180 effects: 140a49630 reads TWO counted lists
        EmptyBlock(blob, 1); // +190: 140a504e0
        EmptyBlock(blob, 1); // +1a0 remote arsenal: 141499c60, followed by separate Reset/Add/Select
        // 140a3b2a0 reads +150 only with feature bit 0x400000. The inner blob has
        // its own boundary and no exhaustion gate, so an empty optional block is
        // valid in both configurations and cannot shift the OUTER movement record.
        blob.WriteInt32(0);

        using var packet = new PacketWriter(blob.Position + 128);
        packet.WriteByte(ZoneOpcodes.LightweightToFullPc);
        packet.WriteBool(false);
        packet.WriteInt32(blob.Position);
        packet.WriteRaw(blob.Written);
        if (HasPosition(movementRecord)) packet.WriteRaw(movementRecord);
        else WriteInitialPose(packet, peer, movementRecord);
        packet.WriteByte(ZoneOpcodes.CharacterBase); // embedded Character.UpdateStat header
        packet.WriteByte(0x40);
        packet.WriteUInt64(peer.Guid);
        packet.WriteInt32(0); // stats
        packet.WriteInt32(0); // remote weapon extra state, consumed by 140c52730
        return packet.Written.ToArray();
    }

    private static bool HasPosition(ReadOnlySpan<byte> record) => record.Length >= 7
        && (BinaryPrimitives.ReadUInt16LittleEndian(record)
            & (ushort)(MovementFieldMask.Position | MovementFieldMask.PrecisePose)) != 0;

    private static void WriteInitialPose(PacketWriter w, PeerCharacterRecord peer, ReadOnlySpan<byte> last)
    {
        w.WriteUInt16((ushort)(MovementFieldMask.Position | MovementFieldMask.Rotation));
        w.WriteUInt32(last.Length >= 7 ? BinaryPrimitives.ReadUInt32LittleEndian(last[2..]) : 0);
        w.WriteByte(peer.Field178);
        Signed(w, peer.Position.X); Signed(w, peer.Position.Y); Signed(w, peer.Position.Z);
        Signed(w, peer.Rotation.X); Signed(w, peer.Rotation.Y);
        Signed(w, peer.Rotation.Z); Signed(w, peer.Rotation.W);
    }

    private static void Signed(PacketWriter w, float value)
    {
        int number = checked((int)MathF.Round(value * 100));
        uint packed = checked((uint)Math.Abs(number) << 3) | (number < 0 ? 1u : 0u);
        int bytes = packed <= 0xff ? 1 : packed <= 0xffff ? 2 : packed <= 0xffffff ? 3 : 4;
        packed |= (uint)(bytes - 1) << 1;
        for (int i = 0; i < bytes; i++) w.WriteByte((byte)(packed >> (i * 8)));
    }

    private static void EmptyBlock(PacketWriter w, int counts)
    {
        w.WriteInt32(counts * sizeof(int));
        Zeros(w, counts);
    }

    private static void Zeros(PacketWriter w, int count)
    {
        for (int i = 0; i < count; i++) w.WriteUInt32(0);
    }
}
