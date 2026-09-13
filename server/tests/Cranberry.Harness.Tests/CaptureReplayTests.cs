using System.Security.Cryptography;
using Cranberry.Harness.Replay;
using Cranberry.Zone;
using Xunit.Abstractions;

namespace Cranberry.Harness.Tests;

/// <summary>
/// A fact that needs the capture archive. It is not part of the repository (the host writes to
/// <c>C:\Aug2017\captures</c>, beside <c>C:\Aug2017\Server</c>), so on a machine without it these
/// skip rather than fail.
/// </summary>
public sealed class CaptureFactAttribute : FactAttribute
{
    public CaptureFactAttribute()
    {
        if (CaptureLocator.TryFindDirectory() is null)
        {
            Skip = "no captures\\wire-*.txt archive was found above the test assembly";
        }
    }
}

/// <summary>
/// The offline half of capture replay: parsing, summarising, diffing, and the regression guards.
/// No server is needed for any of it, and the guards over the archive are the most valuable part —
/// they turn three regressions that each cost a play-test session into a check that runs in
/// milliseconds against every session ever recorded.
/// </summary>
public sealed class CaptureReplayTests(ITestOutputHelper output)
{
    private const string GoodPreFix = "wire-20260829-150206.txt";
    private const string GoodPostFix = "wire-20260829-184346.txt";
    private const string BadHang = "wire-20260829-151627.txt";
    private const string BadG10 = "wire-20260829-173352.txt";
    private const string GunCrash = "wire-20260829-220829.txt";

    /// <summary>
    /// One historic G2 packet that is allowed to remain in the archive as diagnostic evidence.
    /// Each entry pins its complete capture, gateway run and offending row set. This is deliberately
    /// narrower than a date range or a filename-only exemption: changing even an old fixture must
    /// make the archive gate fail until its evidence is reviewed again.
    /// </summary>
    /// <param name="PacketAtSeconds">
    /// Every offending packet in this run, in order. Usually one; the 2026-08-31 deliberate draw
    /// fired twice because the owner picked the same AK-47 up, dropped it, and picked it up again.
    /// Pinning ALL of them is what makes the fixture describe the capture rather than summarise it.
    /// </param>
    /// <param name="RowSlots">The row set each of those packets carried, in the same order.</param>
    private sealed record KnownG2Fixture(
        string FileName,
        string Sha256,
        int RunIndex,
        double[] PacketAtSeconds,
        uint[][] RowSlots);

    /// <summary>
    /// Historic docs/32 captures. G1 and G3 are verified separately for their exact behaviour;
    /// this table ensures an archive sweep never quietly exempts a rewritten capture by filename.
    /// </summary>
    private sealed record KnownFatalCapture(string FileName, string Sha256, string[] GuardIds);

    private static readonly KnownFatalCapture[] KnownG1G3Fixtures =
    [
        new("wire-20260829-151627.txt",
            "25958C3CD4C595A0EA6421B91F36A53A87A90AEE3CF73432BFDD112EF31E6D23", ["G1", "G3"]),
        new("wire-20260829-173352.txt",
            "1AD8417E7BD055228DD6475B2E471721C0113AC2F73B1C51FD57A976C6C47ADD", ["G1", "G3"]),
        new("wire-20260829-180725.txt",
            "ACF68A71413EEDF25BBA697DC9AB50A939021AA9F55657FF3FCE5EDCBC04B96E", ["G1", "G3"]),
        new("wire-20260829-183258.txt",
            "5CFF158431F516FB734D7FE56F17D88032A4AC30068464E8F3D368CE61192582", ["G1", "G3"]),
    ];

