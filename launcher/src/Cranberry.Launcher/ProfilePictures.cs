using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Cranberry.Launcher.Core;

namespace Cranberry.Launcher;

internal static class ProfilePictures
{
    public static string Load(string path)
    {
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidOperationException("Choose a picture smaller than 8 MB.");
        using var stream = File.OpenRead(path);
        using var source = Image.FromStream(stream, true, false);
        if (source.Width > 4096 || source.Height > 4096) throw new InvalidOperationException("Choose a picture no larger than 4096 × 4096 pixels.");
        if (source.RawFormat.Guid != ImageFormat.Jpeg.Guid && source.RawFormat.Guid != ImageFormat.Png.Guid)
            throw new InvalidOperationException("Choose a PNG or JPEG picture.");
        if (source.PropertyIdList.Contains(0x112) && source.GetPropertyItem(0x112)?.Value is { Length: >= 2 } orientation)
            source.RotateFlip(BitConverter.ToUInt16(orientation) switch
            {
                2 => RotateFlipType.RotateNoneFlipX, 3 => RotateFlipType.Rotate180FlipNone, 4 => RotateFlipType.Rotate180FlipX,
                5 => RotateFlipType.Rotate90FlipX, 6 => RotateFlipType.Rotate90FlipNone, 7 => RotateFlipType.Rotate270FlipX,
                8 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone
            });
        using var square = new Bitmap(AvatarPixels.Size, AvatarPixels.Size, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(square))
        {
            graphics.Clear(Color.FromArgb(28, 30, 33));
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            int crop = Math.Min(source.Width, source.Height);
            graphics.DrawImage(source, new Rectangle(0, 0, square.Width, square.Height),
                new Rectangle((source.Width - crop) / 2, (source.Height - crop) / 2, crop, crop), GraphicsUnit.Pixel);
        }
        var bytes = new byte[AvatarPixels.HexLength / 2];
        for (int y = 0, at = 0; y < square.Height; y++)
            for (int x = 0; x < square.Width; x++)
            {
                Color c = square.GetPixel(x, y);
                bytes[at++] = c.R; bytes[at++] = c.G; bytes[at++] = c.B;
            }
        return Convert.ToHexString(bytes);
    }

    public static Bitmap? Decode(string pixels)
    {
        AvatarPixels.Validate(pixels);
        if (pixels.Length == 0) return null;
        byte[] bytes = Convert.FromHexString(pixels);
        var result = new Bitmap(AvatarPixels.Size, AvatarPixels.Size);
        for (int y = 0, at = 0; y < result.Height; y++)
            for (int x = 0; x < result.Width; x++, at += 3)
                result.SetPixel(x, y, Color.FromArgb(bytes[at], bytes[at + 1], bytes[at + 2]));
        return result;
    }
}
