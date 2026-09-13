using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Combat;
using Cranberry.Zone.Equipment;
using Cranberry.Zone.Inventory;
using Cranberry.Zone.Vehicles;
using Cranberry.Zone.Weapons;

namespace Cranberry.Tests.Zone;

/// <summary>
/// <b>docs/98 §5.5 — the starter weapon binds through <c>94 02</c>, never through a <c>94 01</c>
/// carrying a slot-7 row.</b>
///
/// <para>
/// Both harness sessions of 2026-09-02 were driven with <c>CRANBERRY_STARTER_WEAPON=1</c> and
/// <c>CRANBERRY_WIELD_FIRST_PICKUP=1</c>, and both carried a <c>94 01 SetCharacterEquipment</c>
/// whose row set was <c>[2, 3, 4, 5, 7, 28, 29]</c> — guard <b>G2</b>, Fatal, and the exact packet
/// shape that froze the owner on 2026-08-31. The grant put the rifle in the inventory's body slot 7
/// and the landing dress projected a row out of it; docs/95's rule is that a slot-7
/// <em>binding</em> belongs on <c>94 02 SetCharacterEquipmentSlot</c>, sent last, after the ability
/// manager.
/// </para>
///
/// <para>
/// This runs the real match flow — auto-match, zoning, the lobby countdown, StartMatch, the client's
/// own <c>88 19</c> AutoMount echo and its <c>88 18</c> Dismiss — so what is asserted is the landing
/// burst a live session actually produces, not a hand-built dress.
/// </para>
///
/// <para>Send-side only, per D29: this proves what the server SENT and nothing about what the
/// client did with it.</para>
/// </summary>
public sealed partial class StarterWeaponDrawTests
{
    /// <summary>Guard G2's slot: <c>RHand</c>, the active hand.</summary>
    private const uint RHand = 7;

    private sealed class RecordingRecorder : IPacketRecorder
    {
        public List<(string Direction, byte[] Bytes)> Messages { get; } = [];

        public void RecordSession(IPEndPoint remote, in SessionRequest request)
        {
        }

        public void RecordMessage(SoeConnection connection, string direction, ReadOnlySpan<byte> bytes) =>
            Messages.Add((direction, bytes.ToArray()));

        public void RecordRaw(SoeConnection connection, long keystreamPosition, ReadOnlySpan<byte> ciphertext)
        {
        }
    }

    private sealed class SilentLog : ITransportLog
    {
        public bool IsEnabled(TransportLogLevel level) => false;

        public void Log(TransportLogLevel level, string message)
        {
        }
    }

