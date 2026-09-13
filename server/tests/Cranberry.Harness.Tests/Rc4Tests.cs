using System.Text;
using Cranberry.Harness.Wire;

namespace Cranberry.Harness.Tests;

public sealed class Rc4Tests
{
    [Theory]
    // RFC 6229 §2 test vectors: the first 16 keystream bytes for two published keys. The harness's
    // RC4 is checked against the published algorithm, not against the server's implementation, so
    // that the two are genuinely independent opinions.
    [InlineData("0102030405", "b2396305f03dc027ccc3524a0a1118a8")]
    [InlineData("0102030405060708", "97ab8a1bf0afb96132f2f67258da15a8")]
    public void Keystream_matches_the_published_vectors(string keyHex, string expectedHex)
    {
        var rc4 = new HarnessRc4(Convert.FromHexString(keyHex));
        byte[] stream = new byte[16];
        rc4.Transform(stream);
        Assert.Equal(expectedHex.ToUpperInvariant(), Convert.ToHexString(stream));
        Assert.Equal(16, rc4.Position);
    }

    [Fact]
    public void Harness_and_server_ciphers_agree_byte_for_byte()
    {
        byte[] key = Convert.FromBase64String("F70IaxuU8C/w7FPXY1ibXw==");
        byte[] plain = Encoding.ASCII.GetBytes("ClientProtocol_1148 / 0.0.118.208059 — a long enough message to wrap the state table a few times.");

        byte[] byHarness = (byte[])plain.Clone();
        new HarnessRc4(key).Transform(byHarness);

        byte[] byServer = (byte[])plain.Clone();
        new Cranberry.Transport.Rc4Cipher(key).Transform(byServer);

        Assert.Equal(Convert.ToHexString(byServer), Convert.ToHexString(byHarness));
    }

    [Fact]
    public void The_keystream_runs_on_across_messages_rather_than_restarting()
    {
        // 967 -> @967 -> @968 -> @969 in wire-20260829-184346: one running stream per direction.
        byte[] key = [1, 2, 3, 4];
        var continuous = new HarnessRc4(key);
        byte[] whole = new byte[32];
        continuous.Transform(whole);

        var split = new HarnessRc4(key);
        byte[] first = new byte[10];
        byte[] second = new byte[22];
        split.Transform(first);
        split.Transform(second);

        Assert.Equal(Convert.ToHexString(whole), Convert.ToHexString([.. first, .. second]));
        Assert.Equal(32, split.Position);
    }

    [Fact]
    public void Transform_is_its_own_inverse()
    {
        byte[] key = [9, 9, 9];
        byte[] message = Encoding.ASCII.GetBytes("round trip");
        byte[] buffer = (byte[])message.Clone();
        new HarnessRc4(key).Transform(buffer);
        Assert.NotEqual(Convert.ToHexString(message), Convert.ToHexString(buffer));
        new HarnessRc4(key).Transform(buffer);
        Assert.Equal(Convert.ToHexString(message), Convert.ToHexString(buffer));
    }

    [Fact]
    public void An_empty_key_is_refused_rather_than_producing_a_null_cipher()
    {
        Assert.Throws<ArgumentException>(() => new HarnessRc4([]));
    }
}
