using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Cranberry.Zone.World.Doors;

/// <summary>What <see cref="MatchDoors.TryToggle"/> did with a request.</summary>
public enum DoorToggleOutcome
{
    /// <summary>The guid names no door this match has spawned. Almost always ground loot.</summary>
    NotADoor,

    /// <summary>
    /// The second packet of one <c>[F]</c> press (or a repeat inside the swing), absorbed. The door
    /// is unchanged and <b>nothing</b> should be sent.
    /// </summary>
    Absorbed,

    /// <summary>The door flipped; send its <see cref="DoorStateUpdate"/> to everyone who can see it.</summary>
    Toggled,
}

/// <summary>
/// What one tick of the door re-stream pump should do. The three arms are deliberately distinct
/// because folding <see cref="Wait"/> into <see cref="Stop"/> is a defect the verify pass of wave 4
/// found in <c>ZoneService.PumpDoors</c>: <see cref="Stop"/> ends the timer chain for the rest of
/// the match, and only "the match is over" or "doors are switched off" may do that.
/// </summary>
public enum DoorPumpStep
{
    /// <summary>End the chain. The match is over, or door streaming is off.</summary>
    Stop,

    /// <summary>Nothing to do this tick — re-arm and ask again.</summary>
    Wait,

    /// <summary>The player has moved far enough: plan and send another burst, then re-arm.</summary>
    Restream,
}

/// <summary>Tuning for <see cref="MatchDoors"/>. Cranberry's own choices throughout.</summary>
public sealed class MatchDoorOptions
{
    public static MatchDoorOptions Default { get; } = new();

    /// <summary>
    /// How long after an <b>accepted</b> toggle further requests for the same door are absorbed.
    /// <para>
    /// One <c>[F]</c> press produces <b>two</b> c2s packets — <c>09 15 PlayerSelect</c> and, about
    /// 2 ms later, <c>09 07 InteractRequest</c> (docs/42 §3b) — so a server that toggled on each
    /// would open and immediately re-close every door. Loot solves the same problem with a
    /// destructive single-shot claim, which a door cannot use because a door is re-usable; a time
    /// window is the equivalent.
    /// </para>
    /// <para>
    /// <b>That pair is not the binding constraint, and 250 ms — the wave-4 value — was about three
    /// times too short.</b> A <c>[F]</c> press is not one event: while the key is HELD the client
    /// re-fires the whole <c>PlayerSelect → InteractRequest</c> sequence. Two independent
    /// measurements agree on the cadence. The owner's own Z1 server histogrammed 26 toggles over two
    /// sessions on the 1087 client: held-key re-fires at 152–479 ms (640 ms the largest ever seen),
    /// deliberate second presses no closer than 1,010 ms — an empty band between the two. And the
    /// <b>August</b> client's own interaction re-evaluation runs at a median <b>171 ms</b>
    /// (min 135, mean 209, over 39 consecutive sub-second gaps between its 56
    /// <c>Command.InteractCancel 09 08</c> packets in <c>logs/host-20260830-163007.log</c>). At
    /// 250 ms a held key therefore toggles the door two or three more times inside its own animation
    /// and the leaf slams shut.
    /// </para>
    /// <para>
    /// <b>800 ms</b> is not Z1's number transplanted: it is the 1148 client's own swing —
    /// <c>FUN_14149dd10</c> steps a fixed 2 rad/s, so 90° takes 785 ms (docs/42 §6b) — rounded up,
    /// so the guard can never expire while the leaf is still moving, and it sits inside the measured
    /// empty band. That the owner's Z1 round 35 independently settled on exactly 800 from a 1087
    /// decompile of the same stepper is corroboration, not the source. docs/79 §2.
    /// </para>
    /// <para>
    /// The window is per door, not per player: a second player pressing while the leaf is still
    /// moving should not reverse it. It is measured from the last <b>accepted</b> toggle, never from
    /// the last request — measuring from the request would let a held key extend the guard forever
    /// and eat the genuine press that follows the swing (docs/79 §4 E12).
    /// </para>
    /// </summary>
    public int PressWindowMs { get; init; } = 800;

    /// <summary>
    /// Radius for resolving a request by position when its guid is unknown
    /// (<see cref="MatchDoors.TryResolveNearest"/>). <b>0 — off — since docs/79 §4 E9.</b>
    /// <para>
    /// The mechanism was built as insurance against a client that named the doorway rather than the
    /// leaf, with the client's own 3 m interact range widened to 6 to cover every reading of the
    /// <c>09 07</c> float4's anchor (docs/42 §9 open question 4). It never had to fire. Probe D4
    /// pressed door guid <c>4899916394579099649</c> and the August client named it back verbatim
    /// (docs/74 §D4, <c>logs/host-20260830-131819.log:9356-9358</c>), and the owner's own Z1 server
    /// resolves an interaction strictly by guid with no positional fallback anywhere in it.
    /// </para>
    /// <para>
    /// Its live failure mode, meanwhile, is bad: any <c>09 07</c> naming a guid this session does not
    /// recognise — a vehicle, a container, a peer, an object from an earlier match — swings whichever
    /// door happens to be within the radius and returns <c>true</c>, swallowing the dispatcher's
    /// fall-through. <c>ZoneService.TryToggleDoor</c> already had to special-case
    /// <c>LootWorld.IsLootGuid</c> to stop it eating indoor pickups. D78 settled it, and lane 0D
    /// deleted the host switch (<c>CRANBERRY_DOOR_RESOLVE_M</c>) and the <c>ZoneOptions</c> field
    /// that used to restore 6: the shipped value is 0, and <see cref="MatchDoors.TryResolveNearest"/>
    /// answers false for a non-positive radius, so the fallback is unreachable in the server. The
    /// field survives so a test can still prove the resolver works when it is asked for.
    /// </para>
    /// </summary>
    public float ResolveRadiusMetres { get; init; }

