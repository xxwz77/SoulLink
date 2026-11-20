using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

/// <summary>
/// 由 1024x1024 PNG 生成多尺寸 ICO。
/// 每个尺寸以 PNG 形式内嵌（Vista+ 起 ICO 支持，体积小、透明通道无损）。
/// </summary>
public static class IconBuilder
{
    static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: IconBuilder <源PNG> <目标ICO>");
            return 2;
        }
        string srcPath = args[0], outPath = args[1];
        if (!File.Exists(srcPath)) { Console.WriteLine("源文件不存在: " + srcPath); return 2; }

        using var src = Image.FromFile(srcPath);
        Console.WriteLine("源图 = " + src.Width + "x" + src.Height);

        var blobs = new byte[Sizes.Length][];
        for (int i = 0; i < Sizes.Length; i++)
        {
            int s = Sizes[i];
            using var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Transparent);
                g.DrawImage(src, new Rectangle(0, 0, s, s));
            }
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            blobs[i] = ms.ToArray();
            Console.WriteLine("  " + s + "px = " + blobs[i].Length + " bytes");
        }

        using var outMs = new MemoryStream();
        using var bw = new BinaryWriter(outMs);
        bw.Write((ushort)0);                 // reserved
        bw.Write((ushort)1);                 // type = icon
        bw.Write((ushort)Sizes.Length);      // count

        int offset = 6 + 16 * Sizes.Length;
        for (int i = 0; i < Sizes.Length; i++)
        {
            int s = Sizes[i];
            bw.Write((byte)(s >= 256 ? 0 : s));   // width（256 记作 0）
            bw.Write((byte)(s >= 256 ? 0 : s));   // height
            bw.Write((byte)0);                    // color count
            bw.Write((byte)0);                    // reserved
            bw.Write((ushort)1);                  // planes
            bw.Write((ushort)32);                 // bit count
            bw.Write((uint)blobs[i].Length);
            bw.Write((uint)offset);
            offset += blobs[i].Length;
        }
        foreach (var b in blobs) bw.Write(b);
        bw.Flush();

        File.WriteAllBytes(outPath, outMs.ToArray());
        Console.WriteLine("已写入 " + outPath + "  " + new FileInfo(outPath).Length + " bytes");
        return 0;
    }
}
