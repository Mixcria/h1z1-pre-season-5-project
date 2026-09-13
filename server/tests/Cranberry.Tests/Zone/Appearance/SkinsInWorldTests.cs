using System.Collections.Concurrent;
using System.Net;
using Cranberry.Login;
using Cranberry.Protocol;
using Cranberry.Transport;
using Cranberry.Zone;
using Cranberry.Zone.Appearance;

namespace Cranberry.Tests.Zone.Appearance;

/// <summary>
/// docs/106 — "the skins are grey / white in the real world" (owner, 2026-09-02).
/// <para>
/// The three things this file pins, all read off the owner's own 2026-09-02 captures:
/// </para>
/// <list type="number">
/// <item>
/// The <c>DynamicAppearanceDefinitions</c> ReferenceData <b>is</b> re-sent on the zoning burst and
/// is byte-identical to the menu send, and the worn skin rows <b>are</b> re-stated. The "the world
/// has no appearance table" hypothesis is refuted here so no later wave re-derives it:
/// <c>captures\wire-20260902-212215.txt</c> lines 21 and 330 carry the same 1,268,871-byte payload.
/// </item>
/// <item>
/// What the world adds and the menu never has is a gun in body slot 7 and looted gear, and the
/// active-hand attachment was the one dress site that carried neither an appearance list nor a
/// shader group — <c>:8142</c> has <c>slot=7 grp=0 app=[]</c> and <c>slot=76 grp=168
/// app=[181,182]</c> for the same <c>Weapon_M16A4_3P.adr</c>, in one packet.
/// </item>
/// <item>
/// The menu bootstrap ends with the equipment writer and every world burst ended with the skin
/// rows; the world bursts now end with the dress, as the owner's own server does (D86).
/// </item>
/// </list>
/// <para>
/// Send-side only, per docs/32's evidence standard: none of this is LIVE-VERIFIED.
/// </para>
/// </summary>
[Collection(AppearanceStaticsCollection.Name)]
public sealed class SkinsInWorldTests
{
    /// <summary>
    /// The MD5-gated compatibility source (D22). Every assertion that needs the real table is
    /// skipped when it is absent so the suite stays green on a machine without it.
    /// </summary>
    private const string Source = @"C:\Z1\Server\Data\dynamicAppearanceFriend.bin";

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

    // ---------------------------------------------------------------- the new selector mirror

    /// <summary>
    /// <see cref="AugustDynamicAppearanceTable.ShaderGroupFor"/> answers what the client's own
    /// selector <c>FUN_140c476f0</c> would land on. Values from the transmitted table decoded out
    /// of <c>captures\wire-20260902-212215.txt</c> line 21 on 2026-09-03.
    /// </summary>
    [Fact]
    public void ShaderGroupForAnswersTheGroupTheClientWouldApply()
    {
        if (!File.Exists(Source))
        {
            return;
        }

        AugustDynamicAppearanceTable table = Load();

        // Item 2284, the fingerless stitched-leather gloves the owner wore on 2026-09-02: rows
        // 3739 (gender 1) and 3740 (gender 2), both shader group 2461.
        Assert.Equal(2461u, table.ShaderGroupFor(2284, CharacterVisuals.Female));
        Assert.Equal(2461u, table.ShaderGroupFor(2284, CharacterVisuals.Male));

        // Item 10, the AR-15: rows 181 and 182 are BOTH gender 2, group 168. That is exactly the
        // census's "NoRowForThisBody item 10 on a male body" — a male body gets no row, so the
        // packet's own group is all the colour it will ever have, and a wrong answer here is the
        // difference between a black rifle and a white one.
        Assert.Equal(168u, table.ShaderGroupFor(10, CharacterVisuals.Female));
        Assert.Equal(0u, table.ShaderGroupFor(10, CharacterVisuals.Male));

        // An item with no row at all resolves to 0 rather than throwing.
        Assert.Equal(0u, table.ShaderGroupFor(0xDEAD_BEEF, CharacterVisuals.Female));
    }

