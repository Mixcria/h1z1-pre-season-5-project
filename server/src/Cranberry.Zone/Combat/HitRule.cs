namespace Cranberry.Zone.Combat;

/// <summary>What one projectile did to one victim.</summary>
/// <param name="DamageUnits">Health units to take off, after armour.</param>
/// <param name="Headshot">Hit the head.</param>
/// <param name="DamagedArmour">Armour or a helmet absorbed this hit.</param>
/// <param name="BrokeArmour">The absorbing protection was destroyed.</param>
/// <param name="Bleeds">A wound was opened. Bullets, blades and blasts; never gas, falls or walls.</param>
/// <param name="Why">One line for the log, so a refusal and an absorb are never confused.</param>
public readonly record struct HitOutcome(
    int DamageUnits,
    bool Headshot,
    bool DamagedArmour,
    bool BrokeArmour,
    bool Bleeds,
    string Why);

/// <summary>
/// <b>The retail hit rule</b>, adopted whole from the owner's own research (D53) and re-expressed
/// against Cranberry's own armour struct. Resolves one projectile against one victim and
/// <b>consumes armour</b>, so it must be called exactly once per projectile - after every refusal
/// gate and immediately before the fire hint is spent.
/// <para>The ladder, in order:</para>
/// <list type="number">
/// <item><b>Head + hunting rifle</b> - lethal, helmet or not.</item>
/// <item><b>Head + rifle/pistol + no intact helmet</b> - lethal.</item>
/// <item><b>Head + rifle/pistol + intact helmet</b> - 0 damage, the helmet is destroyed. The next
/// one kills: the retail two-tap, and Daybreak's own stated design.</item>
/// <item><b>Head + shotgun</b> - treated as a body pellet. One pellet of eight is not a "headshot"
/// in the two-tap sense, and no retail source says otherwise.</item>
/// <item><b>Body + armour with absorbs left</b> - 0 damage, one absorb spent; at 0 the armour is
/// destroyed.</item>
/// <item>Otherwise - the table's body damage times <see cref="RetailBalance.LimbMultiplier"/>.</item>
/// </list>
/// <para>
/// <b>The head set is HEAD / GLASSES / NECK, matched case-insensitively, and nothing else.</b> The
/// owner carries an 18-name bone vocabulary alongside it; that did not cross (docs/81 §4c.2). Its
/// provenance is excluded, it changes no damage, and every <c>hitLocation</c> string he has ever
/// logged came from his own self-test rather than from a client - so carrying it would hide the
/// first real string the August client sends behind a friendly label. <see cref="IsHead"/> answers
/// the only question the damage model asks, and the caller logs the string verbatim.
/// </para>
/// </summary>
public static class HitRule
{
    /// <summary>
    /// True when this hit location counts as a head. Case-insensitive, ordinal - the owner's own
    /// choice, and it cannot be wrong in either direction for a three-name set.
    /// </summary>
    public static bool IsHead(ReadOnlySpan<char> hitLocation) =>
        hitLocation.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
        || hitLocation.Equals("GLASSES", StringComparison.OrdinalIgnoreCase)
        || hitLocation.Equals("NECK", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves one projectile. <paramref name="armour"/> is read <b>and written</b>: a consumed
    /// plate or a broken helmet is recorded on the way out.
    /// </summary>
    /// <param name="armour">The victim's armour state, mutated in place.</param>
    /// <param name="wornBodyArmourItemId">What is in the victim's armour slot right now; 0 for none.</param>
    /// <param name="wornHelmetItemId">What is in the victim's head slot right now; 0 for none.</param>
    /// <param name="weaponDefinitionId">The firing weapon's <c>WEAPON_ID</c>.</param>
    /// <param name="hitLocation">The client's own string, verbatim. Empty is a body hit.</param>
    /// <param name="distance">Shooter-to-victim distance in world units; only the shotgun reads it.</param>
    /// <param name="unmappedUnits">Body damage for a weapon with no retail row.</param>
    public static HitOutcome Resolve(
        ref Armour armour,
        uint wornBodyArmourItemId,
        uint wornHelmetItemId,
        uint weaponDefinitionId,
        ReadOnlySpan<char> hitLocation,
        double distance,
        int unmappedUnits)
    {
        RetailWeapon? row = RetailBalance.RowFor(weaponDefinitionId);
        WeaponClass weaponClass = row?.Class ?? WeaponClass.Unknown;
        string name = row?.Name ?? $"weapon {weaponDefinitionId}";
        int body = RetailBalance.BodyDamageUnits(weaponDefinitionId, distance, unmappedUnits);
        bool head = IsHead(hitLocation);

        if (head && weaponClass == WeaponClass.HuntingRifle)
        {
            return new HitOutcome(
                RetailBalance.FullHealthUnits, true, false, false, true,
                $"{name} headshot - lethal through a helmet");
        }

        if (head && weaponClass != WeaponClass.Shotgun)
        {
            if (!armour.WearingHelmet(wornHelmetItemId))
            {
                return new HitOutcome(
                    RetailBalance.FullHealthUnits, true, false, false, true,
                    $"{name} headshot, no helmet - lethal");
            }

            armour.HelmetIntact = false;
            return new HitOutcome(
                0, true, true, true, false,
                $"{name} headshot ABSORBED by the helmet, which is destroyed - the retail two-tap");
        }

        ArmourTier tier = armour.Wearing(wornBodyArmourItemId);

        if (!head && tier != ArmourTier.None && armour.AbsorbsLeft > 0)
        {
            armour.AbsorbsLeft--;
            bool broke = armour.AbsorbsLeft == 0;

            return new HitOutcome(
                0, false, true, broke, false,
                $"body shot ABSORBED by {tier} armour"
                + (broke ? ", which is destroyed" : $" ({armour.AbsorbsLeft} absorb(s) left)"));
        }

        int damage = (int)Math.Round(body * (head ? 1.0 : RetailBalance.LimbMultiplier));

        return new HitOutcome(
            damage, head, false, false, true,
            $"{name} {(head ? "head pellet" : "body")} {damage / (double)RetailBalance.UnitsPerHp:0.#} HP"
            + (weaponClass == WeaponClass.Shotgun ? $" at {distance:0.#} u" : string.Empty)
            + (row is null ? " (UNMAPPED weapon - Cranberry default)" : string.Empty));
    }
}
