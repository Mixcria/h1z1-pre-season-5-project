using Cranberry.Zone.Generated;

namespace Cranberry.Zone.Loot;

/// <summary>
/// Airdrop settings for the August 2017 build. Model/effect ids come from its extracted data.
/// Official June 2017 notes establish the bomb-only top-20 cutoff; February 2018 notes establish
/// that August had one supply crate per plane, the same C130 for bombers, and stronger bombs.
/// Server timings, route geometry, frequency and blast falloff remain documented reconstruction
/// values; see docs/airdrop-retail-20260906.md. They are not exact recovered retail constants.
/// </summary>
public sealed record AirdropOptions
{
    /// <summary>The shipped options: the ruling, on.</summary>
    public static AirdropOptions Default { get; } = new();

    /// <summary>Airdrops off restores the pre-D274 world exactly. <c>CRANBERRY_AIRDROPS=0</c>.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Match-clock milliseconds before the first supply plane arrives. Retained 5:00 reconstruction; the retail schedule has not been recovered.</summary>
    public long FirstDropAtMs { get; init; } = Rulings.LootGates.FirstDropAtMs;

    /// <summary>Match-clock milliseconds between crates. 4:00. A ruling.</summary>
    public long DropIntervalMs { get; init; } = Rulings.LootGates.DropIntervalMs;

    /// <summary>Most supply crates per match. One crate per supply plane is supported by the February 2018 change to three; the four-plane cap is reconstruction.</summary>
    public int MaxDropsPerMatch { get; init; } = Rulings.LootGates.MaxDropsPerMatch;

    /// <summary>
    /// Bomb-run cutoff. The June 2017 official notes say BOMBS stop at 20 survivors;
    /// supply crates are not subject to this cutoff. Already scheduled bombs finish their run.
    /// </summary>
    public int StopAtPlayersAlive { get; init; } = Rulings.LootGates.StopAtPlayersAlive;

    /// <summary>
    /// <b>The client's own number</b> — <c>StringHashToValue.txt</c>
    /// <c>Airdrops.DefaultMinPlayerCount = 1</c>, the only <c>Airdrops.*</c> tunable in the whole
    /// August build. It is why a single-player click test still gets a crate.
    /// </summary>
    public int MinPlayersAlive { get; init; } = Rulings.LootGates.MinPlayersAlive;

    /// <summary>Milliseconds from release by the plane to landing, in addition to the approach flight. Parachute descent duration remains reconstruction.</summary>
    public long DescentMs { get; init; } = Rulings.LootGates.DescentMs;

    /// <summary>
    /// <b>Retail, Medium confidence (Jan 2017 guide):</b> the crate <i>"lands and must be unlocked
    /// (~8 s)"</i>. The crate stands in the world from the moment it lands and refuses <c>[F]</c>
    /// until this has elapsed. A press inside the window is logged and does nothing — deliberately
    /// silent, because the client's own prompt vocabulary carries no "unlocking" line and inventing
    /// a banner for it would be a second unproven string for an eight-second window.
    /// </summary>
    public long UnlockMs { get; init; } = Rulings.LootGates.UnlockMs;

    /// <summary>
    /// The bomber spread is bounded by this fraction of the revealed safe circle.
    /// Supply crates land at the circle centre; before reveal they use the play-area centre.
    /// </summary>
    public double SafeZoneFraction { get; init; } = Rulings.LootGates.SafeZoneFraction;

    /// <summary>
    /// <b>Retail, dated:</b> <i>"always laminated armor, <b>50 % chance of a hunting rifle</b>"</i>.
    /// </summary>
    public double RifleChance { get; init; } = Rulings.LootGates.RifleChance;

    /// <summary>Weighted bundles drawn on top of the guaranteed set. A ruling.</summary>
    public int PoolDraws { get; init; } = Rulings.LootGates.PoolDraws;

