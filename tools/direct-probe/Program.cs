// ============================================================================
// direct-probe —— 独立原生 H.264 直连客户端探针（2026-11-24）
// 目的：绕开浏览器，直连 agent /stream?v=2，MF 解码 + GDI 上屏，
//       测 t0收包 / t1解码 / t2上屏 三段耗时 → 判定延迟在浏览器还是服务端。
// 计时口径：
//   t0 = 一帧字节全部从 socket 读齐（4B 长度前缀+数据 read 完成）
//   t1 = MF ProcessOutput 返回该帧 NV12（解码完成）
//   t2 = SetDIBitsToDevice 完成（上屏）
//   frameGap = 相邻帧 t0 差（帧到达节奏，反映服务端出帧率）
// 用法：
//   direct-probe <url> [--duration 15] [--max-frames N]
//   DIRECT_PROBE_NO_WINDOW=1 → 无头（只测收包+解码，不上屏）
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DirectProbe;

internal static class Program
{
    internal static readonly List<double> DecodeMs = new();
    internal static readonly List<double> RenderMs = new();
    internal static readonly List<double> FrameGapMs = new();
    internal static long TotalBytes, TotalFrames;

    static int Main(string[] args)
    {
        int fileIdx = Array.IndexOf(args, "--file");
        string url = fileIdx >= 0 && fileIdx + 1 < args.Length ? args[fileIdx + 1]
            : args.FirstOrDefault(a => !a.StartsWith("--")) ?? "";
        int durationSec = GetOpt(args, "--duration", 15);
        int maxFrames = GetOpt(args, "--max-frames", 0);
        bool noWindow = Environment.GetEnvironmentVariable("DIRECT_PROBE_NO_WINDOW") == "1";
        if (url.Length == 0)
        {
            Console.WriteLine("用法: direct-probe <stream-url(token&v=2) | --file path.h264> [--duration 15] [--max-frames 0]");
            return 1;
        }
        if (fileIdx >= 0)
        {
            return RunFileProbe(url, durationSec);
        }
        bool useFfmpegDecode = Array.IndexOf(args, "--decode-ffmpeg") >= 0;
        if (useFfmpegDecode) return RunFfmpegDecodeProbe(url, durationSec, noWindow);

        Window? win = null;
        MfDecoder? decoder = null;
        try
        {
            if (!noWindow) win = new Window("direct-probe（可随时关窗口结束）", 960, 540);
            long tReq = Stopwatch.GetTimestamp();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSec + 60));
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var resp = http.GetStreamAsync(url, cts.Token).GetAwaiter().GetResult();
            Console.WriteLine("[probe] HTTP 200，连接+TTFB=" +
                ((Stopwatch.GetTimestamp() - tReq) * 1000.0 / Stopwatch.Frequency).ToString("0.0") + "ms");

            var buf = new byte[8 << 20];
            var sw = Stopwatch.StartNew();
            long lastT0 = 0, firstT0 = 0;
            int frames = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                if (win != null && win.ClosedByUser) break;
                if (!ReadExactly(resp, buf, 4, cts.Token)) { Console.WriteLine("[probe] 流结束"); break; }
                int len = (buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3];
                if (len <= 0 || len > buf.Length) { Console.WriteLine("[probe] 坏帧长度 " + len); break; }
                if (!ReadExactly(resp, buf, len, cts.Token)) { Console.WriteLine("[probe] 流中断"); break; }

