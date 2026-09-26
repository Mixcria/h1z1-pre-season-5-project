using System.Text.Json;

namespace Cranberry.Launcher.Core;

/// <summary>The active release and anti-replay history are separate so rollback never enables a remote downgrade.</summary>
public sealed record CommunityUpdateState(int Schema, long HighestAccepted,
    CommunityRelease? Active, CommunityRelease? Previous, CommunityRelease? Pending, string[] Failed);

public sealed class CommunityUpdateStateStore
{
    private readonly CommunityUpdateSettings _trust;
    public string Root { get; }
    public string Versions => GameInstaller.SafePath(Root, "versions");
    public string Cache => GameInstaller.SafePath(Root, "cache");
    private string StatePath => GameInstaller.SafePath(Root, "state.json");

    public CommunityUpdateStateStore(string installation, CommunityUpdateSettings trust)
    {
        trust.Validate(); _trust = trust;
        Root = GameInstaller.SafePath(installation, ".community-updates");
        Directory.CreateDirectory(Root);
    }

    public FileStream AcquireLease() => new(GameInstaller.SafePath(Root, "update.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    public CommunityUpdateState Read()
    {
        if (!File.Exists(StatePath)) return new(1, _trust.Sequence, null, null, null, []);
        if (new FileInfo(StatePath).Length > 128 * 1024) throw new InvalidDataException("Update state is too large.");
        var state = JsonSerializer.Deserialize<CommunityUpdateState>(File.ReadAllText(StatePath))
            ?? throw new InvalidDataException("Update state is empty.");
        Validate(state); return state;
    }

    public void Write(CommunityUpdateState state)
    {
        Validate(state);
        AtomicWrite(StatePath, JsonSerializer.SerializeToUtf8Bytes(state));
    }

    private void Validate(CommunityUpdateState state)
    {
        if (state.Schema != 1 || state.HighestAccepted < _trust.Sequence || state.HighestAccepted > 999999999999
            || state.Failed is null || state.Failed.Length > 256 || state.Failed.Any(h => h is null || h.Length != 64 || !h.All(char.IsAsciiHexDigit)))
            throw new InvalidDataException("Invalid community update state.");
        foreach (var release in new[] { state.Active, state.Previous, state.Pending }) release?.Verify(_trust.PublicKey);
        if (state.Active is { } active && active.Sequence > state.HighestAccepted
            || state.Previous is { } previous && previous.Sequence > state.HighestAccepted)
            throw new InvalidDataException("Invalid accepted release history.");
    }

    public static string[] MarkFailed(string[] failed, CommunityRelease release) =>
        failed.Append(release.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).TakeLast(256).ToArray();

    public string BundlePath(CommunityRelease release)
    {
        release.Verify(_trust.PublicKey);
        return GameInstaller.SafePath(Versions, $"{release.Sequence}-{release.Sha256}/app");
    }

    internal static void AtomicWrite(string destination, byte[] data)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".new";
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough))
        { output.Write(data); output.Flush(flushToDisk: true); }
        File.Move(temporary, destination, overwrite: true);
    }
}
