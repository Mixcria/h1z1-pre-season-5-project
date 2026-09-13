using System.Numerics;

namespace Cranberry.Zone;

// The server-owned menu camera table (docs/105).
//
// WHY THE SERVER OWNS IT. The August client asks for a named viewpoint — `E9 01 00 str name`,
// issued by the UI binding `SetStaticView(name)` — and the names come from the client's own
// `MenuItem.txt` column `STATIC_VIEW` (kotkdefault, kotkcharacter, kotkappearanceweapons, …).
// They are NOT defined anywhere in the client. `StaticViewLocations.txt` ships exactly two rows,
// `mesa` (317.43, 50, 288.68) and `hill` (122.58, 20.85, −70.34) — neither is a `kotk*` name and
// neither is on the LoginZone apron (y ≈ 506). `UIModelCameras.txt` and `RailCameras.txt` are
// header-only; `CameraInfo.txt`'s 57 rows are vehicle seat rigs; `H1Z1.exe` carries only the
// string `kotkdefault`, as the UI's hardcoded fallback, and `UIRoot.swf`'s string pool carries the
// same one name and no other. So the name is a pure server-side lookup key and THIS is the answer.
// That corrects docs/104 §4 and S8 §0.3, both of which said `StaticViewLocations.txt` resolved the
// `kotk*` names; docs/105 §2 carries the correction with its evidence.
//
// WHERE THE NUMBERS COME FROM (D193, which supersedes D21 for this table).
//
//  * `kotkdefault` and `kotkgamemodes` were already in Cranberry from the period menu trace and
//    are returned BYTE-FOR-BYTE unchanged by <see cref="StaticViewReply.TryForView"/>. Nothing
//    here recomputes them.
//  * The other fifteen rows are the DECODED VALUES of the owner's own 2026-08-22 admin capture of
//    the friend's live 2017 server, session `1118:62892` (the cold-entry menu session), decoded by
//    this project's own reader out of
//    `C:\Project\out\ingest-admin-20260822-part1\ops\cPacketIdStaticViewBase.txt` and its paired
//    `cClientUpdatePacketIdUpdateLocation.txt`. Under D53 the *values* cross and the *bytes* do
//    not: these are re-expressed in Cranberry's own `ClientProtocol_1148` writer (base 0xe9, not
//    1087's 0xea), field for field, and the 1087 hex is never copied.
//  * The capture independently confirms the retail footage: every character-screen mark sits at
//    (18.71–18.75, 505.96, 280.55–280.70), which is 3.96 m from
//    `Common_Props_SemiVolvo_Body.adr` @ (22.291, 505.979, 282.392) in the client's own
//    `LoginZone.zone` — the red tractor that IS the backdrop of the retail CHARACTER, APPEARANCE
//    and WEAPONS screens. Two independent sources, one spot.
//  * Every reply in the capture has `yawOffset = pitch = distance = 0` and `hideSubject = 0`, and
//    every paired `UpdateLocation` has identity rotation and the trailer `01 00 00` (apply = 1).
//
// NAMES THE CAPTURE DOES NOT COVER. `kotkappearancefte` (seen on the August wire, docs/02
// 2026-08-28 row 107) takes its parent APPEARANCE shot; `kotkappearancegearback` takes the GEAR
// shot; `kotkbrduos`, `kotkbrfives` and `kotkevents` are children of PLAY in the client's own
// `MenuItem.txt` and take the proven `kotkgamemodes` shot. `kotksettings` and `kotktwitch` get no
// reply at all — they are panels over the standing shot in retail, and the capture never answers
// them either. They are the [U] rows of docs/105 §6.
public enum MenuViewCoverage
{
    /// <summary>No <c>StaticViewReply</c> at all; every view inherits the client's current camera.</summary>
    Off,

    /// <summary>Only the two shots present in the period trace — the pre-D193 behaviour.</summary>
    Reference,

    /// <summary>Those two plus the fifteen capture-decoded rows and the nine aliases.</summary>
    All,
}

/// <summary>Host-configurable coverage of the menu camera table (docs/105).</summary>
public sealed record MenuViewOptions
{
    public const string CoverageVariable = "CRANBERRY_MENU_VIEWS";

    /// <summary>Default ON: every name this project has a derived shot for.</summary>
    public MenuViewCoverage Coverage { get; init; } = MenuViewCoverage.All;

