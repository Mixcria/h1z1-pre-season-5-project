using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Protocol;
using Cranberry.Zone;
using Cranberry.Zone.Generated;
using Cranberry.Zone.World;
using Cranberry.Zone.World.Doors;

namespace Cranberry.Tests.Zone.Doors;

/// <summary>
/// docs/114 — the five door gaps AUDIT-doors left open, one region per gap.
///
/// <para>
/// The audit closed placement, pose and model selection for all 4,103 doors Cranberry shipped
/// (<c>QuaternionYUp</c> proven four ways, the Camper proxy authored in its own mesh's frame), so
/// nothing here re-litigates geometry. What is left is <b>completeness</b> — 44 doors that exist in
/// the client's own world file and never reached a client — and <b>sharing</b>: identity, state and
/// the streaming rules that bound the live set.
/// </para>
/// </summary>
public sealed class DoorsRetailTests
{
    private static readonly Lazy<Z2Doors> Dataset = new(Z2Doors.LoadDefault);

    [Fact]
    public void StationaryViewerReceivesEveryDoorAcrossMultipleBoundedBursts()
    {
        var doors = new MatchDoors(Dataset.Value);
        const float radius = 500f;
        var centre = Enumerable.Range(0, Dataset.Value.Count)
            .Select(i => Dataset.Value[i].Position)
            .MaxBy(p => Dataset.Value.Query(p, radius, []));
        int expected = Dataset.Value.Query(centre, radius, []);
        Assert.True(expected > 48);
        int passes = 0;
        while (doors.ShouldRestream(centre, radius) && passes++ < 100)
        {
            var added = new List<DoorInstance>();
            doors.RegisterNear(centre, radius, 48, added);
            Assert.InRange(added.Count, 1, 48);
        }
        Assert.Equal(expected, doors.Count);
        Assert.False(doors.ShouldRestream(centre, radius));
        Assert.True(doors.ShouldRestream(centre + new Vector3(31, 0, 0), radius));
        Assert.All(doors.Instances, d => Assert.Equal(650f, d.Spawn().Body.RenderDistance));
    }

    /// <summary>
    /// The busiest place on the map, measured: 42 doors inside 60 m. It is the fixture for the burst
    /// cap, because the cap defect only shows where the disc holds more doors than one burst sends.
    /// </summary>
    private static readonly Vector3 DensestDisc = new(-1477.9336f, 72.0028f, -2572.8367f);

    private static byte[] Write(Action<PacketWriter> write)
    {
        using var writer = new PacketWriter(512);
        write(writer);
        return writer.Written.ToArray();
    }

    /// <summary>The index of the first dataset door of <paramref name="family"/>, for a Register.</summary>
    private static int FirstDoorIndexOfFamily(Z2Doors doors, string family)
    {
        int kindIndex = doors.KindIndexOf(family);
        Assert.True(kindIndex >= 0, $"the dataset has no '{family}' family");
        for (int i = 0; i < doors.Count; i++)
        {
            if (doors[i].KindIndex == kindIndex)
            {
                return i;
            }
        }

        Assert.Fail($"no door of family '{family}' in the dataset");
        return -1;
    }

    // ================================================================== gap 1: the lobby's doors

