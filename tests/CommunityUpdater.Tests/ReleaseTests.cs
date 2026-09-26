using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cranberry.Launcher.Core;

namespace CommunityUpdater.Tests;

public sealed class ReleaseTests
{
    [Fact]
    public void CanonicalPayloadIsStableAcrossCulturesAndEveryIdentityFieldIsSigned()
    {
        using var fixture = new UpdateFixture();
        var release = fixture.Sign(new(1, 17, "1.2.3-preview.4", "win-x64", 1, 1, 99,
            new string('A', 64), new string('B', 64), ""));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Assert.Equal("cranberry-community-v1\n17\n1.2.3-preview.4\nwin-x64\n1\n1\n99\n"
                + new string('A', 64) + "\n" + new string('B', 64) + "\n", Encoding.UTF8.GetString(release.SigningPayload()));
            release.Verify(fixture.Trust.PublicKey);
            CommunityRelease[] modified =
            [
                release with { Sequence = 18 }, release with { Version = "1.2.4" }, release with { Size = 100 },
                release with { Sha256 = new string('C', 64) }, release with { FilesSha256 = new string('D', 64) },
                release with { Schema = 2 }, release with { Platform = "linux-x64" },
                release with { MinimumUpdaterVersion = 2 }, release with { DataSchema = 2 }
            ];
            foreach (var changed in modified) Assert.Throws<InvalidDataException>(() => changed.Verify(fixture.Trust.PublicKey));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void RejectsDifferentKeyMalformedSignatureAndNonP256Keys()
    {
        using var fixture = new UpdateFixture();
        var release = fixture.CreateBundle().Release;
        using var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<InvalidDataException>(() => release.Verify(Convert.ToBase64String(wrong.ExportSubjectPublicKeyInfo())));
        Assert.Throws<InvalidDataException>(() => (release with { Signature = new string('!', 88) }).Verify(fixture.Trust.PublicKey));
        using var larger = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<InvalidDataException>(() => release.Verify(Convert.ToBase64String(larger.ExportSubjectPublicKeyInfo())));
        Assert.Throws<InvalidDataException>(() => release.Verify(Convert.ToBase64String(
            Convert.FromBase64String(fixture.Trust.PublicKey).Concat(new byte[] { 0 }).ToArray())));
    }

    [Theory]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("owner/repo/extra")]
    [InlineData("owner/..")]
    [InlineData("owner/repo?token=secret")]
    [InlineData("owner/repo\\evil")]
    public void RejectsUnscopedRepository(string repository)
    {
        using var fixture = new UpdateFixture();
        Assert.Throws<InvalidDataException>(() => (fixture.Trust with { Repository = repository }).Validate());
    }

    [Fact]
    public void EmptyKeyIsOnlyAllowedForDisabledDevelopmentSettings()
    {
        var disabled = new CommunityUpdateSettings(1, "owner/repo", "", true, 0, "0.1.0");
        disabled.Validate();
        Assert.Throws<InvalidDataException>(() => new CommunityReleaseFeed(disabled));
        Assert.Throws<InvalidDataException>(() => (disabled with { Sequence = 1 }).Validate());
    }
}
