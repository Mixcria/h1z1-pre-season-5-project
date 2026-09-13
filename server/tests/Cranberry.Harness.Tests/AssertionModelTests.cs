using Cranberry.Harness.Behaviour;
using Cranberry.Harness.Runtime;

namespace Cranberry.Harness.Tests;

public sealed class AssertionModelTests
{
    private static MilestoneLog NewLog(out HarnessClock clock)
    {
        clock = new HarnessClock();
        return new MilestoneLog(clock);
    }

    [Fact]
    public async Task Waiting_for_a_milestone_that_already_happened_returns_immediately()
    {
        MilestoneLog log = NewLog(out _);
        log.Note(HarnessMilestone.MenuClientIsReadySent);

        MilestoneRecord? record = await log.WaitAsync(
            HarnessMilestone.MenuClientIsReadySent, 1, TimeSpan.FromMilliseconds(10), CancellationToken.None);

        Assert.NotNull(record);
        Assert.Equal(1, record.Occurrence);
    }

    [Fact]
    public async Task Waiting_completes_when_the_milestone_arrives_later()
    {
        MilestoneLog log = NewLog(out _);
        Task<MilestoneRecord?> waiting = log.WaitAsync(
            HarnessMilestone.ZoningClientIsReadySent, 1, TimeSpan.FromSeconds(5), CancellationToken.None);

        await Task.Delay(20);
        log.Note(HarnessMilestone.ZoningClientIsReadySent, "from the responder");

        MilestoneRecord? record = await waiting;
        Assert.NotNull(record);
        Assert.Equal("from the responder", record.Detail);
    }

    [Fact]
    public async Task A_missed_deadline_returns_null_rather_than_hanging()
    {
        MilestoneLog log = NewLog(out _);
        MilestoneRecord? record = await log.WaitAsync(
            HarnessMilestone.ZoningClientIsReadySent, 1, TimeSpan.FromMilliseconds(30), CancellationToken.None);
        Assert.Null(record);
    }

    [Fact]
    public async Task Occurrences_are_counted_so_the_menu_ClientIsReady_cannot_satisfy_the_zoning_one()
    {
        // The loose check this replaces is exactly what let the four-hour Z2 hang through: the menu
        // ClientIsReady had already fired, so "did the client ever send ClientIsReady" was true.
        MilestoneLog log = NewLog(out _);
        log.Note(HarnessMilestone.MenuClientIsReadySent);

        MilestoneRecord? second = await log.WaitAsync(
            HarnessMilestone.MenuClientIsReadySent, 2, TimeSpan.FromMilliseconds(30), CancellationToken.None);
        Assert.Null(second);
        Assert.Equal(1, log.Count(HarnessMilestone.MenuClientIsReadySent));

        log.Note(HarnessMilestone.MenuClientIsReadySent);
        Assert.True(log.Has(HarnessMilestone.MenuClientIsReadySent, 2));
    }

    [Fact]
    public void The_timeline_keeps_order_and_the_last_entry()
    {
        MilestoneLog log = NewLog(out _);
        log.Note(HarnessMilestone.LoginSessionOpen);
        log.Note(HarnessMilestone.LoginReplyReceived);
        log.Note(HarnessMilestone.GatewaySessionOpen);

        Assert.Equal(3, log.Timeline.Count);
        Assert.Equal(HarnessMilestone.GatewaySessionOpen, log.Last()!.Milestone);
        Assert.Contains("LoginReplyReceived", log.Reached(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_missed_milestone_report_names_what_was_expected_and_what_the_server_last_said()
    {
        var clock = new HarnessClock();
        var log = new MilestoneLog(clock);
        var journal = new PacketJournal(10);

        log.Note(HarnessMilestone.ZoningBegun);
        journal.Record(new JournalEntry(
            TimeSpan.FromSeconds(1.5), "ExternalGatewayApi_3", PacketDirection.FromServer,
            "ch0 ZoneDoneSendingInitialData", [0x05, 0x05, 0x00]));

        HarnessAssertionException failure = HarnessAssertion.Missed(
            HarnessMilestone.ZoningClientIsReadySent,
            "ClientBeginZoning",
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(4.002),
            log,
            journal,
            extra: "the server had not delivered: SendSelfToClient after ClientBeginZoning");

        string message = failure.Message;
        Assert.Contains("[F1]", message, StringComparison.Ordinal);
        Assert.Contains("ZoningClientIsReadySent", message, StringComparison.Ordinal);
        Assert.Contains("ClientBeginZoning", message, StringComparison.Ordinal);
        Assert.Contains("4.000s", message, StringComparison.Ordinal);
        Assert.Contains("4.002s", message, StringComparison.Ordinal);
        Assert.Contains("40 of 40 successful zonings", message, StringComparison.Ordinal);
        Assert.Contains("SendSelfToClient", message, StringComparison.Ordinal);
        Assert.Contains("ZoneDoneSendingInitialData", message, StringComparison.Ordinal);
        Assert.Contains("050500", message, StringComparison.Ordinal);
        Assert.Equal(HarnessMilestone.ZoningClientIsReadySent, failure.Milestone);
        Assert.Equal("F1", failure.Definition!.Id);
    }

    [Fact]
    public void The_journal_keeps_only_its_capacity_but_remembers_the_total()
    {
        var journal = new PacketJournal(4);
        for (int i = 0; i < 10; i++)
        {
            journal.Record(new JournalEntry(
                TimeSpan.FromMilliseconds(i), "link", PacketDirection.ToServer, $"packet {i}", [(byte)i]));
        }

        Assert.Equal(4, journal.Snapshot().Count);
        Assert.Equal(10, journal.TotalRecorded);
        Assert.Contains("packet 9", journal.Tail(), StringComparison.Ordinal);
        Assert.DoesNotContain("packet 5", journal.Tail(), StringComparison.Ordinal);
    }

    [Fact]
    public void Long_packets_are_truncated_in_the_report_with_the_full_length_shown()
    {
        var entry = new JournalEntry(
            TimeSpan.Zero, "link", PacketDirection.FromServer, "ReferenceData", new byte[1000]);
        string hex = entry.Hex(16);
        Assert.StartsWith("00000000", hex, StringComparison.Ordinal);
        Assert.Contains("+984 B", hex, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_catalogued_row_has_a_budget_an_evidence_citation_and_a_unique_id()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MilestoneDefinition definition in MilestoneCatalogue.All)
        {
            Assert.True(definition.Budget > TimeSpan.Zero, $"{definition.Id} has no budget");
            Assert.Contains("docs/71", definition.Evidence, StringComparison.Ordinal);
            Assert.True(ids.Add(definition.Id), $"duplicate catalogue id {definition.Id}");
            Assert.Same(definition, MilestoneCatalogue.ById(definition.Id));
        }

        Assert.Equal("F1", MilestoneCatalogue.For(HarnessMilestone.ZoningClientIsReadySent)!.Id);
        Assert.Equal(TimeSpan.FromSeconds(4), MilestoneCatalogue.ById("F1").Budget);
    }

    [Fact]
    public void An_unknown_catalogue_id_fails_loudly() =>
        Assert.Throws<KeyNotFoundException>(() => MilestoneCatalogue.ById("Z9"));

    [Fact]
    public void The_clock_scales_observed_intervals_together()
    {
        var real = new HarnessClock();
        var fast = new HarnessClock { Scale = 0.01 };
        Assert.Equal(TimeSpan.FromSeconds(4), real.Scaled(TimeSpan.FromSeconds(4)));
        Assert.Equal(TimeSpan.FromMilliseconds(40), fast.Scaled(TimeSpan.FromSeconds(4)));
    }
}