    private static readonly KnownG2Fixture[] KnownG2Fixtures =
    [
        new(GunCrash,
            "2AD7E0E861FBB7D8E9FC0740553CBACDF8D1AEC222DB4C5368BE6E63D7F37D06",
            0, [93.631], [[7, 100]]),
        new(GunCrash,
            "2AD7E0E861FBB7D8E9FC0740553CBACDF8D1AEC222DB4C5368BE6E63D7F37D06",
            1, [131.450], [[7, 10]]),
        new(GunCrash,
            "2AD7E0E861FBB7D8E9FC0740553CBACDF8D1AEC222DB4C5368BE6E63D7F37D06",
            2, [141.691], [[5, 7, 11]]),
        new(GunCrash,
            "2AD7E0E861FBB7D8E9FC0740553CBACDF8D1AEC222DB4C5368BE6E63D7F37D06",
            3, [135.025], [[1, 7]]),
        new("wire-20260830-085410.txt",
            "04AF4F4075FBB83C71844CCC2754A6697D7B41F474909503CAB8286E490CFCDE",
            0, [105.539], [[5, 7]]),
        new("wire-20260830-085410.txt",
            "04AF4F4075FBB83C71844CCC2754A6697D7B41F474909503CAB8286E490CFCDE",
            1, [94.262], [[1, 7]]),
        // These two are the 31 August regression that the current unconditional live guard fixes.
        new("wire-20260831-084735.txt",
            "2D34319752BCB8E038B328B1F0BB5C8E055EE61B5E2C67405FC1E5EB7CD164EE",
            3, [107.138], [[2, 3, 4, 5, 7, 28, 29, 76]]),
        new("wire-20260831-094809.txt",
            "CD32A0EEFA12A020A04AECC07EFA6323236CA4AF8E1E83E7EBB7165379C95DCE",
            0, [16.865], [[2, 3, 4, 5, 7, 28, 29]]),
        // 2026-08-31 18:35, and this one is DIFFERENT IN KIND from the seven above: it is the first
        // slot-7 row this project ever sent DELIBERATELY, and it is the packet that falsified this
        // guard's stated cause. The client did not crash - two full sessions, both ending "Regular
        // Shutdown likely from the close button", and the only minidump under wws_crashreport is
        // still 2026-08-28's. What it DID do was freeze the owner's input until he dropped the gun.
        // So the packet is still wrong and G2 stays Fatal, but for the docs/95 reason (a binding
        // belongs on 94 02, after the ability manager) rather than the docs/45 one (a NULL
        // fire-group descriptor, which stage 1 and stage 2 have since supplied).
        new("wire-20260831-183154.txt",
            "916A687CC75E01EB78F681922C13ACA6F82CC5633C549E84E751813CA2A51CCB",
            0, [97.799, 110.047], [[2, 3, 4, 5, 7, 28, 29], [2, 3, 4, 5, 7, 28, 29]]),
    ];

    // ---- parsing --------------------------------------------------------------------------------

    [Fact]
    public void The_gateway_header_splits_five_bits_of_opcode_below_three_of_channel()
    {
        // 0x26 is the client's SetLocale: tunnel-to-server on channel 1. Read as two nibbles it
        // becomes "channel 2", which is how a capture reader manufactures phantom movement.
        PacketSignature locale = PacketSignature.ForGateway(Convert.FromHexString("263305000000656E5F5553"));
        Assert.Equal(SignatureKind.Zone, locale.Kind);
        Assert.Equal(1, locale.Channel);

        PacketSignature movement = PacketSignature.ForGateway([0x46, 0x01, 0x02, 0x03]);
        Assert.Equal(SignatureKind.Movement, movement.Kind);
        Assert.Equal(2, movement.Channel);
        Assert.Equal(PacketSignature.NoSubOpcode, movement.SubOpcode);
    }

