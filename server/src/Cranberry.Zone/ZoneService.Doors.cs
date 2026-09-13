using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone.Generated;
using Cranberry.Zone.World;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Zone;

/// <summary>
/// docs/114 — the door lane's match-scope half: the state a match's sessions share, the fan-out of
/// one player's <c>0f 0a</c> to the others, the doors of the pre-match lobby, and the despawn that
/// bounds the live set.
///
/// <para>
/// <b>Why this is a file of its own.</b> Everything here is <em>between</em> sessions, and
/// <c>ZoneService.cs</c> is where the per-connection door handling already lives (the burst, the
/// spawn sequence, the toggle). Splitting on that seam is the shape lane 3C already chose for
/// <c>ZoneService.Peers.cs</c> — and it keeps this lane out of a 7,500-line file several other lanes
/// are editing at the same time.
/// </para>
///
/// <para>
/// <b>What it deliberately does not do.</b> It does not touch <see cref="SessionRegistry"/> or the
/// peer sweep. A door's audience is not "everyone within 310 m of me": it is "everyone who has this
/// door spawned", which only <see cref="MatchDoors"/> knows, and asking the door set is both exact
/// and free. The one thing it borrows from lane 3C is <see cref="IPeerSink"/>, because "write these
/// bytes to that session" is the same question and a second interface for it would be vocabulary
/// nobody needs.
/// </para>
/// </summary>
public sealed partial class ZoneService
{
    /// <summary>
    /// The doors the sessions of a match share (docs/114 §3): one identity per door and one open
    /// bit per door, ref-counted by its members. One per service, for the same reason
    /// <c>_sharedGas</c> is: its whole purpose is to reach the OTHER sessions.
    /// </summary>
    private readonly Dictionary<ulong, SharedMatchDoors> _sharedDoorMatches = [];

    /// <summary>Reused fan-out buffer, so a door toggle allocates nothing.</summary>
    private readonly List<IPeerSink> _doorListeners = [];

    /// <summary>Reused eviction buffer for <see cref="MatchDoors.UnregisterBeyond"/>.</summary>
    private readonly List<DoorInstance> _doorsStreamedOut = [];

    /// <summary>
    /// This session's <see cref="MatchDoors"/>, created on first use with every door option this
    /// host is running with, and joined to the match's shared set.
    /// <para>
    /// It replaces the inline <c>state.Doors ??= new MatchDoors(...)</c> the landing burst used to
    /// do, because the lobby burst (docs/114 §1) needs exactly the same construction and two copies
    /// of an eight-argument options record is how the two paths drift apart.
    /// </para>
    /// </summary>
    private MatchDoors EnsureMatchDoors(SoeConnection connection, GatewaySessionState state)
    {
        if (state.Doors is { } existing)
        {
            return existing;
        }

        SharedMatchDoors? shared = null;
        if (_options.SharedMatchDoors)
        {
            ulong matchId = state.BountyAdmission.MatchId;
            if (!_sharedDoorMatches.TryGetValue(matchId, out shared))
                _sharedDoorMatches.Add(matchId, shared = new());
        }
        var doors = new MatchDoors(
            DoorData.Value,
            new MatchDoorOptions
            {
                Rotation = _options.DoorRotation,
                // docs/55 §I1 chose the model and docs/68 §3 R2 demoted the choice to COSMETIC:
                // the physics body is gated by the spawn record's flag byte and by nothing else.
                CollisionMode = _options.DoorCollision,
                // docs/85 §2a: +0x1b1 & 0x10, the client's collision switch. docs/114 §5 drops the
                // adjacent bit 5, which docs/85 §2c had already falsified as a lever.
                SpawnFlags1 = _options.DoorSpawnFlags1,
                // docs/85 §2e / docs/55 §2: 0, the only value that leaves the actor's static bit
                // set. A lever for a negative control, not a candidate.
                PositionUpdateType = _options.DoorPositionUpdateType,
                // docs/79 §4 E1: 800 ms, the client's own 785 ms swing rounded up, so a HELD [F]
                // cannot re-toggle the door inside its own animation.
                PressWindowMs = _options.DoorPressWindowMs,
                // docs/114 §4: the cap counts NEW doors, and the live set is bounded by a despawn.
                CapCountsNewDoorsOnly = _options.DoorCapCountsNewOnly,
                DespawnRadiusMetres = _options.DoorDespawnRadiusMetres,
                // docs/114 §12 (D264): every family resolves one of the two door sounds the retail
                // client keeps resident, so no door swings in silence.
                RetailDoorSound = _options.DoorRetailSound,
                // docs/114 §2: the hospital families, each behind its own switch.
                ExcludedKinds = ExcludedDoorKinds(),
            },
            shared: shared);

        state.Doors = doors;
        if (shared is not null)
        {
            bool joined = shared.Join(doors, new SessionPeerSink(this, connection));
            if (joined)
            {
                _log.Info($"{connection} doors: JOINED the match's doors — the guid space and every "
                    + $"open door are the first session's ({shared}). A door either player "
                    + "opens is now open for both (docs/114 §3).");
            }
        }

        return doors;
    }