    public static MenuViewOptions Default { get; } = new();

    /// <summary>
    /// <c>CRANBERRY_MENU_VIEWS</c>: <c>all</c> (default), <c>reference</c> (the two capture-proven
    /// shots only — the exact pre-D193 behaviour), <c>off</c>/<c>0</c> (answer nothing).
    /// An unrecognised value keeps the default rather than failing a boot.
    /// </summary>
    /// <param name="read">
    /// Usually <c>Environment.GetEnvironmentVariable</c>; the host passes
    /// <c>CranberryConfig.Read</c> so <c>cranberry.json</c> can set it too (lane 0D).
    /// </param>
    public static MenuViewOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= System.Environment.GetEnvironmentVariable;
        string? raw = read(CoverageVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Default;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "0" or "off" or "none" or "false" => Default with { Coverage = MenuViewCoverage.Off },
            "reference" or "ref" or "capture" => Default with { Coverage = MenuViewCoverage.Reference },
            "all" or "1" or "true" => Default with { Coverage = MenuViewCoverage.All },
            _ => Default,
        };
    }

    public string Describe() => Coverage switch
    {
        MenuViewCoverage.Off =>
            "menu cameras: OFF (CRANBERRY_MENU_VIEWS=off) — every E9 01 request is unanswered and "
                + "the client keeps whatever camera it has",
        MenuViewCoverage.Reference =>
            "menu cameras: reference only (2 shots — kotkdefault, kotkgamemodes; "
                + "CRANBERRY_MENU_VIEWS=all adds the other 24)",
        _ =>
            $"menu cameras: ON — {MenuViewTable.AnsweredNames.Count} names answered "
                + $"(2 from the period trace, {MenuViewTable.CaptureRowCount} decoded from the "
                + "2026-08-22 admin capture, "
                + $"{MenuViewTable.AnsweredNames.Count - 2 - MenuViewTable.CaptureRowCount} aliased); "
                + $"{MenuViewTable.InheritNames.Count} deliberately inherited "
                + $"({string.Join(", ", MenuViewTable.InheritNames)}); "
                + "CRANBERRY_MENU_VIEWS=reference reverts",
    };
}

/// <summary>
/// Name → (camera, subject mark). See the file header for the derivation and docs/105 for the
/// evidence table.
/// </summary>
public static class MenuViewTable
{
    /// <summary>The two shots that come straight out of the period trace, byte for byte.</summary>
    public static Vector4 SubjectRotation(string view) =>
        string.Equals(view, "kotkappearancegearback", StringComparison.OrdinalIgnoreCase)
            ? new Vector4(0, 1, 0, 0) : new Vector4(0, 0, 0, 1);

    public static IReadOnlyList<string> ReferenceNames { get; } = ["kotkdefault", "kotkgamemodes"];

    /// <summary>
    /// Names the client asks for that this table deliberately leaves unanswered, so "no reply"
    /// reads as a decision rather than an omission. The friend capture never answers them either.
    /// </summary>
    public static IReadOnlyList<string> InheritNames { get; } = ["kotksettings", "kotktwitch"];

    private static StaticViewReply Shot(
        float x, float y, float z, float heading, float aimYaw, float aimPitch, float fov, uint focus) =>
        new(
            TargetX: x,
            TargetY: y,
            TargetZ: z,
            Heading: heading,
            YawOffset: 0f,
            Pitch: 0f,
            Distance: 0f,
            Fov: fov,
            AimYawOffset: aimYaw,
            AimPitchOffset: aimPitch,
            Result: 0,
            FocusArea: focus);

    // The three subject marks the capture uses. Every character screen stands the actor within
    // 15 cm of the same spot, 3.96 m from the client's own Volvo tractor.
    private static readonly Vector4 YardMark = new(18.71f, 505.96f, 280.70f, 1f);
    private static readonly Vector4 YardMarkGear = new(18.75f, 505.965f, 280.55f, 1f);
    private static readonly Vector4 YardMarkTorso = new(18.71f, 505.96f, 280.68f, 1f);
    private static readonly Vector4 YardMarkLower = new(18.71f, 505.96f, 280.58f, 1f);

