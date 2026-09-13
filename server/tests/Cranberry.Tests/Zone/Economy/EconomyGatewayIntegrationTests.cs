using System.Net;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Economy;

namespace Cranberry.Tests.Zone.Economy;

/// <summary>Real gateway dispatch with issued account admissions; verifies sends, not client rendering.</summary>
public sealed class EconomyGatewayIntegrationTests
{
    [Fact]
    public void FriendAdmissionGetsEligibleSkinsAndCannotSelectExcludedSkinsOrGainOwnerPowers()
    {
        using var world = new World(provisionStarters: true);
        var player = world.Admit("friend", 4097);
        Assert.Equal(200_000u, Currency(world.Sent(player), 4));
        Assert.Equal("Menu", player.Tag!.GetType().GetProperty("Match")!.GetValue(player.Tag)!.ToString());
        var console = player.Tag.GetType().GetProperty("DevConsole")!.GetValue(player.Tag)!;
        Assert.Equal("Player", console.GetType().GetProperty("Tier")!.GetValue(console)!.ToString());
        Assert.Equal(ClientSkinGrant.SkinItemIds.Count + 31, Owned(world.Sent(player)).Count);
        Assert.All(ClientSkinGrant.ExcludedItemIds, id => Assert.False(world.Account("friend").Owns(id)));
        world.Send(player, writer =>
        {
            writer.WriteByte(0xac); writer.WriteByte(0x32);
            Words(writer, 1, 0, 1, 3250, 3652); // Grey Henley
        });
        var wardrobe = (AugustWardrobeState)player.Tag.GetType().GetProperty("Wardrobe")!.GetValue(player.Tag)!;
        Assert.Equal(3652u, Assert.Single(wardrobe.Snapshot()).AccountItemId);
        world.Send(player, writer =>
        {
            writer.WriteByte(0xac); writer.WriteByte(0x32);
            Words(writer, 1, 0, 1, Skin.CategoryPrototypeId, Skin.AccountItemId); // newly granted premium
        });
        Assert.Contains(wardrobe.Snapshot(), entry => entry.AccountItemId == Skin.AccountItemId);
        var selections = wardrobe.Snapshot().ToArray();
        var excluded = AugustSkinCatalog.Weapons.Single(entry => entry.AccountItemId == 3752);
        world.Send(player, writer =>
        {
            writer.WriteByte(0xac); writer.WriteByte(0x32);
            Words(writer, 1, 0, 1, excluded.CategoryPrototypeId, excluded.AccountItemId);
        });
        Assert.Equal(selections, wardrobe.Snapshot());
        long revision = world.Account("friend").Revision;
        world.Disconnect(player);
        var again = world.Admit("friend", 4097);
        Assert.Equal(revision, world.Account("friend").Revision);
        Assert.Equal(200_000u, Currency(world.Sent(again), 4));
    }

    [Fact]
    public void OwnerAdmissionPreservesWalletInventoryAndDeveloperTier()
    {
        using var world = new World(provisionStarters: true);
        world.Seed("owner", crowns: 3750, copies: 2);
        var owner = world.Admit("owner", 4097);
        Assert.Equal(3750u, Currency(world.Sent(owner), 4));
        Assert.Single(Owned(world.Sent(owner)));
        Assert.False(world.Account("owner").Receipts.ContainsKey(StarterAccountProfile.OperationId));
        Assert.False(world.Account("owner").Receipts.ContainsKey(ClientSkinGrant.OperationId));
        var console = owner.Tag!.GetType().GetProperty("DevConsole")!.GetValue(owner.Tag)!;
        Assert.Equal("Owner", console.GetType().GetProperty("Tier")!.GetValue(console)!.ToString());
    }

    private static readonly AugustSkinCatalogEntry Skin = AugustSkinCatalog.Apparel.First(entry =>
        EconomyCatalog.Default.Skins.TryGetValue(entry.AccountItemId, out var skin) && skin.CanScrap && skin.ScrapValue > 0);