    /// <summary>
    /// Door families this host is not streaming — the runtime half of docs/114 §2's two hospital
    /// switches. The names are the generator's own family names, and
    /// <c>AugustDoorTable.DoubleLeafKinds</c> is the generated list of the two-leaf ones, so the
    /// data side and this side cannot disagree about which families those are.
    /// </summary>
    private IReadOnlyList<string> ExcludedDoorKinds()
    {
        if (_options.SpawnHospitalDoors && _options.SpawnHospitalDoubleDoors)
        {
            return [];
        }

        var excluded = new List<string>(3);
        foreach (AugustDoorKind kind in AugustDoorTable.Kinds)
        {
            bool hospital = kind.Kind.StartsWith("Hospital", StringComparison.Ordinal);
            if (!hospital)
            {
                continue;
            }

            bool doubleLeaf = false;
            foreach (string name in AugustDoorTable.DoubleLeafKinds)
            {
                doubleLeaf |= string.Equals(name, kind.Kind, StringComparison.Ordinal);
            }

            if (!_options.SpawnHospitalDoors || (doubleLeaf && !_options.SpawnHospitalDoubleDoors))
            {
                excluded.Add(kind.Kind);
            }
        }

        return excluded;
    }

    /// <summary>
    /// Drops this session's hold on the match's doors; the last one out forgets the guid space and
    /// every open bit, so the next match starts with every door shut. Called wherever
    /// <c>state.Doors.Clear()</c> is — a world change, an abandoned match — because a session that
    /// keeps a member slot it is no longer using is a match that never ends.
    /// </summary>
    private void LeaveSharedDoors(GatewaySessionState state)
    {
        if (state.Doors is { } doors && doors.Shared is not null)
        {
            var shared = doors.Shared;
            shared.Leave(doors);
            if (!shared.Active)
                foreach (ulong key in _sharedDoorMatches.Where(pair => ReferenceEquals(pair.Value, shared)).Select(pair => pair.Key).ToArray())
                    _sharedDoorMatches.Remove(key);
        }
    }

    /// <summary>
    /// docs/114 §3: send one toggled door's <c>0f 0a</c> to every OTHER session that has it spawned.
    /// Returns how many were told.
    /// <para>
    /// The <c>0f 0a</c> only. The <c>09 2d</c> re-prompt that accompanies a press is deliberately
    /// NOT fanned out: <c>FUN_14129e7c0</c> drops any interaction-string reply whose guid is not the
    /// UI's CURRENT interaction target (docs/47 §4d), and the only session for which that is
    /// provably true is the one that just named the guid twice. A bystander's caption catches up on
    /// his client's own ~1 s prompt poll, which is what it does for every other world change.
    /// </para>
    /// <para>
    /// The guid is meaningful across sessions only because <see cref="SharedMatchDoors.Identify"/>
    /// mints it once per match; with <c>CRANBERRY_DOOR_SHARED=0</c> there is no shared set, the
    /// listener list is empty by construction, and this is a no-op.
    /// </para>
    /// </summary>
    private int BroadcastDoorState(GatewaySessionState state, DoorInstance door)
    {
        if (state.Doors is not { Shared: { } shared } doors)
        {
            return 0;
        }

        shared.CollectListeners(doors, door.WorldGuid, _doorListeners);
        if (_doorListeners.Count == 0)
        {
            return 0;
        }

        // Written once and sent verbatim: 22 bytes, the same bytes the presser got.
        DoorStateUpdate update = door.StateUpdate();
        using var writer = new PacketWriter(DoorStateUpdate.Length);
        update.WriteTo(writer);
        byte[] packet = writer.Written.ToArray();

        foreach (IPeerSink listener in _doorListeners)
        {
            listener.Send(packet);
        }

        shared.NoteFanout(_doorListeners.Count);
        return _doorListeners.Count;
    }