    /// <summary>
    /// The fifteen rows decoded out of the admin capture's menu session, in the order the client
    /// asked for them. Every field is the capture's own value; nothing here is composed.
    /// </summary>
    private static readonly Dictionary<string, (StaticViewReply Camera, Vector4 Mark)> Captured =
        new(StringComparer.Ordinal)
        {
            ["kotkcharacter"] =
                (Shot(16.97f, 507.00f, 278.60f, -2.5f, 0.445f, 0.310f, 53.2f, 1), YardMark),
            ["kotkappearance"] =
                (Shot(17.63f, 507.32f, 279.20f, -2.25f, 0.475f, 0.550f, 54.5f, 1), YardMark),
            ["kotkappearancegear"] =
                (Shot(16.00f, 507.10f, 280.90f, 5.25f, -0.33f, 0.35f, 53.0f, 1), YardMarkGear),
            ["kotkappearancegearface"] =
                (Shot(17.29f, 507.49f, 280.80f, 5.25f, -0.32f, 0.76f, 52.0f, 1), YardMarkGear),
            ["kotkappearancegeareyes"] =
                (Shot(17.29f, 507.49f, 280.80f, 5.25f, -0.32f, 0.76f, 52.0f, 1), YardMarkGear),
            ["kotkappearancegearchest"] =
                (Shot(17.15f, 507.30f, 281.05f, 5.5f, -0.39f, 0.65f, 58.5f, 2), YardMarkTorso),
            ["kotkappearancegearbodyarmor"] =
                (Shot(17.15f, 507.30f, 281.05f, 5.5f, -0.39f, 0.65f, 58.5f, 2), YardMarkTorso),
            ["kotkappearancegearhands"] =
                (Shot(17.15f, 507.50f, 281.045f, 5.4f, -0.395f, 0.69f, 58.5f, 4), YardMarkTorso),
            ["kotkappearancegearlegs"] =
                (Shot(16.80f, 506.74f, 280.88f, 5.4f, -0.337f, 0.327f, 57.7f, 5), YardMarkLower),
            ["kotkappearancegearfeet"] =
                (Shot(17.15f, 506.40f, 281.05f, 4.6f, -0.38f, 0.20f, 59.0f, 5), YardMarkLower),
            ["kotkappearanceweapons"] =
                (Shot(16.00f, 507.10f, 280.90f, 5.25f, -0.33f, 0.35f, 53.0f, 1), YardMarkGear),
            ["kotkappearanceweaponsguns"] =
                (Shot(17.15f, 507.30f, 281.05f, 5.3f, -0.41f, 0.62f, 56.5f, 0), YardMarkGear),
            ["kotkappearancevehiclesatv"] =
                (Shot(-22.00f, 508.95f, 283.27f, 0f, 0.793f, -0.107f, 55.1f, 0), YardMark),
            ["kotkcrates"] =
                (Shot(-67.95f, 507.751f, 307.163f, 0f, 2.8098f, 0.092f, 49.7f, 1), YardMarkGear),
            ["kotkgrinder"] =
                (Shot(35.20f, 510.16f, 258.40f, -2.5f, 0.60f, -0.095f, 54.0f, 1), YardMark),
        };

