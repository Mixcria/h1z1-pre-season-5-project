using System.Collections.Concurrent;
using System.Net;
using System.Numerics;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Economy;
using Cranberry.Zone.Weapons;
using Cranberry.Tests.Zone.Combat;

namespace Cranberry.Tests.Zone.Economy;

/// <summary>Real gateway dispatch and persisted inventory; these tests do not claim client rendering.</summary>
public sealed class CrateShootingGatewayTests
{
    private const string AccountId = "crate-shooting-test";
    private const ulong CharacterGuid = 4097;
    private const uint CrateItemId = 3208; // Unlocked Predator crate; no second Crown or key charge.

    [Fact]
    public void StartingAndRepeatingStartCannotConsumeCratesOrRevealRewards()
    {
        using var world = new World(copies: 2);
        long revision = world.Account.Revision;
        world.Start(openOne: true);
        Assert.Equal(revision, world.Account.Revision);
        Assert.Equal(2u, world.CratesHeld);
        Assert.Empty(world.Rewards);
        Assert.DoesNotContain(world.Sent, p => Is(p, 0xf5, 7));
        Assert.DoesNotContain(world.Sent, p => Is(p, 0x0f, 0x43));
        Assert.Contains(world.Sent, p => Is(p, 0xf5, 11));
        Assert.NotEqual(0ul, world.CrateGuid);
        Assert.NotEqual(0ul, world.GunGuid);
        int sends = world.Sent.Length;

        world.Start(openOne: false);

        Assert.Equal(revision, world.Account.Revision);
        Assert.Equal(sends, world.Sent.Length);
        Assert.Equal(2u, world.CratesHeld);
    }

    [Fact]
    public void OpenAllStartsAFullBatchWhenMoreThanOneHundredCratesAreOwned()
    {
        using var world = new World(copies: 101);
        long revision = world.Account.Revision;

        world.Start(openOne: false);

        Assert.NotEqual(0ul, world.CrateGuid);
        Assert.NotEqual(0ul, world.GunGuid);
        Assert.Contains(world.Sent, p => Is(p, 0xf5, 11));
        Assert.Equal(100, BitConverter.ToInt32(world.Sent.Last(p => Is(p, 0xf5, 6)), 2));
        Assert.Equal(revision, world.Account.Revision);
        Assert.Equal(101u, world.CratesHeld);
        Assert.Empty(world.Rewards);
        Assert.DoesNotContain(world.Sent, p => Is(p, 0xf5, 7));
    }

    [Fact]
    public async Task OnlySixDistinctAcceptedHitsOnTheSpawnedCrateAwardOneItem()
    {
        using var world = new World(copies: 1);
        long revision = world.Account.Revision;
        world.Start(openOne: true);
        ulong target = world.CrateGuid;
        Vector3 hitPoint = world.CratePosition;

        // A report without a preceding accepted shot is not authority to open anything.
        world.Hit(projectile: 999, target, hitPoint);
        Assert.Equal(revision, world.Account.Revision);

        // A valid shot against another GUID cannot be reused against this crate.
        await world.Fire(1);
        world.Hit(1, target + 1, hitPoint);
        world.Hit(1, target, hitPoint);
        Assert.Equal(revision, world.Account.Revision);

        for (uint projectile = 2; projectile < 2 + StartCrateOpening.RequiredHits; projectile++)
        {
            await world.Fire(projectile);
            world.Hit(projectile, target, hitPoint);
            world.Hit(projectile, target, hitPoint); // A duplicate report must not count twice.
            if (projectile < 1 + StartCrateOpening.RequiredHits)
            {
                Assert.Equal(revision, world.Account.Revision);
                Assert.Equal(1u, world.CratesHeld);
                Assert.DoesNotContain(world.Sent, p => Is(p, 0xf5, 7));
                Assert.DoesNotContain(world.Sent, p => Is(p, 0x0f, 0x43));
            }
        }

        Assert.Equal(0u, world.CratesHeld);
        var reward = Assert.Single(world.Rewards);
        Assert.Equal(1u, reward.Count);
        Assert.Contains(EconomyCatalog.Default.Crates[CrateItemId].Rewards,
            candidate => candidate.AccountItemId == reward.AccountItemId);
        Assert.Equal(12_500u, world.Account.Balance(4));
        Assert.Equal(250u, world.Account.Balance(1));
        Assert.Single(world.Sent, p => Is(p, 0xf5, 7));
        byte[] fanfare = Assert.Single(world.Sent, p => Is(p, 0x0f, 0x43));
        Assert.Equal(PlayWorldCompositeEffect.Length, fanfare.Length);
        Assert.Equal(target, BitConverter.ToUInt64(fanfare, 2));
        Assert.Equal(CrateRewardEffects.ForRarity(EconomyCatalog.Default.Skins[reward.AccountItemId].RarityId),
            BitConverter.ToUInt32(fanfare, 10));
        Assert.Equal(hitPoint, new Vector3(BitConverter.ToSingle(fanfare, 14),
            BitConverter.ToSingle(fanfare, 18), BitConverter.ToSingle(fanfare, 22)));

        long awardedRevision = world.Account.Revision;
        world.Hit(7, target, hitPoint);
        int exitStart = world.Sent.Length;
        world.Send(w => { w.WriteByte(0xf5); w.WriteByte(5); });
        Assert.Equal(awardedRevision, world.Account.Revision);
        world.AssertGalleryGunRemoved(exitStart);
        world.AssertResultsPreserved(exitStart, 1);
        Assert.Single(world.Sent, p => Is(p, 0xf5, 7));
        Assert.Single(world.Sent, p => Is(p, 0x0f, 0x43));
    }

