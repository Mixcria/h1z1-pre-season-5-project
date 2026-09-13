using System.Reflection;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Match;

namespace Cranberry.Tests.Zone.DevConsole;

public partial class ConsoleIntegrationTests
{
    [Fact]
    public void EndmatchPersistsNineteenDummyKillWinAsFirstTopTenResultExactlyOnce()
    {
        string root = Path.Combine(Path.GetTempPath(), "cranberry-practice-rank-" + Guid.NewGuid());
        try
        {
            var (service, connection, recorder) = DevelopmentMatch();
            SetSessionMember(connection, "AccountId", "practice-ranking-account");
            SetSessionMember(connection, "BountyAdmission", new MatchAdmissionContext(12345, MatchQueueKind.Public, MatchMode.Solo));

            string scoreRoot = Path.Combine(root, "ranked-preseason5");
            var store = new RankedScoreStore(scoreRoot);
            store.Complete("practice-ranking-account", MatchMode.Solo, new("earlier-win", 1, 5, 180000));
            typeof(ZoneService).GetField("_rankedScores", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, store);
            var combat = Member<SessionCombat>(connection, "Combat");
            for (int kill = 0; kill < 19; kill++)
            {
                int spawnStart = SentCount(recorder);
                SendExecuteCommand(service, connection, "target", "spawn royalty");
                Assert.True(combat.Targets.Count > 0, $"kill {kill}: " + string.Join(" | ", ConsoleLines(recorder, spawnStart)));
                var target = Assert.Single(combat.Targets.All);
                Assert.True(target.FullKit);
                service.ForTest(connection).KillTarget(target.WorldGuid);
                service.ForTest(connection).KillTarget(target.WorldGuid); // duplicate outcome cannot add a kill
            }
            var score = Member<MatchScore>(connection, "Score");
            Assert.Equal(19, score.Kills);
            Assert.Equal(19, score.PracticeKills);
            Assert.True(score.PracticeSession);
            Assert.False(score.Settled);

            SendExecuteCommand(service, connection, "endmatch");
            SendExecuteCommand(service, connection, "endmatch"); // repeated command cannot add another match
            Assert.Equal("Ended", Member<object>(connection, "Match").ToString());
            Assert.True(score.Settled);
            Assert.Equal(1u, score.FinalPlacement);

            // A fresh store must see the disk result, including aggregates and replay protection.
            store = new RankedScoreStore(scoreRoot);
            var profile = store.Read("practice-ranking-account", MatchMode.Solo);
            Assert.Equal(2, profile.Matches);
            Assert.Equal(2u, profile.Wins);
            Assert.Equal(24u, profile.TotalKills);
            Assert.Equal(374000, profile.Points);
            var win = profile.Best[0];
            Assert.Equal(1u, win.Placement);
            Assert.Equal(19, win.Kills);
            Assert.Equal(194000, win.Points);
            Assert.Equal(2, store.Complete("practice-ranking-account", MatchMode.Solo, win).Matches);
            Assert.Equal(0, store.Read("practice-ranking-account", MatchMode.Duos).Matches);

            int before = SentCount(recorder);
            using var request = new PacketWriter();
            request.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
            request.WriteByte(ZoneOpcodes.MatchHistoryBase);
            request.WriteByte(0x06);
            request.WriteUInt64(0);
            request.WriteUInt64(0);
            request.WriteUInt32(1); // Solo ranking window
            request.WriteUInt32(0);
            service.OnMessage(connection, request.Written.ToArray());
            byte[] reply = Assert.Single(Sent(recorder, before), p => p.Length > 2 && p[1] == 0x67 && p[2] == 0x07);
            var reader = new PacketReader(Payload(reply));
            reader.Skip(35);
            Assert.Equal(2, reader.ReadInt32());
            reader.Skip(8); // history detail key
            Assert.Equal(194000u, reader.ReadUInt32());
            Assert.Equal(1u, reader.ReadUInt32());
            Assert.Equal(19u, reader.ReadUInt32());
            reader.Skip(16 + 36); // remaining first row, second row
            reader.Skip(24); // tier/progress totals
            Assert.Equal(374000u, reader.ReadUInt32());
            reader.Skip(12);
            Assert.Equal(2u, reader.ReadUInt32()); // season matches
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(MatchQueueKind.Unknown, MatchMode.Unknown, 0UL)]
    [InlineData(MatchQueueKind.Custom, MatchMode.Solo, 123UL)]
    [InlineData(MatchQueueKind.Public, MatchMode.Training, 123UL)]
    public void PracticeScoringStillRequiresAPublicSoloAdmission(MatchQueueKind queue, MatchMode mode, ulong matchId)
    {
        var (service, connection, recorder) = DevelopmentMatch();
        SetSessionMember(connection, "BountyAdmission", new MatchAdmissionContext(matchId, queue, mode));
        SendExecuteCommand(service, connection, "target", "spawn");
        var target = Assert.Single(Member<SessionCombat>(connection, "Combat").Targets.All);
        service.ForTest(connection).KillTarget(target.WorldGuid);
        SendExecuteCommand(service, connection, "endmatch");
        int before = SentCount(recorder);
        SendExecuteCommand(service, connection, "rank");
        Assert.Contains(ConsoleLines(recorder, before), line => line.Contains("Season: 0 matches"));
    }
}
