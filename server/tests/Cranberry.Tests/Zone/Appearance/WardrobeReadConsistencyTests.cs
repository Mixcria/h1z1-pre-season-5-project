using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Appearance;

namespace Cranberry.Tests.Zone.Appearance;

/// <summary>Deterministic pending/in-flight write cases; no timing sleeps or player data.</summary>
public sealed class WardrobeReadConsistencyTests
{
    private const ulong Character = 0x1001;
    private static readonly AugustSkinCatalogEntry[] Hats = AugustSkinCatalog.Apparel
        .Where(entry => entry.CategoryPrototypeId == 2158 && entry.AccountItemId != 0).Take(3).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadSeesAQueuedSelectionInsteadOfTheMissingOrOlderFile(bool previousFile)
    {
        using var directory = new TemporaryData();
        using var store = new WardrobeStore(directory.Root, coalesceMs: 0);
        if (previousFile) Persist(store, Selection(0));
        SemaphoreSlim gate = WriterGate(store);
        gate.Wait();
        try
        {
            store.Save(Character, Selection(1));
            AssertHat(1, store.Load(Character));
            Assert.Empty(store.Load(Character + 1).Snapshot());
        }
        finally { gate.Release(); }
    }

    [Fact]
    public void LoadSeesTheLatestSelectionAfterTheWriterTakesItFromTheQueue()
    {
        using var directory = new TemporaryData();
        using var store = new WardrobeStore(directory.Root, coalesceMs: 0);
        Persist(store, Selection(0));
        AugustWardrobeState next = Selection(1);
        object snapshotGate = SnapshotGate(next);
        Monitor.Enter(snapshotGate);
        try
        {
            store.Save(Character, next);
            WaitUntilWriterTakesTheRevision(store);
            // The writer now owns the revision but cannot snapshot or commit it. This thread
            // owns the wardrobe's reentrant snapshot lock, so its read never waits for fsync.
            AssertHat(1, store.Load(Character));
            using var disk = new WardrobeStore(directory.Root, coalesceMs: 0);
            AssertHat(0, disk.Load(Character));
        }
        finally { Monitor.Exit(snapshotGate); }
    }

    [Fact]
    public void FlushWhileARevisionIsInFlightCommitsThatRevisionOnlyOnce()
    {
        using var directory = new TemporaryData();
        using var store = new WardrobeStore(directory.Root, coalesceMs: 0);
        Persist(store, Selection(0));
        long savesBefore = store.Saves;
        AugustWardrobeState next = Selection(1);
        object snapshotGate = SnapshotGate(next);
        using var started = new ManualResetEventSlim();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        var flushing = new Thread(() =>
        {
            started.Set();
            try { store.FlushPending(); }
            catch (Exception exception) { failure = exception; }
            finally { completed.SetResult(); }
        }) { IsBackground = true };
        bool launched = false;

        Monitor.Enter(snapshotGate);
        try
        {
            store.Save(Character, next);
            WaitUntilWriterTakesTheRevision(store);
            flushing.Start();
            launched = true;
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            // This dedicated thread has no other blocking work: it has entered FlushPending
            // and must wait for the writer whose snapshot this thread currently holds.
            Assert.True(SpinWait.SpinUntil(() => completed.Task.IsCompleted
                || (flushing.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5)));
            Assert.False(completed.Task.IsCompleted);
        }
        finally
        {
            Monitor.Exit(snapshotGate);
            if (launched) Assert.True(flushing.Join(TimeSpan.FromSeconds(5)));
        }

        Assert.Null(failure);
        Assert.Equal(savesBefore + 1, store.Saves);
        Assert.Equal(0, Count(store, "_pending"));
        using var disk = new WardrobeStore(directory.Root, coalesceMs: 0);
        AssertHat(1, disk.Load(Character));
    }

    [Fact]
    public async Task ASaveDuringAnEarlierWriteCompletesWithoutAnotherSaveOrFlush()
    {
        using var directory = new TemporaryData();
        using var store = new WardrobeStore(directory.Root, coalesceMs: 0);
        Persist(store, Selection(0));
        AugustWardrobeState first = Selection(1);
        object snapshotGate = SnapshotGate(first);
        Task writer;
        Monitor.Enter(snapshotGate);
        try
        {
            store.Save(Character, first);
            WaitUntilWriterTakesTheRevision(store);
            writer = Writer(store);
            store.Save(Character, Selection(2));
            AssertHat(2, store.Load(Character));
        }
        finally { Monitor.Exit(snapshotGate); }

        await writer.WaitAsync(TimeSpan.FromSeconds(5));
        using var disk = new WardrobeStore(directory.Root, coalesceMs: 0);
        AssertHat(2, disk.Load(Character));
        Assert.Equal(0, Count(store, "_pending"));
        Assert.Equal(0, Count(store, "_dirty"));
    }