    [Fact]
    public async Task AllTenCratesKeepTheNativeHandUntilDonePublishesTheLatestInventory()
    {
        using var world = new World(copies: 10, deferReveal: true);
        world.Start(openOne: false);
        long revision = world.Account.Revision;
        int galleryStart = world.Sent.Length;
        ulong gun = world.GunGuid;
        var targets = new HashSet<ulong>();
        uint projectile = 0;

        for (uint opened = 1; opened <= 10; opened++)
        {
            ulong target = world.CrateGuid;
            Assert.True(targets.Add(target));
            Assert.Equal(gun, world.GunGuid);
            Assert.Equal(11 - opened, BitConverter.ToUInt32(world.Sent.Last(p => Is(p, 0xf5, 6)), 2));
            int revealCount = world.Sent.Count(p => Is(p, 0xf5, 7));
            for (int hit = 0; hit < StartCrateOpening.RequiredHits; hit++)
            {
                await world.Fire(++projectile);
                world.Hit(projectile, target, world.CratePosition);
            }

            // Durability precedes animation; a fresh store must see exactly this many rewards.
            var persisted = world.PersistedAccount;
            Assert.Equal(revision + opened, persisted.Revision);
            Assert.Equal(10 - opened, persisted.Items.Where(i => i.AccountItemId == CrateItemId)
                .Sum(i => (long)i.Count));
            Assert.Equal(opened, persisted.Items.Where(i => i.AccountItemId != CrateItemId)
                .Sum(i => (long)i.Count));
            Assert.Equal(12_500u, persisted.Balance(4));
            Assert.Equal(250u, persisted.Balance(1));
            Assert.Equal(revealCount, world.Sent.Count(p => Is(p, 0xf5, 7)));
            world.AssertNativeHandPreserved(galleryStart);

            await world.RunDeferredReveal();
            Assert.Equal((int)opened, world.Sent.Count(p => Is(p, 0xf5, 7)));
            Assert.Equal((int)opened, world.Sent.Count(p => Is(p, 0x0f, 0x43)));
            Assert.Equal(10 - opened, BitConverter.ToUInt32(world.Sent.Last(p => Is(p, 0xf5, 6)), 2));
            if (opened < 10)
            {
                await world.RunDeferredReveal(); // The separately scheduled next-crate transition.
                Assert.NotEqual(target, world.CrateGuid);
                Assert.Equal((int)opened + 1, world.Sent.Count(p => Is(p, 0xf5, 11)));
            }
            world.AssertNativeHandPreserved(galleryStart);
        }

        // Finished still owns the temporary hand. The catalogue must wait for actual cleanup.
        Assert.Equal(0u, world.CratesHeld);
        Assert.Equal(40, world.Sent.Skip(galleryStart).Count(p => Is(p, 0xab, 3)));
        int exitStart = world.Sent.Length;
        world.Send(w => { w.WriteByte(0xf5); w.WriteByte(5); });
        world.AssertGalleryGunRemoved(exitStart);
        world.AssertLatestInventoryBeforeFinalAppearance(exitStart);
        world.AssertResultsPreserved(exitStart, 10);
        Assert.Equal(revision + 10, world.PersistedAccount.Revision);

        int afterExit = world.Sent.Length;
        world.Send(w => { w.WriteByte(0xf5); w.WriteByte(5); });
        Assert.Equal(afterExit, world.Sent.Length);
    }

