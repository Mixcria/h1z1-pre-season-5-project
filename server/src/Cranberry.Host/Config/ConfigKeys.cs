using System.Globalization;
using Cranberry.Zone.Descent;
using Cranberry.Zone.Match;

namespace Cranberry.Host.Config;

/// <summary>How a key's text is read out of <c>cranberry.json</c> and the environment.</summary>
public enum ConfigKind
{
    /// <summary>A JSON boolean; handed to the option records as <c>"1"</c> / <c>"0"</c>.</summary>
    Bool,

    /// <summary>A JSON integer.</summary>
    Int,

    /// <summary>A JSON number.</summary>
    Float,

    /// <summary>A JSON string: a path, a preset name, an enum word, a flag byte.</summary>
    Text,
}

/// <summary>
/// One configurable value: where it lives in <c>cranberry.json</c>, the <b>exact legacy
/// environment name</b> the owner has been typing all week, the ruling that decides its default,
/// and how to read back the value the server actually ran with.
/// <para>
/// This table is the single source of truth for four things: the reader that binds
/// <c>cranberry.json</c> to the existing option records, the one-line effective-config JSON at
/// boot, <c>cranberry.json.example</c>, and <c>docs/110-config.md</c>. Adding a switch means
/// adding a row here; nothing else has to be edited twice.
/// </para>
/// </summary>
/// <param name="Block">The <c>cranberry.json</c> object this key sits in.</param>
/// <param name="Key">The property name inside that object (camelCase).</param>
/// <param name="Legacy">
/// The environment variable that has always set this value. It keeps working, unchanged, at the
/// top of the precedence order — every one-word revert in the owner's launch scripts survives.
/// </param>
/// <param name="Kind">How the JSON value is turned into the text the option record parses.</param>
/// <param name="Ruling">The <c>docs/01</c> D-row that decides the default, or <c>"-"</c>.</param>
/// <param name="Comment">One line for <c>docs/110-config.md</c>.</param>
/// <param name="Effective">Reads the value the server actually ran with, off the bound records.</param>
/// <param name="Flip">
/// A value that must visibly move <see cref="Effective"/> and nothing else, for the per-option
/// wire test. Null on a boolean means "the opposite of the default".
/// </param>
/// <param name="Couples">
/// The <c>block.key</c> paths this key is ALLOWED to move as well — a preset moves its own block's
/// knobs by definition, and every knob in a preset block moves that block's reported preset NAME to
/// "custom". Everything outside this list must be untouched.
/// </param>
public sealed record ConfigKey(
    string Block,
    string Key,
    string Legacy,
    ConfigKind Kind,
    string Ruling,
    string Comment,
    Func<CranberryConfig, object?> Effective,
    string? Flip = null,
    IReadOnlyList<string>? Couples = null)
{
    /// <summary><c>block.key</c> — the identity the file reader and the tests use.</summary>
    public string Path => Block + "." + Key;

    /// <summary>
    /// The generated overlay name <c>CRANBERRY_&lt;BLOCK&gt;_&lt;KEY&gt;</c>. Often identical to
    /// <see cref="Legacy"/> (most legacy names were already in this shape); where it is not, BOTH
    /// work and <see cref="Legacy"/> wins.
    /// </summary>
    public string Canonical =>
        "CRANBERRY_" + Block.ToUpperInvariant() + "_" + ScreamingSnake(Key);

    private static string ScreamingSnake(string camel)
    {
        var text = new System.Text.StringBuilder(camel.Length + 8);
        foreach (char character in camel)
        {
            if (char.IsUpper(character) && text.Length > 0)
            {
                text.Append('_');
            }

            text.Append(char.ToUpperInvariant(character));
        }

        return text.ToString();
    }
}

/// <summary>The lane-0D table: every surviving switch, in <c>cranberry.json</c> block order.</summary>
public static class ConfigKeys
{
    /// <summary>Every key, in the order <c>cranberry.json.example</c> and the docs list them.</summary>
    public static IReadOnlyList<ConfigKey> All { get; } = Build();

    /// <summary>One line per block, for <c>cranberry.json.example</c>'s <c>_comment</c> keys.</summary>
    public static IReadOnlyDictionary<string, string> BlockComments { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["root"] = "where the host reads data and writes logs; positional arguments 1-9 still win over both file and environment",
            ["queue"] = "public rolling rosters and measured node admission limits; hosted games retain their explicit lifecycle",
            ["ports"] = "the two UDP listeners: login, then the gateway the login reply advertises",
            ["sky"] = "D30: the sky, the frozen clock and the lighting table move together as one named preset",
            ["gas"] = "D23: every number here is Cranberry's own design, so it is retuned by restart and never by rebuild",
            ["movement"] = "D54: the owner's own six speeds and eight blend times, retunable without a rebuild",
            ["descent"] = "D238/D239: the parachute ride - the release is the client's own 850 m KotK.SkySpawn slab, and the landing guard never dismounts a live chute in the air",
            ["drop"] = "D240/D241: where the match drops and what the canopy is dressed in",
            ["loot"] = "D42/D69 with D270-D275: the ground-loot working set that follows the player, the proximity panel, the floor's density, the laminated-armour rules and the airdrop channel",
            ["vehicles"] = "D35/D50: the car park that follows the player, and the three driving arms",
            ["doors"] = "D34/D45 with D243-D247 and D264: door spawn shape, the collision levers, the press guard, the lobby and hospital doors, match-scope door state, the streaming band and the retail swing sound",
            ["skins"] = "D83-D90 with D190/D203/D211: what a dressed character carries, and where the wardrobe is kept",
            ["weapons"] = "D129/D142/D143: the staged WeaponDefinitions blob and the two multipliers that open the hand",
            ["combat"] = "D92-D95: whether the 0x82 family is answered at all, and what a hit does",
            ["ammo"] = "D165-D168: where a magazine comes from, and whether 11 03 ItemUpdate goes out",
            ["crafting"] = "D48 with D256-D258: the recipe list, whether a craft request is accepted, which recipe set is shipped, and the cast bar that opens and closes it",
            ["match"] = "D39 with D151-D155: the replayable match seed and the death-and-victory arms",
            ["lobby"] = "D248-D251: the pre-game lobby at Fort Destiny - its length, when it arms, its labels and its banners",
            ["bounty"] = "D252-D255: Backing your Match - the balances, the payout tables, the ante and the drop-open suppression",
            ["menu"] = "D193/D201/D202/D274/D275: the server-owned lobby camera table, the actor's pose, the top bar",
            ["console"] = "D174-D178: the August client's own debug console",
            ["transport"] = "the landing burst's send window; 0 restores the single synchronous continuation",
            ["metrics"] = "opt-in bounded production JSONL; listener work and queue timing, not a simulation tick or client-display measurement",
            ["features"] = "the whole-subsystem rollbacks of docs/32 - one variable each, because a new packet on a proven path is bisected by turning it off",
            ["dev"] = "development aids and the two open wield experiments; none of these is retail behaviour",
            ["login"] = "D200: how ServerInfo names a region",
            ["peers"] = "reserved for lane 3C's two-client switches; any name it adds keeps working through the environment even before it has a row here",
        };