    [Fact]
    public void CapturedGrinderExchangeCreditsScrapAndRefreshesOwnedRows()
    {
        using var world = new World();
        var skin = EconomyCatalog.Default.Skins[2345];
        world.Store.GetOrCreate("account", new(new Dictionary<uint, uint> { [1] = 200 },
            [new(123, skin.AccountItemId, skin.RewardItemId, 1, "explicit-test-seed")]));
        var player = world.Admit("account", 4097);
        byte[] capture = Convert.FromHexString("DF0100010000002909000001000000");
        world.Send(player, writer => writer.WriteRaw(capture));
        uint expected = 200u + (uint)skin.ScrapValue;
        Assert.Equal(expected, world.Account("account").Balance(1));
        Assert.Empty(world.Account("account").Items);
        Assert.Empty(Owned(world.Sent(player)));
        Assert.Equal(expected, Currency(world.Sent(player), 1));
        Assert.Equal((uint)skin.ScrapValue, BitConverter.ToUInt32(world.Sent(player).Last(p => Is(p, 0xdf, 2)), 3));
        long revision = world.Account("account").Revision;
        world.Send(player, writer => writer.WriteRaw(capture));
        Assert.Equal(revision, world.Account("account").Revision);
        Assert.Equal(0u, BitConverter.ToUInt32(world.Sent(player).Last(p => Is(p, 0xdf, 2)), 3));
    }

    [Fact]
    public void MenuProjectsSavedWalletAndOwnedInstancesInsteadOfTheWholeCatalogue()
    {
        using var world = new World();
        world.Seed("account", scrap: 321, crowns: 654, copies: 2);
        var player = world.Admit("account", 4097);
        var held = Assert.Single(Owned(world.Sent(player)));
        Assert.Equal((123ul, Skin.AccountItemId, 2u), held);
        Assert.Equal(321u, Currency(world.Sent(player), 1));
        Assert.Equal(654u, Currency(world.Sent(player), 4));
        Assert.Contains(world.Sent(player), packet => Is(packet, 0x27, 5));
        var sent = world.Sent(player);
        int groups = Array.FindIndex(sent, packet => Is(packet, 0x27, 6));
        int categories = Array.FindIndex(sent, packet => Is(packet, 0x27, 7));
        int products = Array.FindIndex(sent, packet => Is(packet, 0x27, 5));
        Assert.True(groups >= 0 && groups < categories && categories < products,
            "Native Skull Store category paths and definitions must arrive before the product update.");
        Assert.Equal(Convert.FromHexString("270600010000000D0000000D000000010000000D000000"), sent[groups]);
        Assert.Equal(2u, Assert.Single(world.Account("account").Items).Count);
    }

    [Fact]
    public void ScrapChecksStackCreditsOneCopyAndClearsTheLastEquippedCopy()
    {
        using var world = new World();
        world.Seed("account", copies: 2);
        var player = world.Admit("account", 4097);
        world.Send(player, writer =>
        {
            writer.WriteByte(0xac); writer.WriteByte(0x32);
            Words(writer, 1, 0, 1, Skin.CategoryPrototypeId, Skin.AccountItemId);
        });
        Assert.Contains(world.Sent(player), packet => Is(packet, 0xac, 0x24));
        world.Send(player, writer =>
        {
            writer.WriteByte(ZoneOpcodes.WallOfDataBase); writer.WriteByte(5);
            writer.WriteString("CUSTOMIZATION_WINDOW"); writer.WriteString("open"); writer.WriteUInt32(0);
        });
        Assert.Equal(1, WornCount(world.Sent(player)));

        world.Send(player, writer => Scrap(writer, 2));
        uint value = checked((uint)EconomyCatalog.Default.Skins[Skin.AccountItemId].ScrapValue);
        Assert.Equal(value, world.Account("account").Balance(1));
        Assert.Equal(1u, Assert.Single(world.Account("account").Items).Count);
        Assert.Equal(1, WornCount(world.Sent(player)));
        long revision = world.Account("account").Revision;
        world.Send(player, writer => Scrap(writer, 2)); // stale duplicate click
        Assert.Equal(revision, world.Account("account").Revision);

        world.Send(player, writer => Scrap(writer, 1));
        Assert.Equal(2 * value, world.Account("account").Balance(1));
        Assert.Empty(world.Account("account").Items);
        Assert.Empty(Owned(world.Sent(player)));
        Assert.Equal(0, WornCount(world.Sent(player)));
    }