    [Fact]
    public async Task OpenAllThenStopPreservesEveryCrateThatWasNotShotOpen()
    {
        using var world = new World(copies: 3);
        world.Start(openOne: false);
        ulong target = world.CrateGuid;
        Vector3 hitPoint = world.CratePosition;
        for (uint projectile = 1; projectile <= StartCrateOpening.RequiredHits; projectile++)
        {
            await world.Fire(projectile);
            world.Hit(projectile, target, hitPoint);
        }
        Assert.Equal(2u, world.CratesHeld);
        Assert.Equal(1u, Assert.Single(world.Rewards).Count);
        long revision = world.Account.Revision;

        int exitStart = world.Sent.Length;
        world.Send(w => { w.WriteByte(0xf5); w.WriteByte(4); });

        Assert.Equal(revision, world.Account.Revision);
        Assert.Equal(2u, world.CratesHeld);
        world.AssertGalleryGunRemoved(exitStart);
        world.AssertGalleryReset(exitStart);
        world.Hit(6, target, hitPoint);
        Assert.Equal(revision, world.Account.Revision);
    }

    [Fact]
    public void DoneBeforeAnyShotRestoresTheHandWithoutAnEconomicTransaction()
    {
        using var world = new World(copies: 1);
        long revision = world.Account.Revision;
        world.Start(openOne: true);
        int exitStart = world.Sent.Length;
        world.Send(w => { w.WriteByte(0xf5); w.WriteByte(5); });
        Assert.Equal(revision, world.Account.Revision);
        Assert.Equal(1u, world.CratesHeld);
        Assert.Empty(world.Rewards);
        Assert.DoesNotContain(world.Sent, p => Is(p, 0xf5, 7));
        Assert.DoesNotContain(world.Sent, p => Is(p, 0x0f, 0x43));
        world.AssertGalleryGunRemoved(exitStart);
        world.AssertResultsPreserved(exitStart, 0);
        Assert.DoesNotContain(world.Sent.Skip(exitStart), p => Is(p, 0xac, 0x11) || Is(p, 0xac, 0x23));

        // Done leaves this page open. A later Back must reset it even though the run is gone.
        int backStart = world.Sent.Length;
        world.Send(w => { w.WriteByte(0xf5); w.WriteByte(4); });
        world.AssertGalleryReset(backStart);
        Assert.DoesNotContain(world.Sent.Skip(backStart), p => Is(p, 0x11, 4));
        Assert.Equal(revision, world.Account.Revision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LeavingDuringPendingRevealPreservesTheSavedAwardAndCancelsItsCallback(bool done)
    {
        using var world = new World(copies: 3, deferReveal: true);
        world.Start(openOne: false);
        int galleryStart = world.Sent.Length;
        ulong target = world.CrateGuid;
        Vector3 hitPoint = world.CratePosition;
        for (uint projectile = 1; projectile <= StartCrateOpening.RequiredHits; projectile++)
        {
            await world.Fire(projectile);
            world.Hit(projectile, target, hitPoint);
        }

        Assert.Equal(2u, world.CratesHeld);
        Assert.Equal(1u, Assert.Single(world.Rewards).Count);
        Assert.DoesNotContain(world.Sent, p => Is(p, 0xf5, 7));
        Assert.DoesNotContain(world.Sent, p => Is(p, 0x0f, 0x43));
        world.AssertNativeHandPreserved(galleryStart);
        long awardedRevision = world.Account.Revision;
        int exitStart = world.Sent.Length;
        world.Send(w => { w.WriteByte(0xf5); w.WriteByte(done ? (byte)5 : (byte)4); });
        world.AssertGalleryGunRemoved(exitStart);
        world.AssertLatestInventoryBeforeFinalAppearance(exitStart);
        if (done)
        {
            world.AssertResultsPreserved(exitStart, 1);
            Assert.Single(world.Sent, p => Is(p, 0xf5, 7));
            Assert.Single(world.Sent, p => Is(p, 0x0f, 0x43));
        }
        else
        {
            world.AssertGalleryReset(exitStart);
            Assert.DoesNotContain(world.Sent, p => Is(p, 0xf5, 7));
            Assert.DoesNotContain(world.Sent, p => Is(p, 0x0f, 0x43));
        }

        int afterExit = world.Sent.Length;
        await world.RunDeferredReveal();
        Assert.Equal(afterExit, world.Sent.Length); // No second award row, next crate or f506 reactivation.
        Assert.Equal(awardedRevision, world.Account.Revision);
        Assert.Equal(2u, world.CratesHeld);
        Assert.Equal(1u, Assert.Single(world.Rewards).Count);
        world.Send(w => { w.WriteByte(0xf5); w.WriteByte(5); });
        Assert.Equal(afterExit, world.Sent.Length); // Repeated Done cannot flush the same reward twice.
    }

    private static bool Is(byte[] body, byte opcode, byte sub) =>
        body.Length >= 2 && body[0] == opcode && body[1] == sub;

    private sealed class World : IPacketRecorder, ITransportLog, IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-crate-shooting", Guid.NewGuid().ToString("N"));
        private readonly GatewayTicketRegistry _tickets = new();
        private readonly List<byte[]> _sent = [];
        private readonly ConcurrentQueue<Action> _deferred = new();
        private readonly SemaphoreSlim _deferredPosted = new(0);
        private readonly ZoneService _service;
        private readonly SoeConnection _connection;
        private bool _fired;
        private readonly AccountEconomyStore _store;

        public World(uint copies, bool deferReveal = false)
        {
            _store = new(_root);
            _store.GetOrCreate(AccountId, new(new Dictionary<uint, uint> { [1] = 250, [4] = 12_500 },
                [new(123, CrateItemId, 0, copies, "explicit-test-seed", false)]));
            _service = new(this, this, _tickets, new ZoneOptions
            {
                EconomyStoreRoot = _root, EnableGas = false, SendDoors = false, SendVehicles = false,
                SendContainers = true, GroundLootRadius = 0, DevGroundLootMs = 0, AutoMatchMs = 0,
                Weapons = WeaponStageOptions.Default,
                Combat = CombatOptions.Default,
            }); // Queued tests marshal the real reveal and next-crate callbacks onto this gateway.
            var admission = _tickets.Issue(CharacterGuid, "Crate shooting test", gender: 2,
                headId: 3, hairId: 2, skinToneId: 664, profileId: 270, accountId: AccountId);
            var request = new SessionRequest(3, (uint)CharacterGuid, 512, ZoneService.ProtocolName);
            var endpoint = new IPEndPoint(IPAddress.Loopback, 5901);
            _connection = new(endpoint, in request, SessionSettings.WithSeed(1),
                _service.OnSessionRequest(endpoint, in request), _service, this, (_, _) => { }, now: 0);
            _service.OnConnected(_connection);
            using var login = new PacketWriter();
            login.WriteByte(GatewayLoginRequest.Opcode);
            login.WriteUInt64(CharacterGuid);
            login.WriteString(admission.Ticket);
            login.WriteString(GatewayLoginRequest.AugustProtocol);
            login.WriteString(GatewayLoginRequest.AugustVersion);
            _service.OnMessage(_connection, login.Written.ToArray());
            Send(w => w.WriteByte(ZoneOpcodes.ClientIsReady));
            if (deferReveal)
                _service.Post = work =>
                {
                    _deferred.Enqueue(work);
                    _deferredPosted.Release();
                };
        }

        public AccountEconomySnapshot Account => _store.GetOrCreate(AccountId);
        public AccountEconomySnapshot PersistedAccount => new AccountEconomyStore(_root).GetOrCreate(AccountId);
        public uint CratesHeld => checked((uint)Account.Items.Where(i => i.AccountItemId == CrateItemId).Sum(i => (long)i.Count));
        public OwnedAccountItem[] Rewards => Account.Items.Where(i => i.AccountItemId != CrateItemId).ToArray();
        public byte[][] Sent => _sent.ToArray();
        public ulong CrateGuid => BitConverter.ToUInt64(_sent.Last(p => Is(p, 0xf5, 10)), 2);
        public ulong GunGuid => BitConverter.ToUInt64(_sent.Last(p => Is(p, 0x11, 2)
            && p.Length >= 31 && BitConverter.ToUInt32(p, 15) == CrateOpeningWeapon.ItemId), 23);

        public Vector3 CratePosition
        {
            get
            {
                byte[] spawn = _sent.Last(p => p[0] == ZoneOpcodes.AddLightweightNpc
                    && BitConverter.ToUInt64(p, 1) == CrateGuid);
                var r = new PacketReader(spawn.AsSpan(10 + (spawn[9] & 3)));
                r.ReadString(); r.ReadUInt32(); r.ReadByte(); r.ReadUInt32();
                for (int i = 0; i < 4; i++) r.ReadSingle();
                r.ReadString(); r.ReadString(); r.ReadUInt32();
                return new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            }
        }

        public void Start(bool openOne) => Send(w =>
        {
            w.WriteByte(0xf5); w.WriteByte(3); w.WriteUInt32(CrateItemId); w.WriteBool(openOne);
        });

        public async Task Fire(uint projectile)
        {
            // The server deliberately uses its own refire clock. Wait the actual gallery gate
            // between accepted shots instead of bypassing it or packing six rifle pellets together.
            if (_fired) await Task.Delay(RetailBalance.RefireGateMs(CrateOpeningWeapon.ItemId,
                CombatOptions.Default.RefireFloorMs, 0, CombatOptions.Default.ShippedRefireGate) + 10);
            _fired = true;
            Vector3 origin = CratePosition - Vector3.UnitZ * 2;
            byte[]? location = _sent.LastOrDefault(p => Is(p, 0x11, 10) && p.Length == 38);
            if (location is not null)
                origin = new(BitConverter.ToSingle(location, 3), BitConverter.ToSingle(location, 7), BitConverter.ToSingle(location, 11));
            Send(w => w.WriteRaw(ShootingPacketBuilder.Fire(GunGuid, origin.X, origin.Y, origin.Z, [projectile])));
        }

        public void Hit(uint projectile, ulong target, Vector3 position) => Send(w => w.WriteRaw(
            ShootingPacketBuilder.HitReport(projectile, target, "WorldRoot", position.X, position.Y, position.Z)));

        public async Task RunDeferredReveal()
        {
            // Marshal onto this test's serialized gateway execution, just as the host listener does.
            Assert.True(await _deferredPosted.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(_deferred.TryDequeue(out var work));
            Assert.NotNull(work);
            work();
            Assert.Empty(_deferred);
        }

        public void AssertResultsPreserved(int exitStart, uint count)
        {
            var packets = _sent.Skip(exitStart).ToArray();
            Assert.DoesNotContain(packets, p => Is(p, 0xf5, 2) && p[^6] == 1);
            byte[] screen = Assert.Single(packets, p => Is(p, 0xf5, 2));
            Assert.Equal(1, screen[3]); // ShowResultsMessage
            Assert.Equal(count, BitConverter.ToUInt32(screen, screen.Length - 4));
            var reader = new PacketReader(screen.AsSpan(5));
            reader.ReadString(); // info message
            Assert.Equal(count > 0 ? "UI.CrateOpening.FinishedCongrats" : "UI.CrateOpening.FinishedNoCratesOpened",
                reader.ReadString());
            Assert.DoesNotContain(packets, p => Is(p, 0xf5, 11));
        }

        public void AssertNativeHandPreserved(int galleryStart)
        {
            // Server-injected Fire packets alone would pass even if these packets unbound the
            // real client's rifle. No character/inventory recomposition may split a gallery run.
            var packets = _sent.Skip(galleryStart).ToArray();
            Assert.DoesNotContain(packets, p => p[0] == 0x94
                || Is(p, 0x11, 2) || Is(p, 0x11, 4)
                || Is(p, 0xac, 0x11) || Is(p, 0xac, 0x19) || Is(p, 0xac, 0x23)
                || Is(p, 0xac, 0x24) || Is(p, 0xac, 0x28));
        }

        public void AssertLatestInventoryBeforeFinalAppearance(int exitStart)
        {
            var packets = _sent.Skip(exitStart).ToArray();
            byte[] ownership = Assert.Single(packets, p => Is(p, 0xac, 0x11));
            var reader = new PacketReader(ownership.AsSpan(2));
            int count = reader.ReadInt32();
            var actual = new List<(ulong Instance, uint Item, uint Type, uint Count)>();
            for (int i = 0; i < count; i++)
            {
                ulong key = reader.ReadUInt64();
                ulong instance = reader.ReadUInt64();
                Assert.Equal(key, instance);
                actual.Add((instance, reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32()));
            }
            var expected = PersistedAccount.Items.Select(i => (i.InstanceId, i.AccountItemId, i.ItemType, i.Count));
            Assert.Equal(expected.OrderBy(i => i.InstanceId), actual.OrderBy(i => i.Instance));
            int catalogAt = Array.IndexOf(packets, ownership);
            int skinAt = Array.FindIndex(packets, p => Is(p, 0xac, 0x23));
            int dressAt = Array.FindLastIndex(packets, p => Is(p, 0x94, 1));
            int unsetAt = Array.FindIndex(packets, p => Is(p, 0x94, 3)
                && p.Length == UnsetCharacterEquipmentSlot.Length && BitConverter.ToUInt32(p, 18) == 7);
            int deleteAt = Array.FindIndex(packets, p => Is(p, 0x11, 4)
                && p.Length == ItemDelete.Length && BitConverter.ToUInt64(p, 11) == GunGuid);
            Assert.True(unsetAt >= 0 && unsetAt < deleteAt && deleteAt < catalogAt
                && catalogAt < skinAt && skinAt < dressAt,
                $"Expected hand unset, gun delete, ownership, skin manager, dress; got {unsetAt}, {deleteAt}, {catalogAt}, {skinAt}, {dressAt}.");
            // This fixture entered from the unarmed menu, where fists are deliberately not bound.
            // Its final normal appearance must remain the last character/skin writer on exit.
            Assert.DoesNotContain(packets, p => Is(p, 0x94, 2));
            Assert.DoesNotContain(packets.Skip(dressAt + 1), p => p[0] == 0x94
                || Is(p, 0xac, 0x11) || Is(p, 0xac, 0x19) || Is(p, 0xac, 0x23)
                || Is(p, 0xac, 0x24) || Is(p, 0xac, 0x28));
        }

        public void AssertGalleryReset(int exitStart)
        {
            var packets = _sent.Skip(exitStart).ToArray();
            byte[] reset = Assert.Single(packets, p => Is(p, 0xf5, 2) && p[^6] == 1);
            Assert.Same(reset, packets.Last(p => p[0] == 0xf5));
            Assert.DoesNotContain(packets, p => Is(p, 0xf5, 6) || Is(p, 0xf5, 7) || Is(p, 0xf5, 11));
        }

        public void AssertGalleryGunRemoved(int exitStart)
        {
            ulong gun = GunGuid;
            var exitPackets = _sent.Skip(exitStart).ToArray();
            Assert.Contains(exitPackets, p => Is(p, 0x11, 4) && p.Length == ItemDelete.Length
                && BitConverter.ToUInt64(p, 11) == gun);
            Assert.Contains(exitPackets, p => Is(p, 0x94, 3) && p.Length == UnsetCharacterEquipmentSlot.Length
                && BitConverter.ToUInt32(p, 18) == 7);
        }

        public void Send(Action<PacketWriter> write)
        {
            using var packet = new PacketWriter();
            packet.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
            write(packet);
            _service.OnMessage(_connection, packet.Written.ToArray());
        }

        public void Dispose()
        {
            _service.OnDisconnected(_connection, DisconnectCause.PeerRequested);
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long keystreamPosition, ReadOnlySpan<byte> ciphertext) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        {
            if (direction == "s2c" && bytes.Length > 1 && bytes[0] == 0x05) _sent.Add(bytes[1..].ToArray());
        }
    }
}
