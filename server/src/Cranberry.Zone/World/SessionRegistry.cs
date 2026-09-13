using System.Numerics;

namespace Cranberry.Zone.World;

/// <summary>
/// Where one live gateway session's packets go. Deliberately transport-free — nothing in this file
/// names an <c>SoeConnection</c> or a <c>PacketWriter</c> — for the same reason
/// <c>IVehicleObserver</c> is (docs/22 §4.9): the decision of <em>who</em> should be told what is
/// testable without a socket, and the only thing <c>ZoneService</c> supplies is the writing.
/// </summary>
public interface IPeerSink
{
    /// <summary>False once the link is closed. A closed sink is skipped and then swept.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Writes one complete zone packet (from its base opcode byte) to this session. Called on the
    /// caller's thread — the listener thread, in the host — so an implementation must not block.
    /// </summary>
    void Send(byte[] zonePacket);

    void SendPose(ulong entityGuid, uint transientId, ReadOnlySpan<byte> record, bool completeSnapshot) =>
        Send(PeerBurst.Pose(transientId, record));
    void ForgetPose(ulong entityGuid) { }
    void ForgetAllPoses() { }
}

/// <summary>
/// One live session as the peer wire sees it: who the character is, where it last reported itself,
/// what it is wearing, what it is holding, and — per viewer — which other characters it has been
/// told about (<see cref="View"/>).
///
/// <para>
/// <b>This is the per-connection shape, not a world model.</b> S1 §5.4 records that match state
/// today belongs to the connection: <c>GatewaySessionState</c> owns that client's gas, loot, doors,
/// vehicles, inventory and health, and nothing but <c>VehiclePoseBroadcast</c> ever crosses between
/// two of them. Lane 3C adds the second thing that crosses — characters — without re-homing any of
/// the rest, which is a later lane (the plan's 1C/1D). Everything here is therefore a
/// <em>projection</em> of a <c>GatewaySessionState</c>, kept up to date by the handlers that
/// already own those facts, and never a second copy that can disagree in silence: a field that is
/// not refreshed simply keeps its last value, and every one of them is refreshed on a path the
/// client itself drives.
/// </para>
/// </summary>
public sealed class PeerSession
{
    private byte[] _pose = [];
    private int _poseLength;
    private readonly PeerMovementSnapshot _snapshot = new();

    public PeerSession(ulong characterGuid, IPeerSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (characterGuid == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(characterGuid),
                "0 is the null guid; a session that has not been admitted has no character to replicate.");
        }

