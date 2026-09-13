using System.Buffers.Binary;
using Cranberry.Harness.Behaviour;
using Cranberry.Harness.Protocol;
using Cranberry.Harness.Scenarios;
using Xunit.Abstractions;

namespace Cranberry.Harness.Tests;

/// <summary>
/// <b>S11 — two clients on one host see each other</b> (docs/109 lane 3C).
///
/// <para>
/// This is the scenario the whole peer wire exists for and the one the harness could not express
/// before: every other lane-A scenario drives a single client, and the failure modes here — a peer
/// that never spawns, a peer that spawns and then never moves, a peer that stays on screen after
/// its player has gone — are all invisible with one. It is live-gated exactly like the rest:
/// <code>
/// set CRANBERRY_HARNESS_LIVE=1
/// dotnet test tests\Cranberry.Harness.Tests --filter TwoClientTests
/// </code>
/// </para>
///
/// <para>
/// <b>It needs a roster with two characters.</b> Both clients log in to the same host and pick a
/// different row by <c>HarnessOptions.CharacterIndex</c>; with one row on the roster they would
/// admit the same guid, the registry would treat the second as a reconnect and replace the first
/// (which is the correct behaviour, and is pinned by
/// <c>SessionRegistryTests.ReconnectingOnTheSameCharacterReplacesTheSessionRatherThanAddingASecond</c>).
/// The test says so rather than failing obscurely.
/// </para>
///
/// <para>
/// <b>What is asserted here and what is asserted offline.</b> Live: the enter burst arrives, the
/// relay carries the second client's own bytes unchanged at the expected rate, and the despawn
/// arrives when the second client goes. The <em>negative</em> — nothing at 500 m — is asserted in
/// <c>SessionRegistryTests.NothingIsSpawnedAtFiveHundredMetres</c> instead, because both harness
/// clients replay the same recorded channel-2 pool and therefore stand in the same place; making
/// them 500 m apart live would mean picking a capture window by hand and asserting on a distance
/// the harness deliberately does not decode (<c>MovementReplay</c>: "nothing in this repository
/// decodes them"). The registry test controls the position exactly and is the stronger check.
/// </para>
/// </summary>
public sealed class TwoClientTests(ITestOutputHelper output)
{
    private const byte AddLightweightPc = 0xd5;
    private const byte PlayerUpdatePosition = 0x78;
    private const byte EquipmentBase = 0x94;
    private const byte WeaponBase = 0x82;
    private const byte RemoteWeaponSub = 0x15;

    private HarnessOptions Options => new() { Log = output.WriteLine, JournalCapacity = 200 };