    /// <summary>
    /// <b>The G2 assertion, at the integration level.</b> A whole session from login to the
    /// parachute landing, with the starter weapon on and the draw sequence on: not one
    /// <c>94 01 SetCharacterEquipment</c> may carry an equipment-slot row for body slot 7, and the
    /// weapon must be bound by a <c>94 02 SetCharacterEquipmentSlot</c> instead.
    /// </summary>
    [Fact]
    public void TheStarterGrantNeverPutsASlotSevenRowOnTheWholeCharacterPacket()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder,
            ConcurrentQueue<Action> pending) = World();

        Land(service, connection, recorder, pending);

        byte[][] sent = Sent(recorder);

        // G2, verbatim: every 94 01 on the wire, and the row set each one carried.
        foreach (byte[] dress in sent.Where(p => IsEquipment(p, sub: 0x01)))
        {
            Assert.DoesNotContain(RHand, EquipmentSlotRowIds(dress));
        }

        // ...and the binding did happen, through the packet docs/95 says owns it.
        Assert.Contains(sent, p => IsEquipment(p, sub: 0x02));
    }

    /// <summary>
    /// The other half of the same rule: the draw is Z1's ordering, so the <c>94 02</c> is the
    /// <b>last</b> equipment packet of the landing burst. A <c>94 01</c> after it would re-state the
    /// character's whole slot list and undo the binding.
    /// </summary>
    [Fact]
    public void TheSlotSevenBindingIsTheLastEquipmentPacketOfTheLanding()
    {
        (ZoneService service, SoeConnection connection, RecordingRecorder recorder,
            ConcurrentQueue<Action> pending) = World();

        Land(service, connection, recorder, pending);

        byte[][] equipment =
            [.. Sent(recorder).Where(p => IsEquipment(p, 0x01) || IsEquipment(p, 0x02))];

        Assert.NotEmpty(equipment);
        Assert.True(IsEquipment(equipment[^1], 0x02),
            "the last equipment packet of the session is not the 94 02 binding");
    }

    [Fact]
    public void TheLobbyBindsFistsAfterTheirItemAndAbilityManagerWithoutHotbarInput()
    {
        var (service, connection, recorder, pending) = World(o => o with
        {
            GiveStarterWeapon = false,
            LobbyCountdownMs = 60_000,
        });
        try
        {
            SendClientIsReady(service, connection);
            PumpUntil(pending, () => Sent(recorder).Any(IsClientBeginZoning), "the zoning burst");
            SendClientIsReady(service, connection);
            var inventory = StateProperty<PlayerInventory>(connection, "Inventory");
            ulong fists = inventory.LoadoutSlots[SurvivorLoadout.Fists].Guid;
            Assert.DoesNotContain(Sent(recorder), p => IsHandBinding(p, fists));

            SendVehicle(service, connection, [ZoneOpcodes.ClientFinishedLoading]);
            byte[][] sent = Sent(recorder);
            int binding = Array.FindLastIndex(sent, p => IsHandBinding(p, fists));
            int manager = Array.FindLastIndex(sent, p => p.Length > 2 && p[1] == 0xa0 && p[2] == 0x05);
            Assert.True(manager >= 0 && binding > manager,
                "initial fists must bind after the bootstrap items and ability manager at world-ready");
            Assert.True(StateProperty<bool>(connection, "FistsBound"));
            Assert.DoesNotContain(sent, p => p.Length > 2 && p[1] == 0x86 && p[2] == 0x07);
        }
        finally
        {
            connection.Disconnect();
            service.OnDisconnected(connection, DisconnectCause.ServerRequested);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFullDressRestoresTheExistingGunOnlyWithDeliveredWeaponClearance(bool delivered)
    {
        var (service, connection, recorder, pending) = World(o => o with
        {
            Skins = o.Skins with { SuppressIdenticalDress = false },
        });
        try
        {
            Land(service, connection, recorder, pending);
            var inventory = StateProperty<PlayerInventory>(connection, "Inventory");
            var gun = inventory.Items[inventory.WieldedItemGuid];
            var combat = StateProperty<SessionCombat>(connection, "Combat");
            combat.Shooter.DeclareWeapon(gun.Guid, gun.DefinitionId, 5);
            var reload = new PendingWeaponReload(gun.Guid, gun.DefinitionId,
                AmmoTypes.AmmoItemFor(gun.DefinitionId), new PlayerAmmoContext(inventory, Self, AmmoOptions.Default),
                intervalMs: 3000, shellByShell: false, nowMs: Environment.TickCount64);
            combat.Reload = reload;
            if (!delivered) StateProperty<WeaponSession>(connection, "Weapons").Ledger.Forget(gun.Guid);
            // A changed in-world dress now uses slot deltas. Model an actual actor-baseline
            // invalidation here to keep testing the guarded full-dress recovery path.
            StateProperty<Cranberry.Zone.Appearance.AugustDressSuppressor>(connection, "Dress").Forget();
            int before = recorder.Messages.Count;

            typeof(ZoneService).GetMethod("SendCharacterAppearance", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [connection, connection.Tag, "skin click"]);

            byte[][] sent = [.. recorder.Messages.Skip(before).Where(m => m.Direction == "s2c").Select(m => m.Bytes)];
            Assert.Contains(sent, p => IsEquipment(p, 0x01));
            byte[][] bindings = [.. sent.Where(p => IsHandBinding(p, gun.Guid))];
            if (delivered)
            {
                Assert.Single(bindings);
                Assert.True(IsHandBinding(sent.Last(p => IsEquipmentAny(p)), gun.Guid));
            }
            else Assert.Empty(bindings);
            foreach (byte[] dress in sent.Where(p => IsEquipment(p, 0x01)))
                Assert.DoesNotContain(RHand, EquipmentSlotRowIds(dress));
            // Restoring an existing binding must not rebuild the item or reset its weapon state.
            Assert.DoesNotContain(sent, p => p.Length > 2 && p[1] == 0x11 && p[2] is 0x02 or 0x04);
            Assert.DoesNotContain(sent, p => p.Length > 2 && p[1] == 0xa0);
            Assert.Equal(5, combat.Shooter.AmmoOf(gun.Guid));
            Assert.Same(reload, combat.Reload);
            Assert.Equal(gun.Guid, inventory.WieldedItemGuid);
        }
        finally
        {
            connection.Disconnect();
            service.OnDisconnected(connection, DisconnectCause.ServerRequested);
        }
    }

    private static T StateProperty<T>(SoeConnection connection, string name) =>
        (T)connection.Tag!.GetType().GetProperty(name)!.GetValue(connection.Tag)!;

    private static bool IsHandBinding(byte[] packet, ulong itemGuid) =>
        packet.Length > 30 && IsEquipment(packet, 0x02)
        && BitConverter.ToUInt32(packet, 15) == RHand && BitConverter.ToUInt64(packet, 23) == itemGuid;

    /// <summary>
    /// docs/117 §B / DIAG-recipes-vehicles §B: a parked car reaches the client and is built
    /// ("Mountable npc ready") but never DRAWS until its <c>0xd7</c> record carries a transform and
    /// the collidable bit. The vehicles spawn on the parachute landing (<c>ArmGroundLoot</c>), so
    /// this drives a full session to the touchdown and inspects the nearest parked car — NOT the
    /// parachute, which is also a <c>0xd7</c> but carries the rider's own guid. With the retail fix
    /// on, the car carries an at-rest position block (flags <c>0x00da</c>) and the <c>+0x1b1</c>
    /// collidable flag; the revert reproduces the empty-block, walk-through car.
    /// </summary>
    [Fact]
    public void AParkedCarLandsWithAPositionBlockAndTheCollidableFlag()
    {
        static byte[] FirstParkedCar(bool retail)
        {
            var (service, connection, recorder, pending) = World(o => o with
            {
                SendVehicles = true,
                // A car is in range wherever the deterministic drop lands.
                VehicleRadius = 100_000f,
                VehiclePositionBlock = retail,
                VehicleSpawnFlags1 = retail ? LightweightEntityBody.CollidableFlag : (byte)0,
                VehicleShader = retail,
            });

            Land(service, connection, recorder, pending);
            PumpUntil(pending, () => Sent(recorder).Any(IsParkedCar), "a parked car");
            return Sent(recorder).First(IsParkedCar)[1..];   // drop the tunnel byte
        }

        // Inspect each car on its own terms — the drop is not identical across two fresh sessions,
        // so on and off need not be the same car. The record is [opcode][guid 8][transient varint]…;
        // the body is 200 + (varint len - 1), the +0x1b1 flag byte is 67 back from the body end, and
        // the tail position block starts 16 bytes past the body (owner guid 8 + two tail dwords 8).
        byte[] on = FirstParkedCar(retail: true);
        int onVarint = (on[9] & 3) + 1;
        int onBody = 200 + onVarint - 1;
        Assert.Equal(LightweightEntityBody.CollidableFlag, on[onBody - 67]);        // +0x1b1 collidable
        Assert.Equal(Convert.FromHexString("FA00"), on[(onBody + 16)..(onBody + 18)]); // at-rest block with authored yaw
        Assert.Equal(838u, VehicleShaderGroups.For(1));                            // OffRoader tint

        byte[] off = FirstParkedCar(retail: false);
        int offVarint = (off[9] & 3) + 1;
        int offBody = 200 + offVarint - 1;
        Assert.Equal(0, off[offBody - 67]);                                        // walk-through car
        Assert.Equal(Convert.FromHexString("0000"), off[(offBody + 16)..(offBody + 18)]); // empty block
    }

    /// <summary>A parked car is any <c>0xd7</c> whose guid is not the rider's parachute.</summary>
    private static bool IsParkedCar(byte[] packet) =>
        packet.Length > 10
        && packet[1] == AddLightweightVehicle.Opcode
        && BitConverter.ToUInt64(packet, 2) != Chute;

    // ---------------------------------------------------------------------------- the harness

    /// <summary>
    /// Login through to the landing burst: auto-match, the deferred zoning, the client's second
    /// <c>ClientIsReady</c> and world-ready <c>ClientFinishedLoading</c>, the lobby countdown's
    /// StartMatch, the <c>88 19</c> AutoMount echo that
    /// asks for the possession burst, and the <c>88 18</c> Dismiss that is the August client's own
    /// landing signal (docs/12 §5).
    /// </summary>
    private static void Land(
        ZoneService service,
        SoeConnection connection,
        RecordingRecorder recorder,
        ConcurrentQueue<Action> pending)
    {
        SendClientIsReady(service, connection);

        // The auto-match deferral, then EnterMatch's own zoning deferral.
        PumpUntil(pending, () => Sent(recorder).Any(IsClientBeginZoning), "the zoning burst");

        // The actor-ready reply precedes the client's separate proof that the lobby world is built.
        SendClientIsReady(service, connection);
        SendVehicle(service, connection, [ZoneOpcodes.ClientFinishedLoading]);
        PumpUntil(pending, () => Sent(recorder).Any(IsParachute), "the parachute");

        // The live client confirms the teleport before it can fly or land. Without this echo the
        // fixture stays in Dropping and never exercises InMatch hand restoration.
        SendVehicle(service, connection, [ZoneOpcodes.SynchronizedTeleportBase, (byte)SynchronizedTeleport.ClientReady, 0]);

        // "Mountable npc ready": the client echoes AutoMount for the chute it just auto-mounted.
        SendVehicle(service, connection, AutoMountEcho(Chute));
        // ...and lands. A null-guid Dismiss (88 18) is the signal a live landing under geometry sends.
        SendVehicle(service, connection, Dismiss);

        Assert.Contains(Sent(recorder), IsEquipmentAny);
    }

    /// <summary>The admitted character guid, and the chute's, which is it plus the option's offset.</summary>
    private const ulong Self = 0x1001;

    private static ulong Chute => Self + new ZoneOptions().ParachuteGuidOffset;

    private static (ZoneService Service, SoeConnection Connection, RecordingRecorder Recorder,
        ConcurrentQueue<Action> Pending) World(Func<ZoneOptions, ZoneOptions>? tune = null)
    {
        var pending = new ConcurrentQueue<Action>();
        var options = new ZoneOptions
        {
            // The live-run E0 configuration (docs/98 §4): the starter weapon on, the first weapon
            // wielded, and the draw sequence that is supposed to bind it.
            GiveStarterWeapon = true,
            Inventory = new InventoryOptions { WieldFirstWeapon = true, UseWieldSequence = true },

            // Drive the match with no PLAY click and no waiting.
            AutoMatchMs = 1,
            LobbyCountdownMs = 1,

            // Everything this test does not grade, off: no gas ladder, no doors, no parked cars and
            // no ground loot, so the only equipment packets on the wire are the ones it asserts on.
            EnableGas = false,
            SendDoors = false,
            SendVehicles = false,
            GroundLootRadius = 0f,
            DevGroundLootMs = 0,
        };

        if (tune is not null)
        {
            options = tune(options);
        }

        var tickets = new GatewayTicketRegistry();
        GatewayAdmission admission = tickets.Issue(
            Self, "Cranberry", gender: 2, headId: 3, hairId: 2, skinToneId: 664, profileId: 270);
        var recorder = new RecordingRecorder();
        var service = new ZoneService(new SilentLog(), recorder, tickets, options)
        {
            Post = pending.Enqueue,
        };

        var request = new SessionRequest(3, 0x11223344, 512, ZoneService.ProtocolName);
        var connection = new SoeConnection(
            new IPEndPoint(IPAddress.Loopback, 5555),
            in request,
            SessionSettings.WithSeed(1),
            service.OnSessionRequest(new IPEndPoint(IPAddress.Loopback, 5555), in request),
            service,
            new SilentLog(),
            (_, _) => { },
            now: 0);
        service.OnConnected(connection);

        using var writer = new PacketWriter();
        writer.WriteByte(GatewayLoginRequest.Opcode);
        writer.WriteUInt64(admission.Guid);
        writer.WriteString(admission.Ticket);
        writer.WriteString(GatewayLoginRequest.AugustProtocol);
        writer.WriteString(GatewayLoginRequest.AugustVersion);
        service.OnMessage(connection, writer.Written.ToArray());

        return (service, connection, recorder, pending);
    }

    private static void SendClientIsReady(ZoneService service, SoeConnection connection)
    {
        using var ready = new PacketWriter();
        ready.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        ready.WriteByte(ZoneOpcodes.ClientIsReady);
        service.OnMessage(connection, ready.Written.ToArray());
    }

    private static void SendVehicle(ZoneService service, SoeConnection connection, byte[] payload)
    {
        using var wrapped = new PacketWriter();
        wrapped.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        wrapped.WriteRaw(payload);
        service.OnMessage(connection, wrapped.Written.ToArray());
    }

    /// <summary><c>88 19</c> with the client's own flag byte 0 (ours is 1).</summary>
    private static byte[] AutoMountEcho(ulong chute)
    {
        using var w = new PacketWriter();
        w.WriteByte(ZoneOpcodes.VehicleBase);
        w.WriteByte(0x19);
        w.WriteUInt64(chute);
        w.WriteByte(0);
        w.WriteUInt32(0);
        return w.Written.ToArray();
    }

    /// <summary><c>88 18</c>, null guid — the live landing signal of docs/12.</summary>
    private static byte[] Dismiss
    {
        get
        {
            using var w = new PacketWriter();
            w.WriteByte(ZoneOpcodes.VehicleBase);
            w.WriteByte(VehicleDismiss.SubOpcode);
            w.WriteUInt64(0);
            return w.Written.ToArray();
        }
    }

    /// <summary>
    /// Run deferrals until <paramref name="done"/> or the budget is spent. The watchdog re-arms
    /// itself every second, so the queue never empties and a drain loop needs a stop condition
    /// rather than an empty check.
    /// </summary>
    private static void PumpUntil(ConcurrentQueue<Action> pending, Func<bool> done, string what)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 10_000 && !done())
        {
            if (!pending.TryDequeue(out Action? work))
            {
                Thread.Sleep(1);
                continue;
            }

            work!();
        }

        Assert.True(done(), $"{what} never happened");
    }

    private static byte[][] Sent(RecordingRecorder recorder) =>
        [.. recorder.Messages.ToArray().Where(m => m.Direction == "s2c").Select(m => m.Bytes)];

    private static bool IsClientBeginZoning(byte[] packet) =>
        packet.Length >= 2 && packet[1] == 0x0b;

    private static bool IsParachute(byte[] packet) =>
        packet.Length > 2 && packet[1] == ZoneOpcodes.VehicleBase && packet[2] == 0x19;

    private static bool IsEquipment(byte[] packet, byte sub) =>
        packet.Length > 2 && packet[1] == ZoneOpcodes.EquipmentBase && packet[2] == sub;

    private static bool IsEquipmentAny(byte[] packet) =>
        IsEquipment(packet, 0x01) || IsEquipment(packet, 0x02);

    /// <summary>
    /// The equipment-slot row ids of a <c>94 01 SetCharacterEquipment</c>, read back off the wire
    /// with this project's own writer rather than trusted from the caller's intent — the log-versus-
    /// wire divergence docs/45 warns about is exactly what this test exists to catch.
    /// </summary>
    private static List<uint> EquipmentSlotRowIds(byte[] packet)
    {
        var slots = new List<uint>();
        var reader = new PacketReader(packet.AsSpan(1));
        _ = reader.ReadByte();
        _ = reader.ReadByte();
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt64();
        _ = reader.ReadUInt32();
        _ = reader.ReadString();
        _ = reader.ReadString();
        int count = reader.ReadInt32();
        for (int row = 0; row < count; row++)
        {
            _ = reader.ReadUInt32();               // list hash key
            slots.Add(reader.ReadUInt32());        // row +0x20 slot id
            _ = reader.ReadUInt64();               // row +0x28 item guid
            _ = reader.ReadString();
            _ = reader.ReadString();
        }

        return slots;
    }
}