    [Fact]
    public void NativeScrapyardOrderCommitsOnceAndReplaysTheSamePersistedReward()
    {
        using var world = new World();
        world.Seed("account", scrap: 200);
        var player = world.Admit("account", 4097);
        var order = Order(4097, 244, "SCP");
        world.Send(player, order.WriteTo);
        var first = world.Account("account");
        Assert.Equal(100u, first.Balance(1));
        OwnedAccountItem award = Assert.Single(first.Items);
        Assert.Contains(EconomyCatalog.Default.ScrapyardRewards, row => row.AccountItemId == award.AccountItemId);
        byte[] reply = world.Sent(player).Last(packet => Is(packet, 0x27, 4));
        Assert.Equal(1u, BitConverter.ToUInt32(reply, 7));
        byte[][] sent = world.Sent(player);
        int revealIndex = Array.FindLastIndex(sent, packet => Is(packet, 0x1c, 1));
        Assert.True(revealIndex >= 0 && revealIndex < Array.FindLastIndex(sent, packet => Is(packet, 0x27, 4)));
        byte[] reveal = sent[revealIndex];
        EconomySkin skin = EconomyCatalog.Default.Skins[award.AccountItemId];
        Assert.Equal(EconomyRewardReveal.Length, reveal.Length);
        Assert.Equal(skin.ImageSetId, BitConverter.ToUInt32(reveal, 64));
        Assert.Equal(skin.NameLocaleId, BitConverter.ToUInt32(reveal, 72));
        Assert.Equal(award.AccountItemId, BitConverter.ToUInt32(reveal, 80));
        Assert.Equal(skin.RarityId, BitConverter.ToUInt32(reveal, 92));
        world.Send(player, order.WriteTo);
        Assert.Equal(first.Revision, world.Account("account").Revision);
        Assert.Equal(reply, world.Sent(player).Last(packet => Is(packet, 0x27, 4)));
        Assert.Equal(reveal, world.Sent(player).Last(packet => Is(packet, 0x1c, 1)));
        Assert.Equal(award, Assert.Single(world.Account("account").Items));
        Assert.Equal(100u, Currency(world.Sent(player), 1));
    }

    [Theory]
    [InlineData(3791u)]
    [InlineData(3802u)]
    public void NativeSkullOrderPublishesTheSavedWalletAndSelectedSkinToEveryAccountCharacter(uint itemId)
    {
        using var world = new World();
        const uint skulls = 100_000;
        world.Store.GetOrCreate("account", new(new Dictionary<uint, uint> { [1] = 123, [4] = 456, [5] = skulls }));
        var offer = Assert.Single(new EconomyPurchaseService(world.Store).Offers.Values,
            offer => offer.Bundle.CurrencyId == 5 && offer.Bundle.ContentItemId == itemId);
        var player = world.Admit("account", 4097);
        var sibling = world.Admit("account", 4098);
        Assert.Empty(Owned(world.Sent(player)));
        Assert.Equal(skulls, Currency(world.Sent(player), 5));
        Assert.Equal(skulls, Wallet(world.Sent(player), "KS$"));
        var order = Order(4097, offer.Bundle.BundleId, "KS$");

        long initialRevision = world.Account("account").Revision;
        world.Send(player, (order with { SubOpcode = EconomyOrderRequest.Preview }).WriteTo);
        Assert.Equal(EconomyOrderResponse.Success,
            BitConverter.ToUInt32(world.Sent(player).Last(packet => Is(packet, 0x27, 2)), 7));
        Assert.Equal(initialRevision, world.Account("account").Revision);
        Assert.Empty(Owned(world.Sent(player)));

        world.Send(player, order.WriteTo);
        var purchased = world.Account("account");
        var owned = Assert.Single(purchased.Items);
        Assert.Equal(itemId, owned.AccountItemId);
        Assert.Equal(EconomyCatalog.Default.Skins[itemId].RewardItemId, owned.RewardItemId);
        Assert.Equal(skulls - offer.Bundle.Price, purchased.Balance(5));
        Assert.Equal(123u, purchased.Balance(1));
        Assert.Equal(456u, purchased.Balance(4));
        Assert.Equal((owned.InstanceId, itemId, 1u), Assert.Single(Owned(world.Sent(player))));
        Assert.Equal((owned.InstanceId, itemId, 1u), Assert.Single(Owned(world.Sent(sibling))));
        Assert.Equal(purchased.Balance(5), Currency(world.Sent(player), 5));
        Assert.Equal(purchased.Balance(5), Currency(world.Sent(sibling), 5));
        Assert.Equal(purchased.Balance(5), Wallet(world.Sent(player), "KS$"));
        Assert.Equal(purchased.Balance(5), Wallet(world.Sent(sibling), "KS$"));
        byte[] reply = world.Sent(player).Last(packet => Is(packet, 0x27, 4));
        Assert.Equal(EconomyOrderResponse.Success, BitConverter.ToUInt32(reply, 7));

        world.Send(player, order.WriteTo);
        Assert.Equal(purchased.Revision, world.Account("account").Revision);
        Assert.Equal(reply, world.Sent(player).Last(packet => Is(packet, 0x27, 4)));
        Assert.Equal(owned, Assert.Single(world.Account("account").Items));
        world.Disconnect(player);
        var reconnected = world.Admit("account", 4097);
        Assert.Equal((owned.InstanceId, itemId, 1u), Assert.Single(Owned(world.Sent(reconnected))));
        Assert.Equal(purchased.Balance(5), Currency(world.Sent(reconnected), 5));
        Assert.Equal(purchased.Balance(5), Wallet(world.Sent(reconnected), "KS$"));
        world.Send(reconnected, order.WriteTo);
        Assert.Equal(EconomyOrderResponse.Failure,
            BitConverter.ToUInt32(world.Sent(reconnected).Last(packet => Is(packet, 0x27, 4)), 7));
        Assert.Equal(purchased.Revision, world.Account("account").Revision);
    }