    [Fact]
    public async Task AFailedWriteRetainsTheLatestSelectionAndAnExplicitFlushRetriesIt()
    {
        using var directory = new TemporaryData();
        var messages = new ConcurrentQueue<string>();
        using var store = new WardrobeStore(directory.Root, messages.Enqueue, coalesceMs: 0);
        Persist(store, Selection(0));
        string obstruction = Path.Combine(directory.Root, WardrobeStore.FileNameFor(Character) + ".tmp");
        Directory.CreateDirectory(obstruction);

        store.Save(Character, Selection(1));
        await Writer(store).WaitAsync(TimeSpan.FromSeconds(5));

        AssertHat(1, store.Load(Character));
        Assert.Equal(1, Count(store, "_pending"));
        Assert.Equal(0, Count(store, "_dirty"));
        Assert.Single(messages, message => message.Contains("SAVE FAILED", StringComparison.Ordinal));
        using (var disk = new WardrobeStore(directory.Root, coalesceMs: 0))
            AssertHat(0, disk.Load(Character));

        Directory.Delete(obstruction); // Empty, explicitly created test obstruction only.
        store.FlushPending();
        using var recovered = new WardrobeStore(directory.Root, coalesceMs: 0);
        AssertHat(1, recovered.Load(Character));
        Assert.Equal(0, Count(store, "_pending"));
    }

