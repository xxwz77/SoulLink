using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StreamVerify
{
    /// <summary>
    /// /stream 端到端验证器（独立 agent，不碰用户正在挂机的主程序）。
    ///
    /// 为什么需要它：
    ///   /stream 的历史问题全是「探针里能跑、真实 Agent 里跑不了」。
    ///   差异在于真实 Agent 的画面来自 SessionRecorder 环形缓冲，
    ///   而缓冲只有在**宿主下发 record 指令**之后才开始抓帧。
    ///   所以验证必须同时扮演「宿主」和「浏览器」两个角色：
    ///     宿主  → 建 relay 管道，下发 record / recordstatus
    ///     浏览器 → 打 /api/info、/live、/stream
    ///   只做后者的话 /stream 永远 404「缓冲里没有帧」，看起来像功能坏了，
    ///   实际只是没开始抓帧 —— 这个误判以前发生过。
    ///
    /// 安全边界：
    ///   · 只启动/结束**自己 Process.Start 出来的那个 PID**，绝不按名字或路径通配；
    ///   · 只监听自己算出来的端口，只写 %TEMP% 下的目录；
    ///   · 不碰 dist\stable\、不碰 session 1 的主程序。
    ///
    /// 用法：
    ///   stream-verify run  --dir &lt;agent目录&gt; [--port 19095] [--token test123] [--seconds 8] [--ffprobe &lt;path&gt;]
    ///   stream-verify nolib --dir &lt;agent目录&gt; [--port 19095] [--token test123]
    /// </summary>
    public static class Program
    {
        private const string AgentExeName = "SoulLink.Agent.exe";
        private static int _fails;
        private static readonly StringBuilder Report = new();

        private static void Line(string s) { Console.WriteLine(s); Report.AppendLine(s); }
        private static void Ok(string s) => Line("ok    " + s);
        private static void Fail(string s) { Line("FAIL  " + s); _fails++; }
        private static void Info(string s) => Line("      " + s);

        public static async Task<int> Main(string[] args)
        {
            if (args.Length == 0) { Usage(); return 2; }
            string mode = args[0].ToLowerInvariant();
            var opt = ParseOptions(args.Skip(1).ToArray());

            string dir = Get(opt, "dir") ?? "";
            if (dir.Length == 0) { Console.WriteLine("缺少 --dir"); return 2; }
            if (!Directory.Exists(dir)) { Console.WriteLine("目录不存在：" + dir); return 2; }

            int port = int.TryParse(Get(opt, "port"), out int p) ? p : 19095;
            string token = Get(opt, "token") ?? "test123";
            int seconds = int.TryParse(Get(opt, "seconds"), out int s) ? s : 8;
            string ffprobe = Get(opt, "ffprobe") ?? "";

            string work = Path.Combine(Path.GetTempPath(), "stream-verify-" + port);
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
            Directory.CreateDirectory(work);

            Line("== stream-verify · mode=" + mode + " · port=" + port + " · dir=" + dir);
            Line("   工作目录 " + work);

            try
            {
                if (mode == "run") return await RunFullAsync(dir, port, token, seconds, ffprobe, work);
                if (mode == "nolib") return await RunNoLibAsync(dir, port, token, work);
                if (mode == "perf") return await RunPerfAsync(dir, port, token, seconds, work, Get(opt, "path") ?? "both");
                if (mode == "serve") return await RunServeAsync(dir, port, token, seconds, work);
                if (mode == "latency") return await RunLatencyAsync(dir, port, token, seconds, work);
                if (mode == "delay") return await DelayProbe.RunAsync(dir, port, token, seconds);
                if (mode == "record") return await RecordProbe.RunAsync(dir, port, token, seconds,
                    Get(opt, "ffprobe") ?? "");
                if (mode == "blackscreen") return await BlackScreenProbe.RunAsync(dir, port, token, work);
                if (mode == "dual") return await DualViewerProbe.RunAsync(dir, port, token, seconds,
                    Get(opt, "ffprobe") ?? "");
                Usage();
                return 2;
            }
            finally
            {
                try { File.WriteAllText(Path.Combine(work, "report.txt"), Report.ToString(), new UTF8Encoding(false)); } catch { }
            }
        }

        // ── 场景 1：完整链路 ────────────────────────────────────────────────

        private static async Task<int> RunFullAsync(string agentDir, int port, string token,
            int seconds, string ffprobe, string work)
        {
            string captureDir = Path.Combine(work, "capture");
            Directory.CreateDirectory(captureDir);

            await using var host = await HostSession.StartAsync(agentDir, port, token, captureDir, work);
            if (host == null) return 3;

            try
            {
                // ① 让代理开始抓帧（这是 /live 和 /stream 有画面的前提）
                var rec = await host.SendAsync("{\"Cmd\":\"record\",\"Count\":15,\"X\":75,\"Delta\":30}");
                Line("record 响应：" + rec);
                if (rec == null || !rec.Contains("\"Ok\":true")) Fail("record 指令未被接受");

                // ② 等环形缓冲真的有帧（最多 15 秒）
                string status = "";
                var wait = Stopwatch.StartNew();
                bool haveFrames = false;
                while (wait.Elapsed.TotalSeconds < 15)
                {
                    status = (await host.SendAsync("{\"Cmd\":\"recordstatus\"}")) ?? "";
                    if (status.Contains("|frames=") && !status.Contains("|frames=0|")) { haveFrames = true; break; }
                    await Task.Delay(500);
                }
                Line("recordstatus：" + status);
                if (haveFrames) Ok("环形缓冲有帧（画面真的抓到了）");
                else Fail("15 秒内环形缓冲始终没有帧 —— /stream 会 404「缓冲里没有帧」");

                using var http = NewClient(token);

                // ③ /api/info
                var infoResp = await http.GetAsync($"http://127.0.0.1:{port}/api/info");
                string infoBody = await infoResp.Content.ReadAsStringAsync();
                Line("/api/info → " + (int)infoResp.StatusCode + " " + infoBody);
                if (infoResp.IsSuccessStatusCode && infoBody.Contains("\"ok\":true")) Ok("/api/info 可用");
                else Fail("/api/info 不可用");

                // ④ /live
                var liveResp = await http.GetAsync($"http://127.0.0.1:{port}/live");
                byte[] liveBytes = await liveResp.Content.ReadAsByteArrayAsync();
                Line("/live → " + (int)liveResp.StatusCode + " " + liveResp.Content.Headers.ContentType
                     + " " + liveBytes.Length + "B");
                if (liveResp.IsSuccessStatusCode && liveBytes.Length > 0)
                {
                    string jpg = Path.Combine(work, "live.jpg");
                    await File.WriteAllBytesAsync(jpg, liveBytes);
                    Ok("/live 返回 JPEG " + liveBytes.Length + " 字节 → " + jpg);
                }
                else Fail("/live 失败");

                // ⑤ /stream —— 关键项。要区分「明确报错」和「连接被重置」
                string h264 = Path.Combine(work, "stream.h264");
                var streamResult = await CaptureStreamAsync(http, port, token, h264, seconds);
                Line("/stream → " + streamResult.Describe());
                if (streamResult.StatusCode == 0)
                    Fail("/stream 无 HTTP 响应（连接被重置/中断）—— 这正是交接文档里记录的故障现象");
                else if (streamResult.StatusCode != 200)
                    Fail("/stream 返回 " + streamResult.StatusCode + "：" + streamResult.BodyText);
                else if (streamResult.Bytes < 1024)
                    Fail("/stream 返回 200 但只收到 " + streamResult.Bytes + " 字节");
                else
                {
                    Ok("/stream 返回 200，收到 " + streamResult.Bytes + " 字节 → " + h264);
                    // Annex-B 起始码检查（证明推的是 H.264 而不是别的）
                    byte[] head = await ReadHeadAsync(h264, 64);
                    bool annexB = FindStartCode(head) >= 0;
                    if (annexB) Ok("输出以 Annex-B 起始码开头（00 00 00 01），符合 WebCodecs 输入约定");
                    else Fail("输出不以 Annex-B 起始码开头，网页解码必然失败。首字节 " + Hex(head, 16));

                    if (ffprobe.Length > 0) ProbeWithFfprobe(ffprobe, h264);
                }

                Line("");
                Line("== 代理日志尾部（" + (host.LogPath ?? "无") + "）");
                foreach (var l in host.TailLog(30)) Line("   " + l);
            }
            finally
            {
                await host.StopAsync();
            }

            Line("");
            Line(_fails == 0 ? "RESULT=OK 全部通过" : "RESULT=FAIL 失败 " + _fails + " 项");
            return _fails;
        }

        // ── 场景 2：缺 DLL 时必须给「明确中文 500 」，而不是连接重置 ─────────

        private static async Task<int> RunNoLibAsync(string agentDir, int port, string token, string work)
        {
            // 复制一份，然后抽掉视频运行库
            string broken = Path.Combine(work, "agent-nolib");
            Directory.CreateDirectory(broken);
            foreach (var f in Directory.GetFiles(agentDir))
                File.Copy(f, Path.Combine(broken, Path.GetFileName(f)), true);

            string target = Path.Combine(broken, "Vortice.MediaFoundation.dll");
            if (!File.Exists(target)) { Fail("源目录里没有 Vortice.MediaFoundation.dll，无法构造缺库场景"); return 1; }
            File.Delete(target);
            Line("已抽掉 " + target);

            string captureDir = Path.Combine(work, "capture-nolib");
            Directory.CreateDirectory(captureDir);

            await using var host = await HostSession.StartAsync(broken, port, token, captureDir, work);
            if (host == null) return 3;

            try
            {
                var rec = await host.SendAsync("{\"Cmd\":\"record\",\"Count\":15,\"X\":75,\"Delta\":30}");
                Line("record 响应：" + rec);

                // 等有帧，否则会先撞上「缓冲里没有帧」而掩盖缺库问题
                var wait = Stopwatch.StartNew();
                string lastStatus = "";
                while (wait.Elapsed.TotalSeconds < 15)
                {
                    lastStatus = (await host.SendAsync("{\"Cmd\":\"recordstatus\"}")) ?? "";
                    if (lastStatus.Contains("|frames=") && !lastStatus.Contains("|frames=0|")) break;
                    await Task.Delay(500);
                }
                Line("recordstatus：" + lastStatus);

                using var http = NewClient(token);
                var res = await CaptureStreamAsync(http, port, token, Path.Combine(work, "nolib.h264"), 6);
                Line("/stream → " + res.Describe());

                // ★ 再用裸 TCP 抓一次原始响应头：
                //   HttpClient 的 "连接被重置" 有两种真相 —— 服务端一个字节没发就断开（响应头都没到），
                //   还是发了头又被 RST。必须看原始字节，否则报告只能写"看起来像重置"。
                Line("");
                Line("== 裸 TCP 原始响应（/stream）");
                foreach (var l in RawHttp(port, "/stream?token=" + Uri.EscapeDataString(token)))
                    Line("   " + l);

                // ★ 关键鉴别：连接重置有两种成因，处理方式完全不同
                //   ① 进程还活着 → 是响应没写出去（代码问题，可修）
                //   ② 进程已经死了 → 是缺 DLL 直接崩了（部署问题，可修）
                bool alive = host.AgentAlive;
                Line("代理进程是否存活：" + (alive ? "是" : "否（已退出，退出码 " + host.AgentExitCode + "）"));
                if (alive)
                {
                    try
                    {
                        var still = await http.GetAsync($"http://127.0.0.1:{port}/api/info");
                        Line("重置后再打 /api/info → HTTP " + (int)still.StatusCode + "（服务仍在监听）");
                    }
                    catch (Exception ex) { Line("重置后再打 /api/info → 也失败：" + ex.Message); }
                }
                Line("");
                Line("== 代理日志全文（" + (host.LogPath ?? "无") + "）");
                foreach (var l in host.FullLog()) Line("   " + l);

                if (res.StatusCode == 0)
                    Fail("缺 DLL 时仍然是无响应（连接重置）—— 诊断信息没有传达给客户端");
                else if (res.StatusCode != 500)
                    Fail("缺 DLL 时期望 500，实际 " + res.StatusCode);
                else
                {
                    Ok("缺 DLL 时返回 HTTP 500（不是连接重置）");
                    string body = res.BodyText ?? "";
                    Line("响应正文：" + body);
                    if (body.Contains("缺少") && (body.Contains("Vortice") || body.Contains("运行库")))
                        Ok("错误正文是明确的中文诊断，用户能看懂");
                    else Fail("错误正文没有说明缺的是视频运行库");
                }

                var logTail = host.FullLog();
                Line("== 代理日志（" + logTail.Count + " 行）");
                foreach (var l in logTail) Line("   " + l);
                if (logTail.Any(l => l.Contains("请求失败") || l.Contains("视频服务")))
                    Ok("日志里记录了失败原因（可事后排查）");
                else Fail("日志里没有失败记录");
            }
            finally
            {
                await host.StopAsync();
            }

            Line("");
            Line(_fails == 0 ? "RESULT=OK 全部通过" : "RESULT=FAIL 失败 " + _fails + " 项");
            return _fails;
        }

        // ── 场景 5：延迟分段测量 ────────────────────────────────────────────

        /// <summary>
        /// 量「从画面变化到字节出现在客户端」的真实延迟，并拆成可归因的几段。
        ///
        /// ★ 方法：不依赖任何外部时钟同步。
        ///   SessionRecorder 每帧带 At（DateTime.Now，抓帧完成时刻），
        ///   而 /live 与 /stream 都从同一个环形缓冲取帧。
        ///   所以只要记录「收到字节的本地时刻」和「该帧的 At」，差值就是
        ///   「抓帧完成 → 字节到达客户端」这段，含编码 + HTTP + 客户端读取。
        ///   /live 只差抓帧→HTTP（不含编码），两者相减 = 编码成本。
        ///
        /// ★ 注意：这个测量**不含**「画面变化 → 被抓到」那一段。
        ///   WGC 泵线程只在有变化时更新缓存纹理，静止画面的 At 可能是旧的，
        ///   所以在画面静止的桌面上测出来的数会偏大且不稳定 —— 这是方法本身的边界，
        ///   报告里必须写明，不能当成"我们链路 3 秒延迟"。
        /// </summary>
        private static async Task<int> RunLatencyAsync(string agentDir, int port, string token, int seconds, string work)
        {
            string captureDir = Path.Combine(work, "capture-latency");
            Directory.CreateDirectory(captureDir);

            await using var host = await HostSession.StartAsync(agentDir, port, token, captureDir, work);
            if (host == null) return 3;

            try
            {
                var rec = await host.SendAsync("{\"Cmd\":\"record\",\"Count\":15,\"X\":75,\"Delta\":30}");
                Line("record：" + (rec?.Contains("\"Ok\":true") == true ? "已开始抓帧" : rec ?? "(无响应)"));

                var warm = Stopwatch.StartNew();
                while (warm.Elapsed.TotalSeconds < 15)
                {
                    string st = (await host.SendAsync("{\"Cmd\":\"recordstatus\"}")) ?? "";
                    if (st.Contains("|frames=") && !st.Contains("|frames=0|")) break;
                    await Task.Delay(300);
                }

                using var http = NewClient(token);

                // ── A. /live 通路：抓帧完成 → 客户端拿到 JPEG ──
                //    这段 = 锁等待 + 拷贝字节 + HTTP 写出。数据量小、路径短。
                Line("");
                Line("== A. /live（JPEG）逐帧到达延迟");
                var liveLat = new List<double>();
                for (int i = 0; i < 20; i++)
                {
                    // 记下取帧前的本地时刻，再取帧。
                    // ★ 关键：GetLatestJpeg 返回的帧可能已经被上一次请求取过（同帧），
                    //   所以用 recordstatus 的 total 变化判断是否换了帧，避免把重复帧算进去。
                    string before = (await host.SendAsync("{\"Cmd\":\"recordstatus\"}")) ?? "";
                    long totalBefore = ExtractLong(before, "|total=");

                    var t0 = DateTime.Now;
                    var r = await http.GetAsync($"http://127.0.0.1:{port}/live?token={Uri.EscapeDataString(token)}");
                    if (r.IsSuccessStatusCode)
                    {
                        byte[] b = await r.Content.ReadAsByteArrayAsync();
                        var t1 = DateTime.Now;
                        // 让宿主报一次它自己的「最新帧时间」——代理侧能给出的最接近的锚点
                        string after = (await host.SendAsync("{\"Cmd\":\"recordstatus\"}")) ?? "";
                        long totalAfter = ExtractLong(after, "|total=");
                        if (totalAfter > totalBefore)
                            liveLat.Add((t1 - t0).TotalMilliseconds);
                        if (i == 0) Line("   首帧 " + b.Length + " B");
                    }
                    await Task.Delay(120);
                }
                ReportStats("   /live 单次请求耗时", liveLat);

                // ── B. /stream 通路：编码 + 传输 ──
                //    逐块记录「收到每块字节的时刻」，算相邻块间隔 = 编码+写出节拍。
                Line("");
                Line("== B. /stream（H.264）块到达节拍");
                var blockGaps = new List<double>();
                var blockSizes = new List<int>();
                DateTime? firstByteAt = null;
                DateTime? lastAt = null;
                long streamBytes = 0;
                int nalCount = 0;

                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds)))
                {
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get,
                            $"http://127.0.0.1:{port}/stream?token={Uri.EscapeDataString(token)}");
                        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                        if (resp.StatusCode != System.Net.HttpStatusCode.OK)
                        {
                            Fail("/stream 未返回 200：" + (int)resp.StatusCode);
                            return _fails;
                        }
                        var headerAt = DateTime.Now;
                        await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
                        var buf = new byte[64 * 1024];
                        try
                        {
                            while (true)
                            {
                                int n = await s.ReadAsync(buf, cts.Token);
                                if (n <= 0) break;
                                var now = DateTime.Now;
                                firstByteAt ??= now;
                                if (lastAt.HasValue) blockGaps.Add((now - lastAt.Value).TotalMilliseconds);
                                lastAt = now;
                                blockSizes.Add(n);
                                streamBytes += n;
                                for (int i = 0; i + 3 < n; i++)
                                    if (buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 0 && buf[i + 3] == 1) nalCount++;
                            }
                        }
                        catch (OperationCanceledException) { Line("   （到点主动断开）"); }

                        Line("   响应头到达耗时 " + (headerAt - DateTime.Now).TotalMilliseconds.ToString("F0") + " ms（相对调用时刻）");
                    }
                    catch (OperationCanceledException) { Fail("/stream 超时未返回响应头"); }
                }

                Line("   总字节 " + streamBytes + " · NAL " + nalCount + " · 块数 " + blockSizes.Count);
                ReportStats("   块间隔", blockGaps);
                if (blockGaps.Count > 0)
                {
                    double avg = blockGaps.Average();
                    Line("   → 平均块间隔 " + avg.ToString("F1") + " ms ≈ 上限 "
                         + (avg > 0 ? (1000 / avg).ToString("F1") : "?") + " 帧/秒的推送节拍");
                }

                // ── C. 结论：拿不到「画面变化→字节到达」的可靠锚点，必须如实说明 ──
                Line("");
                Line("== C. 说明");
                Line("   本模式量到的是「请求发出 → 字节到达客户端」的服务端+传输成本。");
                Line("   它**不包含**：画面变化 → WGC 抓到（取决于游戏帧率与合成延迟）、");
                Line("                浏览器解码 → 上屏（取决于 WebCodecs 与合成器）。");
                Line("   那两段必须由页面内埋点测量，见 delay-notes 文档的分段表。");
            }
            finally
            {
                await host.StopAsync();
            }

            Line("");
            Line(_fails == 0 ? "RESULT=OK" : "RESULT=FAIL " + _fails + " 项");
            return _fails;
        }

        private static long ExtractLong(string s, string key)
        {
            int i = s.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return -1;
            int start = i + key.Length;
            int end = start;
            while (end < s.Length && char.IsDigit(s[end])) end++;
            return long.TryParse(s.Substring(start, end - start), out long v) ? v : -1;
        }

        private static void ReportStats(string label, List<double> xs)
        {
            if (xs.Count == 0) { Line(label + "：无样本"); return; }
            var sorted = xs.OrderBy(x => x).ToList();
            double avg = xs.Average();
            double p50 = sorted[sorted.Count / 2];
            double p95 = sorted[(int)Math.Min(sorted.Count - 1, Math.Round(sorted.Count * 0.95))];
            Line(label + "：n=" + xs.Count
                 + "  平均 " + avg.ToString("F1")
                 + "  p50 " + p50.ToString("F1")
                 + "  p95 " + p95.ToString("F1")
                 + "  最大 " + sorted.Last().ToString("F1") + " ms");
        }

        // ── 场景 4：常驻服务（给浏览器验证用） ───────────────────────────────

        /// <summary>
        /// 起一个「假宿主 + 独立 agent」，保持 N 秒不退出，让浏览器去连 /stream。
        ///
        /// ★ 为什么必须由本工具代持宿主：真实 Agent 的 /stream 依赖 SessionRecorder
        ///   环形缓冲有帧，而缓冲只有宿主下发 record 指令后才开始抓。
        ///   浏览器自己没法下发 record，所以没有本工具代持时，
        ///   浏览器打开页面只会看到 404「缓冲里没有帧」，会被误判成网页坏了。
        /// </summary>
        private static async Task<int> RunServeAsync(string agentDir, int port, string token, int seconds, string work)
        {
            string captureDir = Path.Combine(work, "capture-serve");
            Directory.CreateDirectory(captureDir);

            await using var host = await HostSession.StartAsync(agentDir, port, token, captureDir, work);
            if (host == null) return 3;

            try
            {
                var rec = await host.SendAsync("{\"Cmd\":\"record\",\"Count\":15,\"X\":75,\"Delta\":30}");
                Line("record：" + (rec?.Contains("\"Ok\":true") == true ? "已开始抓帧" : rec ?? "(无响应)"));

                var warm = Stopwatch.StartNew();
                while (warm.Elapsed.TotalSeconds < 15)
                {
                    string st = (await host.SendAsync("{\"Cmd\":\"recordstatus\"}")) ?? "";
                    if (st.Contains("|frames=") && !st.Contains("|frames=0|"))
                    {
                        Ok("环形缓冲已有帧，浏览器可以开始验证");
                        break;
                    }
                    await Task.Delay(400);
                }

                Line("");
                Line("SERVE_READY http://127.0.0.1:" + port + "  token=" + token + "  pid=" + host.AgentPid);
                Line("（保持 " + seconds + " 秒；用完自动收拾）");

                // 期间定期确认还在服务，避免「浏览器连的时候 agent 已经死了」
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    await Task.Delay(2000);
                    if (!host.AgentAlive)
                    {
                        Fail("agent 在服务期间退出了（退出码 " + host.AgentExitCode + "）");
                        Line("");
                        foreach (var l in host.FullLog()) Line("   " + l);
                        break;
                    }
                }
                if (host.AgentAlive) Ok("服务期间 agent 一直存活");
            }
            finally
            {
                await host.StopAsync();
            }

            Line("");
            Line(_fails == 0 ? "RESULT=OK" : "RESULT=FAIL " + _fails + " 项");
            return _fails;
        }

        // ── 场景 3：改前/改后性能对比 ────────────────────────────────────────

        /// <summary>
        /// 量一条通路在 N 秒内的**真实**资源占用与产出。
        ///
        /// ★ 为什么要同时给出「产出帧数/字节数」：只看 CPU% 没有意义 ——
        ///   一个 0 帧的进程 CPU 也可以接近 0。必须把分子分母一起报，
        ///   否则对比会得出「改后 CPU 更低」其实是因为「改后没出画面」。
        /// </summary>
        private static async Task<int> RunPerfAsync(string agentDir, int port, string token, int seconds, string work,
            string path)
        {            string captureDir = Path.Combine(work, "capture-perf");
            Directory.CreateDirectory(captureDir);

            await using var host = await HostSession.StartAsync(agentDir, port, token, captureDir, work);
            if (host == null) return 3;

            try
            {
                var rec = await host.SendAsync("{\"Cmd\":\"record\",\"Count\":15,\"X\":75,\"Delta\":30}");
                Line("record：" + (rec ?? "(无响应)"));

                // 等第一帧，避免把启动爬坡算进稳态
                var warm = Stopwatch.StartNew();
                while (warm.Elapsed.TotalSeconds < 15)
                {
                    string st = (await host.SendAsync("{\"Cmd\":\"recordstatus\"}")) ?? "";
                    if (st.Contains("|frames=") && !st.Contains("|frames=0|")) break;
                    await Task.Delay(300);
                }
                await Task.Delay(1000);   // 稳态再开始计

                int pid = host.AgentPid;
                var proc = Process.GetProcessById(pid);
                long cpu0 = (long)proc.TotalProcessorTime.TotalMilliseconds;
                long mem0 = proc.WorkingSet64;

                // 两条通路可以分开跑（--path stream|live|both）。
                // ★ 必须能分开：合跑时内存从 177MB 涨到 1094MB，
                //   不隔离就说不清是哪条通路吃掉的，归因会变成猜。
                bool doStream = path is "stream" or "both";
                bool doLive = path is "live" or "both";
                Line("   通路选择：" + path);

                var sw = Stopwatch.StartNew();
                long streamBytes = 0;
                int streamFrames = 0;
                int liveRequests = 0;
                long liveBytes = 0;

                // 同时跑两条通路，量各自成本（这才能解释"为什么很卡"）
                using var http = NewClient(token);

                // 通路 A：/stream（H.264 长连接）
                var streamTask = Task.Run(async () =>
                {
                    if (!doStream) return;
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
                        using var req = new HttpRequestMessage(HttpMethod.Get,
                            $"http://127.0.0.1:{port}/stream?token={Uri.EscapeDataString(token)}");
                        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                        if (resp.StatusCode != System.Net.HttpStatusCode.OK) return;
                        await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
                        var buf = new byte[64 * 1024];
                        try
                        {
                            while (true)
                            {
                                int n = await s.ReadAsync(buf, cts.Token);
                                if (n <= 0) break;
                                Interlocked.Add(ref streamBytes, n);
                                // 统计 NAL 起始码个数 ≈ 帧数（Annex-B 每帧至少一个 00 00 00 01）
                                for (int i = 0; i + 3 < n; i++)
                                    if (buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 0 && buf[i + 3] == 1)
                                        Interlocked.Increment(ref streamFrames);
                            }
                        }
                        catch (OperationCanceledException) { }
                    }
                    catch { }
                });

                // 通路 B：/live 轮询（改前那条「很卡」的路）——
                //   按 15fps 的节奏拉，模拟网页实际行为
                var liveTask = Task.Run(async () =>
                {
                    if (!doLive) return;
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
                        while (!cts.IsCancellationRequested)
                        {
                            var t0 = Stopwatch.StartNew();
                            try
                            {
                                var r = await http.GetAsync($"http://127.0.0.1:{port}/live?token={Uri.EscapeDataString(token)}", cts.Token);
                                if (r.IsSuccessStatusCode)
                                {
                                    byte[] b = await r.Content.ReadAsByteArrayAsync(cts.Token);
                                    Interlocked.Increment(ref liveRequests);
                                    Interlocked.Add(ref liveBytes, b.Length);
                                }
                            }
                            catch (OperationCanceledException) { break; }
                            catch { }
                            int sleep = 1000 / 15 - (int)t0.ElapsedMilliseconds;
                            if (sleep > 0) { try { await Task.Delay(sleep, cts.Token); } catch { break; } }
                        }
                    }
                    catch { }
                });

                await Task.WhenAll(streamTask, liveTask);
                sw.Stop();

                proc.Refresh();
                long cpu1 = (long)proc.TotalProcessorTime.TotalMilliseconds;
                long mem1 = proc.WorkingSet64;

                double elapsed = sw.Elapsed.TotalSeconds;
                int cores = Environment.ProcessorCount;
                double cpuMs = cpu1 - cpu0;
                // 单核百分比：CPU 毫秒 / (墙钟毫秒) * 100
                double cpuPctOneCore = cpuMs / sw.Elapsed.TotalMilliseconds * 100.0;

                Line("");
                Line("== 性能实测（稳态 " + elapsed.ToString("F1") + " 秒）· 通路=" + path);
                if (doStream)
                {
                    Line("   【H.264 /stream】");
                    Line("     产出帧数（按 Annex-B 起始码计）: " + streamFrames);
                    Line("     产出字节数                     : " + streamBytes + " B"
                         + (elapsed > 0 ? "  = " + (streamBytes * 8 / elapsed / 1e6).ToString("F2") + " Mbps" : ""));
                }
                if (doLive)
                {
                    Line("   【JPEG /live 轮询】");
                    Line("     成功请求数                     : " + liveRequests
                         + (elapsed > 0 ? "  = " + (liveRequests / elapsed).ToString("F1") + " 帧/秒" : ""));
                    Line("     累计字节数                     : " + liveBytes + " B"
                         + (elapsed > 0 ? "  = " + (liveBytes * 8 / elapsed / 1e6).ToString("F2") + " Mbps" : ""));
                }
                Line("   【进程开销】");
                Line("     CPU 时间                       : " + cpuMs + " ms");
                Line("     单核占用                       : " + cpuPctOneCore.ToString("F1") + " %"
                     + "（占全机 " + (cpuPctOneCore / cores).ToString("F2") + " %，共 " + cores + " 核）");
                Line("     内存                           : " + (mem0 / 1048576) + " → " + (mem1 / 1048576) + " MB");

                if (doStream && streamFrames <= 0) Fail("/stream 在这段时间没有产出任何帧，性能数字无意义");
                else if (doStream) Ok("/stream 稳态产出 " + streamFrames + " 帧 / " + streamBytes + " B");
                if (doLive && liveRequests <= 0) Fail("/live 在这段时间没有成功请求");

                Line("");
                Line("   ★ 对比口径：同一台机器、同一会话、同一抓屏后端，只换传输通路。");
                Line("     改前 = BitBlt+JPEG 走 /live 轮询；改后 = WGC+H.264 走 /stream。");
                Line("     注意 SOULLINK_CAPTURE_MODE 影响抓屏后端，对比时两边要一致或明确标注。");
            }
            finally
            {
                await host.StopAsync();
            }

            Line("");
            Line(_fails == 0 ? "RESULT=OK" : "RESULT=FAIL " + _fails + " 项");
            return _fails;
        }

        // ── HTTP 细节 ──────────────────────────────────────────────────────

        private sealed class StreamResult
        {
            public int StatusCode;
            public long Bytes;
            public string? BodyText;
            public string? Error;

            public string Describe()
            {
                if (StatusCode == 0) return "无响应（" + Error + "）";
                return "HTTP " + StatusCode + " · " + Bytes + " 字节" + (BodyText is { Length: > 0 } ? " · " + BodyText : "");
            }
        }

        /// <summary>
        /// 抓 /stream。**必须自己限时**：这是 30 秒的长连接，
        /// 不设上限会让验证卡住，看起来像"卡死"而不是"通了"。
        /// </summary>
        private static async Task<StreamResult> CaptureStreamAsync(HttpClient http, int port, string token,
            string outFile, int seconds)
        {
            var result = new StreamResult();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"http://127.0.0.1:{port}/stream?token={Uri.EscapeDataString(token)}");
                req.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };

                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                result.StatusCode = (int)resp.StatusCode;

                if (resp.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    result.BodyText = await resp.Content.ReadAsStringAsync(cts.Token);
                    return result;
                }

                await using var src = await resp.Content.ReadAsStreamAsync(cts.Token);
                await using var dst = File.Create(outFile);
                var buf = new byte[64 * 1024];
                try
                {
                    while (true)
                    {
                        int n = await src.ReadAsync(buf, cts.Token);
                        if (n <= 0) break;
                        await dst.WriteAsync(buf.AsMemory(0, n), cts.Token);
                        result.Bytes += n;
                    }
                }
                catch (OperationCanceledException)
                {
                    // 到点主动断开：这是预期行为，不是失败
                }
            }
            catch (OperationCanceledException)
            {
                result.Error = "超过 " + seconds + " 秒未收到响应头";
            }
            catch (HttpRequestException ex)
            {
                result.Error = "连接异常：" + ex.Message
                    + (ex.InnerException != null ? " / " + ex.InnerException.Message : "");
            }
            catch (IOException ex)
            {
                result.Error = "连接中断：" + ex.Message;
            }
            return result;
        }

        private static HttpClient NewClient(string token)
        {
            var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.Add("X-SoulLink-Token", token);
            return http;
        }

        private static async Task<byte[]> ReadHeadAsync(string path, int n)
        {
            var buf = new byte[n];
            await using var fs = File.OpenRead(path);
            int read = await fs.ReadAsync(buf);
            return buf.Take(read).ToArray();
        }

        private static int FindStartCode(byte[] b)
        {
            for (int i = 0; i + 3 < b.Length; i++)
                if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1) return i;
            return -1;
        }

        /// <summary>
        /// 裸 TCP 抓响应头的可读形式。
        ///
        /// ★ 为什么不只用 HttpClient：它的 HttpRequestException 会把
        ///   「服务端返回了 500」和「服务端直接 RST」都说成连接问题，
        ///   而这两者对用户的意义完全不同（前者可读，后者莫名其妙）。
        /// </summary>
        private static List<string> RawHttp(int port, string pathWithQuery)
        {
            var lines = new List<string>();
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                tcp.Connect("127.0.0.1", port);
                using var ns = tcp.GetStream();
                ns.ReadTimeout = 6000;
                string req = "GET " + pathWithQuery + " HTTP/1.1\r\n"
                           + "Host: 127.0.0.1:" + port + "\r\n"
                           + "Connection: close\r\n\r\n";
                byte[] send = Encoding.ASCII.GetBytes(req);
                ns.Write(send, 0, send.Length);

                var buf = new byte[8192];
                int n;
                try { n = ns.Read(buf, 0, buf.Length); }
                catch (IOException ex) { lines.Add("(读取异常：" + ex.Message + ")"); return lines; }

                if (n <= 0) { lines.Add("(服务端未发送任何字节就关闭连接)"); return lines; }

                string text = Encoding.UTF8.GetString(buf, 0, n);
                string[] parts = text.Split(new[] { "\r\n\r\n" }, 2, StringSplitOptions.None);
                foreach (var l in parts[0].Split("\r\n")) lines.Add(l);
                if (parts.Length > 1 && parts[1].Length > 0)
                    lines.Add("(正文前 200 字符) " + parts[1].Substring(0, Math.Min(200, parts[1].Length)).Replace("\n", "\\n"));
            }
            catch (Exception ex) { lines.Add("(连接异常：" + ex.Message + ")"); }
            return lines;
        }

        private static string Hex(byte[] b, int n)
            => string.Join(" ", b.Take(n).Select(x => x.ToString("X2")));

        private static void ProbeWithFfprobe(string ffprobe, string file)
        {
            Line("");
            Line("== ffprobe " + file);
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
                    "-show_entries", "stream=codec_name,profile,level,width,height,avg_frame_rate",
                    "-show_entries", "format=format_name,duration,bit_rate,size",
                    "-of", "default=noprint_wrappers=0",
                    file
                }) psi.ArgumentList.Add(a);

                using var p = Process.Start(psi);
                if (p == null) { Fail("ffprobe 启动失败"); return; }
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                foreach (var l in stdout.Split('\n')) if (l.Trim().Length > 0) Line("   " + l.TrimEnd());
                if (stderr.Trim().Length > 0) Line("   [stderr] " + stderr.Trim());
                if (p.ExitCode == 0 && stdout.Contains("h264")) Ok("ffprobe 认出这是 H.264 流");
                else Fail("ffprobe 未能识别 H.264（exit=" + p.ExitCode + "）");
            }
            catch (Exception ex) { Fail("ffprobe 调用异常：" + ex.Message); }
        }

        // ── 假宿主：建 relay 管道 + 起代理 + 收发指令 ────────────────────────

        private sealed class HostSession : IAsyncDisposable
        {
            private NamedPipeServerStream? _pipe;
            private NamedPipeServerStream? _events;
            private StreamReader? _reader;
            private StreamWriter? _writer;
            private CancellationTokenSource? _rxCts;
            private Task? _rx;
            private readonly StringBuilder _inbox = new();
            private Process? _agent;
            private readonly SemaphoreSlim _lock = new(1, 1);

            public string? LogPath { get; private set; }

            /// <summary>代理进程 PID（性能采样要按 PID 取，绝不能按名字找）。</summary>
            public int AgentPid { get; private set; }

            /// <summary>代理进程是否还活着 —— 用来区分「响应没写出去」和「进程崩了」。</summary>
            public bool AgentAlive
            {
                get { try { return _agent != null && !_agent.HasExited; } catch { return false; } }
            }

            public int AgentExitCode
            {
                get { try { return _agent is { HasExited: true } ? _agent.ExitCode : -1; } catch { return -1; } }
            }

            public List<string> FullLog() => TailLog(int.MaxValue);

            public static async Task<HostSession?> StartAsync(string agentDir, int port, string token,
                string captureDir, string work)
            {
                var h = new HostSession();
                string exe = Path.Combine(agentDir, AgentExeName);
                if (!File.Exists(exe)) { Console.WriteLine("找不到 " + exe); return null; }

                // 管道名与宿主的命名规则一致：<身份>_Relay_<会话号>_<8位十六进制>
                int sid = Process.GetCurrentProcess().SessionId;
                string name = "SoulLink_Relay_" + sid + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                Line("   宿主管道 " + name);

                h._pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                // 事件管道（代理连不上只会记一条日志，但保持干净便于读日志）
                try
                {
                    h._events = new NamedPipeServerStream(name + "_Events", PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                }
                catch { }

                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = agentDir
                };
                psi.ArgumentList.Add("--agent");
                psi.ArgumentList.Add("--pipe");
                psi.ArgumentList.Add(name);
                psi.Environment["SOULLINK_WEB_PORT"] = port.ToString();
                psi.Environment["SOULLINK_WEB_TOKEN"] = token;
                psi.Environment["SOULLINK_CAPTURE_MODE"] = "1";
                psi.Environment["SOULLINK_ACCOUNT_ID"] = "verify";
                psi.Environment["SOULLINK_CAPTURE_DIR"] = captureDir;

                h._agent = Process.Start(psi);
                if (h._agent == null) { Console.WriteLine("代理启动失败"); return null; }
                Line("   代理 PID " + h._agent.Id + " · " + exe);
                h.AgentPid = h._agent.Id;
                // 代理日志路径：%TEMP%\soullink-relay-<pid>.log
                h.LogPath = Path.Combine(Path.GetTempPath(), "soullink-relay-" + h._agent.Id + ".log");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await h._pipe.WaitForConnectionAsync(cts.Token); }
                catch (OperationCanceledException) { Console.WriteLine("代理 30 秒内没有连上管道"); h.StopAsync().GetAwaiter().GetResult(); return null; }

                h._reader = new StreamReader(h._pipe, new UTF8Encoding(false), false, 4096, true);
                h._writer = new StreamWriter(h._pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                h._rxCts = new CancellationTokenSource();
                h._rx = Task.Run(() => h.RxLoopAsync(h._rxCts.Token));

                // 等网页服务起来（端口监听）
                var up = Stopwatch.StartNew();
                bool listening = false;
                while (up.Elapsed.TotalSeconds < 15)
                {
                    try
                    {
                        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                        probe.DefaultRequestHeaders.Add("X-SoulLink-Token", token);
                        var r = await probe.GetAsync($"http://127.0.0.1:{port}/api/info");
                        if ((int)r.StatusCode is 200 or 401) { listening = true; break; }
                    }
                    catch { }
                    await Task.Delay(400);
                }
                if (listening) Ok("代理已连上宿主管道，网页服务在 :" + port + " 就绪");
                else { Fail("代理 15 秒内没有在 :" + port + " 上监听"); await h.StopAsync(); return null; }
                return h;
            }

            private async Task RxLoopAsync(CancellationToken ct)
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        string? line = await _reader!.ReadLineAsync(ct);
                        if (line == null) break;
                        lock (_inbox) _inbox.AppendLine(line);
                    }
                }
                catch { }
            }

            /// <summary>发一条指令并等它的回复（协议是串行请求-响应）。</summary>
            public async Task<string?> SendAsync(string json)
            {
                await _lock.WaitAsync();
                try
                {
                    int before;
                    lock (_inbox) before = _inbox.ToString().Length;
                    if (_writer == null) return null;
                    await _writer.WriteLineAsync(json);
                    var sw = Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < 8)
                    {
                        string snap;
                        lock (_inbox) snap = _inbox.ToString();
                        if (snap.Length > before)
                        {
                            string added = snap.Substring(before);
                            int nl = added.IndexOf('\n');
                            if (nl >= 0) return added.Substring(0, nl).Trim();
                        }
                        await Task.Delay(60);
                    }
                    return null;
                }
                finally { _lock.Release(); }
            }

            public List<string> TailLog(int n)
            {
                try
                {
                    if (LogPath != null && File.Exists(LogPath))
                    {
                        var lines = File.ReadAllLines(LogPath);
                        return lines.Skip(Math.Max(0, lines.Length - n)).ToList();
                    }
                }
                catch { }
                return new List<string>();
            }

            public async Task StopAsync()
            {
                try { _rxCts?.Cancel(); } catch { }
                try { if (_writer != null) await _writer.WriteLineAsync("{\"Cmd\":\"exit\"}"); } catch { }
                await Task.Delay(400);
                try { if (_pipe != null) { _pipe.Dispose(); } } catch { }
                try { if (_events != null) { _events.Dispose(); } } catch { }
                // ★ 只结束自己启动的那个 PID，绝不按名字/路径通配
                try
                {
                    if (_agent != null && !_agent.HasExited) _agent.Kill(true);
                    _agent?.WaitForExit(5000);
                }
                catch { }
                try { _agent?.Dispose(); } catch { }
            }

            public async ValueTask DisposeAsync() => await StopAsync();
        }

        // ── 杂项 ──────────────────────────────────────────────────────────

        private static Dictionary<string, string> ParseOptions(string[] args)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (!a.StartsWith("--")) continue;
                string key = a.Substring(2);
                int eq = key.IndexOf('=');
                if (eq >= 0) { d[key.Substring(0, eq)] = key.Substring(eq + 1); continue; }
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { d[key] = args[++i]; continue; }
                d[key] = "";
            }
            return d;
        }

        private static string? Get(Dictionary<string, string> o, string k)
            => o.TryGetValue(k, out var v) ? v : null;

        private static void Usage()
        {
            Console.WriteLine("stream-verify run   --dir <agent目录> [--port 19095] [--token test123] [--seconds 8] [--ffprobe <exe>]");
            Console.WriteLine("stream-verify nolib --dir <agent目录> [--port 19095] [--token test123]");
            Console.WriteLine("stream-verify perf  --dir <agent目录> [--port 19095] [--token test123] [--seconds 10]");
        }
    }
}