    /// <summary>
    /// The rule, stated synthetically so it cannot rot with the archive: the client walks the
    /// attachment's appearance ids in <b>ascending row id</b> (a <c>std::set</c> in-order walk,
    /// <c>FUN_140c476f0</c>), not in the order the packet lists them, and takes the first row that
    /// is a <c>GENDER_ID = 0</c> wildcard or matches the body.
    /// </summary>
    [Fact]
    public void TheClientRuleIsAscendingRowIdAndFirstGenderMatchOrWildcard()
    {
        if (!File.Exists(Source))
        {
            return;
        }

        AugustDynamicAppearanceTable table = Load();

        // Whatever order the wire projection uses for a body, the answer is the same: the ordering
        // switch moves bytes, not the client's choice.
        foreach (uint item in new uint[] { 2284, 2049, 3875, 2281, 2258, 2253 })
        {
            IReadOnlyList<uint> unordered = table.AppearanceRowsFor(item);
            IReadOnlyList<uint> ordered = table.AppearanceRowsFor(item, CharacterVisuals.Female);
            Assert.Equal(unordered.Count, ordered.Count);

            uint answer = table.ShaderGroupFor(item, CharacterVisuals.Female);
            uint[] ascending = [.. unordered];
            Array.Sort(ascending);
            uint expected = 0;
            foreach (uint rowId in ascending)
            {
                if (table.TryGetRow(rowId, out AugustAppearanceRow row)
                    && (row.GenderId == 0 || row.GenderId == CharacterVisuals.Female))
                {
                    expected = row.ShaderParameterGroupId;
                    break;
                }
            }

            Assert.Equal(expected, answer);
        }
    }

    // ---------------------------------------------------------------- byte-exact attachment

    /// <summary>
    /// The attachment element, byte for byte, with a colourway on it. Nothing about the writer
    /// changed in this lane — what changed is that the active hand and the wardrobe rows now put
    /// values in these two fields — so this pins the two offsets the fix writes into:
    /// <c>ShaderParameterGroupId</c> immediately after the slot id, then the counted appearance
    /// list, then the trailing flag.
    /// </summary>
    [Fact]
    public void AnAttachmentCarryingAColourwayHasTheExactBytesTheFieldOrderSpells()
    {
        var attachment = new CharacterEquipmentAttachment(
            "Weapon_M16A4_3P.adr",
            SlotId: 7,
            TextureAlias: "Default",
            TintAlias: "Default",
            ShaderParameterGroupId: 168,
            AppearanceIds: [181, 182]);

        using var writer = new PacketWriter();
        attachment.WriteTo(writer);

        Assert.Equal(
            "13000000" + Convert.ToHexString("Weapon_M16A4_3P.adr"u8).ToUpperInvariant()
            + "07000000" + Convert.ToHexString("Default"u8).ToUpperInvariant()
            + "07000000" + Convert.ToHexString("Default"u8).ToUpperInvariant()
            + "01000000" + "23"          // decal alias "#"
            + "00000000"                 // TintId
            + "00000000"                 // CompositeEffectId
            + "00000000"                 // EffectId
            + "07000000"                 // SlotId 7 (RHand)
            + "A8000000"                 // ShaderParameterGroupId 168
            + "02000000" + "B5000000" + "B6000000"   // two appearance rows, 181 then 182
            + "00",                      // trailing flag
            Convert.ToHexString(writer.Written.ToArray()));
    }

    // ---------------------------------------------------------------- the fake session

    /// <summary>
    /// A dressed character zones into Z2 and the appearance table plus the worn rows go out again,
    /// in order.
    /// <para>
    /// The order asserted is the one the 2026-09-02 sessions actually carry
    /// (<c>captures\wire-20260902-212215.txt</c> rows 328-341): <c>0b → ca → 17
    /// DynamicAppearanceDefinitions → ce → 03 → 94 01 → ac 24×N → 17 ProfileDefinitions → 05</c>.
    /// The <c>94 01</c> before the <c>ac 24</c> rows is docs/32's third regression and this lane
    /// does not move it — the zoning burst never sets <c>reassertDress</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void ZoningADressedCharacterResendsTheAppearanceTableAndTheWornRowsInOrder()
    {
        (byte[][] menu, byte[][] zoning, byte[][] ready) = DriveDressedThroughZoning();

        Assert.Equal(
            ["fb", "0b", "ca", "17", "ce", "03", "94.01", "ac.24", "ac.24", "17", "05"],
            zoning.Select(Label));

        byte[] menuTable = Assert.Single(menu, IsAppearanceReferenceData);
        byte[] worldTable = Assert.Single(zoning, IsAppearanceReferenceData);
        Assert.Equal(menuTable, worldTable);

        // Every packet of the burst is a channel-0 tunnel, as the capture has it.
        Assert.All(zoning, packet => Assert.Equal(0x05, packet[0]));

        // And the zoning ClientIsReady reply ends with the SKIN ROWS. It ended with an appended
        // dress until 2026-09-03, when seven live runs proved that shape is the one the client
        // answers by never finishing its next world load (D204, docs/108).
        Assert.Equal("ac.24", Label(ready.Last(p => IsEquipment(p) || IsSkinRow(p))));
        Assert.Equal(1, ready.Count(IsEquipment));
        Assert.Equal(2, ready.Count(IsSkinRow));
    }

