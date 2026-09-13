using System.Numerics;
using Cranberry.Zone.Vehicles;

namespace Cranberry.Tests.Zone.Vehicles;

/// <summary>
/// docs/61 §1 — the <c>0x78</c> bystander relay, which was written in wave 4 and never called:
/// <c>grep VehiclePoseRelay ZoneService.cs</c> returned nothing, and docs/59 §2.3 calls one call
/// site <i>"the highest-value line of code left in this feature for D25"</i>.
///
/// <para>These tests pin the fan-out rules rather than the packet — <c>VehicleControlPacketTests</c>
/// already pins that a relayed pose differs from the driver own <c>0x90</c> in exactly one byte, at
/// index 0. What was missing is <b>who gets it</b>, and the answer has one correctness rule and
/// three budget rules.</para>
/// </summary>
public sealed class VehiclePoseBroadcastTests
{
    private static readonly Lazy<VehicleRoster> SharedRoster = new(VehicleRoster.LoadDefault);

    private const ulong DriverGuid = 6001;
    private const uint TransientId = 6_100;
    private const ulong VehicleGuid = 0x6000;

    private sealed class FakeObserver(ulong guid) : IVehicleObserver
    {
        public ulong CharacterGuid { get; } = guid;
        public ulong MatchId { get; set; }

        public bool IsOpen { get; set; } = true;

        public Vector3? Position { get; set; } = Vector3.Zero;

        public HashSet<ulong> Held { get; } = [VehicleGuid];

        public List<VehiclePoseRelay> Received { get; } = [];

        public bool Holds(ulong vehicleGuid) => Held.Contains(vehicleGuid);

        public void Relay(VehiclePoseRelay pose) => Received.Add(pose);
    }

    private static MatchVehicle ParkedCar(Vector3? at = null)
    {
        VehicleRoster roster = SharedRoster.Value;
        return new MatchVehicle(
            VehicleGuid, TransientId, roster.Require(1), at ?? Vector3.Zero, 0f, 25_000, 5_000f);
    }