    /// <summary>
    /// Names the capture never carries, resolved to the screen they belong to rather than to an
    /// invented shot. `kotkstats` is the STATS page under CHARACTER (`MenuItem.txt` row 29) and
    /// takes the CHARACTER shot; the three PLAY children take the proven overhead helicopter shot;
    /// `kotkappearancefte` and `…gearback` take their own parent screens.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["kotkappearancefte"] = "kotkappearance",
        ["kotkappearancegearback"] = "kotkappearancegear",
        // The hat/helmet slot. The capture opened FACE and EYES but never HEAD; those two share one
        // head close-up (17.29, 507.49, 280.80 at fov 52), which is the framing HEAD wants too.
        ["kotkappearancegearhead"] = "kotkappearancegearface",
        ["kotkappearanceweaponsmelee"] = "kotkappearanceweaponsguns",
        // EMOTES shares GEAR/WEAPONS' left-side full-body framing, clear of the selection grid.
        // Its inspection pose is cleared below so the native emote preview can animate.
        ["kotkappearanceemotes"] = "kotkappearancegear",
        ["kotkstats"] = "kotkcharacter",
        ["kotkbrduos"] = "kotkgamemodes",
        ["kotkbrfives"] = "kotkgamemodes",
        ["kotkevents"] = "kotkgamemodes",
    };

    /// <summary>How many rows carry the capture's own decoded values.</summary>
    public static int CaptureRowCount => Captured.Count;

    /// <summary>Every name <see cref="MenuViewCoverage.All"/> answers, sorted for the tests.</summary>
    public static IReadOnlyList<string> AnsweredNames { get; } =
        [.. ReferenceNames.Concat(Captured.Keys).Concat(Aliases.Keys).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Resolves a viewpoint name. Returns false when the name is unknown or deliberately inherited,
    /// which is the client-safe answer: it keeps the camera it has and logs nothing
    /// (`ClientStaticViewError.log` is only written for a NON-ZERO result, never for silence).
    /// </summary>
    public static bool TryResolve(
        string view,
        MenuViewCoverage coverage,
        out StaticViewReply camera,
        out Vector4 mark)
    {
        camera = null!;
        mark = default;
        if (coverage == MenuViewCoverage.Off)
        {
            return false;
        }

        // The two proven shots always come from the capture-derived writer in MenuPackets, never
        // from this table — their frozen vectors are pinned by MenuPacketTests.
        if (StaticViewReply.TryForView(view, out camera, out mark))
        {
            return true;
        }

        if (coverage != MenuViewCoverage.All)
        {
            return false;
        }

        if (Aliases.TryGetValue(view, out string? target))
        {
            if (!TryResolve(target, coverage, out camera, out mark)) return false;
            if (view == "kotkappearanceemotes")
            {
                // Native 141388e0a writes this as animation variable UI_Focus_Area.
                // Clothing inspection (1) masks emotes; retain the framing with normal animation (0).
                camera = camera with { FocusArea = 0 };
            }
            if (view == "kotkappearancegearback")
            {
                // 141388a10 passes Heading to 140c75ba0, replacing the actor's yaw.
                // A preceding UpdateLocation rotation is overwritten. Turn the actor
                // here while preserving Heading + YawOffset for the camera offset.
                camera = camera with { Heading = camera.Heading + MathF.PI, YawOffset = camera.YawOffset - MathF.PI };
            }
            return true;
        }

        if (Captured.TryGetValue(view, out (StaticViewReply Camera, Vector4 Mark) row))
        {
            (camera, mark) = row;
            return true;
        }

        return false;
    }
}

// ---------------------------------------------------------------------------------------------
// docs/105 addendum 2026-09-03 — the menu ACTOR, as opposed to the menu camera above.
//
// Three observables the 2026-08-22 admin capture settles, all in the same menu session
// `1118:62892` that produced the camera table (this project's own reader over
// `C:\Project\out\ingest-admin-20260822-part1\ops\`; D53 — the decoded values cross, the 1087
// bytes never do):
//
//  1. WEAPON STANCE ON A VIEW CHANGE (MENU-RETAIL-GAP U-1, S18). The friend server sends
//     `cCharacterPacketIdWeaponStance` 36× in that session: ONCE with stance 1 at t = 27.697 s,
//     before any view is asked for, and then stance **0** exactly once per view change — 35
//     requests, 35 replies, 35 stance-0 packets, every one within 12 ms of its reply and always
//     after the paired `UpdateLocation`. Nothing else poses the lobby actor: all nine
//     `PlayAnimation` in the whole capture are melee swings in the MATCH session.
//
//  2. THE `09 16 00` ECHO (U-1's second half). `cCommandPacketFreeInteractionNpc` is 35 c2s / 35
//     s2c in the menu session and 4 c2s / 4 s2c in the match session `1119:53544`, which answers
//     the question MENU-RETAIL-GAP left open: it is **not** a view-change packet, it is a
//     one-for-one ECHO of the client's own 3-byte `09 16 00`, returned 30–50 ms later. The match
//     session has four of each and zero view changes, so the pairing cannot be coincidence. The
//     body is the same three bytes in both directions on every one of the 78 sends.
//
//  3. WHERE THE ACTOR STANDS BEFORE THE FIRST CAMERA (U-5). The `kotkdefault` mark is
//     `11 0a 00 713dfcc1 c335fd43 29fc8b43 0000803f …` = (−31.53, 506.42, **279.97**, 1). Cranberry
//     spawned the self record at 279.72 — 25 cm below the proven mark, for no recorded reason —
//     so the very first `UpdateLocation` had to correct it.
//
// U-6 (rotation) needs no code. The friend server's own `SendSelfToClient` carries position
// (650.98, 148.47, −1611.41, 1) at blob offset 136 and rotation **(0, 0, 0, 1)** at 152 — identity,
// which is exactly what <see cref="SelfRecord.Orientation"/> already defaults to and writes. Z1's
// heading of 2.16966 rad is his own 1087-era design and the capture contradicts it, so nothing is
// ported. See docs/105 §9.