    [Fact]
    public void CapturedLegacyOpeningStartPreservesKeyAndCratesUntilShooting()
    {
        using var world = new World();
        var crate = EconomyCatalog.Default.Crates[3207];
        world.Store.GetOrCreate("account", new(new Dictionary<uint, uint> { [4] = 1000 },
            [new(123, crate.ItemId, 0, 2, "explicit-test-seed", false),
             new(124, crate.KeyItemId, 0, 1, "explicit-test-seed", false)]));
        var player = world.Admit("account", 4097);
        void Open(Cranberry.Protocol.PacketWriter writer) => writer.WriteRaw(Convert.FromHexString("F503870C000001"));
        world.Send(player, Open);
        var first = world.Account("account");
        Assert.Equal(1000u, first.Balance(4));
        Assert.True(first.Owns(crate.KeyItemId));
        Assert.Equal(2u, first.Items.Single(i => i.AccountItemId == crate.ItemId).Count);
        Assert.DoesNotContain(first.Items, item => crate.Rewards.Any(r => r.AccountItemId == item.AccountItemId));
        world.Send(player, Open);
        Assert.Equal(first.Revision, world.Account("account").Revision);
        Assert.True(world.Account("account").Owns(crate.ItemId));
    }

    [Fact]
    public void CrownOrderUnlocksButOpeningStartDoesNotAwardOrChargeAgain()
    {
        using var world = new World();
        EconomyCrate crate = EconomyCatalog.Default.Crates.Values.First(c => c.CrownsCost > 0 && c.UnlockBundleId > 0);
        world.Store.GetOrCreate("account", new(new Dictionary<uint, uint> { [4] = 1000 },
            [new(123, crate.ItemId, 0, 1, "explicit-test-seed", false)]));
        var player = world.Admit("account", 4097);
        world.Send(player, Order(4097, crate.UnlockBundleId, "KH$").WriteTo);
        var unlocked = world.Account("account");
        Assert.Equal(1000 - crate.CrownsCost, unlocked.Balance(4));
        Assert.Equal(crate.UnlockedItemId, Assert.Single(unlocked.Items).AccountItemId);
        world.Send(player, writer =>
        {
            writer.WriteByte(0xf5); writer.WriteByte(3);
            writer.WriteUInt32(crate.UnlockedItemId); writer.WriteBool(true);
        });
        var opened = world.Account("account");
        Assert.Equal(unlocked.Balance(4), opened.Balance(4));
        Assert.Equal(crate.UnlockedItemId, Assert.Single(opened.Items).AccountItemId);
        Assert.DoesNotContain(world.Sent(player), packet => Is(packet, 0xf5, 7));
        world.Send(player, writer => { writer.WriteByte(0xf5); writer.WriteByte(5); });
        Assert.Equal(opened.Revision, world.Account("account").Revision);
    }

