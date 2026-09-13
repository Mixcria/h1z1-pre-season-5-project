using Cranberry.Login;
using Cranberry.Protocol;

namespace Cranberry.Zone;

/// <summary>
/// One native EventsHostedGames schedule row. August FUN_1413ebae0 parses this exact
/// order; FUN_141660710 publishes the named fields to MatchSchedule. The world also
/// has to exist in the login list or the handler skips it. The service builds these
/// rows per account after checking hosted-game grants; no paid tickets or spectator rights.
/// </summary>
public sealed record HostedGameScheduleEntry(uint WorldId, uint GameModeId = GameWorldCatalog.SoloGameModeId)
{
    public bool CanEnter { get; init; } = true;
    // Client CodeStringMappings: HOSTED GAMES / JOIN A HOSTED GAME.
    public uint TitleStringId { get; init; } = 15997;
    public uint DescriptionStringId { get; init; } = 15998;
    public uint ImageSetId { get; init; }
    public ulong UnlockTime { get; init; }
    public ulong StartTime { get; init; }

    public void WriteTo(PacketWriter writer)
    {
        writer.WriteUInt32(WorldId);              // +0x20 WorldId
        writer.WriteUInt32(1);                    // +0x24 MatchGameModeName.1 (Solo)
        writer.WriteUInt32(GameModeId);           // +0x28 GameMode
        writer.WriteUInt32(TitleStringId);        // +0x30 title locale id
        writer.WriteUInt32(DescriptionStringId);  // +0x34 Description
        writer.WriteUInt32(ImageSetId);           // +0x38 ImageSetId
        writer.WriteUInt64(UnlockTime);           // +0x40
        writer.WriteUInt64(StartTime);            // +0x48
        writer.WriteUInt32(0);                    // +0x6c EntryFee
        writer.WriteString(string.Empty);         // +0x70 EntryFeeCurrencyCode
        writer.WriteUInt32(0);                    // +0x88 EventTicketsRequired
        writer.WriteUInt32(0);                    // +0x8c PrizeItemId
        writer.WriteUInt32(0);                    // +0x94 unknown reward field
        writer.WriteBool(false);                 // +0xa8 IsLocked
        writer.WriteBool(CanEnter);               // +0xa9 CanEnter
        writer.WriteString(string.Empty);         // +0xb0 WatchUrl
        writer.WriteBool(true);                  // +0xc8 IsHostedGame
        writer.WriteUInt32(1);                    // +0xcc role: player (2 is observer)
        writer.WriteBool(false);                 // +0xd0 CanWatch
        writer.WriteUInt32(0);                    // +0xf8 unknown
        writer.WriteInt32(0);                     // +0x108 u64-list count
    }
}
