using System.Numerics;
using Cranberry.Protocol;

namespace Cranberry.Zone.Combat;

/// <summary>One dummy's spawn burst, ready to send.</summary>
/// <param name="Target">The dummy itself.</param>
/// <param name="Add"><c>AddLightweightNpc 0xd6</c> - the body.</param>
/// <param name="Full"><c>LightweightToFullNpc 0xda</c> - clears the "full data pending" bit.</param>
/// <param name="Line">One log line describing what went out.</param>
public readonly record struct PracticeTargetSpawn(
    PracticeTarget Target,
    Action<PacketWriter> Add,
    Action<PacketWriter> Full,
    string Line);

/// <summary>
/// Builds the packets that put practice dummies in the world, so the zone service keeps one call
/// site and no new logic.
/// <para>
/// The pair is the same one ground loot already uses and that docs/19 §7 rule 1 pins: the
/// <c>0xda</c> must follow its own <c>0xd6</c> and carry the same <b>transient</b> id, because the
/// apply matches the record to the object by transient id and requires the "full data pending" bit
/// the <c>0xd6</c> apply set.
/// </para>
/// </summary>
public static class PracticeTargetSpawner
{
    /// <summary>
    /// Places and builds <see cref="CombatOptions.PracticeTargetCount"/> dummies in front of
    /// <paramref name="origin"/>. Returns an empty list when the option is off.
    /// </summary>
    public static IReadOnlyList<PracticeTargetSpawn> Build(
        PracticeTargetPack pack, Vector3 origin, float heading, CombatOptions options)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.PracticeTarget || options.PracticeTargetCount <= 0)
        {
            return [];
        }

        IReadOnlyList<PracticeTarget> spawned = pack.Spawn(
            origin,
            heading,
            options.PracticeTargetCount,
            options.PracticeTargetDistance,
            options.PracticeTargetHealth);

        var burst = new List<PracticeTargetSpawn>(spawned.Count);

        foreach (PracticeTarget target in spawned)
        {
            if (options.PracticeTargetFullKit)
            {
                PracticeTargetKit.Equip(target);
                burst.Add(new(target, w => PracticeTargetKit.WriteSpawn(w, target),
                    w => PracticeTargetKit.WriteDress(w, target),
                    $"combat: full-kit player dummy {target.WorldGuid} at {target.Position}, {target.Health} hp"));
                continue;
            }
            var add = new AddPracticeTarget(
                target.WorldGuid, target.TransientId, target.Position, target.Rotation);
            var full = new LightweightToFullNpc(target.TransientId, target.WorldGuid);

            burst.Add(new PracticeTargetSpawn(
                target,
                add.WriteTo,
                full.WriteTo,
                $"combat: practice target {target.WorldGuid} (transient {target.TransientId}, model "
                + $"{PracticeTargetPack.DefaultModelId}) at ({target.Position.X:0.0}, "
                + $"{target.Position.Y:0.0}, {target.Position.Z:0.0}) with "
                + $"{target.MaxHealth} hp - AddLightweightNpc ({add.Length} B) + LightweightToFullNpc"));
        }

        return burst;
    }
}
