using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Match;
using Cranberry.Zone.Weapons;
using Cranberry.Zone.World;

namespace Cranberry.Zone;

/// <summary>
/// Lane 3C — <b>two players in one match</b>. The wiring that turns lane 3B's derived writers
/// (docs/100) into packets one session actually sends about another.
///
/// <para>
/// <b>Everything here is per connection, on purpose.</b> S1 §5.4: match state today belongs to
/// <c>GatewaySessionState</c> — that client's gas, loot, doors, vehicles, inventory and health —
/// and before this lane the only thing that ever crossed between two of them was a driven car's
/// pose (<c>VehiclePoseBroadcast</c>, D25). This lane adds the second thing that crosses,
/// <em>characters</em>, in the same shape: one shared registry of who is connected, fed by the
/// handlers that already own the facts, with no <c>World</c> and no <c>Match</c> in sight. The
/// re-home onto those is a later lane and this deliberately does not pre-empt its design.
/// </para>
///
/// <para>
/// <b>Provably a no-op with one player.</b> Every fan-out here skips the reporter, and the interest
/// sweep skips <c>ReferenceEquals(subject, viewer)</c>, so a single-session host sends not one byte
/// of it — which is what makes <see cref="PeerOptions.Spawn"/> and <see cref="PeerOptions.Relay"/>
/// safe to default on.
/// </para>
/// </summary>
public sealed partial class ZoneService
{
    /// <summary>
    /// Every live gateway session on this host and the interest sweep between them (docs/109 §2).
    /// One per service, for the same reason <see cref="_vehiclePoses"/> is: its whole purpose is to
    /// reach the OTHER sessions.
    /// </summary>
    private readonly SessionRegistry _peers = new();

    /// <summary>
    /// The gas plan the sessions in a match share (docs/109 §5, the D67 minimum). Only the
    /// <c>(seed, matchClockMs)</c> pair is shared; every session still runs its own
    /// <c>GasController</c>, which is deterministic in exactly those two values.
    /// </summary>
    private readonly Dictionary<ulong, SharedMatchGas> _sharedGasMatches = [];
    private readonly Dictionary<GatewaySessionState, ulong> _sharedGasMembership = [];

    /// <summary>Reused buffers so a 10 Hz relay and a 4 Hz sweep allocate nothing per pass.</summary>
    private readonly List<PeerEnter> _peerEnters = [];

    private readonly List<PeerLeave> _peerLeaves = [];

    private readonly List<PeerViewer> _peerViewers = [];

    private readonly List<byte[]> _peerBurst = [];

    /// <summary>
    /// How often a viewer runs its interest pass, in milliseconds. 250 ms is the same 4 Hz the
    /// world re-stream pump already runs at, and the enter radius is 310 m against a 6.6 m/s
    /// sprint — 1.65 m of travel per pass, so nothing can cross the band unseen.
    /// </summary>
    private const int PeerInterestIntervalMs = 250;

    /// <summary>
    /// This session as a peer sink. Exactly the shape <c>SessionVehicleObserver</c> has, and for
    /// the same reason: the simulation side names no connection, and the one thing the service
    /// supplies is the writing.
    /// </summary>
    private sealed class SessionPeerSink(ZoneService service, SoeConnection connection) : IPeerSink
    {
        private readonly Action<ReadOnlyMemory<byte>> _recordPose = message => service._recorder.RecordMessage(connection, "s2c", message.Span);
        public SoeConnection Connection => connection;
        public bool IsOpen => connection.State == ConnectionState.Open;

        public void SendPose(ulong entityGuid, uint transientId, ReadOnlySpan<byte> record, bool completeSnapshot)
        {
            var diagnostics = service._productionDiagnostics;
            long started = diagnostics is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
            int length = 1 + PeerSpawnWriter.PlayerUpdatePositionLength(transientId, record.Length);
            Span<byte> tunnel = length <= 1024 ? stackalloc byte[length] : new byte[length];
            tunnel[0] = new GatewayHeader(GatewayTunnelToClient.Opcode, Channel: 0).ToByte();
            PeerSpawnWriter.WritePlayerUpdatePosition(tunnel[1..], transientId, record);
            service.RecordPeerPoseOffer(connection, length, completeSnapshot, entityGuid, record);
            if (completeSnapshot) connection.SendLatest(entityGuid, tunnel, _recordPose);
            else
            {
                // A precise/unsupported record retains its exact bytes and ordered history.
                connection.FlushLatest(entityGuid);
                service._recorder.RecordMessage(connection, "s2c", tunnel);
                connection.SendBuffered(tunnel);
            }
            diagnostics?.PeerPoseEnqueueWork.RecordTicks(System.Diagnostics.Stopwatch.GetTimestamp() - started);
        }

