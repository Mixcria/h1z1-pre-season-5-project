using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Cranberry.Zone.DevConsole;

/// <summary>
/// Who this session is allowed to be. Jiggy's permission ladder needs an identity; a zone server on
/// the owner's own machine has three things it can honestly use - where the packet came from, the
/// character name and the character guid - so those are what decide (design §4.2).
/// <para>
/// Resolution happens once, at login, so the boot banner, the Door-A self flag and the first typed
/// command cannot disagree about who is at the keyboard.
/// </para>
/// <para>
/// <b>Order.</b> An explicit entry in <see cref="ConsoleOptions.Tiers"/> naming this character or
/// guid wins, because it is a deliberate statement about one player. Otherwise a loopback or
/// private-network remote is Owner when <see cref="ConsoleOptions.LocalIsOwner"/> is on - the owner
/// plays on the same box the host runs on. Otherwise the list's <c>*</c> entry, and otherwise
/// <see cref="ConsoleTier.Player"/>: a stranger may look, never touch.
/// </para>
/// </summary>
public static class ConsolePermission
{
    /// <summary>Resolves the tier of one session.</summary>
    /// <param name="remote">The gateway remote endpoint; null is treated as not local.</param>
    /// <param name="name">The character name, as the login gave it. May be empty.</param>
    /// <param name="guid">The character guid. Zero when there is not one yet.</param>
    /// <param name="options">The console switches.</param>
    public static ConsoleTier Resolve(IPEndPoint? remote, string? name, ulong guid, ConsoleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ConsoleTier? wildcard = null;
        foreach ((string key, ConsoleTier tier) in ParseTiers(options.Tiers))
        {
            if (key == "*")
            {
                wildcard ??= tier;
                continue;
            }

            if (!string.IsNullOrEmpty(name) && string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return tier;
            }

            if (guid != 0 && TryParseGuid(key, out ulong parsed) && parsed == guid)
            {
                return tier;
            }
        }

        if (options.LocalIsOwner && IsLocal(remote?.Address))
        {
            return ConsoleTier.Owner;
        }

        return wildcard ?? ConsoleTier.Player;
    }

    /// <summary>
    /// Splits <c>name=owner,0x1001=tester,*=player</c>. A malformed pair is skipped rather than
    /// thrown: a typo in the environment must not stop the host booting.
    /// </summary>
    public static IEnumerable<(string Key, ConsoleTier Tier)> ParseTiers(string? list)
    {
        if (string.IsNullOrWhiteSpace(list))
        {
            yield break;
        }

        foreach (string entry in list.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int split = entry.IndexOf('=');
            if (split <= 0 || split == entry.Length - 1)
            {
                continue;
            }

            string key = entry[..split].Trim();
            if (TryParseTier(entry[(split + 1)..].Trim(), out ConsoleTier tier))
            {
                yield return (key, tier);
            }
        }
    }

    /// <summary>Reads owner/admin, tester and player/client, case-insensitively.</summary>
    public static bool TryParseTier(string? word, out ConsoleTier tier)
    {
        switch (word?.Trim().ToLowerInvariant())
        {
            case "owner":
            case "admin":
                tier = ConsoleTier.Owner;
                return true;
            case "tester":
                tier = ConsoleTier.Tester;
                return true;
            case "player":
            case "client":
                tier = ConsoleTier.Player;
                return true;
            default:
                tier = ConsoleTier.Player;
                return false;
        }
    }

    /// <summary>
    /// Loopback, RFC1918, link-local, IPv6 link-local and IPv6 unique-local (<c>fc00::/7</c>). An
    /// IPv4-mapped IPv6 address is unmapped first, because a dual-stack listener reports
    /// <c>::ffff:127.0.0.1</c> for a loopback client.
    /// </summary>
    public static bool IsLocal(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] octets = address.GetAddressBytes();
            return octets[0] switch
            {
                10 => true,
                127 => true,
                169 => octets[1] == 254,
                172 => octets[1] >= 16 && octets[1] <= 31,
                192 => octets[1] == 168,
                _ => false,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            {
                return true;
            }

            byte first = address.GetAddressBytes()[0];
            return (first & 0xFE) == 0xFC;
        }

        return false;
    }

    private static bool TryParseGuid(string key, out ulong value)
    {
        if (key.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(key.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return ulong.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