    /// <summary>
    /// How a door's yaw is packed for the wire. docs/47 §3 proved the field is a <b>quaternion</b>,
    /// so this defaults to <see cref="DoorRotation.QuaternionYUp"/>; the two Euler packings are
    /// substituted at write time (<see cref="DoorRotationPacking.PackForWire"/>) and only the sign of
    /// the yaw is still open.
    /// </summary>
    public DoorRotation Rotation { get; init; } = DoorRotation.QuaternionYUp;

    /// <summary>
    /// How far, as a fraction of the streaming radius, the player must move before
    /// <see cref="MatchDoors.ShouldRestream"/> asks for another burst (docs/47 §I3).
    /// <para>
    /// Wave 3 armed the doors exactly once per match, at the parachute landing point, so a player who
    /// walked a street never saw another door for the rest of the match. Half the radius is the usual
    /// streaming compromise: at <c>DoorRadius</c> = 60 m it re-runs every 30 m, which keeps the disc
    /// ahead of a sprinting player without re-querying the grid on every movement packet. Registration
    /// is idempotent and keyed by ZONE instance id, so a re-run only ever adds doors — no door is
    /// respawned and no open door forgets it is open.
    /// </para>
    /// </summary>
    public float RestreamFraction { get; init; } = 0.5f;

    /// <summary>
    /// Which model a door spawns with. <b>Cosmetic, not the collision lever</b> — docs/68 §3 R2
    /// superseded docs/55 §4 C1/C2 on this: the model id chooses leaf width, texture set and LOD
    /// ladder, i.e. which actor definition a physics body is built FROM, while
    /// <see cref="SpawnFlags1"/> decides whether one is built at all.
    /// <para>
    /// Defaults to <see cref="DoorCollisionMode.KinematicMesh"/>: the owner's wave-4 report is
    /// "doors did spawn but I am able to walk through them", and the only lever the 1148 protocol
    /// gives a server over collision is the model id it names. The kinematic twin costs about
    /// 0.29 m of leaf width (<see cref="DoorCollision.WidthLossMetres"/>) on the 88.7 % of Z2 doors
    /// that have one; <see cref="DoorCollisionMode.VisibleMesh"/> is the A/B baseline that restores
    /// wave 4 exactly.
    /// </para>
    /// </summary>
    public DoorCollisionMode CollisionMode { get; init; } = DoorCollisionMode.VisibleMesh;

    /// <summary>
    /// The <c>+0x1b1</c> flag byte every door's <c>0xd6</c> carries — bit <b>4</b>
    /// (<see cref="LightweightEntityBody.CollidableFlag"/>) is docs/85 §2a's collision switch and is
    /// the answer to "I am still able to walk through them".
    /// <see cref="LightweightEntityBody.DoorSpawnFlagsDefault"/> (<c>0x30</c> = bit 4 plus the inert
    /// bit 5) by default; <c>0</c> restores the wave-7 record byte for byte.
    /// <para>
    /// A <b>byte</b> rather than a bool on purpose (docs/79 §4 E7): wave 8 shipped <c>0x20</c> here,
    /// the owner walked through the doors anyway, and being able to answer that with an env var
    /// (<c>0x10</c>, <c>0x20</c>, <c>0</c>) instead of a rebuild is what lets one play-test settle
    /// which bit it was.
    /// </para>
    /// <para>
    /// <b>Doors only.</b> A ground item must keep writing <c>0</c> here: an item with a physics body
    /// is an obstacle you cannot step over (docs/68 §5 F1 open question 4). This is also why
    /// <see cref="CollisionMode"/> is no longer the collision lever it is named for — docs/68 §3 R2
    /// settled that the model id chooses leaf width, texture set and LOD ladder, i.e. it chooses
    /// which actor definition a body is built FROM, while this byte decides whether one is built at
    /// all.
    /// </para>
    /// <para>
    /// <b>2026-09-03 (docs/114 §5):</b> the default is now
    /// <see cref="LightweightEntityBody.DoorSpawnFlagsRetail"/> (<c>0x10</c>, the friend server's
    /// own byte, adopted under D53) rather than <c>0x30</c>. Bit 5 was falsified as a lever by
    /// docs/85 §2c, so dropping it moves the record one byte closer to his and changes nothing the
    /// client does. <c>CRANBERRY_DOOR_RETAIL_INTERACT=0</c> restores <c>0x30</c>.
    /// </para>
    /// </summary>
    public byte SpawnFlags1 { get; init; } = LightweightEntityBody.DoorSpawnFlagsRetail;

    /// <summary>
    /// The <c>+0x11c</c> <c>positionUpdateType</c> every door's <c>0xd6</c> carries. See
    /// <c>ZoneOptions.DoorPositionUpdateType</c>: <c>0</c> is the only value the 1148 binary allows
    /// to collide, and non-zero is a negative control, not a candidate.
    /// </summary>
    public byte PositionUpdateType { get; init; } = DoorCollision.CollidablePositionUpdateType;

    /// <summary>
    /// docs/114 §4, AUDIT-doors gap 4. <b>The burst cap counts the doors this burst actually
    /// SPAWNS, not the doors the disc holds.</b>
    /// <para>
    /// <see cref="MatchDoors.RegisterNear"/> used to ask <see cref="Z2Doors.QueryNearest"/> for the
    /// nearest <c>cap</c> doors of the whole disc and then skip the ones already live — so in a town
    /// with more than <c>cap</c> doors inside <c>DoorRadius</c>, doors <c>cap+1</c> onward were never
    /// spawned at all until the player had moved 30 m and the nearest-set changed. A doorway with no
    /// door in it is the visible form of that. With this on, the query window is the whole disc and
    /// the cap is applied to the NEW doors only, so a burst always ends with the nearest <c>cap</c>
    /// doors the client does not already have.
    /// </para>
    /// <para><c>false</c> restores the wave-4 behaviour exactly, for the A/B.</para>
    /// </summary>
    public bool CapCountsNewDoorsOnly { get; init; } = true;