                long t0 = Stopwatch.GetTimestamp();
                if (firstT0 == 0)
                {
                    firstT0 = t0;
                    // 诊断：dump 首帧前 32 字节（验证 Annex-B 起始码）
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < Math.Min(32, len); i++) sb.Append(buf[i].ToString("X2")).Append(' ');
                    Console.WriteLine("[probe] 首帧 len=" + len + " 头 32B: " + sb.ToString());
                }
                TotalBytes += 4 + len;
                if (lastT0 > 0) FrameGapMs.Add((t0 - lastT0) * 1000.0 / Stopwatch.Frequency);
                lastT0 = t0;

                decoder ??= new MfDecoder();
                foreach (var frame in decoder.Decode(buf, len))
                {
                    long t1 = frame.DecodedAt;
                    frames++; TotalFrames++;
                    if (win != null)
                    {
                        win.Blit(frame.Data, frame.Width, frame.Height, frame.Stride);
                        long t2 = Stopwatch.GetTimestamp();
                        RenderMs.Add((t2 - t1) * 1000.0 / Stopwatch.Frequency);
                    }
                    DecodeMs.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);
                }

                if (sw.Elapsed.TotalSeconds >= durationSec) break;
                if (maxFrames > 0 && frames >= maxFrames) break;
            }

            Console.WriteLine();
            Console.WriteLine("════ direct-probe 汇总 ════");
            Console.WriteLine("帧数=" + frames + "  字节=" + (TotalBytes / 1024) + "KB  时长=" + sw.Elapsed.TotalSeconds.ToString("0.0") + "s");
            double spanS = (lastT0 - firstT0) * 1000.0 / Stopwatch.Frequency / 1000.0;
            if (frames > 1 && spanS > 0.5)
                Console.WriteLine("实测帧率(解码出帧)=" + (frames / spanS).ToString("0.0") + " fps；收包帧率=" + (FrameGapMs.Count > 1 ? (1000.0 / (FrameGapMs.Sum() / FrameGapMs.Count)).ToString("0.0") : "n/a") + " fps");
            Dump("解码 t1-t0 (ms)", DecodeMs);
            Dump("上屏 t2-t1 (ms)", RenderMs);
            Dump("帧到达间隔 gap (ms)", FrameGapMs);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[probe] 失败：" + ex.GetType().Name + ": " + ex.Message);
            return 2;
        }
        finally
        {
            decoder?.Dispose();
            win?.Dispose();
        }
    }

    static int RunFileProbe(string path, int durationSec)
    {
        // 本地 framed h264 文件探针（诊断用：验证 MFT 解码路径）
        if (!System.IO.File.Exists(path)) { Console.WriteLine("文件不存在"); return 1; }
        var all = System.IO.File.ReadAllBytes(path);
        Console.WriteLine("[file] " + path + " " + all.Length + "B");
        var decoder = new MfDecoder();
        int off = 0, frames = 0;
        while (off + 4 <= all.Length)
        {
            int len = (all[off] << 24) | (all[off + 1] << 16) | (all[off + 2] << 8) | all[off + 3];
            off += 4;
            if (len <= 0 || off + len > all.Length) break;
            var au = new byte[len];
            Array.Copy(all, off, au, 0, len);
            off += len;
            int got = 0;
            foreach (var f in decoder.Decode(au, len)) { got++; frames++; }
            Console.WriteLine("[file] frame#" + frames + " in_len=" + len + " decoded=" + got + " off=" + off);
        }
        Console.WriteLine("[file] 解出帧数=" + frames);
        decoder.Dispose();
        return frames > 0 ? 0 : 3;
    }

    /// <summary>解码计时模式：收到的帧（剥 4B 前缀后）管道喂 ffmpeg 解码，对齐 showinfo 帧处理时刻测 t1（解码完成）。</summary>
    static int RunFfmpegDecodeProbe(string url, int durationSec, bool noWindow)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = "-loglevel info -f h264 -i pipe:0 -vf showinfo -f null - ",
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var ffmpeg = System.Diagnostics.Process.Start(psi)!;
        // 后台读 stderr（showinfo 行）
        var decodeEvents = new System.Collections.Concurrent.ConcurrentQueue<(int frameIdx, long wallclock)>();
        var errTask = Task.Run(() =>
        {
            string? line;
            while ((line = ffmpeg.StandardError.ReadLine()) != null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, "n:\\s*(\\d+)\\s+pts:");
                if (m.Success)
                {
                    decodeEvents.Enqueue((int.Parse(m.Groups[1].Value), Stopwatch.GetTimestamp()));
                }
            }
        });
        try
        {
            long tReq = Stopwatch.GetTimestamp();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSec + 60));
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var resp = http.GetStreamAsync(url, cts.Token).GetAwaiter().GetResult();
            Console.WriteLine("[probe] HTTP 200 TTFB=" + ((Stopwatch.GetTimestamp() - tReq) * 1000.0 / Stopwatch.Frequency).ToString("0.0") + "ms");
            var stdin = ffmpeg.StandardInput.BaseStream;
            var buf = new byte[8 << 20];
            var sw = Stopwatch.StartNew();
            var frameSentAt = new List<(int idx, long t0)>();
            int frames = 0;
            long lastT0 = 0, firstT0 = 0;
            while (sw.Elapsed.TotalSeconds < durationSec)
            {
                if (!ReadExactly(resp, buf, 4, cts.Token)) break;
                int len = (buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3];
                if (len <= 0 || len > buf.Length) break;
                if (!ReadExactly(resp, buf, len, cts.Token)) break;
                long t0 = Stopwatch.GetTimestamp();
                if (firstT0 == 0) firstT0 = t0;
                TotalBytes += 4 + len;
                if (lastT0 > 0) FrameGapMs.Add((t0 - lastT0) * 1000.0 / Stopwatch.Frequency);
                lastT0 = t0;
                // 剥 4B 前缀写裸 Annex-B 给 ffmpeg
                stdin.Write(buf, 0, len);
                frameSentAt.Add((frames, t0));
                frames++;
            }
            try { stdin.Close(); } catch { }
            ffmpeg.WaitForExit(5000);
            // 对齐：frame idx i 的解码完成时刻 - 发送时刻
            var dict = frameSentAt.ToDictionary(x => x.idx, x => x.t0);
            var decodeLat = new List<double>();
            while (decodeEvents.TryDequeue(out var ev))
            {
                if (dict.TryGetValue(ev.frameIdx, out long sentAt))
                {
                    double ms = (ev.wallclock - sentAt) * 1000.0 / Stopwatch.Frequency;
                    if (ms >= 0 && ms < 5000) decodeLat.Add(ms);
                }
            }
            Console.WriteLine();
            Console.WriteLine("════ direct-probe（ffmpeg 解码模式）════");
            Console.WriteLine("帧数=" + frames + " 字节=" + (TotalBytes / 1024) + "KB 时长=" + sw.Elapsed.TotalSeconds.ToString("0.0") + "s");
            if (frames > 1 && lastT0 > firstT0)
                Console.WriteLine("实测帧率=" + (frames / ((lastT0 - firstT0) * 1000.0 / Stopwatch.Frequency)).ToString("0.0") + " fps");
            Dump("解码+排队 t1-t0 (ms)", decodeLat);
            Dump("帧到达间隔 gap (ms)", FrameGapMs);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[probe] 失败：" + ex.GetType().Name + ": " + ex.Message);
            return 2;
        }
        finally
        {
            try { if (!ffmpeg.HasExited) ffmpeg.Kill(); } catch { }
        }
    }

    static int GetOpt(string[] args, string name, int def)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name && int.TryParse(args[i + 1], out int v)) return v;
        return def;
    }

    static bool ReadExactly(System.IO.Stream s, byte[] buf, int count, CancellationToken ct)
    {
        int got = 0;
        while (got < count)
        {
            int n = s.Read(buf, got, count - got);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    internal static void Dump(string label, List<double> xs)
    {
        if (xs.Count == 0) { Console.WriteLine(label + ": 无样本"); return; }
        var s = xs.OrderBy(v => v).ToList();
        double P(double q) => s[(int)Math.Clamp(Math.Floor((s.Count - 1) * q), 0, s.Count - 1)];
        Console.WriteLine(label + ": p50=" + P(0.5).ToString("0.00") + " p95=" + P(0.95).ToString("0.00")
            + " max=" + s[^1].ToString("0.00") + " (n=" + s.Count + ")");
    }
}
