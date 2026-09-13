namespace Cranberry.Zone.Movement;

/// <summary>
/// The <c>StatId.*</c> ordinals the wire uses, resolved from the client's own
/// <see cref="StringHashValues"/> table rather than re-typed as literals (docs/40 §10).
/// <para>
/// Provenance: <c>out\data_aug\StringHashToValue.txt</c> lines 465-512 map every <c>StatId.&lt;Name&gt;</c>
/// string to its ordinal, and <c>out\data_aug\CharacterStatDefinitions.txt</c> is the master table of
/// those ordinals (86 rows, ids 1..89 with gaps at 39/69/87). That sheet has **no value column**, so
/// the client carries no default for any stat — every number Cranberry sends is its own choice
/// (docs/40 §1.1, §4.1).
/// </para>
/// <para>
/// The client reaches these by name through <c>FUN_140c4bde0</c>, which hashes the name with exactly
/// the hash <see cref="StringHashValue.HashName"/> implements and then calls
/// <c>GetStat(entity, ordinal, default)</c> = <c>FUN_140c4beb0</c> (docs/40 §1.4).
/// </para>
/// </summary>
public static class CharacterStatId
{
    private const string Prefix = "StatId.";

    private static readonly Dictionary<string, uint> OrdinalsByName = BuildOrdinals();

    /// <summary><c>StatId.MaxMovementSpeed</c> = 2 — the base speed, in metres per second (docs/40 §4.2).</summary>
    public static readonly uint MaxMovementSpeed = Of("StatId.MaxMovementSpeed");

    /// <summary><c>StatId.BackpedalSpeedModifier</c> = 3 — the axis scalar <c>FUN_1411acb10</c> applies.</summary>
    public static readonly uint BackpedalSpeedModifier = Of("StatId.BackpedalSpeedModifier");

    /// <summary><c>StatId.CrouchSpeedModifier</c> = 4 — <c>FUN_1411acfa0</c>, branch <c>+0x37e7 &amp; 0x08</c>.</summary>
    public static readonly uint CrouchSpeedModifier = Of("StatId.CrouchSpeedModifier");

    /// <summary><c>StatId.SprintSpeedModifier</c> = 5 — the target of the blend at <c>char+0x43f8</c>.</summary>
    public static readonly uint SprintSpeedModifier = Of("StatId.SprintSpeedModifier");

    /// <summary><c>StatId.SwimSpeedModifier</c> = 6 — <c>FUN_1411acfa0</c>, branch <c>+0x37e3 &amp; 0x20</c>.</summary>
    public static readonly uint SwimSpeedModifier = Of("StatId.SwimSpeedModifier");

    /// <summary><c>StatId.StrafeSpeedModifier</c> = 7 — <c>FUN_1411ad740</c>; bypassed while sprinting.</summary>
    public static readonly uint StrafeSpeedModifier = Of("StatId.StrafeSpeedModifier");

    /// <summary><c>StatId.ProneSpeedModifier</c> = 67 — sent for completeness; prone is unreachable in KOTK (docs/40 §4.4).</summary>
    public static readonly uint ProneSpeedModifier = Of("StatId.ProneSpeedModifier");

    /// <summary><c>StatId.ProneRollSpeedModifier</c> = 84 — the strafe scalar when <c>+0x37e7 &amp; 5 == 5</c>.</summary>
    public static readonly uint ProneRollSpeedModifier = Of("StatId.ProneRollSpeedModifier");

    /// <summary><c>StatId.WaterSpeedModifier</c> = 85 — wading; the client's own fallback here is 0.5, not 1.0.</summary>
    public static readonly uint WaterSpeedModifier = Of("StatId.WaterSpeedModifier");

    /// <summary><c>StatId.WalkSpeedModifier</c> = 86 — branch <c>+0x37ed &amp; 0x01</c>, the walk toggle.</summary>
    public static readonly uint WalkSpeedModifier = Of("StatId.WalkSpeedModifier");

    /// <summary><c>StatId.SprintAccelerationTime</c> = 21, seconds (docs/40 §2.4, §3).</summary>
    public static readonly uint SprintAccelerationTime = Of("StatId.SprintAccelerationTime");

    /// <summary><c>StatId.SprintDecelerationTime</c> = 22, seconds.</summary>
    public static readonly uint SprintDecelerationTime = Of("StatId.SprintDecelerationTime");

    /// <summary><c>StatId.ForwardAccelerationTime</c> = 24, seconds.</summary>
    public static readonly uint ForwardAccelerationTime = Of("StatId.ForwardAccelerationTime");

    /// <summary><c>StatId.BackAccelerationTime</c> = 25, seconds.</summary>
    public static readonly uint BackAccelerationTime = Of("StatId.BackAccelerationTime");

    /// <summary><c>StatId.StrafeAccelerationTime</c> = 26, seconds.</summary>
    public static readonly uint StrafeAccelerationTime = Of("StatId.StrafeAccelerationTime");

    /// <summary><c>StatId.ForwardDecelerationTime</c> = 27, seconds.</summary>
    public static readonly uint ForwardDecelerationTime = Of("StatId.ForwardDecelerationTime");

    /// <summary><c>StatId.BackDecelerationTime</c> = 28, seconds.</summary>
    public static readonly uint BackDecelerationTime = Of("StatId.BackDecelerationTime");

    /// <summary><c>StatId.StrafeDecelerationTime</c> = 29, seconds.</summary>
    public static readonly uint StrafeDecelerationTime = Of("StatId.StrafeDecelerationTime");

    /// <summary>
    /// The eight stats flagged <c>SEND_TO_REMOTE_CLIENT</c> in <c>CharacterStatDefinitions.txt</c>
    /// (rows 21, 22, 24-29) — exactly the eight <c>FUN_140c60380</c> re-reads after every
    /// <c>0f 40</c> and pushes into same-named float animation parameters. Remote observers need
    /// them for locomotion blending, and this is the only channel that carries them (docs/40 §3).
    /// </summary>
    public static readonly IReadOnlyList<uint> RemoteAnimationTimes =
    [
        SprintAccelerationTime,
        SprintDecelerationTime,
        ForwardAccelerationTime,
        ForwardDecelerationTime,
        BackAccelerationTime,
        BackDecelerationTime,
        StrafeAccelerationTime,
        StrafeDecelerationTime,
    ];

    /// <summary>
    /// The ordinal of a <c>StatId.*</c> name, straight out of the client's table. Throws when the
    /// name is not a stat registration, which keeps a typo from silently writing stat 0.
    /// </summary>
    public static uint Of(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!OrdinalsByName.TryGetValue(name, out uint ordinal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(name),
                name,
                "Not a StatId.* entry of the client's StringHashToValue table.");
        }

        return ordinal;
    }

    /// <summary>True when <paramref name="name"/> is a known <c>StatId.*</c> registration.</summary>
    public static bool TryGet(string name, out uint ordinal) =>
        OrdinalsByName.TryGetValue(name, out ordinal);

    private static Dictionary<string, uint> BuildOrdinals()
    {
        Dictionary<string, uint> ordinals = new(StringComparer.Ordinal);
        foreach (StringHashValue entry in StringHashValues.Entries)
        {
            if (entry.Name.StartsWith(Prefix, StringComparison.Ordinal)
                && uint.TryParse(entry.Value, out uint ordinal))
            {
                ordinals[entry.Name] = ordinal;
            }
        }

        return ordinals;
    }
}