/// <summary>
/// docs/105 §9 — the menu actor's stance, interaction echo and pre-camera mark. Each arm is
/// separately revertible; all three default ON.
/// </summary>
public sealed record MenuActorOptions
{
    /// <summary><c>CRANBERRY_MENU_STANCE</c>.</summary>
    public const string StanceVariable = "CRANBERRY_MENU_STANCE";

    /// <summary><c>CRANBERRY_MENU_INTERACTION_ECHO</c>.</summary>
    public const string InteractionEchoVariable = "CRANBERRY_MENU_INTERACTION_ECHO";

    /// <summary><c>CRANBERRY_MENU_SPAWN_MARK</c>.</summary>
    public const string SpawnMarkVariable = "CRANBERRY_MENU_SPAWN_MARK";

    /// <summary>
    /// The capture's own <c>kotkdefault</c> subject mark, read out of the paired
    /// <c>ClientUpdate.UpdateLocation</c>: <c>713dfcc1 c335fd43 29fc8b43 0000803f</c>.
    /// </summary>
    public static Vector4 ProvenMenuMark { get; } = new(-31.53f, 506.42f, 279.97f, 1f);

    /// <summary>What Cranberry spawned at before 2026-09-03 — 25 cm low.</summary>
    public static Vector4 LegacyMenuMark { get; } = new(-31.53f, 506.42f, 279.72f, 1f);

    /// <summary>Send <c>Character.WeaponStance</c> = 0 after every answered view change.</summary>
    public bool SendWeaponStanceOnViewChange { get; init; } = true;

    /// <summary>Echo the client's own 3-byte <c>09 16 00</c> straight back at it.</summary>
    public bool EchoFreeInteractionNpc { get; init; } = true;

    /// <summary>Where the self record spawns the lobby actor.</summary>
    public Vector4 SpawnPosition { get; init; } = ProvenMenuMark;

    public static MenuActorOptions Default { get; } = new();

    /// <summary>
    /// <c>CRANBERRY_MENU_STANCE=0</c>, <c>CRANBERRY_MENU_INTERACTION_ECHO=0</c> and
    /// <c>CRANBERRY_MENU_SPAWN_MARK=legacy</c> each revert one arm. Unrecognised values keep the
    /// default rather than failing a boot, as everywhere else in this project.
    /// </summary>
    /// <param name="read">
    /// Usually <c>Environment.GetEnvironmentVariable</c>; the host passes
    /// <c>CranberryConfig.Read</c> so <c>cranberry.json</c> can set these too (lane 0D).
    /// </param>
    public static MenuActorOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= System.Environment.GetEnvironmentVariable;
        return Default with
        {
            SendWeaponStanceOnViewChange = Flag(read, StanceVariable, @default: true),
            EchoFreeInteractionNpc = Flag(read, InteractionEchoVariable, @default: true),
            SpawnPosition =
                Flag(read, SpawnMarkVariable, @default: true) ? ProvenMenuMark : LegacyMenuMark,
        };
    }

    private static bool Flag(Func<string, string?> read, string variable, bool @default)
    {
        string? raw = read(variable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return @default;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "0" or "off" or "no" or "false" or "legacy" or "reference" => false,
            "1" or "on" or "yes" or "true" or "capture" or "proven" => true,
            _ => @default,
        };
    }

    public string Describe() =>
        "menu actor: WeaponStance 0 per answered view "
            + (SendWeaponStanceOnViewChange
                ? "ON (35/35 in the 2026-08-22 admin capture; CRANBERRY_MENU_STANCE=0 reverts)"
                : "off (CRANBERRY_MENU_STANCE=0)")
            + ", 09 16 00 echo "
            + (EchoFreeInteractionNpc
                ? "ON (39 c2s / 39 s2c, one for one, across both captured sessions; "
                    + "CRANBERRY_MENU_INTERACTION_ECHO=0 reverts)"
                : "off (CRANBERRY_MENU_INTERACTION_ECHO=0)")
            + $", spawn mark ({SpawnPosition.X}, {SpawnPosition.Y}, {SpawnPosition.Z}) "
            + (SpawnPosition == ProvenMenuMark
                ? "= the capture's kotkdefault mark (CRANBERRY_MENU_SPAWN_MARK=legacy restores 279.72)"
                : "= the pre-2026-09-03 value");
}

