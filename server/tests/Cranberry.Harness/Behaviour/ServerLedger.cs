using System.Numerics;
using Cranberry.Harness.Protocol;
using Cranberry.Harness.Replay;

namespace Cranberry.Harness.Behaviour;

/// <summary>One tunnelled message the server sent, reduced to what an assertion needs.</summary>
public sealed record ServerRecord(TimeSpan At, byte Channel, byte Opcode, byte Sub8, ushort Sub16, int Length, string Name)
{
    /// <summary>
    /// The identity an ordering assertion compares on: the base opcode, plus a discriminator for
    /// the families where one opcode carries unrelated payloads (<c>ReferenceData</c> above all,
    /// where the type name is the whole meaning).
    /// </summary>
    public string Shape { get; init; } = string.Empty;
}

/// <summary>
/// Everything the server said, kept for the whole session rather than in the journal's bounded
/// ring, and reduced to the facts Lane A asserts on.
///
/// <para><b>Why this exists separately from the journal.</b> The journal is a tail for a failure
/// report: it forgets. Three of Lane A's assertions are about the <i>absence</i> of something
/// ("no <c>UnsetCharacterEquipmentSlot</c> ever arrives") or about a <i>whole-session</i> property
/// ("the loot that appeared after the walk is 200 m from the loot that appeared at the landing"),
/// and neither can be answered by a ring of the last 120 packets.</para>
///
/// <para>It is written only from server bytes, decoded by <see cref="ServerPackets"/>, which is
/// the harness's own reading of the layouts — not the server's writers.</para>
///
/// <para>Thread-safe: the gateway pump writes, the scenario thread reads.</para>
/// </summary>
public sealed class ServerLedger
{
    private readonly object _gate = new();
    private readonly List<ServerRecord> _zone = [];
    private readonly List<byte[]?> _payloads = [];
    private readonly List<LightweightEntity> _npcs = [];
    private readonly List<LightweightEntity> _vehicles = [];
    private readonly List<byte[]> _fullNpcBytes = [];
    private readonly List<ulong> _removed = [];
    private readonly List<EquipmentRows> _equipment = [];
    private readonly Dictionary<string, ReferenceDataHead> _referenceData = [];
    private readonly Dictionary<byte, int> _equipmentSubCounts = [];
    private byte[]? _weaponDefinitions;
    private int _itemAdds;
    private int _proximateItems;
    private string? _zoningZoneName;

    /// <summary>Every tunnelled zone message, in order.</summary>
    public IReadOnlyList<ServerRecord> Zone
    {
        get { lock (_gate) { return [.. _zone]; } }
    }

    /// <summary>Every ground object the server introduced with <c>AddLightweightNpc</c> (0xd6).</summary>
    public IReadOnlyList<LightweightEntity> Npcs
    {
        get { lock (_gate) { return [.. _npcs]; } }
    }

    /// <summary>Every <c>AddLightweightVehicle</c> (0xd7) — the parachute among them.</summary>
    public IReadOnlyList<LightweightEntity> Vehicles
    {
        get { lock (_gate) { return [.. _vehicles]; } }
    }

    /// <summary>How many <c>LightweightToFullNpc</c> (0xda) messages have arrived.</summary>
    public int FullNpcCount
    {
        get { lock (_gate) { return _fullNpcBytes.Count; } }
    }

    /// <summary>World objects the server removed with <c>Character.RemovePlayer</c> (0x0f/01).</summary>
    public IReadOnlyList<ulong> RemovedObjects
    {
        get { lock (_gate) { return [.. _removed]; } }
    }

    /// <summary>Every <c>SetCharacterEquipment</c> (0x94/01) and the slot rows it carried.</summary>
    public IReadOnlyList<EquipmentRows> Equipment
    {
        get { lock (_gate) { return [.. _equipment]; } }
    }

    /// <summary>
    /// The whole <c>ReferenceData "WeaponDefinitions"</c> message, from its base opcode byte, or
    /// null if none arrived. Kept in full despite <see cref="MaxRetainedPayload"/> — see the 0x17
    /// case of <see cref="Observe"/>.
    /// </summary>
    public byte[]? WeaponDefinitions
    {
        get { lock (_gate) { return _weaponDefinitions; } }
    }

    /// <summary>ReferenceData tables by type name, with the payload length each declared.</summary>
    public IReadOnlyDictionary<string, ReferenceDataHead> ReferenceData
    {
        get { lock (_gate) { return new Dictionary<string, ReferenceDataHead>(_referenceData); } }
    }

    /// <summary><c>ClientUpdate.ItemAdd</c> (0x11/0002) count — the grant half of a pickup.</summary>
    public int ItemAdds { get { lock (_gate) { return _itemAdds; } } }

    /// <summary><c>ProximateItemBase</c> (0xf8) count — the pickup panel republishing itself.</summary>
    public int ProximateItemLists { get { lock (_gate) { return _proximateItems; } } }