        public void ForgetPose(ulong entityGuid) => connection.ForgetLatest(entityGuid);
        public void ForgetAllPoses() => connection.ForgetAllLatest();

        public void Send(byte[] zonePacket)
        {
            if (zonePacket.Length > 0)
            {
                // Peer enters, equipment and remote firing share the same ordered reliable stream.
                // Batch all small peer messages, preserving order with at most one tick of delay.
                Span<byte> tunnel = zonePacket.Length <= 1024
                    ? stackalloc byte[zonePacket.Length + 1]
                    : new byte[zonePacket.Length + 1];
                tunnel[0] = new GatewayHeader(GatewayTunnelToClient.Opcode, Channel: 0).ToByte();
                zonePacket.CopyTo(tunnel[1..]);
                service._recorder.RecordMessage(connection, "s2c", tunnel);
                connection.SendBuffered(tunnel);
            }
        }
    }

    /// <summary>
    /// Admission hook: this character is now somebody another client could be told about. Called
    /// once, from <c>HandleLogin</c>, after the guid and the name are known.
    /// <para>
    /// Registering at admission rather than at the drop is what makes the leave path total: a
    /// session that closes in the menu is removed by the same code as one that closes mid-match,
    /// and <see cref="PeerSession.InMatch"/> — not the registration — is what decides whether
    /// anybody is shown anybody.
    /// </para>
    /// </summary>
    private void RegisterPeerSession(SoeConnection connection, GatewaySessionState state)
    {
        if (state.Guid == 0)
        {
            return;
        }

        PeerSession peer = _peers.Register(state.Guid, new SessionPeerSink(this, connection));
        RegisterThrowableSession(connection, state);   // docs/120: a blast reaches players by guid
        peer.CharacterName = state.CharacterName;
        peer.ModelId = _options.ModelIdFor(state.Gender);
        state.Peer = peer;
        _log.Info($"{connection} peers: registered {state.CharacterName}#{state.Guid} "
            + $"({_peers.Count} session(s) on this host) — {_options.Peers.Describe()}");
    }

    /// <summary>
    /// Link-close hook: drop this session and tell everyone who could see it. <c>0f 01</c> goes out
    /// BEFORE the ids are dropped from the registry, which is the same ordering rule
    /// <see cref="TransientIdTable.Release"/> states — the list
    /// <see cref="SessionRegistry.ForgetEverywhere"/> returns is exactly the despawns owed.
    /// </summary>
    private void PeerLinkClosed(SoeConnection connection, GatewaySessionState state)
    {
        if (state.Guid == 0 || state.Peer is null)
        {
            return;
        }

        _peers.ForgetEverywhere(state.Guid, _peerViewers);
        if (_options.Peers.Spawn)
        {
            byte[] despawn = PeerBurst.Leave(state.Guid);
            foreach (PeerViewer viewer in _peerViewers)
            {
                RemovePeerParachute(viewer.Viewer, state.Guid);
                RemoveVehicleAttachment(viewer.Viewer, state.Guid);
                viewer.Viewer.Sink.ForgetPose(state.Guid);
                viewer.Viewer.Sink.Send(despawn);
            }
        }

        int told = _peerViewers.Count;
        _peers.Remove(state.Guid);
        ForgetThrowableSession(state);
        state.Peer = null;
        LeaveSharedGas(state);
        if (told > 0)
        {
            _log.Info($"{connection} peers: link closed — 0f 01 RemovePlayer for {state.Guid} sent to "
                + $"{told} viewer(s); {_peers.Count} session(s) left");
        }
    }