    [Fact]
    public void EquipmentBase_and_ItemsBase_take_a_one_byte_sub_opcode()
    {
        // 05 94 01 05 00 00 00 … is SetCharacterEquipment (sub 1) then u32 profileId = 5. A u16
        // read would make the key "0x0501" and silently split the family.
        PacketSignature set = PacketSignature.ForGateway(Convert.FromHexString("059401050000000310000000000000"));
        Assert.True(EquipmentPackets.IsSetCharacterEquipment(set));
        Assert.Equal(EquipmentPackets.SetSubOpcode, set.SubOpcode);

        PacketSignature unset = PacketSignature.ForGateway(Convert.FromHexString("059403030000000310000000000000"));
        Assert.True(EquipmentPackets.IsUnsetCharacterEquipmentSlot(unset));

        Assert.Equal(1, SubOpcodeWidths.Width(ZoneOpcodes.EquipmentBase));
        Assert.Equal(1, SubOpcodeWidths.Width(ZoneOpcodes.ItemsBase));
    }

    [Fact]
    public void Synchronization_keeps_one_signature_because_its_sub_opcode_is_unknown()
    {
        // Its first payload bytes are a timestamp. Splitting on them turned one family into thirty
        // rows in the first draft of this tool.
        PacketSignature a = PacketSignature.ForGateway([0x05, ZoneOpcodes.Synchronization, 0x01, 0xEF]);
        PacketSignature b = PacketSignature.ForGateway([0x05, ZoneOpcodes.Synchronization, 0x8C, 0x02]);
        Assert.Equal(a, b);
    }

    [CaptureFact]
    public void A_capture_splits_into_runs_by_endpoint()
    {
        IReadOnlyList<CaptureRun> runs = CaptureSessionReader.ReadRuns(CaptureLocator.TryFind(GoodPostFix)!);

        CaptureRun run = Assert.Single(runs);
        Assert.NotNull(run.Login);
        Assert.Equal(CaptureLink.Gateway, run.Gateway.Link);
        Assert.True(run.Gateway.Messages.Count > 5_000, $"expected the full session, got {run.Gateway.Messages.Count}");
        Assert.Equal(3u, run.Gateway.CrcLength);

        // The two known-bad captures are the owner retrying, so they hold several runs each.
        Assert.Equal(4, CaptureSessionReader.ReadRuns(CaptureLocator.TryFind(BadHang)!).Count);
        Assert.Equal(3, CaptureSessionReader.ReadRuns(CaptureLocator.TryFind(BadG10)!).Count);
    }

    [CaptureFact]
    public void The_reference_session_bootstrap_is_summarised_by_opcode()
    {
        CaptureAnalysis analysis = CaptureReplay.Analyse(CaptureLocator.TryFind(GoodPostFix)!)[0];
        output.WriteLine(analysis.ServerStream.Render());

        Assert.Contains(analysis.ServerStream.Rows.Keys, s => s.Opcode == ZoneOpcodes.SendZoneDetails);
        Assert.Contains(analysis.ServerStream.Rows.Keys, s => s.Opcode == ZoneOpcodes.SendSelfToClient);
        Assert.Contains(analysis.ServerStream.Rows.Keys, s => s.Opcode == ZoneOpcodes.ClientBeginZoning);

        // The self record is 1,093 bytes in every session ever recorded.
        SignatureStats self = analysis.ServerStream.Rows
            .Single(r => r.Key.Opcode == ZoneOpcodes.SendSelfToClient).Value;
        Assert.Equal(RegressionGuards.SendSelfToClientLength, self.MinLength);
        Assert.Equal(RegressionGuards.SendSelfToClientLength, self.MaxLength);
    }

    // ---- the guards -----------------------------------------------------------------------------

    [CaptureFact]
    public void The_two_known_good_sessions_pass_every_guard()
    {
        foreach (string name in new[] { GoodPreFix, GoodPostFix })
        {
            foreach (CaptureAnalysis analysis in CaptureReplay.Analyse(CaptureLocator.TryFind(name)!))
            {
                GuardFinding[] fatal = [.. analysis.Guards.Where(g => g.Severity == GuardSeverity.Fatal)];
                Assert.True(fatal.Length == 0, $"{name} run #{analysis.Index}: " + string.Join("\n", fatal));
            }
        }
    }

