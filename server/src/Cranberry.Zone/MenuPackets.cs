using Cranberry.Protocol;
using Cranberry.Zone.Progression;

namespace Cranberry.Zone;

// Replies to the requests the August client makes while it sits in the main menu (LoginZone).
// Every layout below was derived from the client's own parsers (docs/02, 2026-08-28 rows
// "menu"); each writer produces a packet that parses to its exact end, which the client
// requires for most of these (strict = 0 parsers).

/// <summary>
/// <c>LobbyGameDefinition</c> (base 0x41, u16 sub 2) answering the client's <c>41 01 00</c>
/// request: <c>u8 0x41; u16 2; i32 blobLength; blob</c>, where the blob is five counted lists
/// (LobbyGames, LobbyGameOptionMap, LobbyGameOptionValueMap, LobbyGameOptionValues,
/// LobbyGameOptions — names from the client's datasheet loader <c>FUN_142215600</c>; parsers
/// <c>FUN_140a5b670/140a53380/140a53810/140a53570/140a53180</c>). The handler
/// <c>FUN_140b031b0</c> empties client+0x321e8 and refills it; the PLAY buttons do not read
/// these tables (their readers need a live custom-lobby object), so five empty lists suffice.
/// </summary>
public sealed record LobbyGameDefinitions
{
    public const byte Opcode = ZoneOpcodes.LobbyGameDefinitionBase;
    public const ushort SubOpcode = 2;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
        writer.WriteInt32(20);
        for (int list = 0; list < 5; list++)
        {
            writer.WriteInt32(0);
        }
    }
}

/// <summary>
/// <c>StaticView</c> reply (base 0xe9, u16 sub 2; <c>FUN_141388f90</c> drops every other sub):
/// <c>i32 result (0 = success); f32 target x,y,z; f32 heading; f32 yawOffset; f32 pitch;
/// f32 distance; f32 aimYawOffset; f32 aimPitchOffset; f32 fov; u32 focusArea; u8 hideSubject</c>
/// — 52 bytes. Success switches the camera to the static-view controller (mode 0x26) around the
/// target; the request <c>E9 01 00 str name</c> names a menu camera (kotkdefault, kotkappearance,
/// kotkgamemodes, …). The client tolerates no reply (it keeps its current camera).
/// </summary>
public sealed record StaticViewReply(
    float TargetX = 0f,
    float TargetY = 0f,
    float TargetZ = 0f,
    float Heading = 0f,
    float YawOffset = 0f,
    float Pitch = 0.15f,
    float Distance = 4f,
    float Fov = 55f,
    float AimYawOffset = 0f,
    float AimPitchOffset = 0f,
    int Result = 0,
    // Native 141388e0a writes the eleventh word to animation variable UI_Focus_Area.
    // docs/105: zero on every shot the period trace has, but the gear close-ups
    // in the 2026-08-22 admin capture carry 1, 2, 4 and 5 — one value per edited body region — so
    // it is a field, not padding. Defaulted to 0 so every existing frozen vector is untouched.
    uint FocusArea = 0)
{
    public const byte Opcode = ZoneOpcodes.StaticViewBase;
    public const ushort SubOpcode = 2;

    /// <summary>
    /// Returns the two static shots present in the period menu trace. These are absolute camera
    /// eyes: distance and pitch are both zero, while the aim offsets place the local actor in the
    /// right third of the frame. Character and Appearance requests deliberately receive no new
    /// camera; the August client keeps the main-menu shot, matching the observed menu flow.
    /// </summary>
    public static bool TryForView(
        string view,
        out StaticViewReply camera,
        out System.Numerics.Vector4 mark)
    {
        switch (view)
        {
            case "kotkdefault":
                camera = new StaticViewReply(
                    TargetX: -33.5f,
                    TargetY: FromBits(0x43fde666),
                    TargetZ: FromBits(0x438c228f),
                    Heading: -1.5f,
                    YawOffset: 0f,
                    Pitch: 0f,
                    Distance: 0f,
                    Fov: 55f,
                    AimYawOffset: FromBits(0x3eff7cee),
                    AimPitchOffset: FromBits(0x3ef9db23));
                mark = new System.Numerics.Vector4(
                    FromBits(0xc1fc3d71),
                    FromBits(0x43fd35c3),
                    FromBits(0x438bfc29),
                    1f);
                return true;

            case "kotkgamemodes":
                camera = new StaticViewReply(
                    TargetX: -25f,
                    TargetY: FromBits(0x4400eccd),
                    TargetZ: 263.5f,
                    Heading: FromBits(0xbfb33333),
                    YawOffset: 0f,
                    Pitch: 0f,
                    Distance: 0f,
                    Fov: 59f,
                    AimYawOffset: FromBits(0x3ed1eb85),
                    AimPitchOffset: FromBits(0xbf1d70a4));
                mark = new System.Numerics.Vector4(
                    FromBits(0x4195ae14),
                    FromBits(0x43fcfae1),
                    FromBits(0x438c599a),
                    1f);
                return true;

            default:
                camera = null!;
                mark = default;
                return false;
        }
    }

    private static float FromBits(uint bits) =>
        BitConverter.Int32BitsToSingle(unchecked((int)bits));

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
        writer.WriteInt32(Result);
        writer.WriteSingle(TargetX);
        writer.WriteSingle(TargetY);
        writer.WriteSingle(TargetZ);
        writer.WriteSingle(Heading);
        writer.WriteSingle(YawOffset);
        writer.WriteSingle(Pitch);
        writer.WriteSingle(Distance);
        writer.WriteSingle(AimYawOffset);
        writer.WriteSingle(AimPitchOffset);
        writer.WriteSingle(Fov);
        writer.WriteUInt32(FocusArea);
        writer.WriteBool(false);  // hide subject
    }
}