    /// <summary>
    /// Dress hook: the rows the last <c>94 01 SetCharacterEquipment</c> for this character carried,
    /// so a peer can be dressed by re-sending exactly them under this guid (docs/100 step 2).
    /// Also refreshes the held gun, because a weapon change always re-dresses.
    /// </summary>
    /// <returns>
    /// True when the gun in the hand changed (a different instance, or a gun where there was none
    /// and vice versa) - the case a viewer's <c>82 15</c> state has to follow (D322).
    /// </returns>
    private bool NotePeerDress(
        GatewaySessionState state,
        IReadOnlyList<CharacterEquipmentAttachment> dress)
    {
        if (state.Peer is not PeerSession peer)
        {
            return false;
        }

        ulong heldBefore = peer.HeldWeaponItemGuid;
        peer.Dress = dress;
        peer.Footwear = Movement.Footwear.Equipped(state.Inventory);
        peer.CharacterName = state.CharacterName;
        peer.ModelId = _options.ModelIdFor(state.Gender);

        // The equipment-row guid 82 15 needs. Prefer the inventory's own RHand instance — it is the
        // guid the client already keys the item on — and fall back to the legacy held-weapon field
        // for the pre-container path. Fists are not a gun: item 85 Weapon_Empty.adr is the client's
        // representation of an empty hand (PlayerInventory.HandIsFists), and registering it as a
        // remote weapon would put "unarmed" in a peer's hands as though it were a rifle.
        peer.HeldWeaponItemGuid = 0;
        peer.HeldWeaponDefinitionId = 0;
        if (state.Inventory is PlayerInventory inventory
            && inventory.EquipmentSlots.TryGetValue(BodySlots.RightHand, out InventoryItemInstance? held)
            && held.DefinitionId != PlayerInventory.SurvivorFistsItemDefinitionId)
        {
            peer.HeldWeaponItemGuid = held.Guid;
            peer.HeldWeaponDefinitionId = held.DefinitionId;
        }

        bool changed = peer.HeldWeaponItemGuid != heldBefore;
        if (changed)
        {
            state.Combat.Shooter.EndWeaponPresentation(heldBefore);
            peer.HeldFireGroupIndex = (byte)Math.Max(0, state.Combat.Shooter.FireGroupOf(peer.HeldWeaponItemGuid));
            peer.HeldFireModeIndex = (byte)Math.Max(0, state.Combat.Shooter.FireModeOf(peer.HeldWeaponItemGuid));
        }
        return changed;
    }

    /// <summary>
    /// <b>D322 (docs/106 §13): a peer's dress follows its hand.</b> Called from
    /// <c>SendDressTunnel</c> right after a <c>94 01</c> that was NOT suppressed went to the
    /// subject's own client, so every viewer that already has the subject spawned gets the same
    /// dress under the subject's guid - and, when the gun in the hand changed, the enter burst's
    /// <c>82 15</c> pair. Before this the enter burst was the only carrier of a peer's dress, so a
    /// bystander saw a player's outfit and gun as they were at 310 m and nothing after: a skinned
    /// AR-15 picked up and drawn in front of a friend never reached the friend's screen.
    /// <para>
    /// A suppressed dress is byte-identical to the last one (D86), so nothing is owed for it; and
    /// with one session on the host <see cref="SessionRegistry.CollectViewers"/> is empty, so a
    /// single-player match sends not one byte of this.
    /// </para>
    /// </summary>
    private void RelayPeerDress(SoeConnection connection, GatewaySessionState state, bool rearm,
        IReadOnlyList<byte[]>? deltas = null, ulong handItem = 0, CharacterEquipmentAttachment? hand = null)
    {
        if (!_options.Peers.Spawn || !_options.Peers.Redress || state.Peer is not PeerSession subject)
        {
            return;
        }

        _peers.CollectViewers(subject, _peerViewers);
        if (_peerViewers.Count == 0)
        {
            return;
        }

        if (deltas is not null && hand is not null && handItem != 0)
        {
            using var writer = new PacketWriter();
            new Equipment.SetCharacterEquipmentSlot(subject.CharacterGuid, new(BodySlots.RightHand, handItem), hand)
                .WriteForObserverTo(writer, _peerViewers[0].Viewer.CharacterGuid);
            deltas = [.. deltas, writer.Written.ToArray()];
        }

        // The existing in-world 94 02/03 updates name the subject GUID, so the same bytes
        // update observers. Rebuilding every unchanged clothing mesh on a gun draw creates
        // a many-hundred-kilobyte burst per viewer when 150 players draw together.
        byte[] dress = deltas is null ? PeerBurst.Dress(subject) : [];
        byte[] footsteps = Movement.Footwear.AudioPacket(subject.CharacterGuid, subject.Footwear);
        foreach (PeerViewer viewer in _peerViewers)
        {
            PeerBurst.Redress(subject, viewer.TransientId, rearm, dress, footsteps, _peerBurst, deltas);
            foreach (byte[] packet in _peerBurst)
            {
                viewer.Viewer.Sink.Send(packet);
            }
        }

        _log.Info($"{connection} peers: RE-DRESS {subject.CharacterName}#{subject.CharacterGuid} "
            + $"({subject.Dress.Count} meshes) sent to {_peerViewers.Count} viewer(s)"
            + (rearm
                ? subject.HeldWeaponItemGuid != 0
                    ? $" + 82 15 01/02 (item {subject.HeldWeaponDefinitionId} instance "
                        + $"{subject.HeldWeaponItemGuid})"
                    : " + 82 15 01 (empty hands)"
                : string.Empty));
    }

