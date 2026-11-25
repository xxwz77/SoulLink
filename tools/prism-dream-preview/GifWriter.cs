using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoulLink.PrismDreamPreview
{
    /// <summary>极简 GIF89a 编码器（含局部调色板 + 抖动），用于产出循环预览动图。</summary>
    internal static class GifWriter
    {
        public static void Write(string path, IList<BitmapSource> frames, int frameDelayMs)
        {
            if (frames.Count == 0) return;
            int w = frames[0].PixelWidth, h = frames[0].PixelHeight;
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs);

            bw.Write(new[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' });
            bw.Write((ushort)w); bw.Write((ushort)h);
            bw.Write((byte)0xF7);   // 全局表 256 色
            bw.Write((byte)0);      // 背景索引
            bw.Write((byte)0);      // 像素宽高比

            // 全局调色板：从所有帧统计出的固定色板（暗色渐变 + 高光），避免每帧闪烁
            var palette = BuildPalette(frames, 256);
            foreach (var c in palette) { bw.Write(c.R); bw.Write(c.G); bw.Write(c.B); }
            for (int i = palette.Count; i < 256; i++) { bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)0); }

            // Netscape 循环
            bw.Write((byte)0x21); bw.Write((byte)0xFF); bw.Write((byte)0x0B);
            bw.Write("NETSCAPE2.0"u8.ToArray());
            bw.Write((byte)3); bw.Write((byte)1); bw.Write((ushort)0); bw.Write((byte)0);

            int delayCs = Math.Max(2, frameDelayMs / 10);
            for (int f = 0; f < frames.Count; f++)
            {
                var indices = IndexFrame(frames[f], palette);
                // GCE
                bw.Write((byte)0x21); bw.Write((byte)0xF9); bw.Write((byte)4);
                bw.Write((byte)0x04);   // disposal=1 不清除
                bw.Write((ushort)delayCs);
                bw.Write((byte)0);      // 透明索引（无）
                bw.Write((byte)0);
                // Image descriptor
                bw.Write((byte)0x2C);
                bw.Write((ushort)0); bw.Write((ushort)0);
                bw.Write((ushort)w); bw.Write((ushort)h);
                bw.Write((byte)0);
                // LZW
                var data = Lzw(indices, 8);
                bw.Write((byte)8);
                int pos = 0;
                while (pos < data.Length)
                {
                    int n = Math.Min(255, data.Length - pos);
                    bw.Write((byte)n);
                    bw.Write(data, pos, n);
                    pos += n;
                }
                bw.Write((byte)0);
            }
            bw.Write((byte)0x3B);
        }

        private static List<Color> BuildPalette(IList<BitmapSource> frames, int size)
        {
            // 均匀撒点采样，再用 3-3-2 分桶聚合
            var buckets = new Dictionary<int, (long r, long g, long b, int n)>();
            int step = Math.Max(1, frames.Count / 8);
            for (int f = 0; f < frames.Count; f += step)
            {
                var px = Pixels(frames[f]);
                int stride = Math.Max(4, (px.Length / 4) / 24000) * 4;
                for (int i = 0; i + 2 < px.Length; i += stride)
                {
                    byte b = px[i], g = px[i + 1], r = px[i + 2];
                    int key = (r >> 5) << 6 | (g >> 5) << 3 | (b >> 6);
                    buckets.TryGetValue(key, out var cur);
                    buckets[key] = (cur.r + r, cur.g + g, cur.b + b, cur.n + 1);
                }
            }
            var list = new List<Color>();
            foreach (var kv in buckets)
            {
                var v = kv.Value;
                list.Add(Color.FromRgb((byte)(v.r / v.n), (byte)(v.g / v.n), (byte)(v.b / v.n)));
                if (list.Count >= size) break;
            }
            // 补足高亮色，保证闪光不过曝
            for (int i = 0; list.Count < size && i < 32; i++)
                list.Add(Color.FromRgb((byte)(120 + i * 4), (byte)(120 + i * 4), (byte)(130 + i * 3)));
            while (list.Count < size) list.Add(Colors.Black);
            return list;
        }

        private static byte[] Pixels(BitmapSource bmp)
        {
            var src = bmp.Format == PixelFormats.Bgra32 ? bmp : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int stride = src.PixelWidth * 4;
            var data = new byte[stride * src.PixelHeight];
            src.CopyPixels(data, stride, 0);
            return data;
        }

        private static byte[] IndexFrame(BitmapSource bmp, List<Color> palette)
        {
            var px = Pixels(bmp);
            int n = bmp.PixelWidth * bmp.PixelHeight;
            var outIdx = new byte[n];
            // 4-4-4 查找缓存
            var cache = new short[4096];
            for (int i = 0; i < cache.Length; i++) cache[i] = -1;
            for (int i = 0; i < n; i++)
            {
                byte b = px[i * 4], g = px[i * 4 + 1], r = px[i * 4 + 2];
                int key = (r >> 4) << 8 | (g >> 4) << 4 | (b >> 4);
                short idx = cache[key];
                if (idx < 0)
                {
                    int best = 0, bestD = int.MaxValue;
                    for (int p = 0; p < palette.Count; p++)
                    {
                        var c = palette[p];
                        int dr = c.R - r, dg = c.G - g, db = c.B - b;
                        int d = dr * dr * 30 + dg * dg * 59 + db * db * 11;
                        if (d < bestD) { bestD = d; best = p; }
                    }
                    idx = (short)best;
                    cache[key] = idx;
                }
                outIdx[i] = (byte)idx;
            }
            return outIdx;
        }

        private static byte[] Lzw(byte[] indices, int minCodeSize)
        {
            var output = new List<byte>(indices.Length / 2 + 64);
            int clearCode = 1 << minCodeSize;
            int endCode = clearCode + 1;
            int nextCode = endCode + 1;
            int codeSize = minCodeSize + 1;
            var dict = new Dictionary<int, int>();
            int bitBuffer = 0, bitCount = 0;

            void Emit(int code)
            {
                bitBuffer |= code << bitCount;
                bitCount += codeSize;
                while (bitCount >= 8)
                {
                    output.Add((byte)(bitBuffer & 0xFF));
                    bitBuffer >>= 8;
                    bitCount -= 8;
                }
            }

            Emit(clearCode);
            int prefix = indices[0];
            for (int i = 1; i < indices.Length; i++)
            {
                int k = indices[i];
                int key = (prefix << 8) | k;
                if (dict.TryGetValue(key, out int code))
                {
                    prefix = code;
                }
                else
                {
                    Emit(prefix);
                    if (nextCode < 4096)
                    {
                        dict[key] = nextCode++;
                        if (nextCode > (1 << codeSize) && codeSize < 12) codeSize++;
                    }
                    else
                    {
                        Emit(clearCode);
                        dict.Clear();
                        nextCode = endCode + 1;
                        codeSize = minCodeSize + 1;
                    }
                    prefix = k;
                }
            }
            Emit(prefix);
            Emit(endCode);
            if (bitCount > 0) output.Add((byte)(bitBuffer & 0xFF));
            return output.ToArray();
        }
    }
}