        CharacterGuid = characterGuid;
        Sink = sink;
    }

    /// <summary>The admitted character guid — the id <c>d5</c>, <c>94 01</c>, <c>0f 45</c> and
    /// <c>0f 01</c> all name this character by.</summary>
    public ulong CharacterGuid { get; }

    /// <summary>
    /// The key <see cref="ObserverView"/> and <see cref="TransientIdTable"/> index this character
    /// by. It is the raw character guid rather than a minted <see cref="EntityId"/> because the
    /// login host's roster keys are exactly what <see cref="EntityKind.None"/> is reserved for
    /// (docs/22 §4.1) and a match never re-mints them; uniqueness — all the two tables need — comes
    /// from the roster.
    /// </summary>
    public EntityId Key => new(CharacterGuid);

    public IPeerSink Sink { get; }

    /// <summary>What this viewer has been told about, and the transient id it knows each by.</summary>
    public ObserverView View { get; } = new();

    /// <summary>
    /// Whether this session is in a world where another player could be drawn. A menu session has
    /// no Z2 actor to attach a peer to, so it neither sees nor is seen.
    /// </summary>
    private bool _inMatch;
    private ulong _matchId;
    internal Action<PeerSession>? MembershipChanged { get; set; }
    internal long MembershipOrder { get; set; }
    public bool InMatch
    {
        get => _inMatch;
        set { if (_inMatch == value) return; _inMatch = value; MembershipChanged?.Invoke(this); }
    }
    public ulong MatchId
    {
        get => _matchId;
        set { if (_matchId == value) return; _matchId = value; MembershipChanged?.Invoke(this); }
    }

    /// <summary>The owned canopy while mounted; zero for an infantry actor.</summary>
    public ulong ParachuteGuid { get; set; }
    internal Dictionary<ulong, (ulong Guid, uint TransientId)> VisibleParachutes { get; } = [];
    internal Dictionary<ulong, (ulong VehicleGuid, int Seat)> VehicleAttachments { get; } = [];

    public string CharacterName { get; set; } = string.Empty;

    /// <summary>The actor definition the self record carried — <c>d5</c>'s rec+0x13c model id.</summary>
    public uint ModelId { get; set; }
    public Movement.FootwearTier Footwear { get; set; }

    /// <summary>Last position the client reported on channel 2. <see cref="HasPose"/> gates it.</summary>
    public Vector3 Position { get; set; }

    /// <summary>A spectator's interest centre, independent of the replicated character/corpse.</summary>
    public Vector3? ObserverPosition { get; set; }
    public Vector3 ViewPosition => ObserverPosition ?? Position;

    /// <summary>Last orientation, as <c>d5</c>'s rec+0x150 vec4. Identity until one is reported.</summary>
    public Vector4 Rotation { get; set; } = new(0f, 0f, 0f, 1f);

    /// <summary>The client's own word for its stance (docs/49 §I4); 0 before the first report.</summary>
    public uint Posture { get; set; }

    /// <summary>The heading field of the last record that carried one.</summary>
    public float Heading { get; set; }

    /// <summary>
    /// The dress rows the last <c>94 01 SetCharacterEquipment</c> for this character carried. A
    /// peer is dressed by re-sending exactly these under the peer's guid — docs/100 step 2's
    /// "remote dress … <c>94 01</c> for another guid".
    /// </summary>
    public IReadOnlyList<CharacterEquipmentAttachment> Dress { get; set; } = [];

    /// <summary>
    /// The inventory instance guid of the gun in this character's right hand, or 0 for empty hands.
    /// It is <c>82 15</c>'s <c>weaponItemInstanceId</c>, which
    /// <c>RemoteWeaponPackets.GuardEquipmentRowGuid</c> refuses to let be 0.
    /// </summary>
    public ulong HeldWeaponItemGuid { get; set; }

    /// <summary>
    /// The held gun's <c>ClientItemDefinitions</c> id — the key the viewer's client resolves in
    /// <c>ReferenceData "WeaponDefinitions"</c> list 0, so a peer's gun and yours resolve through
    /// one table (docs/100 §4).
    /// </summary>
    public uint HeldWeaponDefinitionId { get; set; }
    public uint? WeaponStance { get; set; }
    /// <summary>Validated selection of the held weapon, replayed after a new observer's AddWeapon.</summary>
    public byte HeldFireGroupIndex { get; set; }
    public byte HeldFireModeIndex { get; set; }
    internal int InterestGeneration { get; set; }
    internal Dictionary<ulong, (byte Version, long At)> MovementVersionReplies { get; } = [];

    /// <summary>Channel-2 records this session has had decoded; the relay stride counts it.</summary>
    public long MovementRecords { get; set; }

    /// <summary>
    /// Round-robin cursor over this subject's viewers, so a byte budget that runs out does not
    /// always cut off the same people (docs/22 §6.1, the same rule <c>RelaySystem</c> follows).
    /// </summary>
    public int RelayCursor { get; set; }

    /// <summary>True once a position from this world has been applied; later sparse records retain it.</summary>
    public bool HasPose { get; private set; }

    /// <summary>False once the link is closed.</summary>
    public bool IsOpen => Sink.IsOpen;

    /// <summary>
    /// The client's exact last channel-2 bytes. <b>Never re-encoded</b>: the pose is quantised at
    /// two decimal places, so a decode/re-encode round trip moves a peer for no reason and drops
    /// any field this server does not yet parse (docs/100 §5).
    /// </summary>
    public ReadOnlySpan<byte> Pose => _pose.AsSpan(0, _poseLength);
    /// <summary>The native movement-version byte in the latest accepted record. A peer's d5
    /// baseline must use this version before the client accepts subsequent partial 0x78 poses.</summary>
    public byte MovementVersion => _poseLength >= 7 ? _pose[6] : (byte)0;
    public ReadOnlySpan<byte> RelayPose => _snapshot.Record.IsEmpty ? Pose : _snapshot.Record;
    public bool HasCompleteRelaySnapshot => !_snapshot.Record.IsEmpty;
    public void ResetRelaySnapshot() => _snapshot.Clear();

    /// <summary>World departure invalidates the old actor's position and movement version.
    /// A new-world position must arrive before this session can see or be seen again.</summary>
    public void ResetWorldPose()
    {
        HasPose = false;
        Position = default;
        ObserverPosition = null;
        Rotation = new Vector4(0f, 0f, 0f, 1f);
        Heading = 0f;
        Posture = 0;
        WeaponStance = null;
        MovementVersionReplies.Clear();
        _poseLength = 0;
        _snapshot.Clear();
    }

    /// <summary>Copies one client record in, growing the buffer only when it has to.
    /// Live movement passes whether this record supplied a position; callers with an already
    /// established position retain the default for their explicit registry seeding.</summary>
    public void SetPose(ReadOnlySpan<byte> record, bool hasPosition = true)
    {
        if (record.Length > _pose.Length)
        {
            _pose = new byte[record.Length];
        }

        record.CopyTo(_pose);
        _poseLength = record.Length;
        _snapshot.Update(record);
        HasPose |= hasPosition;
    }

    /// <summary>
    /// A subject can only be spawned into somebody else's world once it is in one itself and has
    /// said where it is. Without the pose gate a peer would be spawned at the map origin and then
    /// teleport in on its first relay.
    /// </summary>
    public bool IsReplicable => IsOpen && InMatch && HasPose;

    public override string ToString() =>
        $"{CharacterName}#{CharacterGuid} at ({Position.X:F1},{Position.Y:F1},{Position.Z:F1})"
        + $" knows {View.KnownCount}";
}

