using Cranberry.Protocol;

namespace Cranberry.Zone.Combat;

/// <summary>
/// The two <c>Character</c> (base <c>0x0f</c>) packets that kill a player client-side, and the
/// only ones: <c>0f 4f StartMultiStateDeath</c> plays the death and flips the client's own
/// "is dead" bit, <c>0f 48 KilledBy</c> is the kill feed and hands the killer to the death screen.
/// <c>ce 04 GameMode.DeathInfo</c> (<see cref="Cranberry.Zone.Match.EndgamePackets"/>) only fills
/// the boxes on the wrap-up slides — it does not open them (S4 row E1).
///
/// <para>
/// <b>Registrations (1148).</b> <c>out\registrations-1148.md:1149</c>
/// <c>| 0x48 | cCharacterPacketIdKilledBy |</c> and <c>:1155</c>
/// <c>| 0x4f | cCharacterPacketIdStartMultiStateDeath |</c>, both under
/// <c>cPacketIdCharacterBase</c>; the registering call sites are
/// <c>out\ghidra-aug\fable-character-login\_1413c37a0_1413c1ba0_1413d0330_1413c2b50_1413bef90\FUN_1413c1ba0_1413c1ba0.c:151</c>
/// (<c>0x48000f00</c>) and <c>:163</c> (<c>0x4f000f00</c>) — i.e. the sub is one byte, not two.
/// </para>
///
/// <para>
/// <b>Envelope.</b> The <c>0x0f</c> family dispatcher <c>FUN_140af9ca0</c>
/// (<c>out\ghidra-aug\character-dispatch\_140af9ca0\FUN_140af9ca0_140af9ca0.c:361-391</c>) reads
/// <c>u8 sub</c> then <c>u64 guid</c> off the front of every Character packet and routes the rest
/// to that entity's own handler (<c>vtable+0x230</c>) before its local switch — which is why
/// neither <c>0x4f</c> nor <c>0x48</c> appears as a <c>case</c> in that function. The entity-side
/// dispatcher is <c>FUN_140c4d240</c> (case <c>0x4e</c> = sub <c>0x4f</c>, case <c>0x47</c> =
/// sub <c>0x48</c>; docs/21 §2h, §2i).
/// </para>
///
/// <para>
/// <b>Reader dumps</b> for both bodies live in
/// <c>out\ghidra-aug\character-readers\_140c25ce0_140c27420_140c26580_140c21040_140a5f700_140a5f8a0_140a5fed0_140c27550_140c26680_140c21c00_140a66670_140c24a50_140a64fb0_140c81fe0_140c7d900\</c>
/// (abbreviated below to <c>character-readers\…\</c>).
/// </para>
/// </summary>
public static class DeathPackets
{
    /// <summary>Base <c>0x0f</c> — <c>cPacketIdCharacterBase</c>.</summary>
    public const byte Opcode = ZoneOpcodes.CharacterBase;
}