    [Fact]
    public void SameAccountCharactersReceiveUpdatesAndReconnectWithoutRefillWhileOtherAccountStaysIsolated()
    {
        using var world = new World();
        world.Seed("shared", copies: 2);
        world.Seed("separate", scrap: 9);
        var first = world.Admit("shared", 4097);
        var second = world.Admit("shared", 4098);
        var other = world.Admit("separate", 4099);
        int otherMessages = world.Sent(other).Length;
        world.Send(first, writer => Scrap(writer, 2));
        uint value = checked((uint)EconomyCatalog.Default.Skins[Skin.AccountItemId].ScrapValue);
        Assert.Equal(value, Currency(world.Sent(second), 1));
        Assert.Equal(1u, Assert.Single(Owned(world.Sent(second))).Count);
        Assert.Equal(otherMessages, world.Sent(other).Length);
        Assert.Equal(9u, world.Account("separate").Balance(1));
        world.Disconnect(second);
        var reconnected = world.Admit("shared", 4098);
        Assert.Equal(value, Currency(world.Sent(reconnected), 1));
        Assert.Equal(1u, Assert.Single(Owned(world.Sent(reconnected))).Count);
        Assert.Equal(1u, Assert.Single(world.Account("shared").Items).Count);
    }

    [Fact]
    public void AQueuedPlayerCannotScrapOrPlaceMenuOrders()
    {
        using var world = new World();
        world.Seed("account", scrap: 200, copies: 1);
        var player = world.Admit("account", 4097);
        world.Send(player, writer =>
        {
            writer.WriteByte(ZoneOpcodes.PlayerWorldTransferRequest);
            writer.WriteUInt32(1); writer.WriteString(""); writer.WriteUInt32(0);
            writer.WriteByte(1); writer.WriteInt32(1);
        });
        long revision = world.Account("account").Revision;
        world.Send(player, writer => Scrap(writer, 1));
        world.Send(player, Order(4097, 244, "SCP").WriteTo);
        Assert.Equal(revision, world.Account("account").Revision);
        Assert.Equal(200u, world.Account("account").Balance(1));
        Assert.Equal(2u, BitConverter.ToUInt32(world.Sent(player).Last(packet => Is(packet, 0x27, 4)), 7));
    }

    private static EconomyOrderRequest Order(ulong guid, uint bundle, string currency) =>
        new(3, "client-string-is-not-an-account", guid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            1, "en_US", "", currency, "", [new(bundle, 1, 1, "0", EconomyOrderLine.NativeDefaultOutOfBandData)]);

    private static void Scrap(PacketWriter writer, uint stack)
    {
        writer.WriteByte(0xac); writer.WriteByte(0x2d);
        Words(writer, 1, 0, 98, Skin.AccountItemId); writer.WriteByte(0);
        Words(writer, 1, 1, stack, 0, 0, 0, 0);
    }

    private static void Words(PacketWriter writer, params uint[] values)
    {
        foreach (uint value in values) writer.WriteUInt32(value);
    }

    private static bool Is(byte[] body, byte opcode, byte sub) => body.Length >= 2 && body[0] == opcode && body[1] == sub;
    private static uint Currency(byte[][] sent, uint currency) => BitConverter.ToUInt32(
        sent.Last(packet => Is(packet, 0xab, 3) && BitConverter.ToUInt32(packet, 2) == currency), 6);

    private static uint Wallet(byte[][] sent, string currency)
    {
        var wallets = sent.Where(packet => Is(packet, 0x27, 0x30)).Select(packet =>
        {
            var reader = new PacketReader(packet.AsSpan(3));
            uint balance = reader.ReadUInt32();
            string code = reader.ReadString();
            Assert.True(reader.AtEnd);
            return (Balance: balance, Currency: code);
        });
        return wallets.Last(wallet => wallet.Currency == currency).Balance;
    }

    private static int WornCount(byte[][] sent)
    {
        var reader = new PacketReader(sent.Last(packet => Is(packet, 0xac, 0x23)).AsSpan(2));
        reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadString();
        return reader.ReadInt32();
    }

