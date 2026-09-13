using System.Collections.Frozen;
using Cranberry.Zone.Generated;

namespace Cranberry.Zone.Lighting;

/// <summary>
/// Never hand this client build a composite-effect id it has no definition for. The rule is the
/// owner's own (his <c>Z1\Server\Zone\ZoneEffects.cs</c>, round 29); the mechanism here is
/// Cranberry's and the id set is this build's own table, because 1087's ids mean nothing at 1148
/// (D53 - the value crosses, the file does not).
/// </summary>
/// <remarks>
/// <para>
/// <b>An ALLOW list, over the client's own table (lane 2B, docs/104).</b> Until 2026-09-02 this
/// was a deny list, on the stated grounds that "this build ships no composite-effect datasheet -
/// the 2017 extraction has no <c>Actor*Definitions.xml</c> at all". That was simply wrong: the
/// client's own <c>ActorCompositeEffectDefinitions.xml</c> had been extracted since 2026-08-29
/// (S8 section 6.4). It is now parsed into <see cref="AugustEffectCatalog"/> -
/// <see cref="AugustEffectCatalog.Count"/> definitions, ids
/// <see cref="AugustEffectCatalog.MinId"/>..<see cref="AugustEffectCatalog.MaxId"/> - and the gate
/// admits exactly what that table defines. The deny list survives as <em>evidence</em>
/// (<see cref="DeniedInThisBuild"/>): id 0 is refused twice over, once because the client reported
/// it and once because the table has no such row.
/// </para>
/// <para>
/// <b>CLIENT-PROVEN here.</b> <c>C:\Aug2017\Client\Logs\H1Z1 KOTK PlayClient (Live).log</c>, four
/// consecutive lines at 2026-08-30 19:16:31 (21 s after <c>GAMESTATE_LOADINGSCREEN</c> became
/// <c>GAMESTATE_INGAME</c>), all <c>Id #0</c>. A grep of every August client log finds <b>only</b>
/// id 0 - none of Z1's 1087 ids (5836 / 5840 / 5904) appear. S8 section 6 later traced those four
/// lines to the client's own parachute touchdown code reading <c>Vehicles.txt</c> row 13, every one
/// of whose 28 <c>*_EFFECT</c> columns is 0 - so they were never the server's, and this gate has
/// always guarded a path they did not take. It still earns its place: it is what stops the first
/// <c>PlayCompositeEffect</c> sender shipping an id this build cannot resolve.
/// </para>
/// <para>
/// <b>What this is NOT for.</b> It is not for fixed-width table or record columns. Writing 0 into a
/// <c>u32</c> effect column of a record the client reads is the correct "no effect" value - Z1's own
/// table sanitiser rewrites unresolvable ids <em>to</em> 0 for exactly that reason. The gate belongs
/// at a site that <em>chooses</em> to queue an effect, i.e. a <c>PlayCompositeEffect</c> (0xd3) or
/// <c>PlayWorldCompositeEffect</c> (<c>0f 43</c>) sender. <b>Cranberry has no such sender yet</b> -
/// a grep of <c>src</c> finds no non-zero composite-effect id anywhere - so nothing calls this
/// today; it exists so the first lane that writes one has the rule, the table, the evidence and the
/// switch already in place rather than rediscovering them. See docs/82 section 6, docs/104.
/// </para>
/// <para>
/// <b>Optimised over Z1's version.</b> Z1 does a <c>Dictionary</c> upsert and a LINQ
/// <c>.First()</c> scan of its evidence table on <em>every</em> suppressed call. This probes a
/// <see cref="FrozenSet{T}"/> and a frozen catalogue, allocates nothing on the allowed path, and
/// reports each id once.
/// </para>
/// </remarks>
public sealed class CompositeEffectGate
{
    /// <summary>
    /// Ids this client build has itself reported it cannot resolve, each with the evidence that put
    /// it here. <b>Add one row per new "missing effect definition for Id #N" a click-test log
    /// shows</b> - and never on any other authority. This is the evidence trail, not the rule: the
    /// rule is <see cref="AugustEffectCatalog"/>, and every id on this list must also be absent
    /// from it (<c>ClientTableMigrationTests</c> asserts that).
    /// </summary>
    public static readonly IReadOnlyList<CompositeEffectDenial> DeniedInThisBuild =
    [
        new(
            0,
            "the effect table's own no-effect sentinel - never a legal id at a queue call",
            "Client\\Logs\\H1Z1 KOTK PlayClient (Live).log, 2026-08-30 19:16:31, 4 x "
            + "\"Failed to QueueCompositeEffectAtLocation due to missing effect definition for Id #0!\""),
    ];

    private static readonly FrozenSet<uint> DeniedIds =
        DeniedInThisBuild.Select(row => row.Id).ToFrozenSet();

    private readonly HashSet<uint> reported = [];
    private readonly Dictionary<uint, int> suppressed = [];
    private readonly Action<string>? log;

