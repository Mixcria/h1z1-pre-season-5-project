using System.Numerics;
using Cranberry.Protocol;

namespace Cranberry.Zone.Combat;

/// <summary>
/// One practice dummy: a body in the world with a health bar, standing where the shooter can see it.
/// </summary>
public sealed class PracticeTarget
{
    public PracticeTarget(ulong worldGuid, uint transientId, Vector3 position, Vector4 rotation, int health)
    {
        WorldGuid = worldGuid;
        TransientId = transientId;
        Position = position;
        Rotation = rotation;
        Health = health;
        MaxHealth = health;
    }

    /// <summary>The guid a <c>ProjectileHitReport</c> names when this dummy is hit.</summary>
    public ulong WorldGuid { get; }

    public uint TransientId { get; }

    public Vector3 Position { get; private set; }

    public Vector4 Rotation { get; private set; }
    public bool IsCombatBot { get; init; }

    public void Move(Vector3 position, float heading)
    {
        Position = position;
        Rotation = new Vector4(0, MathF.Sin(heading / 2), 0, MathF.Cos(heading / 2));
    }

    public int MaxHealth { get; }

    /// <summary>On the same 10,000-unit bar a player has, so the damage table reads the same.</summary>
    public int Health { get; private set; }

    /// <summary>Server clock at death; 0 while alive. The body is removed a while after this.</summary>
    public long DiedAtMs { get; private set; }

    public bool IsAlive => Health > 0;

    /// <summary>Its armour, so the two-tap and the plate rules are exercised for real.</summary>
    public Armour Armour;

    /// <summary>What is in its armour slot; 0 = bare. Settable so a dummy can be dressed for a test.</summary>
    public uint BodyArmourItemId;

    /// <summary>What is on its head; 0 = bare.</summary>
    public uint HelmetItemId;

    public bool FullKit { get; set; }
    public string Name { get; set; } = "Practice Dummy";
    public Match.RankBadge Badge { get; set; } = new(Match.RankedTier.Bronze);
    /// <summary>Kill-feed item, including the skin captured by the last accepted shot.</summary>
    public uint LastWeaponItemId { get; set; }
    public bool LastHeadshot { get; set; }
    public bool DeathPublished { get; set; }

    /// <summary>Takes health off. Returns true when this hit was the killing one.</summary>
    public bool Damage(int units, long nowMs)
    {
        if (Health == 0)
        {
            return false;
        }

        Health = Math.Max(0, Health - units);

        if (Health > 0)
        {
            return false;
        }

        DiedAtMs = nowMs;
        return true;
    }

    /// <summary>True once the corpse has lain long enough to be removed.</summary>
    public bool DespawnDue(long nowMs, long afterMs) =>
        DiedAtMs != 0 && nowMs - DiedAtMs >= afterMs;
}

/// <summary>
/// <b>Something to shoot.</b> The owner's own request, in his words: "we need to be able to spawn an
/// NPC and shoot it to practice killing other players." Without it a single player has no way to
/// verify that any of the fire-to-hit-to-damage path works at all, because there is nothing in a
/// Cranberry world that a <c>ProjectileHitReport</c> can name.
/// <para>
/// <b>What this is, and what it deliberately is not.</b> His <c>ZonePracticeTarget</c> is a headless
/// second <em>player</em> - a real session in the player registry whose socket is never flushed - so
/// that stream-in, hit validation, damage and death are the player path untouched. Cranberry cannot
/// do that yet: <c>AddLightweightPc 0xd5</c> and <c>LightweightToFullPc 0xd9</c> have opcodes and no
/// derived layout, and there is no multi-client model (docs/20 §8 prerequisite 1). So this dummy is
/// an <b>NPC-bodied</b> target carrying a human model id, spawned with the same proven
/// <c>AddLightweightNpc 0xd6</c> + <c>LightweightToFullNpc 0xda</c> pair that already puts ground
/// loot and vehicles in the world, and removed with the proven <c>Character.RemovePlayer 0f 01</c>.
/// </para>
/// <para>
/// <b>What that costs, stated honestly.</b> The <em>arbitration</em> it exercises is the real one -
/// the decoder, the fire hints, the range gate, the retail damage table, armour and the two-tap all
/// run exactly as they would against a player. What it does <em>not</em> prove is the player-specific
/// half: it has no <c>PoseHistory</c> to rewind (it never moves) and it takes no <c>ce 04</c>,
/// which is per-player-HUD and not per-character. The shooter's hits and damage do count.
/// </para>
/// <para>
/// <b>Two of those divergences were closed by lane 1D-lite</b> (docs/97, D152). A dummy now
/// <em>dies</em> rather than being deleted: <c>ZoneService.KillPracticeTarget</c> sends its own
/// <c>0f 4f StartMultiStateDeath</c> and <c>0f 48 KilledBy</c> before the existing corpse timer,
/// so the shooter watches a body fall and reads a kill-feed line. And behind
/// <c>MatchEndOptions.PracticeTargetCountsAsOpponent</c> (<c>CRANBERRY_MATCH_TARGET_COUNTS=1</c>,
/// <b>off by default</b>) it is counted in N REMAIN and can be the last man standing, which is the
/// only way one player alone can reach the victory path outside a harness. It still takes no
/// <c>ce 04</c>.
/// </para>
/// <para>
/// <b>Off by default</b> (<see cref="CombatOptions.PracticeTarget"/>): it puts an entity in the world
/// that nothing else does, and the owner turns it on for the shooting run and off for every other.
/// </para>
/// </summary>
public sealed class PracticeTargetPack
{
    /// <summary>
    /// The dummies' own guid range, clear of ground loot (<c>0x2000…</c>), inventory items
    /// (<c>0x3100…</c>), doors (<c>0x4400…</c>) and vehicles (<c>0x4600…</c>).
    /// </summary>
    public const ulong DefaultWorldGuidBase = 0x4800_0000_0000_0001;

