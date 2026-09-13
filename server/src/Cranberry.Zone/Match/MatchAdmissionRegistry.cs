using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Cranberry.Protocol;
using Cranberry.Login;

namespace Cranberry.Zone.Match;

/// <summary>
/// August ec, serialized by FUN_140f23780 and assembled by FUN_140f2a0e0:
/// u32 worldId; string; u32; u8; i32 role. The intervening fields retain neutral
/// names because their meanings are not established. Captured public-player requests
/// have empty text, zero value, flag 1, role 1. This decoder alone grants no admission.
/// </summary>
public sealed record PlayerWorldTransferRequest(
    uint WorldId, string AdmissionText, uint UnknownValue, byte Flag, int Role)
{
    public const byte Opcode = ZoneOpcodes.PlayerWorldTransferRequest;
    public const int PlayerRole = 1;
    public const int MinimumLength = 18;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool TryParse(
        ReadOnlySpan<byte> payload, [NotNullWhen(true)] out PlayerWorldTransferRequest? request)
    {
        request = null;
        if (payload.Length < MinimumLength || payload[0] != Opcode) return false;
        try
        {
            var reader = new PacketReader(payload);
            reader.Skip(1);
            uint worldId = reader.ReadUInt32();
            string text = StrictUtf8.GetString(reader.ReadCountedBytes());
            uint value = reader.ReadUInt32();
            byte flag = reader.ReadByte();
            int role = reader.ReadInt32();
            if (!reader.AtEnd) return false;
            request = new(worldId, text, value, flag, role);
            return true;
        }
        catch (PacketFormatException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

/// <summary>Server configuration for an advertised world; mode 13 alone does not mean Solo.</summary>
public sealed record MatchAdmissionDefinition(
    uint WorldId, uint GameModeId, MatchQueueKind QueueKind, MatchMode Mode,
    string Region = "EU", int WorldNumber = 1)
{
    public string DisplayName => QueueKind is MatchQueueKind.Hosted or MatchQueueKind.Custom
        ? "Hosted Games" : $"{Mode} {Region} World {WorldNumber}";
}

/// <summary>
/// Resolves a client-selected world through an explicit server definition. An unknown
/// world, non-player role or unrecognized public request envelope stays ineligible.
/// Hosted/custom classification always comes from server configuration, never player count.
/// </summary>
public sealed class MatchAdmissionRegistry
{
    private readonly IReadOnlyDictionary<uint, MatchAdmissionDefinition> _definitions;

    /// <summary>
    /// Admissions use exactly the same local world definitions as the login list.
    /// World ids are server configuration; mode ids are the August client's data.
    /// </summary>
    public static MatchAdmissionRegistry Default { get; } = new(
        GameWorldCatalog.Default.Select(world => new MatchAdmissionDefinition(
            world.WorldId, world.GameModeId,
            world.IsHosted ? MatchQueueKind.Hosted : MatchQueueKind.Public,
            world.GameModeId switch
            {
                GameWorldCatalog.DuosGameModeId => MatchMode.Duos,
                GameWorldCatalog.FivesGameModeId => MatchMode.Fives,
                _ => MatchMode.Solo,
            }, world.Region, world.WorldNumber)));

    public IReadOnlyCollection<MatchAdmissionDefinition> Definitions => _definitions.Values.ToArray();

    public MatchAdmissionRegistry(IEnumerable<MatchAdmissionDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var copied = new Dictionary<uint, MatchAdmissionDefinition>();
        foreach (MatchAdmissionDefinition definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (definition.WorldId == 0 || definition.GameModeId == 0
                || !Enum.IsDefined(definition.QueueKind) || definition.QueueKind == MatchQueueKind.Unknown
                || !Enum.IsDefined(definition.Mode) || definition.Mode == MatchMode.Unknown
                || string.IsNullOrWhiteSpace(definition.Region) || definition.WorldNumber < 1)
                throw new ArgumentException("Every advertised world needs an explicit valid admission definition.", nameof(definitions));
            if (!copied.TryAdd(definition.WorldId, definition))
                throw new ArgumentException("An advertised world must have exactly one admission definition.", nameof(definitions));
        }
        _definitions = new ReadOnlyDictionary<uint, MatchAdmissionDefinition>(copied);
    }

    public bool TryGetDefinition(uint worldId, [NotNullWhen(true)] out MatchAdmissionDefinition? definition) =>
        _definitions.TryGetValue(worldId, out definition);

    public MatchAdmissionContext Resolve(PlayerWorldTransferRequest? request, ulong matchId)
    {
        if (matchId == 0 || request is null || request.Role != PlayerWorldTransferRequest.PlayerRole
            || !_definitions.TryGetValue(request.WorldId, out MatchAdmissionDefinition? definition))
            return MatchAdmissionContext.Unknown;

        // The text could be a custom/hosted admission token. Its exact semantics are unresolved;
        // do not silently treat an unfamiliar envelope as a normal public Solo request.
        if (definition.QueueKind == MatchQueueKind.Public
            && (request.AdmissionText.Length != 0 || request.UnknownValue != 0 || request.Flag != 1))
            return MatchAdmissionContext.Unknown;

        return new(matchId, definition.QueueKind, definition.Mode);
    }
}
