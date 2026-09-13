using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Cranberry.Launcher.Service;

public sealed record LauncherHostOptions
{
    public int Port { get; init; } = 20040;
    public string BindAddress { get; init; } = "127.0.0.1";
    public string JoinCode { get; init; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    public string OwnerCode { get; init; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public string PackageDirectory { get; init; } = "launcher-release";
    public string CertificateFile { get; init; } = "state/launcher/server.pfx";
    public bool ProximityVoiceEnabled { get; init; } = true;
    public float VoiceRangeMetres { get; init; } = 75;
    public int VoiceMaxSpeakers { get; init; } = 16;

    public static LauncherHostOptions Load(string root)
    {
        string path = Path.Combine(root, "launcher-host.json");
        var options = File.Exists(path) ? JsonSerializer.Deserialize<LauncherHostOptions>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Invalid launcher configuration.") : new();
        if (options.Port is < 1 or > 65535 || !IPAddress.TryParse(options.BindAddress, out _)
            || options.JoinCode.Length < 16 || options.OwnerCode.Length < 32
            || !float.IsFinite(options.VoiceRangeMetres) || options.VoiceRangeMetres is < 5 or > 300
            || options.VoiceMaxSpeakers is < 1 or > 150)
            throw new InvalidDataException("Invalid launcher host port, address or registration codes.");
        if (!File.Exists(path)) File.WriteAllText(path, JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true }));
        return options;
    }

    public X509Certificate2 Certificate(string root)
    {
        string path = Path.GetFullPath(Path.Combine(root, CertificateFile));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            using var key = RSA.Create(3072);
            var request = new CertificateRequest("CN=Cranberry Local Server", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            var usages = new OidCollection { new("1.3.6.1.5.5.7.3.1") };
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
        }
        return X509CertificateLoader.LoadPkcs12FromFile(path, null);
    }
}
