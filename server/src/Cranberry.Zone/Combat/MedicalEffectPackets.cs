using Cranberry.Protocol;

namespace Cranberry.Zone.Combat;

/// <summary>
/// August Effects 9e/06 supplies the stock EffectTag datasource. Decoder 140a38b00;
/// constructor 1421e7810; UI getter 1414d1a60. This form has no client ability:
/// the remaining nested fields retain constructor defaults, including stage -1.
/// </summary>
public sealed record MedicalEffectTag(ulong SubjectGuid, ulong InstanceId, uint EffectId, uint NameId)
{
    public const int WireLength = 115;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(ZoneOpcodes.EffectsBase);
        writer.WriteByte(6);
        writer.WriteUInt64(SubjectGuid);
        writer.WriteUInt64(InstanceId);
        writer.WriteUInt32(0);             // unknown field: constructor default
        writer.WriteUInt32(EffectId);
        writer.WriteUInt32(0);             // no client ability
        writer.WriteInt32(-1);             // no ability stage (not zero)
        writer.WriteUInt32(NameId);
        writer.WriteUInt32(0);             // description ID
        writer.WriteUInt32(0);             // icon ID; HUD selects its authored frame
        writer.WriteUInt64(SubjectGuid);   // source character (self-owned medical status)
        writer.WriteUInt32(0);
        writer.WriteUInt64(0);
        writer.WriteUInt64(0);
        writer.WriteUInt64(0);
        for (int index = 0; index < 4; index++) writer.WriteSingle(0);
        for (int index = 0; index < 4; index++) writer.WriteUInt32(0);
        writer.WriteByte(0);
    }
}

/// <summary>
/// August Effects 9e/07: a timed resource modifier used by the stock projected-health bar.
/// Decoder 140a38070; consumer 1414d2820. Rates are resource units per millisecond.
/// This is presentation state; authoritative resource updates still carry actual health.
/// </summary>
public sealed record MedicalResourceEffect(
    ulong SubjectGuid, ulong InstanceId, uint ResourceType,
    float RegenUnitsPerMs, float BurnUnitsPerMs, ulong StartedAtMs, int DurationMs)
{
    public const int WireLength = 42;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(ZoneOpcodes.EffectsBase);
        writer.WriteByte(7);
        writer.WriteUInt64(SubjectGuid);
        writer.WriteUInt64(InstanceId);
        writer.WriteUInt32(ResourceType);
        writer.WriteSingle(RegenUnitsPerMs);
        writer.WriteSingle(BurnUnitsPerMs);
        writer.WriteUInt64(StartedAtMs);
        writer.WriteInt32(DurationMs);
    }
}

/// <summary>August Effects 9e/08 removes both the tag and resource modifier by instance ID.</summary>
public sealed record RemoveMedicalEffect(ulong SubjectGuid, ulong InstanceId)
{
    public const int WireLength = 18;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(ZoneOpcodes.EffectsBase);
        writer.WriteByte(8);
        writer.WriteUInt64(SubjectGuid);
        writer.WriteUInt64(InstanceId);
    }
}
