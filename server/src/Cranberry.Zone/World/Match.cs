using Cranberry.Protocol;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Gas;

namespace Cranberry.Zone.World;

/// <summary>The phases one game passes through. docs/11 maps these onto the client's own flow.</summary>
public enum MatchPhase : byte
{
    /// <summary>No player yet; the match object exists but nothing is running.</summary>
    Forming,

    /// <summary>Players are in the staging area waiting for the countdown to arm.</summary>
    Lobby,

    /// <summary>The countdown widget is up; <c>ce 16 StartMatch</c> ends it.</summary>
    Countdown,

    /// <summary>Aircraft and parachutes (docs/12).</summary>
    Dropping,

    /// <summary>The match proper: gas runs, damage resolves, placement counts down.</summary>
    Live,

    /// <summary>A winner exists; the result screen is up.</summary>
    Ending,

    /// <summary>Terminal.</summary>
    Finished,
}

/// <summary>Which phase transitions exist. Anything else is a bug, not a state.</summary>
public static class MatchPhases
{
    public static bool CanTransition(MatchPhase from, MatchPhase to)
    {
        if (from == to)
        {
            return false;
        }

        // A match can be abandoned from anywhere (last player left, host shutdown).
        if (to == MatchPhase.Finished)
        {
            return from != MatchPhase.Finished;
        }

        return (from, to) switch
        {
            (MatchPhase.Forming, MatchPhase.Lobby) => true,
            (MatchPhase.Lobby, MatchPhase.Countdown) => true,
            (MatchPhase.Countdown, MatchPhase.Lobby) => true,      // countdown aborted: players left
            (MatchPhase.Countdown, MatchPhase.Dropping) => true,
            (MatchPhase.Dropping, MatchPhase.Live) => true,
            (MatchPhase.Live, MatchPhase.Ending) => true,
            (MatchPhase.Ending, MatchPhase.Finished) => true,
            _ => false,
        };
    }
}

/// <summary>
/// One game. Single-threaded by construction: every field is touched only from <see cref="Tick"/>
/// (and from <see cref="AddPlayer"/>/<see cref="RemovePlayer"/>, which become posted commands once
/// the match owns a thread — migration step 9).
/// <para>
/// Reading <see cref="Tick"/> is reading the whole game: drain, timers, producers, one damage
/// resolver, consequences, publish (docs/22 §5.2). The system order is the contract.
/// </para>
/// </summary>
public sealed class Match : ITimerSink
{
    private readonly CommandQueue _commands;
    private readonly DamageQueue _damage = new();
    private readonly ISystem[] _systems;
    private long _accumulatorMs;
    private long _lastPumpMs = long.MinValue;

    public Match(ushort matchId, MatchSettings? settings = null)
    {
        Settings = settings ?? MatchSettings.Default;
        MatchId = matchId;
        World = new World(matchId, Settings);
        Clock = new MatchClock();
        Scheduler = new TickScheduler();
        _commands = new CommandQueue(Settings.CommandCapacity, Settings.CommandArenaBytes);

        Movement = new MovementSystem();
        Vehicles = new VehicleSystem();
        Combat = new CombatSystem(Settings.Combat);
        Gas = new GasSystem(new GasController(Settings.Gas));
        Loot = new LootSystem();
        Flow = new MatchFlowSystem();
        Interest = new InterestSystem();
        Relay = new RelaySystem();

        // The frozen order of docs/22 §5.2. ResolveDamage runs between Gas and Loot and is not a
        // system, because nothing may be inserted between damage production and its resolution.
        _systems = [Movement, Vehicles, Combat, Gas, Loot, Flow, Interest, Relay];
    }

    public ushort MatchId { get; }

    public MatchSettings Settings { get; }

    public MatchPhase Phase { get; private set; } = MatchPhase.Forming;

    public MatchClock Clock { get; }

    public World World { get; }

    public TickScheduler Scheduler { get; }

    /// <summary>Ticks the pump gave up on rather than replaying. A visible jump, never a stall.</summary>
    public long TickDebt { get; private set; }

    public MovementSystem Movement { get; }

    public VehicleSystem Vehicles { get; }

    public CombatSystem Combat { get; }

    public GasSystem Gas { get; }

    public LootSystem Loot { get; }

    public MatchFlowSystem Flow { get; }

    public InterestSystem Interest { get; }

    public RelaySystem Relay { get; }

    /// <summary>The systems in their frozen tick order; a test pins this.</summary>
    public IReadOnlyList<ISystem> Systems => _systems;