    /// <summary>
    /// docs/114 §4. Metres past which a live door is taken back off the client with <c>0f 01</c>.
    /// <para>
    /// Doors used to be registered and never unregistered, so the live set grew monotonically for
    /// the whole match and a player who walked Z2 accumulated hundreds of door entities the client
    /// paid for for ever. Ground loot has despawned at 90 m since docs/52 §5a — same rule, same
    /// packet, same reason — and the number is his, not a new invention.
    /// </para>
    /// <para><c>0</c> — never despawn — restores the old behaviour.</para>
    /// </summary>
    public float DespawnRadiusMetres { get; init; } = 90f;

    /// <summary>
    /// docs/114 §12 (D264): resolve every door family to one of the <b>two</b> swing sounds the
    /// retail client's own <c>getDoorSound</c> (doorentity.ts) actually plays, so <b>every</b> door
    /// makes a noise on open and close.
    /// <para>
    /// The dataset's per-family <c>Doors.txt</c> row is <b>Cranberry's own choice</b> (C1) and gives
    /// eight of the eleven families an "authored" row — Office 13, CommercialGlass 7, Camper 4,
    /// BathroomStall/Hospital 13, HospitalDouble* 8 — whose composite effect is real but whose FMOD
    /// door bank the retail client never keeps resident, because the reference server never triggers
    /// it: those doors <b>swing in silence</b>. The reference sends only two door sounds
    /// (<c>getDoorSound</c>: model 9903 <c>INDUSTRIAL_DOOR_1</c> → row 12
    /// <c>SFX_Door_Metal_Industrial</c>, every other model → the wooden default row 2
    /// <c>SFX_Door_Wood</c>), the two banks it therefore keeps loaded, and the owner's own Z1 server
    /// derives door sounds the same way (<c>ZoneWorldObjects.DoorDefinitionFor</c>). Adopted under
    /// D53. Any row &gt; 0 still makes a fully working door (docs/42 §6c), so this changes only which
    /// sound plays — never whether the door swings, collides or interacts.
    /// </para>
    /// <para><c>false</c> restores the dataset's authored per-family rows exactly, for the A/B.</para>
    /// </summary>
    public bool RetailDoorSound { get; init; } = true;

    /// <summary>
    /// Family names <see cref="MatchDoors.RegisterNear"/> must not spawn — the runtime half of the
    /// hospital switches (docs/114 §2). Empty by default: every family in <c>z2-doors.bin</c> is
    /// streamed. Compared ordinally against <see cref="DoorKind.Name"/>.
    /// </summary>
    public IReadOnlyList<string> ExcludedKinds { get; init; } = [];

    /// <summary>
    /// <see cref="DespawnRadiusMetres"/> floored at <paramref name="streamRadiusMetres"/> — the
    /// invariant <c>LootStreamOptions.EffectiveDespawnRadiusMetres</c> states and for the identical
    /// reason: with the despawn radius INSIDE the stream radius, a player standing on the boundary
    /// spawns and destroys the same door on alternate bursts for ever. Zero stays zero; that is
    /// "off", not "a very small radius".
    /// </summary>
    public float EffectiveDespawnRadiusMetres(float streamRadiusMetres) =>
        !float.IsFinite(DespawnRadiusMetres) || DespawnRadiusMetres <= 0f
            ? 0f
            : MathF.Max(DespawnRadiusMetres, streamRadiusMetres);
}

/// <summary>
/// One door the server has actually put into a match: the dataset placement plus the ids the spawn
/// packets need and the state bit the client is holding.
/// </summary>
public sealed class DoorInstance
{
    private readonly DoorMotionState _motion;

    internal DoorInstance(
        ulong worldGuid,
        uint transientId,
        int doorIndex,
        in DoorPlacement placement,
        in DoorKind kind,
        DoorCollisionMode collisionMode,
        byte spawnFlags1 = LightweightEntityBody.DoorSpawnFlagsRetail,
        byte positionUpdateType = DoorCollision.CollidablePositionUpdateType,
        DoorMotionState? motion = null)
    {
        _motion = motion ?? new DoorMotionState();
        SpawnFlags1 = spawnFlags1;
        PositionUpdateType = positionUpdateType;
        WorldGuid = worldGuid;
        TransientId = transientId;
        DoorIndex = doorIndex;
        InstanceId = placement.InstanceId;
        Position = placement.Position;
        Yaw = placement.Yaw;
        ModelId = kind.ModelId;
        CollisionModelId = kind.CollisionModelId;
        CollisionMode = collisionMode;
        SpawnModelId = DoorCollision.SpawnModelFor(kind, collisionMode);
        DoorTableId = kind.DoorTableId;
        KindName = kind.Name;
    }

    /// <summary>The guid the client names back in <c>Command.InteractRequest</c>.</summary>
    public ulong WorldGuid { get; }

    /// <summary>Match key for <c>0xda</c> and owner of the <c>ea 04</c> component.</summary>
    public uint TransientId { get; }

    /// <summary>Index into <see cref="Z2Doors.Doors"/>.</summary>
    public int DoorIndex { get; }

    /// <summary>The ZONE instance id — this door's identity across spawns and restarts.</summary>
    public uint InstanceId { get; }

    public Vector3 Position { get; }

    /// <summary>The proxy's yaw, which is the door's <b>closed</b> pose. Never pre-rotated.</summary>
    public float Yaw { get; }

    /// <summary>
    /// The <c>Doors_*</c> mesh the proxy is sized for - this family's <b>visual</b> door, and what
    /// the wire carried before docs/55. Kept separate from <see cref="SpawnModelId"/> so a log line
    /// or a test can say which door the world file asked for and which one actually went out.
    /// </summary>
    public uint ModelId { get; }

    /// <summary>The family's kinematic twin, or 0 when it has none (docs/55 section 1e).</summary>
    public uint CollisionModelId { get; }

    /// <summary>Which of the two <see cref="Spawn"/> will name.</summary>
    public DoorCollisionMode CollisionMode { get; }

