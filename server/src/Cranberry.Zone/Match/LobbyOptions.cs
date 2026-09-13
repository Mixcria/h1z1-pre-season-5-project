namespace Cranberry.Zone.Match;

/// <summary>
/// The pre-game lobby at Fort Destiny: how long the player stands in it, when it arms, what the
/// green HUD widget is labelled, and where the "Match starts in N seconds." banners fall.
///
/// <para>
/// <b>D248 - the lobby is 120 s, not 20 s.</b> Cranberry shipped <c>LobbyCountdownMs = 20000</c>
/// and armed it a blind 15,000 ms after the zoning burst, so the player got about 27 s in Fort
/// Destiny. That is not enough time to open a Tab page, read a payout table and commit an ante -
/// which is the whole of what Fort Destiny is for (client locale <c>4162258810</c>: a Bounty is
/// earned "by <b>Backing your Match in Fort Destiny</b>"). The owner's own Z1 server carries the
/// retail row and it is adopted here under D53:
/// <c>C:\Z1\Server\Zone\ZoneRetailSpawn.cs:584-600</c> -
/// <c>LobbySeconds: 120, MinPlayers: 1, BannerSeconds: [60, 30, 10]</c>, with the labels 13198
/// <c>Match.WaitingForPlayers</c> and 13356 <c>SyncTeleport.StartingMatch</c>. Z1 grades the
/// numbers GUESS and says so plainly - no retail capture of a lobby exists on this disk - so this
/// is the owner's design adopted as his, not a measured retail fact.
/// </para>
/// <para>
/// <b>D249 - it arms at ARRIVAL, not on a blind timer.</b> <see cref="ArmMs"/> defaults to 0,
/// which means "arm on the client's own <c>ClientFinishedLoading</c> in the Zoning step" - the
/// packet that says the Fort Destiny world is built (<c>ZoneService</c>'s own words: "the single
/// strongest 'the world is built' signal on the wire"). The old behaviour is one number away: any
/// positive value restores the blind <c>Later(connection, ArmMs, …)</c> from the zoning burst.
/// </para>
/// <para>
/// <b>D250 - <see cref="MinPlayers"/> is 1.</b> Z1's own retail row says 1 as well, and solo dev
/// play is the only way this server is exercised today. Above the minimum the widget counts down
/// with 13356; below it, it holds on 13198 "Waiting for players...".
/// </para>
/// </summary>
public sealed record LobbyOptions
{
    /// <summary>Environment switch for <see cref="CountdownMs"/> (D248).</summary>
    public const string CountdownVariable = "CRANBERRY_LOBBY_MS";

    /// <summary>Environment switch for <see cref="ArmMs"/> (D249).</summary>
    public const string ArmVariable = "CRANBERRY_LOBBY_ARM_MS";

    /// <summary>Environment switch for <see cref="MinPlayers"/> (D250).</summary>
    public const string MinPlayersVariable = "CRANBERRY_LOBBY_MIN_PLAYERS";

    /// <summary>Environment switch for <see cref="Banners"/> (D251).</summary>
    public const string BannersVariable = "CRANBERRY_LOBBY_BANNERS";

    /// <summary>Environment switch for <see cref="Labels"/> (D250).</summary>
    public const string LabelsVariable = "CRANBERRY_LOBBY_LABELS";

    /// <summary>
    /// The banner steps, in seconds, adopted from Z1's <c>BannerSeconds: [60, 30, 10]</c> (D53).
    /// Descending, and a step longer than the lobby itself is skipped rather than fired late -
    /// the same rule Z1's own <c>ZoneMatchFlow.cs:1067-1072</c> states.
    /// </summary>
    public static IReadOnlyList<uint> RetailBannerSeconds { get; } = [60u, 30u, 10u];

    /// <summary>
    /// How long the player stands in Fort Destiny before <c>ce 16 StartMatch</c>. 120,000 ms is
    /// Z1's retail row (D248/D53).
    /// </summary>
    public uint CountdownMs { get; init; } = 120_000u;

    /// <summary>
    /// Milliseconds after the match-zoning burst before the lobby HUD is sent. <b>0 - the default -
    /// means "at the client's own arrival"</b>: the <c>ClientFinishedLoading</c> it sends once the
    /// Fort Destiny world is built (D249). A positive value restores the old blind timer.
    /// </summary>
    public int ArmMs { get; init; }

