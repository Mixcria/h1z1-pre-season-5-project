namespace Cranberry.Zone.Combat;

/// <summary>One healing item, as the owner's retail research states it.</summary>
/// <param name="Name">For the log and the cast bar.</param>
/// <param name="TotalHp">Hit points restored in total.</param>
/// <param name="OverSeconds">Over how long, once it starts.</param>
/// <param name="ApplySeconds">The cast bar: how long the animation runs before any of it lands.</param>
/// <param name="StopsBleed">Every one of them does; carried so a future item that does not can say so.</param>
public readonly record struct Medical(
    string Name,
    double TotalHp,
    int OverSeconds,
    double ApplySeconds,
    bool StopsBleed);

/// <summary>
/// <b>Bleeding and healing</b>, adopted from the owner's retail Pre-Season-3 research (D53) with one
/// honest subtraction at 1148.
/// <para>
/// <b>The subtraction.</b> He shows bleed severity on the HUD by pushing a <c>ResourceEvent</c> with
/// resource id 21. At 1148 <c>ResourceEventBase 0x8d</c> is undecoded (docs/20 §4), so Cranberry
/// implements bleeding as damage over time with <b>no HUD number</b>: the player will see health
/// falling with no cause shown until <c>0x8d</c> is read. That is stated rather than papered over -
/// inventing a packet to fill the gap is how a client gets desynchronised.
/// </para>
/// <para>
/// <b>Where the numbers come from, graded.</b> The item rows and the apply times are his research
/// (SECONDARY). Five severity states is PROVEN for this build window - the August-2017 note reads
/// "Bleed mechanic simplified to three states (from five)", so five is what this client ships with.
/// The drain rate is his loudest stated GUESS and is carried as one. The increment cooldown is a
/// guess with a concrete reason: a shotgun blast is 8-12 separate hit reports and without it one
/// trigger pull takes a player from no bleed to the maximum in a single frame.
/// </para>
/// <para>
/// <b>Armour halves bleeding by counting, not by rolling</b> - every second qualifying hit raises
/// severity while armour is worn. That is his choice and the reason a self-test is deterministic;
/// Cranberry's rule is seeded determinism anyway, so it crosses unchanged.
/// </para>
/// </summary>
public static class MedicalModel
{
    /// <summary>Severity states. PROVEN for this build window.</summary>
    public const int MaxBleedSeverity = 5;

    /// <summary>Health units lost per second per severity state. 0.5 HP/s/state - <b>the owner's
    /// stated GUESS</b>, and the loudest number he carries.</summary>
    public const int BleedUnitsPerSecondPerState = 50;

    /// <summary>Shortest gap between two severity increments.</summary>
    public const long BleedIncrementCooldownMs = 1_000;

    /// <summary>Bleed and heal both tick at 1 Hz.</summary>
    public const long TickIntervalMs = 1_000;

    /// <summary>Armour halves bleeding, counted by parity.</summary>
    public const bool ArmourHalvesBleed = true;

    /// <summary>
    /// Cast movement buffer, in metres from the application origin. Allows a short settling step
    /// when releasing movement and small adjustments, but does not reset with each position packet.
    /// Server policy tuned after the owner's 2026-09-06 playtest found 0.05m too sensitive.
    /// </summary>
    public const double CastCancelRadiusUnits = 0.75;

    /// <summary>The six healing items, keyed by item definition id.</summary>
    public static IReadOnlyDictionary<uint, Medical> Items { get; } =
        new Dictionary<uint, Medical>
        {
            [24] = new("Field Bandage", 10, 10, 3, true),
            [1751] = new("Gauze", 10, 10, 3, true),
            [78] = new("Tactical First Aid Kit", 60, 60, 5, true),
            [2423] = new("Field Bandage", 10, 10, 3, true),
            [2424] = new("Tactical First Aid Kit", 60, 60, 5, true),
            [3375] = new("Procoagulant", 5, 1, 1, true),
        };

    /// <summary>The row for an item, or null when it is not a medical.</summary>
    public static Medical? For(uint itemDefinitionId) =>
        Items.TryGetValue(itemDefinitionId, out Medical row) ? row : null;

    /// <summary>The cast bar's duration for an item, in milliseconds; 0 when it is not a medical.</summary>
    public static int ApplyMsFor(uint itemDefinitionId) =>
        For(itemDefinitionId) is { } row ? (int)Math.Round(row.ApplySeconds * 1000) : 0;

    /// <summary>Health units this item restores per 1 Hz tick.</summary>
    public static int HealUnitsPerTick(in Medical row) =>
        row.OverSeconds <= 0
            ? RetailBalance.Units(row.TotalHp)
            : Math.Max(1, RetailBalance.Units(row.TotalHp) / row.OverSeconds);

    /// <summary>Health units a bleed of this severity drains per second.</summary>
    public static int BleedUnitsPerSecond(int severity) =>
        Math.Clamp(severity, 0, MaxBleedSeverity) * BleedUnitsPerSecondPerState;
}