/// <summary>
/// <c>RewardBuffInfo</c> (0xb0) answering <c>GetRewardBuffInfo</c> (0xb1): <c>u8 0xb0</c> then
/// exactly 13 floats (<c>FUN_140a44dd0</c>): PopulationBuff, MembershipBuff, DefenseBuff,
/// ImplantBuff, RewardImplantBuff, SquadImplantBuff, CAISBuff, MetaGameEventBuff, OperationBuff,
/// and four more the UI reads as currency-cap values. All zero = no buffs.
/// </summary>
public sealed record RewardBuffInfo
{
    public const byte Opcode = ZoneOpcodes.RewardBuffInfo;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        for (int index = 0; index < 13; index++)
        {
            writer.WriteSingle(0f);
        }
    }
}

/// <summary>
/// <c>ContinentBattleInfo</c> (0x96) answering <c>GetContinentBattleInfo</c> (0x97):
/// <c>u8 0x96; i32 count; entries</c> (<c>FUN_140a62b30</c>/<c>FUN_140a374c0</c>). An empty
/// list is valid and clears the "BaseClient.ContinentBattleInfo" manager.
/// </summary>
public sealed record ContinentBattleInfo
{
    public const byte Opcode = ZoneOpcodes.ContinentBattleInfo;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteInt32(0);
    }
}

/// <summary>
/// <c>InGamePurchase.WalletInfoResponse</c> (base 0x27, u16 sub 0x0b; handler
/// <c>FUN_141052eb0</c>): <c>i32 errorCode (1 = success); u8 flagA; u32 balance; u32 secondary;
/// str currencyCode; str label; u8 flagB; i32 extraWalletCount</c>. Balance 0 gives a defined
/// zero-crown state in the menu's top bar.
/// </summary>
public sealed record WalletInfoResponse(uint Balance = 0, string CurrencyCode = "KH$")
{
    public const byte Opcode = ZoneOpcodes.InGamePurchaseBase;
    public const ushort SubOpcode = 0x000b;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
        writer.WriteInt32(1);
        writer.WriteBool(false);
        writer.WriteUInt32(Balance);
        writer.WriteUInt32(0);
        writer.WriteString(CurrencyCode);
        writer.WriteString(string.Empty);
        writer.WriteBool(false);
        writer.WriteInt32(0);
    }
}

/// <summary>
/// <c>InGamePurchase.AccountInfoResponse</c> (base 0x27, u16 sub 0x1b; handler
/// <c>FUN_14104bc70</c>): <c>i32 errorCode (1); str locale; str currencyCode; u8 flag</c>.
/// Marks the account info as received (client+0x13c05a).
/// </summary>
public sealed record AccountInfoResponse(string Locale = "en_US", string CurrencyCode = "USD")
{
    public const byte Opcode = ZoneOpcodes.InGamePurchaseBase;
    public const ushort SubOpcode = 0x001b;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
        writer.WriteInt32(1);
        writer.WriteString(Locale);
        writer.WriteString(CurrencyCode);
        writer.WriteBool(false);
    }
}