    /// <summary>
    /// The model id this door's <c>0xd6</c> actually carries -
    /// <see cref="DoorCollision.SpawnModelFor"/> over <see cref="ModelId"/> and
    /// <see cref="CollisionModelId"/>. Never 0.
    /// </summary>
    public uint SpawnModelId { get; }

    /// <summary>
    /// Whether the wire named a kinematic actor for this door, i.e. whether the server did the one
    /// thing it can do about collision. <b>A prediction, not an observation</b> - see
    /// <see cref="DoorCollision.IsCollidable"/>.
    /// </summary>
    public bool IsCollidable => CollisionModelId != 0 && SpawnModelId == CollisionModelId;

    /// <summary>
    /// The <c>+0x1b1</c> flag byte this door's <c>0xd6</c> carries — bit <c>0x10</c> is the client's
    /// collision switch (docs/85 §2a). Unlike <see cref="IsCollidable"/>, which is a statement about
    /// the <em>model</em>, this is model-independent: under it a Camper door — which has no kinematic
    /// twin — should block too, which makes a camper door the discriminator between "the flag was the
    /// cause" and "the model was" (docs/68 §5 F3 step 2).
    /// </summary>
    public byte SpawnFlags1 { get; }

    /// <summary>The <c>+0x11c</c> <c>positionUpdateType</c> this door's <c>0xd6</c> carries.</summary>
    public byte PositionUpdateType { get; }

    public uint DoorTableId { get; }

    public string KindName { get; }

    /// <summary>
    /// <c>Models.txt</c> row of <c>INDUSTRIAL_DOOR_1</c> — the one door model the retail client's own
    /// <c>getDoorSound</c> (doorentity.ts) does not send the wooden default sound for.
    /// </summary>
    public const uint RetailIndustrialModelId = 9903;

    /// <summary><c>Doors.txt</c> row 12 — <c>SFX_Door_Metal_Industrial_Open/Close</c>.</summary>
    public const uint RetailIndustrialSoundRow = 12;

    /// <summary><c>Doors.txt</c> row 2 — <c>SFX_Door_Wood_Open/Close</c>, <c>getDoorSound</c>'s default.</summary>
    public const uint RetailWoodSoundRow = 2;

    /// <summary>
    /// The <c>Doors.txt</c> row the retail client's own <c>getDoorSound</c> assigns a door of model
    /// <paramref name="modelId"/>: row 12 for the industrial door (9903), the wooden default row 2
    /// for every other door. Both are non-zero and both are one of the only two door sounds the
    /// retail client keeps resident, so both are audible (<see cref="MatchDoorOptions.RetailDoorSound"/>).
    /// </summary>
    public static uint RetailSoundRowFor(uint modelId) =>
        modelId == RetailIndustrialModelId ? RetailIndustrialSoundRow : RetailWoodSoundRow;

    /// <summary>Open/closed, i.e. bit 48 of the client's state word.</summary>
    public bool IsOpen { get => _motion.IsOpen; internal set => _motion.IsOpen = value; }

    /// <summary>Timestamp of the last accepted toggle, for the press window.</summary>
    public long LastToggleMs { get => _motion.LastToggleMs; internal set => _motion.LastToggleMs = value; }

    /// <summary>0 for a legacy scripted toggle, otherwise the retained +90 or -90 degree direction.</summary>
    public int SwingDirection { get => _motion.SwingDirection; internal set => _motion.SwingDirection = value; }

    /// <summary>The <c>0xd6</c> that spawns this door. Always the closed pose (docs/42 §8 rule 3).</summary>
    public AddLightweightDoor Spawn(DoorRotation rotation = DoorRotation.QuaternionYUp) =>
        new(
            WorldGuid,
            TransientId,
            SpawnModelId,
            Position,
            Yaw,
            DoorTableId,
            rotation,
            SpawnFlags1: SpawnFlags1,
            PositionUpdateType: PositionUpdateType);

    /// <summary>
    /// Where this door's collision is expected to come from, for the log. <c>kinematic</c> = the
    /// wire named the family's kinematic twin (the pre-wave-10 lever). <c>flag</c> = the spawn
    /// record carries <c>+0x1b1</c> bit <c>0x10</c> (docs/85 §2a), which is model-independent and
    /// is what actually made doors solid. <c>none</c> = neither, i.e. genuinely walk-through.
    /// </summary>
    public string CollisionSourceLabel =>
        IsCollidable
            ? "kinematic"
            : (SpawnFlags1 & LightweightEntityBody.CollidableFlag) != 0
                ? "flag"
                : "no-collision";

    /// <summary>The shared open state and chosen direction, including for a newly streamed viewer.</summary>
    public DoorStateUpdate StateUpdate() => new(WorldGuid, IsOpen, SwingDirection: SwingDirection);

    public override string ToString() =>
        $"{KindName}#{InstanceId} guid={WorldGuid} transient={TransientId} "
        + $"model={SpawnModelId}{(SpawnModelId == ModelId ? string.Empty : $" (visual {ModelId})")} "
        // Wave 10 (docs/92): "no-collision" was true only while the MODEL was the collision
        // lever. Since wave 9 the lever is the model-independent +0x1b1 bit 0x10, so a
        // VisibleMesh door with that bit set is expected to collide and must not be labelled
        // no-collision - that wording is what made the last two door reports hard to read.
        + $"door={DoorTableId} {CollisionSourceLabel} "
        + $"flags1=0x{SpawnFlags1:x2} "
        + $"{(IsOpen ? "open" : "closed")}";
}

