using System.Net;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;

namespace Cranberry.Tests.Login;

public sealed class AccountAdmissionTests
{
    [Fact]
    public void UnauthenticatedRequestsCannotEnumerateOrDeleteCharacters()
    {
        var world = new World();
        world.Send(w => w.WriteByte(0x0b));
        world.Delete(world.First.EntityKey);
        Assert.Empty(world.Recorder.Sent);
        Assert.True(world.Roster.ContainsAvailable(world.First.EntityKey, 1));
    }

    [Fact]
    public void AuthenticatedRosterAndDeleteAreRestrictedToTheResolvedAccount()
    {
        var world = new World();
        world.Login("credential-a");
        world.Recorder.Sent.Clear();
        world.Send(w => w.WriteByte(0x0b));
        var roster = new PacketReader(Assert.Single(world.Recorder.Sent).AsSpan(1));
        _ = roster.ReadUInt32();
        _ = roster.ReadBool();
        Assert.Equal(1, roster.ReadInt32());
        Assert.Equal(world.First.EntityKey, roster.ReadUInt64());
        world.Delete(world.Second.EntityKey);
        Assert.True(world.Roster.ContainsAvailable(world.Second.EntityKey, 1));
        Assert.Equal(CharacterDeleteReply.Failure, BitConverter.ToUInt32(world.Recorder.Sent[^1], 9));
        world.Delete(world.First.EntityKey);
        Assert.False(world.Roster.ContainsAvailable(world.First.EntityKey, 1));
    }

    [Fact]
    public void GatewayTicketCarriesTheServerResolvedOwnerAndRejectsOtherCharacters()
    {
        var world = new World();
        world.Login("credential-a");
        world.CharacterLogin(world.Second.EntityKey);
        Assert.Equal(0u, BitConverter.ToUInt32(world.Recorder.Sent[^1], 17));
        world.CharacterLogin(world.First.EntityKey);
        var reply = new PacketReader(world.Recorder.Sent[^1].AsSpan(1));
        Assert.Equal(world.First.EntityKey, reply.ReadUInt64());
        _ = reply.ReadUInt64();
        Assert.Equal(1u, reply.ReadUInt32());
        var gateway = new PacketReader(reply.ReadCountedBytes());
        Assert.Equal(GatewayConnectInfo.FamilyTag, gateway.ReadByte());
        Assert.Equal(GatewayConnectInfo.SubTag, gateway.ReadByte());
        _ = gateway.ReadUInt32();
        _ = gateway.ReadString();
        string ticket = gateway.ReadString();
        Assert.True(world.Tickets.TryValidate(ticket, world.First.EntityKey, out var admission));
        Assert.Equal("account-a", admission.AccountId);
        Assert.False(world.Tickets.TryValidate(ticket, world.Second.EntityKey, out _));
    }

    [Fact]
    public void ARefusedReloginClearsThePriorAccountAdmission()
    {
        var world = new World();
        world.Login("credential-a");
        Assert.Equal("account-a", world.Connection.Tag);
        world.Login("unregistered");
        Assert.Null(world.Connection.Tag);
        world.Recorder.Sent.Clear();
        world.Delete(world.First.EntityKey);
        Assert.Empty(world.Recorder.Sent);
        Assert.True(world.Roster.ContainsAvailable(world.First.EntityKey, 1));
    }

    [Fact]
    public void LauncherLoginUsesItsOwnRelayGatewayAndKeepsAccountOwnership()
    {
        var world = new World();
        world.Accounts.BindTunnel(5555, "account-a");
        world.Login(world.Accounts.IssueLauncherTicket("account-a", 43210));
        Assert.Equal("account-a", world.Connection.Tag);
        world.CharacterLogin(world.First.EntityKey);
        var reply = new PacketReader(world.Recorder.Sent[^1].AsSpan(1));
        _ = reply.ReadUInt64(); _ = reply.ReadUInt64();
        Assert.Equal(1u, reply.ReadUInt32());
        var gateway = new PacketReader(reply.ReadCountedBytes());
        _ = gateway.ReadByte(); _ = gateway.ReadByte(); _ = gateway.ReadUInt32();
        Assert.Equal("127.0.0.1:43210", gateway.ReadString());
        world.Login("credential-a"); // A tunnel cannot bypass launcher credentials with a provisioned direct token.
        Assert.Null(world.Connection.Tag);
    }

    private sealed class World

    {
        public CharacterRosterStore Roster { get; } = new();
        public GatewayTicketRegistry Tickets { get; } = new();
        public Recorder Recorder { get; } = new();
        public SoeConnection Connection { get; }
        public LoginService Service { get; }
        public CharacterEntry First { get; }
        public CharacterEntry Second { get; }
        public LocalAccountDirectory Accounts { get; }

        public World()
        {
            First = Roster.Create(1, Payload("First"));
            Second = Roster.Create(1, Payload("Second"));
            var accounts = new LocalAccountDirectory(allowLoopbackDevelopment: false);
            Accounts = accounts;
            accounts.Register("account-a", "credential-a");
            accounts.Register("account-b", "credential-b");
            accounts.BindCharacter("account-a", First.EntityKey);
            accounts.BindCharacter("account-b", Second.EntityKey);
            var log = new SilentLog();
            Service = new LoginService(log, Recorder, new byte[16], Roster, Tickets, accounts: accounts);
            var remote = new IPEndPoint(IPAddress.Loopback, 5555);
            var request = new SessionRequest(3, 123, 512, "LoginUdp_14");
            Connection = new SoeConnection(remote, in request, SessionSettings.WithSeed(1),
                Service.OnSessionRequest(remote, in request), Service, log, (_, _) => { }, now: 0);
            Service.OnConnected(Connection);
        }

        public void Login(string token) => Send(w =>
        {
            w.WriteByte(LoginRequest.Opcode);
            w.WriteString(token);
            w.WriteString("test-client");
            for (int i = 0; i < 4; i++) w.WriteUInt32(0);
        });

        public void Delete(ulong guid) => Send(w =>
        {
            w.WriteByte(CharacterDeleteRequest.Opcode);
            w.WriteUInt64(guid);
        });

        public void CharacterLogin(ulong guid) => Send(w =>
        {
            using var context = new PacketWriter();
            context.WriteString("en_US");
            context.WriteUInt32(0); context.WriteUInt32(0); context.WriteByte(0);
            context.WriteString(""); context.WriteUInt32(0); context.WriteByte(0);
            context.WriteUInt32(0); context.WriteString(""); context.WriteInt32(0);
            context.WriteUInt32(0);
            for (int i = 0; i < 4; i++) context.WriteString("");
            context.WriteByte(0);
            w.WriteByte(CharacterLoginRequest.Opcode);
            w.WriteUInt64(guid); w.WriteUInt64(1);
            w.WriteCountedBytes(context.Written);
        });

        public void Send(Action<PacketWriter> write)
        {
            using var writer = new PacketWriter();
            write(writer);
            Service.OnMessage(Connection, writer.Written.ToArray());
        }

        private static CharacterCreatePayload Payload(string name) =>
            new(0, 3, 270, 2, name, 664, 2, 0, "", "", "", "");
    }

    private sealed class Recorder : IPacketRecorder
    {
        public List<byte[]> Sent { get; } = [];
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        { if (direction == "s2c") Sent.Add(bytes.ToArray()); }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> ciphertext) { }
    }

    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
    }
}