    /// <summary>
    /// docs/114 §1: arm the pre-match lobby's own door burst, the way the world arms one after a
    /// parachute landing.
    /// <para>
    /// <b>The lobby has five doors and Cranberry has never shipped them.</b> They were dropped from
    /// the dataset by a height filter (fixed in <c>gen-doors.py</c>) and no burst was armed here at
    /// all — the menu's <c>ClientIsReady</c> arms ground loot and nothing else. They are also the
    /// only doors the friend's live server is on record spawning: five <c>AddLightweightNpc</c>
    /// records in the owner's 2026-08-22 admin capture, matching Z2 proxies 1796604819 /
    /// 1435455604 / 1796604901 / 1796604902 / 1796604910 to 0.070 m and to the last decimal of the
    /// yaw.
    /// </para>
    /// <para>
    /// <b>Deferred, never synchronous.</b> The menu burst is the critical path to PLAY and docs/32
    /// is the cautionary tale about adding packets inside it, so this rides its own
    /// <c>Later</c> exactly as the dev loot drop beside it does, and it is paced through
    /// <see cref="DrainBurst"/> with no <c>ProximateItems</c> republish — a door burst does not
    /// touch the ground world (docs/52 §3f rule 4).
    /// </para>
    /// </summary>
    private void ArmLobbyDoors(SoeConnection connection, GatewaySessionState state, string trigger)
    {
        if (!_options.SendDoors
            || !_options.SendLobbyDoors
            || _options.DoorRadius <= 0f
            || _options.DoorMaxPerBurst <= 0
            || state.LobbyDoorsArmed)
        {
            return;
        }

        state.LobbyDoorsArmed = true;
        Vector4 staging = StagingPosition(state);
        var centre = new Vector3(staging.X, staging.Y, staging.Z);
        int delayMs = Math.Max(0, _options.LobbyDoorDelayMs);
        _log.Info($"{connection} doors: lobby door burst armed by {trigger} in {delayMs} ms — "
            + $"within {_options.DoorRadius} m of the staging spawn {FormatPosition(centre)} "
            + "(docs/114 §1: the five doors the friend's own server spawns in the 2026-08-22 "
            + "admin capture)");

        Later(connection, delayMs, () =>
        {
            var burst = new List<Action>();
            SpawnNearbyDoors(connection, state, trigger, burst, centre);
            if (burst.Count == 0)
            {
                return;
            }

            DrainBurst(connection, state, burst, from: 0, trigger, republishProximateItems: false);
        });
    }

    /// <summary>
    /// docs/114 §4: take back every live door outside the despawn band and queue its
    /// <c>0f 01</c> into <paramref name="burst"/>. Returns how many are leaving.
    /// </summary>
    private int DespawnDistantDoors(
        SoeConnection connection,
        GatewaySessionState state,
        MatchDoors doors,
        in Vector3 centre,
        List<Action> burst)
    {
        float radius = doors.Options.EffectiveDespawnRadiusMetres(_options.DoorRadius);
        if (radius <= 0f)
        {
            return 0;
        }

        int leaving = doors.UnregisterBeyond(centre, radius, _doorsStreamedOut);
        if (leaving == 0)
        {
            return 0;
        }

        foreach (DoorInstance door in _doorsStreamedOut)
        {
            DoorInstance planned = door;
            burst.Add(() => EvictDoor(connection, state, planned));
        }

        return leaving;
    }

    /// <summary>
    /// Destroy one streamed-out door client-side. The same packet and the same reasoning as
    /// <see cref="EvictGroundLoot"/>: <c>Character.RemovePlayer (0f 01)</c> with
    /// <c>effectFlag = 0</c> is a full, silent destroy whose entity-destroy call sits outside the
    /// ragdoll branch and whose lookup is by world guid alone (docs/52 §5a), and destroying the
    /// object unlinks its <c>[F]</c> binding with it (docs/52 §5b).
    /// <para>
    /// The door's OPEN bit is kept — <see cref="MatchDoors.UnregisterBeyond"/> holds it by ZONE
    /// instance id — so walking back re-spawns it open, with the 785 ms swing docs/42 §9 q3 records
    /// as the unavoidable cosmetic cost of a controller that constructs itself closed.
    /// </para>
    /// </summary>
    private void EvictDoor(SoeConnection connection, GatewaySessionState state, DoorInstance door)
    {
        state.FullNpcSent.Remove(door.WorldGuid);
        SendTunnel(connection, writer => new RemovePlayer(door.WorldGuid).WriteTo(writer));
        _log.Info($"{connection} doors: streamed out {door} at {FormatPosition(door.Position)} — "
            + $"RemovePlayer, effectFlag 0; {state.Doors?.Count ?? 0} door(s) still live");
    }
}
