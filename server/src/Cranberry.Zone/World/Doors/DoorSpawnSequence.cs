namespace Cranberry.Zone.World.Doors;

/// <summary>One packet in the burst that introduces a door to the client.</summary>
public enum DoorSpawnStep
{
    /// <summary><c>0xd6 AddLightweightNpc</c> — the spawn record, always the CLOSED pose.</summary>
    Spawn,

    /// <summary><c>0xda LightweightToFullNpc</c> — the full-NPC promotion. Never optional; see below.</summary>
    FullNpc,

    /// <summary>
    /// <c>0f 1e Character.SetCollidable</c>, emitted as a <c>false</c> then <c>true</c> PAIR —
    /// the collision bit stated a second time, after the entity is complete. Optional
    /// (<c>ZoneOptions.DoorSetCollidable</c>, default off); see point 3 below and
    /// <see cref="SetCollidablePacket"/>.
    /// </summary>
    SetCollidable,


    /// <summary><c>ea 04 Replication.CreateComponent</c> — the one entry that binds <c>[F]</c>.</summary>
    InteractComponent,

    /// <summary><c>0f 0a UpdateCharacterState</c> — re-opens a door this match already opened.</summary>
    StateUpdate,
}

/// <summary>
/// The order <c>ZoneService.SendDoor</c> must emit a door in, as data rather than as statement
/// order, so a test can pin it. Two of the four steps are load-bearing for reasons that are not
/// obvious from reading the sends.
/// <para>
/// <b>1. <see cref="DoorSpawnStep.FullNpc"/> must follow its own <see cref="DoorSpawnStep.Spawn"/>
/// and must never become conditional.</b> Cranberry sends it to answer the client's full-data
/// request proactively, but it has a second effect nobody had connected: the <c>0xda</c> applier
/// <c>FUN_140b02060</c> CLEARS <c>entity+0x37ec &amp; 0x40</c>, the client's "no full data yet" bit
/// (docs/47 §6). The owner's Z1 server shipped the 1087 equivalent in round 37 for exactly that
/// reason and nothing else — a lightweight NPC keeps that bit set, the entity update throttles it,
/// and the door's 785 ms swing advances in a staircase instead of a sweep: his <i>"opens kinda
/// choppy"</i>. So Cranberry has his smooth-tick fix by accident, and the way to keep it is to make
/// the ordering an artefact a test can hold rather than a line someone can move.
/// (docs/79 §3d. That the 1148 NPC update consults that bit <em>before</em> ticking the door
/// controller is derived at 1087 and only inferred here — UNCERTAIN — which is why this is pinned as
/// behaviour rather than asserted as a fact.)
/// </para>
/// <para>
/// <b>3. <see cref="DoorSpawnStep.SetCollidable"/>, when enabled, comes AFTER the promotion and is
/// always a PAIR.</b> docs/85 §5 E5. It exists for one failure mode: the collision bit in the
/// <c>0xd6</c> record is applied by <c>FUN_140c75ef0</c> at spawn-apply time, and that function
/// skips its actor half when the entity's actor is not built yet — so a door whose model is still
/// loading gets the entity flag and no physics body. Restating it later is the countermeasure, and
/// it has to be <c>false</c> then <c>true</c> because <c>FUN_140c75ef0</c> is idempotent
/// (<c>cmp dl,al / je done</c>): a lone <c>true</c> on an entity whose bit the spawn record already
/// set returns at the first branch and touches nothing. The owner's Z1 server sends a lone
/// <c>true</c> and would have hit exactly that trap, which is one reason his copy is parked.
/// </para>
/// <para>
/// <b>3. <see cref="DoorSpawnStep.SetCollidable"/>, when enabled, comes AFTER the promotion and is
/// always a PAIR.</b> docs/85 §5 E5. It exists for one failure mode: the collision bit in the
/// <c>0xd6</c> record is applied by <c>FUN_140c75ef0</c> at spawn-apply time, and that function
/// skips its actor half when the entity's actor is not built yet — so a door whose model is still
/// loading gets the entity flag and no physics body. Restating it later is the countermeasure, and
/// it has to be <c>false</c> then <c>true</c> because <c>FUN_140c75ef0</c> is idempotent
/// (<c>cmp dl,al / je done</c>): a lone <c>true</c> on an entity whose bit the spawn record already
/// set returns at the first branch and touches nothing. The owner's Z1 server sends a lone
/// <c>true</c> and would have hit exactly that trap, which is one reason his copy is parked.
/// </para>
/// <para>
/// <b>2. <see cref="DoorSpawnStep.StateUpdate"/> comes last, and only for an already-open door.</b>
/// The client's door controller ctor <c>FUN_14149d330</c> reads the state bit at construction, when
/// it is necessarily 0, and computes both yaws from the spawn transform there and then — so a door
/// always constructs itself "closed at its spawn yaw" and a door this match has already opened has
/// to be re-opened AFTER the component exists (docs/42 §6a, §9 q3). This is the correct 1148
/// expression of the owner's Z1 <c>SpawnArm</c>; his arming packet itself is not portable, because
/// <c>0f 51 DoorState</c> is the retired <c>REUSE_81</c> slot here and nothing would handle it.
/// </para>
/// </summary>
public static class DoorSpawnSequence
{
    // Eight cached arrays rather than a built list: SendDoor runs once per door in a burst of up to
    // 48, and the sequence depends on exactly three bools.
    private static readonly DoorSpawnStep[][] Sequences = Build();

    private static DoorSpawnStep[][] Build()
    {
        var sequences = new DoorSpawnStep[8][];
        for (int i = 0; i < sequences.Length; i++)
        {
            bool isOpen = (i & 1) != 0;
            bool sendInteractComponent = (i & 2) != 0;
            bool setCollidable = (i & 4) != 0;

            var steps = new List<DoorSpawnStep>(5) { DoorSpawnStep.Spawn, DoorSpawnStep.FullNpc };
            if (setCollidable)
            {
                // ONE step, TWO packets — the false/true pair lives in the sender, so the sequence
                // cannot be read as "two independent SetCollidables, drop one".
                steps.Add(DoorSpawnStep.SetCollidable);
            }

            if (sendInteractComponent)
            {
                steps.Add(DoorSpawnStep.InteractComponent);
            }

            if (isOpen)
            {
                steps.Add(DoorSpawnStep.StateUpdate);
            }

            sequences[i] = steps.ToArray();
        }

        return sequences;
    }

    /// <summary>
    /// The steps for one door, in wire order. Allocation-free: every result is one of eight cached
    /// arrays. With <paramref name="setCollidable"/> false the sequence is byte-for-byte the one
    /// wave 8 shipped.
    /// </summary>
    public static ReadOnlySpan<DoorSpawnStep> For(
        bool isOpen,
        bool sendInteractComponent,
        bool setCollidable = false) =>
        Sequences[(isOpen ? 1 : 0) | (sendInteractComponent ? 2 : 0) | (setCollidable ? 4 : 0)];
}