    /// <summary>
    /// Transient ids above the vehicle band (2,000,000), so no other subsystem can collide with one.
    /// </summary>
    public const uint DefaultTransientIdBase = 3_000_000;

    /// <summary>
    /// The model a dummy wears: <c>Models.txt</c> 9469 <c>SurvivorMale_Skin_01.adr</c>, the same row
    /// <c>ZoneOptions.MaleModelId</c> gives the local player, so what stands there is unmistakably a
    /// person rather than a crate.
    /// </summary>
    public const uint DefaultModelId = 9469;

    private readonly List<PracticeTarget> _targets = [];
    private ulong _nextGuid = DefaultWorldGuidBase;
    private uint _nextTransientId = DefaultTransientIdBase;

    public IReadOnlyList<PracticeTarget> All => _targets;

    public int Count => _targets.Count;

    /// <summary>Attach the same match-owned combat body to each viewer's hit resolver.</summary>
    public void AddShared(PracticeTarget target)
    {
        if (Find(target.WorldGuid) is null) _targets.Add(target);
    }

    /// <summary>The dummy with this guid, or null. Called for every hit report.</summary>
    public PracticeTarget? Find(ulong worldGuid)
    {
        foreach (PracticeTarget target in _targets)
        {
            if (target.WorldGuid == worldGuid)
            {
                return target;
            }
        }

        return null;
    }

    /// <summary>
    /// Places <paramref name="count"/> dummies in front of a spawner. The first stands
    /// <paramref name="distance"/> units straight ahead on the spawner's own height - the owner's
    /// own placement - and any others step 2 units further out each, so a row of them is visible
    /// from one spot.
    /// </summary>
    /// <param name="origin">Where the shooter is.</param>
    /// <param name="heading">The shooter's facing, in radians.</param>
    /// <param name="count">How many.</param>
    /// <param name="distance">Distance to the first one.</param>
    /// <param name="health">Each dummy's starting health, in units.</param>
    public IReadOnlyList<PracticeTarget> Spawn(
        Vector3 origin, float heading, int count, float distance, int health)
    {
        var spawned = new List<PracticeTarget>(Math.Max(0, count));
        float sin = MathF.Sin(heading);
        float cos = MathF.Cos(heading);

        for (int i = 0; i < count; i++)
        {
            float ahead = distance + (i * 2f);
            var position = new Vector3(origin.X + (sin * ahead), origin.Y, origin.Z + (cos * ahead));

            // Turned to face the spawner: the dummy's heading is the shooter's, reversed.
            float facing = heading + MathF.PI;
            var rotation = new Vector4(0, MathF.Sin(facing / 2f), 0, MathF.Cos(facing / 2f));

            var target = new PracticeTarget(_nextGuid++, _nextTransientId++, position, rotation, health);
            _targets.Add(target);
            spawned.Add(target);
        }

        return spawned;
    }

    /// <summary>Removes one dummy from the pack - the corpse has been despawned.</summary>
    public bool Remove(PracticeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return _targets.Remove(target);
    }

    /// <summary>Every corpse whose <see cref="CombatOptions.PracticeTargetDespawnAfterDeathMs"/> has
    /// elapsed. Allocates only when something is actually due.</summary>
    public List<PracticeTarget> DueForDespawn(long nowMs, long afterMs)
    {
        List<PracticeTarget>? due = null;

        foreach (PracticeTarget target in _targets)
        {
            if (target.DespawnDue(nowMs, afterMs))
            {
                (due ??= []).Add(target);
            }
        }

        return due ?? [];
    }

    /// <summary>Drops every dummy - a match reset.</summary>
    public void Clear() => _targets.Clear();
}

/// <summary>
/// The <c>AddLightweightNpc 0xd6</c> body for a practice dummy: the same shared
/// <c>FUN_140a2d040</c> record ground loot uses, with a human model id at <c>+0x40</c> and a static
/// position update type, because the dummy never moves.
/// </summary>
public sealed record AddPracticeTarget(
    ulong Guid,
    uint TransientId,
    Vector3 Position,
    Vector4 Rotation,
    uint ModelId = PracticeTargetPack.DefaultModelId)
{
    public const byte Opcode = ZoneOpcodes.AddLightweightNpc;

    public LightweightEntityBody Body => new(
        Opcode,
        Guid,
        TransientId,
        ModelId,
        Position,
        Rotation,
        VehicleId: 0,
        NameId: 0,
        PositionUpdateType: 0,
        ProfileId: 0,
        NpcDefinitionId: 0);

    public int Length => Body.Length;

    public void WriteTo(PacketWriter writer) => Body.WriteTo(writer);
}