    /// <summary>
    /// The five doors of the pre-match lobby, as the owner's friend's live server sent them on
    /// 2026-08-22 — <c>cPacketIdAddLightweightNpc</c> records #5, #6, #8, #9 and #10 on session
    /// 1119:53544 at t = 112.404–112.421 s.
    /// <para>
    /// This is the highest evidence grade in the door lane: a real retail-lineage server, the
    /// owner's own capture, and the client's own world file agreeing on five objects to 7 cm and to
    /// the last decimal of the yaw. Cranberry dropped all five, because <c>gen-doors.py</c> filtered
    /// everything above y = 400 as an "off-map lobby set piece".
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(1796604819u, -280.85086f, 506.24966f, -4880.09424f, -1.570798f)]   // capture #5
    [InlineData(1435455604u, -249.94852f, 506.36749f, -4948.74609f, +1.570797f)]   // capture #6
    [InlineData(1796604901u, -280.87967f, 506.23587f, -4852.48828f, -1.570798f)]   // capture #8
    [InlineData(1796604902u, -290.79166f, 506.21759f, -4850.81299f, -3.141593f)]   // capture #9
    [InlineData(1796604910u, -285.47699f, 506.27875f, -4848.44336f, +1.570795f)]   // capture #10
    public void EachLobbyDoorOfTheAdminCaptureIsInTheDataset(
        uint instanceId, float x, float y, float z, float yaw)
    {
        Z2Doors doors = Dataset.Value;
        DoorPlacement? found = null;
        foreach (DoorPlacement door in doors.Doors)
        {
            if (door.InstanceId == instanceId)
            {
                found = door;
                break;
            }
        }

        Assert.True(found is not null, $"the lobby door {instanceId} is not in z2-doors.bin");
        DoorPlacement placement = found!.Value;

        // The audit's own tolerance: the worst of the five matched at 0.070 m (record #9).
        float dx = placement.X - x;
        float dy = placement.Y - y;
        float dz = placement.Z - z;
        Assert.True(
            MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz)) <= 0.08f,
            $"door {instanceId} is at ({placement.X}, {placement.Y}, {placement.Z}), "
                + $"the capture says ({x}, {y}, {z})");

        // The yaw is the whole quaternion proof: the capture's floats are the same
        // (0, sin(t/2), 0, cos(t/2)) Cranberry writes, from this unmodified rot[0].
        Assert.True(
            MathF.Abs(placement.Yaw - yaw) <= 1e-4f,
            $"door {instanceId} yaw {placement.Yaw}, capture {yaw}");

        // All five are Industrial, which is why the old filter's per-family census subtracted 5
        // from that family and nothing else.
        Assert.Equal("Industrial", doors.Kinds[placement.KindIndex].Name);
    }

    /// <summary>
    /// <b>Byte-identity for every other row.</b> Adding 44 doors re-sorts the file — the grid offsets
    /// and the record order both move — so the meaningful claim is per ROW, not per file: the eight
    /// original families still hold exactly their measured Z2 census, the three new ones hold theirs,
    /// and the eight original kind INDICES are unchanged, which is what keeps every pre-existing
    /// record's kind byte the byte it already was.
    /// </summary>
    [Fact]
    public void EveryDoorFamilyThatWasAlreadyHereKeepsItsCensusAndItsKindIndex()
    {
        Z2Doors doors = Dataset.Value;
        (string Name, int Index, int Count)[] expected =
        [
            ("ResidentialFront", 0, 1262),
            ("Office", 1, 905),
            ("Camper", 2, 463),
            ("Residential", 3, 429),
            ("CommercialGlass", 4, 393),
            ("Industrial", 5, 369),          // 364 + the five lobby doors (gap 1)
            ("Cabin", 6, 286),
            ("BathroomStall", 7, 1),
            ("HospitalSingle", 8, 25),       // appended, so nothing above moved (gap 2)
            ("HospitalDouble", 9, 10),
            ("HospitalDoubleMetal", 10, 4),
        ];

        var counts = new int[doors.Kinds.Length];
        foreach (DoorPlacement door in doors.Doors)
        {
            counts[door.KindIndex]++;
        }

        int total = 0;
        foreach ((string name, int index, int count) in expected)
        {
            Assert.Equal(index, doors.KindIndexOf(name));
            Assert.Equal(count, counts[index]);
            total += count;
        }

        Assert.Equal(Z2Doors.Z2PlayableDoorCount, total);
        Assert.Equal(total, doors.Count);
        Assert.Equal(4147, total);
        Assert.Equal(4103 + Z2Doors.Z2LobbyDoorCount + Z2Doors.Z2HospitalDoorCount, total);
    }

    /// <summary>
    /// The lobby doors are reachable from where this server actually puts the player: two of the
    /// five are inside <c>ZoneOptions.DoorRadius</c> of <c>StagingSpawn</c>, at 48 m and 57 m. That
    /// is why gap 1 needed a burst as well as a dataset fix — the doors existing is not the same as
    /// the doors being sent.
    /// </summary>
    [Fact]
    public void TheStagingSpawnHasLobbyDoorsInsideTheDoorRadius()
    {
        var options = new ZoneOptions();
        Vector4 staging = options.StagingSpawn;
        var centre = new Vector3(staging.X, staging.Y, staging.Z);

        Span<int> found = stackalloc int[16];
        int matched = Dataset.Value.QueryNearest(centre, options.DoorRadius, found);

        Assert.True(matched >= 2, $"only {matched} door(s) within {options.DoorRadius} m of the lobby");
        Assert.True(options.SendLobbyDoors, "the lobby burst must be on by default");

        foreach (int index in found[..Math.Min(matched, found.Length)])
        {
            Assert.True(Dataset.Value[index].Y > 400f, "a lobby query reached a door on the map");
        }
    }

    // =============================================================== gap 2: the hospital doors

    /// <summary>
    /// The three hospital families, with the model ids the client's own <c>Models.txt</c> gives them.
    /// <para>
    /// The owner's Z1 maps the placers to 9887 / 9889 / 9891 (<c>ZoneWorldObjects.cs:3190-3192</c>,
    /// adopted under D53) and the client confirms it independently: each <c>_Placer</c> row sits
    /// immediately after the mesh row it places and shares its base name — 9887
    /// <c>Hospital_Door01</c> / 9888 <c>Hospital_Door01_Placer</c>, 9889 / 9890, 9891 / 9892
    /// (<c>out/data_aug/Models.txt:943-948</c>). Nothing here is typed: <c>gen-doors.py</c> resolves
    /// both ids by NAME.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("HospitalSingle", "Hospital_Door01.adr", 9887u, "SFX_Door_Office_Open")]
    [InlineData("HospitalDouble", "Hospital_DoorDouble01.adr", 9889u, "SFX_Door_Metal_Business_Open")]
    [InlineData("HospitalDoubleMetal", "Hospital_DoorDoubleMetal01.adr", 9891u, "SFX_Door_Metal_Business_Open")]
    public void EachHospitalFamilyResolvesToTheClientsOwnMeshAndSound(
        string family, string mesh, uint modelId, string sound)
    {
        Z2Doors doors = Dataset.Value;
        int index = doors.KindIndexOf(family);
        Assert.True(index >= 0, $"the dataset has no '{family}' family");
        Assert.Equal(modelId, doors.Kinds[index].ModelId);

        // A door id of 0 is "not a door": the client never installs a controller and the leaf can
        // never swing (docs/42 §2). The friend's own lobby doors have exactly that defect.
        Assert.True(doors.Kinds[index].DoorTableId > 0);

        AugustDoorKind? row = null;
        foreach (AugustDoorKind kind in AugustDoorTable.Kinds)
        {
            if (kind.Kind == family)
            {
                row = kind;
            }
        }

        Assert.True(row is not null, $"AugustDoorTable has no '{family}' row");
        Assert.Equal(mesh, row!.Value.MeshName);
        Assert.Equal(sound, row.Value.SoundName);
        Assert.Equal(doors.Kinds[index].DoorTableId, row.Value.RowId);
        Assert.NotNull(AugustDoorTable.Row(row.Value.RowId));
    }

    // =============================================================== §12 (D248): the swing sound

    /// <summary>
    /// The owner's report — some doors made a sound, some did not; they should all make some kind of
    /// noise. Under <see cref="MatchDoorOptions.RetailDoorSound"/> (default on) every one of the
    /// eleven families resolves to one of the two door sounds the retail client's own
    /// <c>getDoorSound</c> keeps resident — row 12 metal for the industrial door, the wooden default
    /// row 2 for every other — so no door swings in silence, and each row is a real
    /// <c>SFX_Door_*_Open/Close</c> composite with a non-zero open AND close effect (docs/42 §6c).
    /// </summary>
    [Fact]
    public void EveryFamilyResolvesToAnAudibleRetailDoorSound()
    {
        foreach (DoorKind kind in Dataset.Value.Kinds)
        {
            uint retailRow = DoorInstance.RetailSoundRowFor(kind.ModelId);
            Assert.True(retailRow is 2u or 12u, $"'{kind.Name}' resolved row {retailRow}");

            AugustDoorRow? row = AugustDoorTable.Row(retailRow);
            Assert.NotNull(row);
            Assert.True(row!.Value.OpenEffectId > 0, $"'{kind.Name}' open effect is 0");
            Assert.True(row.Value.CloseEffectId > 0, $"'{kind.Name}' close effect is 0");
            Assert.StartsWith("SFX_Door_", row.Value.OpenEffectName, StringComparison.Ordinal);
            Assert.StartsWith("SFX_Door_", row.Value.CloseEffectName, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <c>getDoorSound</c> is a two-way switch: only the industrial door (model 9903) keeps the metal
    /// sound; every other model — office, glass, camper, residential, hospital — takes the wooden
    /// default. This is the reference's own mapping and the owner's Z1's (D53).
    /// </summary>
    [Theory]
    [InlineData(9903u, 12u)]    // Industrial  -> SFX_Door_Metal_Industrial (kept)
    [InlineData(9884u, 2u)]     // Office      -> wood, was authored row 13 (silent)
    [InlineData(9897u, 2u)]     // CommercialGlass -> wood, was row 7 (silent)
    [InlineData(10055u, 2u)]    // Camper      -> wood, was row 4 (silent)
    [InlineData(10083u, 2u)]    // BathroomStall -> wood, was row 13 (silent)
    [InlineData(9887u, 2u)]     // HospitalSingle -> wood, was row 13 (silent)
    [InlineData(9889u, 2u)]     // HospitalDouble -> wood, was row 8 (silent)
    [InlineData(9904u, 2u)]     // ResidentialFront -> wood (already audible)
    public void GetDoorSoundKeepsMetalForIndustrialAndWoodForTheRest(uint modelId, uint expected) =>
        Assert.Equal(expected, DoorInstance.RetailSoundRowFor(modelId));

    /// <summary>
    /// The switch on the wire. A CommercialGlass door — authored row 7, whose FMOD bank the retail
    /// client never loads — spawns with the audible wood row 2 when RetailDoorSound is on, and with
    /// its own authored row 7 when it is off. Only the door-id field moves; the door still swings,
    /// collides and interacts identically (docs/42 §6c).
    /// </summary>
    [Fact]
    public void RetailDoorSoundMovesTheSilentFamilyToWoodAndOffRestoresIt()
    {
        Z2Doors doors = Dataset.Value;
        int index = FirstDoorIndexOfFamily(doors, "CommercialGlass");
        uint authored = doors.Kinds[doors[index].KindIndex].DoorTableId;
        Assert.Equal(7u, authored);

        DoorInstance on = new MatchDoors(doors).Register(index);
        Assert.Equal(2u, on.DoorTableId);
        Assert.Equal(2u, on.Spawn().DoorTableId);

        DoorInstance off =
            new MatchDoors(doors, new MatchDoorOptions { RetailDoorSound = false }).Register(index);
        Assert.Equal(authored, off.DoorTableId);
    }

    /// <summary>
    /// The two-leaf families are declared by the generator, so the runtime switch and the data
    /// cannot disagree about which families the client swings about a single pivot.
    /// </summary>
    [Fact]
    public void TheTwoLeafHospitalFamiliesAreDeclared()
    {
        Assert.Equal(
            new[] { "HospitalDouble", "HospitalDoubleMetal" },
            AugustDoorTable.DoubleLeafKinds);
    }

    /// <summary>
    /// The runtime half: an excluded family is never registered, and the doors around it still are.
    /// This is the shape of <c>CRANBERRY_DOOR_HOSPITAL=0</c> — the rows stay in
    /// <c>z2-doors.bin</c> and stay out of the world, so the switch needs no second dataset.
    /// </summary>
    [Fact]
    public void AnExcludedFamilyIsNeverSpawnedAndDoesNotCostACapSlot()
    {
        Z2Doors doors = Dataset.Value;
        int hospitalIndex = doors.KindIndexOf("HospitalSingle");
        Vector3 centre = default;
        foreach (DoorPlacement door in doors.Doors)
        {
            if (door.KindIndex == hospitalIndex)
            {
                centre = new Vector3(door.X, door.Y, door.Z);
                break;
            }
        }

        Assert.NotEqual(default, centre);

        var withHospitals = new List<DoorInstance>();
        var open = new MatchDoors(doors);
        open.RegisterNear(centre, 60f, 64, withHospitals);
        Assert.Contains(withHospitals, d => d.KindName == "HospitalSingle");

        var withoutHospitals = new List<DoorInstance>();
        var closed = new MatchDoors(
            doors,
            new MatchDoorOptions
            {
                ExcludedKinds = ["HospitalSingle", "HospitalDouble", "HospitalDoubleMetal"],
            });
        closed.RegisterNear(centre, 60f, 64, withoutHospitals);

        Assert.DoesNotContain(withoutHospitals, d => d.KindName.StartsWith("Hospital", StringComparison.Ordinal));

        // Everything that is NOT a hospital door is still there: the switch removes a family, it
        // does not shrink the burst.
        int nonHospital = 0;
        foreach (DoorInstance door in withHospitals)
        {
            if (!door.KindName.StartsWith("Hospital", StringComparison.Ordinal))
            {
                nonHospital++;
            }
        }

        Assert.Equal(nonHospital, withoutHospitals.Count);
    }

    // ============================================== gap 3: door state shared between sessions

    /// <summary>A door sink that keeps what it was sent, so a fan-out is assertable without a socket.</summary>
    private sealed class RecordingSink : IPeerSink
    {
        public bool IsOpen { get; set; } = true;

        public List<byte[]> Sent { get; } = [];

        public void Send(byte[] zonePacket) => Sent.Add(zonePacket);
    }

    private static (SharedMatchDoors Shared, MatchDoors A, RecordingSink SinkA, MatchDoors B, RecordingSink SinkB)
        TwoSessions()
    {
        var shared = new SharedMatchDoors();
        var a = new MatchDoors(Dataset.Value, shared: shared);
        var b = new MatchDoors(Dataset.Value, shared: shared);
        var sinkA = new RecordingSink();
        var sinkB = new RecordingSink();

        Assert.False(shared.Join(a, sinkA));    // the first session opens the match
        Assert.True(shared.Join(b, sinkB));     // the second joins the one already running
        Assert.Equal(2, shared.Members);
        Assert.Equal(1, shared.Joins);
        return (shared, a, sinkA, b, sinkB);
    }

    /// <summary>
    /// <b>The same door has the same guid in both sessions.</b> This is what the whole gap turns on:
    /// AUDIT-doors §6 measured two live sessions re-using guids <c>0x4400…01</c>–<c>04</c> for
    /// different doors 3 km apart, so a <c>0f 0a</c> naming one of them could not be sent to the
    /// other client at all.
    /// </summary>
    [Fact]
    public void TwoSessionsInOneMatchKnowADoorByTheSameGuid()
    {
        (SharedMatchDoors shared, MatchDoors a, _, MatchDoors b, _) = TwoSessions();

        DoorInstance fromA = a.Register(0);
        DoorInstance fromB = b.Register(0);

        Assert.Equal(fromA.WorldGuid, fromB.WorldGuid);
        Assert.Equal(fromA.TransientId, fromB.TransientId);
        Assert.Equal(fromA.InstanceId, fromB.InstanceId);
        Assert.Equal(1, shared.IdentityCount);

        // A different door still gets different ids, and from one space rather than two.
        DoorInstance otherA = a.Register(1);
        Assert.NotEqual(fromA.WorldGuid, otherA.WorldGuid);
        Assert.Equal(2, shared.IdentityCount);
    }

    /// <summary>
    /// <b>The gap-3 test the lane was asked for: A opens, B receives the <c>0f 0a</c>.</b> The bytes
    /// are asserted, not just the count — 22 bytes, base <c>0x0f</c> sub <c>0x0a</c>, the shared
    /// guid, and the state qword with bit 48 set, which is exactly what the wire carried on
    /// 2026-09-03 (<c>05 0F0A 0100000000000044 000000000000010000000000</c>).
    /// </summary>
    [Fact]
    public void WhenOneSessionOpensADoorTheOtherIsSentTheSameDoorStateUpdate()
    {
        (SharedMatchDoors shared, MatchDoors a, RecordingSink sinkA, MatchDoors b, RecordingSink sinkB) =
            TwoSessions();

        DoorInstance inA = a.Register(0);
        DoorInstance inB = b.Register(0);

        Assert.Equal(DoorToggleOutcome.Toggled, a.TryToggle(inA.WorldGuid, nowMs: 1_000, out DoorInstance? toggled));
        Assert.True(toggled!.IsOpen);

        var listeners = new List<IPeerSink>();
        shared.CollectListeners(a, inA.WorldGuid, listeners);
        Assert.Equal([sinkB], listeners);            // B, and B only: never the presser

        byte[] packet = Write(toggled.StateUpdate().WriteTo);
        foreach (IPeerSink listener in listeners)
        {
            listener.Send(packet);
        }

        shared.NoteFanout(listeners.Count);

        byte[] received = Assert.Single(sinkB.Sent);
        Assert.Empty(sinkA.Sent);
        Assert.Equal(DoorStateUpdate.Length, received.Length);
        Assert.Equal(0x0f, received[0]);
        Assert.Equal(0x0a, received[1]);
        Assert.Equal(inB.WorldGuid, BinaryPrimitives.ReadUInt64LittleEndian(received.AsSpan(2)));
        Assert.Equal(1UL << 48, BinaryPrimitives.ReadUInt64LittleEndian(received.AsSpan(10)));

        // And B's own view of that door agrees, so B's next [F] closes it rather than "opening" a
        // door that is already open — the second half of the defect.
        Assert.True(b.IsOpen(inB.InstanceId));
        Assert.Equal(1, b.OpenCount);
        Assert.Equal(1, shared.OpenCount);
        Assert.Equal(1, shared.Fanouts);

        // A door B has not streamed is not fanned out to B.
        listeners.Add(sinkA);
        shared.CollectListeners(a, a.Register(1).WorldGuid, listeners);
        Assert.Empty(listeners);
    }

    /// <summary>
    /// The revert. Without a shared set each session mints its own ids from the same base — which is
    /// the old defect, kept reachable as <c>CRANBERRY_DOOR_SHARED=0</c> so a regression can be A/B'd
    /// against it — and one session's open door is invisible to the other.
    /// </summary>
    [Fact]
    public void WithoutTheSharedSetTheGuidSpacesCollideAndNothingIsFannedOut()
    {
        var a = new MatchDoors(Dataset.Value);
        var b = new MatchDoors(Dataset.Value);

        Assert.Null(a.Shared);
        DoorInstance fromA = a.Register(0);
        DoorInstance fromB = b.Register(7);

        Assert.NotEqual(fromA.InstanceId, fromB.InstanceId);
        Assert.Equal(fromA.WorldGuid, fromB.WorldGuid);          // the same guid, two different doors
        Assert.Equal(MatchDoors.DefaultWorldGuidBase, fromA.WorldGuid);

        Assert.Equal(DoorToggleOutcome.Toggled, a.TryToggle(fromA.WorldGuid, 1_000, out _));
        Assert.Equal(1, a.OpenCount);
        Assert.Equal(0, b.OpenCount);
    }

    /// <summary>
    /// The match is forgotten when the last session leaves, so the next one starts with a fresh guid
    /// space and every door shut — the same ref-counted life <c>SharedMatchGas</c> has.
    /// </summary>
    [Fact]
    public void TheLastSessionOutForgetsTheMatchsDoors()
    {
        (SharedMatchDoors shared, MatchDoors a, _, MatchDoors b, _) = TwoSessions();
        DoorInstance door = a.Register(0);
        Assert.Equal(DoorToggleOutcome.Toggled, a.TryToggle(door.WorldGuid, 1_000, out _));
        Assert.Equal(1, shared.OpenCount);

        shared.Leave(a);
        Assert.True(shared.Active);
        Assert.Equal(1, shared.OpenCount);          // B is still playing it

        shared.Leave(b);
        Assert.False(shared.Active);
        Assert.Equal(0, shared.OpenCount);
        Assert.Equal(0, shared.IdentityCount);

        var next = new MatchDoors(Dataset.Value, shared: shared);
        shared.Join(next, new RecordingSink());
        Assert.Equal(MatchDoors.DefaultWorldGuidBase, next.Register(0).WorldGuid);
        Assert.False(next.IsOpen(door.InstanceId));
    }

    /// <summary>A closed link is swept out of the member list rather than written to.</summary>
    [Fact]
    public void AClosedSessionIsSweptOutOfTheFanout()
    {
        (SharedMatchDoors shared, MatchDoors a, _, MatchDoors b, RecordingSink sinkB) = TwoSessions();
        DoorInstance door = a.Register(0);
        b.Register(0);

        sinkB.IsOpen = false;
        var listeners = new List<IPeerSink>();
        shared.CollectListeners(a, door.WorldGuid, listeners);

        Assert.Empty(listeners);
        Assert.Equal(1, shared.Members);
    }

    // ====================================================== gap 4: the burst cap and the despawn

    /// <summary>
    /// <b>The cap counts the doors the burst SPAWNS.</b> The fixture is the busiest 60 m disc on the
    /// real map — 42 doors — with the cap set to 4, which is the audit's defect at a scale the map
    /// can actually produce: under the old rule the second burst from the same point sends nothing,
    /// because the nearest four are already live and they consumed the whole window.
    /// </summary>
    [Fact]
    public void ASecondBurstFromTheSamePointStillSpawnsTheNextNearestDoors()
    {
        var fixedCap = new MatchDoors(Dataset.Value);
        var first = new List<DoorInstance>();
        int matched = fixedCap.RegisterNear(DensestDisc, 60f, 4, first);

        Assert.True(matched > 4, $"the fixture disc holds only {matched} doors");
        Assert.Equal(42, matched);
        Assert.Equal(4, first.Count);

        var second = new List<DoorInstance>();
        fixedCap.RegisterNear(DensestDisc, 60f, 4, second);
        Assert.Equal(4, second.Count);
        Assert.Equal(8, fixedCap.Count);

        // Nearest-first is preserved: the second burst's doors are all further out than the first's.
        float nearestOfSecond = float.MaxValue;
        float farthestOfFirst = 0f;
        foreach (DoorInstance door in first)
        {
            farthestOfFirst = MathF.Max(farthestOfFirst, Flat(door.Position, DensestDisc));
        }

        foreach (DoorInstance door in second)
        {
            nearestOfSecond = MathF.Min(nearestOfSecond, Flat(door.Position, DensestDisc));
        }

        Assert.True(nearestOfSecond >= farthestOfFirst, "the second burst jumped the queue");
    }

    /// <summary>The revert: the old arithmetic, where a live door still consumes a cap slot.</summary>
    [Fact]
    public void TheOldCapArithmeticStarvesTheSecondBurst()
    {
        var doors = new MatchDoors(Dataset.Value, new MatchDoorOptions { CapCountsNewDoorsOnly = false });
        var first = new List<DoorInstance>();
        doors.RegisterNear(DensestDisc, 60f, 4, first);
        Assert.Equal(4, first.Count);

        var second = new List<DoorInstance>();
        doors.RegisterNear(DensestDisc, 60f, 4, second);
        Assert.Empty(second);
    }

    /// <summary>
    /// A door that leaves the 90 m band is taken back, and its OPEN bit is kept — so walking away
    /// and back re-spawns the door open rather than shut. The live set is bounded by the band, which
    /// is what stops a match-long walk from accumulating hundreds of door entities.
    /// </summary>
    [Fact]
    public void ADoorThatLeavesTheBandIsUnregisteredAndKeepsItsOpenBit()
    {
        var doors = new MatchDoors(Dataset.Value);
        var spawned = new List<DoorInstance>();
        doors.RegisterNear(DensestDisc, 60f, 48, spawned);
        Assert.NotEmpty(spawned);

        DoorInstance opened = spawned[0];
        Assert.Equal(DoorToggleOutcome.Toggled, doors.TryToggle(opened.WorldGuid, 1_000, out _));
        Assert.Equal(1, doors.OpenCount);

        float despawn = doors.Options.EffectiveDespawnRadiusMetres(60f);
        Assert.Equal(90f, despawn);

        var streamedOut = new List<DoorInstance>();
        var faraway = new Vector3(DensestDisc.X + 1000f, DensestDisc.Y, DensestDisc.Z);
        int leaving = doors.UnregisterBeyond(faraway, despawn, streamedOut);

        Assert.Equal(spawned.Count, leaving);
        Assert.Equal(spawned.Count, streamedOut.Count);
        Assert.Equal(0, doors.Count);
        Assert.Equal(leaving, doors.Despawned);

        // The bit survives, keyed by ZONE instance id, and the re-spawn carries it.
        Assert.True(doors.IsOpen(opened.InstanceId));
        Assert.Equal(1, doors.OpenCount);
        Assert.True(doors.Register(opened.DoorIndex).IsOpen);
    }

    /// <summary>Standing still takes nothing back, and a zero radius is "off", not "everything".</summary>
    [Fact]
    public void NothingIsTakenBackFromInsideTheBandOrWhenTheRuleIsOff()
    {
        var doors = new MatchDoors(Dataset.Value);
        var spawned = new List<DoorInstance>();
        doors.RegisterNear(DensestDisc, 60f, 48, spawned);

        var streamedOut = new List<DoorInstance>();
        Assert.Equal(0, doors.UnregisterBeyond(DensestDisc, 90f, streamedOut));
        Assert.Equal(spawned.Count, doors.Count);

        var never = new MatchDoors(Dataset.Value, new MatchDoorOptions { DespawnRadiusMetres = 0f });
        Assert.Equal(0f, never.Options.EffectiveDespawnRadiusMetres(60f));

        // And the floor: a despawn radius inside the stream radius would spawn and destroy the same
        // door on alternate bursts for ever, so it is raised to the stream radius.
        var tight = new MatchDoors(Dataset.Value, new MatchDoorOptions { DespawnRadiusMetres = 10f });
        Assert.Equal(60f, tight.Options.EffectiveDespawnRadiusMetres(60f));
    }

    private static float Flat(in Vector3 a, in Vector3 b) =>
        MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));

    // ================================================== gap 5: the interact range and the flags

    /// <summary>
    /// The friend server's interaction range on the wire: <c>00 00 00 40</c> = 2.0f, in the first
    /// four bytes of the <c>InteractReplicationData</c> payload — the same field of the same class
    /// hash where Cranberry wrote <c>00 00 40 40</c> = 3.0f. Adopted under D53, and the old value is
    /// the revert.
    /// </summary>
    [Fact]
    public void ADoorsInteractComponentCarriesTheFriendServersTwoMetres()
    {
        Assert.Equal(2f, DoorInteractRange.Retail);
        Assert.Equal(3f, DoorInteractRange.Legacy);
        Assert.Equal(InteractReplicationData.DefaultRange, DoorInteractRange.Legacy);
        Assert.Equal(DoorInteractRange.Retail, new ZoneOptions().DoorInteractRangeMetres);

        byte[] retail = new InteractReplicationData(DoorInteractRange.Retail).ToPayload();
        byte[] legacy = new InteractReplicationData(DoorInteractRange.Legacy).ToPayload();

        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x40 }, retail[..4]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x40, 0x40 }, legacy[..4]);

        // Only the range moves: the four unverified fields behind it stay the constructor's zeros.
        Assert.Equal(retail[4..], legacy[4..]);
        Assert.Equal(InteractReplicationData.PayloadLength, retail.Length);
    }

    /// <summary>
    /// The whole <c>ea 04</c> a door goes out with differs from the 3.0 m one in <b>four bytes</b> —
    /// the float — and in nothing else. Ground loot is untouched: its component still carries
    /// <see cref="InteractReplicationData.DefaultRange"/>.
    /// </summary>
    [Fact]
    public void OnlyTheRangeFloatMovesInTheDoorsComponent()
    {
        byte[] retail = Write(CreateComponentWithRepData
            .ForGroundItem(1_000_000u, 1_000_000u, DoorInteractRange.Retail).WriteTo);
        byte[] legacy = Write(CreateComponentWithRepData
            .ForGroundItem(1_000_000u, 1_000_000u, DoorInteractRange.Legacy).WriteTo);
        byte[] loot = Write(CreateComponentWithRepData.ForGroundItem(1_000u, 1_000u).WriteTo);

        Assert.Equal(retail.Length, legacy.Length);

        var differing = new List<int>();
        for (int index = 0; index < retail.Length; index++)
        {
            if (retail[index] != legacy[index])
            {
                differing.Add(index);
            }
        }

        // 2.0f is 0x40000000 and 3.0f is 0x40400000, so exactly ONE byte of the little-endian
        // float actually moves — and it is inside the four bytes the range occupies, which is the
        // claim: the range float, and nothing else in the packet.
        int rangeAt = Assert.Single(differing);
        Assert.Equal(0x00, retail[rangeAt]);                 // 2.0f -> 00 00 00 40
        Assert.Equal(0x40, legacy[rangeAt]);                 // 3.0f -> 00 00 40 40
        Assert.Equal(0x40, retail[rangeAt + 1]);             // the exponent byte, shared
        Assert.Equal(0x40, legacy[rangeAt + 1]);

        // The default overload is still the ground item's 3.0 m, so this lane moved doors only.
        byte[] lootDefault = Write(CreateComponentWithRepData
            .ForGroundItem(1_000u, 1_000u, InteractReplicationData.DefaultRange).WriteTo);
        Assert.Equal(lootDefault, loot);
    }

    /// <summary>
    /// The <c>0xd6</c> a door spawns with is <b>byte-identical apart from the flag byte</b> between
    /// the shipped <c>0x10</c> and the <c>0x30</c> revert. That is the claim gap 5 has to make: two
    /// values changed, one packet, one byte moved in it.
    /// </summary>
    [Fact]
    public void TheDoorSpawnRecordMovesExactlyTheFlagByte()
    {
        var retail = new AddLightweightDoor(
            0x4400_0000_0000_0001UL, 1_000_000u, 9904, new Vector3(415.111f, 18.25f, -1816.62f),
            -1.570798f, DoorTableId: 2, SpawnFlags1: LightweightEntityBody.DoorSpawnFlagsRetail);
        var legacy = retail with { SpawnFlags1 = LightweightEntityBody.DoorSpawnFlagsDefault };

        byte[] retailBytes = Write(retail.WriteTo);
        byte[] legacyBytes = Write(legacy.WriteTo);

        Assert.Equal(retailBytes.Length, legacyBytes.Length);

        var differing = new List<int>();
        for (int index = 0; index < retailBytes.Length; index++)
        {
            if (retailBytes[index] != legacyBytes[index])
            {
                differing.Add(index);
            }
        }

        Assert.Equal([retail.SpawnFlags1Offset], differing);
        Assert.Equal(0x10, retailBytes[retail.SpawnFlags1Offset]);
        Assert.Equal(0x30, legacyBytes[retail.SpawnFlags1Offset]);

        // And the bit that matters is set either way — this change cannot make a door walk-through.
        Assert.NotEqual(0, retailBytes[retail.SpawnFlags1Offset] & LightweightEntityBody.CollidableFlag);
    }
}
