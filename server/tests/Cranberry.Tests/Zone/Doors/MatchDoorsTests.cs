using System.Numerics;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Tests.Zone.Doors;

/// <summary>
/// The door state machine (docs/42 §8). The rule that matters most is the least obvious one: the
/// August client sends <b>two</b> packets for one <c>[F]</c> press, so a server that toggles on each
/// opens and immediately re-closes every door — which is exactly what the owner would see as
/// "doors don't work".
/// </summary>
public sealed class MatchDoorsTests
{
    private static readonly Lazy<Z2Doors> Dataset = new(Z2Doors.LoadDefault);

    private static MatchDoors NewMatch(MatchDoorOptions? options = null) =>
        new(Dataset.Value, options);

    /// <summary>
    /// A streaming pass may offer the same door every tick. Registering twice must hand back the
    /// same instance, not mint a second guid — two doors in one doorway is the failure mode.
    /// </summary>
    [Fact]
    public void RegisteringTheSameDoorTwiceReturnsTheSameInstance()
    {
        MatchDoors match = NewMatch();

        DoorInstance first = match.Register(0);
        DoorInstance again = match.Register(0);

        Assert.Same(first, again);
        Assert.Equal(1, match.Count);
        Assert.Equal(MatchDoors.DefaultWorldGuidBase, first.WorldGuid);
        Assert.Equal(MatchDoors.DefaultTransientIdBase, first.TransientId);
    }

    /// <summary>Every spawned door carries the ids and the pose its dataset placement holds.</summary>
    [Fact]
    public void ASpawnedDoorCarriesItsPlacementAndItsFamilysModelIds()
    {
        Z2Doors doors = Dataset.Value;
        // RetailDoorSound OFF: this pins the raw dataset passthrough of the door id. The default-ON
        // remap to the retail swing sound (D248) is covered by DoorsRetailTests; here the point is
        // that the family's own row reaches the instance and the spawn unchanged.
        MatchDoors match = NewMatch(new MatchDoorOptions { RetailDoorSound = false });

        const int index = 17;
        DoorPlacement placement = doors[index];
        DoorKind kind = doors.Kinds[placement.KindIndex];
        DoorInstance instance = match.Register(index);

        Assert.Equal(placement.InstanceId, instance.InstanceId);
        Assert.Equal(placement.Position, instance.Position);
        Assert.Equal(placement.Yaw, instance.Yaw);
        Assert.Equal(kind.ModelId, instance.ModelId);
        Assert.Equal(kind.DoorTableId, instance.DoorTableId);
        Assert.Equal(kind.Name, instance.KindName);
        Assert.False(instance.IsOpen);

        // The spawn packet is built from the CLOSED pose: the controller reads the state bit at
        // construction (0 then) and would treat a pre-rotated transform as the open pose.
        AddLightweightDoor spawn = instance.Spawn();
        Assert.Equal(placement.Yaw, spawn.Yaw);
        Assert.Equal(kind.DoorTableId, spawn.DoorTableId);
        Assert.Equal(instance.WorldGuid, spawn.Guid);
        Assert.Equal(instance.TransientId, spawn.TransientId);
    }

    /// <summary>
    /// <b>The rule.</b> One press = <c>09 15 PlayerSelect</c> then, 2 ms later,
    /// <c>09 07 InteractRequest</c> for the same guid. The door must end up open, not open-then-shut.
    /// </summary>
    [Fact]
    public void OnePressTogglesOnceEvenThoughTheClientSendsTwoPackets()
    {
        MatchDoors match = NewMatch();
        DoorInstance door = match.Register(0);

        var select = new DoorToggleRequest(DoorRequestKind.PlayerSelect, door.WorldGuid, 0x1003);
        var interact = new DoorToggleRequest(DoorRequestKind.InteractRequest, door.WorldGuid);

        Assert.Equal(DoorToggleOutcome.Toggled, match.TryToggle(select, nowMs: 1_000, out DoorInstance? opened));
        Assert.Same(door, opened);
        Assert.True(door.IsOpen);

        Assert.Equal(DoorToggleOutcome.Absorbed, match.TryToggle(interact, nowMs: 1_002, out _));
        Assert.True(door.IsOpen);

        Assert.Equal(1, match.TotalToggles);
        Assert.Equal(1, match.AbsorbedRequests);
    }

