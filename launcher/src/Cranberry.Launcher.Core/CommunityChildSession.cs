using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;

namespace Cranberry.Launcher.Core;

internal sealed record CommunityChildContext(string Nonce, CommunityStartRequest Request);
internal sealed record CommunityChildReceipt(string Nonce, int LauncherId, int? HostId, long? HostStartTicks,
    bool Ready, string? Failure);

/// <summary>Private parent/child handshake; success means the GUI and its pinned local host are ready.</summary>
public sealed class CommunityChildSession
{
    internal const string EnvironmentName = "CRANBERRY_COMMUNITY_CHILD";
    private readonly CommunityChildContext _context;
    private readonly string _receipt;
    private CommunityChildReceipt _status;
    public string? Notice => _context.Request.Notice;

    private CommunityChildSession(CommunityChildContext context)
    {
        _context = context;
        _receipt = ReceiptPath(context);
        _status = new(context.Nonce, Environment.ProcessId, null, null, false, null);
        Save();
    }

    public static CommunityChildSession? Open(string packageDirectory, string? stateDirectory)
    {
        string? encoded = Environment.GetEnvironmentVariable(EnvironmentName);
        Environment.SetEnvironmentVariable(EnvironmentName, null);
        if (encoded is null) return null;
        if (encoded.Length > 32 * 1024 || !Console.IsInputRedirected || stateDirectory is null)
            throw new InvalidDataException("Invalid launcher update session.");
        var context = JsonSerializer.Deserialize<CommunityChildContext>(encoded)
            ?? throw new InvalidDataException("Missing launcher update session.");
        if (context.Nonce is null || context.Nonce.Length != 64 || !context.Nonce.All(char.IsAsciiHexDigit)
            || context.Request is null
            || !SamePath(context.Request.PackageDirectory, packageDirectory)
            || !SamePath(context.Request.StateDirectory, stateDirectory))
            throw new InvalidDataException("Launcher update session does not match this package and data folder.");
        return new(context);
    }

    public void HostStarted(int processId, long startTicks)
    {
        _status = _status with { HostId = processId, HostStartTicks = startTicks };
        Save();
    }

    public void Ready()
    {
        if (_status.HostId is null) throw new InvalidOperationException("Local host has not started.");
        _status = _status with { Ready = true }; Save();
    }

    public void Failed(Exception error)
    {
        _status = _status with { Ready = false, Failure = error.Message[..Math.Min(4000, error.Message.Length)] };
        Save();
    }

    /// <summary>The parent retains stdin until the session ends. EOF requests graceful owned-host shutdown.</summary>
    public async Task WaitForOwnerExit()
    {
        var input = Console.OpenStandardInput();
        byte[] buffer = new byte[1];
        try { while (await input.ReadAsync(buffer) != 0) { } }
        catch (IOException) { }
    }

    private void Save() => CommunityUpdateStateStore.AtomicWrite(_receipt, JsonSerializer.SerializeToUtf8Bytes(_status));

    internal static CommunityChildContext Create(CommunityStartRequest request) =>
        new(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), request);
    private static bool SamePath(string first, string second) =>
        Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Equals(
            Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    internal static string ReceiptPath(CommunityChildContext context)
    {
        string directory = GameInstaller.SafePath(context.Request.UpdateDirectory, "sessions");
        Directory.CreateDirectory(directory);
        return GameInstaller.SafePath(directory, context.Nonce + ".json");
    }
}