    [CaptureFact]
    public void The_post_fix_weapon_pickup_capture_has_no_active_hand_row()
    {
        const string fileName = "wire-20260831-101011.txt";
        const string expectedHash = "920863158DB8045691F28E3581EFA2553259ECBA51B8E6E038B1953264D34A2E";
        string? path = CaptureLocator.TryFind(fileName);
        Assert.NotNull(path);
        Assert.Equal(expectedHash, Sha256(path!));

        SetCharacterEquipmentPacket[] packets =
        [
            .. CaptureSessionReader.ReadRuns(path!)
                .SelectMany(run => run.Gateway.ServerToClient)
                .Where(message => EquipmentPackets.IsSetCharacterEquipment(message.Signature))
                .Select(message => EquipmentPackets.TryParseSetCharacterEquipment(message.Bytes))
                .Where(packet => packet is not null)
                .Select(packet => packet!)
        ];

        Assert.Equal(29, packets.Length);
        Assert.All(packets, packet => Assert.True(packet.FullyConsumed));
        Assert.All(packets, packet => Assert.DoesNotContain(RegressionGuards.ActiveHandSlot, packet.SlotRowIds));
        Assert.DoesNotContain(
            CaptureReplay.Analyse(path!).SelectMany(analysis => analysis.Guards),
            finding => finding.Id == "G2" && finding.Severity == GuardSeverity.Fatal);
    }

    [CaptureFact]
    public void A_slot_seven_attachment_is_not_a_slot_seven_row()
    {
        // Every accepted session dresses the right hand with Weapon_Empty.adr. If the guard read
        // that as docs/45's fatal shape it would fire on the known-good captures and be turned off.
        CaptureRun run = CaptureSessionReader.LongestRun(CaptureLocator.TryFind(GoodPostFix)!);
        SetCharacterEquipmentPacket[] packets =
        [
            .. run.Gateway.ServerToClient
                .Where(m => EquipmentPackets.IsSetCharacterEquipment(m.Signature))
                .Select(m => EquipmentPackets.TryParseSetCharacterEquipment(m.Bytes)!)
        ];

        Assert.NotEmpty(packets);
        Assert.All(packets, p => Assert.True(p.FullyConsumed, $"consumed {p.BytesConsumed} of {p.Length}"));
        Assert.All(packets, p => Assert.Contains(RegressionGuards.ActiveHandSlot, p.AttachmentSlotIds));
        Assert.All(packets, p => Assert.Empty(p.SlotRows));
    }

    /// <summary>
    /// The 2026-09-02 narrowing, stated without a capture so it cannot rot with the archive:
    /// docs/95 §2 says the docs/32 rule is about the DRE§ path, and a 94 03 that is closed by a
    /// 94 02 SetCharacterEquipmentSlot inside the burst is WieldSequence step 2 taking an item off
    /// its peg. The first is Fatal, the second is Info, and the discriminator is the 94 02.
    /// </summary>
    [Fact]
    public void A_slot_clear_closed_by_a_binding_is_a_draw_and_a_clear_that_closes_nothing_is_the_dress()
    {
        static GuardMessage Message(double seconds, string hex)
        {
            byte[] bytes = Convert.FromHexString(hex);
            return new GuardMessage(
                TimeSpan.FromSeconds(seconds), PacketSignature.ForGateway(bytes), bytes, bytes.Length, false);
        }

        // 94 03: u8 gateway; u8 base; u8 sub; u32 profile; u64 character; u32 unknown; u32 slot.
        const string ClearSlot76 = "059403050000000310000000000000000000004C000000";
        const string ClearSlot1 = "0594030500000003100000000000000000000001000000";

        // 94 02's body is irrelevant to the guard - only its signature is read.
        GuardMessage bind = Message(10.020, "05940205000000");

        GuardFinding[] draw =
        [
            .. RegressionGuards.Run([Message(10.000, ClearSlot76), bind])
        ];
        Assert.DoesNotContain(draw, f => f.Id == "G1");
        GuardFinding info = Assert.Single(draw, f => f.Id == "G1i");
        Assert.Equal(GuardSeverity.Info, info.Severity);
        Assert.Contains("76", info.Detail, StringComparison.Ordinal);

        // The same clear, with the binding pushed beyond the burst window, is the dress again.
        GuardFinding late = Assert.Single(
            RegressionGuards.Run(
            [
                Message(10.000, ClearSlot76),
                Message(10.000 + RegressionGuards.DrawBurstWindow.TotalSeconds + 1, "05940205000000"),
            ]),
            f => f.Id == "G1");
        Assert.Equal(GuardSeverity.Fatal, late.Severity);

        // And a clear with no binding at all keeps every word of docs/32, slot 1 included.
        GuardFinding dress = Assert.Single(
            RegressionGuards.Run([Message(10.000, ClearSlot1)]), f => f.Id == "G1");
        Assert.Equal(GuardSeverity.Fatal, dress.Severity);
        Assert.Contains("crash-on-clear", dress.Detail, StringComparison.Ordinal);
    }