// ---------------------------------------------------------------------------------------------
// docs/105 §10 addendum 2026-09-03 - the menu TOP BAR (MENU-RETAIL-GAP U-3) and the MOTD (U-4).
//
// U-3. The two packets that own the top bar are `Experience.SetExperience` (1148 base 0x87, u8 sub
// 0x01) and `Currency.SetAccountCurrencyRecord` (base 0xab, u8 sub 0x03). Both bodies are derived
// in `MenuPackets.cs` from the August client own parsers - `FUN_140cfae20` / `FUN_140cf69c0` /
// `FUN_140a39740` for the first, `FUN_140cd39c0` for the second - and NOT from a capture; the
// 2026-08-22 admin capture only confirms the length (55 B / 14 B at 1087) and supplies values.
//
// What the two sources say a fresh account holds:
//
//   the client itself    `FUN_140cfa570` creates a missing experience record as
//                        {key, 0, 0, 1, 0, 0, 0} - XP 0, rank 1. `Currency.txt` gives ids and
//                        names (1 Scrap, 4 Crowns, 5 Skulls, 6 Credits, 7000 Daybreak Cash) and
//                        VALUE_MAX 0 for every one of them, i.e. no cap and no starting balance.
//                        `Experience.txt` is the XP AWARD table (ID^AWARD_TYPE_ID^STRING_ID^
//                        STRING_ID_SECONDARY^XP^NOTABLE_EVENT, 432 rows) - it says nothing about a
//                        starting total. So the client sheets do not define starting values; they
//                        define the ids and the awards.
//   the friend server    TWO of his sessions decode against the layout, and they disagree in
//                        exactly the two fields that should move: menu session 1118:62892 sends
//                        rec[1] = 0 with rec[3] = 1 and rec[4] = 0, while session 1118:64205 sends
//                        rec[1] = 31,662 with rec[3] = 6 and rec[4] = 16. Every other word is
//                        identical between them (flags 3, key 2, 523, 1, 513, empty rank list, then
//                        11 / 12 / 13 / 14 / 1). That covariance is what proves rec[1] is the XP
//                        TOTAL and rec[3] the RANK, and it also shows 11 / 12 / 13 / 14 to be his
//                        server own placeholders rather than data. His one currency row is id 1
//                        (Scrap) = 52,818 - his played balance, not a fresh one.
//   Z1                   sends both, as frozen capture bytes rather than values: `SetExperienceBytes`
//                        and `AccountCurrencyBytes` in `C:\Z1\Server\Zone\ZoneCaptureParity.cs`
//                        :230 and :239 are the 1087 payloads above replayed verbatim, so Z1 shows
//                        every 1087 player rank 6 with 31,662 XP and 52,818 Scrap. It sends no MOTD
//                        at all (`C:\Z1\Server\Zone` has no *Motd* file and no 0x32 sender).
//
// Cranberry therefore sends the client own fresh-account numbers - XP 0, rank 1, and zero for each
// of the three currencies the top bar draws - which is also what MENU-RETAIL-GAP U-3 says retail
// shows (0 / 0 / 1 / 1). The difference from doing nothing is that the datasource rows now EXIST,
// so the tooltips and `handlePlayerCurrencyUpdate` have something to read.
//
// U-4. The MOTD is server-driven after all: base 0x32, two counted strings (title, message), the
// title being the row key and an empty title meaning "use the client own heading". See
// `MessageOfTheDay` in `MenuPackets.cs` for the parser and the consumer. The friend server never
// sends it (0 occurrences in the whole capture), which is why the panel is empty in his sessions,
// and `motd_uri` in `CommandQueue.log` is only where the panel BILLBOARD IMAGES come from.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// docs/105 §10 - the menu top bar (<c>Experience.SetExperience</c> +
/// <c>Currency.SetAccountCurrencyRecord</c>) and the MOTD panel, sent once at the menu
/// <c>ClientIsReady</c>. Both default ON and each is separately revertible.
/// </summary>
public sealed record MenuTopBarOptions
{
    /// <summary><c>CRANBERRY_MENU_TOPBAR</c>.</summary>
    public const string TopBarVariable = "CRANBERRY_MENU_TOPBAR";