    /// <summary>
    /// The safety net behind <see cref="ArmMs"/> = 0: if the client never sends
    /// <c>ClientFinishedLoading</c> the lobby still arms this long after the zoning burst. It is
    /// the exact number the blind arm used before D249, so a session that would have worked before
    /// still works - the arrival arm only ever makes the lobby start SOONER. Whichever fires first
    /// wins; the second is a no-op on <c>GatewaySessionState.LobbyHudSent</c>.
    /// </summary>
    public int FallbackArmMs { get; init; } = 15_000;

    /// <summary>
    /// Population at or above which the countdown runs. Below it the widget holds the
    /// "Waiting for players..." label and the clock does not start (D250).
    /// </summary>
    public int MinPlayers { get; init; } = 1;

    /// <summary>Send the <c>ce 14</c> "Match starts in N seconds." banners (D251).</summary>
    public bool Banners { get; init; } = true;

    /// <summary>
    /// Label the countdown widget from <c>AugustStrings.HudLabels</c> - 13198 below the minimum
    /// population, 13356 while counting. Off leaves the single 13356 label the server always sent.
    /// </summary>
    public bool Labels { get; init; } = true;

    /// <summary>The banner steps this lobby will fire, longest first, none longer than itself.</summary>
    public IReadOnlyList<uint> BannerSeconds { get; init; } = RetailBannerSeconds;

    /// <summary>The shipped defaults.</summary>
    public static LobbyOptions Default { get; } = new();

    /// <summary><see cref="CountdownMs"/> as whole seconds, for the banner arithmetic.</summary>
    public uint CountdownSeconds => (CountdownMs + 999u) / 1000u;

    /// <summary>
    /// The banner steps that actually fit inside a lobby of <paramref name="lobbyMs"/>, longest
    /// first. A <c>CRANBERRY_LOBBY_MS=6000</c> must not announce "60 seconds" and "30 seconds" in
    /// the same tick, which is the exact failure Z1's own comment describes
    /// (<c>ZoneMatchFlow.cs:1067-1072</c>).
    /// <para>
    /// The length is a parameter rather than <see cref="CountdownMs"/> because the value the
    /// service actually runs the lobby for lives on <c>ZoneOptions.LobbyCountdownMs</c>, and a test
    /// that shortens the lobby to 1 ms must not still be handed a 60-second banner.
    /// </para>
    /// </summary>
    public IEnumerable<uint> ApplicableBannerSeconds(uint lobbyMs)
    {
        if (!Banners)
        {
            yield break;
        }

        uint lobby = (lobbyMs + 999u) / 1000u;
        foreach (uint at in BannerSeconds.OrderByDescending(second => second))
        {
            if (at > 0 && at < lobby)
            {
                yield return at;
            }
        }
    }

    /// <summary>Reads the switches. Only a parsable value moves one; a typo keeps the default.</summary>
    public static LobbyOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= System.Environment.GetEnvironmentVariable;
        return Default with
        {
            CountdownMs = Number(read, CountdownVariable, Default.CountdownMs),
            ArmMs = (int)Number(read, ArmVariable, (uint)Default.ArmMs),
            MinPlayers = (int)Number(read, MinPlayersVariable, (uint)Default.MinPlayers),
            Banners = Switch(read, BannersVariable, @default: true),
            Labels = Switch(read, LabelsVariable, @default: true),
        };
    }

    /// <summary>The boot line: what the owner will actually stand in.</summary>
    public string Describe() =>
        $"lobby: {CountdownMs / 1000}s at Fort Destiny (D248, Z1 retail row under D53), "
        + $"arms {(ArmMs <= 0 ? $"at the client's own ClientFinishedLoading (fallback {FallbackArmMs} ms)" : ArmMs + " ms after zoning")}, "
        + $"minPlayers={MinPlayers}, labels {(Labels ? "13198/13356" : "off")}, "
        + $"banners {(Banners ? "ce 14 at " + string.Join("/", ApplicableBannerSeconds(CountdownMs)) + " s" : "off")} "
        + $"(CRANBERRY_LOBBY_MS / _ARM_MS / _MIN_PLAYERS / _LABELS / _BANNERS)";

    private static uint Number(Func<string, string?> read, string name, uint @default) =>
        uint.TryParse(read(name)?.Trim(), out uint value) ? value : @default;

    private static bool Switch(Func<string, string?> read, string name, bool @default) =>
        read(name) switch
        {
            "1" => true,
            "0" => false,
            _ => @default,
        };
}