/// <summary>
/// The per-match door state: which doors have been spawned, which are open, and the one rule that
/// makes <c>[F]</c> work — a press toggles a door exactly once (docs/42 §8).
/// <para>
/// <b>Open state outlives the spawn.</b> A door constructs itself closed on the client the moment
/// <c>0xd6</c> lands (<c>FUN_14149d330</c> reads the state bit, which is 0 then), so a player who
/// walks away and back would see a re-closed door. The open set here is keyed by the ZONE instance
/// id rather than by the world guid, so it survives <see cref="Unregister"/> and a later re-spawn
/// with fresh ids; the caller then sends <see cref="DoorInstance.StateUpdate"/> right after the
/// <c>ea 04</c> for any door <see cref="DoorInstance.IsOpen"/> reports open. The joining player sees
/// a 785 ms swing rather than an already-open door — a cosmetic artefact with no packet-level fix,
/// because the state bit cannot be set before the controller exists (docs/42 §9 open question 3).
/// </para>
/// </summary>
public sealed class MatchDoors
{
    /// <summary>
    /// Guid range for door world objects. A range of its own keeps doors clear of the character
    /// guids the login host issues, of <c>LootWorld</c>'s <c>0x20…</c>/<c>0x31…</c> bases and of
    /// <see cref="EntityId"/>'s match-allocated space (top byte 0x01–0x06). The client only ever
    /// echoes a guid back, so the layout is Cranberry's choice, not a derived fact.
    /// </summary>
    public const ulong DefaultWorldGuidBase = 0x4400_0000_0000_0001;

    /// <summary>
    /// Transient-id range. <c>LootWorld</c> starts at 1,000 and steps one per spawned item, so a
    /// base of 1,000,000 is disjoint from any burst a match could plausibly produce, and the client
    /// varint (30 bits) carries it in three bytes.
    /// </summary>
    public const uint DefaultTransientIdBase = 1_000_000;

    private readonly Z2Doors _doors;
    private readonly MatchDoorOptions _options;
    private readonly Dictionary<ulong, DoorInstance> _byGuid = [];
    private readonly Dictionary<int, DoorInstance> _byIndex = [];

    /// <summary>
    /// The doors this match has opened, by ZONE instance id. Keyed by instance id — not by world
    /// guid and not by dataset index — so the state survives a stream-out/stream-in cycle and a
    /// regeneration of <c>z2-doors.bin</c> that reorders it.
    /// <para>
    /// Read and written only when this <see cref="MatchDoors"/> has no <see cref="Shared"/> set.
    /// With one, the open bits live there instead, so a door one player opens is open for
    /// everybody (docs/114 §3).
    /// </para>
    /// </summary>
    private readonly HashSet<uint> _open = [];
    private readonly Dictionary<uint, DoorMotionState> _motion = [];

    /// <summary>
    /// Kind indices <see cref="RegisterNear"/> refuses to spawn, from
    /// <see cref="MatchDoorOptions.ExcludedKinds"/>. Resolved once in the constructor, because the
    /// alternative is a string comparison per candidate door per burst.
    /// </summary>
    private readonly bool[] _excludedKinds;

    private ulong _nextWorldGuid;
    private uint _nextTransientId;
    private Vector3 _lastBurstCentre;
    private bool _hasStreamed;
    private bool _pendingDoors;

    public MatchDoors(
        Z2Doors doors,
        MatchDoorOptions? options = null,
        ulong worldGuidBase = DefaultWorldGuidBase,
        uint transientIdBase = DefaultTransientIdBase,
        SharedMatchDoors? shared = null)
    {
        ArgumentNullException.ThrowIfNull(doors);
        _doors = doors;
        _options = options ?? MatchDoorOptions.Default;
        _nextWorldGuid = worldGuidBase;
        _nextTransientId = transientIdBase;
        Shared = shared;

        _excludedKinds = new bool[doors.Kinds.Length];
        foreach (string name in _options.ExcludedKinds)
        {
            int index = doors.KindIndexOf(name);
            if (index >= 0)
            {
                _excludedKinds[index] = true;
            }
        }
    }

    /// <summary>
    /// The match-scope door state this session is a member of, or null for the pre-docs/114
    /// per-connection behaviour (which is also what a test that does not care about sharing gets).
    /// <para>
    /// Identity, open state, swing direction and the animation guard are shared by all viewers.
    /// Streamed-door membership and the burst anchor remain per connection.
    /// </para>
    /// </summary>
    public SharedMatchDoors? Shared { get; }

    /// <summary>The dataset these doors are drawn from.</summary>
    public Z2Doors Dataset => _doors;

    public MatchDoorOptions Options => _options;

    /// <summary>How many doors are currently spawned.</summary>
    public int Count => _byGuid.Count;

    /// <summary>How many doors this match has left open, spawned or not.</summary>
    public int OpenCount => Shared?.OpenCount ?? _open.Count;

    public IReadOnlyCollection<DoorInstance> Instances => _byGuid.Values;

    /// <summary>Whether <see cref="RegisterNear"/> has ever run for this match.</summary>
    public bool HasStreamed => _hasStreamed;

    /// <summary>Centre of the most recent <see cref="RegisterNear"/>, for <see cref="ShouldRestream"/>.</summary>
    public Vector3 LastBurstCentre => _lastBurstCentre;

    /// <summary>How many door bursts this match has run, for the log line.</summary>
    public int BurstCount { get; private set; }

    /// <summary>Total accepted toggles, for the log line.</summary>
    public long TotalToggles { get; private set; }

    /// <summary>Requests absorbed as the second packet of a press, for the log line.</summary>
    public long AbsorbedRequests { get; private set; }

    /// <summary>Doors taken back off this client by <see cref="UnregisterBeyond"/>, for the log.</summary>
    public long Despawned { get; private set; }

    /// <summary>
    /// How many spawned doors went out naming a kinematic actor (docs/55). The complement -
    /// <c>Count - CollidableCount</c> - is the <c>Camper</c> and <c>BathroomStall</c> families,
    /// which have no twin the client's actor set can supply and stay walk-through.
    /// </summary>
    public int CollidableCount { get; private set; }