    /// <summary>The zone name the server named in <c>ClientBeginZoning</c>, if it has sent one.</summary>
    public string? ZoningZoneName { get { lock (_gate) { return _zoningZoneName; } } }

    /// <summary>
    /// How many <c>0x94</c> messages arrived with the given sub-opcode. docs/32 regression guard 2
    /// is <c>Count(0x03) == 0</c>: <c>UnsetCharacterEquipmentSlot</c> appears in 55 and 53 packets
    /// of the two sessions the client refused and in zero of the two it accepted.
    /// </summary>
    public int EquipmentSubCount(byte sub)
    {
        lock (_gate)
        {
            return _equipmentSubCounts.TryGetValue(sub, out int count) ? count : 0;
        }
    }

    /// <summary>How many tunnelled messages with this base opcode have arrived.</summary>
    public int Count(byte opcode)
    {
        lock (_gate)
        {
            return _zone.Count(r => r.Opcode == opcode);
        }
    }

    /// <summary>How many messages with this base opcode and 8-bit sub have arrived.</summary>
    public int Count(byte opcode, byte sub)
    {
        lock (_gate)
        {
            return _zone.Count(r => r.Opcode == opcode && r.Sub8 == sub);
        }
    }

    /// <summary>The index into <see cref="Zone"/> a scenario can later ask for "everything since".</summary>
    public int Mark()
    {
        lock (_gate)
        {
            return _zone.Count;
        }
    }

    /// <summary>Ground objects introduced after <paramref name="mark"/> was taken.</summary>
    public IReadOnlyList<LightweightEntity> NpcsSince(int mark)
    {
        lock (_gate)
        {
            // The npc list and the zone list grow together, so the count of 0xd6 records at the
            // mark is the offset into the npc list.
            int taken = _zone.Take(mark).Count(r => r.Opcode == 0xD6);
            return [.. _npcs.Skip(taken)];
        }
    }

    /// <summary>Messages recorded after <paramref name="mark"/>.</summary>
    public IReadOnlyList<ServerRecord> Since(int mark)
    {
        lock (_gate)
        {
            return [.. _zone.Skip(mark)];
        }
    }

    /// <summary>
    /// The <see cref="ServerRecord.Shape"/> sequence between the last <c>ClientBeginZoning</c>
    /// (0x0b) and the first <c>ZoneDoneSendingInitialData</c> (0x05) after it, with runs of the
    /// same shape collapsed to one entry. That is the match-zoning opcode order docs/32 asks to be
    /// held constant, expressed so a repeated packet count (six worn skins versus none) does not
    /// change it.
    /// </summary>
    public IReadOnlyList<string> MatchZoningOrder()
    {
        lock (_gate)
        {
            int begin = _zone.FindLastIndex(r => r.Opcode == 0x0B);
            if (begin < 0)
            {
                return [];
            }

            var shapes = new List<string>();
            for (int i = begin; i < _zone.Count; i++)
            {
                string shape = _zone[i].Shape;
                if (shapes.Count == 0 || shapes[^1] != shape)
                {
                    shapes.Add(shape);
                }

                if (_zone[i].Opcode == 0x05 && i > begin)
                {
                    break;
                }
            }

            return shapes;
        }
    }

    /// <summary>
    /// The largest message payload the ledger keeps a copy of. Everything the run lane decodes is
    /// far below this; the two families above it (<c>ReferenceData</c> and <c>SendZoneDetails</c>)
    /// are asserted on by length and type name, which the records already carry.
    /// </summary>
    public const int MaxRetainedPayload = 8192;

    /// <summary>
    /// Every retained payload for one base opcode, in order, each starting at the base opcode byte.
    /// Optionally narrowed by 8- or 16-bit sub-opcode, and to messages recorded after a mark.
    /// </summary>
    public IReadOnlyList<byte[]> Payloads(byte opcode, byte? sub8 = null, ushort? sub16 = null, int since = 0)
    {
        lock (_gate)
        {
            var found = new List<byte[]>();
            for (int i = Math.Max(0, since); i < _zone.Count; i++)
            {
                ServerRecord record = _zone[i];
                if (record.Opcode != opcode
                    || (sub8 is byte s8 && record.Sub8 != s8)
                    || (sub16 is ushort s16 && record.Sub16 != s16)
                    || i >= _payloads.Count
                    || _payloads[i] is not byte[] bytes)
                {
                    continue;
                }

                found.Add(bytes);
            }

            return found;
        }
    }

    /// <summary>When the first message with this opcode (and optional sub) arrived, if it has.</summary>
    public TimeSpan? FirstAt(byte opcode, ushort? sub16 = null)
    {
        lock (_gate)
        {
            foreach (ServerRecord record in _zone)
            {
                if (record.Opcode == opcode && (sub16 is not ushort s || record.Sub16 == s))
                {
                    return record.At;
                }
            }

            return null;
        }
    }