/// <summary>
/// <c>InGamePurchase.CountryCodesResponse</c> (base 0x27, u16 sub 0x16; handler
/// <c>FUN_14104c4a0</c>): <c>i32 errorCode (1); i32 count; count × {str, str, str, str}</c>.
/// An empty list leaves the client's nine built-in countries.
/// </summary>
public sealed record CountryCodesResponse
{
    public const byte Opcode = ZoneOpcodes.InGamePurchaseBase;
    public const ushort SubOpcode = 0x0016;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
        writer.WriteInt32(1);
        writer.WriteInt32(0);
    }
}

/// <summary>
/// <c>InGamePurchase.StationCashProductsResponse</c> (u16 sub 0x10) / <c>TargetedPromoProductsResponse</c>
/// (u16 sub 0x39): <c>i32 errorCode (1); i32 skuCount; skus…</c> (record body <c>FUN_14102e9d0</c>,
/// not derived). An empty list logs "Received no skus from the world." and leaves the store empty.
/// </summary>
public sealed record StoreProductsResponse(ushort SubOpcode)
{
    public const byte Opcode = ZoneOpcodes.InGamePurchaseBase;
    public const ushort StationCashProducts = 0x0010;
    public const ushort TargetedPromoProducts = 0x0039;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
        writer.WriteInt32(1);
        writer.WriteInt32(0);
    }
}

/// <summary>
/// <c>MatchHistory</c> ranking reply (base 0x67, u8 sub 7; parser <c>FUN_1413ea760</c>, handler
/// <c>FUN_14165d3c0</c>) answering the UI's <c>RequestRanking</c> (<c>67 06 u64 0 u64 0 u32 gameMode
/// u32 0</c>): <c>u64; u64 gameUserId; u32 gameMode (must echo the request); u32 season;
/// u32 seasonNameId; u8 isOffseason; u32 region; i32 rowCount; rows…; 22 × u32 totals</c> —
/// 127 bytes with no rows. All-zero totals are safe (guarded divisions, tier 0 exists).
/// </summary>
public sealed record MatchRankingReply(uint GameMode, Match.RankedProfile? Profile = null, ulong CharacterGuid = 0,
    uint GlobalRank = 0, uint Season = 0, uint SeasonNameId = 0)
{
    public const byte Opcode = ZoneOpcodes.MatchHistoryBase;
    public const byte SubOpcode = 0x07;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt64(0);
        writer.WriteUInt64(CharacterGuid);
        writer.WriteUInt32(GameMode);
        writer.WriteUInt32(Season);
        writer.WriteUInt32(SeasonNameId);
        writer.WriteBool(false);    // off-season
        writer.WriteUInt32(0);      // region
        Match.LeaderboardPackets.WriteBestTen(writer, Profile);
        if (Profile is null)
        {
            for (int index = 0; index < 22; index++) writer.WriteUInt32(0);
            return;
        }
        Match.MatchMode mode = GameMode switch { 2 => Match.MatchMode.Duos, 3 => Match.MatchMode.Fives, _ => Match.MatchMode.Solo };
        var badge = Profile.Badge(mode);
        var progress = Match.RankedScoring.Progress(Profile, mode);
        uint[] totals = [(uint)badge.Tier, (uint)badge.Division,
            progress.PointsRemaining, progress.Percent, (uint)progress.Next.Tier, (uint)progress.Next.Division,
            (uint)Profile.Points, GlobalRank, 0, 0, (uint)Profile.Matches, Profile.Wins, Profile.TopTens,
            Profile.TotalKills, 0, 0, Profile.TotalPlacements, Profile.TotalPoints,
            (uint)(Profile.Best.FirstOrDefault()?.Points ?? 0), Profile.TopKills, 0, 0];
        foreach (uint total in totals) writer.WriteUInt32(total);
    }
}

/// <summary>
/// <c>MatchHistory</c> schedule reply (base 0x67, u8 sub 0x12): <c>i32 count; events…</c>.
/// Answers the Events window's <c>67 11 01</c>; an empty list shows no events.
/// </summary>
public sealed record MatchScheduleReply(IReadOnlyList<HostedGameScheduleEntry>? Games = null)
{
    public const byte Opcode = ZoneOpcodes.MatchHistoryBase;
    public const byte SubOpcode = 0x12;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteInt32(Games?.Count ?? 0);
        if (Games is not null)
            foreach (HostedGameScheduleEntry game in Games) game.WriteTo(writer);
    }
}

