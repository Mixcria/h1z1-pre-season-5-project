using System.Diagnostics;
using System.Security.Cryptography;

namespace Cranberry.Tools.ConsoleOpener;

/// <summary>One build these RVAs were derived from.</summary>
/// <param name="Size">Exact file size in bytes — the free half of the check.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the whole file.</param>
/// <param name="Description">What the owner should see in the log.</param>
public sealed record KnownBuild(long Size, string Sha256, string Description);

/// <summary>
/// THE BUILD GATE — the reason this tool is safe to leave running.
///
/// <para>
/// Every RVA in <see cref="PatchSites"/> was derived from ONE binary. Writing <c>0xEB</c> into the
/// middle of some other build's instruction is how you get a client that crashes in a way nobody can
/// debug afterwards. Identity is checked as file size first (free) and then SHA-256 (once per
/// path/size/mtime, cached — the file is 72 MB), BEFORE a single byte is written. The expected
/// original bytes are still verified at every site afterwards, and the toggle function's prologue is
/// verified in the live process before a remote thread is fired: three independent locks.
/// </para>
///
/// <para>
/// The exe is opened for READING only. This tool never writes a file inside
/// <c>C:\Aug2017\Client</c> — on-disk patching is refused by BattlEye on this client class; only the
/// in-memory route is expected to be tolerated (and even that is unproven on 1148 — see the README).
/// </para>
/// </summary>
public sealed class BuildGate
{
    /// <summary>
    /// Builds these RVAs are known to be correct for. Add a row ONLY after re-deriving every RVA in
    /// <see cref="PatchSites"/> against the new binary — never to silence a refusal.
    ///
    /// <para>
    /// The August client: <c>C:\Aug2017\Client\H1Z1.exe</c>, build 0.0.118.208059,
    /// <c>ClientProtocol_1148</c>. Size and hash read 2026-09-02 (R5 header; re-hashed by this lane
    /// before the row was written).
    /// </para>
    /// </summary>
    public static readonly KnownBuild[] KnownBuilds =
    [
        new(72_818_304L,
            "d949d39f45074f2b223257477803a8858b4970242c6963df9a213a169d8929dd",
            "H1Z1 KotK August 2017 (0.0.118.208059, ClientProtocol_1148)"),
    ];

    private readonly Dictionary<string, (bool Known, string Detail)> _verdicts = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _anyBuild;

    /// <param name="anyBuild">
    /// True only when BOTH <c>--any-build</c> and <c>--i-re-derived-the-rvas</c> were given. A
    /// refusal then becomes a loud allow.
    /// </param>
    public BuildGate(bool anyBuild) => _anyBuild = anyBuild;

    /// <summary>
    /// Is this process the build the patch table came from? Returns a human-readable reason either
    /// way, so every log line says WHY it refused.
    /// </summary>
    public bool IsKnownBuild(Process process, out string why)
    {
        ArgumentNullException.ThrowIfNull(process);

        string path;

        try
        {
            path = process.MainModule?.FileName ?? "";
        }
        catch (Exception ex)
        {
            why = Decorate($"cannot read the module path of pid {process.Id}: {ex.Message}");
            return _anyBuild;
        }

        if (path.Length == 0)
        {
            why = Decorate($"pid {process.Id} has no readable module path");
            return _anyBuild;
        }

        return IsKnownFile(path, out why);
    }

    /// <summary>The same verdict for a path on disk, so a test can check the shipped exe.</summary>
    public bool IsKnownFile(string path, out string why)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var info = new FileInfo(path);
        string key = $"{path}|{(info.Exists ? info.Length : -1)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}";

        if (_verdicts.TryGetValue(key, out (bool Known, string Detail) cached))
        {
            why = cached.Known ? cached.Detail : Decorate(cached.Detail);
            return cached.Known || _anyBuild;
        }

        (bool known, string detail) = Judge(path, info);
        _verdicts[key] = (known, detail);
        why = known ? detail : Decorate(detail);
        return known || _anyBuild;
    }

    private static (bool Known, string Detail) Judge(string path, FileInfo info)
    {
        if (!info.Exists)
        {
            return (false, $"{path} is not readable");
        }

        KnownBuild[] sized = KnownBuilds.Where(b => b.Size == info.Length).ToArray();

        if (sized.Length == 0)
        {
            return (false,
                $"{System.IO.Path.GetFileName(path)} is {info.Length:N0} bytes; the known build is "
                + $"{KnownBuilds[0].Size:N0} bytes — this is NOT the build these RVAs came from");
        }

        string hash;

        try
        {
            using FileStream stream = File.OpenRead(path);   // read-only: the disk image is never written
            hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            return (false, $"cannot hash {path}: {ex.Message}");
        }

        KnownBuild? match = sized.FirstOrDefault(b => b.Sha256 == hash);

        return match is not null
            ? (true, $"build verified: {match.Description} (sha256 {hash[..12]}…)")
            : (false,
                $"{System.IO.Path.GetFileName(path)} hashes to {hash[..16]}… which is not a known build "
                + "— the RVAs in this tool would land in the wrong instructions");
    }

    private string Decorate(string detail) =>
        _anyBuild ? $"{detail}. --any-build given, patching ANYWAY" : detail;
}