    /// <summary>Feeds one server message. Called from the gateway pump.</summary>
    public void Observe(ObservedPacket packet, TimeSpan at)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Kind != ObservedKind.ZoneTunnel || packet.ZoneOpcode is not byte opcode)
        {
            return;
        }

        ReadOnlySpan<byte> payload = packet.Payload.Span;
        byte sub8 = packet.SubOpcodeU8 ?? 0;
        ushort sub16 = packet.SubOpcodeU16 ?? 0;
        string shape = $"{opcode:x2}";

        lock (_gate)
        {
            switch (opcode)
            {
                case 0x0B:
                    _zoningZoneName = ServerPackets.TryReadBeginZoningZoneName(payload) ?? _zoningZoneName;
                    break;

                case 0x17:
                    if (ServerPackets.TryReadReferenceDataHead(payload, packet.Bytes.Length) is ReferenceDataHead head)
                    {
                        _referenceData[head.TypeName] = head;
                        shape = $"17:{head.TypeName}";

                        // The one ReferenceData table kept whole. It is 11.5 KB, well over
                        // MaxRetainedPayload, and it is also the only place the three bytes the
                        // wield fix writes exist (def+0x18, +0x58, +0x5c); S9 has to read them off
                        // the wire or it is asserting on the writer that produced them. One table,
                        // once per session, named explicitly rather than by raising the cap.
                        if (head.TypeName == WeaponDefinitionsReader.TypeName)
                        {
                            _weaponDefinitions = payload.ToArray();
                        }
                    }

                    break;

                case 0x94:
                    _equipmentSubCounts[sub8] = (_equipmentSubCounts.TryGetValue(sub8, out int n) ? n : 0) + 1;
                    if (ServerPackets.TryReadEquipmentRows(payload) is EquipmentRows rows)
                    {
                        _equipment.Add(rows);
                    }

                    shape = $"94/{sub8:x2}";
                    break;

                case 0xD6:
                    if (ServerPackets.TryReadLightweightEntity(payload) is LightweightEntity npc)
                    {
                        _npcs.Add(npc);
                    }

                    break;

                case 0xD7:
                    if (ServerPackets.TryReadLightweightEntity(payload) is LightweightEntity vehicle)
                    {
                        _vehicles.Add(vehicle);
                    }

                    break;

                case 0xDA:
                    _fullNpcBytes.Add(packet.Bytes);
                    break;

                case 0x0F when sub8 == 0x01:
                    if (ServerPackets.TryReadRemovePlayerGuid(payload) is ulong removed)
                    {
                        _removed.Add(removed);
                    }

                    shape = "0f/01";
                    break;

                case 0x11 when sub16 == 0x0002:
                    _itemAdds++;
                    shape = "11/0002";
                    break;

                case 0xF8:
                    _proximateItems++;
                    break;

                case 0xCE:
                    shape = $"ce/{sub16:x4}";
                    break;
            }

            _zone.Add(new ServerRecord(at, packet.Channel, opcode, sub8, sub16, packet.Bytes.Length, packet.Name)
            {
                Shape = shape,
            });

            // The payload (from the base opcode) of every message small enough to be worth keeping,
            // aligned index-for-index with _zone so a mark taken on one indexes the other. The run
            // lane asserts on fields INSIDE packets the build lane only counted - the gas ring's
            // radius, an ItemAdd's blob length, an equipment row's mesh name - and a decoder cannot
            // run on bytes nobody kept. ReferenceData (up to 1.2 MB) is deliberately excluded: its
            // head is already decoded into _referenceData above.
            _payloads.Add(payload.Length <= MaxRetainedPayload ? payload.ToArray() : null);
        }
    }

    /// <summary>
    /// How many <c>0xda</c> messages mention <paramref name="guid"/> as a little-endian u64. Used
    /// to prove a particular <c>0F 45</c> was answered rather than merely that some 0xda arrived.
    /// </summary>
    public int FullNpcMentions(ulong guid)
    {
        lock (_gate)
        {
            return _fullNpcBytes.Count(b => ServerPackets.Mentions(b, guid));
        }
    }

    /// <summary>Every distinct slot id the server has ever bound in a 0x94/01 row.</summary>
    public IReadOnlyCollection<uint> EquipmentSlotIdsEverSent()
    {
        lock (_gate)
        {
            return [.. _equipment.SelectMany(e => e.SlotIds).Distinct().Order()];
        }
    }

    /// <summary>The centroid of a set of world objects, for a distance assertion.</summary>
    public static Vector3 Centroid(IEnumerable<LightweightEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        Vector3 sum = Vector3.Zero;
        int count = 0;
        foreach (LightweightEntity entity in entities)
        {
            sum += entity.Position;
            count++;
        }

        return count == 0 ? Vector3.Zero : sum / count;
    }

    /// <summary>Horizontal distance, the measure the loot and door streamers themselves use.</summary>
    public static float HorizontalDistance(Vector3 a, Vector3 b) =>
        MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));
}