/// <summary>
/// One combatant's bleed and heal state, as plain fields so it can live inline on a player object.
/// The owner hangs the same fields off a <c>ConditionalWeakTable</c> keyed by session; Cranberry's
/// players are slot-indexed objects, so this costs no hashing and no allocation.
/// <para>
/// <b>Nothing here writes health.</b> Both pumps return the units to move and the caller queues
/// them, because <c>Match.ResolveDamage</c> is the only code in the server that writes health - which
/// is what guarantees that gas, a bullet and a bleed landing on the same tick produce one death, one
/// <c>ce 04</c> and one decrement of the alive count.
/// </para>
/// </summary>
public struct MedicalState
{
    /// <summary>0 to <see cref="MedicalModel.MaxBleedSeverity"/>.</summary>
    public byte Bleed;

    /// <summary>False until the first wound, so a match-clock zero is not "long ago".</summary>
    public bool Wounded;

    /// <summary>Server clock of the last severity increment; the cooldown reads this.</summary>
    public long LastWoundAtMs;

    /// <summary>A bleed tick is scheduled.</summary>
    public bool Bleeding;

    /// <summary>Next server clock at which the bleed drains.</summary>
    public long NextBleedTickMs;

    /// <summary>Health units left to give from the running heal.</summary>
    public int HealUnitsLeft;

    /// <summary>Health units the running heal gives per tick.</summary>
    public int HealPerTick;

    /// <summary>A heal tick is scheduled.</summary>
    public bool Healing;

    /// <summary>Next server clock at which the heal gives.</summary>
    public long NextHealTickMs;

    /// <summary>
    /// Opens or worsens a wound. Returns true when severity actually rose. Only bullets, blades and
    /// blasts call this - <b>gas does not bleed, falls do not bleed, walls do not bleed</b>.
    /// </summary>
    /// <param name="armour">The parity counter lives with the armour it belongs to.</param>
    /// <param name="wearingArmour">True while an unbroken plate is worn.</param>
    /// <param name="nowMs">Server clock.</param>
    public bool Wound(ref Armour armour, bool wearingArmour, long nowMs)
    {
        if (Wounded && nowMs - LastWoundAtMs < MedicalModel.BleedIncrementCooldownMs)
        {
            return false;
        }

        if (wearingArmour && MedicalModel.ArmourHalvesBleed)
        {
            armour.BleedParity++;

            if (armour.BleedParity % 2 != 0)
            {
                return false;
            }
        }

        byte before = Bleed;
        Bleed = (byte)Math.Min(MedicalModel.MaxBleedSeverity, Bleed + 1);
        LastWoundAtMs = nowMs;
        Wounded = true;

        if (Bleed == before)
        {
            return false;
        }

        if (!Bleeding)
        {
            NextBleedTickMs = nowMs + MedicalModel.TickIntervalMs;
            Bleeding = true;
        }

        return true;
    }

    /// <summary>Stops bleeding outright - what a bandage does.</summary>
    public void StopBleeding(ref Armour armour)
    {
        Bleed = 0;
        Wounded = false;
        LastWoundAtMs = 0;
        Bleeding = false;
        NextBleedTickMs = 0;
        armour.BleedParity = 0;
    }

    /// <summary>
    /// Starts a heal that has <b>already served its cast bar</b>. The first tick is immediate:
    /// otherwise a 3 s bandage is 3 s of bar followed by 3 s of nothing, which is the owner's own
    /// note and the reason his <c>BeginHealNow</c> exists.
    /// </summary>
    public void BeginHeal(in Medical row, long nowMs)
    {
        HealUnitsLeft = RetailBalance.Units(row.TotalHp);
        HealPerTick = MedicalModel.HealUnitsPerTick(row);
        NextHealTickMs = nowMs;
        Healing = true;
    }

    /// <summary>Cancels a running heal - a shot fired mid-bandage, or moving off the spot.</summary>
    public void CancelHeal()
    {
        HealUnitsLeft = 0;
        HealPerTick = 0;
        NextHealTickMs = 0;
        Healing = false;
    }

    /// <summary>
    /// Health units the bleed owes at this instant, 0 when nothing is due. Advances the schedule,
    /// so the caller must queue whatever comes back.
    /// </summary>
    public int PumpBleed(long nowMs)
    {
        if (Bleed == 0 || !Bleeding || nowMs < NextBleedTickMs)
        {
            return 0;
        }

        NextBleedTickMs = nowMs + MedicalModel.TickIntervalMs;
        return MedicalModel.BleedUnitsPerSecond(Bleed);
    }

    /// <summary>
    /// Health units the running heal gives at this instant, 0 when nothing is due. Advances the
    /// schedule and the remaining budget.
    /// </summary>
    public int PumpHeal(long nowMs)
    {
        if (HealUnitsLeft <= 0 || !Healing || nowMs < NextHealTickMs)
        {
            return 0;
        }

        int units = Math.Min(HealPerTick, HealUnitsLeft);
        HealUnitsLeft -= units;
        Healing = HealUnitsLeft > 0;
        NextHealTickMs = Healing ? nowMs + MedicalModel.TickIntervalMs : 0;
        return units;
    }

    /// <summary>Clears everything - a respawn, or a match reset.</summary>
    public void Reset()
    {
        Bleed = 0;
        Wounded = false;
        LastWoundAtMs = 0;
        Bleeding = false;
        NextBleedTickMs = 0;
        CancelHeal();
    }
}
