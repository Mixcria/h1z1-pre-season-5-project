using Cranberry.Harness.Protocol;
using Cranberry.Harness.Runtime;
using Cranberry.Harness.Scenarios;

namespace Cranberry.Harness.Verification;

/// <summary>
/// <b>V4 — the developer console's server half, read off a live wire.</b>
/// <para>
/// This is the only test in the project that can see the console's packets leave a real host and
/// arrive at a real client socket. What it cannot do is type into the client's debug pane or see
/// what the pane draws - the harness is a modelled client, not the game - so the pane question
/// (design open question 1, docs/103 §8) stays the owner's, and every probe below says so where it
/// applies.
/// </para>
/// <para>
/// The three readings: <b>C1</b> the <c>Command.AddWorldCommand</c> burst leaves after
/// <c>ClientIsReady</c> and carries the names the registry holds; <b>C2</b> a typed
/// <c>Command.ExecuteCommand</c> is answered with a console line on the configured surface; <b>C3</b>
/// the HELP catch-all hash - what the client sends for a name it was never told about - is answered
/// with the help page rather than dropped.
/// </para>
/// </summary>
public static class ConsoleProbeScenario
{
    /// <summary>The journal must be able to hold a 50-packet burst plus the menu bootstrap.</summary>
    public const int RecommendedJournalCapacity = 400;

    /// <summary>Builds V4. Run it with <c>CRANBERRY_CONSOLE=1</c> (the default) on the host.</summary>
    public static Scenario V4ConsoleProbe(ProbeSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        int burstMark = 0;

        return Scenario.Named("V4 developer console - name burst, typed reply, HELP catch-all")
            .Then(LaneAScenarios.S2Menu())
            .Do("note how many AddWorldCommand names the menu bootstrap pushed", client =>
            {
                burstMark = Names(client).Count;
                client.Notes.Add($"console: {burstMark} AddWorldCommand name(s) after the menu ClientIsReady");
            })
            .Probe(sheet, "C1", "the AddWorldCommand burst leaves after ClientIsReady",
                "docs/103 §1.2, D178 - the burst rides the appearance-resync block synchronously; "
                + "without it the client forwards no typed name at all",
                client =>
                {
                    IReadOnlyList<string> names = Names(client);
                    if (names.Count == 0)
                    {
                        return ProbeOutcome.Fail(
                            "no 09 40 Command.AddWorldCommand arrived - either CRANBERRY_CONSOLE=0, "
                            + "or CRANBERRY_CONSOLE_REGISTER=zoning/off, or the hook did not match");
                    }

                    return names.Contains("m") && names.Contains("commands")
                        ? ProbeOutcome.Pass($"{names.Count} name(s), including m and commands")
                        : ProbeOutcome.Fail(
                            $"{names.Count} name(s) but not the two that matter: {string.Join(' ', names.Take(12))}");
                })
            .Do("type /where", client => Type(client, "where"))
            .Wait(TimeSpan.FromSeconds(1))
            .Probe(sheet, "C2", "a typed command is answered with one console line",
                "docs/103 §1.3 - which surface the PANE draws is the owner's click; what this "
                + "reading proves is that the server answered at all, and on which carrier",
                client =>
                {
                    IReadOnlyList<ConsoleLine> lines = ConsoleLines(client);
                    return lines.Count == 0
                        ? ProbeOutcome.Fail("the server answered nothing on 06 03, 06 05 or 11 31")
                        : ProbeOutcome.Pass(
                            $"{lines.Count} line(s): "
                            + string.Join(" | ", lines.Take(4).Select(l => $"[{l.Surface}] {l.Text}")));
                })
            .Do("send the HELP catch-all hash, which is what an unregistered name collapses to",
                client => TypeHash(client, Cranberry.Zone.DevConsole.CommandHash.Help))
            .Wait(TimeSpan.FromSeconds(1))
            .Probe(sheet, "C3", "the HELP catch-all answers the help page",
                "docs/103 §1.1 - the 1087 client turns every unknown /name into hash(HELP) with "
                + "empty arguments; whether the 1148 console input handler does the same is [U] and "
                + "this probe only shows that the SERVER answers that hash",
                client =>
                {
                    IReadOnlyList<ConsoleLine> lines = ConsoleLines(client);
                    return lines.Any(l => l.Text.Contains("Cranberry console --", StringComparison.Ordinal))
                        ? ProbeOutcome.Pass("the help page came back on the catch-all hash")
                        : ProbeOutcome.Fail(
                            $"no help page in {lines.Count} console line(s) - "
                            + string.Join(" | ", lines.TakeLast(4).Select(l => l.Text)));
                })
            .Probe(sheet, "C4", "the pane itself",
                "R2 §6 - the harness is a wire client and has no ConsoleWindow.gfx; only the owner "
                + "pressing F8 or Tilde can say which of the four surfaces the pane draws",
                _ => ProbeOutcome.NotTestable(
                    "run docs/103 §6 steps 2-4 and read the pane; /surface probe sends one labelled "
                    + "line on each of print, chat1, chat0 and alert"))
            .Read("console lines seen", client =>
                string.Join(" | ", ConsoleLines(client).TakeLast(6).Select(l => $"[{l.Surface}] {l.Text}")));
    }

