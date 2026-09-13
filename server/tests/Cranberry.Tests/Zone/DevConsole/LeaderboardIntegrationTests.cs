using System.Reflection;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Match;
using Cranberry.Tests.Zone.MatchEndgame;

namespace Cranberry.Tests.Zone.DevConsole;

public partial class ConsoleIntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QueuedResultSurvivesLeavingAndNeverSendsAnOldRankIntoANewMatch(bool disconnect)
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-rank-departure-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (service, connection, recorder) = DevelopmentMatch();
            var pending = new ConcurrentQueue<Action>();
            service.Post = pending.Enqueue;
            SetSessionMember(connection, "AccountId", "player");
            SetSessionMember(connection, "BountyAdmission", new MatchAdmissionContext(12345, MatchQueueKind.Public, MatchMode.Solo));
            var store = new RankedScoreStore(root, backgroundWrites: true);
            store.RegisterIdentity("player", Member<ulong>(connection, "Guid"), "Player");
            typeof(ZoneService).GetField("_rankedScores", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, store);
            using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString());
            blocker.Open();
            using var transaction = blocker.BeginTransaction();
            int before = SentCount(recorder);
            SendExecuteCommand(service, connection, "endmatch");
            Assert.True(Member<MatchScore>(connection, "Score").Settled);
            Assert.Equal(0, store.Read("player", MatchMode.Solo).Matches);
            Assert.DoesNotContain(Sent(recorder, before), p => p.Length > 2 && p[1] == 0x67 && p[2] == 0x09);
            if (disconnect)
            {
                connection.Disconnect();
                service.OnDisconnected(connection, DisconnectCause.ServerRequested);
            }
            else SetSessionMember(connection, "BountyAdmission", new MatchAdmissionContext(54321, MatchQueueKind.Public, MatchMode.Solo));
            transaction.Rollback();
            store.FlushPending();
            Assert.True(SpinWait.SpinUntil(() => !pending.IsEmpty, 5000));
            while (pending.TryDequeue(out var action)) action();
            Assert.DoesNotContain(Sent(recorder, before), p => p.Length > 2 && p[1] == 0x67 && p[2] == 0x09);
            var saved = new RankedScoreStore(root).Read("player", MatchMode.Solo);
            Assert.Equal(1, saved.Matches);
            Assert.Equal(175000, saved.Points);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void NativeLeaderboardRequestAndOtherPlayerClickReachTheCommittedScoreStore()
    {
        var (service, connection, recorder) = Admit(new ZoneOptions { Console = TestConsole });
        SetSessionMember(connection, "AccountId", "viewer");
        var store = new RankedScoreStore(null);
        ulong viewerGuid = Member<ulong>(connection, "Guid");
        store.RegisterIdentity("viewer", viewerGuid, "Viewer");
        store.RegisterIdentity("other", 9009, "Other Player");
        for (int i = 0; i < 10; i++)
        {
            store.Complete("viewer", MatchMode.Solo, new("win-" + i, 1, 20, 195000));
            store.Complete("other", MatchMode.Solo, new("win-" + i, 1, 15, 190000));
        }
        typeof(ZoneService).GetField("_rankedScores", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, store);
        void Request(byte[] bytes)
        {
            service.OnMessage(connection, [new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte(), .. bytes]);
        }
        int before = SentCount(recorder);
        Request(LeaderboardTests.Selection());
        var reply = Assert.Single(Sent(recorder, before), p => p.Length > 2 && p[1] == 0x67 && p[2] == 0x0b);
        var r = new PacketReader(Payload(reply));
        r.Skip(18); Assert.Equal(2, r.ReadInt32());
        Assert.Equal(viewerGuid, r.ReadUInt64()); Assert.Equal(1u, r.ReadUInt32()); Assert.Equal("Viewer", r.ReadString());
        r.Skip(28); Assert.True(r.ReadBool());
        Assert.Equal(9009UL, r.ReadUInt64()); Assert.Equal(2u, r.ReadUInt32()); Assert.Equal("Other Player", r.ReadString());
        r.Skip(28); Assert.False(r.ReadBool()); Assert.True(r.AtEnd);
        before = SentCount(recorder);
        Request(LeaderboardTests.Other(9009));
        var other = Assert.Single(Sent(recorder, before), p => p.Length > 2 && p[1] == 0x67 && p[2] == 0x1e);
        var top = new PacketReader(Payload(other));
        top.Skip(18); Assert.Equal("Other Player", top.ReadString());
        top.Skip(12); Assert.Equal(1900000u, top.ReadUInt32()); Assert.Equal(10, top.ReadInt32());
        before = SentCount(recorder);
        Request(LeaderboardTests.Other(9009, 2));
        var empty = Assert.Single(Sent(recorder, before), p => p.Length > 2 && p[1] == 0x67 && p[2] == 0x1e);
        Assert.Equal(43, empty.Length);
        before = SentCount(recorder);
        SetSessionMember(connection, "Authenticated", false);
        Request(LeaderboardTests.Selection());
        Assert.Empty(Sent(recorder, before));
    }
}
