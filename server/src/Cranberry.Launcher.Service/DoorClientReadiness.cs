using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Cranberry.Launcher.Core;

namespace Cranberry.Launcher.Service;

/// <summary>Native compatibility belongs to one authenticated game launch, never to a persisted account.</summary>
public sealed class DoorClientReadiness
{
    private sealed record Launch(string TicketHash, bool Ready);
    private readonly ConcurrentDictionary<string, Launch> _launches = new(StringComparer.Ordinal);

    public static void RequireProtocol(int version)
    {
        // Protocol 1 launchers inject protected native code. Require the updated
        // protocol 0 launcher, which uses the client's original door controller.
        if (version != 0)
            throw new InvalidOperationException("Close the game and reopen the launcher to install the native-client compatibility update.");
    }

    public void Begin(string account, string ticket, int version)
    {
        RequireProtocol(version);
        // Original doors need no injected helper or asynchronous patch receipt.
        // This is admission for a declared compatible launch, not patch attestation.
        _launches[account] = new Launch(Hash(ticket), true);
    }

    public bool Confirm(string account, string ticket, int version)
    {
        RequireProtocol(version);
        if (!_launches.TryGetValue(account, out var launch) || launch.TicketHash != Hash(ticket)) return false;
        return launch.Ready || _launches.TryUpdate(account, launch with { Ready = true }, launch);
    }

    public bool IsReady(string account) => _launches.TryGetValue(account, out var launch) && launch.Ready;
    public void Remove(string account) => _launches.TryRemove(account, out _);
    private static string Hash(string ticket) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));
}