    [CaptureFact]
    public void G1_fires_on_every_run_of_the_two_known_bad_captures()
    {
        foreach (string name in new[] { BadHang, BadG10 })
        {
            foreach (CaptureAnalysis analysis in CaptureReplay.Analyse(CaptureLocator.TryFind(name)!))
            {
                GuardFinding finding = Assert.Single(analysis.Guards, g => g.Id == "G1");
                Assert.Equal(GuardSeverity.Fatal, finding.Severity);
                output.WriteLine($"{name} run #{analysis.Index}: {finding.Detail}");
            }
        }
    }

    [CaptureFact]
    public void Each_historic_G2_fixture_is_immutable_and_still_fires_exactly_once()
    {
        foreach (IGrouping<string, KnownG2Fixture> fixtureFile in KnownG2Fixtures.GroupBy(f => f.FileName))
        {
            string? path = CaptureLocator.TryFind(fixtureFile.Key);
            Assert.NotNull(path);

            string expectedHash = Assert.Single(fixtureFile.Select(f => f.Sha256).Distinct());
            Assert.Equal(expectedHash, Sha256(path!));

            IReadOnlyList<CaptureRun> runs = CaptureSessionReader.ReadRuns(path!);
            IReadOnlyList<CaptureAnalysis> analyses = CaptureReplay.Analyse(path!);
            foreach (KnownG2Fixture fixture in fixtureFile)
            {
                CaptureAnalysis analysis = Assert.Single(analyses, a => a.Index == fixture.RunIndex);
                GuardFinding finding = Assert.Single(analysis.Guards, g =>
                    g.Id == "G2" && g.Severity == GuardSeverity.Fatal);
                Assert.Equal(fixture.PacketAtSeconds.Length, finding.At.Count);
                foreach ((TimeSpan at, double expected) in finding.At.Zip(fixture.PacketAtSeconds))
                {
                    Assert.InRange(at.TotalSeconds, expected - 0.001, expected + 0.001);
                }

                CaptureRun run = Assert.Single(runs, r => r.Index == fixture.RunIndex);
                uint[][] rows =
                [
                    .. run.Gateway.ServerToClient
                        .Where(m => EquipmentPackets.IsSetCharacterEquipment(m.Signature))
                        .Select(m => EquipmentPackets.TryParseSetCharacterEquipment(m.Bytes))
                        .Where(p => p is not null)
                        .Select(p => p!)
                        .Where(p => p.SlotRowIds.Contains(RegressionGuards.ActiveHandSlot))
                        .Select(p => p.SlotRows.Select(row => row.SlotId).ToArray())
                ];
                Assert.Equal(fixture.RowSlots, rows);

                output.WriteLine($"{fixture.FileName} run #{fixture.RunIndex}: {finding}");
            }
        }
    }