/// <summary>A character that has just entered a viewer's interest, and the id that viewer will know it by.</summary>
public readonly record struct PeerEnter(PeerSession Subject, uint TransientId);

/// <summary>A character that has just left a viewer's interest. The guid is what <c>0f 01</c> names.</summary>
public readonly record struct PeerLeave(ulong CharacterGuid, uint TransientId);

/// <summary>One viewer that currently holds a given subject, and its own id for it.</summary>
public readonly record struct PeerViewer(PeerSession Viewer, uint TransientId);

/// <summary>
/// Every live gateway session on this host, and the interest sweep between them.
///
/// <para>
/// <b>What it is.</b> The one thing lane 3C adds that is genuinely shared: a list of who is
/// connected, where they are and what they look like. It does not own a match, a world or a tick —
/// it is driven from the handlers that already run (the channel-2 decode above all) exactly the way
/// <c>VehiclePoseBroadcast</c> is, and for the same reason: the re-home onto <c>World</c> /
/// <c>Match</c> is a later lane and this must not pre-empt its design.
/// </para>
///
/// <para>
/// <b>The spatial rules are <see cref="InterestSystem"/>'s, unchanged.</b> 310 m to enter, ×1.2 to
/// leave (<see cref="ObserverView.PlayerEnterMetres"/>, D156 under D53); the self-guard skips
/// <c>ReferenceEquals(subject, viewer)</c>; per-viewer spawn and despawn budgets defer rather than
/// drop; and a transient id is released only <em>after</em> the despawn for it has been queued, or
/// a later spawn can reuse an id the client still has bound. What differs is only the container the
/// candidates come out of: a dictionary of live sessions instead of <c>World.Grid</c>, because
/// there is no <c>World</c> on this path yet. At two players a grid query would cost more than the
/// one distance test it replaces; at the 150-player ladder this becomes
/// <see cref="InterestSystem"/> proper, and the enter/leave contract is written here so that
/// swap changes no behaviour.
/// </para>
///
/// <para>Not thread-safe: every caller is the single listener thread, as the sessions themselves
/// already are.</para>
/// </summary>
public sealed class SessionRegistry
{
    private readonly Dictionary<ulong, PeerSession> _byGuid = [];
    private readonly List<PeerSession> _order = [];
    private readonly Dictionary<ulong, List<PeerSession>> _matches = [];
    private readonly Dictionary<PeerSession, ulong> _membership = [];
    private readonly Dictionary<(ulong Match, EntityId Subject), List<PeerViewer>> _viewers = [];
    private long _membershipOrder;
    public long InterestCandidatesVisited { get; private set; }
    public long RelayCandidatesVisited { get; private set; }