/// <summary>
/// <c>Character.StartMultiStateDeath</c> (<c>0f 4f</c>) — <b>the packet that actually kills a
/// player on the client</b>. <b>[P]</b> at 1148.
///
/// <para>
/// Wire: <c>u8 0f; u8 4f; u64 characterGuid; u8 deathDirection; i8 deathType; u8 flags;
/// [u64 ragdollOwner if flags &amp; 0x80]</c> = <b>13 bytes</b>, or <b>21</b> with the trailing
/// guid. The guid is consumed by the family envelope (<c>FUN_140af9ca0:361-378</c>); the body
/// reader is <c>FUN_140c21c00</c>, cursor form, in
/// <c>character-readers\…\FUN_140c21c00_140c21c00.c</c>:
/// </para>
/// <list type="bullet">
/// <item><description><c>:7-15</c> — <c>u8</c> → object <c>+0x28</c>, <see cref="DeathDirection"/>
/// (stored widened to <c>u32</c>).</description></item>
/// <item><description><c>:16-24</c> — <b><c>i8</c></b> → <c>+0x2c</c>, <see cref="DeathType"/>;
/// the read is <c>*(int *)(param_1 + 0x2c) = (int)**(char **)…</c>, i.e. <b>sign-extended</b>, so
/// a negative type is a real value and not a bug.</description></item>
/// <item><description><c>:25-33</c> — <c>u8</c> → <c>+0x30</c>, <see cref="Flags"/>.</description></item>
/// <item><description><c>:34-43</c> — <c>if (0x7f &lt; flags)</c> a further <c>u64</c> →
/// <c>+0x20</c>, <see cref="RagdollOwner"/>. <b>The gate is <c>flags &gt; 0x7f</c>, i.e. bit
/// <c>0x80</c></b> — not the owner's 1087 <c>flag &gt; 0</c> (Z1 <c>ZoneCombatWire.cs:480-499</c>).
/// Both agree only because he sends exactly <c>128</c>.</description></item>
/// </list>
///
/// <para>
/// <b>What it does</b> (<c>FUN_140c4d240</c> case <c>0x4e</c> → applier <c>FUN_140c7d900</c>,
/// docs/21 §2h): pushes <c>DeathDirection</c> / <c>DeathType</c> into the float animation
/// parameters of those names, enters animation state <c>"Death"</c> (or <c>"DeathVehicle"</c> on
/// a mount) when bit <c>0x80</c> is clear, sets <c>entity+0x37ed</c> bit <c>0x04</c> (is dead),
/// enters <c>"State_OnGround"</c>, frees the steering object — and, <b>only for the viewer's own
/// character</b>, drives the client into run state <c>0x21</c> (<c>FUN_140b7a770</c>), which is
/// what ends that player's match client-side. With bit <c>0x80</c> set it instead allocates the
/// staged multi-state-death controller and feeds it the packet.
/// </para>
///
/// <para>
/// <b>Who gets which copy.</b> The owner sends the victim a copy naming the victim as the ragdoll
/// owner, and every viewer a copy naming <i>that viewer</i> — the ragdoll is simulated by the
/// client that owns it (Z1 <c>ZoneCombat.cs:2402-2427</c>). <see cref="Ragdoll"/> and
/// <see cref="RagdollFor"/> build exactly those two, and <see cref="Simple"/> builds the 13-byte
/// no-ragdoll form.
/// </para>
/// </summary>
/// <param name="CharacterGuid">The dying character (the envelope guid, reader object <c>+0x18</c>).</param>
/// <param name="DeathDirection">Animation parameter <c>"DeathDirection"</c> (<c>+0x28</c>).</param>
/// <param name="DeathType">Animation parameter <c>"DeathType"</c> (<c>+0x2c</c>), signed.</param>
/// <param name="Flags">
/// <c>+0x30</c>. Bit <c>0x80</c> selects the staged multi-state death and is the only bit whose
/// meaning is proven; it is also the bit that puts <paramref name="RagdollOwner"/> on the wire.
/// </param>
/// <param name="RagdollOwner">
/// <c>+0x20</c>, written <b>only</b> when <c>Flags &amp; 0x80</c>. The client that owns the ragdoll
/// simulation — the recipient's own character guid.
/// </param>
public sealed record StartMultiStateDeath(
    ulong CharacterGuid,
    byte DeathDirection = 0,
    sbyte DeathType = 0,
    byte Flags = StartMultiStateDeath.RagdollFlag,
    ulong RagdollOwner = 0)
{
    public const byte Opcode = DeathPackets.Opcode;
    public const byte SubOpcode = 0x4f;

    /// <summary>Bytes without the optional owner guid: <c>1 + 1 + 8 + 1 + 1 + 1</c>.</summary>
    public const int ShortLength = 13;

    /// <summary>Bytes with it: <see cref="ShortLength"/> + 8.</summary>
    public const int LongLength = 21;

    /// <summary>
    /// The flag bit the reader gates the trailing guid on (<c>FUN_140c21c00:34</c>,
    /// <c>if (0x7f &lt; flags)</c>), and the value the owner's server sends for every death
    /// (Z1 <c>ZoneCombat.cs:252</c> <c>DeathRagdollFlag = 128</c>).
    /// </summary>
    public const byte RagdollFlag = 0x80;

    /// <summary>The staged/ragdoll death, with <paramref name="ragdollOwner"/> simulating it.</summary>
    public static StartMultiStateDeath Ragdoll(ulong characterGuid, ulong ragdollOwner) =>
        new(characterGuid, RagdollOwner: ragdollOwner);

    /// <summary>
    /// The copy <paramref name="viewerGuid"/> receives for a death of <paramref name="characterGuid"/>
    /// — the viewer owns the ragdoll in its own client (Z1 <c>ZoneCombat.cs:2424-2426</c>). For the
    /// victim's own copy pass the victim's guid twice (<c>ZoneCombat.cs:2406-2409</c>).
    /// </summary>
    public static StartMultiStateDeath RagdollFor(ulong characterGuid, ulong viewerGuid) =>
        Ragdoll(characterGuid, viewerGuid);

    /// <summary>
    /// The 13-byte form: bit <c>0x80</c> clear, so the client plays the one-shot <c>"Death"</c>
    /// animation and no staged controller is allocated and no guid is read.
    /// </summary>
    public static StartMultiStateDeath Simple(ulong characterGuid) =>
        new(characterGuid, Flags: 0);

    /// <summary>True when the trailing owner guid goes on the wire.</summary>
    public bool CarriesRagdollOwner => (Flags & RagdollFlag) != 0;

    /// <summary>13 or 21 — the client's reader takes the guid only under <see cref="RagdollFlag"/>.</summary>
    public int Length => CarriesRagdollOwner ? LongLength : ShortLength;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(CharacterGuid);
        writer.WriteByte(DeathDirection);
        writer.WriteByte(unchecked((byte)DeathType));
        writer.WriteByte(Flags);

        if (CarriesRagdollOwner)
        {
            writer.WriteUInt64(RagdollOwner);
        }
    }
}