    /// <summary><c>CRANBERRY_MENU_TOPBAR_XP</c> - the experience total, default 0.</summary>
    public const string ExperienceVariable = "CRANBERRY_MENU_TOPBAR_XP";

    /// <summary><c>CRANBERRY_MENU_TOPBAR_RANK</c> - the rank / level, default 1.</summary>
    public const string RankVariable = "CRANBERRY_MENU_TOPBAR_RANK";

    /// <summary><c>CRANBERRY_MENU_TOPBAR_SCRAP</c> - the Scrap balance, default 0.</summary>
    public const string ScrapVariable = "CRANBERRY_MENU_TOPBAR_SCRAP";

    /// <summary>
    /// <c>CRANBERRY_MENU_TOPBAR_CROWNS</c> - the Crowns balance, default 0. The bounty lane's
    /// "hard" ante spends this, so the owner needs a way to give himself one without a store.
    /// </summary>
    public const string CrownsVariable = "CRANBERRY_MENU_TOPBAR_CROWNS";

    /// <summary><c>CRANBERRY_MENU_TOPBAR_SKULLS</c> - the Skulls balance, default 0.</summary>
    public const string SkullsVariable = "CRANBERRY_MENU_TOPBAR_SKULLS";

    /// <summary><c>CRANBERRY_MENU_TOPBAR_CREDITS</c> - the Credits balance, default 0.</summary>
    public const string CreditsVariable = "CRANBERRY_MENU_TOPBAR_CREDITS";

    /// <summary><c>CRANBERRY_MENU_MOTD</c>.</summary>
    public const string MotdVariable = "CRANBERRY_MENU_MOTD";

    /// <summary><c>CRANBERRY_MENU_MOTD_TEXT</c>.</summary>
    public const string MotdTextVariable = "CRANBERRY_MENU_MOTD_TEXT";

    /// <summary>
    /// The default MOTD body: this project name, what it is, and the client build it is written
    /// against.
    /// </summary>
    public const string DefaultMotdText =
        "Cranberry - clean-room August 2017 server (client build 0.0.118.208059)";

    /// <summary>Send the experience and currency records at the menu <c>ClientIsReady</c>.</summary>
    public bool SendTopBar { get; init; } = true;

    /// <summary>One-time account XP seed. Saved progress takes precedence after initialization.</summary>
    public uint Experience { get; init; }

    /// <summary>Minimum initial account level; the XP seed is raised to its threshold. Defaults to 1.</summary>
    public uint Rank { get; init; } = 1u;

    /// <summary><c>Currency.txt</c> id 1. A fresh account has none.</summary>
    public uint Scrap { get; init; }

    /// <summary><c>Currency.txt</c> id 4. A fresh account has none.</summary>
    public uint Crowns { get; init; }

    /// <summary><c>Currency.txt</c> id 5. A fresh account has none.</summary>
    public uint Skulls { get; init; }

    /// <summary>
    /// <c>Currency.txt</c> id 6. A fresh account has none.
    /// <para>
    /// <b>D255 - the starting balances stay ZERO.</b> The client's own fresh state is zero on every
    /// currency and nothing in the tree says otherwise, so the shipped default does not invent an
    /// account. <c>CRANBERRY_MENU_TOPBAR_CROWNS</c> / <c>_SKULLS</c> / <c>_CREDITS</c> exist so the
    /// owner can hand himself a balance for one run and actually press an ante button.
    /// </para>
    /// </summary>
    public uint Credits { get; init; }

    /// <summary>Send the <c>0x32</c> MOTD packet at the menu <c>ClientIsReady</c>.</summary>
    public bool SendMotd { get; init; } = true;

    /// <summary>
    /// The MOTD body. Empty removes the row instead of adding one (<c>FUN_141072ce0</c>), so the
    /// text is what decides whether a panel appears at all.
    /// </summary>
    public string MotdText { get; init; } = DefaultMotdText;

