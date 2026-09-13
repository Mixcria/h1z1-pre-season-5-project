using System.Collections.Frozen;
using Cranberry.Zone.Generated;

namespace Cranberry.Zone.Weapons;

/// <summary>What a throwable does when it goes off (docs/120 §5).</summary>
public enum ThrowableKind : byte
{
    /// <summary>65 M67 Frag: one explosion, radius damage.</summary>
    Frag,

    /// <summary>2235 Stun: one flash, no damage.</summary>
    Stun,

    /// <summary>2236 Smoke: a lingering cloud, no damage.</summary>
    Smoke,

    /// <summary>2237 Gas: a lingering cloud that hurts, like the match gas.</summary>
    Gas,

    /// <summary>14 Molotov: breaks on contact, burns a patch.</summary>
    Molotov,
}

/// <summary>
/// One throwable the August client ships, and everything Cranberry needs to make it fly, bounce
/// and go off (docs/120).
/// </summary>
/// <param name="ItemDefinitionId">The <c>ClientItemDefinitions</c> row. [P]</param>
/// <param name="Kind">What happens at the end of the fuse.</param>
/// <param name="Name">The client's own name, for the log.</param>
/// <param name="FireGroupId">
/// The datasheet's <c>FIRE_GROUP_ID</c> for four of the five [P]; the frag's is
/// <see cref="Rulings.Throwables.FragFireGroupId"/> (D300) because its row says 0.
/// </param>
/// <param name="ProjectileId">
/// The <c>ProjectileDefinitions</c> record the throw spawns - <see cref="Rulings.Throwables.ProjectileIdBase"/>
/// + the item id (D301). No August sheet names a grenade projectile.
/// </param>
/// <param name="ProjectileModel">
/// <c>MODEL_FILE_NAME</c> (<c>rec+0x28</c>): the client's own in-flight mesh, present in
/// <c>pack-index-aug.tsv</c>. [P]
/// </param>
/// <param name="ActorModelId">
/// The numeric <c>Models.txt</c> id of that same mesh (D315): 9443 frag, 9478 stun, 9468 smoke,
/// 9480 gas, 9440 molotov. All five checked present in <b>August's own</b> <c>Models.txt</c> with a
/// <c>MODEL_FILE_NAME</c> byte-identical to <paramref name="ProjectileModel"/>, which is what makes
/// adopting the owner's Z1 numbers a lookup rather than a guess. It is what an
/// <c>AddLightweightNpc</c> takes as <c>actorModelId</c> - the form Z1 spawns a grenade in for
/// everyone inside <see cref="Rulings.Throwables.SpawnRangeUnits"/>. [P]
/// </param>
/// <param name="FuseSeconds">
/// <c>LIFESPAN</c> (<c>rec+0x78</c>). With <see cref="DetonatesOnLifespan"/> the client's actor
/// tick expires the projectile on it and reports the detonation (<c>82 19</c>). RULING (D306).
/// </param>
/// <param name="DetonatesOnLifespan"><c>LIFESPAN_DETONATE</c>, <c>rec+0x20</c> bit <c>0x40</c>. [P bit]</param>
/// <param name="DetonatesOnContact"><c>DETONATE_ON_CONTACT</c>, <c>rec+0x20</c> bit <c>0x04</c>. [P bit]</param>
/// <param name="EffectId">The one-shot composite the detonation plays (<c>0f 43</c>). [P id]</param>
/// <param name="CloudEffectId">The looping composite a lingering cloud carries as an effect tag, or 0.</param>
/// <param name="Radius">The damage radius in world units; 0 for a throwable that hurts nobody.</param>
/// <param name="DamageHp">Frag: hit points at the centre. Clouds: hit points per tick.</param>
/// <param name="LingerSeconds">How long a cloud stays, or 0 for a one-shot.</param>
public readonly record struct ThrowableFact(
    uint ItemDefinitionId,
    ThrowableKind Kind,
    string Name,
    uint FireGroupId,
    uint ProjectileId,
    string ProjectileModel,
    uint ActorModelId,
    float FuseSeconds,
    bool DetonatesOnLifespan,
    bool DetonatesOnContact,
    uint EffectId,
    uint CloudEffectId,
    float Radius,
    int DamageHp,
    int LingerSeconds)
{
    /// <summary>A cloud (gas, smoke, fire) rather than a one-shot bang.</summary>
    public bool Lingers => LingerSeconds > 0;

    /// <summary>Does anything inside <see cref="Radius"/> get hurt.</summary>
    public bool Hurts => Radius > 0f && DamageHp > 0;

    public bool ContactTimedActivation => Kind is ThrowableKind.Smoke or ThrowableKind.Gas or ThrowableKind.Stun;

    // A shorter activation fuse must not destroy the flying actor early. These preserve
    // the existing August flight windows; contact supplies the early activation position.
    public float ProjectileLifespanSeconds => Kind switch
    {
        ThrowableKind.Smoke => MathF.Max(3f, FuseSeconds),
        ThrowableKind.Gas => MathF.Max(5f, FuseSeconds),
        ThrowableKind.Stun => MathF.Max(2f, FuseSeconds),
        _ => FuseSeconds,
    };
}

