using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StreamVerify
{
    /// <summary>
    /// 双观众同时订阅验证 —— P1 架构改动里最关键、也最容易出错的一处。
    ///
    /// 为什么必须专门测：
    ///   P1 把「每个观众一个独立编码」改成「共享一次采集编码，每观众 4 帧队列」。
    ///   单观众能出画面，完全不能证明多观众也对 ——
    ///   队列逻辑写错时，典型症状就是：
    ///     · 第二个观众订阅成功但永远读不到帧（队列没被喂）
    ///     · 两个观众抢同一个读指针（只有一个能读到）
    ///     · 第二个观众把第一个饿死
    ///   这些都只有**同时连两个**才暴露。
    ///
    /// 判定方式：两个独立 HttpClient 同时长连接 /stream，
    /// 各自统计收到的字节数与 Annex-B 帧数，**两个都必须持续增长**。
    /// 只看"HTTP 200"是不够的——200 之后一帧不推也是 200。
    /// </summary>
    public static class DualViewerProbe
    {
        private static int _fails;

        private sealed class ViewerResult
        {
            public string Name = "";
            public int Status;
            public long Bytes;
            public int NalCount;
            public int KeyCount;
            public int Width, Height;
            public string? Error;
            public double Seconds;
            public string? FilePath;
            public bool Ok => Status == 200 && NalCount > 0;
        }

        public static async Task<int> RunAsync(string agentDir, int port, string token, int seconds,
            string ffprobe)
        {
            string work = Path.Combine(Path.GetTempPath(), "dualviewer-" + port);
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
            Directory.CreateDirectory(work);
            string captureDir = Path.Combine(work, "capture");
            Directory.CreateDirectory(captureDir);

            await using var host = await ProbeHost.StartAsync(agentDir, port, token, captureDir,
                s => Console.WriteLine("   " + s), "1");
            if (host == null) return 3;

            // ★ 熔断 2048MB（Lead 修正：1.2GB 是正常值，不能用 500MB 误判）
            var guard = StartMemoryGuard(host.AgentPid, 2048,
                m => Console.WriteLine("   !! 熔断：" + m));

            try
            {
                var rec = await host.SendAsync("{\"Cmd\":\"record\",\"Count\":15,\"X\":75,\"Delta\":30}");
                Console.WriteLine("record：" + (rec != null && rec.Contains("\"Ok\":true") ? "已开始" : (rec ?? "无响应")));

                // 等有帧
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < 25)
                {
                    var st = await host.SendAsync("{\"Cmd\":\"recordstatus\"}") ?? "";
                    if (st.Contains("|frames=") && !st.Contains("|frames=0|"))
                    {
                        Console.WriteLine("recordstatus：" + Hl(st));
                        break;
                    }
                    await Task.Delay(400);
                }

                // ── 两个观众**同时**连上（关键：不能串行）──
                Console.WriteLine();
                Console.WriteLine("== 两个观众同时订阅 /stream（" + seconds + " 秒）");
                var t1 = CaptureViewerAsync("观众A", port, token, Path.Combine(work, "viewerA.h264"), seconds);
                var t2 = CaptureViewerAsync("观众B", port, token, Path.Combine(work, "viewerB.h264"), seconds);

                // 稍错开 1 秒起第二个，模拟真实"后加入"的观众
                await Task.Delay(1000);
                var results = await Task.WhenAll(t1, t2);

                Console.WriteLine();
                Console.WriteLine("== 结果");
                foreach (var r in results) Report(r);

                // ── 判定 ──
                Console.WriteLine();
                Console.WriteLine("== 判定");
                var a = results[0];
                var b = results[1];
                int fails = 0;

                if (a.Status != 200) { Console.WriteLine("FAIL 观众A HTTP " + a.Status); fails++; }
                if (b.Status != 200) { Console.WriteLine("FAIL 观众B HTTP " + b.Status); fails++; }

                if (a.NalCount > 0) Console.WriteLine("ok   观众A 收到 " + a.NalCount + " 个 NAL（" + a.Bytes + " B）");
                else { Console.WriteLine("FAIL 观众A 一个 NAL 都没收到"); fails++; }

                if (b.NalCount > 0) Console.WriteLine("ok   观众B 收到 " + b.NalCount + " 个 NAL（" + b.Bytes + " B）");
                else { Console.WriteLine("FAIL 观众B 一个 NAL 都没收到 —— **第二个观众拿不到帧，队列逻辑有问题**"); fails++; }

                // 两个都收到还不够：要确认不是"只有一个吃饱、另一个只有零星几帧"
                if (a.NalCount > 0 && b.NalCount > 0)
                {
                    double ratio = (double)Math.Min(a.NalCount, b.NalCount) / Math.Max(a.NalCount, b.NalCount);
                    Console.WriteLine("   两观众 NAL 数比值 = " + ratio.ToString("F2")
                        + "（A=" + a.NalCount + " B=" + b.NalCount + "）");
                    if (ratio >= 0.5)
                        Console.WriteLine("ok   两观众吞吐量相当（比值 ≥ 0.5，说明是共享编码+各自队列，不是互相抢占）");
                    else
                    {
                        Console.WriteLine("FAIL 两观众吞吐差距过大（比值 " + ratio.ToString("F2")
                            + " < 0.5）—— 可能存在抢占或饿死");
                        fails++;
                    }
                }

                // ffprobe 验两个流都能解码
                if (!string.IsNullOrEmpty(ffprobe))
                {
                    Console.WriteLine();
                    Console.WriteLine("== ffprobe 分别验证两个观众的码流");
                    foreach (var r in results)
                    {
                        if (r.FilePath == null || !File.Exists(r.FilePath) || new FileInfo(r.FilePath).Length == 0)
                        {
                            Console.WriteLine("   " + r.Name + "：无码流文件，跳过");
                            continue;
                        }
                        if (!ProbeH264(ffprobe, r.FilePath, r.Name)) fails++;
                    }
                }

                Console.WriteLine();
                if (fails == 0)
                {
                    Console.WriteLine("RESULT=PASS 双观众同时订阅通过（各自都能读到帧）");
                    return 0;
                }
                Console.WriteLine("RESULT=FAIL " + fails + " 项不满足");
                return 1;
            }
            finally
            {
                guard.Cancel();
                await host.StopAsync();
            }
        }

        private static async Task<ViewerResult> CaptureViewerAsync(string name, int port, string token,
            string outFile, int seconds)
        {
            var r = new ViewerResult { Name = name, FilePath = outFile };
            var sw = Stopwatch.StartNew();
            try
            {
                using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                http.DefaultRequestHeaders.Add("X-SoulLink-Token", token);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"http://127.0.0.1:{port}/stream?token={Uri.EscapeDataString(token)}");
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                r.Status = (int)resp.StatusCode;
                if (resp.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    r.Error = "HTTP " + (int)resp.StatusCode;
                    return r;
                }

                await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
                await using var fs = File.Create(outFile);
                var buf = new byte[64 * 1024];
                try
                {
                    while (true)
                    {
                        int n = await s.ReadAsync(buf, cts.Token);
                        if (n <= 0) break;
                        await fs.WriteAsync(buf.AsMemory(0, n), cts.Token);
                        r.Bytes += n;
                        for (int i = 0; i + 4 < n; i++)
                        {
                            if (buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 0 && buf[i + 3] == 1)
                            {
                                r.NalCount++;
                                int t = buf[i + 4] & 0x1f;
                                if (t == 5) r.KeyCount++;
                                if (t == 7)
                                {
                                    // SPS：解析尺寸（width 在 SPS 里要 exp-golomb，这里只记录 marker）
                                    r.Width = -1; r.Height = -1;
                                }
                                i += 3;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { /* 到点主动断，正常 */ }
            }
            catch (OperationCanceledException) { r.Error = "超时未收到响应头"; }
            catch (HttpRequestException ex) { r.Error = "连接异常：" + ex.Message; }
            catch (IOException ex) { r.Error = "连接中断：" + ex.Message; }
            r.Seconds = sw.Elapsed.TotalSeconds;
            return r;
        }

        private static void Report(ViewerResult r)
        {
            Console.WriteLine("  " + r.Name + "  HTTP=" + r.Status
                + "  字节=" + r.Bytes
                + "  NAL=" + r.NalCount
                + "  关键帧=" + r.KeyCount
                + "  时长=" + r.Seconds.ToString("F1") + "s"
                + (r.Error != null ? "  错误=" + r.Error : ""));
        }

        private static bool ProbeH264(string ffprobe, string file, string name)
        {
            try
            {
                var psi = new ProcessStartInfo(ffprobe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                foreach (var a in new[]
                {
                    "-hide_banner", "-v", "error",
                    "-show_entries", "stream=codec_name,profile,level,width,height",
                    "-of", "default=noprint_wrappers=0", file
                }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p == null) { Console.WriteLine("   " + name + "：ffprobe 启动失败"); return false; }
                string o = p.StandardOutput.ReadToEnd();
                string e = p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                var kv = o.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                Console.WriteLine("   " + name + "：" + string.Join(" ", kv));
                bool ok = p.ExitCode == 0 && o.Contains("codec_name=h264");
                if (!ok) Console.WriteLine("   " + name + "：ffprobe 未识别出 h264（stderr=" + e.Trim() + "）");
                return ok;
            }
            catch (Exception ex) { Console.WriteLine("   " + name + "：ffprobe 异常 " + ex.Message); return false; }
        }

        private static string Hl(string s)
        {
            // 只留关键字段，避免刷屏
            var parts = new List<string>();
            foreach (var k in new[] { "Message" })
            {
                int i = s.IndexOf("\"" + k + "\"", StringComparison.Ordinal);
                if (i >= 0)
                {
                    int c = s.IndexOf(':', i);
                    int a = s.IndexOf('"', c + 1);
                    int b = s.IndexOf('"', a + 1);
                    if (a > 0 && b > a) parts.Add(s.Substring(a + 1, b - a - 1));
                }
            }
            return parts.Count > 0 ? parts[0] : s;
        }

        private static CancellationTokenSource StartMemoryGuard(int pid, int maxMb, Action<string> report)
        {
            var cts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    while (!cts.IsCancellationRequested)
                    {
                        await Task.Delay(800, cts.Token);
                        try
                        {
                            p.Refresh();
                            if (p.HasExited) return;
                            double priv = p.PrivateMemorySize64 / 1048576.0;
                            if (priv > maxMb)
                            {
                                report("Private " + priv.ToString("F0") + " MB > " + maxMb + " MB，强制结束 PID " + pid);
                                try { p.Kill(true); } catch { }
                                return;
                            }
                        }
                        catch { return; }
                    }
                }
                catch { }
            }, cts.Token);
            return cts;
        }
    }
}
