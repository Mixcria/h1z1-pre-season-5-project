using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cranberry.Launcher.Core;

public sealed record CommunityUpdateSettings(int Schema, string Repository, string PublicKey,
    bool IncludePrereleases, long Sequence, string Version)
{
    public const string FileName = "community-update.json";

    public void Validate()
    {
        string[] parts = Repository?.Split('/') ?? [];
        if (Schema != 1 || Sequence is < 0 or > CommunityRelease.MaximumSequence
            || !CommunityRelease.ValidVersion(Version) || parts.Length != 2
            || parts.Any(p => p.Length is < 1 or > 100 || p is "." or ".."
                || p.StartsWith('.') || p.EndsWith('.')
                || !p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new InvalidDataException("Invalid community update settings.");
        // Unsigned development packages deliberately have no update authority.
        if (Sequence == 0 && PublicKey == "") return;
        using var key = CommunityRelease.ReadPublicKey(PublicKey);
    }

    public static CommunityUpdateSettings Load(string root)
    {
        string path = GameInstaller.SafePath(root, FileName);
        if (new FileInfo(path).Length > 64 * 1024)
            throw new InvalidDataException("Community update settings are too large.");
        try
        {
            var settings = JsonSerializer.Deserialize<CommunityUpdateSettings>(File.ReadAllBytes(path), CommunityRelease.Json)
                ?? throw new InvalidDataException("Missing community update settings.");
            settings.Validate();
            return settings;
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid community update settings JSON.", ex); }
    }
}

/// <summary>Offline-signed identity for a complete, immutable community application bundle.</summary>
public sealed record CommunityRelease(int Schema, long Sequence, string Version, string Platform,
    int MinimumUpdaterVersion, int DataSchema, long Size, string Sha256, string FilesSha256, string Signature)
{
    public const string ArchiveFileName = "Cranberry-Local-update.zip";
    public const string ManifestFileName = "community-release.json";
    public const string FilesFileName = "community-files.json";
    public const long MaximumSequence = 999999999999;
    public const long MaximumSize = 2L * 1024 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    internal static bool ValidVersion(string? version) => version is { Length: >= 1 and <= 64 }
        && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+');

    internal static bool ValidHash(string? hash) => hash is { Length: 64 }
        && hash.All(char.IsAsciiHexDigit) && hash == hash.ToUpperInvariant();

    public byte[] SigningPayload()
    {
        if (Schema != 1 || Sequence is < 1 or > MaximumSequence || !ValidVersion(Version)
            || Platform != "win-x64" || MinimumUpdaterVersion != 1 || DataSchema != 1
            || Size is < 1 or > MaximumSize || !ValidHash(Sha256) || !ValidHash(FilesSha256))
            throw new InvalidDataException("Unsupported or invalid community release metadata.");
        return Encoding.UTF8.GetBytes(string.Join('\n', "cranberry-community-v1",
            Sequence.ToString(CultureInfo.InvariantCulture), Version, Platform,
            MinimumUpdaterVersion.ToString(CultureInfo.InvariantCulture), DataSchema.ToString(CultureInfo.InvariantCulture),
            Size.ToString(CultureInfo.InvariantCulture), Sha256, FilesSha256) + "\n");
    }

    public void Verify(string publicKey)
    {
        byte[] payload = SigningPayload();
        try
        {
            using var key = ReadPublicKey(publicKey);
            if (Signature is null || Signature.Length != 88
                || !key.VerifyData(payload, Convert.FromBase64String(Signature), HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new InvalidDataException("The community release signature is invalid.");
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        { throw new InvalidDataException("The community release signature is invalid.", ex); }
    }

    internal static ECDsa ReadPublicKey(string? publicKey)
    {
        ECDsa? key = null;
        try
        {
            if (publicKey is null || publicKey.Length is < 1 or > 1024)
                throw new InvalidDataException("A community P-256 public key is required.");
            byte[] bytes = Convert.FromBase64String(publicKey);
            key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(bytes, out int read);
            if (read != bytes.Length || key.KeySize != 256
                || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new InvalidDataException("The community public key must be NIST P-256.");
            return key;
        }
        catch (Exception ex)
        {
            key?.Dispose();
            if (ex is FormatException or CryptographicException)
                throw new InvalidDataException("Invalid community public key.", ex);
            throw;
        }
    }
}

public sealed record CommunityReleaseDownload(CommunityRelease Release, Uri ArchiveUri);