/// <summary>
/// <c>Synchronization</c> (0x8c). The client sends <c>8C; u64 t1; u64 t1; u64 x; u64 0; u64 g1; u64 g2</c>
/// every 5 s (<c>FUN_140bc0d40</c>) as an unreliable, unencrypted raw datagram; the reply
/// (parser <c>FUN_140bbf690</c>, 49 bytes, handler <c>FUN_140bbf960</c>) echoes the first three
/// u64 and carries the server clock three times: <c>ping = (now − f1)/2</c>,
/// <c>offset = ping − now + f4</c> — the server-clock offset remote-player interpolation uses.
/// </summary>
public sealed record SynchronizationReply(ulong Echo1, ulong Echo2, ulong Echo3, ulong ServerMs)
{
    public const byte Opcode = ZoneOpcodes.Synchronization;

    public static SynchronizationReply For(ReadOnlySpan<byte> request, ulong serverMs)
    {
        var reader = new PacketReader(request);
        if (reader.ReadByte() != Opcode)
        {
            throw new PacketFormatException("Expected Synchronization opcode 0x8c.");
        }

        ulong t1 = reader.ReadUInt64();
        ulong t2 = reader.ReadUInt64();
        ulong t3 = reader.ReadUInt64();
        return new SynchronizationReply(t1, t2, t3, serverMs);
    }

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt64(Echo1);
        writer.WriteUInt64(Echo2);
        writer.WriteUInt64(Echo3);
        writer.WriteUInt64(ServerMs);
        writer.WriteUInt64(ServerMs);
        writer.WriteUInt64(ServerMs & 0xffffffff);
    }
}

/// <summary>
/// <c>ClientUpdate.CompleteLogoutProcess</c> (base 0x11, u16 sub 0x30; registration levels
/// [17,48]) — the answer to <c>Command.StartLogoutRequest</c> (<c>09 4E 00 00</c>, the BACK
/// button). Case 0x30 of <c>FUN_140afc660</c> moves the client to run state 0x1d
/// (WaitingForReloginSession); it then sends <c>CharacterSelectSessionRequest</c> (0xc3).
/// </summary>
public sealed record CompleteLogoutProcess
{
    public const byte Family = ZoneOpcodes.ClientUpdateBase;
    public const ushort SubOpcode = 0x0030;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Family);
        writer.WriteUInt16(SubOpcode);
    }
}

/// <summary>
/// <c>CharacterSelectSessionResponse</c> (0xc4; parser <c>FUN_140a663d0</c>): <c>u8 status
/// (non-zero = accepted); str sessionId</c>. The session id becomes the ticket of the client's
/// new <c>LoginUdp_14</c> LoginRequest (<c>FUN_140f15410</c>), which brings it back to the
/// character-select screen; status 0 shuts the client down.
/// </summary>
public sealed record CharacterSelectSessionResponse(string SessionId, bool Accepted = true)
{
    public const byte Opcode = ZoneOpcodes.CharacterSelectSessionResponse;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteBool(Accepted);
        writer.WriteString(SessionId);
    }
}

/// <summary>
/// <c>Command.FreeInteractionNpc</c> (<c>09 16 00</c>) — three bytes, and the server sends the
/// same three back.
/// <para>
/// docs/105 §9. The August client emits this c2s whenever it releases an interaction candidate
/// (ZoneService's <c>CommandBase</c> 0x0016 arm; 475 of them across the capture corpus). The
/// 2026-08-22 admin capture shows the friend server answering every single one: 35 c2s / 35 s2c in
/// the menu session <c>1118:62892</c> and 4 c2s / 4 s2c in the match session <c>1119:53544</c>,
/// each reply 30–50 ms behind its request, with the identical body in both directions. The match
/// session has no view changes at all, which is what proves the echo is bound to the client's
/// packet rather than to a menu screen change.
/// </para>
/// </summary>
public sealed record FreeInteractionNpcEcho
{
    public const byte Opcode = ZoneOpcodes.CommandBase;

    /// <summary><c>cCommandPacketFreeInteractionNpc</c>, u16 sub 0x0016.</summary>
    public const ushort SubOpcode = 0x0016;

    /// <summary><c>1 + 2</c>.</summary>
    public const int Length = 3;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteUInt16(SubOpcode);
    }
}

