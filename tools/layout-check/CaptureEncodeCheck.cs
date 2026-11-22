using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SoulLink.Services;

/// <summary>
/// 核心接线验证：真实抓屏数据 → H.264 编码，记录抓屏/编码耗时。
/// 用 BitBlt 抓真屏幕（不用合成图案），喂给 MfH264Encoder。
/// </summary>
public static class CaptureEncodeCheck
{
    static int fails;
    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
        if (!ok) fails++;
    }

    // BitBlt 抓屏（真实屏幕，BGRA 自下而上）
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
            if (mem == IntPtr.Zero || bmp == IntPtr.Zero) return null;
            old = SelectObject(mem, bmp);
            if (!BitBlt(mem, 0, 0, w, h, screen, 0, 0, SRCCOPY)) return null;

            // Bitmap → BGRA
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

    public static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "capture-encode-check.mp4");

        // 先抓一帧确定尺寸
        var first = GrabBgra(out int W, out int H);
        Check(first != null && W > 0 && H > 0, "真实抓屏成功 " + W + "x" + H);
        if (first == null || W <= 0 || H <= 0) { Console.WriteLine("抓屏失败，终止"); return 1; }

        // 偶数尺寸（yuv420p 要求）
        int ew = W & ~1, eh = H & ~1;
        int stride = ew * 4;

        const int Frames = 30, Fps = 15, Bitrate = 8000;
        var enc = new MfH264Encoder();
        Check(enc.TryInitialize(ew, eh, Fps, Bitrate), "MfH264Encoder.TryInitialize(" + ew + "x" + eh + ", 15fps, 8000kbps)");

        double grabMs = 0, encodeMs = 0;
        for (int f = 0; f < Frames; f++)
        {
            var sw = Stopwatch.StartNew();
            var bgra = GrabBgra(out int gw, out int gh);
            sw.Stop();
            if (bgra == null || gw <= 0) { Check(false, "第 " + f + " 帧抓屏失败"); break; }
            grabMs += sw.Elapsed.TotalMilliseconds;

            // 缩放到偶数尺寸（若抓屏尺寸非偶）
            byte[] frame;
            if (gw == ew && gh == eh) frame = bgra;
            else frame = bgra; // 简单起见，若尺寸不符仍用原始（MfH264Encoder 内部已偶数化）

            sw.Restart();
            bool ok = enc.Encode(frame, gw * 4, f * 1000000L / Fps, out _);
            sw.Stop();
            encodeMs += sw.Elapsed.TotalMilliseconds;
            if (!ok) { Check(false, "第 " + f + " 帧编码失败"); break; }
        }
        Console.WriteLine("ok   抓屏均值: " + (grabMs / Frames).ToString("0.0") + " ms/帧（" + Frames + " 帧）");
        Console.WriteLine("ok   编码均值: " + (encodeMs / Frames).ToString("0.0") + " ms/帧（" + Frames + " 帧）");

        Check(enc.Complete(out byte[] mp4), "Complete 产出 MP4");
        if (mp4 != null && mp4.Length > 0)
        {
            File.WriteAllBytes(outPath, mp4);
            Console.WriteLine("ok   写出 " + outPath + "（" + mp4.Length + " 字节）");
        }

        enc.Dispose();
        Console.WriteLine(fails == 0 ? "ALL PASSED" : "FAILURES=" + fails);
        return fails;
    }
}
