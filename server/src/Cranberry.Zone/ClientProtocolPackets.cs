using Cranberry.Protocol;

namespace Cranberry.Zone;

/// <summary>
/// August environment update (0xc1). FUN_140f6e1a0 reads the named group map,
/// current property row and both group selectors, then rebuilds the active properties.
/// The client seeds Sku=2 only in INIT; selecting LIVE_KOTK during initialization
/// otherwise discards it, making the native Crown getter return zero despite a funded wallet.
/// DEFAULT is merged for every environment by FUN_1422f9330.
/// </summary>
public sealed record EnvironmentSettingsUpdate(string Environment = "LIVE_KOTK")
{
    public const byte Opcode = 0xc1;
    public const uint SkuPropertyHash = 0x72279f7f;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteInt32(1);          // named groups (FUN_140f0e7e0)
        writer.WriteString("DEFAULT"); // lookup key must be uppercase
        writer.WriteString("DEFAULT"); // group display name
        writer.WriteInt32(1);          // properties (FUN_140f0e360)
        writer.WriteUInt32(SkuPropertyHash);
        writer.WriteString("2");       // KOTK
        writer.WriteString(Environment);
        writer.WriteInt32(0);          // active properties rebuilt from the groups below
        writer.WriteString(Environment); // primary group
        writer.WriteString("");        // secondary group
    }
}

/// <summary>
/// ClientProtocol_1148 base packet 0x6e.  This is not cosmetic setup: the August handler
/// <c>FUN_140b019d0</c> calls <c>FUN_140ac4760(client, true)</c>, which rebuilds the resource-backed
/// managers and loads <c>ClientItemDefinitions.txt</c> from the client's own packs.  It must arrive
/// before reference data, the player record, and the wardrobe managers that resolve item ids.
/// </summary>
public sealed record InitializationParameters(
    string Environment = "LIVE_KOTK",
    string ProductTag = "")
{
    public const byte Opcode = ZoneOpcodes.InitializationParameters;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteString(Environment);
        writer.WriteString(ProductTag);
        writer.WriteInt32(0); // ruleset definitions
    }
}

/// <summary>
/// ClientProtocol_1148 base packet 0x60.  <c>FUN_140a3dfb0</c> consumes two dwords, one bool,
/// then six dwords/floats.  These values match the working 2017 KOTK menu flow while retaining
/// the August opcode and parser layout.
/// </summary>
public sealed record ClientGameSettings
{
    public const byte Opcode = ZoneOpcodes.ClientGameSettings;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt32(0);
        writer.WriteUInt32(16);      // interaction glow/distance setting
        writer.WriteBool(true);
        writer.WriteSingle(1f);      // time scale
        writer.WriteUInt32(1);       // weapons enabled
        writer.WriteUInt32(1);
        writer.WriteSingle(0f);
        writer.WriteSingle(15f);
        writer.WriteSingle(11f);     // damage multiplier setting
    }
}

/// <summary>
/// ClientProtocol_1148 base packet 0x16. The August parser consumes the fields below in this
/// exact order before recording the active zone name. Unknown/default fields are deliberately
/// kept at zero until their meanings can be established from the August binary or a capture.
/// </summary>
/// <param name="Values">
/// The StringHashToValue table carried in the trailing list. <c>FUN_140a3ea80</c> ends with
/// <c>FUN_140a4dc30(stream, map)</c>, which empties the client's StringHashToValueManager and refills
/// it from the list; an empty list therefore removes every default the client loaded from its own
/// <c>StringHashToValue.txt</c>, and the model loader then crashes on <c>Model.DescriptorReplaceString</c>
/// (<c>FUN_14220be70</c>, access violation at 0x1409b3d35). Pass <see cref="StringHashValues.Entries"/>.
/// </param>
public sealed record SendZoneDetails(
    string ZoneName,
    uint ZoneType = SendZoneDetails.HeightfieldLod,
    IReadOnlyList<StringHashValue>? Values = null,
    WeatherSettings? Weather = null,
    string LightingFile = SendZoneDetails.KotkLightingFile)
{
    public const byte Opcode = 0x16;

    /// <summary>
    /// The only lighting table shipped by the August client. The non-empty string makes the
    /// <c>SendZoneDetails</c> handler call <c>FUN_1424892c0</c>; leaving this field empty skips
    /// zone lighting entirely and produces the flat grey scene seen in the live click test.
    /// </summary>
    public const string KotkLightingFile = "Lighting_Z2.txt";

    /// <summary>
    /// Zone types named by <c>FUN_140b808d0</c>: 0 TileStatic, 1 TileSeamless, 2 RuntimeSeamless,
    /// 3 Mesh, 4 HeightfieldLod. <c>FUN_140b37510</c> registers a world implementation for type 4
    /// only; <c>FUN_1423c7610</c> fails PostInitialize for every other value.
    /// </summary>
    public const uint HeightfieldLod = 4;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteString(ZoneName);
        writer.WriteUInt32(ZoneType);
        writer.WriteBool(false);

        // FUN_140a482b0: the weather struct (21 floats, cloud texture, 12 floats); fog and
        // transition time must be non-zero or the sky blend produces NaN (WeatherSettings).
        (Weather ?? WeatherSettings.Kotk2017).WriteTo(writer);

        WriteZeroUInt32s(writer, 5);
        writer.WriteUInt64(0);
        writer.WriteBool(false);
        writer.WriteString(LightingFile);
        writer.WriteBool(false);
        writer.WriteBool(false);

        // FUN_140a3ea80 ends with FUN_140a4dc30(stream, StringHashToValueManager): i32 count, then
        // {i32 hash; str value; u8 flag; str name} records that replace the client's variable map.
        StringHashValue.WriteList(writer, Values);
    }

    private static void WriteZeroUInt32s(PacketWriter writer, int count)
    {
        for (int index = 0; index < count; index++)
        {
            writer.WriteUInt32(0);
        }
    }
}

