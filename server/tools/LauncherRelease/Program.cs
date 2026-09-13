using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Cranberry.Launcher.Core;

if (args.Length == 2 && args[0] == "verify")
{
    var release = JsonSerializer.Deserialize<LauncherRelease>(File.ReadAllText(args[1]), LauncherRelease.Json)!;
    release.Verify();
    Console.WriteLine($"Verified launcher {release.Version}, release {release.Sequence}, {release.Size} bytes.");
    return;
}
if (args.Length != 6 || args[0] != "sign")
    throw new ArgumentException("Use: sign <exe> <sequence> <version> <private-key.pem> <manifest.json>, or verify <manifest.json>.");
using var stream = File.OpenRead(args[1]);
var unsigned = new LauncherRelease(1, long.Parse(args[2], CultureInfo.InvariantCulture), args[3], "win-x64", stream.Length,
    Convert.ToHexString(SHA256.HashData(stream)), "");
using var key = ECDsa.Create();
key.ImportFromPem(File.ReadAllText(args[4]));
var signed = unsigned with { Signature = Convert.ToBase64String(key.SignData(unsigned.SigningPayload(),
    HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) };
signed.Verify(); // Refuse an accidental release signed by the wrong key.
File.WriteAllText(args[5], JsonSerializer.Serialize(signed, LauncherRelease.Json) + "\n");
Console.WriteLine($"Signed launcher {signed.Version}, release {signed.Sequence}, {signed.Size} bytes.");