    /// <summary>Sessions registered, closed ones included until the next sweep.</summary>
    public int Count => _order.Count;

    /// <summary>Registration order, which is the order a viewer meets candidates in.</summary>
    public IReadOnlyList<PeerSession> Sessions => _order;

    /// <summary>Characters that have entered somebody's view since this registry was created.</summary>
    public long Entered { get; private set; }

    /// <summary>Characters that have left somebody's view.</summary>
    public long Left { get; private set; }

    /// <summary>Enters deferred to a later sweep because a viewer's spawn budget was spent.</summary>
    public long SpawnBudgetStops { get; private set; }

    /// <summary>
    /// Adds a session, replacing any earlier one for the same character. Replacing rather than
    /// appending is what stops a reconnect on the same character from being seen twice — the same
    /// rule <c>VehiclePoseBroadcast.Register</c> follows.
    /// </summary>
    public PeerSession Register(ulong characterGuid, IPeerSink sink)
    {
        var session = new PeerSession(characterGuid, sink) { MembershipChanged = UpdateMembership };
        if (_byGuid.TryGetValue(characterGuid, out PeerSession? existing))
        {
            DetachMembership(existing);
            _order[_order.IndexOf(existing)] = session;
        }
        else
        {
            _order.Add(session);
        }

        _byGuid[characterGuid] = session;
        session.View.Transients.MappingChanged = (id, transientId, added) =>
        {
            if (!_membership.TryGetValue(session, out ulong match)) return;
            if (added) AddViewer(match, session, id, transientId);
            else RemoveViewer(match, session, id);
        };
        return session;
    }

    public PeerSession? Find(ulong characterGuid) =>
        _byGuid.TryGetValue(characterGuid, out PeerSession? session) ? session : null;

    /// <summary>
    /// Removes a session. The caller is expected to have already collected its viewers with
    /// <see cref="ForgetEverywhere"/> and queued their <c>0f 01</c>s — the same post-despawn
    /// ordering <see cref="TransientIdTable.Release"/> documents.
    /// </summary>
    public bool Remove(ulong characterGuid)
    {
        if (!_byGuid.Remove(characterGuid, out PeerSession? session))
        {
            return false;
        }

        _order.Remove(session);
        DetachMembership(session);
        return true;
    }

    private void DetachMembership(PeerSession session)
    {
        session.MembershipChanged = null;
        RemoveMembership(session);
        session.View.Transients.MappingChanged = null;
    }

    private void RemoveMembership(PeerSession session)
    {
        if (!_membership.Remove(session, out ulong match)) return;
        foreach (var mapping in session.View.Transients.Mappings)
            RemoveViewer(match, session, mapping.Key);
        List<PeerSession> members = _matches[match];
        members.Remove(session);
        if (members.Count == 0) _matches.Remove(match);
    }