/// <summary>
/// ClientProtocol_1148 base packet 3. The August client treats the player record as one
/// signed-i32-length byte block; non-negative values have the same wire representation as the
/// protocol writer's u32 counted bytes.
/// </summary>
public sealed record SendSelfToClient(byte[] PlayerData)
{
    public const byte Opcode = 3;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteCountedBytes(PlayerData);
    }

    /// <summary>
    /// Serialises a complete <see cref="SelfRecord"/>. The loader <c>FUN_140a31140</c> aborts the
    /// client (<c>FUN_1409e1080</c>, <c>0xbadbeef</c> to address zero) unless the record is consumed
    /// exactly, so a truncated prefix is never acceptable; every field is written.
    /// </summary>
    public static SendSelfToClient FromRecord(SelfRecord record) =>
        new(SelfRecordCodec.ToArray(record));
}

/// <summary>
/// ClientProtocol_1148 base packet 0x05, no body. The dispatcher case sets the client's
/// <c>InitialZoneDataComplete</c> flag (client+0x32122) that <c>WaitForConfirmationPacket</c>
/// polls, and logs <c>RECEIVED=ZoneDoneSendingInitialData</c>. <c>ClientBeginZoning</c> clears
/// the flag again, so this must follow any <c>0x0b</c>.
/// </summary>
public sealed record ZoneDoneSendingInitialData
{
    public const byte Opcode = ZoneOpcodes.ZoneDoneSendingInitialData;

    public void WriteTo(PacketWriter writer) => writer.WriteByte(Opcode);
}

/// <summary>
/// ClientUpdate family packet (base 0x11, u16 little-endian sub-opcode 0x0019
/// <c>cClientUpdatePacketIdDoneSendingPreloadCharacters</c>, u8 flag). The family handler
/// <c>FUN_140afc660</c> reads the sub-opcode as a u16 at offset 1; <c>FUN_140a609b0</c>
/// unserialises exactly one flag byte, and the case stores <c>ReceivedPreloadDonePacket</c>
/// (client+0x32123). A non-zero flag additionally calls <c>FUN_140ebff70</c> on the proxy manager.
/// </summary>
public sealed record DoneSendingPreloadCharacters(bool Flag = false)
{
    public const byte Family = ZoneOpcodes.ClientUpdateBase;
    public const ushort SubOpcode = 0x0019;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Family);
        writer.WriteUInt16(SubOpcode);
        writer.WriteBool(Flag);
    }
}