// ---------------------------------------------------------------------------------------------
// docs/105 §10 (2026-09-03) - the menu TOP BAR and the MOTD panel
// (MENU-RETAIL-GAP.md §6 rows U-3 and U-4).
//
// All three layouts below were derived from the August client's own parsers, not from any capture.
// The zone dispatcher is `FUN_140af3950`, which switches on the packet's base byte
// (`switch(*(undefined1 *)(param_2 + 8))`, line 506 of the dispatcher decompile) and hands each family
// handler the WHOLE packet, base byte included - every handler below re-reads byte 0.
//
//   case 0x87  -> FUN_140cfaac0(mgr + 0xdd20, body, len)   Experience family      (line 1518)
//   case 0xab  -> FUN_140cd38f0(mgr + 0xd3c0, body, len)   Currency family        (line 1766)
//   case 0x32  -> FUN_140a7a0e0 / FUN_140a62ec0 / FUN_141072cd0   MOTD            (line 833)
//
// Fresh dump for this lane: `out\ghidra-aug\menu-topbar-motd-20260903\
// _140cfae20_140cd39c0_141072cd0_140a7a0e0\` (headless, depth 2, 2026-09-03).
// ---------------------------------------------------------------------------------------------

/// <summary>
/// <c>Experience.SetExperience</c> (base 0x87, <b>u8</b> sub 0x01) - the packet behind the menu top
/// bar's level and XP.
/// <para>
/// The family handler <c>FUN_140cfaac0</c> reads byte 0 (the base) and byte 1 (the sub) and sends
/// sub 1 to <c>FUN_140cfae20</c>, which parses through the shared table reader
/// <c>FUN_140cf69c0</c>. Reading that reader against the packet object <c>FUN_140cfae20</c> builds
/// on its own stack (<c>local_458</c> = the object base) gives the wire, in order:
/// </para>
/// <code>
///   u8   base                 obj+0x08   (0x87)
///   u8   sub                  obj+0x10   read as a byte, widened to u32 (0x01)
///   u32  flags                obj+0x18   bit 0 -> OnPlayerLevelUp, see below
///   -- the account record, 7 x u32, read by FUN_140a39740 into obj+0x20 --
///   u32  recordId             rec[0]     the hash key: FUN_140cfa570(mgr, key)
///   u32  experience           rec[1]     the XP total; "You have received %d xp." prints new - old
///   u32  word2                rec[2]     [U]
///   u32  rank                 rec[3]     1-based; the client fresh record defaults it to 1
///   u32  word4                rec[4]     NextRankPercent
///   u32  word6                rec[6]     PreviousXp   SIXTH on the wire, index 6 in memory
///   u32  word5                rec[5]     PreviousRank SEVENTH on the wire, index 5 in memory
///   i32  awardCount           obj+0x28   counted awards, element stride 0x30 (FUN_140cf7410)
///   u32  word30               obj+0x30   [U]
///   f32  experienceRate       obj+0x34   FUN_140cfdca0 takes it in xmm2 and computes
///                                        piVar10[0xd4] = (int)((float)n * rate)
///   u32  word38               obj+0x38   [U]
///   u32  word3c               obj+0x3c   [U] (client default DAT_145592024)
///   u8   word40               obj+0x40   [U] read as a bool
/// </code>
/// <para>
/// With an empty award list that is <b>55 bytes</b>, which is exactly the length of the
/// <c>Experience::cExperiencePacketIdSetExperience</c> the owner 2026-08-22 admin capture carries
/// five times (1087 base 0x88, otherwise byte-identical field for field) - so the layout is
/// unchanged between the two builds and the capture values decode cleanly against it.
/// </para>
/// <para>
/// <c>FUN_140cfa570</c> creates a missing record as <c>{key, 0, 0, 1, 0, 0, 0}</c>: the client own
/// idea of a fresh account is <b>XP 0, rank 1</b>, which is what the menu already draws unaided.
/// </para>
/// </summary>
public sealed record SetExperience
{
    public const byte Opcode = ZoneOpcodes.ExperienceBase;

    /// <summary><c>Experience::cExperiencePacketIdSetExperience</c> - a ONE-byte sub.</summary>
    public const byte SubOpcode = 0x01;

    /// <summary><c>1 + 1 + 13 x 4 + 1</c>, with an empty award list.</summary>
    public const int Length = 55;

