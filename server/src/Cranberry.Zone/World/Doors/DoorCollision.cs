namespace Cranberry.Zone.World.Doors;

// Door collision (docs/55). The one-paragraph version, because every line below depends on it:
//
//   In ClientProtocol_1148 there is NO collision field, flag or component on the wire. Not in
//   AddLightweightNpc 0xd6, not in LightweightToFullNpc 0xda, and Replication.CreateComponent
//   ea 04 carries only ClientInteractComponent, whose rep-data callback FUN_14146d0a0 enrols the
//   object in the interaction manager and never touches the collision path. The 0xd6 apply
//   FUN_140af2900 builds its actor-spawn flag word from twenty compile-time literals (0x441) and
//   does not even pass the record's own scale. So the ONLY thing the server chooses that can
//   affect collision is the model id it names.
//
//   Collision geometry is named by the actor definition: every .adr carries <CollisionType> and,
//   if it collides, <CollisionData fileName="X.cdt" createAsKinematic="…" useBoundingBox="…"/>,
//   parsed by FUN_1420145e0 / FUN_1420146d0 into the actor definition at +0x2b8 / +0x146 / +0x147
//   / +0x5a0. The client then acquires a collision instance only in FUN_141fe6ad0 (model bind),
//   FUN_141febab0 (LOD change) and FUN_141fd8920 (explicit enable) — each gated on the actor's
//   "static" bit, actor+0x4e1 & 4, whose sole writer in the whole image is
//   FUN_141fdc290(actor, positionUpdateType == 0).
//
//   Cranberry already sends positionUpdateType 0, which IS the collidable value: a non-zero value
//   makes FUN_141fdc290 RELEASE any collision the actor had. That is derived, not guessed
//   (docs/55 §2a, §2d, D55.7) and must not be swept.
//
// What is left, and what this file implements: exactly 33 of the client's 3,406 actor definitions
// are createAsKinematic="1" — the authoring convention for a collision body that moves at run time
// — and 29 of them are Common_Props_Doors_*. Every mesh in the Doors_* family Cranberry spawns is
// createAsKinematic="0" useBoundingBox="0", i.e. static level geometry baked for the zone loader.
// docs/55 §4 C1. This file lets the server spawn the kinematic twin instead.

/// <summary>
/// Which model a door spawns with — the only server-side lever on door collision that exists in
/// 1148 (docs/55 §1, §4).
/// </summary>
public enum DoorCollisionMode
{
    /// <summary>
    /// Spawn the <c>Doors_*</c> mesh the proxy is sized for, always. Wave 4's behaviour exactly, and
    /// the A/B baseline: under it the doors are the right width to the centimetre and — on the
    /// owner's wave-4 play-test — walk-through.
    /// <para>
    /// Keep this reachable. If the kinematic swap turns out not to be the cause (docs/55 §4 C2 — the
    /// static bit is set one step after the model binds, so a door whose mesh is already resident
    /// may miss the acquire whatever model it names), this is what the doors go back to while the
    /// next derivation runs, and it is one option value away rather than a revert.
    /// </para>
    /// </summary>
    VisibleMesh,

    /// <summary>
    /// Spawn the family's kinematic twin (<see cref="DoorKind.CollisionModelId"/>) where it has one,
    /// and the visual mesh where it does not. <b>The default</b>, and Cranberry's answer to the
    /// owner's "doors did spawn but I am able to walk through them".
    /// <para>
    /// The cost is honest and visible: every twin is 0.28–0.31 m narrower than the <c>Doors_*</c>
    /// leaf it replaces, so a door leaves a ≈0.29 m gap at the latch jamb. Collision is worth more
    /// than 29 cm of jamb, and no kinematic actor in this client is any wider —
    /// <see cref="DoorCollision.WidthLossMetres"/> records the measurement rather than hiding it.
    /// </para>
    /// </summary>
    KinematicMesh,
}