    /// <summary>
    /// The window absorbs a press's echo, not a real second press. Just past it, the door closes
    /// again — the swing itself takes ≈785 ms on the client, so the window sits well inside it.
    /// </summary>
    [Fact]
    public void ASecondPressAfterTheWindowClosesTheDoorAgain()
    {
        MatchDoors match = NewMatch();
        DoorInstance door = match.Register(0);
        long window = match.Options.PressWindowMs;

        Assert.Equal(DoorToggleOutcome.Toggled, match.TryToggle(door.WorldGuid, 0, out _));
        Assert.True(door.IsOpen);

        Assert.Equal(DoorToggleOutcome.Absorbed, match.TryToggle(door.WorldGuid, window - 1, out _));
        Assert.True(door.IsOpen);

        Assert.Equal(DoorToggleOutcome.Toggled, match.TryToggle(door.WorldGuid, window, out _));
        Assert.False(door.IsOpen);
        Assert.Equal(2, match.TotalToggles);
    }

    /// <summary>
    /// A ground-loot pickup sends the very same two packets, so an unknown guid is the normal case
    /// and must fall through cleanly rather than being logged as an error.
    /// </summary>
    [Fact]
    public void AGuidThatIsNotADoorFallsThroughToTheCaller()
    {
        MatchDoors match = NewMatch();
        match.Register(0);

        var lootPickup = new DoorToggleRequest(
            DoorRequestKind.InteractRequest,
            TargetGuid: 0x2000_0000_0000_0008UL);   // LootWorld's own guid range

        Assert.Equal(DoorToggleOutcome.NotADoor, match.TryToggle(lootPickup, 0, out DoorInstance? instance));
        Assert.Null(instance);
        Assert.Equal(0, match.TotalToggles);
        Assert.Equal(0, match.AbsorbedRequests);
    }

    /// <summary>
    /// docs/42 §9 open question 3. A door constructs itself <b>closed</b> the moment its <c>0xd6</c>
    /// lands, so an open door that streams out and back must be re-opened by the server. Keying the
    /// open set on the ZONE instance id — not the world guid, which is re-minted — is what makes
    /// that survive.
    /// </summary>
    [Fact]
    public void AnOpenDoorIsStillOpenAfterStreamingOutAndBackWithFreshIds()
    {
        MatchDoors match = NewMatch();
        DoorInstance first = match.Register(0);
        uint instanceId = first.InstanceId;

        Assert.Equal(DoorToggleOutcome.Toggled, match.TryToggle(first.WorldGuid, 0, out _));
        Assert.True(match.IsOpen(instanceId));

        Assert.True(match.Unregister(first.WorldGuid));
        Assert.Equal(0, match.Count);
        Assert.Equal(1, match.OpenCount);            // the bit outlives the spawn
        Assert.True(match.IsOpen(instanceId));

        DoorInstance again = match.Register(0);
        Assert.NotEqual(first.WorldGuid, again.WorldGuid);
        Assert.NotEqual(first.TransientId, again.TransientId);
        Assert.Equal(instanceId, again.InstanceId);
        Assert.True(again.IsOpen);

        // …which is what the caller sends straight after the ea 04.
        Assert.True(again.StateUpdate().IsOpen);
        Assert.Equal(again.WorldGuid, again.StateUpdate().DoorGuid);
    }

