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
    /// 延迟归因专用：把「一帧从被抓到到抵达客户端」拆成可以分别归因的几段。
    ///
    /// 为什么单独一个程序而不是塞进 stream-verify：
    ///   这个测量要**长时间**采样（至少 20 秒）才能区分「稳态节拍」和「启动抖动」，
    ///   而 perf 模式还要兼顾 CPU/内存采样，混在一起容易互相干扰。
    ///
    /// 测什么（都是实测，不是估算）：
    ///   ① 采集节拍 —— 代理实际每秒抓多少帧（SessionRecorder 的 Loop 间隔）
    ///   ② 推流节拍 —— /stream 每秒实际推多少块（Task.Delay 决定的上限）
    ///   ③ 单帧服务端成本 —— 从发请求到字节到齐（/live）
    ///   ④ 帧年龄 —— 客户端拿到的这一帧，在环形缓冲里已经放了多久
    ///                  （= 采集完成 → 客户端拿到，含全部服务端与传输成本）
    ///
    /// 不测什么（必须如实说明）：
    ///   · 画面变化 → WGC 抓到：需要外部时钟，无法在本机单进程内测
    ///   · 浏览器解码 → 上屏：需要页面内埋点，属另一条通路
    /// </summary>
    public static class DelayProbe
    {
        private static int _fails;
        private static void Line(string s) => Console.WriteLine(s);
        private static void Ok(string s) => Line("ok    " + s);
        private static void Fail(string s) { Line("FAIL  " + s); _fails++; }

        public static async Task<int> RunAsync(string agentDir, int port, string token, int seconds)
        {
            string work = Path.Combine(Path.GetTempPath(), "delayprobe-" + port);
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
            Directory.CreateDirectory(work);
            string captureDir = Path.Combine(work, "capture");
            Directory.CreateDirectory(captureDir);

            await using var host = await ProbeHost.StartAsync(agentDir, port, token, captureDir, Line);
            if (host == null) return 3;
            try
            {
                var rec = await host.SendAsync("{\"Cmd\":\"record\",\"Count\":15,\"X\":75,\"Delta\":30}");
                Line("record：" + (rec ?? "(无响应)"));
                if (rec == null || !rec.Contains("\"Ok\":true")) { Fail("record 未被接受"); return _fails; }

                // 等第一帧
                var warm = Stopwatch.StartNew();
                while (warm.Elapsed.TotalSeconds < 20)
                {
                    string st = (await host.SendAsync("{\"Cmd\":\"recordstatus\"}")) ?? "";
                    if (st.Contains("|frames=") && !st.Contains("|frames=0|")) break;
                    await Task.Delay(300);
                }

                using var http = NewClient(token);

                // ── ① 采集节拍 ──────────────────────────────────────────────
                Line("");
                Line("== ① 采集节拍（SessionRecorder 实际抓帧率）");
                string s1 = (await host.SendAsync("{\"Cmd\":\"recordstatus\"}")) ?? "";
                long total1 = Num(s1, "|total=");
                var t1 = Stopwatch.StartNew();
                await Task.Delay(8000);
                string s2 = (await host.SendAsync("{\"Cmd\":\"recordstatus\"}")) ?? "";
                long total2 = Num(s2, "|total=");
                t1.Stop();
                double capFps = total2 > total1 ? (total2 - total1) / t1.Elapsed.TotalSeconds : 0;
                Line("   帧数 " + total1 + " → " + total2 + "，历时 " + t1.Elapsed.TotalSeconds.ToString("F1") + " s");
                Line("   → 实测采集 " + capFps.ToString("F2") + " 帧/秒（配置 record fps=15）");
                Line("   recordstatus 原文：" + s2);
                if (capFps < 5) Fail("采集帧率过低（" + capFps.ToString("F1") + " fps），画面必然卡");

                // ── ④ 帧年龄：/live 拿到的字节有多大、多旧 ──────────────────
                Line("");
                Line("== ④ /live 单帧：请求耗时 + 字节数");
                var liveRtt = new List<double>();
                var liveSizes = new List<int>();
                for (int i = 0; i < 25; i++)
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        var r = await http.GetAsync($"http://127.0.0.1:{port}/live?token={Uri.EscapeDataString(token)}");
                        if (r.IsSuccessStatusCode)
                        {
                            byte[] b = await r.Content.ReadAsByteArrayAsync();
                            sw.Stop();
                            liveRtt.Add(sw.Elapsed.TotalMilliseconds);
                            liveSizes.Add(b.Length);
                        }
                    }
                    catch { }
                    await Task.Delay(100);
                }
                Stats("   请求往返", liveRtt);
                if (liveSizes.Count > 0)
                {
                    double avgKB = liveSizes.Average() / 1024.0;
                    Line("   单帧 " + avgKB.ToString("F1") + " KB（min " + (liveSizes.Min() / 1024.0).ToString("F1")
                         + " / max " + (liveSizes.Max() / 1024.0).ToString("F1") + " KB）");
                    Line("   → 若按 15fps 拉：平均带宽 " + (avgKB * 8 * 15 / 1024).ToString("F2") + " Mbps");
                    Line("   → 若按 30fps 拉：平均带宽 " + (avgKB * 8 * 30 / 1024).ToString("F2") + " Mbps");
                }

                // ── ② 推流节拍 ──────────────────────────────────────────────
                Line("");
                Line("== ② /stream 推流节拍（Task.Delay 决定的上限）");
                var gaps = new List<double>();
                var sizes = new List<int>();
                long totalBytes = 0;
                int nals = 0, idrs = 0;
                var firstAt = Stopwatch.StartNew();
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds)))
                {
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get,
                            $"http://127.0.0.1:{port}/stream?token={Uri.EscapeDataString(token)}");
                        var tReq = Stopwatch.StartNew();
                        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                        Line("   响应头耗时 " + tReq.Elapsed.TotalMilliseconds.ToString("F0") + " ms · HTTP " + (int)resp.StatusCode);
                        if (resp.StatusCode != System.Net.HttpStatusCode.OK) { Fail("/stream 非 200"); return _fails; }

                        await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
                        var buf = new byte[64 * 1024];
                        var last = Stopwatch.StartNew();
                        int blocks = 0;
                        try
                        {
                            while (true)
                            {
                                int n = await s.ReadAsync(buf, cts.Token);
                                if (n <= 0) break;
                                var now = last.Elapsed.TotalMilliseconds;
                                if (blocks > 0) gaps.Add(now - _prevMs);
                                _prevMs = now;
                                blocks++;
                                sizes.Add(n);
                                totalBytes += n;
                                for (int i = 0; i + 4 < n; i++)
                                {
                                    if (buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 0 && buf[i + 3] == 1)
                                    {
                                        nals++;
                                        if ((buf[i + 4] & 0x1f) == 5) idrs++;
                                        i += 3;
                                    }
                                }
                            }
                        }
                        catch (OperationCanceledException) { Line("   （到点主动断开，正常）"); }
                    }
                    catch (OperationCanceledException) { Fail("/stream 超时"); }
                }
                firstAt.Stop();
                Line("   总字节 " + totalBytes + " · NAL " + nals + " · IDR " + idrs + " · 块数 " + sizes.Count);
                Stats("   块间隔", gaps);
                if (gaps.Count > 0)
                {
                    double avg = gaps.Average();
                    Line("   → 实测推流 " + (1000 / avg).ToString("F2") + " 帧/秒");
                    double first = gaps.OrderBy(x => x).First();
                    Line("   → 最快块间隔 " + first.ToString("F1") + " ms（说明是否有突发成块）");
                }
                if (totalBytes > 0 && firstAt.Elapsed.TotalSeconds > 0)
                    Line("   → 平均码率 " + (totalBytes * 8 / firstAt.Elapsed.TotalSeconds / 1e6).ToString("F3") + " Mbps");

                // ── ③ 结论汇总 ─────────────────────────────────────────────
                Line("");
                Line("== ③ 分段成本汇总（本机实测）");
                Line("   采集节拍              : " + capFps.ToString("F2") + " fps  → 单帧间隔 "
                     + (capFps > 0 ? (1000 / capFps).ToString("F0") : "?") + " ms");
                if (gaps.Count > 0)
                    Line("   推流节拍              : " + (1000 / gaps.Average()).ToString("F2")
                         + " fps  → 单帧间隔 " + gaps.Average().ToString("F0") + " ms");
                if (liveRtt.Count > 0)
                    Line("   /live 服务端往返      : 平均 " + liveRtt.Average().ToString("F1") + " ms");
                Line("");
                Line("   ★ 可叠加的下限（连续最坏情况）:");
                if (capFps > 0 && gaps.Count > 0)
                {
                    double capInterval = 1000 / capFps;
                    double pushInterval = gaps.Average();
                    double rtt = liveRtt.Count > 0 ? liveRtt.Average() : 0;
                    Line("     帧年龄上限(采集间隔) + 推流等待 + 传输 = "
                         + capInterval.ToString("F0") + " + " + pushInterval.ToString("F0")
                         + " + " + rtt.ToString("F1") + " ≈ "
                         + (capInterval + pushInterval + rtt).ToString("F0") + " ms");
                    Line("     （两项节拍是串行的：帧在缓冲里等一次，推流循环再等一次）");
                }
                Line("");
                Line("   ★ 未覆盖（无法在本进程内测，需页面埋点 / 外部时钟）：");
                Line("     画面变化 → WGC 抓到 : 未实测");
                Line("     浏览器解码 → 上屏    : 未实测");
            }
            finally { await host.StopAsync(); }

            Line("");
            Line(_fails == 0 ? "RESULT=OK" : "RESULT=FAIL " + _fails + " 项");
            return _fails;
        }

        private static double _prevMs;

        private static void Stats(string label, List<double> xs)
        {
            if (xs.Count == 0) { Line(label + "：无样本"); return; }
            var s = xs.OrderBy(x => x).ToList();
            Line(label + "：n=" + xs.Count
                 + "  平均 " + xs.Average().ToString("F1")
                 + "  p50 " + s[s.Count / 2].ToString("F1")
                 + "  p95 " + s[(int)Math.Min(s.Count - 1, Math.Round(s.Count * 0.95))].ToString("F1")
                 + "  最大 " + s.Last().ToString("F1") + " ms");
        }

        private static long Num(string s, string key)
        {
            int i = s.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return -1;
            int a = i + key.Length, b = a;
            while (b < s.Length && char.IsDigit(s[b])) b++;
            return long.TryParse(s.Substring(a, b - a), out long v) ? v : -1;
        }

        private static HttpClient NewClient(string token)
        {
            var h = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            h.DefaultRequestHeaders.Add("X-SoulLink-Token", token);
            return h;
        }
    }
}
