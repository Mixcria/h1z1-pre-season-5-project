using Cranberry.Protocol;

namespace Cranberry.Zone.Combat;

/// <summary>
/// August Ui.WeaponHitFeedback, read by 1412be8f0 and dispatched by 1412caa00 to
/// 1412d7c80. This sends the complete reticle event: flesh is red, armour yellow,
/// and broken armour selects the break frame. See docs/hit-feedback-20260906.md.
/// </summary>
public sealed record WeaponHitFeedback(
    uint DamageUnits,
    bool IsAlly = false,
    bool IsHeadshot = false,
    bool HitArmour = false,
    bool CrackedArmour = false,
    bool Killed = false,
    bool IsMelee = false,
    bool IsVehicle = false,
    bool SuppressAudio = false,
    int FeedbackId = -1)
{
    public const byte Opcode = ZoneOpcodes.UiBase;
    public const byte SubOpcode = 0x10;
    public const int Length = 11;

    public byte Flags => (byte)((IsAlly ? 0 : 0x01)
        | (IsVehicle ? 0x02 : 0) | (IsHeadshot ? 0x04 : 0)
        | (IsMelee ? 0x08 : 0) | (Killed ? 0x10 : 0)
        | (SuppressAudio ? 0x20 : 0) | (HitArmour ? 0x40 : 0)
        | (CrackedArmour ? 0x80 : 0));

    public static WeaponHitFeedback For(
        in HitOutcome outcome, bool killed = false, bool melee = false, bool? armourHit = null) =>
        new((uint)Math.Max(0, outcome.DamageUnits), IsHeadshot: outcome.Headshot,
            HitArmour: armourHit ?? outcome.DamagedArmour, CrackedArmour: outcome.BrokeArmour,
            Killed: killed, IsMelee: melee);

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt32(DamageUnits);
        writer.WriteByte(Flags);
        writer.WriteInt32(FeedbackId);
    }
}

/// <summary>
/// August Audio.PostEvent (dc03), parsed by 140a5e890 and handled by 140af3510.
/// The surface cues come from legacy hit handler 1412c9f60; the punch impact cue
/// comes from 1412d7c80. Send only to the attacker. Surface feedback accompanies a
/// silent WeaponHitFeedback; lobby punches use sound alone, without a reticle event.
/// </summary>
public sealed record HitFeedbackSound(
    bool IsHeadshot = false,
    bool HitArmour = false,
    bool CrackedArmour = false,
    bool IsMelee = false)
{
    public const byte Opcode = 0xdc;
    public const byte SubOpcode = 0x03;

    public string EventName => IsMelee ? "PLAY_MELEE_ENEMY_HUMAN" : (CrackedArmour, HitArmour, IsHeadshot) switch
    {
        (true, _, true) => "UI_BREAK_ARMOR_HEAD",
        (true, _, false) => "UI_BREAK_ARMOR_BODY",
        (false, true, true) => "UI_HIT_ARMOR_HEAD",
        (false, true, false) => "UI_HIT_ARMOR_BODY",
        (false, false, true) => "UI_HIT_FLESH_HEAD",
        _ => "UI_HIT_FLESH_BODY",
    };

    public static HitFeedbackSound For(WeaponHitFeedback hit) =>
        new(hit.IsHeadshot, hit.HitArmour, hit.CrackedArmour, hit.IsMelee);

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteString(EventName);
    }
}

/// <summary>
/// Legacy packet retained for protocol documentation. Combat uses WeaponHitFeedback:
/// this older event supplies only headshot, leaving the modern reticle's colour/frame
/// arguments absent. It must not accompany WeaponHitFeedback and duplicate hit events.
/// Native 1412caa00 case 0x1c maps its four bits to 1412c9f60 as
/// ally, headshot, armour and break. Those last two arguments select legacy
/// audio cues; they do not supply the missing reticle colour/frame arguments.
/// </summary>
/// <param name="IsAlly">Bit 0.</param>
/// <param name="IsHeadshot">Bit 1.</param>
/// <param name="DamagedArmour">Bit 2 - a plate or a helmet ate this one.</param>
/// <param name="CrackedArmour">Bit 3 - ...and it broke.</param>
public sealed record ConfirmHit(
    bool IsAlly = false,
    bool IsHeadshot = false,
    bool DamagedArmour = false,
    bool CrackedArmour = false)
{
    public const byte Opcode = ZoneOpcodes.UiBase;
    public const byte SubOpcode = 0x1c;
    public const int Length = 3;

    /// <summary>The single body byte, so a test can pin the bits without a writer.</summary>
    public byte Flags =>
        (byte)((IsAlly ? 0x01 : 0)
            | (IsHeadshot ? 0x02 : 0)
            | (DamagedArmour ? 0x04 : 0)
            | (CrackedArmour ? 0x08 : 0));

    /// <summary>The marker for one resolved hit.</summary>
    public static ConfirmHit For(in HitOutcome outcome) =>
        new(IsAlly: false, outcome.Headshot, outcome.DamagedArmour, outcome.BrokeArmour);

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteByte(Flags);
    }
}
