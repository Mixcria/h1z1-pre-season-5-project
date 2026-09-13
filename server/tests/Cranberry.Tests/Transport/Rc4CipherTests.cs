using System.Text;
using Cranberry.Transport;

namespace Cranberry.Tests.Transport;

public class Rc4CipherTests
{
    // Published RC4 test vectors (key, plaintext, ciphertext).
    [Theory]
    [InlineData("Key", "Plaintext", "BBF316E8D940AF0AD3")]
    [InlineData("Wiki", "pedia", "1021BF0420")]
    [InlineData("Secret", "Attack at dawn", "45A01F645FC35B383552544B9BF5")]
    public void MatchesPublishedVectors(string key, string plaintext, string expectedHex)
    {
        var cipher = new Rc4Cipher(Encoding.ASCII.GetBytes(key));
        byte[] data = Encoding.ASCII.GetBytes(plaintext);

        cipher.Transform(data);

        Assert.Equal(expectedHex, Convert.ToHexString(data));
        Assert.Equal(data.Length, cipher.Position);
    }

    [Fact]
    public void KeystreamIsContinuousAcrossCalls()
    {
        byte[] key = Convert.FromBase64String("F70IaxuU8C/w7FPXY1ibXw==");
        byte[] whole = new byte[1000];
        for (int i = 0; i < whole.Length; i++)
        {
            whole[i] = (byte)(i * 7);
        }

        byte[] split = (byte[])whole.Clone();

        new Rc4Cipher(key).Transform(whole);
        var piecewise = new Rc4Cipher(key);
        piecewise.Transform(split.AsSpan(0, 1));
        piecewise.Transform(split.AsSpan(1, 499));
        piecewise.Transform(split.AsSpan(500));

        Assert.Equal(whole, split);
    }

    [Fact]
    public void TransformIsItsOwnInverse()
    {
        byte[] key = [1, 2, 3, 4, 5];
        byte[] original = Encoding.ASCII.GetBytes("the quick brown fox");
        byte[] data = (byte[])original.Clone();

        new Rc4Cipher(key).Transform(data);
        Assert.NotEqual(original, data);
        new Rc4Cipher(key).Transform(data);

        Assert.Equal(original, data);
    }
}
