using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoulLink.Theme;

internal static class DesktopThemeArt
{
    private static readonly Dictionary<string, BitmapSource?> Previews = new(StringComparer.Ordinal);

    internal static BitmapSource? Portrait(string id, int decodeWidth = 0)
        => Still(id, decodeWidth);

    internal static BitmapSource? Still(string id, int decodeWidth = 0)
    {
        using Stream? stream = Resource(id, ".jpg");
        return stream == null ? null : Decode(stream, decodeWidth);
    }

    internal static BitmapSource? Preview(DesktopPalette palette)
    {
        if (palette.IsPlain) return null;
        if (!Previews.TryGetValue(palette.Id, out BitmapSource? preview))
        {
            preview = Still(palette.Id, 320);
            Previews.Add(palette.Id, preview);
        }
        return preview;
    }

    internal static Clip? Open(string id)
    {
        using Stream? stream = Resource(id, ".slframes");
        if (stream == null) return null;
        try
        {
            using var reader = new BinaryReader(stream);
            const int signature = 0x32464C53; // SLF2
            int marker = reader.ReadInt32();
            int rate = reader.ReadUInt16();
            int count = reader.ReadUInt16();
            int width = reader.ReadUInt16();
            int height = reader.ReadUInt16();
            if (marker != signature || rate is < 1 or > 30 || count is < 2 or > 120 ||
                width is < 320 or > 4096 || height is < 200 or > 4096) return null;
            var frames = new EncodedFrame[count];
            for (int index = 0; index < count; index++)
            {
                int length = reader.ReadInt32();
                if (length is < 64 or > 2097152 || length > stream.Length - stream.Position) return null;
                frames[index] = new EncodedFrame(stream.Position, length);
                stream.Position += length;
            }
            return new Clip(id, rate, width, height, frames);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static Stream? Resource(string id, string suffix) =>
        typeof(DesktopThemeArt).Assembly.GetManifestResourceStream("SoulLink.DesktopThemes." + id + suffix);

    private static BitmapSource Decode(Stream stream, int decodeWidth = 0)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    internal readonly record struct EncodedFrame(long Offset, int Length);

    internal sealed class Clip(string id, int framesPerSecond, int sourceWidth, int sourceHeight, EncodedFrame[] data)
    {
        private readonly int[] _cachedFrames = [-1, -1];
        private readonly BitmapSource?[] _buffer = new BitmapSource?[2];
        private BitmapSource? _basis;
        private byte[]? _reference;
        private int _decodeWidth;
        private int _nextSlot;

        internal int FrameCount => data.Length;
        internal int FrameRate => framesPerSecond;
        internal int Width => sourceWidth;
        internal double Aspect => (double)sourceWidth / sourceHeight;

        internal BitmapSource Frame(int frame, int decodeWidth)
        {
            if (_decodeWidth != decodeWidth)
            {
                _basis = null;
                _reference = null;
                _decodeWidth = decodeWidth;
                Array.Fill(_cachedFrames, -1);
                Array.Clear(_buffer);
            }
            var basis = _basis ??= ReadFrame(0);
            if (frame == 0) return basis;
            int previous = Array.IndexOf(_cachedFrames, frame);
            if (previous >= 0) return _buffer[previous]!;

            BitmapSource difference = ReadFrame(frame);
            int stride = difference.PixelWidth * 3;
            int length = stride * difference.PixelHeight;
            if (_reference == null)
            {
                _reference = new byte[length];
                CopyPixels(basis, _reference, stride);
            }
            byte[] reconstructed = ArrayPool<byte>.Shared.Rent(length);
            BitmapSource bitmap;
            try
            {
                CopyPixels(difference, reconstructed, stride);
                RestorePixels(_reference, reconstructed, length);
                bitmap = BitmapSource.Create(difference.PixelWidth, difference.PixelHeight, 96, 96, PixelFormats.Bgr24,
                    null, reconstructed, stride);
                bitmap.Freeze();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(reconstructed);
            }
            _cachedFrames[_nextSlot] = frame;
            _buffer[_nextSlot] = bitmap;
            _nextSlot = (_nextSlot + 1) % _buffer.Length;
            return bitmap;
        }

        private BitmapSource ReadFrame(int index)
        {
            EncodedFrame entry = data[index];
            using Stream stream = Resource(id, ".slframes") ?? throw new IOException("Theme artwork is missing.");
            stream.Position = entry.Offset;
            using var reader = new BinaryReader(stream);
            using var frame = new MemoryStream(reader.ReadBytes(entry.Length), writable: false);
            return Decode(frame, _decodeWidth < Width ? _decodeWidth : 0);
        }

        private static void CopyPixels(BitmapSource frame, byte[] buffer, int stride)
        {
            BitmapSource source = frame.Format == PixelFormats.Bgr24 ? frame
                : new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
            source.CopyPixels(buffer, stride, 0);
        }

        private static void RestorePixels(byte[] basis, byte[] destination, int length)
        {
            int index = 0;
            if (Sse2.IsSupported)
            {
                Vector128<byte> neutral = Vector128.Create((byte)128);
                for (; index <= length - Vector128<byte>.Count; index += Vector128<byte>.Count)
                {
                    Vector128<byte> original = Unsafe.ReadUnaligned<Vector128<byte>>(ref basis[index]);
                    Vector128<byte> difference = Unsafe.ReadUnaligned<Vector128<byte>>(ref destination[index]);
                    Vector128<byte> higher = Sse2.SubtractSaturate(difference, neutral);
                    Vector128<byte> lower = Sse2.SubtractSaturate(neutral, difference);
                    Vector128<byte> raised = Sse2.AddSaturate(original, Sse2.AddSaturate(higher, higher));
                    Unsafe.WriteUnaligned(ref destination[index],
                        Sse2.SubtractSaturate(raised, Sse2.AddSaturate(lower, lower)));
                }
            }
            for (; index < length; index++)
                destination[index] = (byte)Math.Clamp(basis[index] + (destination[index] - 128) * 2, 0, 255);
        }
    }
}
