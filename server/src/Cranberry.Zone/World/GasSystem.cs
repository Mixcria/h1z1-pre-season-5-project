using Cranberry.Protocol;
using Cranberry.Zone.Gas;

namespace Cranberry.Zone.World;

/// <summary>
/// Wires the existing gas lane (<c>src/Cranberry.Zone/Gas</c>, D23) into the tick. It owns no
/// schedule, no geometry and no randomness of its own: <see cref="GasController"/> does all of that
/// and is already covered by its own tests. This type samples positions, calls
/// <see cref="GasController.Tick"/>, broadcasts what the result asks for and turns damage into
/// <see cref="Match.Damage"/> — it never writes health itself (docs/22 §4.8, §6.2).
/// </summary>
public sealed class GasSystem : ISystem
{
    private PlayerSample[] _samples = new PlayerSample[16];
    private GasToxicity[] _toxicityViews = new GasToxicity[16];
    private EntityId[] _sampledPlayers = new EntityId[16];

    public GasSystem(GasController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        Controller = controller;
    }

    public GasController Controller { get; }

    public bool Running => Controller.Running;

    public int PhaseIndex { get; private set; }

    public long Reveals { get; private set; }

    public long SafeZoneUpdates { get; private set; }

    public long DamageTicks { get; private set; }

    public bool Finished { get; private set; }

    /// <summary>
    /// Called by <see cref="MatchFlowSystem"/> at <c>ce 16</c> StartMatch — where
    /// <c>ZoneService.StartGas</c> also starts it, so the gas clock and the client's own
    /// "Revealing safe zone" countdown agree. docs/22 §6.2's diagram says "entering Live"; this is
    /// a deliberate deviation, recorded in docs/25 §2, and the damage side is phase-gated in
    /// <see cref="Tick"/> so nothing is lethal before <see cref="MatchPhase.Live"/>.
    /// </summary>
    public void Start(in TickContext context)
    {
        Controller.Start(context.Time.ElapsedMs, context.Settings.Seed);
        PhaseIndex = 0;
        Finished = false;
        Array.Clear(_toxicityViews);
        Array.Clear(_sampledPlayers);

        // What goes out here is GasSettings.PreMoveRing (docs/77 section 6), the owner's own
        // click-test ruling: nothing gas-shaped on the map until the gas starts moving.
        //   None       - nothing at all; the first ce 01 goes out on ShrinkStarted at 4:30.
        //   ZeroRadius - one ce 01 at radius 0, the client's own recognised "no gas" value.
        //   Boundary   - the pre-wave-8 behaviour, the play-area boundary from the first tick.
        // ce 02 is withheld in every position: it is the "next safe zone" widget and phase 1 is not
        // revealed yet. GasSchedule.IsLethalAt carries the matching damage gate, so the drawn ring
        // and the burning ring can never disagree.
        if (Controller.Schedule is GasSchedule schedule)
        {
            switch (context.Settings.Gas.PreMoveRing)
            {
                case GasPreMoveRing.Boundary:
                    BroadcastRing(context.World, context.Settings.Gas, schedule.InitialCircle);
                    break;
                case GasPreMoveRing.ZeroRadius:
                    BroadcastRing(
                        context.World,
                        context.Settings.Gas,
                        schedule.InitialCircle with { Radius = GasPackets.RingTerminalRadius });
                    break;
                default:
                    break;
            }
        }
    }

