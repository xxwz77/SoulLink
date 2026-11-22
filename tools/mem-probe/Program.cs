using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MemProbe
{
    /// <summary>
    /// 内存/句柄采样器：对指定 PID 每 N 秒采 WorkingSet / PrivateMemory / HandleCount，
    /// 输出 CSV，并自动判定「泄漏 / 正常波动 / 样本不足」。
    ///
    /// ★ 为什么单独写一个而不用 Process.HandleCount：
    ///   AGENTS.md 性能约束第 3 条明确写了 —— 取句柄数必须用 GetProcessHandleCount，
    ///   Process.HandleCount 每次 3~5ms，挂在 1Hz 循环里自己就吃掉 20% CPU。
    ///   采样本是为了观测泄漏，结果采样器自己成了负载，数据就不可信。
    ///
    /// ★ 为什么必须只按 PID 采：
    ///   项目里曾经按路径通配停进程，误杀了用户挂机的 dist\stable\SoulLink-Stable.exe。
    ///   本工具只接受一个明确的 PID，绝不做任何名字/路径匹配。
    ///
    /// ★ 为什么要有熔断（--max-mb）：
    ///   修复前的 /stream 泄漏可达 ~128 MB/s，30 秒就能把进程吃到 4 GB。
    ///   而本机已占 71% 内存（原神 6.5GB + BetterGI 1.7GB）。
    ///   采样时若不管，唯一结果是让用户的机器再次 OOM。
    ///   所以默认带一个上限：超过就停止采样并明确报告「已触发熔断」。
    ///
    /// 用法：
    ///   mem-probe --pid 1234 --seconds 60 --interval 1
    ///   mem-probe --pid 1234 --seconds 60 --interval 1 --csv out.csv --max-mb 1500
    ///   mem-probe --pid 1234 --seconds 900 --baseline-only      # 只采不判（长采样用）
    ///
    /// 退出码：0 正常；2 参数错；3 目标进程消失；4 触发熔断
    /// </summary>
    public static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessHandleCount(IntPtr hProcess, out uint handleCount);

        private static int _fails;

        public static int Main(string[] rawArgs)
        {
            var opt = ParseOptions(rawArgs);
            if (rawArgs.Length == 0 || opt.ContainsKey("help")) { Usage(); return rawArgs.Length == 0 ? 2 : 0; }
            if (opt.ContainsKey("selftest")) return SelfTest();

            if (!int.TryParse(Get(opt, "pid"), out int pid) || pid <= 0)
            {
                Console.Error.WriteLine("缺少或非法的 --pid");
                return 2;
            }
            int seconds = int.TryParse(Get(opt, "seconds"), out int s) && s > 0 ? s : 60;
            double interval = double.TryParse(Get(opt, "interval"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double iv) && iv > 0 ? iv : 1.0;
            double maxMb = double.TryParse(Get(opt, "max-mb"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double mm) && mm > 0 ? mm : 0;
            bool baselineOnly = opt.ContainsKey("baseline-only");
            bool quiet = opt.ContainsKey("quiet");
            string csvPath = Get(opt, "csv") ?? Path.Combine(Path.GetTempPath(),
                "mem-probe-" + pid + "-" + DateTime.Now.ToString("HHmmss") + ".csv");

            Process proc;
            try { proc = Process.GetProcessById(pid); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("找不到 PID " + pid + "：" + ex.Message);
                return 3;
            }

            string name = "";
            try { name = proc.ProcessName; } catch { }

            Console.WriteLine("== mem-probe · PID " + pid + " (" + name + ")");
            Console.WriteLine("   间隔 " + interval.ToString("F1") + " s · 时长 " + seconds + " s · CSV " + csvPath);
            Console.WriteLine("   句柄用 GetProcessHandleCount（非 Process.HandleCount，见 AGENTS.md 性能约束第 3 条）");
            if (maxMb > 0) Console.WriteLine("   熔断阈值 " + maxMb.ToString("F0") + " MB（超过即停止采样，避免加重系统内存压力）");
            Console.WriteLine();

            var samples = new List<Sample>();
            var sb = new StringBuilder();
            sb.AppendLine("elapsedSec,workingSetMB,privateMB,handleCount,cpuMs");
            double startCpu = 0;
            bool cpuOk = true;
            try { startCpu = proc.TotalProcessorTime.TotalMilliseconds; } catch { cpuOk = false; }

            int exitCode = 0;
            var clock = Stopwatch.StartNew();
            bool tripped = false;

            // 表头（非 CSV 模式给人看的）
            if (!quiet)
                Console.WriteLine("  t(s)   WorkingSet   Private   Handles   ΔWS/s");

            double lastWs = -1, lastT = 0;
            while (true)
            {
                if (clock.Elapsed.TotalSeconds > seconds) break;

                bool alive = true;
                try { proc.Refresh(); alive = !proc.HasExited; } catch { alive = false; }
                if (!alive)
                {
                    Console.WriteLine();
                    Console.WriteLine("!! 目标进程在 " + clock.Elapsed.TotalSeconds.ToString("F0") + " s 时消失（PID " + pid + "）");
                    exitCode = 3;
                    break;
                }

                double ws = 0, priv = 0, cpu = 0;
                try { ws = proc.WorkingSet64 / 1048576.0; } catch { }
                try { priv = proc.PrivateMemorySize64 / 1048576.0; } catch { }
                try { if (cpuOk) cpu = proc.TotalProcessorTime.TotalMilliseconds; } catch { cpuOk = false; }

                uint handles = 0;
                try
                {
                    if (!GetProcessHandleCount(proc.Handle, out handles)) handles = 0;
                }
                catch { }

                double t = clock.Elapsed.TotalSeconds;
                var smp = new Sample { T = t, WorkingSetMB = ws, PrivateMB = priv, Handles = handles, CpuMs = cpu };
                samples.Add(smp);
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0:F1},{1:F1},{2:F1},{3},{4:F0}", t, ws, priv, handles, cpu));

                if (!quiet)
                {
                    double delta = lastWs > 0 && t > lastT ? (ws - lastWs) / (t - lastT) : 0;
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "  {0,5:F0}  {1,9:F1}  {2,8:F1}  {3,7}   {4,7:F1}",
                        t, ws, priv, handles, delta));
                }

                lastWs = ws; lastT = t;

                // 熔断：保护本机（已占 71%），别让观测行为制造事故
                if (maxMb > 0 && ws > maxMb)
                {
                    Console.WriteLine();
                    Console.WriteLine("!! 熔断：WorkingSet " + ws.ToString("F0") + " MB 超过阈值 "
                        + maxMb.ToString("F0") + " MB，停止采样");
                    Console.WriteLine("   这本身就是强泄漏证据（" + (t > 0 ? (ws - samples[0].WorkingSetMB) / t : 0).ToString("F1")
                        + " MB/s）");
                    tripped = true;
                    exitCode = 4;
                    break;
                }

                Thread.Sleep(TimeSpan.FromSeconds(interval));
            }

            try { File.WriteAllText(csvPath, sb.ToString(), new UTF8Encoding(false)); } catch (Exception ex)
            {
                Console.Error.WriteLine("写 CSV 失败：" + ex.Message);
            }

            Console.WriteLine();
            Console.WriteLine("== 采样完成：" + samples.Count + " 个点，CSV → " + csvPath);

            if (samples.Count < 2)
            {
                Console.WriteLine("RESULT=不足 样本太少，无法判定");
                return exitCode == 0 ? 3 : exitCode;
            }

            var verdict = Judge(samples, baselineOnly);
            PrintVerdict(verdict, samples, seconds, interval);

            if (exitCode != 0) return exitCode;
            return verdict.IsLeak ? 1 : 0;
        }

        // ── 自检：证明判据能区分「泄漏」和「不泄漏」 ──────────────────────

        /// <summary>
        /// 用两组合成数据喂进同一套判据，验证：
        ///   ① 稳定线性上涨 → 判为泄漏
        ///   ② 锯齿波动 / 平稳 → 不判泄漏
        ///
        /// ★ 为什么要自检：判定工具本身出错是最难发现的失败模式 ——
        ///   如果判据把所有曲线都判成"正常"，修复前后跑两次都是 OK，
        ///   看起来"修好了"，实际什么都没验证。这条自检就是防这个。
        /// </summary>
        private static int SelfTest()
        {
            Console.WriteLine("== mem-probe 判据自检（合成数据，不采样真实进程）");
            Console.WriteLine();
            int fails = 0;

            void Case(string name, List<Sample> data, bool expectLeak)
            {
                var v = Judge(data, false);
                bool ok = v.IsLeak == expectLeak;
                Console.WriteLine((ok ? "ok    " : "FAIL  ") + name);
                Console.WriteLine("        期望 " + (expectLeak ? "LEAK" : "OK")
                    + " · 实得 " + v.Kind + "（斜率 " + v.SlopeMBPerSec.ToString("F2")
                    + " MB/s, R²=" + v.R2.ToString("F3") + "）");
                if (!ok) fails++;
            }

            // ① 陡峭线性泄漏：52MB 起，每秒 +120MB（对标实测的 128MB/s）
            var leak = new List<Sample>();
            for (int i = 0; i <= 30; i++)
                leak.Add(new Sample { T = i, WorkingSetMB = 52 + 120 * i, PrivateMB = 50 + 120 * i, Handles = 330 });
            Case("陡峭线性上涨 120 MB/s → 应判泄漏", leak, true);

            // ② 低速率泄漏：每秒 +1MB（25 分钟能涨 1.5GB，属真问题）
            var slowLeak = new List<Sample>();
            for (int i = 0; i <= 60; i++)
                slowLeak.Add(new Sample { T = i, WorkingSetMB = 100 + 1.0 * i, PrivateMB = 90 + 1.0 * i, Handles = 400 });
            Case("缓慢线性上涨 1 MB/s → 应判泄漏", slowLeak, true);

            // ③ 平稳：小幅抖动，不涨
            var flat = new List<Sample>();
            var rnd = new Random(42);
            for (int i = 0; i <= 60; i++)
                flat.Add(new Sample { T = i, WorkingSetMB = 100 + rnd.NextDouble() * 2 - 1, PrivateMB = 90, Handles = 400 });
            Case("平稳 + ±1MB 抖动 → 不应判泄漏", flat, false);

            // ④ 锯齿：涨上去又被 GC 收回（最常见的"假泄漏"）
            var saw = new List<Sample>();
            for (int i = 0; i <= 60; i++)
                saw.Add(new Sample { T = i, WorkingSetMB = 100 + (i % 10) * 8, PrivateMB = 90, Handles = 400 });
            Case("锯齿波动（涨了又回落）→ 不应判泄漏", saw, false);

            // ⑤ 启动爬坡后平稳（交接文档特意提醒过的正常现象）
            var ramp = new List<Sample>();
            for (int i = 0; i <= 60; i++)
                ramp.Add(new Sample
                {
                    T = i,
                    WorkingSetMB = i <= 10 ? 50 + i * 6 : 110 + rnd.NextDouble() * 2,
                    PrivateMB = 90, Handles = 400
                });
            Case("启动爬坡后平稳 → 不应判泄漏", ramp, false);

            // ⑥ 下降（GC 回收）
            var down = new List<Sample>();
            for (int i = 0; i <= 60; i++)
                down.Add(new Sample { T = i, WorkingSetMB = 200 - 1.5 * i, PrivateMB = 190, Handles = 400 });
            Case("持续下降 → 不应判泄漏", down, false);

            Console.WriteLine();
            if (fails == 0) { Console.WriteLine("RESULT=PASS 判据自检全部通过（6/6）"); return 0; }
            Console.WriteLine("RESULT=FAIL 判据自检失败 " + fails + " 项 —— 先用这个结果怀疑工具，别拿去判真实进程");
            return 1;
        }

        // ── 判据 ────────────────────────────────────────────────────────────

        private sealed class Sample
        {
            public double T;
            public double WorkingSetMB;
            public double PrivateMB;
            public uint Handles;
            public double CpuMs;
        }

        private sealed class Verdict
        {
            public bool IsLeak;
            public string Kind = "";
            public double SlopeMBPerSec;      // 后半段线性斜率
            public double R2;                 // 拟合优度：越接近 1 越像"稳定泄漏"
            public double GrowthMB;           // 观察窗口内的净增长
            public double CvPct;              // 波动系数（标准差/均值）
            public double DurationSec;
            public bool EnoughDuration;       // 时长是否够定性
            public string Advice = "";
        }

        /// <summary>
        /// 判定逻辑（写在代码里而不是文档里，避免文档与实现漂移）。
        ///
        /// 为什么不看「首尾差」：启动期有 JIT、缓存预热、GC 策略调整，
        /// 前几秒的自然爬坡很容易被误判成泄漏（交接文档也提醒过"启动爬坡是正常的"）。
        /// 所以只取**后半段**做线性回归，并看拟合优度：
        ///   · 真泄漏：斜率大 + R² 高（稳定单调上涨）
        ///   · 正常波动：斜率为 0 或 R² 低（锯齿、来回）
        /// </summary>
        private static Verdict Judge(List<Sample> xs, bool baselineOnly)
        {
            var v = new Verdict();
            var first = xs[0];
            var last = xs[xs.Count - 1];
            v.DurationSec = last.T - first.T;
            v.GrowthMB = last.WorkingSetMB - first.WorkingSetMB;

            // 波动系数（整段）
            double mean = xs.Average(s => s.WorkingSetMB);
            double sd = Math.Sqrt(xs.Average(s => Math.Pow(s.WorkingSetMB - mean, 2)));
            v.CvPct = mean > 0 ? sd / mean * 100.0 : 0;

            // 后半段线性回归
            int half = xs.Count / 2;
            var tail = xs.Skip(half).ToList();
            if (tail.Count >= 3)
            {
                double n = tail.Count;
                double sumX = tail.Sum(s => s.T);
                double sumY = tail.Sum(s => s.WorkingSetMB);
                double sumXY = tail.Sum(s => s.T * s.WorkingSetMB);
                double sumXX = tail.Sum(s => s.T * s.T);
                double denom = n * sumXX - sumX * sumX;
                if (Math.Abs(denom) > 1e-9)
                {
                    double slope = (n * sumXY - sumX * sumY) / denom;
                    double intercept = (sumY - slope * sumX) / n;
                    // R²
                    double ssTot = tail.Sum(s => Math.Pow(s.WorkingSetMB - sumY / n, 2));
                    double ssRes = tail.Sum(s =>
                    {
                        double pred = intercept + slope * s.T;
                        return Math.Pow(s.WorkingSetMB - pred, 2);
                    });
                    v.SlopeMBPerSec = slope;
                    v.R2 = ssTot > 1e-9 ? 1.0 - ssRes / ssTot : 0;
                }
            }

            // AGENTS.md：句柄增长要观察 >15 分钟才能定性。
            // 内存同理 —— 但内存比句柄更容易在短窗口内暴露陡峭泄漏。
            v.EnoughDuration = v.DurationSec >= 60;

            if (baselineOnly)
            {
                v.Kind = "仅采样（未判定）";
                v.Advice = "用了 --baseline-only，不做泄漏判定。适用于长时间后台采样。";
                return v;
            }

            // 判定顺序：先看有没有陡峭泄漏（最危险），再看波动
            if (v.SlopeMBPerSec > 5.0 && v.R2 > 0.9)
            {
                v.IsLeak = true;
                v.Kind = "确定泄漏";
                v.Advice = "后半段以 " + v.SlopeMBPerSec.ToString("F1") + " MB/s 稳定线性上涨（R²="
                    + v.R2.ToString("F3") + "）。这是典型泄漏曲线，必须修。";
            }
            else if (v.SlopeMBPerSec > 0.5 && v.R2 > 0.9)
            {
                v.IsLeak = true;
                v.Kind = "疑似泄漏（速率较低）";
                v.Advice = "后半段以 " + v.SlopeMBPerSec.ToString("F2") + " MB/s 上涨（R²="
                    + v.R2.ToString("F3") + "）。速率不高但趋势稳定，"
                    + (v.EnoughDuration ? "建议延长采样到 15 分钟确认。" : "采样时长不足，建议延长到 15 分钟再定性。");
            }
            else if (v.SlopeMBPerSec <= 0.5 && Math.Abs(v.SlopeMBPerSec) < 0.5)
            {
                v.Kind = "平稳";
                v.Advice = "无有效增长（斜率 " + v.SlopeMBPerSec.ToString("F2") + " MB/s，波动系数 "
                    + v.CvPct.ToString("F1") + "%）。"
                    + (v.EnoughDuration ? "" : "但采样不足 60 秒，仅作参考。");
            }
            else if (v.SlopeMBPerSec < -0.5)
            {
                v.Kind = "下降（正常）";
                v.Advice = "内存在回落（斜率 " + v.SlopeMBPerSec.ToString("F2")
                    + " MB/s），常见于 GC 回收或缓存释放，不是泄漏。";
            }
            else
            {
                v.Kind = "波动（无法定性）";
                v.Advice = "斜率 " + v.SlopeMBPerSec.ToString("F2") + " MB/s 但 R²=" + v.R2.ToString("F3")
                    + " 很低，说明不是单调上涨而是锯齿波动。需要更长采样才能定性。";
            }
            return v;
        }

        private static void PrintVerdict(Verdict v, List<Sample> xs, int seconds, double interval)
        {
            var first = xs[0];
            var last = xs[xs.Count - 1];
            Console.WriteLine();
            Console.WriteLine("== 判定");
            Console.WriteLine("   采样跨度      : " + v.DurationSec.ToString("F0") + " s（" + xs.Count + " 点，间隔 "
                + interval.ToString("F1") + " s）");
            Console.WriteLine("   WorkingSet    : " + first.WorkingSetMB.ToString("F0") + " → "
                + last.WorkingSetMB.ToString("F0") + " MB（净变化 " + v.GrowthMB.ToString("+0;-0;0") + " MB）");
            Console.WriteLine("   Private       : " + first.PrivateMB.ToString("F0") + " → "
                + last.PrivateMB.ToString("F0") + " MB");
            Console.WriteLine("   Handles       : " + first.Handles + " → " + last.Handles
                + "（净变化 " + ((long)last.Handles - first.Handles) + "）");
            Console.WriteLine("   后半段斜率    : " + v.SlopeMBPerSec.ToString("F2") + " MB/s（R²="
                + v.R2.ToString("F3") + "）");
            Console.WriteLine("   波动系数      : " + v.CvPct.ToString("F1") + " %");
            Console.WriteLine("   判定          : " + v.Kind);
            Console.WriteLine("   说明          : " + v.Advice);
            Console.WriteLine();
            Console.WriteLine("RESULT=" + (v.IsLeak ? "LEAK" : "OK") + " (" + v.Kind + ")");
        }

        // ── 参数 ────────────────────────────────────────────────────────────

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
            Console.WriteLine("mem-probe --pid <PID> [选项]");
            Console.WriteLine();
            Console.WriteLine("  --seconds N      采样时长，默认 60");
            Console.WriteLine("  --interval S     采样间隔秒，默认 1.0");
            Console.WriteLine("  --csv PATH       CSV 输出路径（默认 %TEMP%\\mem-probe-<pid>-<HHmmss>.csv）");
            Console.WriteLine("  --max-mb N       熔断阈值（WorkingSet 超过即停止并报告），默认不限");
            Console.WriteLine("                   本机内存紧张时建议设 1500~2000，避免观测行为制造事故");
            Console.WriteLine("  --baseline-only  只采不判（长采样用）");
            Console.WriteLine("  --quiet          不打印逐点表格，只出结论");
            Console.WriteLine();
            Console.WriteLine("自检（验证判据本身是否可信，不需要任何外部进程）：");
            Console.WriteLine("  mem-probe --selftest");
        }
    }
}