/// <summary>
/// ClientProtocol_1148 base packet 0x17 <c>ReferenceData</c>: dispatcher case 0x17 calls client
/// vtable+0x328 = <c>FUN_140b055c0(client, data, len)</c>, which reads a type name
/// (<c>FUN_140a12af0</c>: <c>u16 length|flags</c> — bit 15 = hash-only, bit 14 = a u32 index
/// follows — then the name, hashed client-side), a <c>u32 uncompressedLength</c> and an
/// <c>i32</c>-counted blob (<c>FUN_14220e860</c> copies it raw when the two lengths agree,
/// otherwise decompresses it), then dispatches on the name: <c>ItemClasses</c>,
/// <c>DynamicAppearanceDefinitions</c>, <c>ItemCategories</c>, <c>ProfileDefinitions</c>,
/// <c>ProjectileDefinitions</c>, <c>Facility</c>, <c>WeaponDefinitions</c>; anything else logs
/// <c>Received ReferenceData type=%s, but no handler!</c>.
/// </summary>
public sealed record ReferenceData(string TypeName, byte[] Payload)
{
    public const byte Opcode = ZoneOpcodes.ReferenceData;
    private byte[]? _compressedSource, _compressedPayload;
    private byte[] WirePayload => ReferenceEquals(Payload, _compressedSource) ? _compressedPayload! : Payload;
    internal int WirePayloadLength => WirePayload.Length;

    internal static ReferenceData CreateCompressed(string typeName, byte[] payload)
    {
        byte[] compressed = ReferenceDataCompression.Encode(payload);
        return compressed.Length < payload.Length
            ? new(typeName, payload) { _compressedSource = payload, _compressedPayload = compressed }
            : new(typeName, payload);
    }

    /// <summary>
    /// <c>ProfileDefinitions</c> with an empty table (<c>FUN_140a4d4c0</c>: <c>i32 count</c> then
    /// <c>{u32 id; fields}</c> entries). With the <c>PreLoadPcModels</c> option on (client+0x32338,
    /// default) the handler creates the model-preload tracker at client+0x32340 from the table;
    /// <c>WaitForConfirmationPacket</c> cannot set its physics flag (client+0x31637) until that
    /// tracker exists and reports done — an empty table is done at once.
    /// </summary>
    public static ReferenceData EmptyProfileDefinitions { get; } = new("ProfileDefinitions", [0, 0, 0, 0]);

    public void WriteTo(PacketWriter writer)
    {
        if (TypeName.Length > 0x1fff)
        {
            throw new ArgumentException("ReferenceData type names carry a 13-bit length.");
        }

        // FUN_140a12af0: bits 0-12 = length; bit 13 becomes bit 31 of the hash word and is the
        // flag handed to the hash function (FUN_140981390). The handler builds its comparison
        // values with that flag set (FUN_140981390(name, -1, 1, …)), and only equal flag bits take
        // the direct hash comparison — without 0x2000 the client answered "no handler!" (run 21:18).
        // The name is interned through FUN_140980620, which compares it as a NUL-terminated C
        // string straight out of the packet buffer, and FUN_140a12af0 advances the cursor by
        // length + 1: the name is followed by a NUL byte that the 13-bit length does not count.
        writer.WriteByte(Opcode);
        writer.WriteUInt16((ushort)(0x2000 | TypeName.Length));
        writer.WriteRaw(System.Text.Encoding.ASCII.GetBytes(TypeName));
        writer.WriteByte(0);
        writer.WriteUInt32((uint)Payload.Length);
        // Keep the original table accessible to callers. A record copied with a
        // replacement Payload must not reuse the old table's compressed bytes.
        writer.WriteCountedBytes(WirePayload);
    }
}

/// <summary>
/// ClientUpdate family packet (base 0x11, u16 sub-opcode 0x0034
/// <c>cClientUpdatePacketIdNetworkProximityUpdatesComplete</c>, no body — <c>FUN_140a60d00</c> reads
/// only the family byte and the u16). The case in <c>FUN_140afc660</c> calls <c>FUN_140ebff70</c> on
/// the proxy manager and stores the current time into <c>client+0x32128</c>, the value
/// <c>WaitForConfirmationPacket</c> (<c>FUN_140b8dc30</c>) prints as <c>NetworkProximityUpdateComplete</c>
/// and requires to differ from the sentinel <c>DAT_143f67e68</c> that the WaitForFirstZone entry
/// stores — so this must arrive after the client has entered WaitForFirstZone.
/// </summary>
public sealed record NetworkProximityUpdatesComplete
{
    public const byte Family = ZoneOpcodes.ClientUpdateBase;
    public const ushort SubOpcode = 0x0034;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Family);
        writer.WriteUInt16(SubOpcode);
    }
}

