using System.Buffers.Binary;
using System.Diagnostics;
using Cranberry.Transport;

namespace Cranberry.Zone;

public sealed partial class ZoneService
{
    private ProductionDiagnostics? _productionDiagnostics;

    /// <summary>Opt-in only. Configure before starting the gateway listener. Snapshots and all
    /// gameplay counters belong to that listener; delayed-continuation histograms are thread safe.</summary>
    public bool DiagnosticsEnabled
    {
        get => _productionDiagnostics is not null;
        set => _productionDiagnostics = value ? _productionDiagnostics ?? new() : null;
    }

    /// <summary>Capture/reset on the gateway owner thread, then serialize elsewhere. Counts cover
    /// the elapsed window; populations are current gauges. Samples contain process-local numbers,
    /// never character/account identifiers. At most 64 sessions are exported, busiest first.</summary>
    public object CaptureDiagnostics(int maxSessions = 16)
    {
        if (_productionDiagnostics is not { } d) return new { Enabled = false };
        maxSessions = Math.Clamp(maxSessions, 0, 64);
        long now = Stopwatch.GetTimestamp();
        double seconds = (now - d.WindowStarted) / (double)Stopwatch.Frequency;
        d.WindowStarted = now;
        var phases = new int[Enum.GetValues<MatchStep>().Length];
        var matches = new Dictionary<ulong, MatchStep>();
        var samples = new List<(GatewaySessionState State, SessionDiagnostics Metrics)>(maxSessions + 1);
        int connected = 0, authenticated = 0, knownPeerViews = 0, streamedLootViews = 0, managedOwnerships = 0;
        foreach (var (connection, state) in _accountSessions)
        {
            if (connection.State != ConnectionState.Open) continue;
            connected++;
            if (state.Authenticated) authenticated++;
            phases[(int)state.Match]++;
            knownPeerViews += state.Peer?.View.KnownCount ?? 0;
            streamedLootViews += state.Loot.Count;
            managedOwnerships += state.Movement.ManagedEntityCount;
            ulong match = state.BountyAdmission.MatchId;
            if (match != 0 && state.Match != MatchStep.Menu
                && (!matches.TryGetValue(match, out var previous)
                    || ObservedMatchPhasePriority(previous) < ObservedMatchPhasePriority(state.Match)))
                matches[match] = state.Match;

            var metrics = GetSessionDiagnostics(state, d);
            if (maxSessions == 0) continue;
            int index = 0;
            while (index < samples.Count && (samples[index].Metrics.Activity > metrics.Activity
                || samples[index].Metrics.Activity == metrics.Activity && samples[index].Metrics.Id < metrics.Id)) index++;
            if (index < maxSessions)
            {
                samples.Insert(index, (state, metrics));
                if (samples.Count > maxSessions) samples.RemoveAt(maxSessions);
            }
        }

        var sessions = samples.Select(sample => new
        {
            Session = sample.Metrics.Id,
            Phase = sample.State.Match.ToString(),
            PlayerMovement = sample.Metrics.Player.Snapshot(),
            ManagedMovement = sample.Metrics.Managed.Snapshot(),
            PeerPoseOffers = sample.Metrics.PeerPoseOffers,
            VehiclePoseOffers = sample.Metrics.VehiclePoseOffers,
            MovementWire = new
            {
                PlayerParsedByPhase = sample.Metrics.PlayerWire?.Snapshot() ?? [],
                ManagedParsedByPhase = sample.Metrics.ManagedWire?.Snapshot() ?? [],
                PeerPoseOffersByReceiverPhase = sample.Metrics.PeerOfferWire?.Snapshot() ?? [],
                MovementVersionRequestHeaders = sample.Metrics.MovementVersionRequestHeaders,
                TruncatedMovementVersionRequestHeaders = sample.Metrics.TruncatedMovementVersionRequestHeaders,
            },
            KnownPeerViews = sample.State.Peer?.View.KnownCount ?? 0,
            StreamedLootViews = sample.State.Loot.Count,
            ManagedOwnerships = sample.State.Movement.ManagedEntityCount,
        }).ToArray();
        // Reset every live session, not only the exported subset. No disconnected-session map is retained.
        foreach (var state in _accountSessions.Values) state.ProductionMetrics?.ResetWindow();