    [Fact]
    public async Task ANewRevisionQueuedDuringAFailedWriteReplacesTheFailedRevision()
    {
        using var directory = new TemporaryData();
        WardrobeStore? store = null;
        int failures = 0;
        store = new WardrobeStore(directory.Root, message =>
        {
            if (message.Contains("SAVE FAILED", StringComparison.Ordinal)
                && Interlocked.Increment(ref failures) == 1)
                store!.Save(Character, Selection(2));
        }, coalesceMs: 0);
        using var ownedStore = store;
        Persist(store, Selection(0));
        string obstruction = Path.Combine(directory.Root, WardrobeStore.FileNameFor(Character) + ".tmp");
        Directory.CreateDirectory(obstruction);

        store.Save(Character, Selection(1));
        await Writer(store).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, failures); // One attempt per queued revision, with no failure retry loop.
        AssertHat(2, store.Load(Character));
        Assert.Equal(1, Count(store, "_pending"));
        Assert.Equal(0, Count(store, "_dirty"));
        Directory.Delete(obstruction);
        store.FlushPending();
        using var disk = new WardrobeStore(directory.Root, coalesceMs: 0);
        AssertHat(2, disk.Load(Character));
        Assert.Equal(0, Count(store, "_pending"));
    }

    [Fact]
    public void DisabledOrDisposedStoresDoNotAcceptNewSaveWork()
    {
        using var disabled = new WardrobeStore(null, coalesceMs: 0);
        disabled.Save(Character, Selection(1));
        Assert.Empty(disabled.Load(Character).Snapshot());
        Assert.Equal(0, Count(disabled, "_pending"));

        using var directory = new TemporaryData();
        using var disposed = new WardrobeStore(directory.Root, coalesceMs: 0);
        Persist(disposed, Selection(0));
        disposed.Dispose();
        disposed.Save(Character, Selection(1));
        AssertHat(0, disposed.Load(Character));
        Assert.Equal(0, Count(disposed, "_pending"));
    }

    [Fact]
    public void OrdinaryDisconnectThenImmediateAdmissionKeepsTheLatestWardrobeBeforeDiskCommit()
    {
        using var directory = new TemporaryData();
        using var gateway = new GatewayFixture(directory.Root);
        SoeConnection first = gateway.Admit();
        Apply(gateway.Wardrobe(first), 1);
        SemaphoreSlim gate = WriterGate(gateway.Store);
        gate.Wait();
        try
        {
            gateway.Close(first);
            Assert.Null(gateway.Service.PeerRegistry.Find(Character));
            SoeConnection replacement = gateway.Admit();
            // The earlier session has already completed departure: the overlapping-session
            // wardrobe handoff cannot satisfy this ordinary reconnect case.
            AssertHat(1, gateway.Wardrobe(replacement));
        }
        finally { gate.Release(); }
    }

    private static AugustWardrobeState Selection(int hat)
    {
        var state = new AugustWardrobeState();
        Apply(state, hat);
        return state;
    }

    private static void Apply(AugustWardrobeState state, int hat)
    {
        AugustSkinCatalogEntry entry = Hats[hat];
        Assert.True(state.TryApply(new SkinItemSelectionRequest(SkinItemSelectionRequest.RequestSetSkinItem,
            Field1: 0, Field2: 0, SlotType: 0, entry.CategoryPrototypeId, entry.AccountItemId),
            out _, out _, out _));
    }

    private static void AssertHat(int hat, AugustWardrobeState state) =>
        Assert.Equal(Hats[hat].AccountItemId, Assert.Single(state.Snapshot()).AccountItemId);

    private static void Persist(WardrobeStore store, AugustWardrobeState state)
    {
        store.Save(Character, state);
        store.FlushPending();
    }

    private static SemaphoreSlim WriterGate(WardrobeStore store) => Field<SemaphoreSlim>(store, "_writerGate");
    private static Task Writer(WardrobeStore store) => Field<Task>(store, "_writer");
    private static object SnapshotGate(AugustWardrobeState state) => Field<object>(state, "_selectedByCategory");
    private static int Count(WardrobeStore store, string name) => Field<IDictionary>(store, name).Count;
    private static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static void WaitUntilWriterTakesTheRevision(WardrobeStore store) =>
        Assert.True(SpinWait.SpinUntil(() => WriterGate(store).CurrentCount == 0 && Count(store, "_dirty") == 0,
            TimeSpan.FromSeconds(5)), "The writer did not take the revision while its snapshot was held.");

    private sealed class TemporaryData : IDisposable
    {
        private readonly string _parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cranberry-wardrobe-read-consistency"));
        public string Root { get; }
        public TemporaryData() => Root = Path.Combine(_parent, Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            string target = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(target) != _parent) throw new IOException("Unexpected test cleanup directory.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }

    private sealed class GatewayFixture : IPacketRecorder, ITransportLog, IDisposable
    {
        private readonly GatewayTicketRegistry _tickets = new();
        private readonly List<SoeConnection> _connections = [];
        private uint _sequence;
        public ZoneService Service { get; }
        public WardrobeStore Store => Field<WardrobeStore>(Service, "_wardrobeStore");

        public GatewayFixture(string root)
        {
            Service = new(this, this, _tickets, new ZoneOptions
            {
                BootstrapDelayMs = 1000, EnableGas = false, SendDoors = false, SendVehicles = false,
                SendContainers = false, GroundLootRadius = 0, DevGroundLootMs = 0, AutoMatchMs = 0,
                Skins = new SkinOptions { WardrobeStoreRoot = root },
            }) { Post = _ => { } }; // No sockets or native bootstrap; queued timer work is not run.
        }

        public SoeConnection Admit()
        {
            var admission = _tickets.Issue(Character, "Wardrobe test", accountId: "wardrobe-test");
            var endpoint = new IPEndPoint(IPAddress.Loopback, 5600 + (int)++_sequence);
            var request = new SessionRequest(3, _sequence, 512, ZoneService.ProtocolName);
            var connection = new SoeConnection(endpoint, in request, SessionSettings.WithSeed(1),
                Service.OnSessionRequest(endpoint, in request), Service, this, (_, _) => { }, now: 0);
            _connections.Add(connection);
            Service.OnConnected(connection);
            using var login = new PacketWriter();
            login.WriteByte(GatewayLoginRequest.Opcode);
            login.WriteUInt64(Character);
            login.WriteString(admission.Ticket);
            login.WriteString(GatewayLoginRequest.AugustProtocol);
            login.WriteString(GatewayLoginRequest.AugustVersion);
            Service.OnMessage(connection, login.Written.ToArray());
            Assert.True((bool)connection.Tag!.GetType().GetProperty("Authenticated")!.GetValue(connection.Tag)!);
            return connection;
        }

        public AugustWardrobeState Wardrobe(SoeConnection connection) =>
            (AugustWardrobeState)connection.Tag!.GetType().GetProperty("Wardrobe")!.GetValue(connection.Tag)!;

        public void Close(SoeConnection connection)
        {
            if (!_connections.Remove(connection)) return;
            connection.Disconnect();
            Service.OnDisconnected(connection, DisconnectCause.ServerRequested);
        }

        public void Dispose()
        {
            foreach (SoeConnection connection in _connections.ToArray()) Close(connection);
            Store.Dispose();
        }

        public bool IsEnabled(TransportLogLevel level) => false;
        public void Log(TransportLogLevel level, string message) { }
        public void RecordSession(IPEndPoint remote, in SessionRequest request) { }
        public void RecordRaw(SoeConnection connection, long position, ReadOnlySpan<byte> bytes) { }
        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes) { }
    }
}