    private static IReadOnlyList<(ulong Instance, uint Item, uint Count)> Owned(byte[][] sent)
    {
        var reader = new PacketReader(sent.Last(packet => Is(packet, 0xac, 0x11)).AsSpan(2));
        int count = reader.ReadInt32();
        var rows = new List<(ulong, uint, uint)>();
        for (int i = 0; i < count; i++)
        {
            ulong instance = reader.ReadUInt64();
            Assert.Equal(instance, reader.ReadUInt64());
            uint item = reader.ReadUInt32(); reader.ReadUInt32();
            rows.Add((instance, item, reader.ReadUInt32()));
        }
        Assert.Equal(0, reader.ReadInt32()); Assert.Equal(0, reader.ReadInt32()); Assert.True(reader.AtEnd);
        return rows;
    }

    private sealed class World : IPacketRecorder, ITransportLog, IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-economy-gateway", Guid.NewGuid().ToString("N"));
        private readonly GatewayTicketRegistry _tickets = new();
        private readonly List<(SoeConnection Connection, byte[] Body)> _sent = [];
        private readonly List<SoeConnection> _connections = [];
        private readonly ZoneService _service;
        public AccountEconomyStore Store { get; }
        public World(bool provisionStarters = false)
        {
            Store = new(_root);
            _service = new(this, this, _tickets, new ZoneOptions
            {
                EconomyStoreRoot = _root, EnableGas = false, SendDoors = false, SendVehicles = false,
                ProvisionStarterAccounts = provisionStarters,
                SendContainers = false, GroundLootRadius = 0, DevGroundLootMs = 0, AutoMatchMs = 0,
                MenuTopBar = MenuTopBarOptions.Default with { Scrap = 777, Crowns = 888 },
            }) { Post = _ => { }, LocalOwnerAccountId = "owner" }; // This fixture never pumps timers or waits on a lobby countdown.
        }
        public AccountEconomySnapshot Account(string account) => Store.GetOrCreate(account);
        public void Seed(string account, uint scrap = 0, uint crowns = 0, uint copies = 0) =>
            Store.GetOrCreate(account, new(new Dictionary<uint, uint> { [1] = scrap, [4] = crowns },
                copies == 0 ? [] : [new(123, Skin.AccountItemId, Skin.RewardItemId, copies, "explicit-test-seed")]));
        public SoeConnection Admit(string account, ulong character)
        {
            var admission = _tickets.Issue(character, "Economy test", gender: 2, headId: 3, hairId: 2,
                skinToneId: 664, profileId: 270, accountId: account);
            var request = new SessionRequest(3, (uint)character, 512, ZoneService.ProtocolName);
            var endpoint = new IPEndPoint(IPAddress.Loopback, 5000 + _connections.Count);
            var connection = new SoeConnection(endpoint, in request, SessionSettings.WithSeed(1),
                _service.OnSessionRequest(endpoint, in request), _service, this, (_, _) => { }, now: 0);
            _connections.Add(connection);
            _service.OnConnected(connection);
            using var login = new PacketWriter();
            login.WriteByte(GatewayLoginRequest.Opcode); login.WriteUInt64(character); login.WriteString(admission.Ticket);
            login.WriteString(GatewayLoginRequest.AugustProtocol); login.WriteString(GatewayLoginRequest.AugustVersion);
            _service.OnMessage(connection, login.Written.ToArray());
            Send(connection, writer => writer.WriteByte(ZoneOpcodes.ClientIsReady));
            return connection;
        }
        public void Send(SoeConnection connection, Action<PacketWriter> write)
        {
            using var packet = new PacketWriter();
            packet.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
            write(packet); _service.OnMessage(connection, packet.Written.ToArray());
        }
        public byte[][] Sent(SoeConnection connection) => _sent.Where(item => ReferenceEquals(item.Connection, connection))
            .Select(item => item.Body).ToArray();
        public void Disconnect(SoeConnection connection)
        {
            _service.OnDisconnected(connection, DisconnectCause.PeerRequested);
            _connections.Remove(connection);
        }
        public void Dispose()
        {
            foreach (var connection in _connections.ToArray()) Disconnect(connection);
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long keystreamPosition, ReadOnlySpan<byte> ciphertext) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        {
            if (direction == "s2c" && bytes.Length > 1 && bytes[0] == 0x05) _sent.Add((connection, bytes[1..].ToArray()));
        }
    }
}
