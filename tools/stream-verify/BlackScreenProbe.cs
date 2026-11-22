using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StreamVerify
{
    /// <summary>
    /// 「画面全黑」事实分歧的判定器。
    ///
    /// 背景：我实测 agent 抓出来全黑，stream-core 在 A1 实测是彩色。
    /// 两个结论必须有条件差异，否则必有一个错。本工具把差异逐项隔离：
    ///
    ///   对照组 1：WGC 后端      vs  BitBlt 后端（同一会话、同一时刻）
    ///   对照组 2：agent 进程    vs  本进程 Graphics.CopyFromScreen（绕过 agent 的抓屏实现）
    ///   对照组 3：整帧 0x00     vs  有内容但极暗 / 颜色空间问题
    ///
    /// ★ 关键：不能只看"黑不黑"，要给出**量化**结果（Y 均值、非黑占比、
    ///   逐通道均值），否则"看起来黑"和"真的是 0x00"分不清。
    ///
    /// 安全边界：只起自己 Process.Start 的 agent，只写 %TEMP%。
    /// 每个 agent 实例都挂内存熔断，达到阈值立即停止并报告。
    /// </summary>
    public static class BlackScreenProbe
    {
        private static int _fails;

        public static async Task<int> RunAsync(string agentDir, int port, string token, string work)
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
            Directory.CreateDirectory(work);

            Console.WriteLine("== 黑屏归因对照实验");
            Console.WriteLine("   会话 = " + Process.GetCurrentProcess().SessionId);

            // ── 对照组 2 的前半：本进程直接抓屏（绕过 agent）──
            Console.WriteLine();
            Console.WriteLine("== A. 本进程 Graphics.CopyFromScreen（绕过 agent 的全部抓屏代码）");
            string direct = Path.Combine(work, "A-direct-copyfromscreen.png");
            var directStat = CaptureDirect(direct);
            PrintStat("CopyFromScreen", directStat);

            // ── 对照组 1：BitBlt 后端 ──
            Console.WriteLine();
            Console.WriteLine("== B. agent + BitBlt 后端（SOULLINK_CAPTURE_MODE=0）");
            var bitbltStat = await RunOneBackendAsync(agentDir, port, token, work, "0", "B-bitblt");

            // ── 对照组 1：WGC 后端 ──
            Console.WriteLine();
            Console.WriteLine("== C. agent + WGC 后端（SOULLINK_CAPTURE_MODE=1）");
            var wgcStat = await RunOneBackendAsync(agentDir, port, token, work, "1", "C-wgc");

            // ── 判定 ──
            Console.WriteLine();
            Console.WriteLine("== 结论对比");
            Console.WriteLine("  后端           尺寸           Y均值   非黑占比  R均值  G均值  B均值   判定");
            Row("CopyFromScreen", directStat);
            Row("agent BitBlt", bitbltStat);
            Row("agent WGC", wgcStat);
            Console.WriteLine();

            // 交叉判定：制造有区分度的结论，而不是各说各话
            if (directStat == null) { Console.WriteLine("!! 本进程抓屏都失败了，实验无效"); return 3; }

            bool directBlack = directStat.NonBlackPct < 1.0;
            if (directBlack)
            {
                Console.WriteLine("★ 本进程 CopyFromScreen 也是黑的 → 当前桌面本身没有可见内容（锁屏/最小化/空桌面）。");
                Console.WriteLine("  这个条件下测不出「agent 抓屏是否正常」，必须换到有内容的桌面重测。");
                Console.WriteLine("RESULT=INCONCLUSIVE 桌面无内容，无法判定");
                return 2;
            }

            Console.WriteLine("★ 本进程能抓到内容（非黑 " + directStat.NonBlackPct.ToString("F1") + "%）。");

            if (bitbltStat == null || wgcStat == null)
            {
                Console.WriteLine("  某个后端没能起起来，见上面日志。");
                Console.WriteLine("RESULT=INCONCLUSIVE 后端未就绪");
                return 3;
            }

            bool bitbltBlack = bitbltStat.NonBlackPct < 1.0;
            bool wgcBlack = wgcStat.NonBlackPct < 1.0;

            if (bitbltBlack && wgcBlack)
            {
                Console.WriteLine("★ BitBlt 与 WGC **都黑**，而本进程能抓到内容。");
                Console.WriteLine("  → 说明问题在 **agent 进程侧**（DPI 上下文 / 桌面访问 / 会话归属），");
                Console.WriteLine("    而不是某一个抓屏后端的实现。");
                Console.WriteLine("RESULT=BLACK-AGENT-WIDE agent 两种后端全黑");
                return 1;
            }
            if (bitbltBlack && !wgcBlack)
            {
                Console.WriteLine("★ BitBlt 黑、WGC 正常 → 问题只在 BitBlt 实现。");
                Console.WriteLine("RESULT=BLACK-BITBLT-ONLY 仅 BitBlt 黑");
                return 1;
            }
            if (!bitbltBlack && wgcBlack)
            {
                Console.WriteLine("★ WGC 黑、BitBlt 正常 → 问题只在 WGC 实现（与 stream-core 的 A1 彩色结论一致）。");
                Console.WriteLine("RESULT=BLACK-WGC-ONLY 仅 WGC 黑");
                return 1;
            }

            Console.WriteLine("★ 两种后端都**不是全黑**。");
            Console.WriteLine("  若这是修复后回归：说明 WGC 帧池尺寸已正确（原先 GetSystemMetrics 全屏尺寸 → 现用 item.Size）。");
            Console.WriteLine("  若这是修复前基线：说明当时那个桌面没有可见内容，实验条件不成立，需换有内容的桌面重测。");
            Console.WriteLine("RESULT=OK BOTH-FINE 两种后端都能抓到内容");
            return 0;
        }

        private static void Row(string name, ImgStat? s)
        {
            if (s == null) { Console.WriteLine("  " + name.PadRight(15) + " (未取到)"); return; }
            Console.WriteLine("  " + name.PadRight(15)
                + (s.W + "x" + s.H).PadRight(13)
                + s.YMean.ToString("F1").PadLeft(7)
                + (s.NonBlackPct.ToString("F1") + "%").PadLeft(10)
                + s.RMean.ToString("F0").PadLeft(7)
                + s.GMean.ToString("F0").PadLeft(7)
                + s.BMean.ToString("F0").PadLeft(7)
                + "   " + (s.NonBlackPct < 1.0 ? "全黑" : "有内容"));
        }

        private static void PrintStat(string tag, ImgStat? s)
        {
            if (s == null) { Console.WriteLine("   " + tag + "：(失败)"); _fails++; return; }
            Console.WriteLine("   " + tag + " " + s.W + "x" + s.H
                + "  Y均值=" + s.YMean.ToString("F1")
                + "  非黑占比=" + s.NonBlackPct.ToString("F1") + "%"
                + "  RGB均值=" + s.RMean.ToString("F0") + "/" + s.GMean.ToString("F0") + "/" + s.BMean.ToString("F0")
                + "  → " + (s.NonBlackPct < 1.0 ? "**全黑**" : "有内容"));
        }

        // ── 本进程直接抓屏 ──────────────────────────────────────────────
        private static ImgStat? CaptureDirect(string outPath)
        {
            try
            {
                var b = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
                using var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(b.X, b.Y, 0, 0, new Size(b.Width, b.Height), CopyPixelOperation.SourceCopy);
                bmp.Save(outPath, ImageFormat.Png);
                Console.WriteLine("   已保存 " + outPath + " (" + (new FileInfo(outPath).Length / 1024) + " KB)");
                return Analyze(bmp);
            }
            catch (Exception ex) { Console.WriteLine("   CopyFromScreen 失败：" + ex.Message); return null; }
        }

        // ── 起一个后端，取 /live 的 JPEG 分析 ────────────────────────────
        private static async Task<ImgStat?> RunOneBackendAsync(string agentDir, int port, string token,
            string work, string mode, string tag)
        {
            string cap = Path.Combine(work, "cap-" + tag);
            Directory.CreateDirectory(cap);

            // 熔断：本工具在采样期间盯着 agent 内存，超限即杀
            await using var host = await ProbeHost.StartAsync(agentDir, port, token, cap,
                s => Console.WriteLine("   " + s), mode);
            if (host == null) { _fails++; return null; }

            var killer = StartMemoryGuard(host.AgentPid, 2048,
                msg => Console.WriteLine("   !! 熔断：" + msg));

            try
            {
                var rec = await host.SendAsync("{\"Cmd\":\"record\",\"Count\":15,\"X\":95,\"Delta\":10}");
                Console.WriteLine("   record：" + (rec != null && rec.Contains("\"Ok\":true") ? "已开始" : (rec ?? "无响应")));

                // 等有帧
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < 20)
                {
                    var st = await host.SendAsync("{\"Cmd\":\"recordstatus\"}") ?? "";
                    if (st.Contains("|frames=") && !st.Contains("|frames=0|"))
                    {
                        // 打印实际后端，确认环境变量真的生效
                        int i = st.IndexOf("|capture=", StringComparison.Ordinal);
                        string backend = i >= 0 ? st.Substring(i + 9).Split('"')[0] : "?";
                        int j = st.IndexOf("|size=", StringComparison.Ordinal);
                        string size = j >= 0 ? st.Substring(j + 6).Split('|')[0] : "?";
                        Console.WriteLine("   实测后端=" + backend + "  尺寸=" + size
                            + "  （请求 mode=" + mode + "）");
                        if (backend != "?") break;
                    }
                    await Task.Delay(400);
                }

                // 取 /live 的 JPEG（质量 95，减少"因为压缩而看不出内容"的可能）
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.Add("X-SoulLink-Token", token);
                var r = await http.GetAsync($"http://127.0.0.1:{port}/live?token={Uri.EscapeDataString(token)}");
                if (!r.IsSuccessStatusCode) { Console.WriteLine("   /live → " + (int)r.StatusCode); _fails++; return null; }
                byte[] jpeg = await r.Content.ReadAsByteArrayAsync();
                string p = Path.Combine(work, tag + "-live.jpg");
                await File.WriteAllBytesAsync(p, jpeg);
                Console.WriteLine("   /live → " + jpeg.Length + " B → " + p);

                using var ms = new MemoryStream(jpeg);
                using var bmp = new Bitmap(ms);
                return Analyze(bmp);
            }
            catch (Exception ex) { Console.WriteLine("   异常：" + ex.Message); _fails++; return null; }
            finally
            {
                killer.Cancel();
                await host.StopAsync();
            }
        }

        /// <summary>内存熔断：Private 超限就杀 agent，避免重演涨到 9.9GB。</summary>
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

        // ── 图像量化 ────────────────────────────────────────────────────
        public sealed class ImgStat
        {
            public int W, H;
            public double YMean;        // 亮度均值（BT.601）
            public double NonBlackPct;  // 非黑像素占比（RGB 之和 > 30 视为"非黑"）
            public double RMean, GMean, BMean;
            public long AllZeroPixels;  // 严格 RGB 全 0 的像素数
        }

        /// <summary>
        /// 逐像素统计。★ 一定要把"严格全 0"和"有内容但很暗"分开报：
        /// 前者是抓屏失败（没拿到数据），后者可能是颜色空间/伽马问题，两者修法完全不同。
        /// </summary>
        private static ImgStat Analyze(Bitmap bmp)
        {
            var s = new ImgStat { W = bmp.Width, H = bmp.Height };
            var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = Math.Abs(bd.Stride);
                byte[] buf = new byte[stride * bmp.Height];
                Marshal.Copy(bd.Scan0, buf, 0, buf.Length);

                long n = 0, nonBlack = 0, allZero = 0;
                double sumR = 0, sumG = 0, sumB = 0, sumY = 0;
                // 抽样步长：大图（2560x1368≈350万像素）全扫也能接受，但抽 3 像素一次更快
                int step = 3;
                for (int y = 0; y < bmp.Height; y += step)
                {
                    int row = y * stride;
                    for (int x = 0; x < bmp.Width; x += step)
                    {
                        int i = row + x * 4;
                        byte bb = buf[i], gg = buf[i + 1], rr = buf[i + 2];
                        n++;
                        sumR += rr; sumG += gg; sumB += bb;
                        sumY += 0.299 * rr + 0.587 * gg + 0.114 * bb;
                        if (rr + gg + bb > 30) nonBlack++;
                        if (rr == 0 && gg == 0 && bb == 0) allZero++;
                    }
                }
                if (n > 0)
                {
                    s.RMean = sumR / n; s.GMean = sumG / n; s.BMean = sumB / n;
                    s.YMean = sumY / n;
                    s.NonBlackPct = 100.0 * nonBlack / n;
                    s.AllZeroPixels = allZero;
                }
            }
            finally { bmp.UnlockBits(bd); }
            return s;
        }
    }
}