    /// <summary>A gate over <see cref="DeniedInThisBuild"/>.</summary>
    /// <param name="enabled">
    /// <see cref="ZoneOptions.GateCompositeEffectIds"/>. False ships every id unaltered, which is
    /// the bisect row and the byte-parity row.
    /// </param>
    /// <param name="log">
    /// Where the once-per-id suppression line goes. Null keeps the gate silent, which is what a unit
    /// test wants.
    /// </param>
    public CompositeEffectGate(bool enabled, Action<string>? log = null)
    {
        Enabled = enabled;
        this.log = log;
    }

    /// <summary>The shipped gate: <see cref="DeniedInThisBuild"/>, enabled, silent.</summary>
    public static CompositeEffectGate Default { get; } = new(enabled: true);

    /// <summary>Whether this gate suppresses anything at all.</summary>
    public bool Enabled { get; }

    /// <summary>How many sends this gate has stopped, over every id.</summary>
    public int SuppressedCount
    {
        get
        {
            lock (suppressed)
            {
                int total = 0;
                foreach (int count in suppressed.Values)
                {
                    total += count;
                }

                return total;
            }
        }
    }

    /// <summary>Whether an id is on this build's deny list, regardless of any gate's switch.</summary>
    public static bool IsDenied(uint effectId) => DeniedIds.Contains(effectId);

    /// <summary>
    /// Whether this client build has a definition for that id - the allow list itself, independent
    /// of any gate's switch.
    /// </summary>
    public static bool IsDefined(uint effectId) =>
        !DeniedIds.Contains(effectId) && AugustEffectCatalog.Contains(effectId);

    /// <summary>The evidence row for a denied id.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The id is not on the deny list.</exception>
    public static CompositeEffectDenial DenialFor(uint effectId)
    {
        foreach (CompositeEffectDenial row in DeniedInThisBuild)
        {
            if (row.Id == effectId)
            {
                return row;
            }
        }

        throw new ArgumentOutOfRangeException(
            nameof(effectId), effectId, "That id is not on this build's composite-effect deny list.");
    }

    /// <summary>May this effect id go to the client?</summary>
    /// <param name="effectId">The composite-effect id a sender is about to write.</param>
    /// <param name="origin">
    /// Where the id came from, named well enough to fix it: the gate is a net, not a cure, and an id
    /// this build cannot resolve is a bug at the origin.
    /// </param>
    /// <returns>
    /// True to send it. False to drop the send entirely - <b>not</b> to substitute 0, which is
    /// itself denied.
    /// </returns>
    public bool Allowed(uint effectId, string origin)
    {
        if (!Enabled || IsDefined(effectId))
        {
            return true;
        }

        bool first;
        lock (suppressed)
        {
            suppressed[effectId] = suppressed.TryGetValue(effectId, out int seen) ? seen + 1 : 1;
            first = reported.Add(effectId);
        }

        if (first && log is not null)
        {
            string why = DeniedIds.Contains(effectId)
                ? $"({DenialFor(effectId).Meaning}). Evidence: {DenialFor(effectId).Evidence}."
                : $"- it is not one of the {AugustEffectCatalog.Count} definitions in this build's "
                  + $"ActorCompositeEffectDefinitions.xml (ids {AugustEffectCatalog.MinId}"
                  + $"..{AugustEffectCatalog.MaxId}).";
            log(
                $"[FX] suppressed composite effect {effectId} from {origin} - this client build has "
                + $"no definition for it {why} Further suppressions of this id are silent; fix the "
                + "origin.");
        }

        return false;
    }

    /// <summary>How many times one id has been suppressed by this gate.</summary>
    public int SuppressedCountFor(uint effectId)
    {
        lock (suppressed)
        {
            return suppressed.TryGetValue(effectId, out int seen) ? seen : 0;
        }
    }

    /// <summary>The host's start-up line: what is allowed, and what is denied on whose evidence.</summary>
    public string Describe() =>
        Enabled
            ? $"composite-effect gate ON; allow list = the client's own {AugustEffectCatalog.Count} "
              + $"definitions (ids {AugustEffectCatalog.MinId}..{AugustEffectCatalog.MaxId}); "
              + "additionally denied: "
              + string.Join(", ", DeniedInThisBuild.Select(row => $"{row.Id} ({row.Meaning})"))
            : "composite-effect gate OFF (bisect): every id ships unaltered";
}

/// <summary>One row of <see cref="CompositeEffectGate.DeniedInThisBuild"/>.</summary>
/// <param name="Id">The composite-effect id the client could not resolve.</param>
/// <param name="Meaning">What the id is, as far as it is known on this build.</param>
/// <param name="Evidence">The client-originated line that put it on the list (D29).</param>
public readonly record struct CompositeEffectDenial(uint Id, string Meaning, string Evidence);