    /// <summary>
    /// How far from the crate its contents are laid when it is opened. 1.5 m keeps the whole spill
    /// inside the client's own 2 m proximity ball (<c>LootStreamOptions.PanelRadiusMetres</c>, D70),
    /// so the <c>[F]</c> panel lists a crate's contents as one set — the same coupling the gun
    /// cluster's 0.5 m offset has.
    /// </summary>
    public float SpillRadiusMetres { get; init; } = Rulings.LootGates.SpillRadiusMetres;

    /// <summary>The August client composite PFX_Impact_AirDrop_Large, id 5038.</summary>
    public uint LandingEffectId { get; init; } = Rulings.LootGates.LandingEffectId;

    /// <summary>August PFX_RallyPoint_Smoke_Green. Candidate color/placement require native acceptance; 0 disables the marker.</summary>
    public uint MarkerEffectId { get; init; } = 3622;

    /// <summary>Independent of nearby loot and grenade visibility; declared server interest tuning.</summary>
    public float MarkerRangeMetres { get; init; } = 2000f;

    /// <summary>Prevents repeated add/remove at the interest boundary. The anchor explicitly receives the same render distance as this exit radius.</summary>
    public float MarkerHysteresisMetres { get; init; } = 100f;

    /// <summary>The August client Vehicle_C130.adr, model 9215, used for supply and bomber flights.</summary>
    public uint PlaneModelId { get; init; } = Rulings.LootGates.PlaneModelId;

    /// <summary>
    /// The inbound banner, on <c>ClientUpdate.TextAlert 11 31</c>. The August client has <b>no</b>
    /// airdrop locale string — all 21 rows of <c>AugustStrings</c> were enumerated and none matches
    /// airdrop, supply, crate, plane or cargo — and <c>TextAlert</c> carries a <i>string</i> rather
    /// than an id (docs, <c>GasAlerts</c>), so the server says this in its own words. The noun is
    /// the client's: string 1344 <i>"Military Crate"</i>, the display name of item 1501.
    /// </summary>
    public string InboundText { get; init; } = Rulings.LootGates.InboundText;

    /// <summary>The landed banner. As <see cref="InboundText"/>.</summary>
    public string LandedText { get; init; } = Rulings.LootGates.LandedText;

    // Route and bomb tuning is reconstruction, explicitly separate from client-derived identities.
    /// <summary>Fixed world altitude from an older 1087 supply-route capture; August parity unverified.</summary>
    public float PlaneAltitudeMetres { get; init; } = 867.593f;
    public float PlaneSpeedMetresPerSecond { get; init; } = 150f;
    public float FlightMarginMetres { get; init; } = 1_500f;
    public float MinimumTerrainClearanceMetres { get; init; } = 200f;
    /// <summary>Post-release climb shape is supported by July 2016 notes; the height is reconstruction.</summary>
    public float DepartureClimbMetres { get; init; } = 250f;
    /// <summary>The exported August playable terrain square extends from -4096 to +4096 on X/Z.</summary>
    public float PayloadMapHalfExtentMetres { get; init; } = 4096f;
    public bool BombsEnabled { get; init; } = true;
    /// <summary>Chance of a separate bomber run per supply schedule slot; not a measured retail probability.</summary>
    public double BombRunChance { get; init; } = 0.35;
    public long BombRunDelayMs { get; init; } = 60_000;
    public int BombsPerRun { get; init; } = 5;
    public float BombSpacingMetres { get; init; } = 35f;
    public float BombGravityMetresPerSecondSquared { get; init; } = 9.81f;
    /// <summary>August Models.txt 9372, Common_Props_Bomb.adr.</summary>
    public uint BombModelId { get; init; } = 9372;
    /// <summary>August composite 5328, PFX_Impact_Explosion_AirdropBomb_Default_10m.</summary>
    public uint BombExplosionEffectId { get; init; } = 5328;
    /// <summary>August composite 5179, SFX_Bomb_Falling.</summary>
    public uint BombFallingEffectId { get; init; } = 5179;
    /// <summary>Blast dimensions and damage are reconstruction. The effect name is not a damage table.</summary>
    public float BombBlastRadiusMetres { get; init; } = 10f;
    public float BombLethalRadiusMetres { get; init; } = 3f;
    public int BombPlayerDamage { get; init; } = 10_000;
    public int BombVehicleDamage { get; init; } = 100_000;

