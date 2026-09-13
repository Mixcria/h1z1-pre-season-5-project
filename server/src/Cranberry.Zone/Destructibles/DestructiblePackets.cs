using Cranberry.Protocol;

namespace Cranberry.Zone.Destructibles;

public readonly record struct DestructibleHit(uint ObjectId, string Model, uint ProjectileId, ulong SourceGuid);

/// <summary>August native DTO codecs, FUN_1413b27e0 / 2af0 / 35d0; docs/destructibles-shooting-20260906.md.</summary>
public static class DestructiblePackets
{
    public const byte Opcode = 0xba;
    public const string EmptyModel = "Weapon_Empty.adr";

    public static bool TryReadHit(ReadOnlySpan<byte> bytes, out DestructibleHit hit)
    {
        hit = default;
        if (bytes.Length < 23 || bytes[0] != Opcode) return false;
        var reader = new PacketReader(bytes);
        try
        {
            reader.Skip(1);
            if (reader.ReadUInt16() != 1) return false;
            uint id = reader.ReadUInt32();
            uint length = reader.ReadUInt32();
            if (length is 0 or > 256 || reader.Remaining != length + 12) return false;
            var nameBytes = reader.ReadBytes((int)length);
            foreach (byte b in nameBytes) if (b < 32 || b > 126) return false;
            string name = System.Text.Encoding.ASCII.GetString(nameBytes);
            hit = new(id, name, reader.ReadUInt32(), reader.ReadUInt64());
            return reader.AtEnd;
        }
        catch (PacketFormatException) { return false; }
    }

    // The final wire flags are +40, +41, +43, +42 in that order (not memory order).
    // Disable collision, interaction and rendering; keep the override for future stream-ins.
    public static void WriteDestroyed(PacketWriter writer, DestructibleProp prop, bool effects = true)
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt16(2);
        writer.WriteUInt32(prop.ObjectId);
        writer.WriteString(EmptyModel);
        writer.WriteUInt32(effects ? prop.EffectId : 0);
        writer.WriteSingle(0);
        writer.WriteBool(false); // terrain actor, not SpeedTree
        writer.WriteUInt32(0);
        writer.WriteBool(true); // disable physics
        writer.WriteBool(true); // disable interaction
        writer.WriteBool(false); // retain override (do not forget after stream-in)
        writer.WriteBool(true); // invisible
    }

    public static void WriteInitial(PacketWriter writer, DestructibleCatalog catalog,
        IReadOnlyList<DestructibleProp> destroyed)
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt16(3);
        writer.WriteUInt32(0);
        writer.WriteUInt32((uint)destroyed.Count);
        foreach (var prop in destroyed)
        {
            writer.WriteUInt32(prop.ObjectId);
            writer.WriteString(EmptyModel);
            writer.WriteUInt32(0);
            writer.WriteBool(true);
            writer.WriteBool(true);
            writer.WriteBool(false); // retain the replacement for later stream-ins (native +0x2f)
        }
        // Native bullet AND vehicle DTO reports are gated by this actor-name hash table.
        // Enable every vehicle family too; BA/06 collision flags alone do not send reports.
        // This list does not replace or respawn intact map actors.
        uint[] hashes = catalog.Models.Concat(VehicleFencePolicy.ModelIds.Keys)
            .SelectMany(m => new[] { m, m.ToLowerInvariant(), m.ToUpperInvariant() })
            .Select(StringHashValue.HashName).Distinct().ToArray();
        writer.WriteUInt32((uint)hashes.Length);
        foreach (uint hash in hashes) { writer.WriteUInt32(hash); writer.WriteUInt32(0); }
    }
}