    /// <summary>The peer registry, for tests that stand a viewer next to a session.</summary>
    internal SessionRegistry PeerRegistry => _peers;

    /// <summary>
    /// Match hook: whether this session is in a world another player could be drawn in. A menu
    /// session has no Z2 actor to attach a peer to, so it neither sees nor is seen.
    /// </summary>
    private void NotePeerInMatch(GatewaySessionState state, bool inMatch)
    {
        if (!inMatch) state.Movement.ResetPlayerWorldPose();
        if (state.Peer is PeerSession peer)
        {
            bool entering = inMatch && (!peer.InMatch || peer.MatchId != state.BountyAdmission.MatchId);
            if (!inMatch)
            {
                peer.InterestGeneration++;
                EndPeerParachute(state);
                peer.Sink.ForgetAllPoses();
                peer.ResetWorldPose();
                foreach (ulong rider in peer.VisibleParachutes.Keys.ToArray())
                    RemovePeerParachute(peer, rider);
                foreach (ulong rider in peer.VehicleAttachments.Keys.ToArray())
                    RemoveVehicleAttachment(peer, rider);
                // The departing client destroys this world. Do not retain known peer ids until
                // another movement-driven sweep: menu/next-zone input need not trigger one.
                // Other viewers still use their existing ordered leave/despawn path.
                peer.View.Clear();
            }
            peer.InMatch = inMatch;
            peer.MatchId = inMatch ? state.BountyAdmission.MatchId : 0;
            if (entering && peer.Sink is SessionPeerSink sink)
                ArmIdlePeerInterest(sink.Connection, state, peer, ++peer.InterestGeneration);
        }
    }

    private void ArmIdlePeerInterest(SoeConnection connection, GatewaySessionState state, PeerSession peer, int generation)
    {
        Later(connection, PeerInterestIntervalMs, () =>
        {
            if (!peer.InMatch || peer.InterestGeneration != generation) return;
            RefreshHostedObserverTarget(connection, state);
            if (peer.IsReplicable && _peers.Count > 1) RunPeerInterest(connection, state, peer);
            ArmIdlePeerInterest(connection, state, peer, generation);
        });
    }

