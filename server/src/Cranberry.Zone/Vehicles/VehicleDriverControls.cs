using System.Buffers.Binary;
using Cranberry.Protocol;

namespace Cranberry.Zone.Vehicles;

/// <summary>August AbilityEx VehicleMotorRun/VehicleHorn and the a0/06 manager keys.
/// The client resolves the user's input bindings; the server handles abilities, never keys.</summary>
public static class VehicleDriverControls
{
    public const uint HeadlightsKey = 10;
    public const uint EngineKey = 11;
    public const uint HornKey = 13;
    public const uint HornAbility = 1111301; // Equipped item 1735.
    public const uint SirenKey = 14;
    public const uint SirenAbility = 1111295; // Police light bar, item 1732, loadout slot 35.
    public const uint SirenEffect = 275; // VEH_SirenLight_PoliceCar: flashing lights and siren sound.

    public static uint EngineAbility(uint vehicleId) => vehicleId switch
    {
        1 => 1111153, 2 => 1111285, 3 => 1111288, 5 => 1111607, _ => 0,
    };

    // ClientEffects.txt MotorRun rows: client id + SERVER_EFFECT_ID, not composite tags.
    public static byte[] RemoveMotorEffect(MatchVehicle vehicle, ulong driver)
    {
        var (clientEffect, serverEffect) = vehicle.Definition.VehicleId switch
        {
            1 => (90001u, 100042u),
            2 => (90062u, 110237u),
            3 => (90063u, 110260u),
            5 => (90187u, 120649u),
            _ => throw new ArgumentOutOfRangeException(nameof(vehicle)),
        };
        return new EffectRequest(EffectRequest.RemoveSub, new(4, clientEffect, serverEffect),
            driver, vehicle.Guid).RemoveEcho();
    }

    public static uint HornEffect(uint vehicleId) => vehicleId switch
    {
        1 => 5556, 2 => 5557, 3 => 5558, 5 => 5559, _ => 0,
    };

    public static uint HeadlightsAbility(uint vehicleId) => vehicleId switch
    {
        1 => 99998, 2 => 1111291, 3 => 1111293, 5 => 1111608, _ => 0,
    };

    public static uint HeadlightsEffect(uint vehicleId) => vehicleId switch
    {
        1 => 273, 2 => 321, 3 => 281, 5 => 355, _ => 0,
    };

    // Captured August c2s a0/01 InitAbility (72 bytes) and a0/03 UninitAbility
    // (14 bytes). a0/0d and a0/0f are server notifications, NOT client requests.
    public static bool TryRead(ReadOnlySpan<byte> payload, out uint ability, out uint key, out bool on,
        out ulong source, out ulong target)
    {
        ability = key = 0;
        source = target = 0;
        on = false;
        if (payload.Length < 14 || payload[0] != 0xa0
            || BinaryPrimitives.ReadUInt32LittleEndian(payload[2..]) != 1) return false;
        if (payload[1] == 0x01 && payload.Length == 72)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(payload[6..]) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(payload[26..]) != 1
                || payload[70] != 0 || payload[71] != 1) return false;
            on = true;
            ability = BinaryPrimitives.ReadUInt32LittleEndian(payload[10..]);
            key = BinaryPrimitives.ReadUInt32LittleEndian(payload[14..]);
            source = BinaryPrimitives.ReadUInt64LittleEndian(payload[18..]);
            target = BinaryPrimitives.ReadUInt64LittleEndian(payload[38..]);
        }
        else if (payload[1] == 0x03 && payload.Length == 14)
        {
            ability = BinaryPrimitives.ReadUInt32LittleEndian(payload[6..]);
            key = BinaryPrimitives.ReadUInt32LittleEndian(payload[10..]);
        }
        else return false;
        return ability != 0;
    }

    public static byte[] Ability(uint ability, uint key, bool on)
    {
        using var writer = new PacketWriter();
        writer.WriteByte(0xa0);
        writer.WriteByte(on ? (byte)0x0d : (byte)0x0f);
        writer.WriteUInt32(ability);
        writer.WriteUInt32(key);
        return writer.Written.ToArray();
    }

    public static bool TryReadRuntimeFailure(ReadOnlySpan<byte> payload, out uint ability,
        out uint status, out ulong source, out ulong target)
    {
        ability = status = 0;
        source = target = 0;
        if (payload.Length < 72 || payload[0] != 0xa0 || payload[1] != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(payload[2..]) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(payload[14..]) != EngineKey) return false;
        status = BinaryPrimitives.ReadUInt32LittleEndian(payload[6..]);
        ability = BinaryPrimitives.ReadUInt32LittleEndian(payload[10..]);
        source = BinaryPrimitives.ReadUInt64LittleEndian(payload[18..]);
        target = BinaryPrimitives.ReadUInt64LittleEndian(payload[38..]);
        return status != 0;
    }

    // Native cb6980 type 3 creates the client-run motor runtime. The separate
    // a0/0d notification marks it active; sending only that notification leaves K
    // trying to deactivate an ability whose runtime was never created.
    public static byte[] StartEngineRuntime(MatchVehicle vehicle, ulong driver)
    {
        using var writer = new PacketWriter();
        writer.WriteByte(0xa0);
        writer.WriteByte(0x01);
        writer.WriteUInt32(3);
        writer.WriteUInt32(0);
        writer.WriteUInt32(EngineAbility(vehicle.Definition.VehicleId));
        writer.WriteUInt32(EngineKey);
        writer.WriteUInt64(driver);
        writer.WriteUInt32(1);
        writer.WriteUInt64(0);
        writer.WriteUInt64(vehicle.Guid);
        writer.WriteUInt64(0);
        writer.WriteSingle(vehicle.Position.X);
        writer.WriteSingle(vehicle.Position.Y);
        writer.WriteSingle(vehicle.Position.Z);
        writer.WriteSingle(1);
        writer.WriteByte(0);
        writer.WriteByte(1);
        return writer.Written.ToArray();
    }

    public static byte[] StopEngineRuntime(uint vehicleId)
    {
        using var writer = new PacketWriter();
        writer.WriteByte(0xa0);
        writer.WriteByte(0x03);
        writer.WriteUInt32(3);
        writer.WriteUInt32(EngineAbility(vehicleId));
        writer.WriteUInt32(EngineKey);
        return writer.Written.ToArray();
    }
}