    /// <summary>
    /// Puts the dataset door at <paramref name="doorIndex"/> into the match, or returns the instance
    /// already there. Idempotent, so a streaming pass may re-offer the same door every tick without
    /// minting a second guid — which would leave the client holding two doors in one doorway.
    /// </summary>
    public DoorInstance Register(int doorIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(doorIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(doorIndex, _doors.Count);

        if (_byIndex.TryGetValue(doorIndex, out DoorInstance? existing))
        {
            return existing;
        }

        ref readonly DoorPlacement placement = ref _doors[doorIndex];
        // docs/114 §3: with a shared set the ids come from the MATCH, so the same door has the same
        // guid in every session and one 0f 0a is addressable to all of them. Without one this is
        // byte-for-byte the pre-docs/114 allocation, which is what makes the switch a no-op.
        DoorIdentity identity = Shared is { } shared
            ? shared.Identify(doorIndex)
            : new DoorIdentity(_nextWorldGuid++, _nextTransientId++);
        DoorKind kind = _doors.KindOf(placement);
        if (_options.RetailDoorSound)
        {
            // docs/114 §12 (D264): map the family to the one of two sounds the retail client keeps
            // resident, so no door swings in silence. Any row > 0 makes an equally working door
            // (docs/42 §6c), so only the sound moves.
            kind = kind with { DoorTableId = DoorInstance.RetailSoundRowFor(kind.ModelId) };
        }

        var instance = new DoorInstance(
            identity.WorldGuid,
            identity.TransientId,
            doorIndex,
            placement,
            kind,
            _options.CollisionMode,
            _options.SpawnFlags1,
            _options.PositionUpdateType,
            Shared?.MotionFor(placement.InstanceId) ?? LocalMotionFor(placement.InstanceId))
        {
            IsOpen = IsOpen(placement.InstanceId),
        };

        _byGuid.Add(instance.WorldGuid, instance);
        _byIndex.Add(doorIndex, instance);
        if (instance.IsCollidable)
        {
            CollidableCount++;
        }

        return instance;
    }

    /// <summary>
    /// Registers every dataset door within <paramref name="radius"/> of <paramref name="centre"/>,
    /// nearest first, at most <paramref name="cap"/> of them, and appends the ones this call newly
    /// spawned to <paramref name="spawned"/>. Returns how many doors the disc holds in total.
    /// <para>
    /// Nearest-first, not grid order: a cap applied to a grid-order scan keeps a band at one edge of
    /// the disc, which for doors means streaming a row of doors 90 m away instead of the house the
    /// player is standing in front of.
    /// </para>
    /// </summary>
    public int RegisterNear(in Vector3 centre, float radius, int cap, ICollection<DoorInstance> spawned)
    {
        ArgumentNullException.ThrowIfNull(spawned);
        ArgumentOutOfRangeException.ThrowIfNegative(cap);

        // docs/47 §I3: every burst — including one that spawns nothing — moves the re-stream anchor,
        // so a player standing in an already-streamed street does not re-query the grid every tick.
        NoteStreamed(centre);

        if (cap == 0)
        {
            return _doors.Query(centre, radius, []);
        }

        // docs/114 §4. THE WINDOW IS NOT THE CAP. The old code asked for the nearest `cap` doors of
        // the disc and then skipped the ones already live, so a door that was live consumed a slot
        // and a town with more than `cap` doors inside the radius simply never spawned the rest —
        // AUDIT-doors gap 4, and its visible form is a doorway with no door in it. The window is
        // therefore the whole disc, counted first with an allocation-free Query, and the cap is
        // applied to the doors this burst actually SPAWNS. `false` restores the old arithmetic.
        int discCount = _options.CapCountsNewDoorsOnly ? _doors.Query(centre, radius, []) : 0;
        int window = _options.CapCountsNewDoorsOnly ? Math.Max(cap, discCount) : cap;

        int[] rented = ArrayPool<int>.Shared.Rent(window);
        try
        {
            Span<int> found = rented.AsSpan(0, window);
            int matched = _doors.QueryNearest(centre, radius, found);
            int examined = Math.Min(matched, window);
            int minted = 0;

            _pendingDoors = false;
            for (int i = 0; i < examined; i++)
            {
                int doorIndex = found[i];
                // Already on this client, or a family this host is not streaming (docs/114 §2).
                // Neither costs a cap slot: the cap bounds the BURST, and a burst that sends
                // nothing must not be the reason the next door is missing.
                if (_byIndex.ContainsKey(doorIndex) || IsExcluded(doorIndex))
                {
                    continue;
                }

                if (minted == cap)
                {
                    _pendingDoors = true;
                    break;
                }
                spawned.Add(Register(doorIndex));
                minted++;
            }

            return matched;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// docs/114 §4: takes back every live door further than <paramref name="radius"/> metres from
    /// <paramref name="centre"/>, appending each one to <paramref name="streamedOut"/> so the caller
    /// can send its <c>0f 01</c>. Returns how many were taken back.
    /// <para>
    /// This is the loot streamer's own eviction rule, moved onto doors:
    /// <c>MatchLoot.PlanRestream</c> step 1 measures the same horizontal distance against the same
    /// 90 m and <c>ZoneService.EvictGroundLoot</c> sends the same 13-byte
    /// <c>Character.RemovePlayer (0f 01)</c> with <c>effectFlag = 0</c> — a full, silent destroy
    /// whose entity-destroy call sits OUTSIDE the ragdoll branch and whose lookup is by world guid
    /// alone (docs/52 §5a). Destroying the object also unlinks its <c>[F]</c> binding, because
    /// <c>ClientInteractComponent</c>'s destructor splices its <c>ProximityTrackableTemplate</c> out
    /// of the interaction manager's lists (docs/52 §5b), so an evicted door leaves no stale prompt.
    /// </para>
    /// <para>
    /// <b>The open bit is kept</b>, exactly as <see cref="Unregister"/> keeps it: it is held by ZONE
    /// instance id, so a door the player opened and walked away from is still open when he comes
    /// back, and the re-spawn re-sends its <c>0f 0a</c> (<c>DoorSpawnSequence</c>). With a
    /// <see cref="Shared"/> set the ids are kept too, so the door the other player still has on his
    /// client keeps the guid this one is about to forget.
    /// </para>
    /// <para>A non-positive or non-finite radius is "off" and takes nothing back.</para>
    /// </summary>
    public int UnregisterBeyond(in Vector3 centre, float radius, ICollection<DoorInstance> streamedOut)
    {
        ArgumentNullException.ThrowIfNull(streamedOut);

        if (!float.IsFinite(radius) || radius <= 0f || _byGuid.Count == 0)
        {
            return 0;
        }

        float radiusSquared = radius * radius;
        List<DoorInstance>? leaving = null;
        foreach (DoorInstance candidate in _byGuid.Values)
        {
            // Horizontal, the measure every streamer in this tree uses: a door is reachable from
            // the floor it is on, and Z2's Y range would otherwise evict the upstairs of a house.
            float dx = candidate.Position.X - centre.X;
            float dz = candidate.Position.Z - centre.Z;
            if ((dx * dx) + (dz * dz) <= radiusSquared)
            {
                continue;
            }

            (leaving ??= []).Add(candidate);
        }

        if (leaving is null)
        {
            return 0;
        }

        foreach (DoorInstance door in leaving)
        {
            if (Unregister(door.WorldGuid))
            {
                streamedOut.Add(door);
                Despawned++;
            }
        }

        return leaving.Count;
    }

    /// <summary>
    /// Whether another door burst is due at <paramref name="centre"/> — docs/47 §I3, the fix for
    /// "doors exist only where the player landed".
    /// <para>
    /// True before the first burst, and thereafter once the player has moved more than
    /// <see cref="MatchDoorOptions.RestreamFraction"/> × <paramref name="radius"/> from the centre of
    /// the last one. This replaces the wave-3 <c>state.DoorsArmed</c> latch, which was set on the
    /// landing burst and never cleared. Re-running is safe at any frequency:
    /// <see cref="Register"/> is keyed by dataset index and returns the existing instance, so a
    /// re-run mints no second guid for a door already on the client and cannot reset an open door.
    /// </para>
    /// <para>
    /// Distance is measured in the horizontal plane only. A door is reachable from the floor it is
    /// on, and Z2's Y range would otherwise make a player on a hillside re-stream on every step.
    /// </para>
    /// </summary>
    public bool ShouldRestream(in Vector3 centre, float radius)
    {
        if (!_hasStreamed)
        {
            return true;
        }

        if (!float.IsFinite(radius) || radius <= 0f)
        {
            return false;
        }

        float fraction = _options.RestreamFraction;
        if (!float.IsFinite(fraction) || fraction <= 0f)
        {
            // A non-positive fraction is the explicit "stream once per match" setting: wave 3's
            // behaviour, kept reachable so a regression can be A/B'd against it.
            return false;
        }

        if (_pendingDoors) return true;
        float threshold = MathF.Min(30f, radius * fraction);
        float dx = centre.X - _lastBurstCentre.X;
        float dz = centre.Z - _lastBurstCentre.Z;
        return (dx * dx) + (dz * dz) > threshold * threshold;
    }

    /// <summary>
    /// Records that a burst ran at <paramref name="centre"/>. <see cref="RegisterNear"/> does this
    /// itself; it is public so a caller that streams by some other rule can still drive
    /// <see cref="ShouldRestream"/>.
    /// </summary>
    public void NoteStreamed(in Vector3 centre)
    {
        _lastBurstCentre = centre;
        _hasStreamed = true;
        BurstCount++;
    }

    /// <summary>
    /// The decision one tick of <c>ZoneService.PumpDoors</c> must make, as a pure function so it can
    /// be pinned by a test rather than by a live match.
    /// <para>
    /// <paramref name="doors"/> is <b>null until the landing burst has run</b>: <c>state.Doors</c> is
    /// created lazily inside <c>SpawnNearbyDoors</c>, which rides
    /// <c>ZoneOptions.GroundLootDelayMs</c>, while the pump is armed in the same method at
    /// <c>DoorRestreamIntervalMs</c>. At the shipped defaults 3000 &gt; 2000 and the burst wins by a
    /// second — but <c>CRANBERRY_DOOR_RESTREAM_MS</c> is overridable and
    /// <c>GroundLootDelayMs</c> is not, so setting the interval to 1000 (the obvious move when
    /// chasing "the owner sees no doors") used to make the first tick fire with no
    /// <see cref="MatchDoors"/>, take the terminal arm and disable the re-stream for the whole
    /// match, silently. That case is <see cref="DoorPumpStep.Wait"/>, never
    /// <see cref="DoorPumpStep.Stop"/>.
    /// </para>
    /// </summary>
    public static DoorPumpStep NextPumpStep(
        bool inMatch,
        bool sendDoors,
        int restreamIntervalMs,
        MatchDoors? doors,
        Vector3? centre,
        float radius)
    {
        if (!inMatch || !sendDoors || restreamIntervalMs <= 0)
        {
            return DoorPumpStep.Stop;
        }

        if (doors is null || centre is not Vector3 point)
        {
            return DoorPumpStep.Wait;
        }

        return doors.ShouldRestream(point, radius) ? DoorPumpStep.Restream : DoorPumpStep.Wait;
    }

    /// <summary>Takes a door back out of the match. Its open/closed state is kept.</summary>
    public bool Unregister(ulong worldGuid)
    {
        if (!_byGuid.Remove(worldGuid, out DoorInstance? instance))
        {
            return false;
        }

        _byIndex.Remove(instance.DoorIndex);
        if (instance.IsCollidable)
        {
            CollidableCount--;
        }

        return true;
    }

    public bool TryGet(ulong worldGuid, [NotNullWhen(true)] out DoorInstance? instance) =>
        _byGuid.TryGetValue(worldGuid, out instance);

    /// <summary>Whether the door with that ZONE instance id is open, spawned or not.</summary>
    public bool IsOpen(uint instanceId) => Shared?.IsOpen(instanceId) ?? _open.Contains(instanceId);

    private DoorMotionState LocalMotionFor(uint instanceId)
    {
        if (!_motion.TryGetValue(instanceId, out var motion))
            _motion.Add(instanceId, motion = new DoorMotionState { IsOpen = _open.Contains(instanceId) });
        return motion;
    }

    /// <summary>Records a door's open bit in whichever set owns it. True when the bit moved.</summary>
    private bool NoteOpen(uint instanceId, bool isOpen) =>
        Shared is { } shared
            ? shared.SetOpen(instanceId, isOpen)
            : isOpen ? _open.Add(instanceId) : _open.Remove(instanceId);

    /// <summary>Whether this dataset door's family is streamable (docs/114 §2's switches).</summary>
    public bool IsExcluded(int doorIndex) => _excludedKinds[_doors[doorIndex].KindIndex];

    /// <summary>
    /// Resolves one c2s request to a door: by guid first — which is exact, because the guid is the
    /// one this match minted — and only then, when the guid names nothing (an object streamed by an
    /// earlier match, or a client that named the doorway rather than the leaf), by the request's own
    /// world position.
    /// <para>
    /// Returning false is the normal case, not an error: the very same two packets arrive for every
    /// ground-loot pickup, so a dispatcher asks the doors first and falls through to loot.
    /// </para>
    /// </summary>
    public bool TryResolve(DoorToggleRequest request, [NotNullWhen(true)] out DoorInstance? instance)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_byGuid.TryGetValue(request.TargetGuid, out instance))
        {
            return true;
        }

        if (request.Position is { } position)
        {
            return TryResolveNearest(position, _options.ResolveRadiusMetres, out instance);
        }

        instance = null;
        return false;
    }

    /// <summary>
    /// The spawned door nearest <paramref name="centre"/> within <paramref name="radius"/> metres.
    /// A linear scan of the spawned set on purpose: a match holds tens of doors at a time, and the
    /// grid in <see cref="Z2Doors"/> answers a different question — which doors <i>exist</i> near a
    /// point, not which of them this match has put on the wire.
    /// </summary>
    public bool TryResolveNearest(in Vector3 centre, float radius, [NotNullWhen(true)] out DoorInstance? instance)
    {
        instance = null;
        if (!float.IsFinite(radius) || radius <= 0f)
        {
            return false;
        }

        float best = radius * radius;
        foreach (DoorInstance candidate in _byGuid.Values)
        {
            float dx = candidate.Position.X - centre.X;
            float dy = candidate.Position.Y - centre.Y;
            float dz = candidate.Position.Z - centre.Z;
            float distanceSquared = (dx * dx) + (dy * dy) + (dz * dz);
            if (distanceSquared <= best)
            {
                best = distanceSquared;
                instance = candidate;
            }
        }

        return instance is not null;
    }

    /// <summary>
    /// Flips the door named by <paramref name="worldGuid"/>, once per press.
    /// <para>
    /// <paramref name="nowMs"/> is any monotonic millisecond clock the caller already has; it is a
    /// parameter rather than a read of <see cref="Environment.TickCount64"/> so the press window is
    /// testable and so a match tick and a packet handler share one notion of "now".
    /// </para>
    /// </summary>
    public DoorToggleOutcome TryToggle(ulong worldGuid, long nowMs, out DoorInstance? instance, Vector3? openerPosition = null)
    {
        if (!_byGuid.TryGetValue(worldGuid, out instance))
        {
            return DoorToggleOutcome.NotADoor;
        }

        if (instance.LastToggleMs != long.MinValue && nowMs - instance.LastToggleMs < _options.PressWindowMs)
        {
            AbsorbedRequests++;
            return DoorToggleOutcome.Absorbed;
        }

        instance.LastToggleMs = nowMs;
        if (!instance.IsOpen && openerPosition is { } player)
            instance.SwingDirection = DoorSwing.AwayFrom(instance.Position, instance.Yaw, instance.KindName, player);
        instance.IsOpen = !instance.IsOpen;
        NoteOpen(instance.InstanceId, instance.IsOpen);
        TotalToggles++;
        return DoorToggleOutcome.Toggled;
    }

    /// <summary>
    /// Resolve and toggle in one step — the shape a packet handler wants. Returns
    /// <see cref="DoorToggleOutcome.NotADoor"/> when the request was for something else entirely, so
    /// the caller can fall through to ground loot.
    /// </summary>
    public DoorToggleOutcome TryToggle(DoorToggleRequest request, long nowMs, out DoorInstance? instance, Vector3? openerPosition = null)
    {
        if (!TryResolve(request, out instance))
        {
            return DoorToggleOutcome.NotADoor;
        }

        return TryToggle(instance.WorldGuid, nowMs, out instance, openerPosition);
    }

    /// <summary>
    /// Forces a door's state without a press — a scripted opening, a test, or a match reset. Returns
    /// false when the state was already that, in which case nothing needs to be sent: a repeated
    /// <c>0f 0a</c> changes no bit and the client's applier does nothing with it.
    /// </summary>
    public bool SetOpen(ulong worldGuid, bool isOpen, [NotNullWhen(true)] out DoorInstance? instance)
    {
        if (!_byGuid.TryGetValue(worldGuid, out instance) || instance.IsOpen == isOpen)
        {
            instance = null;
            return false;
        }

        instance.IsOpen = isOpen;
        NoteOpen(instance.InstanceId, isOpen);
        return true;
    }

    /// <summary>Drops every spawned door <b>and</b> every open bit; used when a session leaves a world.</summary>
    public void Clear()
    {
        _byGuid.Clear();
        _byIndex.Clear();
        _open.Clear();
        _motion.Clear();
        _hasStreamed = false;
        _lastBurstCentre = default;
        BurstCount = 0;
        CollidableCount = 0;
        Despawned = 0;
    }
}
