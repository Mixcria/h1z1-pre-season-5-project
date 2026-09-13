using Cranberry.Protocol;

namespace Cranberry.Zone.Vehicles;

/// <summary>August F2: senders 140d896c0/640/520/89890, handlers 140d891f0 and 140d890f0.</summary>
public sealed record VehicleSkinRequest(byte SubOpcode, uint VehicleId = 0, uint ModPoint = 0, uint ItemId = 0)
{
    public static VehicleSkinRequest Parse(ReadOnlySpan<byte> bytes)
    {
        var reader = new PacketReader(bytes);
        if (reader.ReadByte() != ZoneOpcodes.VehicleSkinBase) throw new PacketFormatException("Expected VehicleSkinBase.");
        byte sub = reader.ReadByte();
        var request = sub switch
        {
            2 => new VehicleSkinRequest(sub, reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32()),
            3 => new VehicleSkinRequest(sub, reader.ReadUInt32(), reader.ReadUInt32()),
            4 => new VehicleSkinRequest(sub),
            6 => new VehicleSkinRequest(sub, reader.ReadUInt32()),
            _ => throw new PacketFormatException($"Unknown vehicle-skin request {sub}."),
        };
        if (!reader.AtEnd) throw new PacketFormatException("Trailing vehicle-skin bytes.");
        return request;
    }
}

public static class VehicleSkinPackets
{
    // F2/08: vehicle count { vehicle id, mod count { mod point, reward item id } }.
    public static void WriteManager(PacketWriter writer, IReadOnlyList<VehicleSkinChoice> selections)
    {
        writer.WriteByte(ZoneOpcodes.VehicleSkinBase);
        writer.WriteByte(8);
        var vehicles = selections.GroupBy(s => s.VehicleId).OrderBy(g => g.Key).ToArray();
        writer.WriteInt32(vehicles.Length);
        foreach (var vehicle in vehicles)
        {
            writer.WriteUInt32(vehicle.Key);
            writer.WriteInt32(vehicle.Count());
            foreach (var skin in vehicle.OrderBy(s => s.ModPoint))
            {
                writer.WriteUInt32(skin.ModPoint);
                writer.WriteUInt32(skin.ItemId);
            }
        }
    }

    // F2/07's guid is the spawned preview actor, NOT a vehicle definition id.
    public static void WritePreviewResponse(PacketWriter writer, ulong guid)
    {
        writer.WriteByte(ZoneOpcodes.VehicleSkinBase);
        writer.WriteByte(7);
        writer.WriteUInt64(guid);
    }

    // 140d88a50 reads two guids and a shader group; 140d89b30 recolours the first actor.
    public static void WriteNotify(PacketWriter writer, ulong vehicleGuid, ulong characterGuid, uint shader)
    {
        writer.WriteByte(ZoneOpcodes.VehicleSkinBase);
        writer.WriteByte(1);
        writer.WriteUInt64(vehicleGuid);
        writer.WriteUInt64(characterGuid);
        writer.WriteUInt32(shader);
    }
}
