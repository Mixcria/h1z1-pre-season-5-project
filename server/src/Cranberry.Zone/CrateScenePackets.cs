using Cranberry.Protocol;

namespace Cranberry.Zone;

/// <summary>
/// Character.PlayAnimation (0f04), August reader 140c21610 and applier 140c63bc0.
/// The inline names use 140a12af0: a 13-bit byte length excluding the trailing NUL.
/// DurationMs sets the generic MeleeDuration parameter in seconds; it is not playback speed.
/// The crate's ShootingGalleryCrateX64.mrn accepts Open, Close, Flinch and Despawn messages.
/// </summary>
public sealed record CrateSceneAnimation(ulong ActorGuid, string AnimationName,
    uint DurationMs = 1000, string ParameterName = "", float ParameterValue = 0,
    uint ClientTime = 0, byte Flags = 0)
{
    public const byte Opcode = ZoneOpcodes.CharacterBase;
    public const byte SubOpcode = 4;

    public void WriteTo(PacketWriter writer) => new CharacterPlayAnimation(ActorGuid, AnimationName,
        DurationMs, ParameterName, ParameterValue, ClientTime, Flags).WriteTo(writer);
}