/// <summary>
/// <b>The five throwables of the August client</b> (<c>ITEM_CLASS 25078</c>,
/// <c>ACTIVATABLE_ABILITY_ID 1111507</c>), as one table (docs/120 §3, §5).
/// <para>
/// <b>What is the client's own [P]:</b> the item ids, the names, four of the five fire groups
/// (7, 42, 43, 44), the in-flight models, the composite-effect ids, the flag bits and the field
/// offsets. <b>What is ruled:</b> the frag's fire group (D300), the projectile ids and the flight
/// numbers (D301), the fuses (D306) and every damage number (D308) - all declared in
/// <c>rulings/throwables.json</c> and read back through <see cref="Rulings.Throwables"/>.
/// </para>
/// <para>
/// <b>The owner's Z1 table IS here now (D312 / D315, docs/125 §4).</b> docs/120 §5.0 excluded
/// <c>C:\Z1\Server\Zone\ZoneCombatThrowables.cs</c> whole, because its fuses, effects and radii
/// cite <c>projectileentity.ts</c> line by line; the owner's 2026-09-04 ruling - <i>"override that
/// and use it"</i> - lifts that. So the fuses (3000 / 2000 / 1000 / 5000 / 1500 ms), the effect ids
/// (5301, 4658, 2333, 2335, 5308), the model ids, the 5-unit blast, the 7-unit gas cloud, the
/// 10,000-bar damage and its inverse-distance fall-off are his. <b>Every id was checked against
/// August's own tables before it shipped</b>: all five effect ids are in
/// <c>ActorCompositeEffectDefinitions.xml</c> and all five model ids are in <c>Models.txt</c>, with
/// the names this table already carried. His two mechanisms still cross (D306): a grenade ALWAYS
/// goes off, and a detonation at the throw point never hurts the thrower.
/// </para>
/// </summary>
public static class AugustThrowables
{
    /// <summary><c>ITEM_CLASS 25078</c>, the client's throwable class. [P]</summary>
    public const uint ItemClass = AugustWeaponTable.ThrowableItemClass;

    /// <summary><c>ACTIVATABLE_ABILITY_ID 1111507</c> on all five rows. [P]</summary>
    public const uint ActivatableAbilityId = 1_111_507;

    /// <summary>
    /// <c>FLIGHT_TYPE 9</c> - the physics flight model (<c>FUN_140f09e30:116-134</c> allocates the
    /// 336-byte <c>ProjectileCallbackPhysics</c> for it), the one that bounces. [P]
    /// </summary>
    public const byte PhysicsFlightType = 9;

    /// <summary>
    /// The captured grenade modes' RANGE, in world units. This is an aim distance from
    /// the camera, not the grenade's maximum travel distance (docs/throwable-launch-20260906.md).
    /// Source: friendWeaponDefinitions.bin modes 10/297, 56/293, 59/301, 125/300, 63/299.
    /// </summary>
    public const float AimRange = 100f;

    /// <summary>The same ten captured modes' LAUNCH_PITCH_ADDITIVE_DEGREES.</summary>
    public const float LaunchPitchAdditiveDegrees = 8f;

    private static readonly ThrowableFact[] Rows =
    [
        new(65u, ThrowableKind.Frag, "M67 Frag Grenade",
            Rulings.Throwables.FragFireGroupId,
            Rulings.Throwables.ProjectileIdBase + 65u,
            "Projectile_Grenades_HEGrenade.adr",
            Rulings.Throwables.ActorModelIds[0],
            Rulings.Throwables.FuseSeconds[0], DetonatesOnLifespan: true, DetonatesOnContact: false,
            Rulings.Throwables.FragEffectId, CloudEffectId: 0,
            Rulings.Throwables.FragDamageRadius, Rulings.Throwables.FragDamageHp, LingerSeconds: 0),
        new(2235u, ThrowableKind.Stun, "Stun Grenade",
            42u,
            Rulings.Throwables.ProjectileIdBase + 2235u,
            "Projectile_Grenades_FlashBang.adr",
            Rulings.Throwables.ActorModelIds[1],
            Rulings.Throwables.FuseSeconds[1], DetonatesOnLifespan: true, DetonatesOnContact: false,
            Rulings.Throwables.StunEffectId, CloudEffectId: 0,
            Radius: 0f, DamageHp: 0, LingerSeconds: 0),
        new(2236u, ThrowableKind.Smoke, "Smoke Grenade",
            43u,
            Rulings.Throwables.ProjectileIdBase + 2236u,
            "Projectile_Grenades_SmokeGrenade.adr",
            Rulings.Throwables.ActorModelIds[2],
            // Activate at the first native collision, which supplies its actual position.
            // The previous bounce-only actor emitted no position until its 3 s expiry.
            Rulings.Throwables.FuseSeconds[2], DetonatesOnLifespan: true, DetonatesOnContact: true,
            EffectId: 0, Rulings.Throwables.SmokeEffectId,
            Radius: 0f, DamageHp: 0, Rulings.Throwables.SmokeCloudSeconds),
        new(2237u, ThrowableKind.Gas, "Gas Grenade",
            44u,
            Rulings.Throwables.ProjectileIdBase + 2237u,
            "Projectile_Grenades_GasGrenade.adr",
            Rulings.Throwables.ActorModelIds[3],
            Rulings.Throwables.FuseSeconds[3], DetonatesOnLifespan: true, DetonatesOnContact: false,
            EffectId: 0, Rulings.Throwables.GasEffectId,
            Rulings.Throwables.GasCloudRadius, Rulings.Throwables.GasCloudDamagePerSecondHp,
            Rulings.Throwables.GasCloudSeconds),
        new(14u, ThrowableKind.Molotov, "Molotov Cocktail",
            7u,
            Rulings.Throwables.ProjectileIdBase + 14u,
            "Projectile_MolotovCocktail.adr",
            Rulings.Throwables.ActorModelIds[4],
            Rulings.Throwables.FuseSeconds[4], DetonatesOnLifespan: true, DetonatesOnContact: true,
            Rulings.Throwables.MolotovEffectId, Rulings.Throwables.MolotovPersistEffectId,
            Rulings.Throwables.MolotovRadius, Rulings.Throwables.MolotovDamagePerSecondHp,
            Rulings.Throwables.MolotovSeconds),
    ];