/// <summary>
/// The rule that turns a <see cref="DoorKind"/> and a <see cref="DoorCollisionMode"/> into the model
/// id a door's <c>0xd6</c> actually carries, plus the derivation constants that justify it.
/// <para>
/// The twin ids themselves are <b>not</b> here: they live in <c>z2-doors.bin</c> (CRDR v2), resolved
/// by actor name out of the client's own <c>Models.txt</c> by <c>tools/data/gen-doors.py</c>, so a
/// wrong id is impossible and a missing actor is a build failure rather than a silent zero. This
/// type only holds the choice and the arithmetic.
/// </para>
/// </summary>
public static class DoorCollision
{
    /// <summary>
    /// How the twin for each family was chosen (docs/55 §1e and the <c>KINDS</c> table in
    /// <c>gen-doors.py</c>), recorded here because the number below is what a reviewer will want to
    /// check first.
    /// <para>
    /// <b>Gate</b> — the twin must share the <c>Doors_*</c> hinge convention (hinge at local
    /// <c>x ≈ 0</c>, leaf running to <c>−x</c>, base at <c>y ≈ 0</c>) and stand within 10 mm of the
    /// visual mesh's height, measured from the <c>.dme</c> DMOD v4 headers. That is an
    /// <i>elimination</i>: it removes the 2.466–2.468 m kinematic subset, which would stand 21 cm
    /// proud of a 2.26 m Z2 door frame.
    /// <b>Choice</b> — among the survivors, all 2.258 m tall, match <c>MaterialType</c> and name.
    /// Width is not a discriminator because every survivor loses the same
    /// <see cref="WidthLossMetres"/>.
    /// </para>
    /// </summary>
    public const float WidthLossMetres = 0.29f;

    /// <summary>
    /// Kinematic actors in the whole client: 33 of 3,406, of which 29 are
    /// <c>Common_Props_Doors_*</c> and the rest are player-built doors and a gate (docs/55 D55.11).
    /// A census, pinned so a re-extraction that changes it is caught.
    /// </summary>
    public const int KinematicActorCount = 33;

    /// <summary>
    /// The value <c>AddLightweightNpc</c> must carry at <c>+0x11c</c> for a door to be collidable:
    /// <b>0</b>. <c>FUN_140c51c90</c> stores it at <c>entity+0x382c</c> and calls
    /// <c>FUN_140c872c0</c> → <c>FUN_141fdc290(actor, positionUpdateType == 0)</c>, the only writer
    /// of the actor's static bit; non-zero clears that bit and <i>releases</i> collision
    /// (docs/55 §2a, D55.7).
    /// <para>
    /// <b>Re-verified from the 1148 image, docs/85 §2e.</b> <c>FUN_141fdc290</c>'s own bytes:
    /// <c>test dl,dl / jnz +0x3e</c> takes the non-zero (i.e. <c>positionUpdateType != 0</c>) case
    /// straight past the release block, and both paths then run
    /// <c>and byte [rbx+0x4e1],0xfb</c> followed by <c>or byte [rbx+0x4e1],dil</c> with
    /// <c>dil = isStatic &lt;&lt; 2</c> — so a non-zero <c>positionUpdateType</c> ends with
    /// <c>actor+0x4e1</c> bit 2 <em>clear</em>, and that bit is the guard on all four callers of the
    /// collision acquire <c>FUN_141ff4ca0</c>. No static bit, no collision.
    /// </para>
    /// <para>
    /// <b>The owner's Z1 server and the 1087 reference both send <c>1</c> here</b> (47 of 47 doors in
    /// his capture) and his doors are solid, which is why the conflict is recorded rather than
    /// hidden. <c>ZoneOptions.DoorPositionUpdateType</c> keeps <c>1</c> reachable as a negative
    /// control; on this build the binary says it can only hurt.
    /// </para>
    /// <para>
    /// Pinned as a constant so <see cref="AddLightweightDoor.Body"/> and its test name the same
    /// number and a future sweep cannot quietly try 1 or 2.
    /// </para>
    /// </summary>
    public const byte CollidablePositionUpdateType = 0;

    /// <summary>
    /// The model id a door of <paramref name="kind"/> spawns with under
    /// <paramref name="mode"/>.
    /// <para>
    /// Never returns 0 and never throws: a family with no twin, and a CRDR v1 file that has no twin
    /// column at all, both fall back to <see cref="DoorKind.ModelId"/> — the wave-4 behaviour. A
    /// door is always spawned, and the worst case is the walk-through door the owner already has.
    /// </para>
    /// </summary>
    public static uint SpawnModelFor(in DoorKind kind, DoorCollisionMode mode) =>
        mode == DoorCollisionMode.KinematicMesh && kind.HasCollisionModel
            ? kind.CollisionModelId
            : kind.ModelId;

    /// <summary>
    /// Whether a door of <paramref name="kind"/>, spawned under <paramref name="mode"/>, is expected
    /// to collide — i.e. whether the wire will name a kinematic actor.
    /// <para>
    /// This is a <b>prediction, not an observation</b>: it says the server did the one thing it can
    /// do. Whether the client then acquires the collision instance is docs/55 §4 C2's open question
    /// and can only be answered by walking into the door (docs/32, D29 — nothing is LIVE-VERIFIED
    /// without a client-originated packet or a screenshot).
    /// </para>
    /// </summary>
    public static bool IsCollidable(in DoorKind kind, DoorCollisionMode mode) =>
        mode == DoorCollisionMode.KinematicMesh && kind.HasCollisionModel;
}
