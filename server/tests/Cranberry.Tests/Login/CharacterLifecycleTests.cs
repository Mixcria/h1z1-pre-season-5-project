using System.Net;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;

namespace Cranberry.Tests.Login;

public sealed class CharacterLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedRosterCreateKeepsTheSavedRowsNameAndHighWater(bool failRename)
    {
        using var world = new World();
        byte[] saved = File.ReadAllBytes(world.RosterPath);
        using (world.BlockRoster(failRename))
        {
            Exception? failure = Record.Exception(() => world.Roster.TryCreateUnique(1, Payload("Retry"), out _));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.False(world.Roster.ContainsName("Retry"));
            Assert.Equal(2, world.Roster.Snapshot().Length);
        }
        Assert.Equal(saved, File.ReadAllBytes(world.RosterPath));
        Assert.Equal(2, CharacterRosterStore.Load(world.RosterPath).Snapshot().Length);

        Assert.True(world.Roster.TryCreateUnique(1, Payload("Retry"), out CharacterEntry created));
        Assert.Equal(world.Second.EntityKey + 0x10, created.EntityKey);
        Assert.True(CharacterRosterStore.Load(world.RosterPath).ContainsAvailable(created.EntityKey, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedRosterDeleteKeepsTheSavedCharacterUntilRetry(bool failRename)
    {
        using var world = new World();
        byte[] saved = File.ReadAllBytes(world.RosterPath);
        using (world.BlockRoster(failRename))
        {
            Exception? failure = Record.Exception(() => world.Roster.Remove(world.First.EntityKey));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.True(world.Roster.ContainsAvailable(world.First.EntityKey, 1));
        }
        Assert.Equal(saved, File.ReadAllBytes(world.RosterPath));
        Assert.True(CharacterRosterStore.Load(world.RosterPath).ContainsAvailable(world.First.EntityKey, 1));

        Assert.True(world.Roster.Remove(world.First.EntityKey));
        Assert.False(CharacterRosterStore.Load(world.RosterPath).ContainsAvailable(world.First.EntityKey, 1));
        Assert.Equal(world.Second.EntityKey + 0x10, world.Roster.Create(1, Payload("Next")).EntityKey);
    }

    [Fact]
    public void SuccessfulDeleteRevokesEveryOutstandingHandoffAndPreventsAReselect()
    {
        using var world = new World();
        world.Login("credential-b");
        string otherTicket = world.SelectTicket(world.Second.EntityKey);
        world.Login("credential-a");
        string firstTicket = world.SelectTicket(world.First.EntityKey);
        string retryTicket = world.SelectTicket(world.First.EntityKey);
        Assert.True(world.Tickets.TryValidate(firstTicket, world.First.EntityKey, out _));
        Assert.True(world.Tickets.TryValidate(firstTicket, world.First.EntityKey, out _)); // Normal handoffs remain reusable.

        world.Delete(world.First.EntityKey);

        Assert.Equal(CharacterDeleteReply.Success, world.DeleteStatus);
        Assert.False(world.Tickets.TryValidate(firstTicket, world.First.EntityKey, out _));
        Assert.False(world.Tickets.TryValidate(retryTicket, world.First.EntityKey, out _));
        Assert.True(world.Tickets.TryValidate(otherTicket, world.Second.EntityKey, out _));
        Assert.False(CharacterRosterStore.Load(world.RosterPath).ContainsAvailable(world.First.EntityKey, 1));
        world.CharacterLogin(world.First.EntityKey);
        Assert.Equal(0u, BitConverter.ToUInt32(world.Sent[^1], 17));
        world.Delete(world.First.EntityKey);
        Assert.Equal(CharacterDeleteReply.Failure, world.DeleteStatus);
    }

    [Fact]
    public void AForeignDeleteCannotRevokeTheOwnersExistingTicket()
    {
        using var world = new World();
        world.Login("credential-b");
        string ticket = world.SelectTicket(world.Second.EntityKey);
        world.Login("credential-a");

        world.Delete(world.Second.EntityKey);

        Assert.Equal(CharacterDeleteReply.Failure, world.DeleteStatus);
        Assert.True(world.Tickets.TryValidate(ticket, world.Second.EntityKey, out GatewayAdmission admission));
        Assert.Equal("account-b", admission.AccountId);
        Assert.True(world.Roster.ContainsAvailable(world.Second.EntityKey, 1));
    }

    [Fact]
    public void LoginCreateReturnsFailureWithoutPublishingAnUnsavedRowThenAcceptsRetry()
    {
        using var world = new World();
        world.Login("credential-a");
        using (world.BlockRoster())
        {
            world.Create("Retry");
            Assert.Equal(CharacterCreateReply.StatusFailure, world.CreateStatus);
            Assert.Equal(0ul, world.CreatedGuid);
            Assert.False(world.Roster.ContainsName("Retry"));
            Assert.Equal(2, world.Accounts.CharacterOwnersSnapshot().Count);
        }

        world.Create("Retry");

        Assert.Equal(CharacterCreateReply.StatusSuccess, world.CreateStatus);
        ulong guid = world.CreatedGuid;
        Assert.Equal(world.Second.EntityKey + 0x10, guid);
        Assert.True(CharacterRosterStore.Load(world.RosterPath).ContainsAvailable(guid, 1));
        Assert.True(LocalAccountDirectory.Load(world.AccountPath, []).Owns("account-a", guid));
    }

    [Fact]
    public void LoginDeleteReturnsFailureAndKeepsHandoffUntilTheSuccessfulRetry()
    {
        using var world = new World();
        world.Login("credential-a");
        string ticket = world.SelectTicket(world.First.EntityKey);
        using (world.BlockRoster())
        {
            world.Delete(world.First.EntityKey);
            Assert.Equal(CharacterDeleteReply.Failure, world.DeleteStatus);
            Assert.True(world.Tickets.TryValidate(ticket, world.First.EntityKey, out _));
            Assert.True(world.Roster.ContainsAvailable(world.First.EntityKey, 1));
        }
        Assert.True(CharacterRosterStore.Load(world.RosterPath).ContainsAvailable(world.First.EntityKey, 1));

        world.Delete(world.First.EntityKey);

        Assert.Equal(CharacterDeleteReply.Success, world.DeleteStatus);
        Assert.False(world.Tickets.TryValidate(ticket, world.First.EntityKey, out _));
        Assert.False(CharacterRosterStore.Load(world.RosterPath).ContainsAvailable(world.First.EntityKey, 1));
    }

    [Fact]
    public void AFailedAccountBindRollsBackTheNewRosterRowAndAllowsTheSameNameOnRetry()
    {
        using var world = new World();
        world.Login("credential-a");
        using (new RenameBlocker(world.AccountPath))
        {
            world.Create("Retry");
            Assert.Equal(CharacterCreateReply.StatusFailure, world.CreateStatus);
            Assert.False(world.Roster.ContainsName("Retry"));
            Assert.Equal(2, world.Accounts.CharacterOwnersSnapshot().Count);
        }
        Assert.False(CharacterRosterStore.Load(world.RosterPath).ContainsName("Retry"));
        Assert.Equal(2, LocalAccountDirectory.Load(world.AccountPath, []).CharacterOwnersSnapshot().Count);

        world.Create("Retry");

        Assert.Equal(CharacterCreateReply.StatusSuccess, world.CreateStatus);
        ulong guid = world.CreatedGuid;
        // The failed membership followed a durable create/delete, so its GUID stays retired.
        Assert.Equal(world.Second.EntityKey + 0x20, guid);
        Assert.True(LocalAccountDirectory.Load(world.AccountPath, []).Owns("account-a", guid));
        Assert.True(CharacterRosterStore.Load(world.RosterPath).ContainsAvailable(guid, 1));
    }

    private static CharacterCreatePayload Payload(string name) =>
        new(0, 3, 270, 2, name, 664, 2, 0, "", "", "", "");

    private sealed class World : ITransportLog, IPacketRecorder, IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cranberry-character-lifecycle-" + Guid.NewGuid().ToString("N"));
        public string RosterPath => Path.Combine(_root, "roster.json");
        public string AccountPath => Path.Combine(_root, "accounts.json");
        public CharacterRosterStore Roster { get; }
        public LocalAccountDirectory Accounts { get; }
        public GatewayTicketRegistry Tickets { get; } = new();
        public CharacterEntry First { get; }
        public CharacterEntry Second { get; }
        public List<byte[]> Sent { get; } = [];
        private readonly SoeConnection _connection;
        private readonly LoginService _service;
        public uint DeleteStatus => BitConverter.ToUInt32(Sent[^1], 9);
        public uint CreateStatus => BitConverter.ToUInt32(Sent[^1], 1);
        public ulong CreatedGuid => BitConverter.ToUInt64(Sent[^1], 5);

        public World()
        {
            Directory.CreateDirectory(_root);
            Roster = CharacterRosterStore.Load(RosterPath);
            First = Roster.Create(1, Payload("First"));
            Second = Roster.Create(1, Payload("Second"));
            Accounts = LocalAccountDirectory.Load(AccountPath, []);
            Accounts.Register("account-a", "credential-a");
            Accounts.Register("account-b", "credential-b");
            Accounts.BindCharacter("account-a", First.EntityKey);
            Accounts.BindCharacter("account-b", Second.EntityKey);
            _service = new LoginService(this, this, new byte[16], Roster, Tickets, accounts: Accounts);
            var remote = new IPEndPoint(IPAddress.Loopback, 5555);
            var request = new SessionRequest(3, 123, 512, "LoginUdp_14");
            _connection = new SoeConnection(remote, in request, SessionSettings.WithSeed(1),
                _service.OnSessionRequest(remote, in request), _service, this, (_, _) => { }, now: 0);
            _service.OnConnected(_connection);
        }

        public IDisposable BlockRoster(bool failRename = false) => failRename
            ? new RenameBlocker(RosterPath) : new WriteBlocker(RosterPath);

        public void Login(string token) => Send(w =>
        {
            w.WriteByte(LoginRequest.Opcode); w.WriteString(token); w.WriteString("test-client");
            for (int i = 0; i < 4; i++) w.WriteUInt32(0);
        });

        public void Create(string name) => Send(w =>
        {
            using var payload = new PacketWriter();
            payload.WriteByte(0); payload.WriteUInt32(3); payload.WriteUInt32(270); payload.WriteUInt32(2);
            payload.WriteString(name); payload.WriteUInt32(664); payload.WriteUInt32(2); payload.WriteUInt32(0);
            for (int i = 0; i < 4; i++) payload.WriteString("");
            w.WriteByte(CharacterCreateRequest.Opcode); w.WriteUInt64(1); w.WriteCountedBytes(payload.Written);
        });

        public void Delete(ulong guid) => Send(w =>
        { w.WriteByte(CharacterDeleteRequest.Opcode); w.WriteUInt64(guid); });

        public void CharacterLogin(ulong guid) => Send(w =>
        {
            using var context = new PacketWriter();
            context.WriteString("en_US");
            context.WriteUInt32(0); context.WriteUInt32(0); context.WriteByte(0);
            context.WriteString(""); context.WriteUInt32(0); context.WriteByte(0);
            context.WriteUInt32(0); context.WriteString(""); context.WriteInt32(0); context.WriteUInt32(0);
            for (int i = 0; i < 4; i++) context.WriteString("");
            context.WriteByte(0);
            w.WriteByte(CharacterLoginRequest.Opcode); w.WriteUInt64(guid); w.WriteUInt64(1);
            w.WriteCountedBytes(context.Written);
        });

        public string SelectTicket(ulong guid)
        {
            CharacterLogin(guid);
            var reply = new PacketReader(Sent[^1].AsSpan(1));
            Assert.Equal(guid, reply.ReadUInt64()); _ = reply.ReadUInt64(); Assert.Equal(1u, reply.ReadUInt32());
            var gateway = new PacketReader(reply.ReadCountedBytes());
            _ = gateway.ReadByte(); _ = gateway.ReadByte(); _ = gateway.ReadUInt32(); _ = gateway.ReadString();
            return gateway.ReadString();
        }

        private void Send(Action<PacketWriter> write)
        {
            using var writer = new PacketWriter();
            write(writer);
            _service.OnMessage(_connection, writer.Written.ToArray());
        }

        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes)
        { if (direction == "s2c") Sent.Add(bytes.ToArray()); }

        public void Dispose()
        {
            _connection.Disconnect();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    // Owned temporary paths only. Directories deterministically reject file creation/rename
    // on Windows and Unix without timing, permissions, sleeping, or real accounts.
    private sealed class WriteBlocker : IDisposable
    {
        private readonly string _temporary;
        public WriteBlocker(string path) { _temporary = path + ".tmp"; Directory.CreateDirectory(_temporary); }
        public void Dispose() => Directory.Delete(_temporary);
    }

    private sealed class RenameBlocker : IDisposable
    {
        private readonly string _path;
        private readonly string _backup;
        public RenameBlocker(string path)
        {
            _path = path; _backup = path + ".test-backup";
            File.Move(path, _backup);
            Directory.CreateDirectory(path);
        }
        public void Dispose() { Directory.Delete(_path); File.Move(_backup, _path); }
    }
}