        var matchPhases = new int[phases.Length];
        foreach (MatchStep phase in matches.Values) matchPhases[(int)phase]++;
        long sharedVehicles = 0, sharedBodyBags = 0, sharedCrates = 0, sharedDrops = 0, destroyedProps = 0;
        foreach (var shared in _sharedLootMatches.Values)
        {
            sharedVehicles += shared.Fleet?.Count ?? 0;
            sharedBodyBags += shared.BodyBags.Count;
            sharedCrates += shared.Crates.Count;
            sharedDrops += shared.Drops.Count;
            destroyedProps += shared.Destructibles.Destroyed.Count;
        }
        var result = new
        {
            Enabled = true,
            WindowSeconds = seconds,
            Population = new
            {
                AccountLinkedOpenSessions = connected,
                AuthenticatedOpenSessions = authenticated,
                SessionsByPhase = PhaseCounts(phases),
                AdmittedMatches = matches.Count,
                FormingMatches = matchPhases[(int)MatchStep.Queued],
                StartingMatches = matchPhases[(int)MatchStep.Transferring] + matchPhases[(int)MatchStep.Zoning]
                    + matchPhases[(int)MatchStep.Lobby],
                ActiveMatches = matchPhases[(int)MatchStep.Dropping] + matchPhases[(int)MatchStep.InMatch],
                EndingMatches = matchPhases[(int)MatchStep.Ended],
                MatchesByMostActiveMemberPhase = PhaseCounts(matchPhases),
                SharedWorlds = _sharedLootMatches.Count,
                PublicMatches = PublicMatches.Select(r => new { r.MatchId, r.WorldId, Mode = r.Mode.ToString(),
                    Phase = r.Phase.ToString(), RosterCount = r.Roster.Count, r.PresentPlayers }).ToArray(),
                // Only counts actually owned by the shared match. Static map assets are excluded.
                SharedEntities = new { Vehicles = sharedVehicles, BodyBags = sharedBodyBags,
                    Crates = sharedCrates, PlayerDrops = sharedDrops, DestroyedPropStates = destroyedProps },
                // These are replicas/ownership records, deliberately not unique simulated entities.
                ViewerRecords = new { KnownPeerViews = knownPeerViews, StreamedLootViews = streamedLootViews,
                    ManagedOwnerships = managedOwnerships },
            },
            PlayerMovement = d.Player.SnapshotAndReset(),
            ManagedMovement = d.Managed.SnapshotAndReset(),
            MovementWire = new
            {
                PlayerParsedByPhase = d.PlayerWire.SnapshotAndReset(),
                ManagedParsedByPhase = d.ManagedWire.SnapshotAndReset(),
                PeerPoseOffersByReceiverPhase = d.PeerOfferWire.SnapshotAndReset(),
                MovementVersionRequestHeadersByPhase = PhaseCounts(d.MovementVersionRequestHeaders),
                TruncatedMovementVersionRequestHeaders = d.TruncatedMovementVersionRequestHeaders,
            },
            Replication = new { PeerPoseOffers = d.PeerPoseOffers, PeerPoseOfferBytes = d.PeerPoseOfferBytes,
                CompleteSnapshotOffers = d.CompleteSnapshotOffers, OrderedRecordOffers = d.OrderedRecordOffers,
                VehiclePoseOffers = d.VehiclePoseOffers, PeerBudgetDeferredRecipients = d.PeerBudgetDeferredRecipients,
                FireRelayAttempts = d.FireRelayAttempts, FireStartOffers = d.FireStartOffers, FireStopOffers = d.FireStopOffers,
                ProjectileLaunchOffers = d.ProjectileLaunchOffers, MovementVersionReplies = d.MovementVersionReplies,
                WorldPoseWork = d.WorldPoseWork.TakeSnapshot(), PeerPoseEnqueueWork = d.PeerPoseEnqueueWork.TakeSnapshot(),
                VehiclePoseEnqueueWork = d.VehiclePoseEnqueueWork.TakeSnapshot() },
            Interest = new { CandidatesVisited = _peers.InterestCandidatesVisited - d.InterestCandidates,
                RelayCandidatesVisited = _peers.RelayCandidatesVisited - d.RelayCandidates,
                Entered = _peers.Entered - d.Entered, Left = _peers.Left - d.Left,
                SpawnBudgetStops = _peers.SpawnBudgetStops - d.SpawnBudgetStops,
                SweepWork = d.InterestSweepWork.TakeSnapshot() },
            Timers = new { Scheduled = d.TimersScheduled, Executed = d.TimersExecuted,
                ClosedLinkSkipped = d.TimersClosedLinkSkipped,
                ScheduleToContinuation = d.ScheduleToContinuation.TakeSnapshot(),
                ContinuationLateness = d.ContinuationLateness.TakeSnapshot(),
                ContinuationToCallback = d.ContinuationToCallback.TakeSnapshot(),
                CallbackDuration = d.CallbackDuration.TakeSnapshot() },
            Sessions = sessions,
        };
        d.PeerPoseOffers = d.PeerPoseOfferBytes = d.CompleteSnapshotOffers = d.OrderedRecordOffers = 0;
        d.VehiclePoseOffers = d.PeerBudgetDeferredRecipients = 0;
        d.FireRelayAttempts = d.FireStartOffers = d.FireStopOffers = d.ProjectileLaunchOffers = d.MovementVersionReplies = 0;
        d.TimersScheduled = d.TimersExecuted = d.TimersClosedLinkSkipped = 0;
        Array.Clear(d.MovementVersionRequestHeaders);
        d.TruncatedMovementVersionRequestHeaders = 0;
        d.InterestCandidates = _peers.InterestCandidatesVisited;
        d.RelayCandidates = _peers.RelayCandidatesVisited;
        d.Entered = _peers.Entered; d.Left = _peers.Left; d.SpawnBudgetStops = _peers.SpawnBudgetStops;
        return result;
    }