/// <summary>
/// <c>Character.KilledBy</c> (<c>0f 48</c>) — <b>the kill feed</b>, and the packet that hands the
/// killer's guid to the game mode for the death screen. <b>[P]</b> at 1148, <b>19 bytes</b>.
///
/// <para>
/// Wire: <c>u8 0f; u8 48; u64 killerGuid; u64 victimGuid; u8 isInvulnerableCheater</c>. Reader
/// <c>FUN_140c26680</c>, self-framing, in <c>character-readers\…\FUN_140c26680_140c26680.c</c>:
/// <c>:18-28</c> <c>u8</c> opcode → <c>+8</c>; <c>:29-39</c> <c>u8</c> sub → <c>+0x10</c>;
/// <c>:40-49</c> <c>u64</c> → <c>+0x18</c>; <c>:50-59</c> <c>u64</c> → <c>+0x20</c>;
/// <c>:60-66</c> <c>u8</c> bool → <c>+0x28</c>. <b>Exact-length gated</b> at <c>:67-72</c>
/// (<c>uVar5 = end − cursor; (int)uVar5 &lt; 1</c> is required for the success return), so a
/// 20-byte copy is dropped in silence.
/// </para>
///
/// <para>
/// <b>The envelope guid is the KILLER, not the victim</b> — counter-intuitive for the name, and
/// the binary is explicit: <c>FUN_140c4d240</c> case <c>0x47</c> formats
/// <c>"%s has killed %s"</c> with the envelope entity first and the entity at <c>+0x20</c>
/// second, or <c>"Invulnerable cheater-face %s has killed %s"</c> when the trailing bool is set,
/// and posts it as a 3000 ms <c>SystemNotification</c> in category <c>"PlayerKilled"</c>
/// (docs/21 §2i). Separately, when the <i>victim</i> resolves to the local player and the game
/// mode reports type <c>0x21</c>, the handler calls <c>FUN_140e9c9a0(gameMode, &amp;killerGuid)</c>,
/// which stores the killer at <c>gameMode+0xa8</c>, sets <c>gameMode+0xa0 = 5</c> and invokes
/// <c>vtable[0x2d0](gameMode, 5)</c> — the death-screen transition.
/// </para>
///
/// <para>
/// For a death with no second party (gas, a fall, the match clock) the owner's server sends
/// <c>killer = 0</c> (Z1 <c>ZoneCombat.cs:2450-2453</c>) and gives the feed to the victim and, if
/// there is one, the killer — not to the whole server.
/// </para>
/// </summary>
/// <param name="KillerGuid">The killer — reader object <c>+0x18</c>, the family envelope guid. 0 when nobody killed them.</param>
/// <param name="VictimGuid">The player who died — reader object <c>+0x20</c>.</param>
/// <param name="IsInvulnerableCheater">
/// <c>+0x28</c>. Selects the client's alternate feed sentence; always false for us.
/// </param>
public sealed record KilledBy(ulong KillerGuid, ulong VictimGuid, bool IsInvulnerableCheater = false)
{
    public const byte Opcode = DeathPackets.Opcode;
    public const byte SubOpcode = 0x48;

    /// <summary><c>1 + 1 + 8 + 8 + 1</c>, and the reader accepts no other length.</summary>
    public const int Length = 19;

    /// <summary>A death with no killer — gas, a fall, the match ending under someone.</summary>
    public static KilledBy Environmental(ulong victimGuid) => new(0, victimGuid);

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(KillerGuid);
        writer.WriteUInt64(VictimGuid);
        writer.WriteBool(IsInvulnerableCheater);
    }
}