    /// <summary>
    /// The last-writer law, inverted by the live client on 2026-09-03 (D204, docs/108).
    /// <para>
    /// The law this lane wrote — the client's own skin-manager recomposite must never be the last
    /// thing to touch the outfit (the owner's Z1 round-29 purple suit, D86) — is not free on 1148:
    /// a lobby <c>ClientIsReady</c> that ends <c>94 01 → ac 24×N → 94 01</c> leaves the August
    /// client unable to finish the NEXT <c>ClientBeginZoning</c> world load. So the DEFAULT is the
    /// skin rows last, and <c>CRANBERRY_DRESS_LAST=1</c> is the appended dress the client refuses.
    /// This test states both halves so neither the switch nor its default can rot.
    /// </para>
    /// </summary>
    [Fact]
    public void TheSkinRowBurstEndsWithTheSkinRowsAndTheSwitchAppendsADress()
    {
        (_, _, byte[][] offReady) = DriveDressedThroughZoning();
        Assert.Equal("ac.24", Label(offReady.Last(p => IsEquipment(p) || IsSkinRow(p))));
        Assert.Equal(1, offReady.Count(IsEquipment));

        (_, _, byte[][] onReady) = DriveDressedThroughZoning(
            skins => skins with { DressLastAfterSkinRows = true });
        Assert.Equal("94.01", Label(onReady.Last(p => IsEquipment(p) || IsSkinRow(p))));
        Assert.Equal(2, onReady.Count(IsEquipment));
    }

    /// <summary>
    /// The defect itself: the gun in body slot 7 used to go out with no appearance ids and no
    /// shader group while the same mesh on a stow peg carried both.
    /// </summary>
    [Fact]
    public void TheActiveHandCarriesAColourwayLikeEveryOtherSlot()
    {
        if (!File.Exists(Source))
        {
            return;
        }

        AugustDynamicAppearanceTable table = Load();

        // Slot 7 and slot 76 hold the same item; the colourway they resolve to must be the same
        // one. Before this lane the hand resolved to (0, no rows) unconditionally.
        Assert.NotEmpty(table.AppearanceRowsFor(10, CharacterVisuals.Female));
        Assert.True(table.DefinesShaderGroup(table.ShaderGroupFor(10, CharacterVisuals.Female)));
    }

    // ---------------------------------------------------------------- harness

    private static AugustDynamicAppearanceTable Load()
    {
        Assert.True(
            AugustDynamicAppearanceTable.TryLoad(
                Source, out AugustDynamicAppearanceTable? table, out string status),
            status);
        return table!;
    }

    private static bool IsAppearanceReferenceData(byte[] packet)
    {
        if (packet.Length < 4 || packet[1] != ZoneOpcodes.ReferenceData)
        {
            return false;
        }

        int length = (packet[2] | (packet[3] << 8)) & 0x1fff;
        return length == DynamicAppearanceReference.TypeName.Length
            && System.Text.Encoding.ASCII.GetString(packet, 4, length)
                == DynamicAppearanceReference.TypeName;
    }

    private static bool IsEquipment(byte[] packet) =>
        packet.Length > 2
        && packet[1] == ZoneOpcodes.EquipmentBase
        && packet[2] == SetCharacterEquipment.SubOpcode;

    private static bool IsSkinRow(byte[] packet) =>
        packet.Length > 2
        && packet[1] == SetSkinItem.Opcode
        && packet[2] == SetSkinItem.SubOpcode;

    private static string Label(byte[] packet) => packet[1] switch
    {
        ZoneOpcodes.EquipmentBase or ZoneOpcodes.ItemsBase => $"{packet[1]:x2}.{packet[2]:x2}",
        _ => $"{packet[1]:x2}",
    };