/// <summary>
/// ClientProtocol_1148 base packet 0x0b. Unserializer <c>FUN_140a3dae0</c> (docs/11, corrected
/// 2026-08-28): <c>u8 0x0b; str zoneName; u32 zoneType; f32×4 position; f32×4 rotation; weather
/// struct (FUN_140a482b0); u8; u32 a, b, c, d, e, f; u64; u8; u8; u8</c>. The handler
/// <c>FUN_140afc260</c> stores the name and type, logs <c>RECEIVED=Begin Zoning ZONE=%s
/// LOCATION=x, y, z</c> (position[0..2]), clears InitialZoneDataComplete (client+0x32122), copies
/// a→+0x32898, c,d→+0x32270/74, e→+0x3229c, f→+0x322a0, u64→DAT_143f6a100+0x340,
/// bool2→+0x32130 (must stay 0 — WaitForConfirmationPacket requires it clear), bool3→+0x326ee,
/// then calls <c>FUN_140b90470(client, name, e, type, &amp;pos, &amp;rot, u8, c, sameZone)</c>. The tail
/// values are the ones the 2016 server sent (owner's capture lead): 05, 5, 0, 2, 0, 0, 0.
/// </summary>
public sealed record ClientBeginZoning(
    string ZoneName,
    System.Numerics.Vector4 Position,
    System.Numerics.Vector4 Rotation,
    uint ZoneType = SendZoneDetails.HeightfieldLod,
    WeatherSettings? Weather = null)
{
    public const byte Opcode = ZoneOpcodes.ClientBeginZoning;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteString(ZoneName);
        writer.WriteUInt32(ZoneType);
        writer.WriteSingle(Position.X);
        writer.WriteSingle(Position.Y);
        writer.WriteSingle(Position.Z);
        writer.WriteSingle(Position.W);
        writer.WriteSingle(Rotation.X);
        writer.WriteSingle(Rotation.Y);
        writer.WriteSingle(Rotation.Z);
        writer.WriteSingle(Rotation.W);
        (Weather ?? WeatherSettings.Kotk2017).WriteTo(writer);
        writer.WriteByte(5);
        writer.WriteUInt32(5);      // a → client+0x32898
        writer.WriteUInt32(0);      // b → FUN_140b90470
        writer.WriteUInt32(2);      // c → client+0x32270
        writer.WriteUInt32(0);      // d → client+0x32274
        writer.WriteUInt32(0);      // e → client+0x3229c
        writer.WriteUInt32(0);      // f → client+0x322a0
        writer.WriteUInt64(0);
        writer.WriteBool(false);
        writer.WriteBool(false);    // client+0x32130 must stay clear
        writer.WriteBool(false);
    }
}

/// <summary>
/// ClientProtocol_1148 base packet 0x1d, both directions: <c>u64 time; u32 value; u8 flag</c>.
/// The client sends its own clock when it enters <c>WaitForFirstZone</c>; the handler
/// <c>FUN_140ff2250</c> (parser <c>FUN_140ff1b10</c>) applies the server's time through the game
/// clock (vtable+0x70), stores the u32 at clock+0x94 and toggles a bool when the flag differs.
/// </summary>
public sealed record GameTimeSync(ulong Time, uint Value = 0, bool Flag = false)
{
    public const byte Opcode = ZoneOpcodes.GameTimeSync;

    public static GameTimeSync Parse(ReadOnlySpan<byte> packet)
    {
        var reader = new PacketReader(packet);
        if (reader.ReadByte() != Opcode)
        {
            throw new PacketFormatException("Expected GameTimeSync opcode 0x1d.");
        }

        var sync = new GameTimeSync(reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadBool());
        if (!reader.AtEnd)
        {
            throw new PacketFormatException($"GameTimeSync has {reader.Remaining} trailing byte(s).");
        }

        return sync;
    }

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        writer.WriteUInt64(Time);
        writer.WriteUInt32(Value);
        writer.WriteBool(Flag);
    }
}

/// <summary>
/// ClientProtocol_1148 base packet 0xca: the weather struct <c>FUN_140a482b0</c> also embedded in
/// <c>SendZoneDetails</c> — 21 u32, one string, 12 u32 — followed by <c>FUN_140b88950</c>, which
/// sets <c>WeatherDataSynced</c> (client+0x31634) polled by <c>WaitForFirstZone</c>.
/// </summary>
public sealed record UpdateWeatherData(WeatherSettings? Weather = null)
{
    public const byte Opcode = ZoneOpcodes.UpdateWeatherData;

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(Opcode);
        (Weather ?? WeatherSettings.Kotk2017).WriteTo(writer);
    }
}
