using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Appearance;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.World;

namespace Cranberry.Tests.Zone.Appearance;

[Collection(AppearanceStaticsCollection.Name)]
public sealed class CharacterIdentityTests
{
    // August HeadTypes.txt and SkinTones.txt, independent of the production resolver.
    [Theory]
    [InlineData(1u, 1u, 665u, "SurvivorMale_Head_01.adr")]
    [InlineData(2u, 1u, 666u, "SurvivorMale_Head_02.adr")]
    [InlineData(3u, 2u, 664u, "SurvivorFemale_Head_01.adr")]
    [InlineData(4u, 2u, 666u, "SurvivorFemale_Head_02.adr")]
    [InlineData(5u, 1u, 662u, "SurvivorMale_Head_03.adr")]
    [InlineData(6u, 2u, 662u, "SurvivorFemale_Head_03.adr")]
    [InlineData(7u, 1u, 664u, "SurvivorMale_Head_04.adr")]
    [InlineData(8u, 2u, 665u, "SurvivorFemale_Head_04.adr")]
    public void CreatedCharacterKeepsItsIdentityAcrossReloadZoningLandingAndPeerDress(
        uint headId, uint gender, uint skinTone, string headModel)
    {
        using var world = new Fixture(headId, gender, skinTone);
        string hairModel = gender == 2 ? "SurvivorFemale_Hair_ShortMessy.adr" : "SurvivorMale_Hair_MediumMessy.adr";
        Assert.Equal(headId, world.Admission.HeadId);
        Assert.Equal(gender, world.Admission.Gender);
        Assert.Equal(skinTone, world.Admission.SkinToneId);
        AssertSelf(world.Sent, world.Admission.Guid, gender, headModel, hairModel, skinTone);
        AssertDress(world.Sent, world.Admission.Guid, headModel, hairModel, skinTone);

        world.Sent.Clear();
        world.Ready();
        world.Pump(); // auto-match request
        world.Pump(); // actual ClientBeginZoning and second self record
        Assert.Contains(world.Sent, p => p[0] == ClientBeginZoning.Opcode);
        AssertSelf(world.Sent, world.Admission.Guid, gender, headModel, hairModel, skinTone);
        AssertDress(world.Sent, world.Admission.Guid, headModel, hairModel, skinTone);

        world.Sent.Clear();
        world.Ready(); // pre-game lobby inventory and complete appearance
        AssertDress(world.Sent, world.Admission.Guid, headModel, hairModel, skinTone);

        // Landing recreates the dress after inventory owns clothing. Include a real helmet
        // to verify it cannot replace customization slot 15 or erase the saved face.
        var inventory = (PlayerInventory)world.Get("Inventory");
        var helmet = inventory.CreateInstance(2170, 1);
        inventory.BindLoadout(helmet, 12, BodySlots.Head);
        world.SetMatch("InMatch");
        world.Sent.Clear();
        world.Land();
        AssertDress(world.Sent, world.Admission.Guid, headModel, hairModel, skinTone);
        PeerSession peer = (PeerSession)world.Get("Peer");
        Assert.Equal(headModel, Assert.Single(peer.Dress, a => a.SlotId == 15).ModelName);
        Assert.Equal(skinTone, Assert.Single(peer.Dress, a => a.SlotId == 15).ShaderParameterGroupId);
        Assert.Equal(hairModel, Assert.Single(peer.Dress, a => a.SlotId == 27).ModelName);
        Assert.Contains(peer.Dress, a => a.SlotId == BodySlots.Head && a.ModelName != headModel);

        world.Sent.Clear();
        world.Dress("unchanged equipment");
        Assert.DoesNotContain(world.Sent, p => p[0] == 0x94);
    }

    private static void AssertSelf(List<byte[]> sent, ulong guid, uint gender, string head, string hair, uint skin)
    {
        byte[] packet = Assert.Single(sent, p => p[0] == SendSelfToClient.Opcode);
        var r = new PacketReader(packet.AsSpan(5));
        r.ReadUInt64(); Assert.Equal(guid, r.ReadUInt64());
        Assert.Equal(4, r.ReadByte()); r.ReadUInt64();
        Assert.Equal(gender == 2 ? 9474u : 9469u, r.ReadUInt32());
        Assert.Equal(head, r.ReadString()); Assert.Equal(hair, r.ReadString());
        r.ReadUInt32(); r.ReadUInt32();
        r.ReadString(); r.ReadString(); r.ReadString();
        r.ReadUInt32(); r.ReadUInt32(); Assert.Equal(skin, r.ReadUInt32());
    }