    /// <summary>Every key by its legacy environment name.</summary>
    public static IReadOnlyDictionary<string, ConfigKey> ByLegacy { get; } =
        All.ToDictionary(key => key.Legacy, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every key by <c>block.key</c>.</summary>
    public static IReadOnlyDictionary<string, ConfigKey> ByPath { get; } =
        All.ToDictionary(key => key.Path, StringComparer.OrdinalIgnoreCase);

    /// <summary>The block names, in file order.</summary>
    public static IReadOnlyList<string> Blocks { get; } =
        All.Select(key => key.Block).Distinct(StringComparer.Ordinal).ToArray();

    private static List<ConfigKey> Build()
    {
        var keys = new List<ConfigKey>();

        void Add(
            string block,
            string key,
            string legacy,
            ConfigKind kind,
            string ruling,
            string comment,
            Func<CranberryConfig, object?> effective,
            string? flip = null,
            IReadOnlyList<string>? couples = null) =>
            keys.Add(new ConfigKey(block, key, legacy, kind, ruling, comment, effective, flip, couples));

        // root ---------------------------------------------------------------------------------
        Add("root", "path", "CRANBERRY_ROOT", ConfigKind.Text, "D150",
            "data, logs, captures and state live under here",
            c => c.Root.Path, @"C:\Aug2017Alt");
        Add("root", "zone", "CRANBERRY_ZONE", ConfigKind.Text, "D24",
            "the zone name the bootstrap sends the client into first",
            c => c.Root.Zone, "Z2");
        Add("root", "seedCharacter", "CRANBERRY_SEED_CHARACTER", ConfigKind.Text, "-",
            "development seed name, used only when the roster is empty",
            c => c.Root.SeedCharacterName ?? string.Empty, "flipseed");
        Add("root", "dynamicAppearanceSource", "CRANBERRY_DYNAMIC_APPEARANCE_SOURCE", ConfigKind.Text, "D22",
            "the checksummed DynamicAppearance blob (D22's declared compatibility input)",
            c => c.Root.DynamicAppearanceSource, @"C:\Aug2017\state\appearance.bin");

        // ports --------------------------------------------------------------------------------
        Add("ports", "login", "CRANBERRY_PORT_LOGIN", ConfigKind.Int, "-",
            "the login UDP listener", c => c.Ports.Login, "20142");
        Add("ports", "gateway", "CRANBERRY_PORT_GATEWAY", ConfigKind.Int, "-",
            "the gateway UDP listener, advertised in the login reply", c => c.Ports.Gateway, "20143");

        // metrics ------------------------------------------------------------------------------
        Add("metrics", "enabled", "CRANBERRY_METRICS", ConfigKind.Bool, "-",
            "enable bounded local production metrics; default off", c => c.Metrics.Enabled);
        Add("metrics", "intervalMs", "CRANBERRY_METRICS_INTERVAL_MS", ConfigKind.Int, "-",
            "snapshot interval, clamped to 1000..60000 ms", c => c.Metrics.IntervalMs, "2000");
        Add("metrics", "maxSessions", "CRANBERRY_METRICS_MAX_SESSIONS", ConfigKind.Int, "-",
            "maximum anonymous session detail rows per source, 0..64", c => c.Metrics.MaxSessions, "8");
        Add("metrics", "maxFileMiB", "CRANBERRY_METRICS_MAX_FILE_MIB", ConfigKind.Int, "-",
            "maximum size per capture file, 1..64 MiB", c => c.Metrics.MaxFileMiB, "8");
        Add("metrics", "maxFiles", "CRANBERRY_METRICS_MAX_FILES", ConfigKind.Int, "-",
            "maximum retained files per run, 1..16; stop at limit unless rolling is enabled", c => c.Metrics.MaxFiles, "2");
        Add("metrics", "rolling", "CRANBERRY_METRICS_ROLLING", ConfigKind.Bool, "-",
            "continue capture by deleting only this run's oldest file at the retention limit; default off", c => c.Metrics.Rolling);
        Add("metrics", "nodeId", "CRANBERRY_METRICS_NODE_ID", ConfigKind.Text, "-",
            "non-secret node label, up to 64 ASCII letters/digits/dot/dash/underscore", c => c.Metrics.NodeId, "eu-test-1");

        Add("queue", "waitMs", "CRANBERRY_QUEUE_WAIT_MS", ConfigKind.Int, "-",
            "pregame countdown after the ready-player minimum; 0..900000 ms", c => c.PublicQueue.WaitMs, "10000");
        Add("queue", "loadTimeoutMs", "CRANBERRY_QUEUE_LOAD_TIMEOUT_MS", ConfigKind.Int, "-",
            "release failed loading reservations after 30000..600000 ms; ready players retain their match", c => c.PublicQueue.LoadTimeoutMs, "120000");
        Add("queue", "maxPlayers", "CRANBERRY_QUEUE_MAX_PLAYERS", ConfigKind.Int, "-",
            "whole-party roster capacity, 6..150", c => c.PublicQueue.MaxPlayers, "100");
        Add("queue", "minPlayers", "CRANBERRY_QUEUE_MIN_PLAYERS", ConfigKind.Int, "-",
            "ready pregame minimum, also requires more than one full team", c => c.PublicQueue.MinPlayers, "10");
        Add("queue", "maxMatches", "CRANBERRY_QUEUE_MAX_MATCHES", ConfigKind.Int, "-",
            "node-wide allocated public match limit; raise only after measured acceptance", c => c.PublicQueue.MaxAllocatedMatches, "3");
        Add("queue", "acceptTimeoutMs", "CRANBERRY_QUEUE_ACCEPT_TIMEOUT_MS", ConfigKind.Int, "-",
            "frozen reservation accept timeout, 10000..180000 ms", c => c.PublicQueue.AcceptTimeoutMs, "30000");

        // sky ----------------------------------------------------------------------------------
        Add("sky", "preset", "CRANBERRY_SKY", ConfigKind.Text, "D30",
            "sky, frozen clock and lighting table as one named preset",
            c => c.Sky.Settings.Name, "Aug2017Clear");

        // gas ----------------------------------------------------------------------------------
        Add("gas", "enabled", "CRANBERRY_GAS", ConfigKind.Bool, "D25",
            "the whole safe-zone runtime; off is the bring-up rollback", c => c.Gas.Enabled);
        Add("gas", "preset", "CRANBERRY_GAS_PRESET", ConfigKind.Text, "D23",
            "the named ladder; Sprint plays the same schedule at one fifth of the clock",
            c => GasName(c), "Sprint");
        Add("gas", "pacing", "CRANBERRY_GAS_PACING", ConfigKind.Text, "D43",
            "PhaseTable (per-phase hold and movement durations), SpeedPaced (fixed radius rate), or FixedWindows (legacy timed windows)",
            c => c.Gas.Settings.Pacing.ToString(), "FixedWindows");
        Add("gas", "preMoveRing", "CRANBERRY_GAS_PRE_MOVE_RING", ConfigKind.Text, "D65",
            "what is drawn before phase 1 first moves; None is D65",
            c => c.Gas.Settings.PreMoveRing.ToString(), "Boundary");
        Add("gas", "scale", "CRANBERRY_GAS_SCALE", ConfigKind.Float, "D23",
            "multiplies every time in the ladder, leaving the radii and the damage alone",
            c => c.GasScale, "0.5");
        Add("gas", "damageScale", "CRANBERRY_GAS_DAMAGE_SCALE", ConfigKind.Float, "D23",
            "multiplies every entry of the per-phase damage table",
            c => c.GasDamageScale, "0.5");
        Add("gas", "phases", "CRANBERRY_GAS_PHASES", ConfigKind.Float, "D43",
            "how many waves the ladder runs; changing the table's count selects SpeedPaced with geometric radii unless PhaseTable was explicitly requested", c => c.Gas.Settings.PhaseCount, "8");
        Add("gas", "firstRevealMs", "CRANBERRY_GAS_FIRST_REVEAL_MS", ConfigKind.Float, "D43",
            "when the first circle is drawn; table pacing adjusts its first hold to preserve the first movement time", c => c.Gas.Settings.FirstRevealDelayMs, "90000");
        Add("gas", "firstMoveMs", "CRANBERRY_GAS_FIRST_MOVE_MS", ConfigKind.Float, "D43",
            "when the wall first moves; table pacing adjusts its first hold", c => c.Gas.Settings.FirstMoveDelayMs, "260000");
        Add("gas", "holdMs", "CRANBERRY_GAS_HOLD_MS", ConfigKind.Float, "D43",
            "the pause before later waves; changing this scalar replaces later hold-table entries, while repeating its default preserves the table", c => c.Gas.Settings.InterPhaseHoldMs, "20000");
        Add("gas", "wallSpeed", "CRANBERRY_GAS_WALL_SPEED", ConfigKind.Float, "D63",
            "radius decrease in m/s; selects SpeedPaced unless PhaseTable was explicitly requested, and remains limited by maxEdge",
            c => c.Gas.Settings.ShrinkSpeedMetresPerSecond, "3.5");
        Add("gas", "finalRadiusM", "CRANBERRY_GAS_FINAL_RADIUS_M", ConfigKind.Float, "D63",
            "the last circle's radius", c => c.Gas.Settings.FinalRadius, "50");
        Add("gas", "initialRadiusM", "CRANBERRY_GAS_INITIAL_RADIUS_M", ConfigKind.Float, "D63",
            "the play area's radius at t=0", c => c.Gas.Settings.InitialRadius, "5000");
        Add("gas", "centreX", "CRANBERRY_GAS_CENTRE_X", ConfigKind.Float, "D63",
            "the play area's centre, x", c => c.Gas.Settings.PlayAreaCentre.X, "-200");
        Add("gas", "centreZ", "CRANBERRY_GAS_CENTRE_Z", ConfigKind.Float, "D63",
            "the play area's centre, z", c => c.Gas.Settings.PlayAreaCentre.Z, "50");
        Add("gas", "drift", "CRANBERRY_GAS_DRIFT", ConfigKind.Float, "D122",
            "how far a new circle's centre may walk (D62 caps it against maxEdge)",
            c => c.Gas.Settings.CentreDriftFraction, "0.15");
        Add("gas", "driftCone", "CRANBERRY_GAS_DRIFT_CONE", ConfigKind.Float, "D122",
            "the heading cone the walk is drawn in", c => c.Gas.Settings.DriftConeDegrees, "200");
        Add("gas", "maxEdge", "CRANBERRY_GAS_MAX_EDGE", ConfigKind.Float, "D276",
            "the cap on the drawn ring's leading edge, including centre movement",
            c => c.Gas.Settings.MaxEdgeSpeedMetresPerSecond, "45");
        Add("gas", "radiusRoundM", "CRANBERRY_GAS_RADIUS_ROUND_M", ConfigKind.Float, "D63",
            "rounds every radius to a whole number of metres", c => c.Gas.Settings.RadiusRoundingMetres, "10");
        Add("gas", "radiusLadder", "CRANBERRY_GAS_RADIUS_LADDER", ConfigKind.Float, "D285",
            "0 selects geometric radii and SpeedPaced unless PhaseTable was explicitly requested; 1 retains the preset mode; inherited tables rescale with radius bounds",
            c => c.Gas.Settings.RadiusLadder.Count > 0 ? 1 : 0, "0");
        Add("gas", "tickMs", "CRANBERRY_GAS_TICK_MS", ConfigKind.Float, "D23",
            "the damage tick", c => c.Gas.Settings.TickPeriodMs, "2000");
        Add("gas", "updateMs", "CRANBERRY_GAS_UPDATE_MS", ConfigKind.Float, "D123",
            "how often a moving circle is re-stated", c => c.Gas.Settings.SafeZoneUpdateIntervalMs, "700");
        Add("gas", "blendMs", "CRANBERRY_GAS_BLEND_MS", ConfigKind.Float, "D23",
            "the client-side ring blend", c => c.Gas.Settings.RingBlendMs, "3000");
        Add("gas", "blendMode", "CRANBERRY_GAS_BLEND_MODE", ConfigKind.Text, "D280",
            "SendPeriod writes ce 01's blend as the interval to the next ce 01; Fixed is the flat blendMs",
            c => c.Gas.Settings.RingBlendMode.ToString(), "Fixed");
        Add("gas", "centrePlan", "CRANBERRY_GAS_CENTRE_PLAN", ConfigKind.Text, "D277",
            "PoiDestination aims the match at one of the client's nine GasWeightArea volumes; Drift is D62's cone walk",
            c => c.Gas.Settings.CentrePlan.ToString(), "Drift");
        Add("gas", "poiExponent", "CRANBERRY_GAS_POI_EXPONENT", ConfigKind.Float, "D277",
            "how hard the nine volumes are weighted by their own footprint; 0 makes them equally likely",
            c => c.Gas.Settings.PoiWeightExponent, "0");
        Add("gas", "playAreaLeadM", "CRANBERRY_GAS_PLAY_AREA_LEAD_M", ConfigKind.Float, "D278",
            "how far the play area may lead toward a destination containment cannot otherwise reach; 0 pins it",
            c => c.Gas.Settings.PlayAreaLeadMetres, "0");
        Add("gas", "toxicity", "CRANBERRY_GAS_TOXICITY", ConfigKind.Float, "D279",
            "the client's own resource-611 toxicity meter, filled in the gas and drained outside it",
            c => c.Gas.Settings.SendToxicity ? 1 : 0, "0");
        Add("gas", "toxicityDrain", "CRANBERRY_GAS_TOXICITY_DRAIN", ConfigKind.Float, "D279",
            "how fast the toxicity meter drains outside the gas, units per second",
            c => c.Gas.Settings.ToxicityDrainPerSecond, "500");
        Add("gas", "hudHealMs", "CRANBERRY_GAS_HUD_HEAL_MS", ConfigKind.Float, "D120",
            "the 1 Hz ce 0f countdown heal", c => c.Gas.Settings.HudHealIntervalMs, "2000");
        Add("gas", "safezoneHealMs", "CRANBERRY_GAS_SAFEZONE_HEAL_MS", ConfigKind.Float, "D123",
            "how often ce 02 is re-sent while a circle is revealed",
            c => c.Gas.Settings.SafeZoneHealIntervalMs, "20000");
        Add("gas", "banners", "CRANBERRY_GAS_BANNERS", ConfigKind.Float, "D64",
            "the three HUD banner labels", c => c.Gas.Settings.SendBanners ? 1 : 0, "0");

        // movement -----------------------------------------------------------------------------
        Add("movement", "preset", "CRANBERRY_MOVE_PRESET", ConfigKind.Text, "D54",
            "the named speed profile", c => MovementName(c), "Wave3Legacy");
        Add("movement", "base", "CRANBERRY_MOVE_BASE", ConfigKind.Float, "D54",
            "base movement speed, m/s", c => c.Movement.Profile.MaxMovementSpeed, "5");
        Add("movement", "sprint", "CRANBERRY_MOVE_SPRINT", ConfigKind.Float, "D54",
            "sprint modifier", c => c.Movement.Profile.SprintSpeedModifier, "1.5");
        Add("movement", "walk", "CRANBERRY_MOVE_WALK", ConfigKind.Float, "D54",
            "walk modifier", c => c.Movement.Profile.WalkSpeedModifier, "0.4");
        Add("movement", "crouch", "CRANBERRY_MOVE_CROUCH", ConfigKind.Float, "D54",
            "crouch modifier", c => c.Movement.Profile.CrouchSpeedModifier, "0.4");
        Add("movement", "back", "CRANBERRY_MOVE_BACK", ConfigKind.Float, "D59",
            "backpedal modifier", c => c.Movement.Profile.BackpedalSpeedModifier, "0.6");
        Add("movement", "strafe", "CRANBERRY_MOVE_STRAFE", ConfigKind.Float, "D54",
            "strafe modifier", c => c.Movement.Profile.StrafeSpeedModifier, "0.8");
        Add("movement", "swim", "CRANBERRY_MOVE_SWIM", ConfigKind.Float, "D56",
            "swim modifier (Z1's 0 is refused)", c => c.Movement.Profile.SwimSpeedModifier, "0.6");
        Add("movement", "water", "CRANBERRY_MOVE_WATER", ConfigKind.Float, "D54",
            "wading modifier", c => c.Movement.Profile.WaterSpeedModifier, "0.7");
        Add("movement", "sprintAccel", "CRANBERRY_MOVE_SPRINT_ACCEL", ConfigKind.Float, "D55",
            "sprint acceleration time", c => c.Movement.Profile.SprintAccelerationTime, "0.7");
        Add("movement", "sprintDecel", "CRANBERRY_MOVE_SPRINT_DECEL", ConfigKind.Float, "D55",
            "sprint deceleration time", c => c.Movement.Profile.SprintDecelerationTime, "0.7");
        Add("movement", "fwdAccel", "CRANBERRY_MOVE_FWD_ACCEL", ConfigKind.Float, "D55",
            "forward acceleration time", c => c.Movement.Profile.ForwardAccelerationTime, "0.65");
        Add("movement", "backAccel", "CRANBERRY_MOVE_BACK_ACCEL", ConfigKind.Float, "D55",
            "back acceleration time", c => c.Movement.Profile.BackAccelerationTime, "0.65");
        Add("movement", "strafeAccel", "CRANBERRY_MOVE_STRAFE_ACCEL", ConfigKind.Float, "D55",
            "strafe acceleration time", c => c.Movement.Profile.StrafeAccelerationTime, "0.65");

        // descent ------------------------------------------------------------------------------
        Add("descent", "preset", "CRANBERRY_DESCENT_PRESET", ConfigKind.Text, "D125",
            "the named descent profile", c => DescentName(c), "Owner30");
        Add("descent", "seconds", "CRANBERRY_DESCENT_SECONDS", ConfigKind.Float, "D125",
            "seconds under canopy; the sky-spawn altitude follows from it",
            c => c.Descent.Settings.TargetDescentSeconds, "20");
        Add("descent", "rate", "CRANBERRY_DESCENT_RATE", ConfigKind.Float, "D239",
            "the PLANNING mean, m/s - the mean of dived rides, not a client constant (the real rate is a 10-56 m/s player-controlled band)",
            c => c.Descent.Settings.PlannedDescentMetresPerSecond, "35");
        Add("descent", "landingGuard", DescentTuning.LandingGuardVariable, ConfigKind.Bool, "D239",
            "never force-dismount a live chute in the air; 0 restores the pre-D239 deadline that did",
            c => c.Descent.LandingGuard);

        // drop ------------------------------------------------------------------------------------
        Add("drop", "jitterFloor", DropTuning.JitterFloorVariable, ConfigKind.Float, "D240",
            "a jittered marker must keep this fraction of its place's own anchor density; 0 is the pre-D240 absolute-only floor",
            c => c.Drop.Options.JitterFloorFraction, "0");
        Add("drop", "parachuteSkin", DropTuning.ParachuteSkinVariable, ConfigKind.Text, "D241",
            "green / blue / tan (items 4055/4056/4057) dresses the canopy; unset is the default parachute, and the carrier field is still unproven",
            c => c.Drop.ParachuteSkinItemId, "green");

        // loot ---------------------------------------------------------------------------------
        Add("loot", "groundRadiusM", "CRANBERRY_GROUND_LOOT_RADIUS_M", ConfigKind.Float, "D39",
            "metres around the landing in which the map's own spawn markers are rolled",
            c => c.Loot.GroundLootRadiusMetres, "32");
        Add("loot", "stream", "CRANBERRY_LOOT_STREAM", ConfigKind.Bool, "D42",
            "the working set that follows the player; off is wave 4's single landing burst",
            c => c.Loot.Stream.Enabled);
        Add("loot", "streamMs", "CRANBERRY_LOOT_STREAM_MS", ConfigKind.Int, "D42",
            "how often the streamer re-plans", c => c.Loot.Stream.RestreamIntervalMs, "2000");
        Add("loot", "streamRadius", "CRANBERRY_LOOT_STREAM_RADIUS", ConfigKind.Float, "D69",
            "the streamed disc", c => c.Loot.Stream.StreamRadiusMetres, "80");
        Add("loot", "despawnRadius", "CRANBERRY_LOOT_DESPAWN_RADIUS", ConfigKind.Float, "D69",
            "where a streamed object is taken back", c => c.Loot.Stream.DespawnRadiusMetres, "120");
        Add("loot", "maxLive", "CRANBERRY_LOOT_MAX_LIVE", ConfigKind.Int, "D69",
            "the live-object ceiling", c => c.Loot.Stream.MaxLive, "64");
        Add("loot", "maxPerRestream", "CRANBERRY_LOOT_MAX_PER_RESTREAM", ConfigKind.Int, "D69",
            "spawns per re-plan", c => c.Loot.Stream.MaxPerRestream, "32");
        Add("loot", "byteBudget", "CRANBERRY_LOOT_BYTE_BUDGET", ConfigKind.Int, "D73",
            "the owner's per-re-stream byte budget", c => c.Loot.Stream.RestreamByteBudget, "50000");
        Add("loot", "maxEvictions", "CRANBERRY_LOOT_MAX_EVICTIONS", ConfigKind.Int, "D73",
            "the owner's eviction ceiling", c => c.Loot.Stream.MaxEvictionsPerRestream, "8");
        Add("loot", "panelRadius", "CRANBERRY_LOOT_PANEL_RADIUS", ConfigKind.Float, "D70",
            "the f8 01 proximity panel's disc", c => c.Loot.Stream.PanelRadiusMetres, "3");
        Add("loot", "panelRows", "CRANBERRY_LOOT_PANEL_ROWS", ConfigKind.Int, "D70",
            "the panel's row cap", c => c.Loot.Stream.PanelMaxRows, "16");
        Add("loot", "pickupReach", "CRANBERRY_LOOT_PICKUP_REACH", ConfigKind.Float, "D73",
            "how far a pickup may reach", c => c.Loot.Stream.PickupReachMetres, "5");
        Add("loot", "spawnChance", "CRANBERRY_LOOT_SPAWN_CHANCE", ConfigKind.Float, "D270",
            "one flat gate for every family, overriding the file's five; 0 = use the file",
            c => c.Loot.Density.SpawnChanceOverride ?? 0.0, "0.1223");
        Add("loot", "armourRules", "CRANBERRY_LOOT_ARMOUR_RULES", ConfigKind.Bool, "D275",
            "the 2017-06-29 laminated-armour rules: 5 % world chance, 250 m apart, 3 per map square",
            c => c.Loot.Density.LaminatedArmourRules);
        Add("loot", "armourChance", "CRANBERRY_LOOT_ARMOUR_CHANCE", ConfigKind.Float, "D275",
            "the vest's world spawn chance; retail cut it from 10 % to 5 %",
            c => c.Loot.Density.LaminatedArmourWorldChance, "0.1");
        Add("loot", "armourSpacing", "CRANBERRY_LOOT_ARMOUR_SPACING", ConfigKind.Float, "D275",
            "the vest's anti-cluster radius", c => c.Loot.Density.LaminatedArmourSpacingMetres, "500");
        Add("loot", "armourPerSquare", "CRANBERRY_LOOT_ARMOUR_PER_SQUARE", ConfigKind.Int, "D275",
            "most vests one map square may hold", c => c.Loot.Density.LaminatedArmourMaxPerSquare, "1");
        Add("loot", "airdrops", "CRANBERRY_AIRDROPS", ConfigKind.Bool, "D274",
            "retail's second loot channel; off is the pre-D274 world, with no route to the .308",
            c => c.Loot.Airdrop.Enabled);
        Add("loot", "airdropFirstMs", "CRANBERRY_AIRDROP_FIRST_MS", ConfigKind.Int, "D274",
            "match clock at the first crate", c => c.Loot.Airdrop.FirstDropAtMs, "60000");
        Add("loot", "airdropIntervalMs", "CRANBERRY_AIRDROP_INTERVAL_MS", ConfigKind.Int, "D274",
            "match clock between crates", c => c.Loot.Airdrop.DropIntervalMs, "120000");
        Add("loot", "airdropMax", "CRANBERRY_AIRDROP_MAX", ConfigKind.Int, "D274",
            "most crates one match delivers", c => c.Loot.Airdrop.MaxDropsPerMatch, "1");
        Add("loot", "airdropUnlockMs", "CRANBERRY_AIRDROP_UNLOCK_MS", ConfigKind.Int, "D274",
            "retail's ~8 s crate unlock", c => c.Loot.Airdrop.UnlockMs, "0");
        Add("loot", "airdropRifleChance", "CRANBERRY_AIRDROP_RIFLE_CHANCE", ConfigKind.Float, "D274",
            "retail's 50 % chance of the .308 Hunting Rifle", c => c.Loot.Airdrop.RifleChance, "1");
        Add("loot", "airdropPoolDraws", "CRANBERRY_AIRDROP_POOL_DRAWS", ConfigKind.Int, "D274",
            "weighted bundles drawn on top of the guaranteed set", c => c.Loot.Airdrop.PoolDraws, "5");

        // vehicles -----------------------------------------------------------------------------
        Add("vehicles", "spawnChance", "CRANBERRY_VEHICLE_SPAWN_CHANCE", ConfigKind.Float, "-",
            "ordinary vehicle pad chance (0-1); fractional chances keep 1-2 cars per police station; 0 disables, 1 fills every pad; applies to new matches",
            c => c.Vehicles.Plan.SpawnChance, "0.8");
        Add("vehicles", "stream", "CRANBERRY_VEHICLE_STREAM", ConfigKind.Bool, "D50",
            "the car park that follows the player; off is wave 5's single landing burst",
            c => c.Vehicles.Stream.Enabled);
        Add("vehicles", "streamMs", "CRANBERRY_VEHICLE_STREAM_MS", ConfigKind.Int, "D50",
            "how often the car park re-plans", c => c.Vehicles.Stream.RestreamIntervalMs, "2000");
        Add("vehicles", "streamRadius", "CRANBERRY_VEHICLE_STREAM_RADIUS", ConfigKind.Float, "D50",
            "the streamed disc", c => c.Vehicles.Stream.StreamRadiusMetres, "300");
        Add("vehicles", "despawnRadius", "CRANBERRY_VEHICLE_DESPAWN_RADIUS", ConfigKind.Float, "D50",
            "where a car is taken back", c => c.Vehicles.Stream.DespawnRadiusMetres, "600");
        Add("vehicles", "maxLive", "CRANBERRY_VEHICLE_MAX_LIVE", ConfigKind.Int, "D50",
            "the live-car ceiling", c => c.Vehicles.Stream.MaxLive, "20");
        Add("vehicles", "maxPerRestream", "CRANBERRY_VEHICLE_MAX_PER_RESTREAM", ConfigKind.Int, "D50",
            "spawns per re-plan", c => c.Vehicles.Stream.MaxPerRestream, "8");
        Add("vehicles", "fuel", "CRANBERRY_VEHICLE_FUEL", ConfigKind.Bool, "D35",
            "fuel BURN - and the boost meter, because the client's VehicleTurbo rows spend RESOURCE_TYPE 50",
            c => c.Vehicles.Fuel.Enabled);
        Add("vehicles", "fuelGauge", "CRANBERRY_VEHICLE_FUEL_GAUGE", ConfigKind.Bool, "D35",
            "the fuel resource row the client already parses", c => c.Vehicles.Fuel.SendGauge);
        Add("vehicles", "relay", "CRANBERRY_VEHICLE_RELAY", ConfigKind.Bool, "D50",
            "the 0x78 bystander relay", c => c.Vehicles.Relay.Enabled);
        Add("vehicles", "ignition", "CRANBERRY_VEHICLE_IGNITION", ConfigKind.Bool, "D51",
            "require a hotwire or a key; off because neither is in a loot table",
            c => c.Vehicles.Ignition.Required);
        Add("vehicles", "damage", "CRANBERRY_VEHICLE_DAMAGE", ConfigKind.Bool, "D270",
            "the whole vehicle damage model - crashes, bullets, flips, the condition ladder",
            c => c.Vehicles.Damage.Enabled);
        Add("vehicles", "bulletDamage", "CRANBERRY_VEHICLE_BULLET_DAMAGE", ConfigKind.Bool, "D270",
            "a 82 06 ProjectileHitReport whose target guid names a car damages it",
            c => c.Vehicles.Damage.Bullets);
        Add("vehicles", "flipDamage", "CRANBERRY_VEHICLE_FLIP_DAMAGE", ConfigKind.Bool, "D270",
            "an upside-down car takes its own UPSIDE_DOWN_DAMAGE_PULSE",
            c => c.Vehicles.Damage.Flip);
        Add("vehicles", "flipMs", "CRANBERRY_VEHICLE_FLIP_MS", ConfigKind.Int, "D270",
            "how often a car on its roof is pulsed; the amount is the client's, the period is ours",
            c => c.Vehicles.Damage.FlipPulseIntervalMs, "5000");
        Add("vehicles", "collisionCap", "CRANBERRY_VEHICLE_COLLISION_CAP", ConfigKind.Int, "-",
            "maximum condition damage per continuous impact; server tuning (1000 units = 1 vehicle HP)",
            c => (int)c.Vehicles.Damage.MaximumCollisionDamage, "10000");
        Add("vehicles", "collisionMinimum", "CRANBERRY_VEHICLE_COLLISION_MIN", ConfigKind.Int, "-",
            "ignore this much of each cumulative terrain impact", c => (int)c.Vehicles.Damage.MinimumCollisionDamage, "2000");
        Add("vehicles", "collisionMultiplier", "CRANBERRY_VEHICLE_COLLISION_MULTIPLIER", ConfigKind.Float, "-",
            "scale crash damage after removing small bumps", c => c.Vehicles.Damage.CollisionDamageMultiplier, "0.2");
        Add("vehicles", "flipMultiplier", "CRANBERRY_VEHICLE_FLIP_MULTIPLIER", ConfigKind.Float, "-",
            "scale sustained rollover damage", c => c.Vehicles.Damage.FlipDamageMultiplier, "0.2");
        Add("vehicles", "flipGraceMs", "CRANBERRY_VEHICLE_FLIP_GRACE_MS", ConfigKind.Int, "-",
            "time allowed to right a tumbling car before the first roof-damage pulse; server tuning",
            c => c.Vehicles.Damage.FlipInitialGraceMs, "9000");
        Add("vehicles", "wreckDamage", "CRANBERRY_VEHICLE_WRECK_DAMAGE", ConfigKind.Int, "D270",
            "optional minimum occupant damage, in addition to the zero-health blast (zero disables the extra minimum)",
            c => (int)c.Vehicles.Damage.WreckOccupantDamage, "2500");
        Add("vehicles", "explosions", "CRANBERRY_VEHICLE_EXPLOSIONS", ConfigKind.Bool, "-",
            "zero-health vehicle blasts damage players; owner request 2026-09-06", c => c.Vehicles.Damage.Explosions);
        Add("vehicles", "explosionDamage", "CRANBERRY_VEHICLE_EXPLOSION_DAMAGE", ConfigKind.Int, "-",
            "blast damage in player health units (100 units = 1 HP); server tuning",
            c => (int)c.Vehicles.Damage.ExplosionDamage, "5000");
        Add("vehicles", "explosionFullRadius", "CRANBERRY_VEHICLE_EXPLOSION_FULL_RADIUS", ConfigKind.Float, "-",
            "full-damage blast radius in metres; server tuning", c => c.Vehicles.Damage.ExplosionFullDamageRadius, "2");
        Add("vehicles", "explosionRadius", "CRANBERRY_VEHICLE_EXPLOSION_RADIUS", ConfigKind.Float, "-",
            "blast reaches zero damage at this radius in metres; server tuning", c => c.Vehicles.Damage.ExplosionRadius, "10");
        Add("vehicles", "boost", "CRANBERRY_VEHICLE_BOOST", ConfigKind.Bool, "D271",
            "answer a boost press: 9e 01/03, 0f 15/16, 0f 33 and the 88 2b refusal",
            c => c.Vehicles.Boost.Enabled);
        Add("vehicles", "turboByte", "CRANBERRY_VEHICLE_TURBO_BYTE", ConfigKind.Int, "D271",
            "the byte 0f 33 Character.Turbo carries on a grant; the polarity is inferred and one live press settles it",
            c => (int)c.Vehicles.Boost.TurboOnValue, "1");
        Add("vehicles", "positionBlock", "CRANBERRY_VEHICLE_POSITION_BLOCK", ConfigKind.Bool, "D290",
            "the parked car's at-rest position-update block in its 0xd7 tail; off restores the empty block that never drew",
            c => c.Vehicles.PositionBlock);
        Add("vehicles", "spawnFlags1", "CRANBERRY_VEHICLE_SPAWN_FLAGS1", ConfigKind.Text, "D290",
            "the +0x1b1 flag byte a parked car ships; 0x10 is the collidable bit, 0 restores the walk-through car",
            c => Hex(c.Vehicles.SpawnFlags1), "0x00");
        Add("vehicles", "shader", "CRANBERRY_VEHICLE_SHADER", ConfigKind.Bool, "D290",
            "the client's own shader group at +0x1a4 (838 for the OffRoader); off leaves the car white/rust",
            c => c.Vehicles.Shader);

        // doors --------------------------------------------------------------------------------
        Add("doors", "rotation", "CRANBERRY_DOOR_ROTATION", ConfigKind.Text, "D38",
            "the +0xa0 quaternion packing; the yaw SIGN is what is still open",
            c => c.Doors.Rotation.ToString(), "QuaternionYUpNegated");
        Add("doors", "collision", "CRANBERRY_DOOR_COLLISION", ConfigKind.Text, "D77",
            "which mesh a door spawn names - cosmetic, not collision (D77)",
            c => c.Doors.Collision.ToString(), "KinematicMesh");
        Add("doors", "restreamMs", "CRANBERRY_DOOR_RESTREAM_MS", ConfigKind.Int, "D42",
            "how often the door re-stream pump asks whether the player moved",
            c => c.Doors.RestreamIntervalMs, "1500");
        Add("doors", "pressMs", "CRANBERRY_DOOR_PRESS_MS", ConfigKind.Int, "D75",
            "how long an accepted toggle absorbs further requests for the same door",
            c => c.Doors.PressWindowMs, "400");
        Add("doors", "spawnFlags1", "CRANBERRY_DOOR_SPAWN_FLAGS1", ConfigKind.Text, "D76",
            "the +0x1b1 physics flag byte; accepts 0x-hex or plain decimal",
            c => Hex(c.Doors.SpawnFlags1), "0x30");
        Add("doors", "positionUpdateType", "CRANBERRY_DOOR_POSITION_UPDATE_TYPE", ConfigKind.Text, "D80",
            "the +0x11c positionUpdateType; non-zero RELEASES collision - a negative control",
            c => Hex(c.Doors.PositionUpdateType), "1");
        Add("doors", "setCollidable", "CRANBERRY_DOOR_SET_COLLIDABLE", ConfigKind.Bool, "D45",
            "restate collision on its own 0f 1e pair; experiment B of docs/85",
            c => c.Doors.SetCollidable);
        Add("doors", "lobby", "CRANBERRY_DOOR_LOBBY", ConfigKind.Bool, "D243",
            "arm a door burst in the pre-match lobby - the five doors the friend's own server "
                + "spawns there",
            c => c.Doors.Lobby);
        Add("doors", "hospital", "CRANBERRY_DOOR_HOSPITAL", ConfigKind.Bool, "D244",
            "spawn the 39 Hospital_*_Placer doors the client's own Models.txt marks as "
                + "placement markers",
            c => c.Doors.Hospital);
        Add("doors", "hospitalDouble", "CRANBERRY_DOOR_HOSPITAL_DOUBLE", ConfigKind.Bool, "D244",
            "include the 14 TWO-LEAF hospital doors, which the client swings about one pivot",
            c => c.Doors.HospitalDouble);
        Add("doors", "shared", "CRANBERRY_DOOR_SHARED", ConfigKind.Bool, "D245",
            "one guid space and one open bit per MATCH, so a door one player opens is open "
                + "for the other",
            c => c.Doors.Shared);
        Add("doors", "capNewOnly", "CRANBERRY_DOOR_CAP_NEW_ONLY", ConfigKind.Bool, "D246",
            "the burst cap counts the doors it SPAWNS, not the doors the disc holds",
            c => c.Doors.CapNewOnly);
        Add("doors", "despawnRadius", "CRANBERRY_DOOR_DESPAWN_RADIUS", ConfigKind.Float, "D246",
            "where a door is taken back with 0f 01; 0 never despawns, which is what doors did "
                + "until now",
            c => c.Doors.DespawnRadiusMetres, "150");
        Add("doors", "retailInteract", "CRANBERRY_DOOR_RETAIL_INTERACT", ConfigKind.Bool, "D247",
            "the friend server's two door numbers under D53: interact range 2.0 m and "
                + "flags1 0x10",
            c => c.Doors.RetailInteract);
        Add("doors", "interactRange", "CRANBERRY_DOOR_INTERACT_RANGE", ConfigKind.Float, "D247",
            "the InteractReplicationData range on a DOOR's ea 04; ground loot keeps its own",
            c => c.Doors.InteractRangeMetres, "3");
        Add("doors", "retailSound", "CRANBERRY_DOOR_RETAIL_SOUND", ConfigKind.Bool, "D264",
            "every family plays one of the two door sounds the retail client keeps resident "
                + "(model 9903 -> row 12 metal, everything else -> row 2 wood), so no door is silent",
            c => c.Doors.RetailSound);

        // skins --------------------------------------------------------------------------------
        Add("skins", "stowedMeshes", "CRANBERRY_STOWED_MESHES", ConfigKind.Bool, "D83",
            "a stowed gun gets a mesh", c => c.Skins.Options.DressStowedWeapons);
        Add("skins", "wornShader", "CRANBERRY_WORN_SHADER", ConfigKind.Bool, "D84",
            "a worn attachment carries its own ShaderParameterGroupId",
            c => c.Skins.Options.SendWornShaderGroups);
        Add("skins", "handAppearance", "CRANBERRY_HAND_APPEARANCE", ConfigKind.Bool, "D190",
            "the gun in the HAND carries the same appearance rows as the gun on the back",
            c => c.Skins.Options.SendActiveHandAppearance);
        Add("skins", "wardrobeShader", "CRANBERRY_WARDROBE_SHADER", ConfigKind.Bool, "D190",
            "a wardrobe attachment gets a fallback group instead of the neutral Default tint",
            c => c.Skins.Options.SendWardrobeShaderGroups);
        Add("skins", "dressLast", "CRANBERRY_DRESS_LAST", ConfigKind.Bool, "D211",
            "OFF: an appended dress after the lobby skin rows wedges the Z2 world load",
            c => c.Skins.Options.DressLastAfterSkinRows);
        Add("skins", "remodel", "CRANBERRY_SKIN_REMODEL", ConfigKind.Bool, "D85",
            "let a wardrobe pick re-MODEL rather than re-tint (default: re-tint only)",
            c => !c.Skins.Options.SkinRetintOnly);
        Add("skins", "dressSuppress", "CRANBERRY_DRESS_SUPPRESS", ConfigKind.Bool, "D86",
            "drop a SetCharacterEquipment byte-identical to the last one this session",
            c => c.Skins.Options.SuppressIdenticalDress);
        Add("skins", "census", "CRANBERRY_SKIN_CENSUS", ConfigKind.Bool, "D87",
            "the boot-time grey census - a diagnostic, byte-free",
            c => c.Skins.Options.RunSkinCensus);
        Add("skins", "genderRows", "CRANBERRY_GENDER_ROWS", ConfigKind.Bool, "D88",
            "order an attachment's appearance-id pair by the wearer's own body",
            c => c.Skins.Options.OrderAppearanceRowsByGender);
        Add("skins", "lobbyPacks", "CRANBERRY_LOBBY_PACKS", ConfigKind.Bool, "D90",
            "let backpacks and performance footwear through the preview-only gate",
            c => !c.Skins.Options.GateBackpacksAndPerformanceFootwear);
        Add("skins", "wardrobeStore", "CRANBERRY_WARDROBE_STORE", ConfigKind.Text, "D89",
            "where wardrobe selections are kept, per character guid",
            c => c.Skins.Options.WardrobeStoreRoot, @"C:\Aug2017\state\wardrobe-flip");
        Add("skins", "wornSkins", "CRANBERRY_WORN_SKINS_IN_WORLD", ConfigKind.Bool, "D283",
            "a picked-up item wears its selected skin on every worn slot and stow peg, not only the hand",
            c => c.Skins.Options.SendWornSkinsInWorld);
        Add("skins", "wardrobeRestore", "CRANBERRY_WARDROBE_RESTORE", ConfigKind.Bool, "D284",
            "replay the stored wardrobe selections at login; OFF is the retail starter outfit",
            c => c.Skins.Options.RestoreWardrobeSelections);
        Add("skins", "authoredRows", "CRANBERRY_APPEARANCE_AUTHORED_ROWS", ConfigKind.Bool, "D203",
            "Cranberry's own appearance rows for the thirteen grey ground-loot wearables",
            c => c.Skins.AppearanceAuthoredRows);
        Add("skins", "appearanceTable", "CRANBERRY_APPEARANCE_TABLE", ConfigKind.Text, "D316",
            "full: the whole friend DynamicAppearance capture; filtered: only the reward catalogue",
            c => c.Skins.AppearanceTable, "filtered");

        // weapons ------------------------------------------------------------------------------
        Add("weapons", "z1LiveGunplay", "CRANBERRY_Z1_LIVE_GUNPLAY", ConfigKind.Bool, "-",
            "Recorded Z1BR firearm tuning adapted to August; excludes shotguns", c => c.Weapons.Stages.Z1LiveGunplay);
        Add("weapons", "table", "CRANBERRY_WEAPON_TABLE", ConfigKind.Text, "D313",
            "which WeaponDefinitions table ships: \"captured\" crosses the friend's own recoil, cone-of-fire, reload and pellet numbers into list 2 and fills lists 3 and 5 from his capture; \"generated\" is Cranberry's own table, byte for byte",
            c => c.Weapons.Stages.WeaponTable.ToString().ToLowerInvariant(), "generated");
        Add("weapons", "projectileTable", "CRANBERRY_PROJECTILE_TABLE", ConfigKind.Text, "D314",
            "which ProjectileDefinitions table ships: \"z1\" is all 131 rows of the owner's own projectileDefinitions.json (models, speeds, flight types, tracers); \"august\" is Cranberry's 14 constructor-default records",
            c => c.Weapons.Stages.ProjectileTable.ToString().ToLowerInvariant(), "august");
        Add("weapons", "definitions", "CRANBERRY_WEAPON_DEFINITIONS", ConfigKind.Bool, "D130",
            "send the WeaponDefinitions ReferenceData blob at all",
            c => c.Weapons.Stages.SendWeaponDefinitions);
        Add("weapons", "list0", "CRANBERRY_WEAPON_DEFS_LIST0", ConfigKind.Bool, "D129",
            "populate list 0 (the weapon records)", c => c.Weapons.Stages.PopulateWeaponDefinitions);
        Add("weapons", "list1", "CRANBERRY_WEAPON_DEFS_LIST1", ConfigKind.Bool, "D129",
            "populate list 1 (fire groups)", c => c.Weapons.Stages.PopulateFireGroups);
        Add("weapons", "list2", "CRANBERRY_WEAPON_DEFS_LIST2", ConfigKind.Bool, "D160",
            "populate list 2 (fire modes)", c => c.Weapons.Stages.PopulateFireModes);
        Add("weapons", "list4", "CRANBERRY_WEAPON_DEFS_LIST4", ConfigKind.Bool, "D272",
            "populate list 4 (fire mode to projectile)",
            c => c.Weapons.Stages.PopulateFireModeProjectiles);
        Add("weapons", "ammoSlots", "CRANBERRY_WEAPON_DEFS_AMMOSLOTS", ConfigKind.Bool, "D196",
            "populate the weapon record's ammo-slot array", c => c.Weapons.Stages.PopulateAmmoSlots);
        Add("weapons", "projectileDefinitions", "CRANBERRY_PROJECTILE_DEFINITIONS", ConfigKind.Bool, "D273",
            "send ReferenceData \"ProjectileDefinitions\"",
            c => c.Weapons.Stages.SendProjectileDefinitions);
        Add("weapons", "adsFov", "CRANBERRY_WEAPON_ADS_FOV", ConfigKind.Bool, "D212",
            "write DEFAULT_ZOOM into list 2", c => c.Weapons.Stages.WriteAdsZoom);
        Add("weapons", "adsZoom", "CRANBERRY_WEAPON_ADS_ZOOM", ConfigKind.Float, "D212",
            "the ADS zoom written when adsFov is on", c => c.Weapons.Stages.WeaponAdsZoom, "2.5");
        Add("weapons", "tail", "CRANBERRY_WEAPON_TAIL", ConfigKind.Bool, "D129",
            "the Weapon ItemAdd tail", c => c.Weapons.Stages.WriteWeaponItemAddTail);
        Add("weapons", "tailState", "CRANBERRY_WEAPON_TAIL_STATE", ConfigKind.Bool, "D148",
            "the tail's idle scalars", c => c.Weapons.Stages.TailIdleState);
        Add("weapons", "tailAmmo", "CRANBERRY_WEAPON_TAIL_AMMO", ConfigKind.Bool, "D196",
            "the tail's magazine word", c => c.Weapons.Stages.WriteWeaponTailMagazine);
        Add("weapons", "wield", "CRANBERRY_WIELD", ConfigKind.Bool, "D129",
            "permit a body-slot-7 equipment row at all", c => c.Weapons.Stages.AllowWielding);
        Add("weapons", "automatic", "CRANBERRY_WEAPON_AUTOMATIC", ConfigKind.Bool, "D129",
            "mark fire groups automatic", c => c.Weapons.Stages.MarkFireGroupsAutomatic);
        Add("weapons", "stance", "CRANBERRY_WEAPON_STANCE", ConfigKind.Bool, "D149",
            "send 0f 20 Character.WeaponStance after a draw", c => c.Weapons.Stages.SendWeaponStance);
        Add("weapons", "movementModifier", "CRANBERRY_WEAPON_MOVEMENT_MODIFIER", ConfigKind.Float, "D143",
            "def+0x5c and +0x58 - the client presets 1.0f and 0 freezes the hand (D143)",
            c => c.Weapons.Stages.WeaponMovementModifier, "0.5");
        Add("weapons", "bodyId", "CRANBERRY_WEAPON_DEF_BODY_ID", ConfigKind.Bool, "D142",
            "write the record's id at def+0x18 as well as at the list key",
            c => c.Weapons.Stages.WriteWeaponDefinitionBodyId);
        Add("weapons", "ironSightsArmedOnly", "CRANBERRY_WEAPON_IRONSIGHTS_ARMED", ConfigKind.Bool, "D287",
            "IRON_SIGHTS on the fire modes of armed groups only - not the fists or the binoculars",
            c => c.Weapons.Stages.IronSightsArmedOnly);
        Add("weapons", "meleeAbility", "CRANBERRY_WEAPON_MELEE_ABILITY", ConfigKind.Bool, "D288",
            "write MELEE_ABILITY_ID (rec+0x194) on the fire modes of unarmed groups",
            c => c.Weapons.Stages.WriteMeleeAbilityIds);
        Add("weapons", "fireSound", "CRANBERRY_WEAPON_FIRE_SOUND", ConfigKind.Bool, "D234",
            "write EFFECT_GROUP (rec+0x104) - the fire composite / gunshot sound",
            c => c.Weapons.Stages.WriteFireEffect);
        Add("weapons", "noMeleeMag", "CRANBERRY_WEAPON_NO_MELEE_MAG", ConfigKind.Bool, "D236",
            "ship mode-0 charge 0 for a magazine-less weapon so the fists and binoculars show no magazine",
            c => c.Weapons.Stages.PlainWieldNoMagazine);
        Add("weapons", "binocularsOptic", "CRANBERRY_BINOCULARS_OPTIC", ConfigKind.Bool, "D235",
            "FORCE_FP_SCOPE on the binoculars - raise to first person and zoom on use",
            c => c.Weapons.Stages.BinocularsOptic);
        Add("weapons", "binocularsOpticFov", "CRANBERRY_BINOCULARS_OPTIC_FOV", ConfigKind.Float, "D235",
            "the binoculars' aimed first-person field of view in degrees",
            c => c.Weapons.Stages.BinocularsOpticFovDegrees, "25");
        Add("weapons", "fireModeTypes", "CRANBERRY_WEAPON_FIRE_MODE_TYPES", ConfigKind.Bool, "D291",
            "write TYPE (rec+0x24): 3 melee on the fists and blades, 12 throwable on the grenades, 8 on the binoculars - what the trigger does, and what hides the hotbar's 0-0",
            c => c.Weapons.Stages.WriteFireModeTypes);
        Add("weapons", "binocularsAbility", "CRANBERRY_BINOCULARS_ABILITY", ConfigKind.Bool, "D293",
            "TYPE 8 on the binoculars - the trigger runs the item's own activatable ability 1111157",
            c => c.Weapons.Stages.BinocularsTriggerAbility);
        Add("weapons", "autoRetail", "CRANBERRY_WEAPON_AUTO_RETAIL", ConfigKind.Bool, "D331",
            "the AK-47 family is automatic (its own text says so) and the AR-15 is not; AUTO_FIRE_TIME_MS = REFIRE_TIME_MS on the automatic modes - off is the wave-9 AR-15 diagnostic",
            c => c.Weapons.Stages.RetailAutomatic);
        Add("weapons", "shotgunPellets", "CRANBERRY_SHOTGUN_PELLETS", ConfigKind.Int, "D332",
            "Generated-table diagnostic pellet count; positive values enable the systematic 17-pellet early-August overlay; 0 omits that overlay",
            c => c.Weapons.Stages.ShotgunPellets, "12");
        Add("weapons", "shotgunSpreadDeg", "CRANBERRY_SHOTGUN_SPREAD_DEG", ConfigKind.Float, "D332",
            "PELLET_SPREAD on the same modes, in degrees (RULING 4.0); 0 puts every pellet on one line",
            c => c.Weapons.Stages.ShotgunSpreadDegrees, "2.5");

        Add("weapons", "throwables", "CRANBERRY_THROWABLES", ConfigKind.Bool, "D300",
            "the grenades' data: the frag's ruled fire group 1404, FIRE_DURATION_MS on every throwable mode, a list-4 row per throwable mode and the five physics ProjectileDefinitions records",
            c => c.Weapons.Stages.Throwables);
        Add("weapons", "throwableSpeed", "CRANBERRY_THROWABLE_SPEED", ConfigKind.Float, "D301",
            "SPEED of the five grenade projectiles, world units per second",
            c => c.Weapons.Stages.ThrowableSpeed, "12");
        Add("weapons", "throwableWindupMs", "CRANBERRY_THROWABLE_WINDUP_MS", ConfigKind.Int, "D304",
            "FIRE_DURATION_MS on every throwable mode - the throw wind-up the client's trigger press needs to be >= 1",
            c => c.Weapons.Stages.ThrowableWindupMs, "250");

        // combat -------------------------------------------------------------------------------
        Add("combat", "enabled", "CRANBERRY_COMBAT", ConfigKind.Bool, "D95",
            "answer the 0x82 weapon family at all", c => c.Combat.Options.Enabled);
        Add("combat", "damage", "CRANBERRY_COMBAT_DAMAGE", ConfigKind.Bool, "D92",
            "apply the retail damage table to a hit", c => c.Combat.Options.EnableCombatDamage);
        Add("combat", "hitMarker", "CRANBERRY_HIT_MARKER", ConfigKind.Bool, "D94",
            "send the hit marker", c => c.Combat.Options.SendHitMarker);
        Add("combat", "fireLayout", "CRANBERRY_WEAPON_FIRE_LAYOUT", ConfigKind.Bool, "D93",
            "read c2s fire with the decoded layout rather than treating it as unknown",
            c => c.Combat.Options.Layout != Zone.Combat.WeaponFireLayout.Unknown);
        Add("combat", "practiceTarget", "CRANBERRY_PRACTICE_TARGET", ConfigKind.Bool, "D95",
            "opt-in NPC practice dummy; normal landings do not spawn a human actor (landing-body-20260906.md)",
            c => c.Combat.Options.PracticeTarget);
        Add("combat", "meleeDamage", "CRANBERRY_MELEE_DAMAGE", ConfigKind.Bool, "D132",
            "melee through the 0xa0 ABILITY family", c => c.Combat.Options.MeleeDamage);
        Add("combat", "meleeOnTrigger", "CRANBERRY_MELEE_ON_TRIGGER", ConfigKind.Bool, "D237",
            "arbitrate a melee swing on the 82 01 trigger-down, not the 0xa0 abilities family",
            c => c.Combat.Options.MeleeOnTrigger);
        Add("combat", "abilityManager", "CRANBERRY_ABILITY_MANAGER", ConfigKind.Bool, "D132",
            "send the ability manager", c => c.Combat.Options.SendAbilityManager);
        Add("combat", "fireModeReply", "CRANBERRY_WEAPON_FIREMODE_REPLY", ConfigKind.Bool, "D198",
            "answer 82 0c SwitchFireModeRequest", c => c.Combat.Options.SwitchFireModeReply);
        Add("combat", "magazineResync", "CRANBERRY_MAGAZINE_RESYNC", ConfigKind.Bool, "D223",
            "adopt the client's non-dry trigger on a weapon this server declared empty (docs/107 section 10)",
            c => c.Combat.Options.MagazineResync);
        Add("combat", "resyncFromBag", "CRANBERRY_MAGAZINE_RESYNC_FROM_BAG", ConfigKind.Bool, "D333",
            "the adopted magazine is paid for out of the bag, and withheld when the bag holds none",
            c => c.Combat.Options.MagazineResyncFromBag);
        Add("combat", "fireHint", "CRANBERRY_WEAPON_FIRE_HINT", ConfigKind.Bool, "D223",
            "decode 82 20 WeaponFireHint and cross-check it against the accepted shot (docs/107 section 10)",
            c => c.Combat.Options.ActOnWeaponFireHint);
        Add("combat", "reloadTimed", "CRANBERRY_RELOAD_TIMED", ConfigKind.Bool, "D334",
            "the 82 08 Reload and the stack packets go out after the weapon's own RELOAD_TIME_MS, a pump's shells one apart",
            c => c.Combat.Options.TimedReload);

        Add("combat", "throwables", "CRANBERRY_THROWABLES_ARM", ConfigKind.Bool, "D305",
            "a throwable's 82 03 is a THROW: one grenade leaves the stack, 82 19 GuidedExplode places the blast, 82 26 bounces are counted, the fallback detonates at the throw point",
            c => c.Combat.Options.Throwables);
        Add("combat", "throwableDamage", "CRANBERRY_THROWABLE_DAMAGE", ConfigKind.Bool, "D308",
            "a detonation hurts what is inside its radius - practice targets, players, the thrower",
            c => c.Combat.Options.ThrowableDamage);

        // ammo ---------------------------------------------------------------------------------
        Add("ammo", "fromBag", "CRANBERRY_AMMO_FROM_BAG", ConfigKind.Bool, "D165",
            "a magazine is fed from the bag", c => c.Ammo.Options.AmmoFromBag);
        Add("ammo", "gunsSpawnEmpty", "CRANBERRY_GUNS_SPAWN_EMPTY", ConfigKind.Bool, "D165",
            "a looted gun spawns EMPTY", c => c.Ammo.Options.GunsSpawnEmpty);
        Add("ammo", "itemUpdate", "CRANBERRY_ITEM_UPDATE", ConfigKind.Bool, "D168",
            "send 11 03 ItemUpdate for stack counts and per-shot durability",
            c => c.Ammo.Options.SendItemUpdate);

        // crafting -----------------------------------------------------------------------------
        Add("crafting", "recipeList", "CRANBERRY_CRAFTING", ConfigKind.Bool, "D48",
            "the 0x26 09 Recipe.List burst after the in-match ClientIsReady",
            c => c.Crafting.Options.SendRecipeList);
        Add("crafting", "craft", "CRANBERRY_CRAFT", ConfigKind.Bool, "D48",
            "accept a craft request", c => c.Crafting.Options.AllowCrafting);
        Add("crafting", "retailRecipes", "CRANBERRY_CRAFT_RECIPES", ConfigKind.Bool, "D256",
            "ship the six recipes decoded from the owner's admin capture, not the wave-6 design four",
            c => c.Crafting.Options.RetailRecipes);
        Add("crafting", "castBar", "CRANBERRY_CRAFT_CAST_BAR", ConfigKind.Bool, "D257",
            "a craft runs on a cf 02 cast bar and a timer instead of completing inside the request",
            c => c.Crafting.Options.CraftCastBar);
        Add("crafting", "interactionStop", "CRANBERRY_INTERACTION_STOP", ConfigKind.Bool, "D258",
            "close every completed cast with cf 03 InteractionStop, twice, as retail does",
            c => c.Crafting.Options.SendInteractionStop);

        // match --------------------------------------------------------------------------------
        Add("match", "seed", "CRANBERRY_MATCH_SEED", ConfigKind.Text, "D39",
            "1-16 hex digits replays that match's drop AND its gas circles; unset = per match",
            c => c.Match.Seed == 0 ? "per match" : MatchSeedText(c), "1a2b3c");
        Add("match", "endgame", "CRANBERRY_MATCH_ENDGAME", ConfigKind.Bool, "D154",
            "the death-and-victory sends", c => c.Match.End.Enabled);
        Add("match", "targetCounts", "CRANBERRY_MATCH_TARGET_COUNTS", ConfigKind.Bool, "D152",
            "let the practice target count as the last opponent (dev only); the older alias CRANBERRY_PRACTICE_TARGET_ENDS_MATCH still works",
            c => c.Match.End.PracticeTargetCountsAsOpponent);
        Add("match", "leaveOnEnd", "CRANBERRY_MATCH_LEAVE_ON_END", ConfigKind.Bool, "D154",
            "send ce 1b LeaveMatch; the re-login path is untested", c => c.Match.End.LeaveOnEnd);
        Add("match", "collisionDamage", "CRANBERRY_COLLISION_DAMAGE", ConfigKind.Bool, "D155",
            "apply 8e 01 Collision.Damage as fall or vehicle damage; the 44-byte body is proven from 47 live records",
            c => c.Match.End.CollisionDamage);

        // lobby --------------------------------------------------------------------------------
        Add("lobby", "ms", "CRANBERRY_LOBBY_MS", ConfigKind.Int, "D248",
            "how long the player stands in Fort Destiny before StartMatch (Z1 retail row: 120 s)",
            c => c.Lobby.Options.CountdownMs, "20000");
        Add("lobby", "armMs", "CRANBERRY_LOBBY_ARM_MS", ConfigKind.Int, "D249",
            "0 arms the lobby HUD at the client's own ClientFinishedLoading; a positive value is the old blind timer",
            c => c.Lobby.Options.ArmMs, "15000");
        Add("lobby", "minPlayers", "CRANBERRY_LOBBY_MIN_PLAYERS", ConfigKind.Int, "D250",
            "population at or above which the countdown runs rather than holding on \"Waiting for players...\"",
            c => c.Lobby.Options.MinPlayers, "8");
        Add("lobby", "labels", "CRANBERRY_LOBBY_LABELS", ConfigKind.Bool, "D250",
            "label the green widget 13198 below the minimum population and 13356 while counting",
            c => c.Lobby.Options.Labels);
        Add("lobby", "banners", "CRANBERRY_LOBBY_BANNERS", ConfigKind.Bool, "D251",
            "the ce 14 \"Match starts in N seconds.\" banners at 60 / 30 / 10 s",
            c => c.Lobby.Options.Banners);

        // bounty -------------------------------------------------------------------------------
        Add("bounty", "enabled", "CRANBERRY_BOUNTY", ConfigKind.Bool, "D253",
            "67 0e payout tables and 67 0d backed state with the lobby HUD",
            c => c.Bounty.Options.Enabled);
        Add("bounty", "currency", "CRANBERRY_BOUNTY_CURRENCY", ConfigKind.Bool, "D255",
            "the four ab 03 balance rows at Fort Destiny arrival, Credits included",
            c => c.Bounty.Options.Currency);
        Add("bounty", "ante", "CRANBERRY_BOUNTY_ANTE", ConfigKind.Bool, "D253",
            "answer 67 0c SelectBounty: debit, re-send ab 03, echo 67 0d",
            c => c.Bounty.Options.AcceptAnte);
        Add("bounty", "suppressDropOpen", "CRANBERRY_BOUNTY_SUPPRESS_DROP_OPEN", ConfigKind.Bool, "D254",
            "keep every bounty byte inside the lobby and send nothing bounty-shaped at/after ce 16 StartMatch (the in-match auto-open is a 1148 client artefact); off restores the old pre-StartMatch 67 0d re-state",
            c => c.Bounty.Options.SuppressDropOpen);

        // menu ---------------------------------------------------------------------------------
        Add("menu", "views", "CRANBERRY_MENU_VIEWS", ConfigKind.Text, "D193",
            "all | reference (the two capture-proven shots) | off",
            c => c.Menu.Views.Coverage.ToString(), "reference");
        Add("menu", "stance", "CRANBERRY_MENU_STANCE", ConfigKind.Bool, "D201",
            "pose the menu actor with Character.WeaponStance 0 on every answered view",
            c => c.Menu.Actor.SendWeaponStanceOnViewChange);
        Add("menu", "interactionEcho", "CRANBERRY_MENU_INTERACTION_ECHO", ConfigKind.Bool, "D201",
            "echo the client's own 09 16 00 straight back at it",
            c => c.Menu.Actor.EchoFreeInteractionNpc);
        Add("menu", "spawnMark", "CRANBERRY_MENU_SPAWN_MARK", ConfigKind.Bool, "D202",
            "spawn the lobby actor on the capture's own kotkdefault mark (z 279.97, not 279.72)",
            c => c.Menu.Actor.SpawnPosition == Zone.MenuActorOptions.ProvenMenuMark);
        Add("menu", "topBar", "CRANBERRY_MENU_TOPBAR", ConfigKind.Bool, "D274",
            "the Experience and Currency top bar", c => c.Menu.TopBar.SendTopBar);
        Add("menu", "topBarXp", "CRANBERRY_MENU_TOPBAR_XP", ConfigKind.Int, "D274",
            "the experience number", c => c.Menu.TopBar.Experience, "4242");
        Add("menu", "topBarRank", "CRANBERRY_MENU_TOPBAR_RANK", ConfigKind.Int, "D274",
            "the rank number", c => c.Menu.TopBar.Rank, "7");
        Add("menu", "topBarScrap", "CRANBERRY_MENU_TOPBAR_SCRAP", ConfigKind.Int, "D274",
            "the scrap number", c => c.Menu.TopBar.Scrap, "99");
        Add("menu", "topBarCrowns", "CRANBERRY_MENU_TOPBAR_CROWNS", ConfigKind.Int, "D255",
            "the Crowns balance - the Bounty screen's HARD ante spends it",
            c => c.Menu.TopBar.Crowns, "2500");
        Add("menu", "topBarSkulls", "CRANBERRY_MENU_TOPBAR_SKULLS", ConfigKind.Int, "D255",
            "the Skulls balance - what a Bounty pays out in", c => c.Menu.TopBar.Skulls, "1000");
        Add("menu", "topBarCredits", "CRANBERRY_MENU_TOPBAR_CREDITS", ConfigKind.Int, "D255",
            "the Credits balance - the Bounty screen's SOFT ante spends it",
            c => c.Menu.TopBar.Credits, "500");
        Add("menu", "motd", "CRANBERRY_MENU_MOTD", ConfigKind.Bool, "D275",
            "the MOTD panel", c => c.Menu.TopBar.SendMotd);
        Add("menu", "motdText", "CRANBERRY_MENU_MOTD_TEXT", ConfigKind.Text, "D275",
            "the MOTD body text", c => c.Menu.TopBar.MotdText, "flip");

        // console ------------------------------------------------------------------------------
        Add("console", "enabled", "CRANBERRY_CONSOLE", ConfigKind.Bool, "D174",
            "the August client's own debug console", c => c.Console.Options.Enabled);
        Add("console", "surface", "CRANBERRY_CONSOLE_SURFACE", ConfigKind.Text, "D174",
            "print | chat | chat0 | alert | lua", c => c.Console.Options.Surface.ToString(), "chat");
        Add("console", "localOwner", "CRANBERRY_CONSOLE_LOCAL_OWNER", ConfigKind.Bool, "D174",
            "treat the local player as the console's owner", c => c.Console.Options.LocalIsOwner);
        Add("console", "tiers", "CRANBERRY_CONSOLE_TIERS", ConfigKind.Text, "D176",
            "which command tiers are registered", c => c.Console.Options.Tiers, "1");
        Add("console", "register", "CRANBERRY_CONSOLE_REGISTER", ConfigKind.Text, "D178",
            "each-zone | once | zoning | off", c => c.Console.Options.Register.ToString(), "once");
        Add("console", "selfFlag", "CRANBERRY_CONSOLE_SELF_FLAG", ConfigKind.Bool, "D174",
            "set the self record's flag that opens the console",
            c => c.Console.Options.SelfFlagOpensConsole);
        Add("console", "rows", "CRANBERRY_CONSOLE_ROWS", ConfigKind.Int, "D210",
            "the mod menu's row count", c => c.Console.Options.MenuRows, "12");
        Add("console", "width", "CRANBERRY_CONSOLE_WIDTH", ConfigKind.Int, "D210",
            "the mod menu's width", c => c.Console.Options.MenuWidth, "301");
        Add("console", "confirms", "CRANBERRY_CONSOLE_CONFIRMS", ConfigKind.Bool, "D177",
            "echo a confirmation for every accepted command", c => c.Console.Options.Confirms);
        Add("console", "clientLogs", "CRANBERRY_CLIENT_LOGS", ConfigKind.Text, "D174",
            "where the client's own AdminCommands.log is read from",
            c => c.Console.Options.ClientLogsPath, @"C:\Aug2017\state\clientlogs");

        // transport ----------------------------------------------------------------------------
        Add("transport", "maxLoginConnections", "CRANBERRY_MAX_LOGIN_CONNECTIONS", ConfigKind.Int, "network-scale-20260908",
            "maximum concurrent login transport sessions (1..65536)", c => c.Transport.MaxLoginConnections, "16384");
        Add("transport", "maxGatewayConnections", "CRANBERRY_MAX_GATEWAY_CONNECTIONS", ConfigKind.Int, "network-scale-20260908",
            "maximum gateway sessions, including menu and match players (1..65536)", c => c.Transport.MaxGatewayConnections, "16384");
        Add("transport", "burstSlice", "CRANBERRY_BURST_SLICE", ConfigKind.Int, "D73",
            "tunnel messages per slice of the landing burst; 0 = one synchronous continuation",
            c => c.Transport.BurstSliceSize, "8");
        Add("transport", "burstSliceMs", "CRANBERRY_BURST_SLICE_MS", ConfigKind.Int, "D73",
            "the pause between two slices", c => c.Transport.BurstSliceDelayMs, "20");

        // features -----------------------------------------------------------------------------
        Add("features", "containers", "CRANBERRY_CONTAINERS", ConfigKind.Bool, "D33",
            "the container and loadout model", c => c.Features.Containers);
        Add("features", "movementStats", "CRANBERRY_MOVEMENT_STATS", ConfigKind.Bool, "D32",
            "the per-character movement stat burst", c => c.Features.MovementStats);
        Add("features", "doors", "CRANBERRY_DOORS", ConfigKind.Bool, "D34",
            "spawn doors at all", c => c.Features.Doors);
        Add("features", "vehicles", "CRANBERRY_VEHICLES", ConfigKind.Bool, "D35",
            "spawn vehicles at all", c => c.Features.Vehicles);
        Add("features", "lootClusters", "CRANBERRY_LOOT_CLUSTERS", ConfigKind.Bool, "D73",
            "cluster ground loot rather than scattering it", c => c.Features.LootClusters);
        Add("features", "groundLootShader", "CRANBERRY_GROUND_LOOT_SHADER", ConfigKind.Bool, "D328",
            "a gun lying on the ground carries its base appearance shader group (AddLightweightNpc +0x1a4, the vehicle tint field) instead of 0, which composites white",
            c => c.Features.GroundLootShader);

        // dev ----------------------------------------------------------------------------------
        Add("dev", "bootstrapDelayMs", "CRANBERRY_BOOTSTRAP_DELAY_MS", ConfigKind.Int, "-",
            "hold the zone bootstrap this long (bring-up aid)", c => c.Dev.BootstrapDelayMs, "500");
        Add("dev", "autoMatchMs", "CRANBERRY_AUTO_MATCH_MS", ConfigKind.Int, "-",
            "zone into Z2 without a PLAY click, this long after the lobby's ClientIsReady",
            c => c.Dev.AutoMatchMs, "1000");
        Add("dev", "groundLootMs", "CRANBERRY_DEV_GROUND_LOOT_MS", ConfigKind.Int, "D31",
            "drop the sample items around the player; superseded by real loot, kept until `loot ring`",
            c => c.Dev.GroundLootMs, "250");
        Add("dev", "starterWeapon", "CRANBERRY_STARTER_WEAPON", ConfigKind.Bool, "D182",
            "grant and draw a weapon at bootstrap; kept until `give <item> wield` exists",
            c => c.Dev.StarterWeapon);
        Add("dev", "wieldFirstPickup", "CRANBERRY_WIELD_FIRST_PICKUP", ConfigKind.Bool, "D36",
            "draw the first gun picked up - an open experiment (docs/95)",
            c => c.Dev.Inventory.WieldFirstWeapon);
        Add("dev", "wieldSequence", "CRANBERRY_WIELD_SEQUENCE", ConfigKind.Bool, "D183",
            "draw with the eight-packet sequence instead of one whole-character dress",
            c => c.Dev.Inventory.UseWieldSequence);
        Add("dev", "bootstrapWeaponItems", "CRANBERRY_BOOTSTRAP_WEAPON_ITEMS", ConfigKind.Bool, "D129",
            "grant CODE_FACTORY_NAME=Weapon items in the bootstrap burst",
            c => c.Dev.Inventory.GrantWeaponItemsAtBootstrap);
        Add("dev", "classMappings", "CRANBERRY_CLASS_MAPPINGS", ConfigKind.Bool, "D72",
            "fold ItemClassMappings.txt into a loadout slot's class test",
            c => c.Dev.Inventory.FoldItemClassMappings);
        Add("dev", "quickUseConsumables", "CRANBERRY_QUICK_USE_CONSUMABLES", ConfigKind.Bool, "D141",
            "let a bandage take a free quick-use tile", c => c.Dev.Inventory.QuickUseConsumables);
        Add("dev", "shredAll", "CRANBERRY_SHRED_ALL", ConfigKind.Bool, "D260",
            "every item whose own context menu offers Shred actually shreds - Boots included",
            c => c.Dev.Inventory.ShredEveryOfferedItem);
        Add("dev", "dropNotification", "CRANBERRY_DROP_NOTIFICATION", ConfigKind.Bool, "D259",
            "finish a drop with 0f 4a DroppedItemNotification",
            c => c.Dev.Inventory.SendDroppedItemNotification);
        Add("dev", "dropYaw", "CRANBERRY_DROP_YAW", ConfigKind.Bool, "D259",
            "a dropped object carries the player's facing instead of an identity rotation",
            c => c.Dev.Inventory.DropCarriesFacing);

        // login --------------------------------------------------------------------------------
        Add("login", "regionKeys", "CRANBERRY_LOGIN_REGION_KEYS", ConfigKind.Bool, "D200",
            "ServerInfo Region carries the client's own resolvable locale key, not a bare code",
            c => c.LoginNaming.Regions.Naming == Login.RegionNaming.LocaleKeys);

        // peers (lane 3C) ----------------------------------------------------------------------
        Add("peers", "selfTransientId", "CRANBERRY_SELF_TRANSIENT_ID", ConfigKind.Bool, "D214",
            "write TransientIdTable.LocalPlayer (1) into the self record's +0xe0, not 0",
            c => c.Peers.Options.SelfTransientId);
        Add("peers", "spawn", "CRANBERRY_PEER_SPAWN", ConfigKind.Bool, "D215",
            "the peer enter/leave burst: d5, the peer's 94 01, 82 15, 0f 01",
            c => c.Peers.Options.Spawn);
        Add("peers", "relay", "CRANBERRY_PEER_RELAY", ConfigKind.Bool, "D215",
            "re-frame each decoded channel-2 record as 0x78 for every viewer",
            c => c.Peers.Options.Relay);
        Add("peers", "coalesceMovement", "CRANBERRY_PEER_COALESCE_MOVEMENT", ConfigKind.Bool, "Audit20260910",
            "diagnostic A/B only: expand sparse poses with retained fields before coalescing; default preserves native sparse records",
            c => c.Peers.Options.CoalesceMovement);
        Add("peers", "fireRelay", "CRANBERRY_PEER_FIRE_RELAY", ConfigKind.Bool, "D335",
            "forward a shooter's trigger to his viewers as 82 15 04 01 FireState (start on the corroborated hint, stop on the trigger-up)",
            c => c.Peers.Options.FireRelay);
        Add("peers", "projectileLaunch", "CRANBERRY_PROJECTILE_LAUNCH_RELAY", ConfigKind.Bool, "D315",
            "82 15 04 0b ProjectileLaunch with 12 zero bytes to a shooter's viewers on every accepted 82 03 (shot and throw)",
            c => c.Peers.Options.ProjectileLaunch);
        Add("peers", "sharedGasSeed", "CRANBERRY_SHARED_GAS_SEED", ConfigKind.Bool, "D217",
            "seed a second session's gas from the first so both players see one circle",
            c => c.Peers.Options.SharedGasSeed);
        Add("peers", "redress", "CRANBERRY_PEER_REDRESS", ConfigKind.Bool, "D322",
            "re-send a peer's 94 01 (and 82 15 when its hand changed) to viewers that already see it",
            c => c.Peers.Options.Redress);

        return Couple(keys);
    }

    /// <summary>
    /// Declares the couplings the per-option matrix must tolerate. Three blocks are preset-shaped:
    /// naming a preset moves every knob in that block, and moving any knob moves the block's
    /// reported preset name to "custom" (<c>GasTuning.NameOf</c> and friends). And the root path is
    /// where the wardrobe store lives when nothing else names it. Nothing else in this table may
    /// move two values at once.
    /// <para>
    /// <b>2026-09-03 (docs/114 §5, D247) adds the fifth and last:</b> <c>doors.retailInteract</c>
    /// moves <c>doors.interactRange</c> and <c>doors.spawnFlags1</c>, because the friend server's
    /// 2.0 m interact range and its <c>0x10</c> flag byte are ONE adoption under D53 and a revert
    /// of half of it would answer nothing. Both knobs still move independently.
    /// </para>
    /// </summary>
    private static List<ConfigKey> Couple(List<ConfigKey> keys)
    {
        foreach (string block in new[] { "gas", "movement", "descent" })
        {
            string[] whole = keys.Where(k => k.Block == block).Select(k => k.Path).ToArray();
            string preset = block + ".preset";
            for (int index = 0; index < keys.Count; index++)
            {
                ConfigKey key = keys[index];
                if (key.Block != block)
                {
                    continue;
                }

                bool movesTheWholeBlock = key.Path == preset
                    || key.Path == "gas.scale"
                    || key.Path == "gas.damageScale";
                keys[index] = key with { Couples = movesTheWholeBlock ? whole : [preset] };
            }
        }

        for (int index = 0; index < keys.Count; index++)
        {
            // Per-phase timing belongs to its measured radius sequence. Asking for a different
            // count, geometric sizing, or an explicit radius rate selects the derived clock.
            if (keys[index].Path == "gas.phases")
            {
                keys[index] = keys[index] with { Couples = ["gas.preset", "gas.pacing", "gas.radiusLadder"] };
            }
            else if (keys[index].Path is "gas.wallSpeed" or "gas.radiusLadder")
            {
                keys[index] = keys[index] with { Couples = ["gas.preset", "gas.pacing"] };
            }

            // The wardrobe store lives under the root when nothing else names it (D89).
            if (keys[index].Path == "root.path")
            {
                keys[index] = keys[index] with { Couples = ["skins.wardrobeStore"] };
            }

            // docs/89 (wave 9): the Fists grant FOLLOWS the item-class tail rather than being its
            // own forgotten switch, so turning the tail off takes the bootstrap grant with it.
            if (keys[index].Path == "weapons.tail")
            {
                keys[index] = keys[index] with { Couples = ["dev.bootstrapWeaponItems"] };
            }

            // docs/114 §5 / D247: ONE adoption, TWO numbers. The friend server's
            // ClientInteractComponent range (2.0 m) and its door flag byte (0x10) are the same
            // D53 decision and reverting half of it answers nothing, so this switch chooses the
            // DEFAULT of both. Each of the two still moves on its own, which is what keeps the
            // bisect available.
            if (keys[index].Path == "doors.retailInteract")
            {
                keys[index] = keys[index] with
                {
                    Couples = ["doors.interactRange", "doors.spawnFlags1"],
                };
            }
        }

        return keys;
    }

    private static string Hex(byte value) => "0x" + value.ToString("x2", CultureInfo.InvariantCulture);

    private static string GasName(CranberryConfig config) =>
        Zone.Gas.GasTuning.NameOf(config.Gas.Settings);

    private static string MovementName(CranberryConfig config) =>
        Zone.Movement.MovementTuning.NameOf(config.Movement.Profile);

    private static string DescentName(CranberryConfig config) =>
        Zone.Descent.DescentTuning.NameOf(config.Descent.Settings);

    private static string MatchSeedText(CranberryConfig config) =>
        Zone.Match.MatchSeeds.Format(config.Match.Seed);
}
