using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoulLink.Services;

internal static class CustomArtworkStore
{
    private const int MaximumEdge = 4096;
    private const long MaximumBytes = 128 * 1024 * 1024;
    private static string DirectoryPath => Path.Combine(ConfigStore.ConfigDir, "custom-artwork");

    internal static string? PathFor(string? key)
    {
        if (key is not { Length: 64 }) return null;
        foreach (char digit in key)
            if (!Uri.IsHexDigit(digit)) return null;
        return Path.Combine(DirectoryPath, key.ToLowerInvariant() + ".png");
    }

    internal static BitmapSource? TryLoad(string? key)
    {
        var path = PathFor(key);
        if (path == null) return null;
        try { return LoadImage(path); }
        catch (Exception ex) when (IsFileError(ex)) { return null; }
    }

    internal static bool IsFileError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or NotSupportedException or InvalidOperationException or ArgumentException or FormatException or COMException;

    internal static BitmapSource LoadImage(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("图片文件超过 128 MB。");
        var frames = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None).Frames;
        if (frames.Count == 0) throw new InvalidDataException("图片中没有可读取的画面。");
        BitmapFrame frame = frames[0];
        int width = frame.PixelWidth, height = frame.PixelHeight;
        int longest = Math.Max(width, height);
        if (width < 1 || height < 1 || longest > 100_000 || (long)width * height > 100_000_000)
            throw new InvalidDataException("图片像素尺寸超出支持范围。");
        int orientation = GetOrientation(frame);
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.StreamSource = stream;
        if (longest > MaximumEdge)
        {
            double scale = (double)MaximumEdge / longest;
            bitmap.DecodePixelWidth = Math.Max(1, (int)Math.Round(width * scale));
            bitmap.DecodePixelHeight = Math.Max(1, (int)Math.Round(height * scale));
        }
        bitmap.EndInit();
        bitmap.Freeze();
        BitmapSource normalized = orientation switch
        {
            2 => new TransformedBitmap(bitmap, new ScaleTransform(-1, 1)),
            3 => new TransformedBitmap(bitmap, new RotateTransform(180)),
            4 => new TransformedBitmap(bitmap, new ScaleTransform(1, -1)),
            5 => new TransformedBitmap(bitmap, FlipAndRotate(270)),
            6 => new TransformedBitmap(bitmap, new RotateTransform(90)),
            7 => new TransformedBitmap(bitmap, FlipAndRotate(90)),
            8 => new TransformedBitmap(bitmap, new RotateTransform(270)),
            _ => bitmap
        };
        if (!normalized.IsFrozen) normalized.Freeze();
        return normalized;
    }

    private static TransformGroup FlipAndRotate(int degrees) => new()
    {
        Children = { new ScaleTransform(-1, 1), new RotateTransform(degrees) }
    };

    private static int GetOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata)
            {
                if (metadata.GetQuery("/app1/ifd/{ushort=274}") is ushort jpeg) return jpeg;
                if (metadata.GetQuery("/ifd/{ushort=274}") is ushort tiff) return tiff;
            }
        }
        catch (Exception ex) when (IsFileError(ex)) { }
        return 1;
    }

    internal static string Save(BitmapSource image)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = Path.Combine(DirectoryPath, $"{Guid.NewGuid():N}.tmp");
        try
        {
            string key;
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                var encoder = new PngBitmapEncoder();
                // Persist pixels without original file metadata, including EXIF location or orientation tags.
                var pixels = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
                encoder.Frames.Add(BitmapFrame.Create(pixels, null, null, null));
                encoder.Save(stream);
                stream.Position = 0;
                key = Convert.ToHexStringLower(SHA256.HashData(stream));
            }
            // Promote a complete file atomically. Re-choosing a picture also repairs a damaged copy.
            File.Move(temporary, PathFor(key)!, overwrite: true);
            return key;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (IsFileError(ex)) { }
        }
    }
}
