using Cranberry.Protocol;
using Cranberry.Zone.Equipment;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Weapons;

namespace Cranberry.Zone.Appearance;

/// <summary>A complete dress retained for computing safe in-world per-slot updates.</summary>
public sealed class EquipmentDressSnapshot
{
    private sealed record Slot(EquipmentSlotRow? Row, CharacterEquipmentAttachment? Attachment, byte[] Bytes);
    private readonly Dictionary<uint, Slot> _slots = [];

    public EquipmentDressSnapshot(IReadOnlyList<CharacterEquipmentAttachment> attachments,
        IReadOnlyList<EquipmentSlotRow> rows)
    {
        foreach (uint slotId in attachments.Select(a => a.SlotId).Concat(rows.Select(r => r.SlotId)).Distinct())
        {
            var attachment = attachments.FirstOrDefault(a => a.SlotId == slotId);
            var row = rows.FirstOrDefault(r => r.SlotId == slotId);
            using var writer = new PacketWriter();
            writer.WriteBool(row is not null);
            row?.WriteTo(writer);
            writer.WriteBool(attachment is not null);
            attachment?.WriteTo(writer);
            _slots[slotId] = new(row, attachment, writer.Written.ToArray());
        }
    }

    /// <summary>A separately bound hand needs refreshing only when its own appearance changes.</summary>
    public bool HandDiffersFrom(EquipmentDressSnapshot? previous)
    {
        _slots.TryGetValue(BodySlots.RightHand, out var hand);
        return previous is null || !Same(hand, previous._slots.GetValueOrDefault(BodySlots.RightHand));
    }

    private static bool Same(Slot? current, Slot? previous) => current is null
        ? previous is null : previous is not null && current.Bytes.AsSpan().SequenceEqual(previous.Bytes);

    /// <summary>Null means a change lacks a safe item binding and requires the complete dress.</summary>
    public IReadOnlyList<byte[]>? DeltaFrom(EquipmentDressSnapshot previous, ulong characterId,
        IActiveHandClearance? clearance, bool handBoundSeparately = false, bool reassertBindings = false)
    {
        var packets = new List<byte[]>();
        foreach (var (slotId, old) in previous._slots)
            if (!_slots.ContainsKey(slotId))
            {
                if (handBoundSeparately && slotId == BodySlots.RightHand) continue;
                if (BodySlots.ClearingCrashesClient(slotId)) return null;
                if (old.Row is null || slotId == BodySlots.RightHand) return null;
                using var writer = new PacketWriter();
                new UnsetCharacterEquipmentSlot(characterId, slotId, ProfileId: 3).WriteTo(writer);
                packets.Add(writer.Written.ToArray());
            }
        foreach (var (slotId, current) in _slots)
        {
            if (handBoundSeparately && slotId == BodySlots.RightHand) continue;
            previous._slots.TryGetValue(slotId, out var old);
            if (Same(current, old) && (!reassertBindings || current.Row is null)) continue;
            // 94/02 requires a positive item guid. Its native handler skips the attachment
            // update for an empty model/appearance list, while still replacing the slot row.
            // This represents equipment without a mesh (e.g. belts), not removal of an old mesh.
            if (current.Row is null || current.Row.ItemGuid == 0
                || (old?.Attachment is not null && current.Attachment is null)) return null;
            var packet = new SetCharacterEquipmentSlot(characterId, current.Row,
                current.Attachment ?? new CharacterEquipmentAttachment(string.Empty, slotId), Clearance: clearance);
            if (!packet.IsPermitted) return null;
            using var writer = new PacketWriter();
            packet.WriteTo(writer);
            packets.Add(writer.Written.ToArray());
        }
        return packets;
    }
}
