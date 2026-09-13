namespace Cranberry.Zone.World;

/// <summary>
/// Per-observer enter/leave sets: who each viewer can see, and the transient id they know them by.
///
/// <para>
/// <b>The guard is gone.</b> This system carried <c>NotYetDerived =&gt; true</c> while the peer-spawn
/// family was a guess. It is not one any more: <c>d5 AddLightweightPc</c> is derived to the byte
/// (docs/100 §2, <see cref="PeerSpawnWriter.AddLightweightPc"/>), the <c>0x78</c> relay framing is
/// proven in both of its forms (docs/100 §5), <c>0f 01</c> is the despawn this server has always
/// sent, and only <c>d9 LightweightToFullPc</c>'s inner blob stays underived — where the refusal now
/// lives, on the one writer it belongs to, instead of on the whole subsystem.
/// </para>
///
/// <para>
/// <b>Still pure, still unreferenced by <c>ZoneService</c>.</b> Everything here decides <em>who</em>
/// a viewer should know about; nothing sends. Handing the enter/leave sets to a sink is lane 3C, and
/// keeping the seam is what lets the spatial rules be tested without a client.
/// </para>
///
/// <para>
/// <b>The radii are the owner's (D156, D53).</b> 310 m to enter, x1.2 to leave — see
/// <see cref="ObserverView.PlayerEnterMetres"/>. The hysteresis is not decoration: an entity sitting
/// on a single boundary would be spawned and despawned every tick, and each churn cycle costs a
/// 125-byte minimum <c>d5</c> and a 12-byte <c>0f 01</c> in a 512-byte datagram budget.
/// </para>
///
/// <para>
/// <b>Z1's viewer fan-out shape is deliberately not ported (D157).</b> Only the radii, the
/// hysteresis, the self-guard and the redress-on-enter intent cross the boundary. The sweep here is
/// a grid query per viewer against Cranberry's own <see cref="InterestGrid"/> with per-viewer
/// budgets, which is a different arrangement from his, and S6 §3.8 records that his fan-out is wrong
/// at 1148 in any case.
/// </para>
/// </summary>
public sealed class InterestSystem : ISystem
{
    private int[] _keys = new int[256];

    public long Entered { get; private set; }

    public long Left { get; private set; }

    /// <summary>Enters deferred to a later tick because the viewer's spawn budget was spent.</summary>
    public long SpawnBudgetStops { get; private set; }

    /// <summary>
    /// What a viewer must be sent for each entity that entered its view. Nothing in this class calls
    /// it — the wiring is lane 3C — but naming it here is what stops the enter set and the packet
    /// order from drifting apart: <c>d5</c> first (it creates the entity every later packet needs),
    /// then the dress, then the arsenal.
    /// </summary>
    public static readonly IReadOnlyList<string> EnterBurst =
    [
        "d5 AddLightweightPc",
        "94 01 SetCharacterEquipment (the peer's guid)",
        "82 15 01 RemoteWeapon.Reset",
    ];

    public void Tick(in TickContext context)
    {
        World world = context.World;

        foreach (MatchPlayer? candidate in world.Players)
        {
            if (candidate is not MatchPlayer viewer)
            {
                continue;
            }

            UpdateEnters(world, viewer);
            UpdateLeaves(world, viewer);
        }
    }

    private void UpdateEnters(World world, MatchPlayer viewer)
    {
        ObserverView view = viewer.View;
        int found = world.Grid.Query(viewer.Position, ObserverView.PlayerLeaveMetres, _keys);
        if (found > _keys.Length)
        {
            _keys = new int[Math.Max(found, _keys.Length * 2)];
            found = world.Grid.Query(viewer.Position, ObserverView.PlayerLeaveMetres, _keys);
        }

        int budget = view.SpawnBudgetPerTick;
        for (int i = 0; i < found; i++)
        {
            int key = _keys[i];
            EntityId id;
            System.Numerics.Vector3 position;
            float enterMetres;

            if (world.PlayerFromKey(key) is MatchPlayer subject)
            {
                if (ReferenceEquals(subject, viewer))
                {
                    continue;
                }

                id = subject.Id;
                position = subject.Position;
                enterMetres = ObserverView.PlayerEnterMetres;
            }
            else if (world.EntityFromKey(key) is WorldEntity entity)
            {
                if (entity.Removed)
                {
                    continue;
                }

                id = entity.Id;
                position = entity.Position;
                enterMetres = entity.Kind == EntityKind.GroundItem
                    ? ObserverView.ItemEnterMetres
                    : ObserverView.VehicleEnterMetres;
            }
            else
            {
                continue;
            }

            if (view.Knows(id) || !Within(viewer, position, enterMetres))
            {
                continue;
            }

            if (budget <= 0)
            {
                // Deferred, not lost: the next pass picks it up. This is what stops a drop from
                // queueing 50 lightweight records into one tick.
                SpawnBudgetStops++;
                return;
            }

            budget--;
            view.MarkKnown(id);
            view.Transients.Acquire(id);
            Entered++;
        }
    }

    private void UpdateLeaves(World world, MatchPlayer viewer)
    {
        ObserverView view = viewer.View;
        int budget = view.DespawnBudgetPerTick;

        // Backwards: MarkForgotten swap-removes, so a lower index is never disturbed.
        for (int i = view.KnownCount - 1; i >= 0 && budget > 0; i--)
        {
            EntityId id = view.KnownAt(i);
            bool drop;

            if (world.TryGetPlayer(id, out MatchPlayer? subject))
            {
                drop = !Within(viewer, subject.Position, ObserverView.PlayerLeaveMetres);
            }
            else if (world.TryGetEntity(id, out WorldEntity? entity))
            {
                float leave = entity.Kind == EntityKind.GroundItem
                    ? ObserverView.ItemLeaveMetres
                    : ObserverView.VehicleLeaveMetres;
                drop = entity.Removed || !Within(viewer, entity.Position, leave);
            }
            else
            {
                // Gone from the world entirely.
                drop = true;
            }

            if (!drop)
            {
                continue;
            }

            budget--;
            view.MarkForgotten(id);

            // The id is released only after the despawn would have been queued, which is this
            // point: step 7 writes the packet here.
            view.Transients.Release(id);
            Left++;
        }
    }

    private static bool Within(MatchPlayer viewer, in System.Numerics.Vector3 position, float metres) =>
        InterestGrid.HorizontalDistanceSquared(viewer.Position, position) <= metres * metres;
}