    public CommandQueue Commands => _commands;

    public int PendingDamageCount => _damage.Count;

    // --- timer dispatch accounting (both guards of docs/22 §4.3) ---

    public long TimersFired { get; private set; }

    /// <summary>Actions dropped because their subject had no live outbound path.</summary>
    public long TimersDroppedLiveness { get; private set; }

    /// <summary>Actions dropped because their kind is not legal in the current phase.</summary>
    public long TimersDroppedPhase { get; private set; }

    /// <summary>The last action actually dispatched; a scaffold observation point.</summary>
    public ScheduledAction LastFired { get; private set; }

    public long Deaths { get; private set; }

    // --- membership ---

    public MatchPlayer AddPlayer(ulong accountGuid, string name, IPlayerSink? sink = null) =>
        World.AddPlayer(accountGuid, name, sink);

    /// <summary>
    /// Removes a body and cancels every pending action naming it — the scheduler half of the
    /// liveness guard.
    /// </summary>
    public void RemovePlayer(MatchPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        Scheduler.CancelAllFor(player.Id);
        World.RemovePlayer(player);
    }

    /// <summary>Applies a phase change if the state machine allows it. Returns false otherwise.</summary>
    public bool TryTransition(MatchPhase phase)
    {
        if (!MatchPhases.CanTransition(Phase, phase))
        {
            return false;
        }

        Phase = phase;
        return true;
    }

    // --- inbound ---

    /// <summary>Callable from a transport thread. Copies the payload once, into the queue's arena.</summary>
    public bool Post(in Command command, ReadOnlySpan<byte> payload) => _commands.TryEnqueue(command, payload);

    /// <summary>The only way health ever changes: producers queue, <see cref="ResolveDamage"/> applies.</summary>
    /// <param name="bleeds">
    /// docs/81 §4g: this damage opens or worsens a wound. Bullets, blades and blasts do; gas, falls
    /// and walls do not. Decided by the hit rule, applied by the resolver, because the resolver is
    /// the only place health moves.
    /// </param>
    /// <param name="headshot">Carried for the log and the death message.</param>
    public void Damage(
        MatchPlayer target,
        int amount,
        DamageCause cause,
        EntityId attacker = default,
        bool bleeds = false,
        bool headshot = false)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (amount <= 0)
        {
            return;
        }