    /// <summary>
    /// The MOTD row key. Left empty on purpose: an empty title makes the client substitute its own
    /// string-table entry <c>MessageOfTheDay</c> (or the literal "Message of the Day"), which is
    /// the heading the widget is authored around.
    /// </summary>
    public string MotdTitle { get; init; } = string.Empty;

    public static MenuTopBarOptions Default { get; } = new();

    /// <summary>
    /// The four currency rows the top bar draws, in the order they go out. <b>Credits (id 6) was
    /// added by the bounty lane</b>: it is one of the three antes the Bounty screen offers
    /// (locale <c>1967504565</c>) and Cranberry had never sent it anywhere (AUDIT-bounty G3).
    /// </summary>
    public IEnumerable<SetAccountCurrencyRecord> CurrencyRecords()
    {
        yield return new SetAccountCurrencyRecord(SetAccountCurrencyRecord.Scrap, Scrap);
        yield return new SetAccountCurrencyRecord(SetAccountCurrencyRecord.Crowns, Crowns);
        yield return new SetAccountCurrencyRecord(SetAccountCurrencyRecord.Skulls, Skulls);
        yield return new SetAccountCurrencyRecord(SetAccountCurrencyRecord.Credits, Credits);
    }

    /// <summary>The experience packet these options describe.</summary>
    public SetExperience ExperiencePacket() =>
        new() { Experience = Experience, Rank = Rank };

    /// <summary>The MOTD packet these options describe.</summary>
    public MessageOfTheDay MotdPacket() => new(MotdText, MotdTitle);

    /// <summary>
    /// <c>CRANBERRY_MENU_TOPBAR=0</c> and <c>CRANBERRY_MENU_MOTD=0</c> each silence one half;
    /// <c>CRANBERRY_MENU_MOTD_TEXT</c> replaces the body text and
    /// <c>CRANBERRY_MENU_TOPBAR_XP</c> / <c>_RANK</c> / <c>_SCRAP</c> the three numbers.
    /// Unrecognised values keep the default rather than failing a boot.
    /// </summary>
    /// <param name="read">
    /// Usually <c>Environment.GetEnvironmentVariable</c>; the host passes
    /// <c>CranberryConfig.Read</c> so <c>cranberry.json</c> can set these too (lane 0D).
    /// </param>
    public static MenuTopBarOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= System.Environment.GetEnvironmentVariable;
        string? text = read(MotdTextVariable);
        return Default with
        {
            SendTopBar = Flag(read, TopBarVariable, @default: true),
            Experience = Number(read, ExperienceVariable, Default.Experience),
            Rank = Number(read, RankVariable, Default.Rank),
            Scrap = Number(read, ScrapVariable, Default.Scrap),
            Crowns = Number(read, CrownsVariable, Default.Crowns),
            Skulls = Number(read, SkullsVariable, Default.Skulls),
            Credits = Number(read, CreditsVariable, Default.Credits),
            SendMotd = Flag(read, MotdVariable, @default: true),
            MotdText = string.IsNullOrEmpty(text) ? DefaultMotdText : text,
        };
    }

    private static bool Flag(Func<string, string?> read, string variable, bool @default)
    {
        string? raw = read(variable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return @default;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "0" or "off" or "no" or "false" => false,
            "1" or "on" or "yes" or "true" => true,
            _ => @default,
        };
    }

    private static uint Number(Func<string, string?> read, string variable, uint @default)
    {
        string? raw = read(variable);
        return uint.TryParse(raw?.Trim(), out uint value) ? value : @default;
    }

    public string Describe() =>
        "menu top bar: "
            + (SendTopBar
                ? $"ON (SetExperience xp {Experience} rank {Rank}, "
                    + $"SetAccountCurrencyRecord scrap {Scrap} / crowns {Crowns} / skulls {Skulls} "
                    + $"/ credits {Credits}; "
                    + "CRANBERRY_MENU_TOPBAR=0 reverts)"
                : "off (CRANBERRY_MENU_TOPBAR=0)")
            + ", MOTD "
            + (SendMotd && MotdText.Length > 0
                ? $"ON (\"{MotdText}\"; CRANBERRY_MENU_MOTD=0 reverts, "
                    + "CRANBERRY_MENU_MOTD_TEXT replaces the text)"
                : "off (CRANBERRY_MENU_MOTD=0)");
}