    private void UpdateMembership(PeerSession session)
    {
        RemoveMembership(session);
        if (!session.InMatch) return;
        if (!_matches.TryGetValue(session.MatchId, out List<PeerSession>? members))
            _matches.Add(session.MatchId, members = []);
        members.Add(session);
        _membership.Add(session, session.MatchId);
        session.MembershipOrder = ++_membershipOrder;
        foreach (var mapping in session.View.Transients.Mappings)
            AddViewer(session.MatchId, session, mapping.Key, mapping.Value);
    }

    private void AddViewer(ulong match, PeerSession viewer, EntityId subject, uint transientId)
    {
        if (subject == viewer.Key) return;
        var key = (match, subject);
        if (!_viewers.TryGetValue(key, out List<PeerViewer>? viewers))
            _viewers.Add(key, viewers = []);
        // Preserve the previous match-member order, including leave/rejoin ordering.
        // Transient acquisition can happen in any order; movement fan-out stays stable.
        int at = viewers.Count;
        while (at > 0 && viewers[at - 1].Viewer.MembershipOrder > viewer.MembershipOrder) at--;
        viewers.Insert(at, new PeerViewer(viewer, transientId));
    }

    private void RemoveViewer(ulong match, PeerSession viewer, EntityId subject)
    {
        var key = (match, subject);
        if (!_viewers.TryGetValue(key, out List<PeerViewer>? viewers)) return;
        for (int i = 0; i < viewers.Count; i++)
            if (ReferenceEquals(viewers[i].Viewer, viewer))
            {
                viewers.RemoveAt(i);
                if (viewers.Count == 0) _viewers.Remove(key);
                return;
            }
    }

    /// <summary>Drops sessions whose link has closed. Cheap and idempotent; called before a sweep.</summary>
    public void SweepClosed()
    {
        for (int index = _order.Count - 1; index >= 0; index--)
        {
            PeerSession session = _order[index];
            if (session.IsOpen)
            {
                continue;
            }

            _order.RemoveAt(index);
            _byGuid.Remove(session.CharacterGuid);
            DetachMembership(session);
        }
    }

    /// <summary>
    /// One viewer's interest pass. Fills <paramref name="enters"/> with the characters that have
    /// just come into range (already marked known, with their ids minted) and
    /// <paramref name="leaves"/> with the ones that have just gone out (already forgotten, ids
    /// released). Both lists are cleared first, so a caller can reuse one pair for every viewer.
    ///
    /// <para><b>Leaves are computed before enters</b> so an id freed this pass is available to a
    /// character entering on the same pass; nothing depends on it, but it keeps the ids dense.</para>
    /// </summary>
    public void Sweep(PeerSession viewer, List<PeerEnter> enters, List<PeerLeave> leaves)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(enters);
        ArgumentNullException.ThrowIfNull(leaves);

        enters.Clear();
        leaves.Clear();

        UpdateLeaves(viewer, leaves);

        // A viewer with no world of its own is not shown anybody. It still runs its leaves above:
        // a player who zones back to the menu must have its peers removed, not retained.
        if (!viewer.IsReplicable)
        {
            return;
        }