    /// <summary>
    /// The positional fallback still works when it is asked for: an unknown guid resolves to the
    /// nearest spawned door within the radius. <b>It has to be asked for now</b> — docs/79 §4 E9
    /// turned the default off, so this test opts in explicitly rather than relying on the default,
    /// which is what <see cref="AnUnknownGuidResolvesToNothingByDefault"/> pins.
    /// </summary>
    [Fact]
    public void AnUnknownGuidIsResolvedByThePositionInTheRequestWhenTheFallbackIsOn()
    {
        MatchDoors match = NewMatch(new MatchDoorOptions { ResolveRadiusMetres = 6f });
        DoorInstance door = match.Register(0);

        var byPosition = new DoorToggleRequest(
            DoorRequestKind.InteractRequest,
            TargetGuid: 0xDEAD_BEEFUL,
            Point: new Vector4(door.Position.X + 1f, door.Position.Y, door.Position.Z, 1f),
            HasPoint: true);

        Assert.True(match.TryResolve(byPosition, out DoorInstance? resolved));
        Assert.Same(door, resolved);
        Assert.Equal(DoorToggleOutcome.Toggled, match.TryToggle(byPosition, 0, out _));
        Assert.True(door.IsOpen);
    }

    /// <summary>A position far from every spawned door resolves to nothing, not to the nearest one.</summary>
    [Fact]
    public void APositionOutsideTheResolveRadiusResolvesToNothing()
    {
        // Explicit radius: with the shipped default of 0 this assertion would be vacuously true
        // (TryResolveNearest answers false for any non-positive radius) and would stop testing the
        // range check it is named for.
        MatchDoors match = NewMatch(new MatchDoorOptions { ResolveRadiusMetres = 6f });
        DoorInstance door = match.Register(0);
        float outside = match.Options.ResolveRadiusMetres + 1f;

        var faraway = new DoorToggleRequest(
            DoorRequestKind.InteractRequest,
            TargetGuid: 0xDEAD_BEEFUL,
            Point: new Vector4(door.Position.X + outside, door.Position.Y, door.Position.Z, 1f),
            HasPoint: true);

        Assert.False(match.TryResolve(faraway, out DoorInstance? resolved));
        Assert.Null(resolved);
    }

    /// <summary>
    /// A <c>09 15</c> carries no position, so an unknown guid on it has nothing to fall back to and
    /// must not be resolved by guesswork.
    /// </summary>
    [Fact]
    public void APlayerSelectWithAnUnknownGuidResolvesToNothing()
    {
        MatchDoors match = NewMatch();
        match.Register(0);

        var select = new DoorToggleRequest(DoorRequestKind.PlayerSelect, 0xDEAD_BEEFUL, 0x1003);
        Assert.False(match.TryResolve(select, out DoorInstance? resolved));
        Assert.Null(resolved);
    }