    /// <summary>
    /// <c>obj+0x18</c>. Bit 0 raises OnPlayerLevelUp through <c>FUN_140cfa030</c>; live progression
    /// sets it only for an actual level transition. The default 3 preserves the historical capture.
    /// </summary>
    public uint Flags { get; init; } = 3u;

    /// <summary>The experience record hash key. Account progression uses 0; the capture uses 2.</summary>
    public uint RecordId { get; init; } = 2u;

    /// <summary>The XP total the top bar shows. 0 on a fresh account.</summary>
    public uint Experience { get; init; }

    /// <summary>[U] - 523 in the capture.</summary>
    public uint Word2 { get; init; } = 523u;

    /// <summary>The rank / level. The client own default for a fresh record is 1.</summary>
    public uint Rank { get; init; } = 1u;

    /// <summary>NextRankPercent. 0 in the capture.</summary>
    public uint Word4 { get; init; }

    /// <summary>PreviousXp - the SIXTH wire word; 1 in the capture.</summary>
    public uint Word6 { get; init; } = 1u;

    /// <summary>PreviousRank - the SEVENTH wire word; 513 in the capture.</summary>
    public uint Word5 { get; init; } = 513u;

    /// <summary>Awards populate ExperienceAwards and complete the victim's native kill receipt.</summary>
    public IReadOnlyList<ExperienceAward> Awards { get; init; } = [];

    /// <summary>
    /// <c>obj+0x34</c>, a <b>float</b> in this build: <c>FUN_140cfdca0</c> receives it in xmm2 and
    /// multiplies a count by it. Cranberry writes a neutral 1.0. The capture server wrote the
    /// integer 12 into the same four bytes, which as an IEEE-754 float is a denormal (~1.7e-44) and
    /// would scale everything to zero - a 1087-era difference this project does not copy.
    /// </summary>
    public float ExperienceRate { get; init; } = 1f;

    /// <summary>[U] - 11 in the capture.</summary>
    public uint Word30 { get; init; } = 11u;

    /// <summary>[U] - 13 in the capture.</summary>
    public uint Word38 { get; init; } = 13u;

    /// <summary>[U] - 14 in the capture.</summary>
    public uint Word3c { get; init; } = 14u;

    /// <summary>[U] - read as a bool; 1 in the capture.</summary>
    public bool Word40 { get; init; } = true;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt32(Flags);
        writer.WriteUInt32(RecordId);
        writer.WriteUInt32(Experience);
        writer.WriteUInt32(Word2);
        writer.WriteUInt32(Rank);
        writer.WriteUInt32(Word4);
        writer.WriteUInt32(Word6);
        writer.WriteUInt32(Word5);
        writer.WriteInt32(Awards.Count);
        foreach (ExperienceAward award in Awards)
            award.WriteTo(writer);
        writer.WriteUInt32(Word30);
        writer.WriteSingle(ExperienceRate);
        writer.WriteUInt32(Word38);
        writer.WriteUInt32(Word3c);
        writer.WriteBool(Word40);
    }
}

/// <summary>
/// <c>Currency.SetAccountCurrencyRecord</c> (base 0xab, <b>u8</b> sub 0x03) - one currency counter
/// in the menu top bar.
/// <para>
/// <c>FUN_140cd38f0</c> reads byte 0 (base) and byte 1 (sub) and routes sub 3 to
/// <c>FUN_140cd39c0</c>, which reads exactly two more <c>u32</c>s into an eight-byte record and
/// then does two things with it:
/// </para>
/// <code>
///   u8   base            0xab
///   u8   sub             0x03
///   u32  currencyId      record[0]
///   u32  amount          record[1]
///
///   FUN_1421eb5d0(mgr + 0x80, "HandleCurrencyPacketSetAccountCurrencyRecord", record)
///       -> bucket (currencyId AND 3), match on entry+0x18, store clamp(amount, 0, 0x7fffffff)
///   FUN_1414c82d0(DAT_143f6a1b0 + 0x2f8, record, amount)
///       -> the UI datasource row (PlayerCurrencyRow: currencyId, formattedValue, ...)
/// </code>
/// <para>
/// Ten bytes. The owner 2026-08-22 admin capture carries the 1087 form four times as
/// <c>ac 03 01000000 52ce0000 00000000</c> - the same two words plus a third the August parser does
/// not read, and the friend own balance of 52,818.
/// </para>
/// <para>
/// The ids are the client own <c>Currency.txt</c>: 1 Scrap, 4 Crowns, 5 Skulls, 6 Credits,
/// 7000 Daybreak Cash. The top bar (<c>UIRoot</c> <c>views.playerinfo:UIPlayerInfoManager</c>) has
/// clips for Crowns, Skulls, Tickets and Scrap.
/// </para>
/// </summary>
public sealed record SetAccountCurrencyRecord(uint CurrencyId, uint Amount)
{
    public const byte Opcode = ZoneOpcodes.CurrencyBase;

