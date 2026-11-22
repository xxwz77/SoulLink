using System;
using System.IO;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

/// <summary>探针：Vortice.MediaFoundation 的 IMFTransform 逐帧 H.264 编码 → Annex-B NAL。</summary>
public static class NalEncoderProbe
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    const uint SRCCOPY = 0x00CC0020;

    static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00AA00389B71");
    static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");

    static ulong Pack(uint hi, uint lo) => ((ulong)hi << 32) | lo;

    static byte[]? GrabBgra(out int w, out int h)
    {
        w = 0; h = 0;
        IntPtr screen = IntPtr.Zero, mem = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            w = GetSystemMetrics(0); h = GetSystemMetrics(1);
            if (w <= 0 || h <= 0) return null;
            screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;
            mem = CreateCompatibleDC(screen);
            bmp = CreateCompatibleBitmap(screen, w, h);
            old = SelectObject(mem, bmp);
            if (!BitBlt(mem, 0, 0, w, h, screen, 0, 0, SRCCOPY)) return null;

            using var b = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = System.Drawing.Graphics.FromImage(b);
            IntPtr hdc = g.GetHdc();
            BitBlt(hdc, 0, 0, w, h, mem, 0, 0, SRCCOPY);
            g.ReleaseHdc(hdc);
            var data = b.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var arr = new byte[Math.Abs(data.Stride) * h];
                Marshal.Copy(data.Scan0, arr, 0, arr.Length);
                return arr;
            }
            finally { b.UnlockBits(data); }
        }
        catch { return null; }
        finally
        {
            if (mem != IntPtr.Zero && old != IntPtr.Zero) { try { SelectObject(mem, old); } catch { } }
            if (bmp != IntPtr.Zero) { try { DeleteObject(bmp); } catch { } }
            if (mem != IntPtr.Zero) { try { DeleteDC(mem); } catch { } }
            if (screen != IntPtr.Zero) { try { ReleaseDC(IntPtr.Zero, screen); } catch { } }
        }
    }

    public static int Main()
    {
        try
        {
            MediaFactory.MFStartup(false);
            Console.WriteLine("=== 1. 枚举 H.264 编码器 ===");
            // 编码器 MFT 的输入是 NV12/原始帧，输出是 H.264。枚举用「输出 H.264」匹配。
            var inInfo = new RegisterTypeInfo { GuidMajorType = MFMediaType_Video, GuidSubtype = MFVideoFormat_NV12 };
            var outInfo = new RegisterTypeInfo { GuidMajorType = MFMediaType_Video, GuidSubtype = MFVideoFormat_H264 };

            IMFTransform transform = null;
            var hwColl = MediaFactory.MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, 4, inInfo, outInfo); // 硬件
            var swColl = MediaFactory.MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, 3, inInfo, outInfo); // 软件
            int hwCount = 0, swCount = 0;
            foreach (var _ in hwColl) hwCount++;
            foreach (var _ in swColl) swCount++;
            Console.WriteLine("硬件 MFT 数=" + hwCount + " 软件 MFT 数=" + swCount);

            // ★ 硬件 MFT（NVENC）是异步 MFT，需要事件驱动；第一步先用软件 MFT（同步），
            //   跑通「逐帧 NAL」这条链，硬件异步是后续优化。
            foreach (var act in swColl) { transform = act.ActivateObject<IMFTransform>(); break; }
            if (transform == null) foreach (var act in hwColl) { transform = act.ActivateObject<IMFTransform>(); break; }
            if (transform == null) { Console.WriteLine("FAIL: 没拿到 IMFTransform"); return 1; }
            Console.WriteLine("=== 2. 拿到 IMFTransform ===");

            var outMediaType = MediaFactory.MFCreateMediaType();
            outMediaType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            outMediaType.Set(MF_MT_SUBTYPE, MFVideoFormat_H264);
            outMediaType.Set(MF_MT_AVG_BITRATE, 8_000_000u);
            outMediaType.Set(MF_MT_INTERLACE_MODE, 2u);
            outMediaType.Set(MF_MT_FRAME_SIZE, Pack(1280, 720));
            outMediaType.Set(MF_MT_FRAME_RATE, Pack(15, 1));
            outMediaType.Set(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            transform.SetOutputType(0, outMediaType, 0);
            Console.WriteLine("SetOutputType OK");

            var inMediaType = MediaFactory.MFCreateMediaType();
            inMediaType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            inMediaType.Set(MF_MT_SUBTYPE, MFVideoFormat_NV12);
            inMediaType.Set(MF_MT_INTERLACE_MODE, 2u);
            inMediaType.Set(MF_MT_FRAME_SIZE, Pack(1280, 720));
            inMediaType.Set(MF_MT_FRAME_RATE, Pack(15, 1));
            inMediaType.Set(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            transform.SetInputType(0, inMediaType, 0);
            Console.WriteLine("SetInputType OK");

            transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

            // ★ 输出流要求我们自己提供 sample（MFT_OUTPUT_STREAM_PROVIDES_SAMPLES）
            var outStreamInfo = transform.GetOutputStreamInfo(0);
            Console.WriteLine("输出流 flags=0x" + outStreamInfo.Flags.ToString("X8") + " size=" + outStreamInfo.Size);

            Console.WriteLine("=== 3. 逐帧编码（真实抓屏 → NV12 → ProcessInput → ProcessOutput）===");
            int frameCount = 0, nalFrames = 0;
            long totalNal = 0;
            for (int f = 0; f < 30; f++)
            {
                var bgra = GrabBgra(out int w, out int h);
                if (bgra == null) break;
                var nv12 = BgraToNv12(bgra, w, h);

                var sample = MediaFactory.MFCreateSample();
                var buf = MediaFactory.MFCreateMemoryBuffer(nv12.Length);
                buf.Lock(out IntPtr pData, out _, out _);
                Marshal.Copy(nv12, 0, pData, nv12.Length);
                buf.Unlock();
                buf.CurrentLength = nv12.Length;
                sample.AddBuffer(buf);
                sample.SampleTime = f * 666666L;
                sample.SampleDuration = 666666L;

                transform.ProcessInput(0, sample, 0);

                // 输出 sample 由我们自己提供
                var outBuf = new OutputDataBuffer();
                outBuf.StreamID = 0;
                int outSize = Math.Max(outStreamInfo.Size, 1920 * 1080 * 3 / 2);
                var outSample = MediaFactory.MFCreateSample();
                var outMemBuf = MediaFactory.MFCreateMemoryBuffer(outSize);
                outMemBuf.CurrentLength = 0;
                outSample.AddBuffer(outMemBuf);
                outBuf.Sample = outSample;

                var status = new ProcessOutputStatus();
                while (true)
                {
                    var r = transform.ProcessOutput(ProcessOutputFlags.None, 1, ref outBuf, out status);
                    if (!r.Success) break;  // 无更多输出 / 需更多输入
                    if (outBuf.Sample == null) break;
                    var nals = ExtractNals(outBuf.Sample);
                    if (nals != null && nals.Length > 0) { nalFrames++; totalNal += nals.Length; }
                    outBuf.Sample = null;
                }
                frameCount++;
            }
            Console.WriteLine("=== 结果 ===");
            Console.WriteLine("  喂入 " + frameCount + " 帧，产出 NAL 的帧 " + nalFrames + " 帧，NAL 字节 " + totalNal);
            Console.WriteLine((nalFrames > 0 ? "PASS: 拿到逐帧 NAL" : "FAIL: 没拿到 NAL"));
            return nalFrames > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
        finally
        {
            try { MediaFactory.MFShutdown(); } catch { }
        }
    }

    static byte[] BgraToNv12(byte[] bgra, int w, int h)
    {
        int ySize = w * h;
        var nv12 = new byte[w * h * 3 / 2];
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            int yRow = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                nv12[yRow + x] = (byte)((66 * r + 129 * g + 25 * b + 128) >> 8);
            }
        }
        for (int i = ySize; i < nv12.Length; i++) nv12[i] = 128;
        return nv12;
    }

    static byte[]? ExtractNals(IMFSample sample)
    {
        var buf = sample.ConvertToContiguousBuffer();
        if (buf == null) return null;
        int len = buf.CurrentLength;
        var bytes = new byte[len];
        buf.Lock(out IntPtr pData, out _, out _);
        Marshal.Copy(pData, bytes, 0, len);
        buf.Unlock();
        return bytes;
    }
}
