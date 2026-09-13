using Cranberry.Protocol;

namespace Cranberry.Zone.Combat;

// August 140a34ea0 parser, 140afcc1c..140afcc35 call site, 1411ada40 consumer.
// XMM2 carries bearing and XMM3 carries the directional indicator scalar; the old
// untyped decompile omitted both floating-point arguments from its virtual call.
public sealed record IncomingDamage(uint Damage, float Bearing, float HealthFraction,
    float ArmourFraction = 0, bool Headshot = false)
{
    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(ZoneOpcodes.ClientUpdateBase);
        writer.WriteUInt16(0x1e);
        writer.WriteUInt32(0); // Apply immediately, irrespective of the peer's movement clock.
        ClientVarInt.Write(writer, 0);
        writer.WriteUInt32(Math.Max(1u, Damage)); // Native UI gates on positive damage, including armour contacts.
        writer.WriteSingle(Bearing);
        writer.WriteSingle(1f);
        writer.WriteSingle(Math.Clamp(HealthFraction, 0, 1));
        writer.WriteSingle(Math.Clamp(ArmourFraction, 0, 1));
        writer.WriteUInt32(66); // Bullet resistance category, adopted from Z1 ZoneCombatWire.DamageInfo.
        writer.WriteByte((byte)(1 | (Headshot ? 2 : 0) | (ArmourFraction > 0 ? 4 : 0)));
    }
}