    /// <summary><c>Currency::cCurrencyPacketIdSetAccountCurrencyRecord</c> - a ONE-byte sub.</summary>
    public const byte SubOpcode = 0x03;

    /// <summary><c>1 + 1 + 4 + 4</c>.</summary>
    public const int Length = 10;

    /// <summary><c>Currency.txt</c> id 1 - the scrapyard currency.</summary>
    public const uint Scrap = 1u;

    /// <summary><c>Currency.txt</c> id 4.</summary>
    public const uint Crowns = 4u;

    /// <summary><c>Currency.txt</c> id 5.</summary>
    public const uint Skulls = 5u;

    /// <summary>
    /// <c>Currency.txt</c> id 6 - CREDITS, the "soft" ante. The locale is explicit about what it
    /// is for: <c>1967504565</c> "CREDITS are used to BACK YOUR MATCH in the Bounty menu".
    /// Cranberry never sent this row anywhere before the bounty lane (AUDIT-bounty G3).
    /// </summary>
    public const uint Credits = 6u;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteByte(SubOpcode);
        writer.WriteUInt32(CurrencyId);
        writer.WriteUInt32(Amount);
    }
}

/// <summary>
/// <c>MOTD</c> (base 0x32, no sub) - the message-of-the-day panel, and the answer to
/// MENU-RETAIL-GAP row U-4: <b>it is server-driven, by a packet, not by the launcher and not by a
/// locale string.</b>
/// <para>
/// Dispatcher line 833 constructs the packet object with <c>FUN_140a7a0e0</c> (which stamps the id
/// 0x32 and two <c>SoeUtil::StringFixed 512</c> at object offsets 0x10 and 0x230), parses it
/// with <c>FUN_140a62ec0(obj, body, len, strict: 1)</c> and, on success, pushes the two strings
/// into the UI object at <c>client+0x326c8</c> via <c>FUN_141072cd0</c> then
/// <c>FUN_141072ce0(store, title, message)</c>. The parser is:
/// </para>
/// <code>
///   u8   base            0x32          -> obj+0x08
///   i32  titleLength                                (rejected if negative or past the end)
///   char title[titleLength]            -> obj+0x10
///   i32  messageLength
///   char message[messageLength]        -> obj+0x230
/// </code>
/// <para>
/// Strict: with the dispatcher <c>strict = 1</c> the parse only succeeds when the two strings end
/// exactly at the packet last byte, so no padding is tolerated.
/// </para>
/// <para>
/// What the consumer does with the pair (<c>FUN_141072ce0</c>): both strings are widened to UTF-16;
/// an <b>empty title is replaced by the client own</b> - the string-table entry
/// <c>MessageOfTheDay</c>, falling back to the literal <c>L"Message of the Day"</c>; the title is
/// then looked up in the store entry array (base <c>+0x88</c>, count <c>+0x90</c>, stride 0x30);
/// a matching entry with an <b>empty message is removed</b> (<c>FUN_141072580</c>), a non-empty one
/// has its value replaced, and an unknown title with a non-empty message is <b>inserted</b>
/// (<c>FUN_141071250</c>). So the title is the row key and the message is the row text.
/// <c>FUN_141071250</c> and <c>FUN_141072580</c> have exactly one caller each - this one - so the
/// store is the MOTD panel own, not a general datasource table.
/// </para>
/// <para>
/// That store is what feeds the widget own SQL, which <c>UIRoot.strings.txt</c> L5572-5605 carries
/// verbatim: <c>SELECT t4lookup(UI.DUCTitle) AS label, ..., Message AS description FROM MOTD UNION
/// ... FROM NudgeOffers ... FROM BillboardPanels</c>. The panel heading is a locale lookup, not
/// anything the server sends; only the body text comes from here.
/// </para>
/// </summary>
public sealed record MessageOfTheDay(string Message, string Title = "")
{
    public const byte Opcode = ZoneOpcodes.MOTD;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Opcode);
        writer.WriteString(Title);
        writer.WriteString(Message);
    }
}
