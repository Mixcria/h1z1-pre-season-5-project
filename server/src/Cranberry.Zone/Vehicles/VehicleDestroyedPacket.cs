using Cranberry.Protocol;

namespace Cranberry.Zone.Vehicles;

/// <summary>
/// August Character.Destroyed, reader 140c21040 and entity dispatcher 140c4d240 case 0x24.
/// Model lookup 14220c720 takes the Models.txt id (EDX), not a destruction-stage index.
/// Explosion audio/effects are sent separately so stream-in does not replay the blast.
/// </summary>
public sealed record VehicleDestroyedPacket(ulong Guid, uint ModelId)
{
    public const int Length = 28;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(0x0f);
        writer.WriteByte(0x25);
        writer.WriteUInt64(Guid);
        writer.WriteUInt32(0); // +20 -> entity+9f0; explosion already played by the world effect.
        writer.WriteUInt32(ModelId); // +28 -> model lookup and replacement.
        writer.WriteUInt32(0); // +2c unused by the entity handler.
        writer.WriteBool(false); // +30: native destroyed state (vtable+310 receives true).
        writer.WriteUInt32(0); // +34 -> entity+43ac.
        writer.WriteBool(false); // +24 -> entity+9f4.
    }
}
