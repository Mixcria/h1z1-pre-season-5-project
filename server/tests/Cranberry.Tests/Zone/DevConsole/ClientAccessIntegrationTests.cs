using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.DevConsole;
using Cranberry.Zone.DevConsole.Commands;
using Cranberry.Zone.Economy;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.HostedGames;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.DevConsole;

public partial class ConsoleIntegrationTests
{
    [Fact]
    public void AuthenticatedClientOnLoopbackGetsOnlyPlayerCommandRegistrationAndNoDebugFrontDoor()
    {
        var (service, connection, recorder) = Admit(new ZoneOptions { Console = TestConsole },
            accountId: "client-account", localOwnerAccountId: "owner-account");
        SendClientIsReady(service, connection);
        Assert.Equal(ConsoleTier.Player, Session(connection).Tier);
        string[] names = Sent(recorder).Where(IsAddWorldCommand).Select(packet =>
        {
            var reader = new PacketReader(packet.AsSpan(4));
            return reader.ReadString();
        }).ToArray();
        Assert.Equal(["hostgame"], names);
        Assert.Equal(["@cranberry/match-exit/1;ready"], ConsoleLines(recorder));
        Assert.False(Session(connection).ReadyLineSent);

        int before = SentCount(recorder);
        foreach (var command in CommandCatalog.Build(TestConsole).Commands.Where(c => c.Name != "hostgame"))
            foreach (string name in command.AllNames) SendExecuteCommand(service, connection, name);
        SendRawHash(service, connection, 0xdeadbeef);
        using var menu = new PacketWriter();
        menu.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte());
        menu.WriteByte(ZoneOpcodes.WallOfDataBase);
        menu.WriteByte(5);
        menu.WriteString("CRANBERRY");
        menu.WriteString("m");
        menu.WriteUInt32(0);
        service.OnMessage(connection, menu.Written.ToArray());
        Assert.Empty(Sent(recorder, before));
        Assert.False(Session(connection).Menu.IsOpen);
        Assert.False(Session(connection).Invulnerable);
        Assert.Null(Session(connection).TierOverride);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(6u)]
    [InlineData(7u)]
    [InlineData(9u)]
    [InlineData(10u)]
    [InlineData(11u)]
    public void ClientAndOwnerUseTheSamePublicModeAdmission(uint worldId)
    {
        MatchAdmissionContext? clientAdmission = null;
        foreach (string account in new[] { "client-account", "owner-account" })
        {
            var (service, connection, _) = Admit(new ZoneOptions { Console = TestConsole },
                post: _ => { }, accountId: account, localOwnerAccountId: "owner-account");
            SendClientIsReady(service, connection);
            using var transfer = new PacketWriter();
            transfer.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte());
            transfer.WriteByte(0xec);
            transfer.WriteUInt32(worldId);
            transfer.WriteString("");
            transfer.WriteUInt32(0);
            transfer.WriteByte(1);
            transfer.WriteInt32(1);
            service.OnMessage(connection, transfer.Written.ToArray());
            Assert.Equal("Queued", Member<object>(connection, "Match").ToString());
            var admission = Member<MatchAdmissionContext>(connection, "BountyAdmission");
            Assert.NotEqual(MatchAdmissionContext.Unknown, admission);
            if (clientAdmission is null) clientAdmission = admission;
            else
            {
                Assert.Equal(clientAdmission.QueueKind, admission.QueueKind);
                Assert.Equal(clientAdmission.Mode, admission.Mode);
            }
        }
    }

    [Fact]
    public void ClientHostedGameCommandUsesChatAndCannotIssueAdministrativeKeys()
    {
        var (service, connection, recorder) = Admit(new ZoneOptions { Console = TestConsole },
            accountId: "client-account", localOwnerAccountId: "owner-account");
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "hostgame", "key eu permanent");
        byte[] refusal = Assert.Single(Sent(recorder, before), IsChatText);
        Assert.True(IsChatText(refusal));
        Assert.Equal(0, refusal[^1]); // AlsoConsole stays false.
        var reader = new PacketReader(refusal.AsSpan(4));
        Assert.Contains("Owner or admin permission", reader.ReadString());

        before = SentCount(recorder);
        SendExecuteCommand(service, connection, "commands", "hostgame");
        Assert.NotEmpty(Sent(recorder, before));
        Assert.All(Sent(recorder, before), packet =>
        {
            Assert.True(IsChatText(packet));
            Assert.Equal(0, packet[^1]);
        });
    }

    [Fact]
    public void ClientCanRedeemAnInvitationThroughChatAndQueueTheHostedWorld()
    {
        var hosted = new HostedGameStore();
        var hostKey = hosted.IssueHostKey("owner-account", true, "EU");
        Assert.True(hosted.Redeem("host-account", hostKey.Secret!).Success);
        var game = hosted.CreateGame("host-account", "EU", 5, "Friend Game").Game!;
        var invitation = hosted.IssuePlayerKey("host-account", false, game.WorldId,
            targetAccount: "client-account");
        var (service, connection, recorder) = Admit(new ZoneOptions
        { Console = TestConsole, HostedGames = hosted }, post: _ => { },
            accountId: "client-account", localOwnerAccountId: "owner-account");
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "hostgame", "redeem " + invitation.Secret);
        Assert.True(hosted.CanEnter("client-account", game.WorldId));
        SendExecuteCommand(service, connection, "hostgame", "join " + game.WorldId);
        Assert.Equal("Queued", Member<object>(connection, "Match").ToString());
        Assert.Equal(MatchQueueKind.Hosted, Member<MatchAdmissionContext>(connection, "BountyAdmission").QueueKind);
        Assert.Equal(ConsoleTier.Player, Session(connection).Tier);
        Assert.DoesNotContain(Sent(recorder, before), IsConsolePrint);
        Assert.Contains(Sent(recorder, before), IsChatText);
        Assert.All(Sent(recorder, before).Where(IsChatText), packet => Assert.Equal(0, packet[^1]));
    }

    [Fact]
    public void ClientNormalInventoryDropStillWorksWhileNativeDeveloperItemAddIsBlocked()
    {
        var (service, connection, recorder) = DevelopmentMatch();
        SendNativeItem(service, connection, NativeAddPacket(2424));
        var inventory = Member<PlayerInventory>(connection, "Inventory");
        var item = Assert.Single(inventory.Items.Values, row => row.DefinitionId == 2424);
        Session(connection).TierOverride = ConsoleTier.Player;
        int before = SentCount(recorder);
        SendNativeItem(service, connection, NativeAddPacket(2424));
        Assert.Empty(Sent(recorder, before));
        Assert.Single(inventory.Items.Values, row => row.DefinitionId == 2424);

        using var use = new PacketWriter();
        use.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte());
        use.WriteByte(ItemUseOpcodes.ItemsBase);
        use.WriteByte(ItemUseOpcodes.RequestUseItemSub);
        use.WriteUInt64(1);
        use.WriteUInt32(4);
        ulong self = Member<ulong>(connection, "Guid");
        use.WriteUInt64(self);
        use.WriteUInt64(self);
        use.WriteUInt64(self);
        use.WriteUInt64(item.Guid);
        use.WriteByte(1);
        service.OnMessage(connection, use.Written.ToArray());
        Assert.DoesNotContain(item.Guid, inventory.Items.Keys);
        Assert.DoesNotContain(Sent(recorder, before), IsConsolePrint);
    }

    [Fact]
    public void ClientAdmissionGrantsEligibleSkinsWithoutCopyingOwnerExclusivesOrReplenishingThePackage()
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-client-access", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AccountEconomyStore(root);
            var premium = EconomyCatalog.Default.Skins[3752]; // excluded Infernal shotgun
            var owner = store.Execute("owner-account", "owner-existing", "test", draft =>
            {
                draft.Credit(4, 123_456);
                draft.Grant(premium.AccountItemId, premium.RewardItemId, 1, "previously-owned");
                return null;
            }).Snapshot!;
            var options = new ZoneOptions
            { Console = TestConsole, EconomyStoreRoot = root, ProvisionStarterAccounts = true };
            var (service, connection, _) = Admit(options,
                accountId: "client-account", localOwnerAccountId: "owner-account");
            Assert.Equal(ConsoleTier.Player, Session(connection).Tier);
            var client = store.GetOrCreate("client-account");
            Assert.False(client.Owns(premium.AccountItemId));
            Assert.Equal(ClientSkinGrant.SkinItemIds.Order(), client.Items
                .Where(item => EconomyCatalog.Default.Skins.ContainsKey(item.AccountItemId))
                .Select(item => item.AccountItemId).Order());
            Assert.Equal(StarterAccountProfile.Crowns, client.Balance(4));
            SendExecuteCommand(service, connection, "tier", "Cranberry admin");
            SendExecuteCommand(service, connection, "crowns", "999999");
            Assert.Equal(client.Revision, store.GetOrCreate("client-account").Revision);
            var second = Admit(options, accountId: "client-account", localOwnerAccountId: "owner-account");
            Assert.Equal(client.Revision, store.GetOrCreate("client-account").Revision);
            Assert.Equal(owner.Revision, store.GetOrCreate("owner-account").Revision);
            Assert.Equal(owner.Items, store.GetOrCreate("owner-account").Items);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("owner-account")]
    [InlineData("client-account")]
    public void LocalAdmissionPublishesEverySkinAndCrateFamilyForBothAccountLevels(string accountId)
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-local-access", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new ZoneOptions
            { Console = TestConsole, EconomyStoreRoot = root, ProvisionStarterAccounts = true, ProvisionLocalAccounts = true };
            var (service, connection, recorder) = Admit(options, accountId: accountId, localOwnerAccountId: "owner-account");
            SendClientIsReady(service, connection);
            byte[] packet = Assert.Single(Sent(recorder), p => p.Length >= 3
                && p[1] == SetAccountItemManager.Opcode && p[2] == SetAccountItemManager.SubOpcode);
            var reader = new PacketReader(packet.AsSpan(3));
            int count = reader.ReadInt32();
            var items = new Dictionary<uint, uint>();
            for (int i = 0; i < count; i++)
            {
                ulong instance = reader.ReadUInt64();
                Assert.Equal(instance, reader.ReadUInt64());
                uint id = reader.ReadUInt32(); reader.ReadUInt32();
                items.Add(id, reader.ReadUInt32());
            }
            Assert.All(EconomyCatalog.Default.Skins.Keys, id => Assert.True(items.ContainsKey(id), $"Missing menu skin {id}"));
            Assert.All(StarterAccountProfile.CrateFamilies, crate => Assert.Equal(500u, items[crate.ItemId]));
            var store = new AccountEconomyStore(root);
            long revision = store.GetOrCreate(accountId).Revision;
            Admit(options, accountId: accountId, localOwnerAccountId: "owner-account");
            Assert.Equal(revision, store.GetOrCreate(accountId).Revision);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
