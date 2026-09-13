using Cranberry.Protocol;
using Cranberry.Zone.Economy;

namespace Cranberry.Zone;

/// <summary>August 14122bb20/14122bf80: f503 + crate definition u32 + open-one bool.</summary>
public sealed record StartCrateOpening(uint CrateItemId, bool OpenOne)
{
    public const byte BaseOpcode = 0xf5;
    public const int RequiredHits = 6; // ShootingGalleryCrate's six clasp-hit stages

    public static StartCrateOpening Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        if (reader.ReadByte() != BaseOpcode || reader.ReadByte() != 3)
            throw new PacketFormatException("Expected a crate-opening start request.");
        uint item = reader.ReadUInt32();
        byte one = reader.ReadByte();
        if (item == 0 || one > 1 || reader.Remaining != 0)
            throw new PacketFormatException("Invalid crate-opening request.");
        return new(item, one != 0);
    }

    /// <summary>August 14122c4f0/14122c6a0 send header-only Done (5) and Stop (4).</summary>
    public static bool IsCompletion(ReadOnlySpan<byte> payload) =>
        payload.Length == 2 && payload[0] == BaseOpcode && payload[1] is 4 or 5;
}

/// <summary>
/// f502, August 140d9f480/140da4fc0. Strings are locale code keys, not literal text.
/// ResetTables invokes 140da25d0 and clears all three crate-opening UI tables.
/// </summary>
public sealed record CrateOpeningScreen(bool ShowInfoMessage = false, bool ShowResultsMessage = false,
    bool CrateIsOpen = false, string InfoMessageCode = "", string ResultsTitleCode = "",
    string ResultsSubtitleCode = "", string ResultsListTitleCode = "UI.CrateOpening.ReceivedItemsTitle",
    uint ListCrateImageSetId = 0, bool ResetTables = false, bool ShowAgain = false, uint SubtitleCount = 0)
{
    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(StartCrateOpening.BaseOpcode);
        writer.WriteByte(2);
        writer.WriteBool(ShowInfoMessage);
        writer.WriteBool(ShowResultsMessage);
        writer.WriteBool(CrateIsOpen);
        writer.WriteString(InfoMessageCode);
        writer.WriteString(ResultsTitleCode);
        writer.WriteString(ResultsSubtitleCode);
        writer.WriteString(ResultsListTitleCode);
        writer.WriteUInt32(ListCrateImageSetId);
        writer.WriteBool(ResetTables);
        writer.WriteBool(ShowAgain);
        writer.WriteUInt32(SubtitleCount);
    }
}

public readonly record struct UnopenedCrateRow(uint NameLocaleId, uint ImageSetId,
    uint RarityColorRed, uint RarityColorGreen, uint RarityColorBlue);

/// <summary>f506, 140d9f810/140d9f0d0: replace the unopened list, including an empty list.</summary>
public sealed record CrateOpeningUnopened(IReadOnlyList<UnopenedCrateRow> Crates)
{
    public void WriteTo(PacketWriter writer)
    {
        if (Crates.Count > 100 || Crates.Any(row => row.NameLocaleId == 0 || row.ImageSetId == 0
            || row.RarityColorRed > 255 || row.RarityColorGreen > 255 || row.RarityColorBlue > 255))
            throw new ArgumentException("Invalid unopened crate presentation.");
        writer.WriteByte(StartCrateOpening.BaseOpcode);
        writer.WriteByte(6);
        writer.WriteInt32(Crates.Count);
        foreach (var row in Crates)
        {
            writer.WriteUInt32(row.NameLocaleId);
            writer.WriteUInt32(row.ImageSetId);
            writer.WriteUInt32(row.RarityColorRed);
            writer.WriteUInt32(row.RarityColorGreen);
            writer.WriteUInt32(row.RarityColorBlue);
        }
    }
}

/// <summary>
/// f507, 140d9f700/140d9f020. 140da4770 presents only the first row; send one packet per reward.
/// The first value is a numeric locale ID, not an account or appearance item ID.
/// </summary>
public sealed record CrateOpeningOpened(uint NameLocaleId, uint ImageSetId, uint RarityId)
{
    public void WriteTo(PacketWriter writer)
    {
        if (NameLocaleId == 0 || ImageSetId == 0)
            throw new ArgumentException("Missing native reward presentation metadata.");
        writer.WriteByte(StartCrateOpening.BaseOpcode);
        writer.WriteByte(7);
        writer.WriteInt32(1);
        writer.WriteUInt32(NameLocaleId);
        writer.WriteUInt32(ImageSetId);
        writer.WriteUInt32(RarityId);
    }
}

/// <summary>August f508/09 updates the seal UI; f50b/c enables/disables native gallery shooting.</summary>
public sealed record CrateOpeningControl(byte SubOpcode)
{
    public void WriteTo(PacketWriter writer)
    {
        if (SubOpcode is not (8 or 9 or 11 or 12))
            throw new ArgumentOutOfRangeException(nameof(SubOpcode));
        writer.WriteByte(StartCrateOpening.BaseOpcode);
        writer.WriteByte(SubOpcode);
    }
}

/// <summary>
/// August f50a, 140da35a0: identifies the actor/socket projected into the reward icon's
/// screen coordinates. This packet does not create the actor or reveal a reward.
/// </summary>
public sealed record CrateOpeningTarget(ulong ActorGuid, string Socket = "WorldRoot")
{
    public void WriteTo(PacketWriter writer)
    {
        writer.WriteByte(StartCrateOpening.BaseOpcode);
        writer.WriteByte(10);
        writer.WriteUInt64(ActorGuid);
        writer.WriteString(Socket);
    }
}

/// <summary>
/// Immediate result presentation using proven August table packets. This does not synthesize
/// the separate 3D shooting scene. The persisted transaction must finish before these packets.
/// </summary>
public static class CrateOpeningPresentation
{
    public static IReadOnlyList<byte[]> Result(EconomyCrate? crate, IReadOnlyList<OwnedAccountItem> awards,
        bool showAgain, EconomyCatalog? catalog = null)
    {
        catalog ??= EconomyCatalog.Default;
        var packets = new List<byte[]>();
        void Add(Action<PacketWriter> write)
        {
            using var writer = new PacketWriter();
            write(writer);
            packets.Add(writer.Written.ToArray());
        }
        // Reset before appending rows: the reset also installs native screen-center icon coordinates.
        Add(new CrateOpeningScreen(ListCrateImageSetId: crate?.ImageSetId ?? 0, ResetTables: true).WriteTo);
        Add(new CrateOpeningUnopened([]).WriteTo);
        uint count = 0;
        foreach (var item in awards)
        {
            var skin = catalog.Skins[item.AccountItemId];
            for (uint i = 0; i < item.Count; i++)
                Add(new CrateOpeningOpened(skin.NameLocaleId, skin.ImageSetId, skin.RarityId).WriteTo);
            count = checked(count + item.Count);
        }
        Add(new CrateOpeningScreen(ShowResultsMessage: true, CrateIsOpen: count > 0,
            ResultsTitleCode: count > 0 ? "UI.CrateOpening.FinishedCongrats" : "UI.CrateOpening.FinishedNoCratesOpened",
            ResultsSubtitleCode: count > 0 ? "UI.CrateOpening.FinishedItemCount" : "",
            ListCrateImageSetId: crate?.ImageSetId ?? 0, ShowAgain: showAgain, SubtitleCount: count).WriteTo);
        return packets;
    }
}
