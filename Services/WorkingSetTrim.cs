using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SoulLink.Services
{
    /// <summary>
    /// 连接后延迟回收工作集（T12，降常驻内存）。
    ///
    /// ★ 背景（HANDOFF 4.2）：Broker 服务 61 MB + agent 47 MB × 2 常驻；
    ///   全仓此前 0 处 EmptyWorkingSet。内存大头是投送/连接路径刚写热的页，
    ///   静置后大多数已可换出。
    ///
    /// ★ 机制（借鉴 WorkingSetTrimScheduler 的思想）：
    ///   1) **延迟 10 秒**：RequestTrim() 只把「最后请求时间」前移；TickHub 节拍到点
    ///      检查 now - last ≥ 10s 才真正回收。连接瞬间回收会把刚写热的页立刻赶走，
    ///      下一次操作重新缺页、卡顿——所以必须等静置。
    ///   2) **任务执行中跳过，但仍更新时间戳**：自动化任务运行期间（BeginTask 计数 > 0）
    ///      不回收，只把静置计时重新推满（任务结束后的 10 秒才是真静置）。
    ///      否则空闲巡检每轮都会重试回收、日志刷满。
    ///   3) **走 TickHub.RegisterBackground，零新建 DispatcherTimer**（AGENTS.md 性能
    ///      约束 1）。1 秒节拍上的回调只做一次 volatile long 比较（纳秒级），
    ///      无挂起请求时直接 return——不是往 UI 线程插队的轮询。
    ///   4) **幂等**：重复 RequestTrim 只前移时间戳，不叠加订阅、不重复回收；
    ///      回收完成后清 _pending，直到下一次请求才可能再回收。
    ///
    /// ★ 回收目标（白名单，精确路径匹配）：
    ///   · 本进程（SoulLink 主程序，agent 的落点/宿主）；
    ///   · Broker 服务进程（C:\Program Files\SoulLinkDev\SoulLink.exe）；
    ///   · agent 进程（C:\Program Files\SoulLinkDev\SoulLink.Agent.exe）。
    ///   其它进程（BGI/游戏/挂机版）一概不碰——跨会话权限与误伤风险。
    ///
    /// ★ 开关（出问题能立刻停）：
    ///   · 配置 Settings.WorkingSetTrimEnabled（默认 true）；
    ///   · 环境变量 SOULLINK_TRIM_DISABLED=1 硬关（不落盘即可停，优先级最高）。
    /// </summary>
    public static class WorkingSetTrim
    {
        // ── P/Invoke ────────────────────────────────────────────────────
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        // ★ EmptyWorkingSet 的导出在 psapi.dll（kernel32 只有 K32 前缀版）。
        //   实测（2026-11-24 probe）：kernel32 导入会抛 EntryPointNotFoundException
        //   被吞掉 → 回收从未真正执行。psapi.dll 的经典导出在所有受支持系统上存在。
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint PROCESS_SET_QUOTA = 0x0100;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        // ── 参数 ────────────────────────────────────────────────────────
        /// <summary>静置时长（秒）：最后一次连接动作后等这么久才回收。</summary>
        public const int IdleDelaySeconds = 10;

        /// <summary>TickHub 节拍粒度（秒）。回调只做时间戳比较，纳秒级。</summary>
        private const int TickSeconds = 1;

        // ── 状态（全部无锁：Interlocked / volatile）─────────────────────
        /// <summary>是否有挂起的回收请求（0=无 1=有）。</summary>
        private static int _pending;

        /// <summary>最后请求时间（UTC Ticks）。RequestTrim 前移；任务执行中也前移。</summary>
        private static long _lastRequestUtcTicks;

        /// <summary>正在执行的自动化任务计数（>0 = 忙，跳过回收但推进时间戳）。</summary>
        private static int _tasksRunning;

        /// <summary>TickHub 订阅句柄（只挂一次）。</summary>
        private static IDisposable? _tickSub;

        /// <summary>上次实际回收时间（防重复回收 + 诊断）。</summary>
        private static long _lastTrimUtcTicks;

        /// <summary>硬开关：环境变量 SOULLINK_TRIM_DISABLED=1 时全程不动作。</summary>
        private static bool EnvDisabled =>
            string.Equals(Environment.GetEnvironmentVariable("SOULLINK_TRIM_DISABLED"), "1", StringComparison.OrdinalIgnoreCase);

        /// <summary>总开关：环境变量硬关 > 配置开关。读配置失败按「关」处理（保守）。</summary>
        private static bool Enabled
        {
            get
            {
                if (EnvDisabled) return false;
                try { return App.Config?.Settings?.WorkingSetTrimEnabled == true; }
                catch { return false; }
            }
        }

        // ── 公开 API ────────────────────────────────────────────────────

        /// <summary>
        /// 请求一次延迟回收（幂等）：连接成功 / 自动化任务启动时调用。
        /// 只前移静置计时起点，不立即回收；10 秒静置后由 TickHub 节拍执行。
        /// 重复调用不叠加订阅、不重复回收（_pending 只置一次，时间戳前移即可）。
        /// </summary>
        public static void RequestTrim(string reason)
        {
            if (!Enabled) return;
            Interlocked.Exchange(ref _lastRequestUtcTicks, DateTime.UtcNow.Ticks);
            if (Interlocked.CompareExchange(ref _pending, 1, 0) == 0)
                EnsureTickSubscribed();
        }

        /// <summary>自动化任务开始：忙计数 +1，并把静置计时推满（任务中不回收）。</summary>
        public static void BeginTask()
        {
            Interlocked.Increment(ref _tasksRunning);
            Interlocked.Exchange(ref _lastRequestUtcTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>自动化任务结束：忙计数 -1（归零后从当前时刻重新计 10 秒静置）。</summary>
        public static void EndTask()
        {
            Interlocked.Decrement(ref _tasksRunning);
        }

        // ── 内部 ────────────────────────────────────────────────────────

        private static void EnsureTickSubscribed()
        {
            if (_tickSub != null) return;
            lock (typeof(WorkingSetTrim))
            {
                if (_tickSub != null) return;
                try
                {
                    _tickSub = TickHub.RegisterBackground("WorkingSetTrim", TickSeconds, OnTick);
                }
                catch (Exception ex)
                {
                    // 订阅失败：清 pending，宁可不做也不抛。
                    Interlocked.Exchange(ref _pending, 0);
                    try { LogBus.Warn("工作集回收订阅失败", ex.Message); } catch { }
                }
            }
        }

        /// <summary>
        /// TickHub 后台节拍：无挂起请求时纳秒级返回；有请求但未静置够 10 秒也直接返回。
        /// 忙碌时**更新时间戳**（保证任务结束后重新计满 10 秒），不回收。
        /// </summary>
        private static void OnTick()
        {
            try
            {
                if (Interlocked.CompareExchange(ref _pending, 0, 0) == 0) return;   // 无请求

                long last = Interlocked.Read(ref _lastRequestUtcTicks);
                DateTime lastAt = new DateTime(last, DateTimeKind.Utc);
                double idle = (DateTime.UtcNow - lastAt).TotalSeconds;
                if (idle < IdleDelaySeconds) return;   // 还没静置够

                // ★ 任务执行中：跳过回收，但把时间戳推到现在——
                //   否则任务期间的每一拍都会重试（到点又判失败），日志/行为每轮刷一遍。
                if (Interlocked.CompareExchange(ref _tasksRunning, 0, 0) > 0)
                {
                    Interlocked.Exchange(ref _lastRequestUtcTicks, DateTime.UtcNow.Ticks);
                    return;
                }

                // 到点：执行回收（挂起标志先清，成功与否都算本轮完成——幂等）。
                Interlocked.Exchange(ref _pending, 0);
                Interlocked.Exchange(ref _lastTrimUtcTicks, DateTime.UtcNow.Ticks);
                TrimCore();
            }
            catch { /* 回收失败不影响下一轮 */ }
        }

        /// <summary>
        /// 实际回收：本进程 + 白名单路径的 Broker/agent 进程。
        /// 每个目标独立 try/catch，权限不足/进程不存在/句柄打不开就跳过该目标。
        /// </summary>
        private static void TrimCore()
        {
            long selfBefore = 0;
            try { using var p = Process.GetCurrentProcess(); selfBefore = p.WorkingSet64; } catch { }

            int trimmed = 0;

            // ① 本进程（agent 落点/宿主）：GetCurrentProcess 伪句柄，零权限问题。
            try
            {
                if (EmptyWorkingSet(GetCurrentProcess())) trimmed++;
            }
            catch { }

            // ② 白名单进程：Broker 服务 + agent（按精确路径匹配，绝不误伤别的版本/目录）
            foreach (string path in new[] { BrokerInstall.ServiceExe, BrokerInstall.AgentExe })
            {
                try
                {
                    string want = Path.GetFullPath(path);
                    foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
                    {
                        try
                        {
                            string? actual = null;
                            try { actual = p.MainModule?.FileName; } catch { }
                            if (actual == null || !string.Equals(Path.GetFullPath(actual), want, StringComparison.OrdinalIgnoreCase))
                                continue;   // 同名但不同路径（挂机版/临时副本）→ 不碰
                            IntPtr h = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                            if (h == IntPtr.Zero) continue;   // 权限不足（Broker 是 SYSTEM）→ 跳过
                            try { if (EmptyWorkingSet(h)) trimmed++; }
                            finally { try { CloseHandle(h); } catch { } }
                        }
                        finally { try { p.Dispose(); } catch { } }
                    }
                }
                catch { }
            }

            // 一次回收一条日志（不是每拍刷屏：只有真正执行了回收才记）。
            long selfAfter = 0;
            try { using var p = Process.GetCurrentProcess(); selfAfter = p.WorkingSet64; } catch { }
            long freed = selfBefore - selfAfter;
            if (freed > 0)
            {
                try
                {
                    LogBus.Info("工作集回收完成",
                        "目标 " + trimmed + " 个进程 · 本进程 " + (selfBefore / 1048576) + " MB → "
                        + (selfAfter / 1048576) + " MB（回收 " + (freed / 1048576) + " MB）· 静置 "
                        + IdleDelaySeconds + "s 后执行");
                }
                catch { }
            }
        }

        /// <summary>诊断：距下一次回收还差多少秒（测试用；无请求返回 -1）。</summary>
        public static int SecondsUntilTrim()
        {
            if (Interlocked.CompareExchange(ref _pending, 0, 0) == 0) return -1;
            long last = Interlocked.Read(ref _lastRequestUtcTicks);
            double idle = (DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc)).TotalSeconds;
            int remain = IdleDelaySeconds - (int)idle;
            if (Interlocked.CompareExchange(ref _tasksRunning, 0, 0) > 0) return IdleDelaySeconds;
            return remain;
        }

        /// <summary>诊断：上次实际回收的 UTC 时间（从未回收返回 MinValue）。</summary>
        public static DateTime LastTrimUtc =>
            _lastTrimUtcTicks == 0 ? DateTime.MinValue : new DateTime(_lastTrimUtcTicks, DateTimeKind.Utc);
    }
}