    /// <summary>
    /// <b>The channel-2 hook</b>, and the only line lane 3C adds to
    /// <c>ZoneService.HandlePlayerMovement</c>. Three things happen here, in this order:
    /// <list type="number">
    /// <item>the registry's copy of this character's pose is refreshed — the client's <em>exact</em>
    /// bytes, never a re-encode (docs/100 §5);</item>
    /// <item>at most every <see cref="PeerInterestIntervalMs"/>, this session runs its own interest
    /// pass and sends the enter burst / the despawns it produces;</item>
    /// <item>at the relay stride, this record is re-framed as <c>0x78</c> for every viewer that has
    /// this character spawned.</item>
    /// </list>
    /// </summary>
    private void NotePeerMovement(
        SoeConnection connection,
        GatewaySessionState state,
        ClientMovementUpdate update,
        EntityMovementState movement)
    {
        if (state.Peer is not PeerSession peer)
        {
            return;
        }

        long worldStarted = _productionDiagnostics is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
        // EntityMovementState, not the raw record: the client's records are sparse deltas and this
        // is the merged view that retains a field a later packet omitted. A rotation-only record
        // must not move a peer to the origin.
        if (movement.Position is Vector3 position)
        {
            peer.Position = position;
        }

        // Wire 0x200 is native lookInfo, not body rotation (FUN_1423391e0).
        // Use the same derived Euler conversion as the vehicle attitude path:
        // FUN_142339720 -> FUN_140c43490. Never seed d5/d7 with a look vector.
        if (movement.Orientation is float yaw && float.IsFinite(yaw)
            && float.IsFinite(movement.Scalar14C ?? 0) && float.IsFinite(movement.Scalar150 ?? 0))
        {
            Quaternion rotation = Quaternion.CreateFromYawPitchRoll(yaw, movement.Scalar14C ?? 0, movement.Scalar150 ?? 0);
            peer.Rotation = new Vector4(rotation.X, rotation.Y, rotation.Z, rotation.W);
        }

        if (movement.Orientation is float heading)
        {
            peer.Heading = heading;
        }

        if (movement.Posture is uint posture)
        {
            peer.Posture = posture;
        }

        peer.SetPose(update.Payload.Span, hasPosition: update.EffectivePosition is not null);
        peer.MovementRecords++;
        _productionDiagnostics?.WorldPoseWork.RecordTicks(System.Diagnostics.Stopwatch.GetTimestamp() - worldStarted);

        if (_peers.Count < 2)
        {
            // The single-player no-op, stated rather than implied: with nobody else on the host
            // there is no sweep to run and no viewer to relay to.
            return;
        }

        RunPeerInterest(connection, state, peer);
        RelayPeerPose(peer);
    }

    /// <summary>
    /// One viewer's interest pass, throttled to <see cref="PeerInterestIntervalMs"/>. The sweep is
    /// run for the mover only: every session's own client streams channel 2 at 20 Hz once it is
    /// running, so each session drives its own pass and the cost stays linear in the number of
    /// sessions rather than quadratic per record.
    /// </summary>
    private void RunPeerInterest(SoeConnection connection, GatewaySessionState state, PeerSession peer)
    {
        long now = Environment.TickCount64;
        if (now < state.NextPeerInterestMs)
        {
            return;
        }

        state.NextPeerInterestMs = now + PeerInterestIntervalMs;
        // The listener retires links immediately. Do not scan thousands of idle menu links
        // on each moving player's interest pass; Sweep itself rejects closed candidates.
        long sweepStarted = _productionDiagnostics is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
        _peers.Sweep(peer, _peerEnters, _peerLeaves);
        _productionDiagnostics?.InterestSweepWork.RecordTicks(System.Diagnostics.Stopwatch.GetTimestamp() - sweepStarted);

        if (!_options.Peers.Spawn)
        {
            if (_peerEnters.Count > 0 || _peerLeaves.Count > 0)
            {
                _log.Info($"{connection} peers: {_peerEnters.Count} enter(s) and {_peerLeaves.Count} "
                    + $"leave(s) suppressed ({PeerOptions.SpawnVariable}=0)");
            }

            return;
        }

        foreach (PeerLeave leave in _peerLeaves)
        {
            RemovePeerParachute(peer, leave.CharacterGuid);
            RemoveVehicleAttachment(peer, leave.CharacterGuid);
            peer.Sink.ForgetPose(leave.CharacterGuid);
            peer.Sink.Send(PeerBurst.Leave(leave.CharacterGuid));
            _log.Info($"{connection} peers: 0f 01 RemovePlayer guid={leave.CharacterGuid} "
                + $"(transient {leave.TransientId}) — left the {ObserverView.PlayerLeaveMetres:F0} m band");
        }

        foreach (PeerEnter enter in _peerEnters)
        {
            SendPeerEnterBurst(connection, peer, enter);
        }
    }