    /// <summary>
    /// Every <c>AddWorldCommand</c> name the journal saw, in arrival order.
    /// <c>09 40 00 | u32 len | len x utf8</c>, decoded here rather than in
    /// <c>ServerPacketDecoders</c> because this is the only reader that wants it.
    /// </summary>
    private static IReadOnlyList<string> Names(HarnessClient client)
    {
        List<string> names = [];
        foreach (ReadOnlyMemory<byte> payload in ServerPayloads(client))
        {
            ReadOnlySpan<byte> span = payload.Span;
            if (span.Length < 7 || span[0] != 0x09 || span[1] != 0x40 || span[2] != 0x00)
            {
                continue;
            }

            uint length = BitConverter.ToUInt32(span.Slice(3, 4));
            if (length <= (uint)(span.Length - 7))
            {
                names.Add(System.Text.Encoding.UTF8.GetString(span.Slice(7, (int)length)));
            }
        }

        return names;
    }

    /// <summary>Every console line the journal saw, in arrival order.</summary>
    private static IReadOnlyList<ConsoleLine> ConsoleLines(HarnessClient client)
    {
        List<ConsoleLine> lines = [];
        foreach (ReadOnlyMemory<byte> payload in ServerPayloads(client))
        {
            if (ServerPackets.TryReadConsoleLine(payload.Span) is ConsoleLine line)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    /// <summary>
    /// The zone payload of every s2c gateway message still in the journal. The journal is a ring:
    /// a 50-name burst plus the menu bootstrap overflows the default 120 entries, which is why
    /// <see cref="RecommendedJournalCapacity"/> exists.
    /// </summary>
    private static IEnumerable<ReadOnlyMemory<byte>> ServerPayloads(HarnessClient client)
    {
        foreach (JournalEntry entry in client.Journal.Snapshot())
        {
            if (entry.Direction != PacketDirection.FromServer || entry.Bytes.Length == 0)
            {
                continue;
            }

            ObservedPacket observed = ObservedPacket.ParseGateway(entry.Bytes);
            if (observed.Channel == 0 && !observed.Payload.IsEmpty)
            {
                yield return observed.Payload;
            }
        }
    }

    private static void Type(HarnessClient client, string name, string arguments = "") =>
        Link(client).Send(ZoneClientMessages.ExecuteCommand(name, arguments), $"ExecuteCommand /{name}");

    private static void TypeHash(HarnessClient client, uint hash) =>
        Link(client).Send(ZoneClientMessages.ExecuteCommand(hash), $"ExecuteCommand 0x{hash:x8}");

    private static Soe.SoeClientSession Link(HarnessClient client) =>
        client.GatewayLink
        ?? throw new InvalidOperationException("The gateway link must be open before the console can be typed at.");
}
