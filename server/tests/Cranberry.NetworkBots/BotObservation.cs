using System.Buffers.Binary;
using System.Numerics;
using Cranberry.Harness.Protocol;
using Cranberry.Harness.Verification;
using Cranberry.Harness;

namespace Cranberry.NetworkBots;

internal sealed class BotObservation
{
    public readonly object Gate = new();
    public readonly Dictionary<ulong, uint> Peers = [];
    public readonly Dictionary<ulong, LightweightEntity> Loot = [];
    public readonly Dictionary<ulong, LightweightEntity> Canopies = [];
    public readonly Dictionary<ulong, ulong> MountedRiders = [];
    public ulong OwnChuteGuid;
    public readonly Dictionary<ulong, ItemGrant> Inventory = [];
    public readonly Dictionary<uint, long> GrantedUnitsByDefinition = [];
    public readonly Dictionary<ulong, ulong> RemoteHandItems = [];
    public readonly Dictionary<ulong, uint> WorldItemDefinitions = [];
    public Func<ulong> SelfGuid = () => 0;
    public readonly HashSet<ulong> Removed = [];
    public readonly Dictionary<uint, long> PeerPoses = [];
    public readonly Dictionary<uint, long> PeerFireStarts = [];
    public readonly HashSet<ulong> Deaths = [];
    public readonly long[] PoseAgeMs = new long[2001];
    private readonly long[] _windowAge = new long[10001];
    private readonly Dictionary<uint, long> _windowPoses = [];
    private readonly Dictionary<uint, uint> _windowLatest = [];
    private readonly Dictionary<uint, uint> _latestPeerTick = [];
    public readonly HashSet<uint> WindowJumpPeers = [];
    private uint _windowStart;
    private long _windowOlder;
    private int _windowMax;
    private long _windowOutOfOrder;
    public readonly Dictionary<byte, long> Opcodes = [];
    public uint? ChuteTransient;
    public Vector3? ChuteSpawn;
    public uint Health = 10_000;
    public int HealthChanges, ItemAdds, RemoteWeapons, RemoteFireStarts, Reloads, Mounts;
    public int IncomingHits;
    public uint? FirstAmmoTick;
    public long Messages, Bytes, Poses, InvalidPoses;
    public int Results, VictoryScreens, CompletedLogouts, StartMatches;
    public uint LastResultTick, LastLobbyTick, LastLogoutTick;
    public string? ReturnTicket;
    public Func<uint> Tick = () => 0;

    private void ObserveOwnedItem(ItemGrant item)
    {
        uint before = Inventory.TryGetValue(item.ItemGuid, out var held) ? held.Count : 0;
        Inventory[item.ItemGuid] = item;
        if (item.Count > before)
            GrantedUnitsByDefinition[item.DefinitionId] = GrantedUnitsByDefinition.GetValueOrDefault(item.DefinitionId)
                + (item.Count - before);
    }