    /// <summary>
    /// The enter burst — <see cref="PeerBurst.Enter"/>'s bytes, written to one viewer. The ORDER
    /// and the three <c>82 15</c> invariants are client facts and live on that type, where a test
    /// can read them without a socket; what is here is the sending and the log line.
    /// </summary>
    private void SendPeerEnterBurst(SoeConnection connection, PeerSession viewer, PeerEnter enter)
    {
        PeerSession subject = enter.Subject;
        PeerBurst.Enter(subject, enter.TransientId, _peerBurst);
        foreach (byte[] packet in _peerBurst)
        {
            viewer.Sink.Send(packet);
        }

        EnsurePeerParachute(viewer, subject);
        SyncPeerVehicleAttachment(viewer, subject);
        if (_throwableSessions.TryGetValue(subject.CharacterGuid, out var wounded) && wounded.State.BleedEffect != 0)
            SendTunnel(connection, new Vehicles.AddEffectTagCompositeEffect(
                subject.CharacterGuid, wounded.State.BleedEffect).WriteTo);
        if (_throwableSessions.TryGetValue(subject.CharacterGuid, out var burning)
            && burning.State.CharacterFireActive)
            SendTunnel(connection, new Vehicles.AddEffectTagCompositeEffect(
                subject.CharacterGuid, CharacterFireEffectId).WriteTo);

        _log.Info($"{connection} peers: ENTER {subject.CharacterName}#{subject.CharacterGuid} as "
            + $"transient {enter.TransientId} at {FormatPosition(subject.Position)} — d5 + 94 01 "
            + $"({subject.Dress.Count} meshes) + d9 + 82 15 01"
            + (subject.HeldWeaponItemGuid != 0 && subject.HeldWeaponDefinitionId != 0
                ? $" + 82 15 02 (item {subject.HeldWeaponDefinitionId} instance {subject.HeldWeaponItemGuid})"
                : " (empty hands)"));
    }

    /// <summary>
    /// The pose relay: this character's own channel-2 bytes, re-framed as
    /// <c>78 | varint transientId | record</c> for every viewer that has it spawned.
    ///
    /// <para>
    /// <b>The opcode-framed form, not the opcode-free one.</b> Both are proven (docs/100 §5), and
    /// <c>RelaySystem</c> writes the opcode-free one because it writes on channel 2 where the
    /// client synthesises <c>0x78</c> from the channel number. Everything <see cref="SendTunnel"/>
    /// sends goes out on channel 0, so the opcode has to be in the bytes — which is exactly the
    /// framing <c>VehiclePoseRelay</c> has been sending live since D25.
    /// </para>
    ///
    /// <para>
    /// One record in <see cref="PeerOptions.RelayStride"/> goes out (every record by default);
    /// a pass spends at most <see cref="PeerOptions.RelayBudgetBytes"/> and leaves the rest to the
    /// next pose from a round-robin cursor, so a crowded landing zone degrades to a slower rate for
    /// everyone instead of freezing the far half of the view.
    /// </para>
    /// </summary>
    private void RelayPeerPose(PeerSession subject)
    {
        PeerOptions options = _options.Peers;
        int stride = Math.Max(1, options.RelayStride);
        if (!options.Relay || subject.MovementRecords % stride != 0 || subject.Pose.IsEmpty)
        {
            return;
        }

        _peers.CollectViewers(subject, _peerViewers);
        int count = _peerViewers.Count;
        if (count == 0)
        {
            return;
        }

        int budget = options.RelayBudgetBytes;
        int cursor = subject.RelayCursor;
        ReadOnlySpan<byte> pose = options.CoalesceMovement ? subject.RelayPose : subject.Pose;
        for (int n = 0; n < count; n++)
        {
            int index = (cursor + n) % count;
            PeerViewer viewer = _peerViewers[index];
            uint transientId = subject.ParachuteGuid != 0
                ? EnsurePeerParachute(viewer.Viewer, subject)
                : viewer.TransientId;
            if (transientId == 0) continue;
            int bytes = PeerSpawnWriter.PlayerUpdatePositionLength(transientId, pose.Length);
            if (bytes > budget)
            {
                if (_productionDiagnostics is { } diagnostics) diagnostics.PeerBudgetDeferredRecipients += count - n;
                subject.RelayCursor = index;
                return;
            }

            budget -= bytes;
            subject.RelayCursor = index + 1;
            viewer.Viewer.Sink.SendPose(subject.ParachuteGuid != 0 ? subject.ParachuteGuid : subject.CharacterGuid,
                transientId, pose, options.CoalesceMovement && subject.HasCompleteRelaySnapshot);
        }
    }