        _damage.Add(new PendingDamage(target.Slot, amount, cause, attacker, bleeds, headshot));
    }

    /// <summary>
    /// Puts health back. Queued through the same resolver as damage - a negative amount - so a
    /// bandage tick and a bullet landing on the same tick still resolve in production order and
    /// still produce at most one death (docs/81 §3d). Never raises health above
    /// <see cref="MatchSettings.StartingHealth"/>.
    /// </summary>
    public void Heal(MatchPlayer target, int amount)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (amount <= 0)
        {
            return;
        }

        _damage.Add(new PendingDamage(target.Slot, -amount, DamageCause.Unknown, default));
    }

    // --- the loop ---

    /// <summary>
    /// Advances by whole ticks from the host's millisecond stamp. Catch-up is bounded and drops time
    /// rather than replaying it, so a debugger break or a collection pause costs a visible jump and
    /// never a hundred-step stall (docs/22 §5.1). Returns steps run.
    /// </summary>
    public int Pump(long nowMs)
    {
        if (_lastPumpMs == long.MinValue)
        {
            _lastPumpMs = nowMs;
            return 0;
        }

        long delta = nowMs - _lastPumpMs;
        _lastPumpMs = nowMs;
        if (delta <= 0)
        {
            return 0;
        }

        _accumulatorMs += delta;
        int due = (int)Math.Min(int.MaxValue, _accumulatorMs / MatchClock.FixedDeltaMs);
        int steps = Math.Min(due, MatchClock.MaxCatchUpTicks);

        if (due > MatchClock.MaxCatchUpTicks)
        {
            TickDebt += due - MatchClock.MaxCatchUpTicks;
            _accumulatorMs = 0;
        }
        else
        {
            _accumulatorMs -= (long)steps * MatchClock.FixedDeltaMs;
        }

        for (int i = 0; i < steps; i++)
        {
            Tick();
        }

        return steps;
    }

    /// <summary>
    /// Exactly one fixed step. Public and argument-free so a test can drive a match with no thread,
    /// no clock and no socket.
    /// </summary>
    public void Tick()
    {
        TickTime now = Clock.Advance();
        var context = new TickContext(this, now);

        Drain(in context);

        // Timers replace every Later(...) chain; both of today's guards apply at dispatch (Fire).
        Scheduler.Advance(now, this);

        // Producers, in the frozen order. None of them writes Health.
        Movement.Tick(in context);
        Vehicles.Tick(in context);
        Combat.Tick(in context);
        if (Settings.GasEnabled)
        {
            Gas.Tick(in context);
        }

        // The one resolver.
        ResolveDamage(in context);

        // Consequences and progression.
        Loot.Tick(in context);
        Flow.Tick(in context);

        // Publish: interest first (who can see what), then relay (what they get).
        if (now.Every(Settings.InterestStride, phase: MatchId & 3))
        {
            Interest.Tick(in context);
        }

        if (now.Every(Settings.RelayStride, phase: MatchId & 1))
        {
            Relay.Tick(in context);
        }

        World.SweepRemoved();
    }

    /// <summary>Flushes every connected player's sink. The caller's job, once per pump.</summary>
    public void FlushSinks()
    {
        foreach (MatchPlayer? player in World.Players)
        {
            player?.Sink?.Flush();
        }
    }

    void ITimerSink.Fire(in ScheduledAction action, TickTime now)
    {
        // Guard 1 — liveness. Later re-checks the connection state at fire time (ZoneService.cs:934).
        if (!action.Subject.IsNone)
        {
            if (!World.TryGetPlayer(action.Subject, out MatchPlayer? subject) || !subject.IsConnected)
            {
                TimersDroppedLiveness++;
                return;
            }
        }

        // Guard 2 — phase legality. Every match-flow closure re-checks state.Match today
        // (ZoneService.cs:952). A dropped action is counted, never silently swallowed.
        if (!TimerPhaseTable.IsLegal(action.Kind, Phase))
        {
            TimersDroppedPhase++;
            return;
        }

        TimersFired++;
        LastFired = action;
        Flow.OnTimer(this, action, now);
    }

    private void Drain(in TickContext context)
    {
        int drained = 0;
        while (drained < Settings.InboundBudgetPerTick && _commands.TryDequeue(out Command command))
        {
            drained++;
            if (World.PlayerAt(command.Slot) is not MatchPlayer player)
            {
                continue;
            }

            switch (command.Kind)
            {
                case CommandKind.Movement:
                    Movement.Stage(player, _commands.Payload(command), context.Time);
                    break;
                case CommandKind.ManagedMovement:
                    Vehicles.Stage(player, command, _commands.Payload(command));
                    break;
                case CommandKind.Interact:
                case CommandKind.PlayerSelect:
                    Loot.StageClaim(player, command.Target);
                    break;
                case CommandKind.Mount:
                case CommandKind.Dismount:
                case CommandKind.AutoMountEcho:
                    Vehicles.StageMount(player, command);
                    break;
                case CommandKind.Fire:
                    Combat.StageShot(player, _commands.Payload(command));
                    break;
                case CommandKind.Ready:
                case CommandKind.Join:
                case CommandKind.Leave:
                    Flow.Stage(player, command);
                    break;
                default:
                    break;
            }
        }

        // Only legal once the ring is empty; a budget-clipped drain keeps its payloads.
        _commands.ResetArena();
    }

    /// <summary>
    /// The only code in the server that writes health or kills. Without it, gas and a bullet landing
    /// on the same tick kill twice: two <c>ce 04</c>, two decrements, two spectate hand-offs
    /// (docs/22 §6.3).
    /// </summary>
    private void ResolveDamage(in TickContext context)
    {
        int count = _damage.Count;
        for (int i = 0; i < count; i++)
        {
            PendingDamage damage = _damage[i];
            if (World.PlayerAt(damage.Slot) is not { Life: LifeState.Alive } player)
            {
                continue;
            }

            int previousHealth = player.Health;
            player.Health = Math.Clamp(player.Health - damage.Amount, 0, Settings.StartingHealth);

            // docs/81 §4g. The wound is opened HERE, where health moves, and only for a cause that
            // bleeds - which is why PendingDamage carries the flag instead of the resolver guessing
            // it from the cause. Armour halves it by parity, deterministically.
            if (damage.Bleeds && player.Health > 0)
            {
                bool armoured = player.Armour.Wearing(player.WornBodyArmourItemId) != ArmourTier.None;
                player.Medical.Wound(ref player.Armour, armoured, context.Time.ElapsedMs);
                player.LastWoundedBy = damage.Attacker;
            }

            IPlayerSink? sink = player.Sink;
            if (sink is { IsOpen: true })
            {
                PacketWriter writer = sink.Begin();
                new GasPackets.Hitpoints((uint)player.Health, (uint)Settings.StartingHealth).WriteTo(writer);
                sink.End();

                // August's current HUD observes Resources.PlayerResourceDataSource. The legacy
                // hitpoints message alone leaves that resource at its initial full value.
                PacketWriter resourceWriter = sink.Begin();
                CharacterResourceUpdate.Health(player.Id.Value, (uint)player.Health,
                    (uint)previousHealth, (uint)Settings.StartingHealth).WriteTo(resourceWriter);
                sink.End();

                // Gas only. GasTick() is the one shape docs/15 §9's experiment defines (the amount
                // in field 5, everything else zero); §6.3's cause-dependent DamageInfo.For does not
                // exist while G-08 is open, and putting a bullet through the gas-shaped body would
                // contaminate the very run that is meant to read the field semantics off the wire.
                if (damage.Amount > 0 && Settings.Gas.SendDamageInfo && damage.Cause is DamageCause.ToxicGas)
                {
                    PacketWriter damageWriter = sink.Begin();
                    GasPackets.DamageInfo.GasTick((uint)damage.Amount)
                        .WriteTo(damageWriter, Settings.Gas);
                    sink.End();
                }
            }

            if (player.Health == 0 && damage.Amount > 0)
            {
                Kill(player, damage.Cause, damage.Attacker, in context);
            }
        }

        _damage.RemoveFirst(count);
    }

    private void Kill(MatchPlayer player, DamageCause cause, EntityId attacker, in TickContext context)
    {
        player.Life = LifeState.Dead;
        player.Mount = EntityId.None;
        Deaths++;

        // AliveCount is scanned, so it already excludes this player. Placement is the 1-based
        // human placement: the first of three to die placed 3rd.
        int remaining = World.AliveCount;
        player.Placement = remaining + 1;

        // A self-inflicted death (gas, falling, own explosive) resolves the attacker to the victim.
        // It credits no kill, and it must not name the victim as their own killer either.
        MatchPlayer? killer = World.TryGetPlayer(attacker, out MatchPlayer? found) && found != player
            ? found
            : null;
        if (killer is not null)
        {
            killer.Kills++;
        }

        IPlayerSink? sink = player.Sink;
        if (sink is { IsOpen: true })
        {
            PacketWriter writer = sink.Begin();
            new GasPackets.DeathInfo(
                // ce 04's rankIndex is 0-based: the handler displays rankIndex + 1 everywhere
                // (docs/18 §3a, FUN_140bbb120 → DAT_143f6e918 / KOTK_ELIMINATION_RANK). Sending the
                // 1-based Placement would show "#4" in a three-player match.
                Rank: player.Placement - 1,
                Killer: killer?.Name ?? string.Empty,
                Cause: ClientDeathCause(cause)).WriteTo(writer);
            sink.End();
        }

        Broadcast(remaining);
        Flow.OnPlayerDied(in context, player, cause);
    }

    /// <summary><c>ce 09</c> players-remaining to everyone still connected.</summary>
    private void Broadcast(int playersRemaining)
    {
        foreach (MatchPlayer? player in World.Players)
        {
            if (player?.Sink is not { IsOpen: true } sink)
            {
                continue;
            }

            PacketWriter writer = sink.Begin();
            GameModeHud.WritePlayersRemaining(writer, playersRemaining);
            sink.End();
        }
    }

    /// <summary>
    /// docs/18 §3b closes this enum (the <c>ce 04</c> wire one, not docs/15 §5's results-file
    /// table); causes with no client value fall through to <c>Environment</c>.
    /// <para>
    /// <b>Lane 1D-lite (the wiring plan's §4.1):</b> this used to be the same table minus the two
    /// weapon rows, so a shot player's slide read "You died from the environment." It now defers to
    /// <see cref="DeathCauseCodes.For(DamageCause)"/>, which is the one place the two value spaces
    /// meet and which carries the <b>[P]</b>/<b>[I]</b> marks — <c>Bullet</c> and <c>Melee</c> both
    /// answer <see cref="DeathCauseCode.GameModeKillCondition"/> (<c>0x4d</c>, <b>[I]</b>).
    /// Every other row is unchanged by construction: <c>DeathCauseCodeTests</c> pins the enum
    /// against <c>GasPackets.DeathCause</c>, so the two can never drift.
    /// </para>
    /// </summary>
    private static uint ClientDeathCause(DamageCause cause) => (uint)DeathCauseCodes.For(cause);
}