    public void Tick(in TickContext context)
    {
        if (!Controller.Running)
        {
            return;
        }

        World world = context.World;
        int count = 0;
        if (_samples.Length < world.Players.Length)
        {
            _samples = new PlayerSample[world.Players.Length];
            Array.Resize(ref _toxicityViews, world.Players.Length);
            Array.Resize(ref _sampledPlayers, world.Players.Length);
        }

        // Nothing is damageable outside Live, and a rider under a canopy is never damageable: the
        // gas clock starts at ce 16 (Dropping) so the client's reveal countdown lines up, but the
        // drop itself must not be lethal. TimerPhaseTable already declares GasReveal/GasClose to be
        // Live-only; this is the same rule applied to the producer. ZoneService.PumpGas carries the
        // identical gate on state.MountRequested.
        bool damageable = context.Match.Phase == MatchPhase.Live;

        foreach (MatchPlayer? player in world.Players)
        {
            if (player is null)
            {
                continue;
            }

            if (_sampledPlayers[player.Slot] != player.Id)
            {
                Controller.ForgetPlayer(player.Slot);
                _toxicityViews[player.Slot].Reset();
                _sampledPlayers[player.Slot] = player.Id;
            }

            bool mountedVehicle = !player.Mount.IsNone
                && world.TryGetEntity(player.Mount, out WorldEntity? vehicle)
                && vehicle.VehicleId != 13; // The client's parachute vehicle row.
            // Gas harms car occupants too. Their authoritative pose belongs to the vehicle;
            // only a canopy (or an unresolved mount during handover) retains descent immunity.
            var position = mountedVehicle && world.TryGetEntity(player.Mount, out WorldEntity? mount)
                ? mount.Position : player.Position;

            _samples[count++] = new PlayerSample(
                player.Slot,
                position,
                damageable && player.IsAlive && (player.Mount.IsNone || mountedVehicle));
        }

        GasTickResult result = Controller.Tick(context.Time.ElapsedMs, _samples.AsSpan(0, count));
        SendToxicity(world, context.Settings.Gas);
        if (result.Events == GasTickEvents.None)
        {
            return;
        }

        if (result.Has(GasTickEvents.PhaseChanged))
        {
            PhaseIndex = result.PhaseIndex;
        }

        // Under GasPreMoveRing.None the wall does not exist yet, so a reveal sends ce 02 alone and
        // the first ce 01 of the match goes out on ShrinkStarted below. For every later phase this
        // is always true and the send is unchanged.
        bool ringVisible = Controller.Schedule is not GasSchedule plan
            || plan.IsRingVisibleAt(Controller.MatchClockAt(context.Time.ElapsedMs));

        if (result.Has(GasTickEvents.RevealSafeZone))
        {
            Reveals++;
            BroadcastReveal(world, context.Settings.Gas, result.Active, result.Revealed, ringVisible);
        }

        if (result.Has(GasTickEvents.ShrinkStarted) || result.Has(GasTickEvents.SafeZoneUpdate))
        {
            SafeZoneUpdates++;
            BroadcastRing(world, context.Settings.Gas, result.Active,
                Controller.Schedule?.IsClosingAt(Controller.MatchClockAt(context.Time.ElapsedMs)) == true);
        }

        if (result.Has(GasTickEvents.DamageTick))
        {
            DamageTicks++;
            foreach (GasDamageTick damage in result.Damage)
            {
                if (world.PlayerAt(damage.PlayerIndex) is { Life: LifeState.Alive } victim)
                {
                    context.Match.Damage(victim, (int)damage.Amount, DamageCause.ToxicGas);
                }
            }
        }

        if (result.Has(GasTickEvents.Finished))
        {
            Finished = true;
        }
    }

    /// <summary>
    /// The pair a reveal sends: <c>ce 01</c> with the boundary still in force (the phase's origin)
    /// and <c>ce 02</c> with the phase's destination.
    /// <para>
    /// The two are not interchangeable. docs/18 §1b proves <c>ce 01</c> is the only sub that feeds
    /// the gas-volume renderer and the minimap ring, and §2 proves <c>ce 02</c> has "no per-frame
    /// or renderer consumer at all". Drawing the destination on <c>ce 01</c> at the reveal would
    /// put a lethal-looking wall around a player who is not being damaged for another three
    /// minutes; the wall tracks the server instead through <see cref="BroadcastRing"/>.
    /// </para>
    /// </summary>
    private static void BroadcastReveal(
        World world,
        GasSettings settings,
        GasCircle boundary,
        GasCircle next,
        bool drawRing)
    {
        foreach (MatchPlayer? player in world.Players)
        {
            if (player?.Sink is not { IsOpen: true } sink)
            {
                continue;
            }

            if (drawRing)
            {
                PacketWriter ring = sink.Begin();
                GasPackets.WriteRing(ring, settings, boundary);
                sink.End();
            }

            PacketWriter safeZone = sink.Begin();
            GasPackets.WriteSafeZone(safeZone, next);
            sink.End();
        }
    }

    /// <summary><c>ce 01</c> alone: the drawn wall, re-sent as the server's own circle closes.</summary>
    private static void BroadcastRing(World world, GasSettings settings, GasCircle circle, bool advancing = false)
    {
        foreach (MatchPlayer? player in world.Players)
        {
            if (player?.Sink is not { IsOpen: true } sink)
            {
                continue;
            }

            PacketWriter writer = sink.Begin();
            GasPackets.WriteRing(writer, settings, circle, advancing);
            sink.End();
        }
    }

    private void SendToxicity(World world, GasSettings settings)
    {
        if (!settings.SendToxicity) return;
        foreach (MatchPlayer? player in world.Players)
        {
            if (player?.Sink is not { IsOpen: true } sink) continue;
            ref GasToxicity view = ref _toxicityViews[player.Slot];
            bool changed = view.Synchronize(Controller.ToxicityForPlayer(player.Slot));
            if (view.Armed && !changed) continue;

            PacketWriter writer = sink.Begin();
            new CharacterResourceUpdate(player.Id.Value, settings.ToxicityResourceId,
                settings.ToxicityResourceType, view.Value, view.Sent).WriteTo(writer);
            sink.End();
            view.MarkArmed();
        }
    }
}