    public void Observe(ObservedPacket packet, TimeSpan at)
    {
        if (packet.Kind != ObservedKind.ZoneTunnel) return;
        ReadOnlySpan<byte> p = packet.Payload.Span;
        if (p.IsEmpty) return;
        lock (Gate)
        {
            Messages++; Bytes += packet.Bytes.Length;
            if (p.Length == 33 && p[0] == 0x11 && p[1] == 0x1e && p[2] == 0) IncomingHits++;
            Opcodes[p[0]] = Opcodes.GetValueOrDefault(p[0]) + 1;
            if (p.Length >= 3 && p[0] == 0xce && p[2] == 0)
            {
                if (p[1] == 4) { Results++; LastResultTick = Tick(); }
                if (p[1] == 0x18) VictoryScreens++;
                if (p[1] == 0x16) StartMatches++;
                if (p[1] == 0x15 && p.Length == 4 && p[3] == 1) LastLobbyTick = Tick();
            }
            if (p.Length == 3 && p[0] == 0x11 && p[1] == 0x30 && p[2] == 0)
            {
                CompletedLogouts++; LastLogoutTick = Tick();
                // The native world has exited on this reply. A dead bot can receive it
                // during a long slow-peer check without taking the explicit reconnect path.
                // Keep session handoff/history while retiring that world's observer cache.
                ResetMatchView(preserveReturnTicket: true);
            }
            if (p.Length >= 6 && p[0] == 0xc4 && p[1] == 1
                && BinaryPrimitives.ReadUInt32LittleEndian(p[2..]) == p.Length - 6)
                ReturnTicket = System.Text.Encoding.UTF8.GetString(p[6..]);
            if (p[0] == 0x94 && p.Length >= 38 && p[1] == 2
                && BinaryPrimitives.ReadUInt32LittleEndian(p[14..]) == 7
                && BinaryPrimitives.ReadUInt32LittleEndian(p[18..]) == 7
                && System.Text.Encoding.UTF8.GetString(p).Contains("Weapon_", StringComparison.Ordinal))
                RemoteHandItems[BinaryPrimitives.ReadUInt64LittleEndian(p[6..])] = BinaryPrimitives.ReadUInt64LittleEndian(p[22..]);
            if (p[0] == 0xD5 && p.Length > 12)
                Peers[BinaryPrimitives.ReadUInt64LittleEndian(p[1..])] = BotWire.VarInt(p[9..], out _);
            if (p[0] is 0xD6 or 0xD7 && ServerPackets.TryReadLightweightEntity(p) is { } entity)
            {
                // Doors share the NPC spawn opcode. The native door-id field,
                // already decoded by the harness, distinguishes them from pickups.
                if (p[0] == 0xD6 && VerificationPackets.DoorIdOf(p) == 0) Loot[entity.Guid] = entity;
                else if (entity.ModelId == 9374) Canopies[entity.Guid] = entity;
            }
            if (p[0] == 0x88 && p.Length >= 10 && p[1] == 0x19)
            {
                OwnChuteGuid = BinaryPrimitives.ReadUInt64LittleEndian(p[2..]);
                if (Canopies.TryGetValue(OwnChuteGuid, out var own))
                { ChuteTransient = own.TransientId; ChuteSpawn = own.Position; }
            }
            if (p[0] == 0x70 && p.Length >= 18)
            {
                ulong rider = BinaryPrimitives.ReadUInt64LittleEndian(p[2..]);
                if (p[1] == 2) MountedRiders[rider] = BinaryPrimitives.ReadUInt64LittleEndian(p[10..]);
                if (p[1] == 4) MountedRiders.Remove(rider);
                if (rider == SelfGuid()) Mounts++;
            }
            if (p[0] == 0x0F && p.Length >= 10 && p[1] == 1)
            {
                ulong guid = BinaryPrimitives.ReadUInt64LittleEndian(p[2..]);
                Removed.Add(guid); Loot.Remove(guid); Peers.Remove(guid); Canopies.Remove(guid);
            }
            if (p[0] == 0x0F && p.Length >= 10 && p[1] == 0x4F)
                Deaths.Add(BinaryPrimitives.ReadUInt64LittleEndian(p[2..]));
            if (p[0] == 0x78 && p.Length > 8)
            {
                uint id = BotWire.VarInt(p[1..], out int length);
                var move = p[(1 + length)..];
                if (move.Length >= 7)
                {
                    uint tick = BinaryPrimitives.ReadUInt32LittleEndian(move[2..]);
                    long age = unchecked((int)(Tick() - tick));
                    if (age is >= 0 and <= 120000)
                    {
                        PoseAgeMs[Math.Min(2000, age)]++;
                        PeerPoses[id] = PeerPoses.GetValueOrDefault(id) + 1; Poses++;
                        if (_latestPeerTick.TryGetValue(id, out uint preceding) && unchecked((int)(tick - preceding)) < 0)
                            _windowOutOfOrder++;
                        _latestPeerTick[id] = tick;
                        if (unchecked((int)(tick - _windowStart)) >= 0)
                        {
                            _windowAge[Math.Min(10000, age)]++;
                            _windowMax = Math.Max(_windowMax, (int)age);
                            _windowPoses[id] = _windowPoses.GetValueOrDefault(id) + 1;
                            _windowLatest[id] = tick;
                            if (move.Length >= 8 && (BinaryPrimitives.ReadUInt16LittleEndian(move) & 1) != 0
                                && (BotWire.VarInt(move[7..], out _) & 0x420) == 0x20)
                                WindowJumpPeers.Add(id);
                        }
                        else _windowOlder++;
                    }
                    else InvalidPoses++;
                }
            }
            if (p[0] == 0x11 && p.Length >= 7 && p[1] == 1 && p[2] == 0)
            {
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(p[3..]);
                if (value != Health) HealthChanges++;
                Health = value;
            }
            if (p[0] == 0x11 && VerificationPackets.TryReadItemAdd(p) is { } item)
            {
                if (item.TargetGuid == SelfGuid())
                {
                    ObserveOwnedItem(item); ItemAdds++;
                    if (item.DefinitionId == 1429) FirstAmmoTick ??= Tick();
                }
                else WorldItemDefinitions[item.TargetGuid] = item.DefinitionId;
            }
            if (p[0] == 0x11 && VerificationPackets.TryReadItemUpdate(p) is { } changed
                && changed.TargetGuid == SelfGuid()
                && Inventory.TryGetValue(changed.ItemGuid, out var held)
                && held.DefinitionId == changed.DefinitionId)
                ObserveOwnedItem(changed);
            if (p[0] == 0x82 && p.Length > 5)
            {
                if (p[5] == 0x15) RemoteWeapons++;
                if (p.Length > 18 && p[5] == 0x15 && p[6] == 4)
                {
                    uint owner = BotWire.VarInt(p[7..], out int idLength);
                    if (p.Length > 16 + idLength && p[7 + idLength] == 1 && (p[16 + idLength] & 1) == 0)
                    {
                        RemoteFireStarts++;
                        PeerFireStarts[owner] = PeerFireStarts.GetValueOrDefault(owner) + 1;
                    }
                }
                if (p[5] is 0x08 or 0x09) Reloads++;
            }
        }
    }