    /// <summary>
    /// The streaming entry point takes the <b>nearest</b> doors, because a cap applied to a
    /// grid-order scan keeps a band at the far edge of the disc rather than the house in front of
    /// the player, and because re-running it must not re-spawn what is already there.
    /// </summary>
    [Fact]
    public void RegisterNearTakesTheNearestDoorsAndIsIdempotent()
    {
        Z2Doors doors = Dataset.Value;
        MatchDoors match = NewMatch();
        Vector3 centre = doors[doors.Count / 2].Position;

        var spawned = new List<DoorInstance>();
        int matched = match.RegisterNear(centre, 128f, cap: 8, spawned);

        Assert.True(matched >= spawned.Count);
        Assert.True(spawned.Count > 0);
        Assert.Equal(spawned.Count, match.Count);

        float previous = -1f;
        foreach (DoorInstance instance in spawned)
        {
            float distance = Horizontal(instance.Position, centre);
            Assert.True(distance >= previous, "RegisterNear spawned out of distance order");
            Assert.True(distance <= 128f);
            previous = distance;
        }

        // docs/114 §4: the second run is idempotent PER DOOR — no door already on this client is
        // re-minted — but it is no longer a no-op, because the cap now counts the doors a burst
        // SPAWNS rather than the doors the disc holds. That is the fix: under the old arithmetic
        // the eight live doors consumed the whole window and doors 9+ were never sent at all.
        var second = new List<DoorInstance>();
        Assert.Equal(matched, match.RegisterNear(centre, 128f, cap: 8, second));
        Assert.Equal(spawned.Count + second.Count, match.Count);
        foreach (DoorInstance repeat in second)
        {
            Assert.DoesNotContain(spawned, first => first.WorldGuid == repeat.WorldGuid);
        }

        // The old arithmetic, still reachable as CRANBERRY_DOOR_CAP_NEW_ONLY=0, IS a no-op here.
        var legacy = new MatchDoors(
            doors,
            new MatchDoorOptions { CapCountsNewDoorsOnly = false });
        var legacyFirst = new List<DoorInstance>();
        var legacySecond = new List<DoorInstance>();
        legacy.RegisterNear(centre, 128f, cap: 8, legacyFirst);
        legacy.RegisterNear(centre, 128f, cap: 8, legacySecond);
        Assert.NotEmpty(legacyFirst);
        Assert.Empty(legacySecond);
        Assert.Equal(legacyFirst.Count, legacy.Count);
    }

    /// <summary>
    /// <c>SetOpen</c> to the state a door is already in changes nothing and says so: a repeated
    /// <c>0f 0a</c> flips no bit on the client either, so sending one would be pure noise.
    /// </summary>
    [Fact]
    public void SettingADoorToTheStateItIsAlreadyInSendsNothing()
    {
        MatchDoors match = NewMatch();
        DoorInstance door = match.Register(0);

        Assert.False(match.SetOpen(door.WorldGuid, isOpen: false, out DoorInstance? unchanged));
        Assert.Null(unchanged);

        Assert.True(match.SetOpen(door.WorldGuid, isOpen: true, out DoorInstance? opened));
        Assert.Same(door, opened);
        Assert.True(door.IsOpen);
        Assert.Equal(1, match.OpenCount);

        Assert.False(match.SetOpen(door.WorldGuid, isOpen: true, out _));
        Assert.False(match.SetOpen(0xDEAD_BEEFUL, isOpen: true, out _));
    }

    /// <summary>Leaving a world drops the doors and the open bits together.</summary>
    [Fact]
    public void ClearDropsTheSpawnedDoorsAndTheOpenBits()
    {
        MatchDoors match = NewMatch();
        DoorInstance door = match.Register(0);
        match.TryToggle(door.WorldGuid, 0, out _);

        match.Clear();

        Assert.Equal(0, match.Count);
        Assert.Equal(0, match.OpenCount);
        Assert.False(match.IsOpen(door.InstanceId));
        Assert.False(match.TryGet(door.WorldGuid, out _));
    }

    /// <summary>A door index outside the dataset is a programming error, not a silent no-op.</summary>
    [Fact]
    public void RegisteringADoorThatIsNotInTheDatasetThrows()
    {
        MatchDoors match = NewMatch();

        Assert.Throws<ArgumentOutOfRangeException>(() => match.Register(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => match.Register(Dataset.Value.Count));
    }

    /// <summary>The press window is tunable, and a zero window turns the debounce off entirely.</summary>
    [Fact]
    public void AZeroPressWindowTogglesOnEveryRequest()
    {
        MatchDoors match = NewMatch(new MatchDoorOptions { PressWindowMs = 0 });
        DoorInstance door = match.Register(0);

        Assert.Equal(DoorToggleOutcome.Toggled, match.TryToggle(door.WorldGuid, 0, out _));
        Assert.Equal(DoorToggleOutcome.Toggled, match.TryToggle(door.WorldGuid, 0, out _));
        Assert.False(door.IsOpen);
        Assert.Equal(2, match.TotalToggles);
    }

    private static float Horizontal(in Vector3 a, in Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }
}
