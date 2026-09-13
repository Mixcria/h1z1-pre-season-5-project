using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cranberry.Launcher.Core;

/// <summary>A release signed offline by the publisher. The server never holds the signing key.</summary>
public sealed record LauncherRelease(int Schema, long Sequence, string Version, string Platform,
    long Size, string Sha256, string Signature)
{
    public const long MaximumSize = 256L * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEgE8f+xnUzpOkgrU36Pfu5Lpo/m6+HD82ZagHoMkM4vaodz+YeZ/k8DDu5NruaUjH0SGvcSvj691hzKK+Ouj0gg==";

    public byte[] SigningPayload()
    {
        if (Schema != 1 || Sequence is < 1 or > 999999999999 || Platform != "win-x64"
            || Size is < 1 or > MaximumSize || Version is null || Version.Length is < 1 or > 64
            || !Version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+')
            || Sha256 is null || Sha256.Length != 64 || !Sha256.All(char.IsAsciiHexDigit)
            || Sha256 != Sha256.ToUpperInvariant())
            throw new InvalidDataException("Invalid launcher release metadata.");
        return Encoding.UTF8.GetBytes(string.Join('\n', "cranberry-launcher-v1",
            Sequence.ToString(CultureInfo.InvariantCulture), Version, Platform,
            Size.ToString(CultureInfo.InvariantCulture), Sha256) + "\n");
    }

    public void Verify() => Verify(Convert.FromBase64String(PublicKey));

    internal void Verify(byte[] publicKey)
    {
        byte[] payload = SigningPayload();
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            if (Signature is null || Signature.Length != 88 || !key.VerifyData(payload,
                    Convert.FromBase64String(Signature), HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new InvalidDataException("The launcher update signature is invalid.");
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        { throw new InvalidDataException("The launcher update signature is invalid.", ex); }
    }

    public Task<bool> Matches(string path, CancellationToken ct = default) =>
        GameInstaller.Matches(path, new GameFile("Cranberry.Launcher.exe", Size, Sha256), ct);
}