    // Clear the simulated client's world view only after it has received the logout reply.
    // Connection counters and result history remain, so replay checks cannot pass on old data.
    public void ResetMatchView(bool preserveReturnTicket = false)
    {
        lock (Gate)
        {
            Peers.Clear(); Loot.Clear(); Canopies.Clear(); MountedRiders.Clear();
            Inventory.Clear(); RemoteHandItems.Clear(); WorldItemDefinitions.Clear();
            PeerPoses.Clear(); PeerFireStarts.Clear(); Removed.Clear(); Deaths.Clear();
            _latestPeerTick.Clear(); _windowPoses.Clear(); _windowLatest.Clear(); WindowJumpPeers.Clear();
            OwnChuteGuid = 0; ChuteTransient = null; ChuteSpawn = null;
            Health = 0; HealthChanges = 0; FirstAmmoTick = null;
            if (!preserveReturnTicket) ReturnTicket = null;
            BeginMovementWindow(Tick());
        }
    }

    public double Percentile(double q)
    {
        lock (Gate)
        {
            long count = PoseAgeMs.Sum();
            if (count == 0) return -1;
            long target = (long)Math.Ceiling(count * q), sum = 0;
            for (int i = 0; i < PoseAgeMs.Length; i++) if ((sum += PoseAgeMs[i]) >= target) return i;
            return 2000;
        }
    }

    public void BeginMovementWindow(uint start)
    {
        lock (Gate)
        {
            _windowStart = start; _windowOlder = 0;
            _windowMax = 0; _windowOutOfOrder = 0;
            Array.Clear(_windowAge); _windowPoses.Clear(); _windowLatest.Clear();
            WindowJumpPeers.Clear();
        }
    }

    public MovementWindowResult MovementWindow(uint end, bool airborne)
    {
        lock (Gate)
        {
            uint[] expected = airborne
                ? MountedRiders.Where(r => r.Key != SelfGuid() && Canopies.ContainsKey(r.Value))
                    .Select(r => Canopies[r.Value].TransientId).ToArray()
                : Peers.Values.ToArray();
            double seconds = Math.Max(.001, unchecked(end - _windowStart) / 1000d);
            long samples = _windowAge.Sum();
            int Quantile(double q)
            {
                long target = (long)Math.Ceiling(samples * q), sum = 0;
                if (samples == 0) return -1;
                for (int i = 0; i < _windowAge.Length; i++) if ((sum += _windowAge[i]) >= target) return i;
                return 10000;
            }
            return new(seconds, samples, _windowOlder, Quantile(.95), Quantile(.99),
                expected.Select(id => _windowPoses.GetValueOrDefault(id) / seconds).DefaultIfEmpty(0).Min(),
                expected.Count(id => !_windowLatest.ContainsKey(id)),
                expected.Select(id => _latestPeerTick.TryGetValue(id, out uint tick)
                    ? Math.Max(0, unchecked((int)(end - tick))) : 120000).DefaultIfEmpty(120000).Max())
            { P50Ms = Quantile(.50), MaxMs = _windowMax, OutOfOrderTimestamps = _windowOutOfOrder };
        }
    }
}

internal sealed record MovementWindowResult(double Seconds, long Samples, long OlderRecords, int P95Ms,
    int P99Ms, double MinimumPairHz, int MissingPeers, int StalestPeerMs)
{
    public int P50Ms { get; init; }
    public int MaxMs { get; init; }
    public long OutOfOrderTimestamps { get; init; }
}