    private static Dictionary<string, int> PhaseCounts(int[] counts) => Enum.GetValues<MatchStep>()
        .ToDictionary(phase => phase.ToString(), phase => counts[(int)phase]);

    // One member finishing must not hide other members still playing the same admitted match.
    private static int ObservedMatchPhasePriority(MatchStep phase) => phase switch
    {
        MatchStep.Menu => 0,
        MatchStep.Ended => 1,
        _ => (int)phase + 1,
    };

    private static SessionDiagnostics GetSessionDiagnostics(GatewaySessionState state, ProductionDiagnostics d) =>
        state.ProductionMetrics ??= new() { Id = ++d.NextSessionId };

    private long BeginMovementDiagnostics(GatewaySessionState state, bool managed)
    {
        if (_productionDiagnostics is not { } d) return 0;
        var session = GetSessionDiagnostics(state, d);
        var totals = managed ? d.Managed : d.Player;
        ref MovementCounters counters = ref (managed ? ref session.Managed : ref session.Player);
        counters.Received++; totals.Counts.Received++;
        long now = Stopwatch.GetTimestamp();
        ref MovementTrace trace = ref (managed ? ref session.ManagedTrace : ref session.PlayerTrace);
        if (trace.HasArrival && trace.Generation == state.MatchAdmissionGeneration)
            totals.ArrivalIntervals.RecordTicks(now - trace.Arrival);
        else trace.HasClientTime = false;
        trace.Arrival = now; trace.HasArrival = true; trace.Generation = state.MatchAdmissionGeneration;
        return now;
    }