    private static void AssertDress(List<byte[]> sent, ulong guid, string head, string hair, uint skin)
    {
        byte[][] dresses = sent.Where(p => p.Length > 1 && p[0] == 0x94 && p[1] == 1).ToArray();
        Assert.NotEmpty(dresses);
        foreach (byte[] packet in dresses)
        {
            var r = new PacketReader(packet.AsSpan(2));
            r.ReadUInt32(); Assert.Equal(guid, r.ReadUInt64());
            r.ReadUInt32(); r.ReadString(); r.ReadString();
            int rows = r.ReadInt32();
            for (int i = 0; i < rows; i++)
            {
                r.ReadUInt32();
                uint slot = r.ReadUInt32();
                Assert.DoesNotContain(slot, new uint[] { 7, 15, 27 });
                r.ReadUInt64(); r.ReadString(); r.ReadString();
            }
            int count = r.ReadInt32();
            var attachments = new Dictionary<uint, (string Model, uint Skin)>();
            for (int i = 0; i < count; i++)
            {
                string model = r.ReadString();
                r.ReadString(); r.ReadString(); r.ReadString();
                r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32();
                uint slot = r.ReadUInt32(); uint shader = r.ReadUInt32();
                int appearances = r.ReadInt32();
                for (int j = 0; j < appearances; j++) r.ReadUInt32();
                r.ReadBool(); attachments.Add(slot, (model, shader));
            }
            Assert.Equal((head, skin), attachments[15]);
            Assert.Equal(hair, attachments[27].Model);
            Assert.Equal((head.StartsWith("SurvivorFemale_")
                ? "SurvivorFemale_Chest_Hoodie_Down_Tintable.adr"
                : "SurvivorMale_Chest_Hoodie_Down_Tintable.adr", 340u), attachments[3]);
            r.ReadBool(); Assert.True(r.AtEnd);
        }
    }

