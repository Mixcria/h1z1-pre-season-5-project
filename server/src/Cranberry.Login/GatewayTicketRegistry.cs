using System.Security.Cryptography;

namespace Cranberry.Login;

/// <summary>
/// Short-lived handoff from the login listener to the gateway listener. The ticket and guid are
/// echoed by the client in its first gateway message; the key arms RC4 immediately afterwards.
/// </summary>
public sealed class GatewayTicketRegistry
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly Dictionary<string, GatewayAdmission> _tickets = new(StringComparer.Ordinal);

    public GatewayAdmission Issue(
        ulong guid,
        string characterName = "",
        uint gender = 0,
        uint headId = 0,
        uint hairId = 0,
        uint skinToneId = 0,
        uint profileId = 0,
        string accountId = "")
    {
        byte[] key = RandomNumberGenerator.GetBytes(16);
        string ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        var admission = new GatewayAdmission(
            guid,
            ticket,
            key,
            DateTimeOffset.UtcNow + Lifetime,
            characterName,
            gender,
            headId,
            hairId,
            skinToneId,
            profileId,
            accountId);

        lock (_gate)
        {
            RemoveExpired(DateTimeOffset.UtcNow);
            _tickets[ticket] = admission;
        }

        return admission;
    }

    public bool TryValidate(string ticket, ulong guid, out GatewayAdmission admission)
    {
        lock (_gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            RemoveExpired(now);
            if (_tickets.TryGetValue(ticket, out GatewayAdmission? found)
                && found.Guid == guid
                && found.ExpiresAt > now)
            {
                admission = found;
                return true;
            }
        }

        admission = null!;
        return false;
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (string ticket in _tickets
            .Where(pair => pair.Value.ExpiresAt <= now)
            .Select(pair => pair.Key)
            .ToArray())
        {
            _tickets.Remove(ticket);
        }
    }
}

/// <summary>
/// One issued gateway ticket. The selected appearance travels with the handoff so the zone can
/// build the same actor the roster advertised without owning or re-reading login persistence.
/// Gender uses the client's own values (Models.txt column GENDER: 1 male, 2 female).
/// </summary>
public sealed record GatewayAdmission(
    ulong Guid,
    string Ticket,
    byte[] Key,
    DateTimeOffset ExpiresAt,
    string CharacterName = "",
    uint Gender = 0,
    uint HeadId = 0,
    uint HairId = 0,
    uint SkinToneId = 0,
    uint ProfileId = 0,
    string AccountId = "");