    private static readonly FrozenDictionary<uint, ThrowableFact> ByItem =
        Rows.ToFrozenDictionary(row => row.ItemDefinitionId);

    private static readonly FrozenDictionary<uint, ThrowableFact> ByGroup =
        Rows.ToFrozenDictionary(row => row.FireGroupId);

    private static readonly FrozenDictionary<uint, ThrowableFact> ByProjectile =
        Rows.ToFrozenDictionary(row => row.ProjectileId);

    /// <summary>All five, in table order.</summary>
    public static IReadOnlyList<ThrowableFact> All => Rows;

    /// <summary>The throwable an item is, if it is one.</summary>
    public static bool TryGet(uint itemDefinitionId, out ThrowableFact fact) =>
        ByItem.TryGetValue(itemDefinitionId, out fact);

    /// <summary>True for the five items this table names.</summary>
    public static bool IsThrowable(uint itemDefinitionId) => ByItem.ContainsKey(itemDefinitionId);

    /// <summary>The throwable a fire group belongs to, if any - the frag's ruled group included.</summary>
    public static bool TryGetByFireGroup(uint fireGroupId, out ThrowableFact fact) =>
        ByGroup.TryGetValue(fireGroupId, out fact);

    /// <summary>The throwable a projectile id belongs to, if any.</summary>
    public static bool TryGetByProjectile(uint projectileId, out ThrowableFact fact) =>
        ByProjectile.TryGetValue(projectileId, out fact);

    /// <summary>
    /// The fire group a throwable item names: the datasheet's for four of them, the D300 ruling
    /// for the frag. 0 for anything that is not a throwable.
    /// </summary>
    public static uint FireGroupFor(uint itemDefinitionId) =>
        ByItem.TryGetValue(itemDefinitionId, out ThrowableFact fact) ? fact.FireGroupId : 0u;

    /// <summary>
    /// The <c>ProjectileDefinitions</c> record one throwable spawns (docs/120 §3.2-3.5): the
    /// physics flight model, the client's own in-flight mesh, the fuse as <c>LIFESPAN</c> with
    /// <c>LIFESPAN_DETONATE</c> set (and <c>DETONATE_ON_CONTACT</c> for the molotov), a hand
    /// throw's speed, gravity 1.0 and a tumble.
    /// </summary>
    public static ProjectileDefinitionRecord ProjectileRecordFor(in ThrowableFact fact, float throwSpeed) =>
        new(fact.ProjectileId, fact.ProjectileModel)
        {
            FlightType = PhysicsFlightType,
            Speed = throwSpeed,
            Lifespan = fact.ProjectileLifespanSeconds,
            DetonatesOnLifespan = fact.DetonatesOnLifespan,
            DetonatesOnContact = fact.DetonatesOnContact,
            GravityScale = Rulings.Throwables.GravityScale,
            // D327: Z1's grenade rows carry DRAG 0 (the record's own default is a bullet's 0.005).
            Drag = 0f,
            AngularVelocityMin = -Rulings.Throwables.TumbleRadiansPerSecond,
            AngularVelocityMax = Rulings.Throwables.TumbleRadiansPerSecond,
            DamageRadius = fact.Radius,
            // FUN_142224ef0 copies two entries without checking the count. Match the
            // reference grenade rows instead of handing it an empty radius list.
            BulletRadii = [1u, 0u],
        };

    /// <summary>The five records, in table order, at the given throw speed.</summary>
    public static IReadOnlyList<ProjectileDefinitionRecord> ProjectileRecords(float throwSpeed) =>
        [.. Rows.Select(row => ProjectileRecordFor(in row, throwSpeed))];
}
