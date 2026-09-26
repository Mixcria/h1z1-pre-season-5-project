namespace Cranberry.Zone.Combat;

/// <summary>One healing item under the current server policy; not an August retail specification.</summary>
/// <param name="Name">For the log and the cast bar.</param>
/// <param name="TotalHp">Hit points restored by the continuing heal, excluding completion.</param>
/// <param name="OverSeconds">Over how long, once it starts.</param>
/// <param name="ApplySeconds">The cast bar: how long the animation runs before any of it lands.</param>
/// <param name="StopsBleed">Whether successful application clears the current server bleed state.</param>
public readonly record struct Medical(
    string Name,
    double TotalHp,
    int OverSeconds,
    double ApplySeconds,
    bool StopsBleed)
{
    /// <summary>Immediate HP after a successful cast. The September 20 ROTK bandage
    /// capture shows +3 before independent +1 HP/s timers. The uncapped total was
    /// not observed; the existing continuing-heal budget is retained separately.</summary>
    public double CompletionHp { get; init; }
}

/// <summary>
/// <b>Bleeding and healing policy.</b> These pre-existing values include older research (D53)
/// and a September 20 other-game bandage completion observation. They are preserved for
/// compatibility with existing server behaviour, not verified August 2017 server rules.
/// <para>
/// The live August adapter publishes health and bleed severity through the derived
/// <c>ResourceEventBase 0x8d</c> paths. Successful medical completion stops the bleeding effect;
/// the model below supplies the healing values and the existing bleed policy.
/// </para>
/// <para>
/// The stock August HUD recognizes five named bleeding effect IDs, but this establishes the
/// presentation vocabulary, not original wound probabilities, damage rates or transitions.
/// The rates, cooldown and armour parity below are existing server policy. Native presentation
/// evidence and unresolved gameplay questions are in docs/restoration-medical-20260926.md.
/// </para>
/// <para>
/// <b>Armour halves bleeding by counting, not by rolling</b> - every second qualifying hit raises
/// severity while armour is worn. That is his choice and the reason a self-test is deterministic;
/// Cranberry's rule is seeded determinism anyway, so it crosses unchanged.
/// </para>
/// </summary>
public static class MedicalModel
{
    /// <summary>Current server severity cap; the client recognizes five named bleed tags.</summary>
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
            [24] = new("Field Bandage", 10, 10, 3, true) { CompletionHp = 3 },
            [1751] = new("Gauze", 10, 10, 3, true),
            [78] = new("Tactical First Aid Kit", 60, 60, 5, true),
            [2423] = new("Field Bandage", 10, 10, 3, true) { CompletionHp = 3 },
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