    /// <summary>
    /// docs/100: the client's own <c>0f 20 WeaponStance</c> is forwarded to its viewers. The packet
    /// names the character by guid, so the relay is the same fourteen bytes with nothing changed —
    /// and without it a peer aims and hip-fires with the wrong upper body on every other screen.
    /// </summary>
    private void RelayPeerStance(SoeConnection connection, GatewaySessionState state, WeaponStance reported)
    {
        if (!_options.Peers.Relay || state.Peer is not PeerSession subject)
        {
            return;
        }

        // A player can aim before an observer enters interest. Retain the validated state
        // even when no viewers currently exist, and replay it after that observer's spawn.
        subject.WeaponStance = reported.Stance;
        _peers.CollectViewers(subject, _peerViewers);
        if (_peerViewers.Count == 0)
        {
            return;
        }

        // The guid is the subject's own, exactly as the client sent it; only a stance for THIS
        // character may be forwarded, or a spoofed guid would move somebody else's arms.
        byte[] stance = new WeaponStance(subject.CharacterGuid, reported.Stance).ToArray();
        foreach (PeerViewer viewer in _peerViewers)
        {
            viewer.Viewer.Sink.Send(stance);
        }

        _log.Info($"{connection} peers: 0f 20 WeaponStance {reported.Stance} forwarded to "
            + $"{_peerViewers.Count} viewer(s)");
    }

    /// <summary>
    /// docs/121 §7 (D335): the shooter's trigger, forwarded to everyone who has him spawned as
    /// <c>82 15 / 04 / 01 RemoteWeaponUpdate.FireState</c> in its point form (docs/20 §3c, [P]).
    /// A START carries the corroborated <c>82 20</c> aim; a STOP is the <c>82 01</c> trigger-up.
    /// The weapon guid is the peer's own right-hand instance - the one the viewer's enter burst
    /// registered with <c>82 15 02 AddWeapon</c> - so a weapon the viewer was never told about is
    /// never named, and a session holding no gun relays nothing.
    /// </summary>
    private void RelayPeerFire(
        SoeConnection connection,
        GatewaySessionState state,
        PeerFireRelay relay,
        Vector3 aimPoint,
        ulong weaponGuid)
    {
        if (_productionDiagnostics is { } attempted) attempted.FireRelayAttempts++;
        PeerOptions options = _options.Peers;
        if (!options.Relay || !options.FireRelay || relay == PeerFireRelay.None
            || state.Peer is not PeerSession subject
            || weaponGuid == 0 || subject.HeldWeaponItemGuid != weaponGuid
            || state.DeathSent || state.Hitpoints == 0)
        {
            return;
        }

        _peers.CollectViewers(subject, _peerViewers);
        if (_peerViewers.Count == 0)
        {
            return;
        }

        bool firing = relay == PeerFireRelay.Start;
        var aim = new Vector4(aimPoint.X, aimPoint.Y, aimPoint.Z, 1f);
        foreach (PeerViewer viewer in _peerViewers)
        {
            viewer.Viewer.Sink.Send(RemoteWeaponPackets.FireState(
                viewer.TransientId, weaponGuid, firing, aim));
            if (_productionDiagnostics is { } offered)
            {
                if (firing) offered.FireStartOffers++; else offered.FireStopOffers++;
            }
        }

        _log.Info($"{connection} peers: 82 15 04 01 FireState {(firing ? "START" : "stop")} for "
            + $"item {subject.HeldWeaponDefinitionId} instance {subject.HeldWeaponItemGuid}"
            + (firing ? $" aimed at {FormatPosition(aimPoint)}" : string.Empty)
            + $" forwarded to {_peerViewers.Count} viewer(s)");
    }

    /// <summary>
    /// <b>D315 (docs/125 §6): <c>82 15 / 04 / 0b ProjectileLaunch</c> to every viewer, on every
    /// accepted <c>82 03 Fire</c> - a shot and a throw alike</b>, with the owner's own payload of
    /// <b>12 zero bytes</b> (<c>ZoneCombat.cs:1069</c> / <c>:1119</c> send <c>new byte[12]</c>).
    /// <para>
    /// This is the one packet the owner's server puts on the wire for a bystander that Cranberry
    /// sent for nothing (docs/121 row 52) except, since D310, a throw - and that one shipped the
    /// client's real projectile id where his ships a zero. D312 makes the zero the shipped value:
    /// what a working 1087 server sends beats an inference about a [BLOCKED] field.
    /// </para>
    /// <para>
    /// A solo session sends none: <see cref="PeerRegistry.CollectViewers"/> skips the subject
    /// itself, so with one player the list is empty and the method returns before writing a byte.
    /// </para>
    /// </summary>
    private void RelayProjectileLaunch(SoeConnection connection, GatewaySessionState state, string what, ulong weaponGuid)
    {
        PeerOptions options = _options.Peers;
        if (!options.Relay || !options.ProjectileLaunch
            || state.Peer is not PeerSession subject
            || weaponGuid == 0 || subject.HeldWeaponItemGuid != weaponGuid
            || state.DeathSent || state.Hitpoints == 0)
        {
            return;
        }

        _peers.CollectViewers(subject, _peerViewers);
        if (_peerViewers.Count == 0)
        {
            return;
        }

        foreach (PeerViewer viewer in _peerViewers)
        {
            viewer.Viewer.Sink.Send(RemoteWeaponPackets.ProjectileLaunch(
                viewer.TransientId, weaponGuid, ProjectileLaunchRelayProjectileId));
            if (_productionDiagnostics is { } diagnostics) diagnostics.ProjectileLaunchOffers++;
        }

        _log.Info($"{connection} peers: 82 15 04 0b ProjectileLaunch ({what}) for item "
            + $"{subject.HeldWeaponDefinitionId} instance {subject.HeldWeaponItemGuid}, 12 zero "
            + $"payload bytes, forwarded to {_peerViewers.Count} viewer(s) (D315)");
    }