        UpdateEnters(viewer, enters);
    }

    private void UpdateEnters(PeerSession viewer, List<PeerEnter> enters)
    {
        ObserverView view = viewer.View;
        int budget = view.SpawnBudgetPerTick;

        if (!_matches.TryGetValue(viewer.MatchId, out List<PeerSession>? members)) return;
        foreach (PeerSession subject in members)
        {
            InterestCandidatesVisited++;
            // The self-guard, from both ends: this reference test is InterestSystem's own, and
            // TransientIdTable additionally reserves ids 0-15 so no peer can ever be handed
            // TransientIdTable.LocalPlayer even if this test were removed (docs/100 §8).
            if (ReferenceEquals(subject, viewer)
                || subject.MatchId != viewer.MatchId
                || !subject.IsReplicable
                || view.Knows(subject.Key)
                || !Within(viewer.ViewPosition, subject.Position, ObserverView.PlayerEnterMetres))
            {
                continue;
            }

            if (budget <= 0)
            {
                // Deferred, not lost: the next pass picks it up. This is what stops fifty people
                // landing in one place from queueing fifty d5 spawn records into one tick.
                SpawnBudgetStops++;
                return;
            }

            budget--;
            view.MarkKnown(subject.Key);
            enters.Add(new PeerEnter(subject, view.Transients.Acquire(subject.Key)));
            Entered++;
        }
    }

    private void UpdateLeaves(PeerSession viewer, List<PeerLeave> leaves)
    {
        ObserverView view = viewer.View;
        int budget = view.DespawnBudgetPerTick;

        // Backwards: MarkForgotten swap-removes, so a lower index is never disturbed.
        for (int index = view.KnownCount - 1; index >= 0 && budget > 0; index--)
        {
            EntityId id = view.KnownAt(index);
            PeerSession? subject = Find(id.Value);
            bool drop = subject is null
                || subject.MatchId != viewer.MatchId
                || !subject.IsReplicable
                || !viewer.IsOpen
                || !viewer.InMatch
                || !Within(viewer.ViewPosition, subject.Position, ObserverView.PlayerLeaveMetres);

            if (!drop)
            {
                continue;
            }

            budget--;

            // Read the id BEFORE the release, and release only after the despawn has been queued —
            // which is what the caller does with this list. Reversing the two lets a later spawn
            // reuse an id the client still has bound (TransientIdTable.Release).
            _ = view.Transients.TryGet(id, out uint transientId);
            view.MarkForgotten(id);
            view.Transients.Release(id);
            leaves.Add(new PeerLeave(id.Value, transientId));
            Left++;
        }
    }

    /// <summary>
    /// Everyone who currently holds <paramref name="subject"/>, with their own id for it — the
    /// fan-out list for a pose relay. The subject is never a viewer of itself.
    /// <paramref name="into"/> is cleared first and is expected to be a caller-owned buffer, so a
    /// relay allocates nothing. The reverse view is updated on transient and membership
    /// changes, so a movement packet visits only existing viewers in this match.
    /// </summary>
    public void CollectViewers(PeerSession subject, List<PeerViewer> into)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();
        if (!_viewers.TryGetValue((subject.MatchId, subject.Key), out List<PeerViewer>? viewers)) return;
        foreach (PeerViewer entry in viewers)
        {
            RelayCandidatesVisited++;
            if (!entry.Viewer.IsOpen)
            {
                continue;
            }

            into.Add(entry);
        }
    }

    /// <summary>
    /// Forgets <paramref name="characterGuid"/> in every other viewer's view and reports who needs
    /// a <c>0f 01</c> for it — the link-close path. The ids are released here, which is correct
    /// because the caller writes the despawns from the list this returns and the character is gone
    /// either way.
    /// </summary>
    public void ForgetEverywhere(ulong characterGuid, List<PeerViewer> into)
    {
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();
        var key = new EntityId(characterGuid);
        foreach (PeerSession viewer in _order)
        {
            if (viewer.CharacterGuid == characterGuid || !viewer.View.Knows(key))
            {
                continue;
            }

            _ = viewer.View.Transients.TryGet(key, out uint transientId);
            viewer.View.MarkForgotten(key);
            viewer.View.Transients.Release(key);
            Left++;
            if (viewer.IsOpen)
            {
                into.Add(new PeerViewer(viewer, transientId));
            }
        }
    }

    /// <summary>
    /// Horizontal distance, the measure every streamer in this tree uses: Y is world height and two
    /// players on different floors of the same building are neighbours, not strangers.
    /// </summary>
    private static bool Within(in Vector3 viewer, in Vector3 subject, float metres) =>
        InterestGrid.HorizontalDistanceSquared(viewer, subject) <= metres * metres;
}
