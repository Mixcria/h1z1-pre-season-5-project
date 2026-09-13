using System.Numerics;
using Cranberry.Harness.Protocol;
using Cranberry.Harness.Wire;

namespace Cranberry.Harness.Verification;

/// <summary>One <c>GameMode.Ring</c> (<c>ce 01 00</c>) — the gas circle the client actually draws.</summary>
public sealed record GasRing(Vector3 Centre, float Radius, uint BlendMs)
{
    public const int Length = 31;
}

/// <summary>One <c>GameMode.SafeZone</c> (<c>ce 02 00</c>) — the NEXT circle, i.e. a phase reveal.</summary>
public sealed record GasSafeZone(Vector3 Centre, float Radius)
{
    public const int Length = 23;
}

/// <summary>A decoded <c>ClientUpdate.ItemAdd</c> (<c>11 02 00</c>).</summary>
public sealed record ItemGrant(
    ulong TargetGuid,
    uint BlobLength,
    uint DefinitionId,
    ulong ItemGuid,
    uint Count,
    ulong ContainerGuid,
    uint ContainerDefinitionId,
    uint SlotId,
    int MessageLength)
{
    /// <summary>
    /// The wave-5 blob: the 62-byte base item record plus the one-byte Generic item-class tail
    /// (docs/13 §4b, <c>LootPackets.ItemAdd</c>). A Weapon-class row carrying the docs/58 §5 tail
    /// would be 62 + 68 = 130.
    /// </summary>
    public const uint Wave5BlobLength = 63;
}

/// <summary>
/// One row of the <c>94 01</c> <b>equipment-slot</b> list (<c>FUN_140a558a0</c>): the inventory
/// identity of what is bound to a body slot. Its two strings are the tint and decal aliases — NOT
/// a mesh (docs/45, <c>EquipmentSlotRow</c>). Slot 7 here is the docs/45 crash.
/// </summary>
public sealed record EquipmentSlotBinding(uint SlotId, ulong ItemGuid, string TintAlias, string DecalAlias);

/// <summary>
/// One element of the <c>94 01</c> <b>attachment</b> list (<c>FUN_140a2ed70</c>) — this is where a
/// mesh lives, and where docs/54 B1's empty <c>MODEL_NAME</c> would show as an empty string or as a
/// body slot with no element at all.
/// </summary>
public sealed record EquipmentMesh(
    uint SlotId,
    string ModelName,
    string TextureAlias,
    string TintAlias,
    uint ShaderParameterGroupId,
    int AppearanceIds);

/// <summary>Both lists of one <c>Equipment.SetCharacterEquipment</c> (<c>94 01</c>).</summary>
public sealed record EquipmentPacket(
    ulong CharacterGuid,
    IReadOnlyList<EquipmentSlotBinding> Slots,
    IReadOnlyList<EquipmentMesh> Attachments);

/// <summary>
/// Decoders for the packets the <b>run lane</b> has to look inside and Lane A did not. Written from
/// the layouts docs/12/13/15/18/40/42/45/58 record, in the harness's own reader, for the same reason
/// the transport is re-implemented: a decoder that shares code with the encoder cannot see a bug in
/// the shared part. Every one is total — a short or unexpected packet returns null rather than
/// throwing, so a probe reports the server and never the decoder.
/// </summary>
public static class VerificationPackets
{
    public const byte CommandBase = 0x09;
    public const byte CharacterBase = 0x0F;
    public const byte ClientUpdateBase = 0x11;
    public const byte ReferenceData = 0x17;
    public const byte RecipeBase = 0x26;
    public const byte MountBase = 0x70;
    public const byte PlayerUpdatePosition = 0x78;
    public const byte LoadoutsBase = 0x86;
    public const byte VehicleBase = 0x88;
    public const byte EquipmentBase = 0x94;
    public const byte ItemsBase = 0xAC;
    public const byte ContainerBase = 0xC8;
    public const byte GameModeHud = 0xCE;
    public const byte AddLightweightNpc = 0xD6;
    public const byte AddLightweightVehicle = 0xD7;
    public const byte LightweightToFullNpc = 0xDA;
    public const byte LightweightToFullVehicle = 0xDB;
    public const byte ReplicationBase = 0xEA;
    public const byte ProximateItemBase = 0xF8;

    public const byte UpdateStatSub = 0x40;
    public const byte DoorStateUpdateSub = 0x0A;
    public const byte RecipeListSub = 0x09;
    public const ushort ItemAddSub = 0x0002;
    public const ushort BaseSpeedSub = 0x0005;
    public const ushort GasRingSub = 0x0001;
    public const ushort GasSafeZoneSub = 0x0002;
    public const ushort StartMatchSub = 0x0016;
    public const ushort InitContainersSub = 0x0002;
    public const ushort UpdateContainerSub = 0x0006;
    public const ushort InteractionStringSub = 0x002D;