    /// <summary>
    /// The archive-wide sweep, and the reason it is an assertion rather than a print: historic
    /// client-fatal packet shapes are permitted only as SHA-pinned fixtures; G2 is additionally
    /// pinned to its gateway run and row set. A new capture or a changed historic capture is a
    /// regression this test reports before someone needs to play it.
    /// </summary>
    [CaptureFact]
    public void No_capture_outside_the_known_immutable_fixtures_carries_a_client_fatal_packet()
    {
        var offenders = new List<string>();
        int sessions = 0;
        int redacted = 0;

        foreach (string path in CaptureLocator.All())
        {
            string name = Path.GetFileName(path);
            foreach (CaptureAnalysis analysis in CaptureReplay.Analyse(path))
            {
                sessions++;
                redacted += analysis.RedactedMessages;
                foreach (GuardFinding finding in analysis.Guards.Where(g => g.Severity == GuardSeverity.Fatal))
                {
                    bool expected = finding.Id switch
                    {
                        // G1 and G3 are the two halves of docs/32 and appear together in the same
                        // four SHA-pinned captures: the slot-clearing burst and the wardrobe
                        // re-announce that was moved onto the match-zoning path.
                        "G1" or "G3" => IsKnownG1OrG3Fixture(path, finding),
                        "G2" => IsKnownG2Fixture(path, analysis, finding),
                        _ => false,
                    };

                    if (!expected)
                    {
                        offenders.Add($"{name} run #{analysis.Index}: {finding}");
                    }
                }
            }
        }

        output.WriteLine($"swept {CaptureLocator.All().Count} capture(s), {sessions} gateway session(s), "
            + $"{redacted} explicitly redacted packet(s) unavailable to the guards");
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    private static bool IsKnownG2Fixture(string path, CaptureAnalysis analysis, GuardFinding finding)
    {
        KnownG2Fixture? fixture = KnownG2Fixtures.SingleOrDefault(f =>
            f.FileName == Path.GetFileName(path) && f.RunIndex == analysis.Index);
        return fixture is not null
            && finding.Severity == GuardSeverity.Fatal
            && string.Equals(Sha256(path), fixture.Sha256, StringComparison.Ordinal);
    }

    private static bool IsKnownG1OrG3Fixture(string path, GuardFinding finding)
    {
        KnownFatalCapture? fixture = KnownG1G3Fixtures.SingleOrDefault(f =>
            f.FileName == Path.GetFileName(path));
        return fixture is not null
            && finding.Severity == GuardSeverity.Fatal
            && fixture.GuardIds.Contains(finding.Id)
            && string.Equals(Sha256(path), fixture.Sha256, StringComparison.Ordinal);
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    // ---- the diff -------------------------------------------------------------------------------

    [Fact]
    public void A_family_that_vanishes_is_structural_and_a_count_that_drifts_is_cadence()
    {
        PacketSignature loot = PacketSignature.ForGateway([0x05, ZoneOpcodes.ProximateItemBase, 0x01]);
        PacketSignature tick = PacketSignature.ForGateway([0x05, ZoneOpcodes.GameTimeSync]);

        StreamSummary reference = StreamSummary.Build("ref",
        [
            (TimeSpan.Zero, loot, 229),
            (TimeSpan.FromSeconds(1), tick, 15),
            (TimeSpan.FromSeconds(2), tick, 15),
            (TimeSpan.FromSeconds(3), tick, 15),
            (TimeSpan.FromSeconds(4), tick, 15),
        ]);

        StreamSummary live = StreamSummary.Build("live",
        [
            (TimeSpan.FromSeconds(1), tick, 15),
            (TimeSpan.FromSeconds(2), tick, 15),
            (TimeSpan.FromSeconds(3), tick, 15),
        ]);

        StreamDiff diff = StreamDiff.Compare(reference, live);
        StreamDifference missing = Assert.Single(diff.Structural);
        Assert.Equal(DifferenceKind.Missing, missing.Kind);
        Assert.Equal(loot, missing.Signature);

        // 4 -> 3 is inside the default tolerance, so the periodic family produces no noise.
        Assert.DoesNotContain(diff.Differences, d => d.Signature == tick);
        Assert.False(diff.IsClean);
    }

    [Fact]
    public void A_payload_that_grows_is_reported_even_when_the_count_is_identical()
    {
        PacketSignature equipment = PacketSignature.ForGateway([0x05, ZoneOpcodes.EquipmentBase, 0x01]);

        StreamSummary reference = StreamSummary.Build("ref", [(TimeSpan.Zero, equipment, 624)]);
        StreamSummary live = StreamSummary.Build("live", [(TimeSpan.Zero, equipment, 648)]);

        StreamDifference difference = Assert.Single(StreamDiff.Compare(reference, live).Payload);
        Assert.Equal(DifferenceKind.SizeChanged, difference.Kind);
        Assert.Contains("624", difference.Detail);
        Assert.Contains("648", difference.Detail);
    }

    // ---- the script -----------------------------------------------------------------------------

    [CaptureFact]
    public void The_script_rebuilds_the_gateway_login_and_replays_everything_else_verbatim()
    {
        CaptureRun run = CaptureSessionReader.LongestRun(CaptureLocator.TryFind(GoodPostFix)!);
        ReplayScript script = ReplayScript.Build(run, TimeSpan.FromSeconds(3), maxMovementPackets: 40);

        Assert.Contains(script.Notes, n => n.Contains("rebuilt", StringComparison.Ordinal));
        Assert.DoesNotContain(script.GatewaySteps, s =>
            s.Signature.Kind == SignatureKind.GatewayControl && s.Signature.Opcode == 1);
        Assert.All(script.GatewaySteps, s => Assert.NotEqual(ReplayFidelity.Skipped, s.Fidelity));

        // The movement cap holds, and the rest of the stream is untouched by it.
        Assert.Equal(40, script.GatewaySteps.Count(s => s.Signature.Kind == SignatureKind.Movement));
        Assert.Contains(script.Notes, n => n.Contains("cap were not replayed", StringComparison.Ordinal));

        // The client's own login messages are replayed, fingerprint and all, and only the
        // character choice is rewritten.
        Assert.Equal(ReplayFidelity.Verbatim, script.LoginSteps[0].Fidelity);
        Assert.True(script.LoginSteps[0].Bytes.Length > 900, "the recorded LoginRequest carries the SystemFingerprint");
        Assert.Contains(script.LoginSteps, s => s.Fidelity == ReplayFidelity.Rewritten);
    }

    [CaptureFact]
    public void Anchors_are_the_causal_edges_the_recording_shows()
    {
        CaptureRun run = CaptureSessionReader.LongestRun(CaptureLocator.TryFind(GoodPostFix)!);
        ReplayScript script = ReplayScript.Build(run, TimeSpan.FromSeconds(3), maxMovementPackets: 0);

        ReplayStep[] anchored = [.. script.GatewaySteps.Where(s => s.Anchor is not null)];
        Assert.NotEmpty(anchored);

        // The client's ClientIsReady for the new zone follows the server's zoning burst; the
        // recording is what says which packet of it came last.
        ReplayStep? ready = script.GatewaySteps.FirstOrDefault(s =>
            s.Signature.Kind == SignatureKind.Zone && s.Signature.Opcode == ZoneOpcodes.ClientIsReady);
        Assert.NotNull(ready);

        output.WriteLine($"{anchored.Length} of {script.GatewaySteps.Count} step(s) anchored");
        foreach (ReplayStep step in anchored.Take(20))
        {
            output.WriteLine("  " + step);
        }
    }
}