    /// <summary>Throws when a host has configured a schedule that cannot deliver a crate.</summary>
    public void Validate(string origin)
    {
        if (!float.IsFinite(MarkerRangeMetres) || MarkerRangeMetres <= 0
            || !float.IsFinite(MarkerHysteresisMetres) || MarkerHysteresisMetres < 0
            || !float.IsFinite(MarkerRangeMetres + MarkerHysteresisMetres))
            throw new InvalidDataException($"{origin}: invalid airdrop marker interest radius.");
        if (MinPlayersAlive < 0 || StopAtPlayersAlive < 0 || BombRunDelayMs < 0
            || BombsPerRun < 0 || BombPlayerDamage < 0 || BombVehicleDamage < 0)
            throw new InvalidDataException($"{origin}: airdrop player counts, bomb counts, damage and delays must not be negative.");
        if (!double.IsFinite(BombRunChance) || BombRunChance is < 0 or > 1)
            throw new InvalidDataException($"{origin}: bombRunChance must be a probability.");
        if (!float.IsFinite(PlaneAltitudeMetres)
            || !float.IsFinite(PlaneSpeedMetresPerSecond) || PlaneSpeedMetresPerSecond <= 0
            || !float.IsFinite(FlightMarginMetres) || FlightMarginMetres <= 0
            || !float.IsFinite(MinimumTerrainClearanceMetres) || MinimumTerrainClearanceMetres <= 0
            || !float.IsFinite(DepartureClimbMetres) || DepartureClimbMetres < 0
            || !float.IsFinite(PayloadMapHalfExtentMetres) || PayloadMapHalfExtentMetres <= 0
            || !float.IsFinite(BombSpacingMetres) || BombSpacingMetres < 0
            || !float.IsFinite(BombGravityMetresPerSecondSquared) || BombGravityMetresPerSecondSquared <= 0
            || !float.IsFinite(BombBlastRadiusMetres) || BombBlastRadiusMetres <= 0
            || !float.IsFinite(BombLethalRadiusMetres) || BombLethalRadiusMetres < 0
            || BombLethalRadiusMetres >= BombBlastRadiusMetres)
            throw new InvalidDataException($"{origin}: invalid airdrop route or bomb geometry.");

        if (FirstDropAtMs < 0 || DropIntervalMs <= 0)
        {
            throw new InvalidDataException(
                $"{origin}: airdrop schedule is first {FirstDropAtMs} ms / every {DropIntervalMs} ms; "
                + "a non-positive interval would deliver every crate in the same tick.");
        }

        if (MaxDropsPerMatch < 0 || DescentMs < 0 || UnlockMs < 0)
        {
            throw new InvalidDataException(
                $"{origin}: airdrop counts and delays must not be negative "
                + $"({MaxDropsPerMatch} drop(s), {DescentMs} ms descent, {UnlockMs} ms unlock).");
        }

        if (!double.IsFinite(RifleChance) || RifleChance is < 0.0 or > 1.0)
        {
            throw new InvalidDataException($"{origin}: airdrop rifleChance {RifleChance} is not a probability.");
        }

        if (!double.IsFinite(SafeZoneFraction) || SafeZoneFraction is <= 0.0 or > 1.0)
        {
            throw new InvalidDataException(
                $"{origin}: airdrop safeZoneFraction {SafeZoneFraction} is not a fraction of the circle.");
        }

        if (PoolDraws < 0)
        {
            throw new InvalidDataException($"{origin}: airdrop poolDraws is {PoolDraws}.");
        }

        if (!float.IsFinite(SpillRadiusMetres) || SpillRadiusMetres < 0f)
        {
            throw new InvalidDataException($"{origin}: airdrop spillRadiusMetres is {SpillRadiusMetres}.");
        }
    }
}