    /// <summary>
    /// The <c>u32</c> of <c>82 15 04 0b</c>'s 12-byte payload. <b>Zero</b>, because that is what the
    /// owner's working server writes (<c>new byte[12]</c>) - see <see cref="PeerOptions.ProjectileLaunch"/>.
    /// </summary>
    private const uint ProjectileLaunchRelayProjectileId = 0;

    /// <summary>
    /// Answer full-data requests only for an actor already in this viewer's world.
    /// Promotion is also sent proactively before the arsenal. August ignores a
    /// duplicate d9 once the pending bit clears, so a retry cannot reset a live gun.
    /// </summary>
    private bool NotePeerFullCharacterDataRequest(
        SoeConnection connection, GatewaySessionState state, ulong characterGuid)
    {
        if (state.Peer is { } viewer && SendPeerParachuteFull(viewer, characterGuid)) return true;
        if (state.Combat.Targets.Find(characterGuid) is { FullKit: true } dummy)
        {
            SendTunnel(connection, w => w.WriteRaw(PracticeTargetKit.FullCharacter(dummy)));
            return true;
        }
        if (_peers.Find(characterGuid) is not PeerSession subject) return false;
        if (state.Peer is not { } observer || ReferenceEquals(observer, subject)
            || !observer.InMatch || !subject.IsReplicable || observer.MatchId != subject.MatchId
            || !observer.View.Knows(subject.Key)
            || !observer.View.Transients.TryGet(subject.Key, out uint transient)) return true;
        SendTunnel(connection, w => w.WriteRaw(PeerBurst.FullCharacter(subject, transient)));
        return true;
    }

    /// <summary>
    /// docs/109 §5: open this session's gas on the plan the match is already playing, or open the
    /// plan if this session is the first. <c>GasController</c> is a pure function of
    /// <c>(seed, matchClockMs)</c>, so adopting both makes the second player's schedule identical
    /// to the first's — same phase, same centre, same radius, same timers — with no new gas API and
    /// no traffic between the sessions.
    /// </summary>
    /// <returns>True when an existing plan was adopted.</returns>
    private bool JoinSharedGas(GatewaySessionState state, ref ulong seed, ref long startedAtMs)
    {
        if (!_options.Peers.SharedGasSeed)
        {
            return false;
        }

        ulong matchId = state.BountyAdmission.MatchId;
        if (matchId == 0) return false;
        LeaveSharedGas(state);
        if (!_sharedGasMatches.TryGetValue(matchId, out var plan))
            _sharedGasMatches.Add(matchId, plan = new());
        bool joined = plan.Join(ref seed, ref startedAtMs);
        _sharedGasMembership.Add(state, matchId);
        state.SharedGasJoined = true;
        return joined;
    }

    /// <summary>Drops this session's hold on the shared plan; the last one out forgets it.</summary>
    private void LeaveSharedGas(GatewaySessionState state)
    {
        if (!state.SharedGasJoined)
        {
            return;
        }

        state.SharedGasJoined = false;
        if (_sharedGasMembership.Remove(state, out ulong matchId)
            && _sharedGasMatches.TryGetValue(matchId, out var plan))
        {
            plan.Leave();
            if (!plan.Active) _sharedGasMatches.Remove(matchId);
        }
    }
}