    /// <summary>
    /// <c>ce 01 00 | f32x4 centre | f32 radius | u32 blendMs | u32</c>, 31 bytes (docs/15 §2,
    /// docs/18 §1). This is the only circle the client draws, and the server re-sends it on its own
    /// tick with the interpolated radius — which is what makes the wall's speed measurable from the
    /// wire rather than read out of a settings file.
    /// </summary>
    public static GasRing? TryReadGasRing(ReadOnlySpan<byte> payload)
    {
        try
        {
            var r = new WireReader(payload);
            if (r.U8() != GameModeHud || r.LeU16() != GasRingSub || payload.Length < GasRing.Length)
            {
                return null;
            }

            float x = r.LeF32();
            float y = r.LeF32();
            float z = r.LeF32();
            _ = r.LeF32();
            float radius = r.LeF32();
            uint blend = r.LeU32();
            return new GasRing(new Vector3(x, y, z), radius, blend);
        }
        catch (WireFormatException)
        {
            return null;
        }
    }

    /// <summary><c>ce 02 00 | f32x4 centre | f32 radius</c>, 23 bytes — the phase reveal.</summary>
    public static GasSafeZone? TryReadGasSafeZone(ReadOnlySpan<byte> payload)
    {
        try
        {
            var r = new WireReader(payload);
            if (r.U8() != GameModeHud || r.LeU16() != GasSafeZoneSub || payload.Length < GasSafeZone.Length)
            {
                return null;
            }

            float x = r.LeF32();
            float y = r.LeF32();
            float z = r.LeF32();
            _ = r.LeF32();
            return new GasSafeZone(new Vector3(x, y, z), r.LeF32());
        }
        catch (WireFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>11 02 00 | u64 target | u32 blobLength | blob</c>, the blob being the 62-byte item record
    /// plus the item-class tail (docs/13 §4). <see cref="ItemGrant.BlobLength"/> is the whole
    /// weapon-stage question in one number.
    /// </summary>
    public static ItemGrant? TryReadItemAdd(ReadOnlySpan<byte> payload)
    {
        try
        {
            var r = new WireReader(payload);
            if (r.U8() != ClientUpdateBase || r.LeU16() != ItemAddSub)
            {
                return null;
            }

            ulong target = r.LeU64();
            uint blob = r.LeU32();
            uint definition = r.LeU32();
            _ = r.LeU32();                  // tint
            ulong itemGuid = r.LeU64();
            uint count = r.LeU32();
            if (r.Bool())
            {
                // A detail block would move every field after it. The run lane has never seen one
                // and would rather report an unknown shape than mis-read a container guid.
                return new ItemGrant(target, blob, definition, itemGuid, count, 0, 0, 0, payload.Length + 1);
            }

            ulong container = r.LeU64();
            uint containerDefinition = r.LeU32();
            uint slot = r.LeU32();
            return new ItemGrant(
                target, blob, definition, itemGuid, count, container, containerDefinition, slot,
                payload.Length + 1);
        }
        catch (WireFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Independently reads the native 11 03 00 update envelope and its unprefixed 62-byte item
    /// record. Unlike ItemAdd, this updates a known item and carries no class-specific tail.
    /// </summary>
    public static ItemGrant? TryReadItemUpdate(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 73) return null;
        try
        {
            var r = new WireReader(payload);
            if (r.U8() != ClientUpdateBase || r.LeU16() != 3) return null;
            ulong target = r.LeU64();
            uint definition = r.LeU32();
            _ = r.LeU32();
            ulong guid = r.LeU64();
            uint count = r.LeU32();
            if (r.Bool()) return null;
            ulong container = r.LeU64();
            uint containerDefinition = r.LeU32();
            uint slot = r.LeU32();
            return new ItemGrant(target, 0, definition, guid, count, container, containerDefinition,
                slot, payload.Length + 1);
        }
        catch (WireFormatException) { return null; }
    }

    /// <summary>
    /// <c>0f 40 | u64 guid | u32 count | count x 13-byte entry</c> (docs/40 §3). Only the count is
    /// decoded: the entry's stat ordinal comes from the client's own StringHashToValue table, and
    /// re-deriving it here would prove nothing the count does not.
    /// </summary>
    public static (ulong Guid, uint Entries)? TryReadStatBurst(ReadOnlySpan<byte> payload)
    {
        try
        {
            var r = new WireReader(payload);
            if (r.U8() != CharacterBase || r.U8() != UpdateStatSub)
            {
                return null;
            }

            ulong guid = r.LeU64();
            return (guid, r.LeU32());
        }
        catch (WireFormatException)
        {
            return null;
        }
    }

    /// <summary><c>0f 0a | u64 doorGuid | u64 state | u32 time</c> — the only packet that swings a door (docs/42 §5c).</summary>
    public static (ulong DoorGuid, bool Open)? TryReadDoorState(ReadOnlySpan<byte> payload)
    {
        try
        {
            var r = new WireReader(payload);
            if (r.U8() != CharacterBase || r.U8() != DoorStateUpdateSub)
            {
                return null;
            }

            ulong guid = r.LeU64();
            ulong state = r.LeU64();
            return (guid, (state & (1UL << 48)) != 0);
        }
        catch (WireFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Both lists of a <c>94 01</c>: the equipment-slot bindings and the attachment meshes.
    ///
    /// <para>Reading only the first list is how a probe gets docs/54 B1 wrong. The row list is the
    /// inventory identity (slot id, item guid, tint/decal aliases) and its strings are empty by
    /// design; the <b>mesh</b> is an element of the second list, keyed by the same slot id. "The
    /// item went into the slot but it does not show on me" is precisely a row in list one with no
    /// element in list two.</para>
    /// </summary>
    public static EquipmentPacket? TryReadEquipment(ReadOnlySpan<byte> payload)
    {
        try
        {
            var r = new WireReader(payload);
            if (r.U8() != EquipmentBase || r.U8() != 0x01)
            {
                return null;
            }

            _ = r.LeU32();              // profile id
            ulong character = r.LeU64();
            _ = r.LeU32();
            _ = r.CountedString();      // tint alias
            _ = r.CountedString();      // decal alias

            uint slotCount = r.LeU32();
            if (slotCount > 1024)
            {
                return null;
            }

            var slots = new List<EquipmentSlotBinding>((int)slotCount);
            for (uint i = 0; i < slotCount; i++)
            {
                uint key = r.LeU32();
                _ = r.LeU32();          // the same slot id again (row +0x20)
                ulong itemGuid = r.LeU64();
                string tint = r.CountedString();
                string decal = r.CountedString();
                slots.Add(new EquipmentSlotBinding(key, itemGuid, tint, decal));
            }

            uint meshCount = r.LeU32();
            if (meshCount > 1024)
            {
                return null;
            }

            var meshes = new List<EquipmentMesh>((int)meshCount);
            for (uint i = 0; i < meshCount; i++)
            {
                string model = r.CountedString();
                string texture = r.CountedString();
                string tint = r.CountedString();
                _ = r.CountedString();  // decal alias
                _ = r.LeU32();          // tint id
                _ = r.LeU32();          // composite effect
                _ = r.LeU32();          // effect
                uint slot = r.LeU32();
                uint shaderGroup = r.LeU32();
                uint appearances = r.LeU32();
                if (appearances > 4096)
                {
                    return null;
                }

                for (uint a = 0; a < appearances; a++)
                {
                    _ = r.LeU32();
                }

                _ = r.U8();             // flag
                meshes.Add(new EquipmentMesh(slot, model, texture, tint, shaderGroup, (int)appearances));
            }

            return new EquipmentPacket(character, slots, meshes);
        }
        catch (WireFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The door id a <c>0xd6</c> body carries at <c>+0x19c</c> — 24 bytes from the end of the body
    /// (docs/42 §7, <c>AddLightweightDoor.DoorIdOffset</c>). It is zero for every other world
    /// object, so a non-zero value is how the harness tells a door from a bandage without asking
    /// the server which is which.
    /// </summary>
    public static uint DoorIdOf(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 32 || payload[0] is not (AddLightweightNpc or AddLightweightVehicle))
        {
            return 0u;
        }

        return BitConverter.ToUInt32(payload.Slice(payload.Length - 24, 4));
    }

    /// <summary>
    /// The spawn record's flag byte at <c>+0x1b1</c> — 67 bytes back from the end of the body, the
    /// same measured-from-the-end trick <see cref="DoorIdOf"/> uses so a body change moves the offset
    /// instead of misplacing it.
    /// <para>
    /// docs/68 §2d: bit <c>0x20</c> is the ONLY wire-reachable switch that reaches the client's
    /// physics-body creator, and docs/68 §1 read <c>00</c> here on all 823 records of the owner's own
    /// capture. This reader is docs/68 F2 done inside the harness instead of by hand with
    /// <c>parse_doors.py</c>.
    /// </para>
    /// </summary>
    public static byte SpawnFlagsOf(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 80 || payload[0] is not (AddLightweightNpc or AddLightweightVehicle))
        {
            return 0;
        }

        return payload[^67];
    }

    /// <summary>
    /// The <c>ReferenceData</c> type name of a <c>0x17</c>, so a probe can say "no
    /// <c>WeaponDefinitions</c> table was sent" without re-deriving the head (docs/60).
    /// </summary>
    public static string? ReferenceDataName(ReadOnlySpan<byte> payload) =>
        ServerPackets.TryReadReferenceDataHead(payload, payload.Length + 1)?.TypeName;
}
