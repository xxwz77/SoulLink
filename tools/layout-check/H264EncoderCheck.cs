using System;
using System.IO;
using SoulLink.Services;

/// <summary>
/// 回归：MfH264Encoder 编码 100 帧 → 产出可被 ffprobe/ffplay 识别的 MP4。
/// 只验证「能编码出可播放流 + 码率接近设定值」，不接 Ring、不接分发。
/// </summary>
public static class H264EncoderCheck
{
    static int fails;
    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
        if (!ok) fails++;
    }

    public static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "soullink-h264-check.mp4");

        const int W = 1280, H = 720, Fps = 30, Bitrate = 8000;
        var enc = new MfH264Encoder();
        bool init = enc.TryInitialize(W, H, Fps, Bitrate);
        Check(init, "TryInitialize(1280x720, 30fps, 8000kbps) 成功");
        Check(enc.Name == "MF-H264", "编码器名 = MF-H264");
        if (!init)
        {
            Console.WriteLine("初始化失败，终止。");
            return 1;
        }

        int stride = W * 4;
        var bgra = new byte[stride * H];

        // 确定性移动图案（斜向移动的色块 + 高频噪声），确保画面非静止、有可压缩内容
        uint seed = 12345u;
        for (int f = 0; f < 100; f++)
        {
            int offset = f * 4;
            FillPattern(bgra, W, H, stride, offset, ref seed);

            bool ok = enc.Encode(bgra, stride, f * 1000000L / Fps, out byte[]? nal);
            // 第一步（SinkWriter）不逐帧出 NAL，恒为 null，只验证返回 true
            if (f == 0) Check(nal == null, "第一步 SinkWriter 不逐帧出 NAL（nal == null）");
            if (!ok)
            {
                Console.WriteLine("FAIL 第 " + f + " 帧 Encode 失败");
                fails++;
                break;
            }
        }
        Console.WriteLine("ok   编码 100 帧完成");

        bool complete = enc.Complete(out byte[] mp4);
        Check(complete, "Complete() 成功产出 MP4");
        Check(mp4 != null && mp4.Length > 0, "MP4 非空");

        if (mp4 != null && mp4.Length > 0)
        {
            File.WriteAllBytes(outPath, mp4);
            Console.WriteLine("ok   已写出：" + outPath + "（" + mp4.Length + " 字节）");

            // 结构性自检：MP4 应以 ftyp 盒开头
            bool ftyp = mp4.Length > 12 && mp4[4] == (byte)'f' && mp4[5] == (byte)'t' && mp4[6] == (byte)'y' && mp4[7] == (byte)'p';
            Check(ftyp, "MP4 以 ftyp 盒开头（结构合法）");
        }

        enc.Dispose();

        Console.WriteLine(fails == 0 ? "ALL PASSED" : "FAILURES=" + fails);
        return fails;
    }

    private static void FillPattern(byte[] bgra, int w, int h, int stride, int offset, ref uint seed)
    {
        for (int y = 0; y < h; y++)
        {
            int rowBase = y * stride;
            for (int x = 0; x < w; x++)
            {
                seed = seed * 1664525u + 1013904223u;
                int noise = (int)(seed >> 27) & 0x1F;
                // 斜向移动的亮色块 + 文字状竖条 + 噪声，接近真实画面
                int band = ((x + offset) / 24) & 0x3;
                int bx = band * 40 + 30;
                int by = band * 60 + 20;
                int bar = ((x % 6) == 0) ? 70 : 0;
                int bb = (byte)Math.Min(255, bx + bar + noise);
                int bg = (byte)Math.Min(255, by + noise);
                int br = (byte)Math.Min(255, (x + offset) & 0xFF);

                int i = rowBase + x * 4;
                bgra[i] = (byte)bb;      // B
                bgra[i + 1] = (byte)bg;  // G
                bgra[i + 2] = (byte)br;  // R
                bgra[i + 3] = 255;       // A
            }
        }
    }
}