    private sealed class Fixture : ITransportLog, IPacketRecorder, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "cranberry-identity-" + Guid.NewGuid().ToString("N"));
        private readonly ConcurrentQueue<Action> _pending = new();
        private readonly ZoneService _zone;
        private readonly SoeConnection _connection;
        public GatewayAdmission Admission { get; }
        public List<byte[]> Sent { get; } = [];

        public Fixture(uint head, uint gender, uint skin)
        {
            string path = Path.Combine(_directory, "roster.json");
            var roster = CharacterRosterStore.Load(path);
            var tickets = new GatewayTicketRegistry();
            var login = new LoginService(this, this, new byte[16], roster, tickets);
            var loginConnection = Connect(login, "LoginUdp_14");
            using (var payload = new PacketWriter())
            using (var packet = new PacketWriter())
            {
                payload.WriteByte(2); payload.WriteUInt32(head); payload.WriteUInt32(270);
                payload.WriteUInt32(gender); payload.WriteString("Character" + head);
                payload.WriteUInt32(skin); payload.WriteUInt32(gender); payload.WriteUInt32(2);
                foreach (string value in new[] { "Windows 10 Home", "6.2", "0.0.118.208059", "Live" }) payload.WriteString(value);
                packet.WriteByte(CharacterCreateRequest.Opcode); packet.WriteUInt64(1); packet.WriteCountedBytes(payload.Written);
                login.OnMessage(loginConnection, packet.Written.ToArray());
            }
            CharacterEntry character = Assert.Single(CharacterRosterStore.Load(path).Snapshot());
            Assert.Equal(head, CharacterSelectionPayload.Parse(character.Payload).HeadId);
            // Use a fresh login service against the reloaded roster, as on a server restart.
            login = new LoginService(this, this, new byte[16], CharacterRosterStore.Load(path), tickets);
            using (var context = new PacketWriter())
            using (var packet = new PacketWriter())
            {
                context.WriteString("en_US"); context.WriteUInt32(0); context.WriteUInt32(0); context.WriteByte(0);
                context.WriteString(""); context.WriteUInt32(0); context.WriteByte(0);
                context.WriteUInt32(0); context.WriteString(""); context.WriteInt32(0); context.WriteUInt32(0);
                for (int i = 0; i < 4; i++) context.WriteString("");
                context.WriteByte(0);
                packet.WriteByte(CharacterLoginRequest.Opcode); packet.WriteUInt64(character.EntityKey);
                packet.WriteUInt64(1); packet.WriteCountedBytes(context.Written);
                Sent.Clear(); login.OnMessage(loginConnection, packet.Written.ToArray());
            }
            var reply = new PacketReader(Assert.Single(Sent).AsSpan(1));
            reply.ReadUInt64(); reply.ReadUInt64(); Assert.Equal(1u, reply.ReadUInt32());
            var gateway = new PacketReader(reply.ReadCountedBytes());
            gateway.ReadByte(); gateway.ReadByte(); gateway.ReadUInt32(); gateway.ReadString();
            string ticket = gateway.ReadString();
            Assert.True(tickets.TryValidate(ticket, character.EntityKey, out var admission));
            Admission = admission;
            string wardrobeRoot = Path.Combine(_directory, "wardrobe");
            using (var store = new WardrobeStore(wardrobeRoot, coalesceMs: 0))
            {
                var wardrobe = store.Load(admission.Guid);
                Assert.True(wardrobe.TryApply(new SkinItemSelectionRequest(0x32, 1, 0, 1, 3250, 2888),
                    out _, out _, out _));
                store.Save(admission.Guid, wardrobe);
                store.FlushPending();
            }
            _zone = new ZoneService(this, this, tickets, new ZoneOptions
            {
                AutoMatchMs = 1, SendProximateItems = false,
                DynamicAppearanceSourcePath = @"C:\Z1\Server\Data\dynamicAppearanceFriend.bin",
                Skins = new SkinOptions { WardrobeStoreRoot = wardrobeRoot },
            }) { Post = _pending.Enqueue };
            _connection = Connect(_zone, ZoneService.ProtocolName);
            using var request = new PacketWriter();
            request.WriteByte(GatewayLoginRequest.Opcode); request.WriteUInt64(admission.Guid);
            request.WriteString(ticket); request.WriteString(GatewayLoginRequest.AugustProtocol); request.WriteString(GatewayLoginRequest.AugustVersion);
            Sent.Clear(); _zone.OnMessage(_connection, request.Written.ToArray());
        }

        private SoeConnection Connect(ISoeService service, string protocol)
        {
            var request = new SessionRequest(3, 123, 512, protocol);
            var connection = new SoeConnection(new(IPAddress.Loopback, 12345), in request,
                new(), SessionDecision.Clear, service, this, (_, _) => { }, 0);
            service.OnConnected(connection); return connection;
        }
        public object Get(string name) => _connection.Tag!.GetType().GetProperty(name)!.GetValue(_connection.Tag)!;
        public void SetMatch(string name)
        {
            var property = _connection.Tag!.GetType().GetProperty("Match")!;
            property.SetValue(_connection.Tag, Enum.Parse(property.PropertyType, name));
        }
        public void Ready() => _zone.OnMessage(_connection, [new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte(), ZoneOpcodes.ClientIsReady]);
        public void Land()
        {
            object state = _connection.Tag!;
            state.GetType().GetProperty("ChuteGuid")!.SetValue(state, 0x4000_0000_0000_0001UL);
            state.GetType().GetProperty("MountRequested")!.SetValue(state, true);
            _zone.OnMessage(_connection, [new GatewayHeader(GatewayTunnelFromClient.Opcode, 0).ToByte(),
                ZoneOpcodes.VehicleBase, VehicleDismiss.SubOpcode, .. BitConverter.GetBytes(0UL)]);
            Assert.Equal(0UL, Get("ChuteGuid"));
        }
        public void Dress(string reason) => typeof(ZoneService).GetMethod("SendCharacterAppearance", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_zone, [_connection, _connection.Tag!, reason]);
        public void Pump()
        {
            Assert.True(SpinWait.SpinUntil(() => !_pending.IsEmpty, 5000));
            Assert.True(_pending.TryDequeue(out var work)); work!();
        }
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        { if (direction == "s2c") Sent.Add(connection.ProtocolName == ZoneService.ProtocolName ? bytes[1..].ToArray() : bytes.ToArray()); }
        public void Dispose()
        {
            _connection.Disconnect();
            File.Delete(Path.Combine(_directory, "roster.json"));
            foreach (string file in Directory.GetFiles(Path.Combine(_directory, "wardrobe"))) File.Delete(file);
            Directory.Delete(Path.Combine(_directory, "wardrobe"));
            Directory.Delete(_directory);
        }
    }
}