    private void ParsedMovementDiagnostics(GatewaySessionState state, bool managed, ClientMovementUpdate update,
        uint transientId = 0)
    {
        if (_productionDiagnostics is not { } d) return;
        var session = GetSessionDiagnostics(state, d);
        var totals = managed ? d.Managed : d.Player;
        ref MovementCounters counters = ref (managed ? ref session.Managed : ref session.Player);
        counters.Parsed++; totals.Counts.Parsed++;
        var header = new MovementWireHeader((ushort)update.Fields, update.ClientTime, update.State, update.Posture);
        var wire = managed ? (session.ManagedWire ??= new()) : (session.PlayerWire ??= new());
        bool versionChanged = wire.Observe(state.Match, header, transientId, state.MatchAdmissionGeneration);
        (managed ? d.ManagedWire : d.PlayerWire).ObserveAggregate(state.Match, header, versionChanged);
        if (update.EffectivePosition is not null) totals.PositionFields++;
        if (update.Rotation is not null || update.PrecisePose is not null) totals.RotationFields++;
        if (update.Orientation is not null) totals.OrientationFields++;
        if (update.Posture is not null) totals.PostureFields++;
        if (update.HorizontalSpeed is not null || update.VerticalSpeed is not null) totals.SpeedFields++;
        if (update.HorizontalSpeed == 0f) totals.ZeroHorizontalSpeedFields++;
        if (update.PrecisePose is not null) totals.PreciseFields++;
        if (update.EffectivePosition is { } p && (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
            || update.Orientation is float orientation && !float.IsFinite(orientation)
            || update.HorizontalSpeed is float h && !float.IsFinite(h)
            || update.VerticalSpeed is float v && !float.IsFinite(v)) totals.NonFiniteRecords++;

        ref MovementTrace trace = ref (managed ? ref session.ManagedTrace : ref session.PlayerTrace);
        // A session can simulate different entities. Never compare one entity's clock to another's.
        if (trace.HasClientTime && (!managed || trace.TransientId == transientId))
        {
            int delta = unchecked((int)(update.ClientTime - trace.ClientTime));
            if (delta == 0) totals.ClientTimestampDuplicates++;
            else if (delta < 0) totals.ClientTimestampBackwardOrReset++;
            else
            {
                if (update.ClientTime < trace.ClientTime) totals.ClientTimestampForwardWraps++;
                totals.ClientForwardDeltaMs.RecordTicks((long)(delta * (Stopwatch.Frequency / 1000d)));
            }
        }
        trace.ClientTime = update.ClientTime; trace.TransientId = transientId; trace.HasClientTime = true;
    }

    private void EndMovementDiagnostics(GatewaySessionState state, bool managed, long started, MovementOutcome outcome)
    {
        if (started == 0 || _productionDiagnostics is not { } d) return;
        var session = GetSessionDiagnostics(state, d);
        var totals = managed ? d.Managed : d.Player;
        ref MovementCounters counters = ref (managed ? ref session.Managed : ref session.Player);
        counters.Record(outcome); totals.Counts.Record(outcome);
        totals.HandlerDuration.RecordTicks(Stopwatch.GetTimestamp() - started);
    }

    private void RecordMovementFormatFailure(bool managed, bool afterApply)
    {
        if (_productionDiagnostics is not { } d) return;
        var totals = managed ? d.Managed : d.Player;
        totals.CaughtPacketFormatErrors++;
        if (afterApply) totals.PacketFormatErrorsAfterApply++;
    }

    private void RecordPeerPoseOffer(SoeConnection connection, int bytes, bool completeSnapshot,
        ulong entityGuid, ReadOnlySpan<byte> record)
    {
        if (_productionDiagnostics is not { } d) return;
        d.PeerPoseOffers++; d.PeerPoseOfferBytes += bytes;
        if (completeSnapshot) d.CompleteSnapshotOffers++; else d.OrderedRecordOffers++;
        if (connection.Tag is GatewaySessionState state)
        {
            var session = GetSessionDiagnostics(state, d);
            session.PeerPoseOffers++;
            if (MovementWireHeader.TryRead(record, out var header))
            {
                bool versionChanged = (session.PeerOfferWire ??= new()).Observe(
                    state.Match, header, entityGuid, state.MatchAdmissionGeneration);
                d.PeerOfferWire.ObserveAggregate(state.Match, header, versionChanged);
            }
        }
    }

    // This is an observation at the existing decoded tunnel boundary, not a new request handler.
    // 0f 57 asks for MovementVersion. Bare 57 is unrelated client telemetry. Do not reply here.
    private void RecordMovementVersionRequest(GatewaySessionState state, ReadOnlySpan<byte> payload)
    {
        if (_productionDiagnostics is not { } d || payload.Length < 2 || payload[0] != 0x0f || payload[1] != 0x57)
            return;
        var session = GetSessionDiagnostics(state, d);
        if (payload.Length < 10)
        {
            d.TruncatedMovementVersionRequestHeaders++;
            session.TruncatedMovementVersionRequestHeaders++;
            return;
        }
        d.MovementVersionRequestHeaders[(int)state.Match]++;
        session.MovementVersionRequestHeaders++;
    }

    private void RecordVehiclePoseOffer(GatewaySessionState state)
    {
        if (_productionDiagnostics is not { } d) return;
        d.VehiclePoseOffers++; GetSessionDiagnostics(state, d).VehiclePoseOffers++;
    }

    private enum MovementOutcome { UnexpectedFailure, Applied, MountedSuppressed, ZeroPoseSuppressed,
        DismountHandoffSuppressed, UnownedSuppressed, Malformed }

    private struct MovementCounters
    {
        public long Received, Parsed, Applied, MountedSuppressed, ZeroPoseSuppressed, DismountHandoffSuppressed,
            UnownedSuppressed, Malformed, UnexpectedFailure;
        public void Record(MovementOutcome outcome)
        {
            switch (outcome)
            {
                case MovementOutcome.Applied: Applied++; break;
                case MovementOutcome.MountedSuppressed: MountedSuppressed++; break;
                case MovementOutcome.ZeroPoseSuppressed: ZeroPoseSuppressed++; break;
                case MovementOutcome.DismountHandoffSuppressed: DismountHandoffSuppressed++; break;
                case MovementOutcome.UnownedSuppressed: UnownedSuppressed++; break;
                case MovementOutcome.Malformed: Malformed++; break;
                default: UnexpectedFailure++; break;
            }
        }
        public readonly object Snapshot() => new { Received, Parsed, Applied, MountedSuppressed, ZeroPoseSuppressed,
            DismountHandoffSuppressed, UnownedSuppressed, Malformed, UnexpectedFailure,
            Suppressed = MountedSuppressed + ZeroPoseSuppressed + DismountHandoffSuppressed + UnownedSuppressed };
    }

    private sealed class MovementTotals
    {
        public MovementCounters Counts;
        public readonly DiagnosticTiming HandlerDuration = new(), ArrivalIntervals = new(), ClientForwardDeltaMs = new();
        public long PositionFields, RotationFields, OrientationFields, PostureFields, SpeedFields,
            ZeroHorizontalSpeedFields, PreciseFields, NonFiniteRecords, ClientTimestampDuplicates,
            ClientTimestampBackwardOrReset, ClientTimestampForwardWraps, CaughtPacketFormatErrors, PacketFormatErrorsAfterApply;
        public object SnapshotAndReset()
        {
            var result = new { Counts = Counts.Snapshot(), HandlerDuration = HandlerDuration.TakeSnapshot(),
                ArrivalIntervals = ArrivalIntervals.TakeSnapshot(), ClientForwardDeltaMs = ClientForwardDeltaMs.TakeSnapshot(),
                PositionFields, RotationFields, OrientationFields, PostureFields, SpeedFields, ZeroHorizontalSpeedFields,
                PreciseFields, NonFiniteRecords, ClientTimestampDuplicates, ClientTimestampBackwardOrReset,
                ClientTimestampForwardWraps, CaughtPacketFormatErrors, PacketFormatErrorsAfterApply };
            Counts = default;
            PositionFields = RotationFields = OrientationFields = PostureFields = SpeedFields = ZeroHorizontalSpeedFields = 0;
            PreciseFields = NonFiniteRecords = ClientTimestampDuplicates = ClientTimestampBackwardOrReset = ClientTimestampForwardWraps = 0;
            CaughtPacketFormatErrors = PacketFormatErrorsAfterApply = 0;
            return result;
        }
    }

    private struct MovementTrace
    {
        public long Arrival;
        public int Generation;
        public uint ClientTime, TransientId;
        public bool HasArrival, HasClientTime;
    }

    // Native MotionRecord terminology: State is version; Posture is motion flags (STOP is 0x40).
    // Read only the fixed header and the first optional packed field. The movement parser already
    // owns validation; this helper neither reparses the rest nor changes bytes offered to transport.
    private readonly record struct MovementWireHeader(ushort Mask, uint ClientTime, byte Version, uint? MotionFlags)
    {
        public static bool TryRead(ReadOnlySpan<byte> record, out MovementWireHeader header)
        {
            header = default;
            if (record.Length < 7) return false;
            ushort mask = BinaryPrimitives.ReadUInt16LittleEndian(record);
            uint? flags = null;
            if ((mask & 1) != 0)
            {
                if (record.Length < 8) return false;
                int length = 1 + (record[7] & 3);
                if (record.Length < 7 + length) return false;
                uint packed = 0;
                for (int i = 0; i < length; i++) packed |= (uint)record[7 + i] << (8 * i);
                flags = packed >> 2;
            }
            header = new(mask, BinaryPrimitives.ReadUInt32LittleEndian(record[2..]), record[6], flags);
            return true;
        }
    }

    // Fixed MatchStep slots, with no character/account/IP labels or payload history. Per-session
    // profiles are lazy and use the existing 16/default, 64/maximum export selection. Only the last
    // internal stream key per phase is retained to avoid comparing different actors' versions.
    private sealed class MovementWireDiagnostics
    {
        private readonly WirePhaseCounters[] _phases = new WirePhaseCounters[Enum.GetValues<MatchStep>().Length];
        private readonly WireVersionTrace[] _versions = new WireVersionTrace[Enum.GetValues<MatchStep>().Length];

        public bool Observe(MatchStep phase, in MovementWireHeader header, ulong streamKey, int generation)
        {
            ref var previous = ref _versions[(int)phase];
            bool changed = previous.HasVersion && previous.StreamKey == streamKey
                && previous.Generation == generation && previous.Version != header.Version;
            previous = new() { HasVersion = true, StreamKey = streamKey, Generation = generation, Version = header.Version };
            ObserveAggregate(phase, header, changed);
            return changed;
        }

        public void ObserveAggregate(MatchStep phase, in MovementWireHeader header, bool versionChanged)
        {
            ref var row = ref _phases[(int)phase];
            if (row.Records == 0)
            {
                row.FirstMask = header.Mask; row.FirstVersion = header.Version; row.FirstClientTime = header.ClientTime;
            }
            row.Records++;
            row.LastMask = header.Mask; row.LastVersion = header.Version; row.LastClientTime = header.ClientTime;
            if (header.Mask == 0x1fff) row.ExactFullRecords++;
            if ((header.Mask & 0x1000) != 0) row.PreciseRecords++;
            if (header.Mask == 0) row.HeaderOnlyRecords++;
            if ((header.Mask & 0x0002) != 0) row.PositionRecords++;
            if (header.Version != 0) row.NonZeroVersionRecords++;
            if (versionChanged) row.VersionChanges++;
            if (header.MotionFlags is uint flags)
            {
                if (row.MotionFlagsRecords == 0) row.FirstMotionFlags = flags;
                row.LastMotionFlags = flags; row.MotionFlagsRecords++;
                if ((flags & 0x40) != 0) row.StopFlagRecords++;
            }
        }

        public object[] Snapshot() => Enum.GetValues<MatchStep>()
            .Where(phase => _phases[(int)phase].Records != 0)
            .Select(phase => _phases[(int)phase].Snapshot(phase)).ToArray();

        public object[] SnapshotAndReset()
        {
            var result = Snapshot();
            ResetWindow();
            return result;
        }

        public void ResetWindow() => Array.Clear(_phases);

        private struct WireVersionTrace
        {
            public bool HasVersion;
            public ulong StreamKey;
            public int Generation;
            public byte Version;
        }

        private struct WirePhaseCounters
        {
            public long Records, ExactFullRecords, PreciseRecords, HeaderOnlyRecords, PositionRecords,
                NonZeroVersionRecords, VersionChanges, MotionFlagsRecords, StopFlagRecords;
            public ushort FirstMask, LastMask;
            public byte FirstVersion, LastVersion;
            public uint FirstClientTime, LastClientTime;
            public uint? FirstMotionFlags, LastMotionFlags;
            public readonly object Snapshot(MatchStep phase) => new
            {
                Phase = phase.ToString(), Records, ExactFullRecords, PreciseRecords, HeaderOnlyRecords, PositionRecords,
                NonZeroVersionRecords, VersionChanges, MotionFlagsRecords, StopFlagRecords,
                FirstMask, LastMask, FirstVersion, LastVersion, FirstClientTime, LastClientTime,
                FirstMotionFlags, LastMotionFlags,
            };
        }
    }

    private sealed class SessionDiagnostics
    {
        public long Id, PeerPoseOffers, VehiclePoseOffers, MovementVersionRequestHeaders, TruncatedMovementVersionRequestHeaders;
        public MovementCounters Player, Managed;
        public MovementTrace PlayerTrace, ManagedTrace;
        public MovementWireDiagnostics? PlayerWire, ManagedWire, PeerOfferWire;
        public long Activity => Player.Received + Managed.Received + PeerPoseOffers + VehiclePoseOffers + MovementVersionRequestHeaders;
        public void ResetWindow()
        {
            Player = default; Managed = default; PeerPoseOffers = VehiclePoseOffers = 0;
            MovementVersionRequestHeaders = TruncatedMovementVersionRequestHeaders = 0;
            PlayerWire?.ResetWindow(); ManagedWire?.ResetWindow(); PeerOfferWire?.ResetWindow();
        }
    }

    private sealed class ProductionDiagnostics
    {
        public long WindowStarted = Stopwatch.GetTimestamp(), NextSessionId;
        public readonly MovementTotals Player = new(), Managed = new();
        public readonly MovementWireDiagnostics PlayerWire = new(), ManagedWire = new(), PeerOfferWire = new();
        public readonly int[] MovementVersionRequestHeaders = new int[Enum.GetValues<MatchStep>().Length];
        public long TruncatedMovementVersionRequestHeaders;
        public readonly DiagnosticTiming ScheduleToContinuation = new(), ContinuationLateness = new(),
            ContinuationToCallback = new(), CallbackDuration = new();
        public readonly DiagnosticTiming WorldPoseWork = new(), PeerPoseEnqueueWork = new(),
            VehiclePoseEnqueueWork = new(), InterestSweepWork = new();
        public long FireRelayAttempts, FireStartOffers, FireStopOffers, ProjectileLaunchOffers, MovementVersionReplies;
        public long PeerPoseOffers, PeerPoseOfferBytes, CompleteSnapshotOffers, OrderedRecordOffers,
            VehiclePoseOffers, PeerBudgetDeferredRecipients, TimersScheduled, TimersExecuted, TimersClosedLinkSkipped,
            InterestCandidates, RelayCandidates, Entered, Left, SpawnBudgetStops;
    }
}