    private static VehiclePoseRelay Pose() =>
        new(TransientId, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 });

    [Fact]
    public void IdenticalVehicleIdsInDifferentWorldsNeverReceiveEachOthersPoses()
    {
        var broadcast = new VehiclePoseBroadcast();
        var sameWorld = new FakeObserver(7001) { MatchId = 42 };
        var otherWorld = new FakeObserver(7002) { MatchId = 43 };
        broadcast.Register(sameWorld); broadcast.Register(otherWorld);
        broadcast.Relay(ParkedCar(), Pose(), DriverGuid, 1000, VehicleRelayOptions.Default, matchId: 42);
        Assert.Single(sameWorld.Received);
        Assert.Empty(otherWorld.Received);
    }

    // --- the correctness rule --------------------------------------------------------------------

    /// <summary>
    /// <b>The one rule that is correctness and not budget.</b> A <c>0x78</c> naming a transient id
    /// whose actor the receiving client has never been given resolves to nothing —
    /// <c>FUN_140a600b0</c> looks the transient up in the zone client managed table before it applies
    /// anything — so a viewer whose streamer has not spawned this car must not be sent poses for it.
    /// </summary>
    [Fact]
    public void AViewerThatHasNotBeenSentTheCarIsNeverToldWhereItIs()
    {
        var broadcast = new VehiclePoseBroadcast();
        var holder = new FakeObserver(7001);
        var stranger = new FakeObserver(7002);
        stranger.Held.Clear();

        broadcast.Register(holder);
        broadcast.Register(stranger);

        VehicleRelayResult result = broadcast.Relay(
            ParkedCar(), Pose(), DriverGuid, 1_000, VehicleRelayOptions.Default);

        Assert.Equal(1, result.Sent);
        Assert.Single(holder.Received);
        Assert.Empty(stranger.Received);
    }

    /// <summary>The driver already knows: relaying their own pose back is a pointless round trip.</summary>
    [Fact]
    public void TheReporterIsNeverRelayedToItself()
    {
        var broadcast = new VehiclePoseBroadcast();
        var driver = new FakeObserver(DriverGuid);
        var bystander = new FakeObserver(7003);
        broadcast.Register(driver);
        broadcast.Register(bystander);

        broadcast.Relay(ParkedCar(), Pose(), DriverGuid, 1_000, VehicleRelayOptions.Default);

        Assert.Empty(driver.Received);
        Assert.Single(bystander.Received);
    }

    /// <summary>
    /// With one player in the match the relay is a provable no-op, which is why it can ship on by
    /// default without a play-test.
    /// </summary>
    [Fact]
    public void ASoloMatchRelaysNothingAtAll()
    {
        var broadcast = new VehiclePoseBroadcast();
        broadcast.Register(new FakeObserver(DriverGuid));

        VehicleRelayResult result = broadcast.Relay(
            ParkedCar(), Pose(), DriverGuid, 1_000, VehicleRelayOptions.Default);

        Assert.Equal(0, result.Sent);
        Assert.Equal(0, result.Bytes);
    }

    // --- the budget rules -------------------------------------------------------------------------

    [Fact]
    public void AViewerBeyondTheRelayRadiusIsSkipped()
    {
        var broadcast = new VehiclePoseBroadcast();
        var near = new FakeObserver(7004) { Position = new Vector3(10, 30, 0) };
        var far = new FakeObserver(7005) { Position = new Vector3(4_000, 30, 0) };
        broadcast.Register(near);
        broadcast.Register(far);

        broadcast.Relay(ParkedCar(), Pose(), DriverGuid, 1_000, VehicleRelayOptions.Default);

        Assert.Single(near.Received);
        Assert.Empty(far.Received);
    }

    /// <summary>
    /// A viewer with no pose yet — the first second or two of every match — must still be told, or a
    /// player who lands beside a moving car sees it frozen until their own first movement packet.
    /// </summary>
    [Fact]
    public void AViewerWithNoPoseYetIsStillRelayedTo()
    {
        var broadcast = new VehiclePoseBroadcast();
        var fresh = new FakeObserver(7006) { Position = null };
        broadcast.Register(fresh);

        broadcast.Relay(ParkedCar(), Pose(), DriverGuid, 1_000, VehicleRelayOptions.Default);
        Assert.Single(fresh.Received);
    }

    /// <summary>
    /// The driver client streams <c>0x90</c> at about 5 Hz (97 poses in 20 s, measured live). At the
    /// 100 ms floor that is transparent; a client streaming at 60 Hz is capped at 10 Hz per viewer
    /// instead of multiplying into every one of them.
    /// </summary>
    [Fact]
    public void AnExplicitRateLimitIsAppliedPerViewer()
    {
        var broadcast = new VehiclePoseBroadcast();
        var viewer = new FakeObserver(7007);
        broadcast.Register(viewer);
        MatchVehicle car = ParkedCar();
        var options = new VehicleRelayOptions { MinIntervalMs = 100 };

        // 60 Hz for one second.
        for (int frame = 0; frame < 60; frame++)
        {
            broadcast.Relay(car, Pose(), DriverGuid, frame * 16, options);
        }

        Assert.InRange(viewer.Received.Count, 9, 11);

        // 5 Hz for one second, from a fresh broadcaster: everything gets through.
        var honest = new VehiclePoseBroadcast();
        var second = new FakeObserver(7008);
        honest.Register(second);
        for (int frame = 0; frame < 5; frame++)
        {
            honest.Relay(car, Pose(), DriverGuid, 10_000 + (frame * 200), options);
        }

        Assert.Equal(5, second.Received.Count);
    }

    [Fact]
    public void DefaultRelayPreservesConsecutiveSparseDeltasForEveryPlayerInAFullMatch()
    {
        var broadcast = new VehiclePoseBroadcast();
        var viewers = Enumerable.Range(0, 149).Select(i => new FakeObserver((ulong)(8000 + i))).ToArray();
        foreach (var viewer in viewers) broadcast.Register(viewer);
        var position = new VehiclePoseRelay(TransientId, new byte[] { 0x02, 0x00, 0x11 });
        var rotation = new VehiclePoseRelay(TransientId, new byte[] { 0x20, 0x00, 0x22 });
        var car = ParkedCar();
        Assert.Equal(149, broadcast.Relay(car, position, DriverGuid, 1000, VehicleRelayOptions.Default).Sent);
        Assert.Equal(149, broadcast.Relay(car, rotation, DriverGuid, 1016, VehicleRelayOptions.Default).Sent);
        Assert.All(viewers, viewer => Assert.Equal(new[] { position, rotation }, viewer.Received));
    }

    /// <summary>
    /// At the D25 target of 50 players, one driven car at 5 Hz to 49 viewers would be ~9.8 KB/s. The
    /// per-pose cap holds one car to 24 viewers per pose and the round-robin cursor gives the rest
    /// the <i>next</i> pose — the same "degrade the rate for everyone rather than freeze half the
    /// view" rule <c>RelaySystem</c> follows for peer movement (docs/22 §6.1).
    /// </summary>
    [Fact]
    public void ACrowdIsServedRoundRobinSoNobodyIsFrozenOut()
    {
        var broadcast = new VehiclePoseBroadcast();
        var viewers = new List<FakeObserver>();
        for (int index = 0; index < 49; index++)
        {
            var viewer = new FakeObserver(8_000UL + (ulong)index);
            viewers.Add(viewer);
            broadcast.Register(viewer);
        }

        MatchVehicle car = ParkedCar();
        var options = new VehicleRelayOptions { MinIntervalMs = 0, MaxObserversPerPose = 24 };

        VehicleRelayResult first = broadcast.Relay(car, Pose(), DriverGuid, 0, options);
        Assert.Equal(24, first.Sent);
        Assert.True(first.Deferred > 0);

        // Three more poses and every one of the 49 has heard at least once.
        for (int pose = 1; pose <= 3; pose++)
        {
            broadcast.Relay(car, Pose(), DriverGuid, pose * 200, options);
        }

        Assert.All(viewers, viewer => Assert.NotEmpty(viewer.Received));
    }

    [Fact]
    public void TheRelayIsOnByDefaultAndCanBeSwitchedOff()
    {
        Assert.True(VehicleRelayOptions.Default.Enabled);

        var broadcast = new VehiclePoseBroadcast();
        var viewer = new FakeObserver(7009);
        broadcast.Register(viewer);

        broadcast.Relay(
            ParkedCar(), Pose(), DriverGuid, 1_000, new VehicleRelayOptions { Enabled = false });
        Assert.Empty(viewer.Received);
    }

    // --- housekeeping -----------------------------------------------------------------------------

    /// <summary>
    /// A closed link that is merely skipped still costs a slot of the per-pose cap on every pose, so
    /// the relay sweeps rather than filters.
    /// </summary>
    [Fact]
    public void AClosedObserverIsSweptOutRatherThanSkippedForever()
    {
        var broadcast = new VehiclePoseBroadcast();
        var gone = new FakeObserver(7010) { IsOpen = false };
        var here = new FakeObserver(7011);
        broadcast.Register(gone);
        broadcast.Register(here);
        Assert.Equal(2, broadcast.ObserverCount);

        broadcast.Relay(ParkedCar(), Pose(), DriverGuid, 1_000, VehicleRelayOptions.Default);

        Assert.Equal(1, broadcast.ObserverCount);
        Assert.Empty(gone.Received);
        Assert.Single(here.Received);
    }

    /// <summary>Registering the same character twice — a reconnect — must not double its relays.</summary>
    [Fact]
    public void RegisteringTheSameCharacterTwiceReplacesRatherThanDuplicates()
    {
        var broadcast = new VehiclePoseBroadcast();
        var first = new FakeObserver(7012);
        var second = new FakeObserver(7012);
        broadcast.Register(first);
        broadcast.Register(second);

        Assert.Equal(1, broadcast.ObserverCount);
        broadcast.Relay(ParkedCar(), Pose(), DriverGuid, 1_000, VehicleRelayOptions.Default);
        Assert.Empty(first.Received);
        Assert.Single(second.Received);
    }

    [Fact]
    public void UnregisteringForgetsTheViewerAndItsRateLimitStamps()
    {
        var broadcast = new VehiclePoseBroadcast();
        var viewer = new FakeObserver(7013);
        broadcast.Register(viewer);
        broadcast.Relay(ParkedCar(), Pose(), DriverGuid, 1_000, VehicleRelayOptions.Default);

        Assert.True(broadcast.Unregister(7013));
        Assert.False(broadcast.Unregister(7013));
        Assert.Equal(0, broadcast.ObserverCount);

        // Re-registering inside the rate-limit window still gets the very next pose, because the
        // stamp went with the old registration.
        broadcast.Register(viewer);
        broadcast.Relay(ParkedCar(), Pose(), DriverGuid, 1_050, VehicleRelayOptions.Default);
        Assert.Equal(2, viewer.Received.Count);
    }

    [Fact]
    public void TheRelayBodyIsTheDriverOwnBytesUnderTheZeroSevenEightOpcode()
    {
        var pose = new VehiclePoseRelay(TransientId, new byte[] { 0xAA, 0xBB, 0xCC });
        Assert.Equal((byte)0x78, VehiclePoseRelay.Opcode);
        Assert.Equal(3, pose.MovementPayload.Length);

        var broadcast = new VehiclePoseBroadcast();
        var viewer = new FakeObserver(7014);
        broadcast.Register(viewer);
        VehicleRelayResult result = broadcast.Relay(
            ParkedCar(), pose, DriverGuid, 1_000, VehicleRelayOptions.Default);

        Assert.Same(pose, viewer.Received[0]);
        Assert.Equal(pose.Length, result.Bytes);
    }
}
