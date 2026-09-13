using Cranberry.Protocol;

namespace Cranberry.Zone;

/// <summary>August FUN_140c12ac0 / FUN_140c174a0 case 0x56: family, sub, guid, observed version.
/// This sets the existing actor's version; it does not manufacture a full motion baseline.</summary>
public sealed record CharacterMovementVersion(ulong Guid, byte Version)
{
    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(0x0f); writer.WriteByte(0x56); writer.WriteUInt64(Guid); writer.WriteByte(Version);
    }
}