    /// <summary>
    /// Menu bootstrap → two wardrobe picks → menu <c>ClientIsReady</c> → auto-match zoning burst →
    /// zoning <c>ClientIsReady</c>. Returns the three slices this file asserts over.
    /// </summary>
    private static (byte[][] Menu, byte[][] Zoning, byte[][] Ready) DriveDressedThroughZoning(
        Func<SkinOptions, SkinOptions>? configureSkins = null)
    {
        var pending = new ConcurrentQueue<Action>();
        var options = new ZoneOptions
        {
            AutoMatchMs = 1,
            Skins = configureSkins?.Invoke(new SkinOptions()) ?? new SkinOptions(),
        };

        var tickets = new GatewayTicketRegistry();
        GatewayAdmission admission = tickets.Issue(
            0x1001, "Cranberry", gender: 2, headId: 3, hairId: 2, skinToneId: 664, profileId: 270);
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

        using (var login = new PacketWriter())
        {
            login.WriteByte(GatewayLoginRequest.Opcode);
            login.WriteUInt64(admission.Guid);
            login.WriteString(admission.Ticket);
            login.WriteString(GatewayLoginRequest.AugustProtocol);
            login.WriteString(GatewayLoginRequest.AugustVersion);
            service.OnMessage(connection, login.Written.ToArray());
        }

        byte[][] menu = Slice(recorder, 0);

        // Two picks, one apparel and one weapon, so the burst carries skin rows at all: the
        // 2026-08-29 session that proved zoning works had none and never exercised any of this.
        AugustSkinCatalogEntry hat = AugustSkinCatalog.Apparel.Single(entry =>
            entry.CategoryPrototypeId == 2158 && entry.RewardItemId == 2484);
        AugustSkinCatalogEntry weapon = AugustSkinCatalog.Weapons.First(entry =>
            !string.IsNullOrWhiteSpace(entry.FemaleModelName));
        SendSkinClick(service, connection, hat, SetSkinItemManager.ApparelCollectionId);
        SendSkinClick(service, connection, weapon, SetSkinItemManager.WeaponCollectionId);

        SendClientIsReady(service, connection);

        int beforeZoning = recorder.Messages.Count(message => message.Direction == "s2c");

        // AutoMatchMs arms the transfer; EnterMatch's own timer fires the burst.
        for (int hop = 0; hop < 2; hop++)
        {
            Assert.True(
                SpinWait.SpinUntil(() => !pending.IsEmpty, 30_000),
                $"deferred match work {hop} was never posted");
            Assert.True(pending.TryDequeue(out Action? work));
            work!();
        }

        byte[][] zoning = Slice(recorder, beforeZoning);
        int beforeReady = recorder.Messages.Count(message => message.Direction == "s2c");

        SendClientIsReady(service, connection);
        byte[][] ready = Slice(recorder, beforeReady);
        return (menu, zoning, ready);
    }

    private static byte[][] Slice(RecordingRecorder recorder, int skip) =>
    [
        .. recorder.Messages
            .Where(message => message.Direction == "s2c")
            .Skip(skip)
            .Select(message => message.Bytes),
    ];

    private static void SendClientIsReady(ZoneService service, SoeConnection connection)
    {
        using var ready = new PacketWriter();
        ready.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        ready.WriteByte(ZoneOpcodes.ClientIsReady);
        service.OnMessage(connection, ready.Written.ToArray());
    }

    private static void SendSkinClick(
        ZoneService service,
        SoeConnection connection,
        AugustSkinCatalogEntry entry,
        uint collectionId)
    {
        using var click = new PacketWriter();
        click.WriteByte(new GatewayHeader(GatewayTunnelFromClient.Opcode, Channel: 0).ToByte());
        click.WriteByte(ZoneOpcodes.ItemsBase);
        click.WriteByte(SkinItemSelectionRequest.RequestSetSkinItemByItemId);
        click.WriteUInt32(1);
        click.WriteUInt32(0);
        click.WriteUInt32(collectionId);
        click.WriteUInt32(entry.CategoryPrototypeId);
        click.WriteUInt32(entry.AccountItemId);
        service.OnMessage(connection, click.Written.ToArray());
    }
}
