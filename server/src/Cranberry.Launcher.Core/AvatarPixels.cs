using System.Security.Cryptography;

namespace Cranberry.Launcher.Core;

/// <summary>Fixed-size RGB pixels: safe to decode without image codecs on the server or the August client.</summary>
public static class AvatarPixels
{
    public const int Size = 48, HexLength = Size * Size * 6;
    public static string Validate(string? pixels)
    {
        if (pixels is null || (pixels.Length != 0 && (pixels.Length != HexLength || !pixels.All(char.IsAsciiHexDigit))))
            throw new InvalidOperationException("Select a valid profile picture in the launcher.");
        return pixels.ToUpperInvariant();
    }
    public static string Version(string pixels) => pixels.Length == 0 ? "" :
        Convert.ToHexString(SHA256.HashData(Convert.FromHexString(pixels)))[..24];
}