    [LiveFact]
    public async Task S11_a_second_client_is_spawned_dressed_armed_relayed_and_despawned()
    {
        await using var alice = new HarnessClient(Options with { CharacterIndex = 0 });
        await using var bob = new HarnessClient(Options with { CharacterIndex = 1 });

        // Alice lands first: the peer wire only runs for a session that is in a world, so both have
        // to reach the drop before either can be told about the other (PeerSession.InMatch).
        ScenarioResult first = await LaneAScenarios.S4Drop().RunAsync(alice);
        output.WriteLine(first.Report());
        first.ThrowIfFailed();

        ScenarioResult second = await LaneAScenarios.S4Drop().RunAsync(bob);
        output.WriteLine(second.Report());
        second.ThrowIfFailed();

        Assert.True(
            alice.SelfGuid != bob.SelfGuid,
            $"both harness clients admitted guid {alice.SelfGuid}: the host's roster needs a SECOND "
            + "character for this scenario, or the registry correctly treats the second login as a "
            + "reconnect and replaces the first.");

        // ---- the enter burst (docs/100 §1, docs/109 §3) ----------------------------------------

        byte[] spawn = await Eventually(
            () => alice.Ledger.Payloads(AddLightweightPc)
                .FirstOrDefault(p => p.Length > 9 && Guid(p, 1) == bob.SelfGuid),
            TimeSpan.FromSeconds(30),
            $"no d5 AddLightweightPc naming {bob.SelfGuid} reached the first client");

        uint transientId = ReadClientVarInt(spawn.AsSpan(9), out int varIntLength);
        output.WriteLine($"  reading | d5 for {bob.SelfGuid} is {spawn.Length} B, "
            + $"transient {transientId} ({varIntLength} varint byte(s))");

        // The viewer's own actor is transient 1 and 0 is the "no network id" sentinel; the table
        // reserves 0-15 and allocates from 16, so a peer can never collide with "me".
        Assert.True(transientId >= 16, $"peer transient id {transientId} is inside the reserved 0-15 range");

        Assert.Contains(
            alice.Ledger.Payloads(EquipmentBase, sub8: 0x01),
            dress => dress.Length > 14 && Guid(dress, 6) == bob.SelfGuid);

        Assert.Contains(
            alice.Ledger.Payloads(WeaponBase),
            arsenal => arsenal.Length > 7 && arsenal[5] == RemoteWeaponSub && arsenal[6] == 0x01);

        // ---- the relay (docs/100 §5) -----------------------------------------------------------

        // Every record the modelled client can send, so "byte-equal to the second client's own
        // records" is checkable without decoding a pose the harness deliberately does not decode.
        var pool = new List<string>();
        MovementReplay replay = MovementReplay.PlayerMovement();
        for (int i = 0; i < replay.Count; i++)
        {
            pool.Add(Convert.ToHexString(replay.Next()));
        }

        int before = alice.Ledger.Payloads(PlayerUpdatePosition).Count;
        TimeSpan startedAt = alice.Clock.Now;
        await Task.Delay(TimeSpan.FromSeconds(3));
        IReadOnlyList<byte[]> relayed = alice.Ledger.Payloads(PlayerUpdatePosition);
        double seconds = (alice.Clock.Now - startedAt).TotalSeconds;

        int arrived = relayed.Count - before;
        double hertz = arrived / seconds;
        output.WriteLine($"  reading | {arrived} relayed 0x78 frame(s) in {seconds:F2} s = {hertz:F1} Hz");

        Assert.True(arrived > 0, "not one 0x78 relay frame reached the first client");

        foreach (byte[] frame in relayed.Skip(before))
        {
            // 78 | varint transientId | the mover's own record, unchanged. The varint is the ONLY
            // thing this server writes; re-encoding the record would move the peer by up to a
            // centimetre per relay and drop any field the server does not yet parse.
            Assert.Equal(PlayerUpdatePosition, frame[0]);
            Assert.Equal(transientId, ReadClientVarInt(frame.AsSpan(1), out int idBytes));
            Assert.Contains(Convert.ToHexString(frame[(1 + idBytes)..]), pool);
        }

        // The client streams channel 2 every 24 ms (ClientTimings.PlayerMovementInterval, the
        // measured median) and PeerOptions.RelayStride is 2, so the relay is about 20 Hz of client
        // records halved. The band is deliberately wide: this asserts "a live stream at roughly the
        // right cadence", not a scheduler's accuracy.
        Assert.InRange(hertz, 3d, 30d);

        // ---- the despawn (docs/109 §3, the leave path) ------------------------------------------

        await bob.DisposeAsync();
        await Eventually(
            () => alice.Ledger.RemovedObjects.Contains(bob.SelfGuid) ? new byte[1] : null,
            TimeSpan.FromSeconds(30),
            $"no 0f 01 RemovePlayer for {bob.SelfGuid} reached the first client after the second left");

        output.WriteLine(alice.Journal.Tail());
    }

    private static ulong Guid(byte[] payload, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(offset));

    /// <summary>
    /// The client's compact unsigned integer, read the way <c>FUN_140a190f0</c> does: the low two
    /// bits of the first byte are the number of EXTRA little-endian bytes, and the value is the
    /// whole quantity shifted right by two. The harness reads it itself rather than calling the
    /// server's writer, so a relay assertion cannot pass because both sides share a bug.
    /// </summary>
    private static uint ReadClientVarInt(ReadOnlySpan<byte> bytes, out int length)
    {
        int extra = bytes[0] & 3;
        length = extra + 1;
        uint packed = 0;
        for (int i = 0; i <= extra; i++)
        {
            packed |= (uint)bytes[i] << (8 * i);
        }

        return packed >> 2;
    }

    private static async Task<byte[]> Eventually(
        Func<byte[]?> probe,
        TimeSpan budget,
        string failure)
    {
        DateTime deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is byte[] found)
            {
                return found;
            }

            await Task.Delay(100);
        }

        Assert.Fail(failure);
        return [];
    }
}
