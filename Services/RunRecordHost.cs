using System;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>
    /// 运行记录（SQLite）的**集成宿主**：启动重建、留存清理、退出排空。
    ///
    /// ── 为什么单独一个类 ───────────────────────────────────────────────
    ///   <see cref="RunRecordStore"/> 只管存储（建表/写队列/查询/清理 SQL），
    ///   <see cref="RunRecordCollector"/> 只管归因（纯内存 + 入队）。
    ///   「什么时候启动、什么时候清理、留存几天」属于策略，放这里，
    ///   三者都不碰日志读取（唯一入口仍是 BgiLogCenter.ProcessChunk）。
    ///
    /// ── 硬约束 ─────────────────────────────────────────────────────────
    ///   · **不新建 DispatcherTimer**：周期任务一律 TickHub.RegisterBackground
    ///     （AGENTS.md 性能约束 1；TickHub.cs:95-107）。
    ///   · **不扫日志、不枚举进程、不建 FileSystemWatcher**：本类只跟 DB 和配置打交道。
    ///   · 回调在线程池执行（RegisterBackground），不占 UI 线程。
    ///
    /// ── 触发时机（契约 DATA-CENTER-RUN-DB-20261125.md §5.5 / §6.2）────────
    ///   ① 启动后一次：EnsureBootedAsync（把 open 行灌回内存窗口）+ PurgeOlderThan(days)；
    ///   ② 之后每 3600 拍（≈1 小时）一次清理 —— 跨天由此覆盖，不另开日期轮询；
    ///   ③ 退出时 FlushAsync（有界等待），保证最后一次入队不丢。
    ///
    /// ── 失败语义 ───────────────────────────────────────────────────────
    ///   存储层自身是「失败即降级丢弃 + error.log 一条警告」（RunRecordStore.Disable）；
    ///   本类再加一道**单次警告**闩锁：连续失败只记第一条，恢复后闩锁复位。
    /// </summary>
    public static class RunRecordHost
    {
        /// <summary>清理节拍（秒）。3600 拍 ≈ 1 小时；TickHub 的节拍是 1 秒。</summary>
        private const int CleanupEverySeconds = 3600;

        /// <summary>留存天数的默认值与上限（契约 §6.1：缺省 7，合法 [1,365]）。</summary>
        public const int DefaultRetentionDays = 7;
        public const int MaxRetentionDays = 365;

        private static readonly object Gate = new();
        private static bool _started;
        private static IDisposable? _cleanupSub;
        private static int _warnLatch;

        /// <summary>启动过几次（诊断用；保证幂等）。</summary>
        public static int StartCount { get; private set; }

        /// <summary>最近一次清理删除的行数（诊断用；-1 表示还没跑过）。</summary>
        public static int LastPurgedRows { get; private set; } = -1;

        /// <summary>最近一次清理的时刻（诊断用）。</summary>
        public static DateTime LastPurgeAt { get; private set; } = DateTime.MinValue;

        /// <summary>
        /// 把配置里的留存天数归一成合法值。
        /// 缺失/≤0/垃圾值 → 7；&gt;365 → 365（与 RunRecordStore.Purge 的 clamp 一致）。
        /// </summary>
        public static int NormalizeRetentionDays(int configured)
            => configured <= 0 ? DefaultRetentionDays : Math.Min(configured, MaxRetentionDays);

        /// <summary>当前生效的留存天数（读 App.Config，读不到就用默认 7）。</summary>
        public static int RetentionDays
        {
            get
            {
                try { return NormalizeRetentionDays(App.Config?.Settings?.RunDbRetentionDays ?? DefaultRetentionDays); }
                catch { return DefaultRetentionDays; }
            }
        }

        /// <summary>
        /// 启动集成：重建 open 窗口 + 首次清理 + 注册低频清理拍。
        /// **幂等**，重复调用无副作用（第二次直接返回）。
        /// 只应在进程启动路径调用一次（App.xaml.cs ShowMainWindow，紧邻 AccountStateCenter.Start）。
        /// </summary>
        public static void Start()
        {
            lock (Gate)
            {
                if (_started) return;
                _started = true;
                StartCount++;
            }

            // ① 启动重建 + 首次清理：全放后台线程，绝不挡启动（DB 首次建库可能要几十 ms）。
            _ = Task.Run(async () =>
            {
                try { await RunRecordCollector.EnsureBootedAsync().ConfigureAwait(false); }
                catch (Exception ex) { WarnOnce("启动重建 open 运行窗口失败", ex); }

                // ★ t10：状态段重启沿用（进行中的 idle/not_running 段继续，不凭空开新段）
                try { await RunRecordCollector.EnsureSpanBootAsync().ConfigureAwait(false); }
                catch (Exception ex) { WarnOnce("启动重建状态段失败", ex); }

                await PurgeOnceAsync("启动").ConfigureAwait(false);
            });

            // ② 低频清理拍：跨天由「每小时一次」覆盖，不新增日期轮询、不新建 DispatcherTimer。
            try
            {
                _cleanupSub = TickHub.RegisterBackground("RunRecordHost.Cleanup", CleanupEverySeconds,
                    () => _ = PurgeOnceAsync("周期"));
            }
            catch (Exception ex) { WarnOnce("注册运行记录清理节拍失败", ex); }
        }

        /// <summary>
        /// 立即清理一次（内部/诊断/验收用）。幂等：无过期行时返回 0，不做任何写放大。
        /// 返回删除的行数；数据库不可用时返回 0 并只记一次警告。
        /// </summary>
        public static async Task<int> PurgeOnceAsync(string reason)
        {
            int days = RetentionDays;
            int deleted;
            try
            {
                deleted = await RunRecordStore.PurgeOlderThanAsync(days).ConfigureAwait(false);
                // t11/t12：沿用既有小时 TickHub，只重算今天/昨天；昨天封盘后不重复写历史。
                foreach (string account in await RunRecordStore.QuerySpanAccountsAsync().ConfigureAwait(false))
                    await RunRecordStore.UpdateDaySpansForAccountAsync(account).ConfigureAwait(false);
                foreach (string account in await RunRecordStore.QueryRunAccountsForDaySpanAsync().ConfigureAwait(false))
                    await RunRecordStore.UpdateDaySpansForAccountAsync(account).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WarnOnce("运行记录清理失败（" + days + " 天留存）", ex);
                return 0;
            }

            LastPurgedRows = deleted;
            LastPurgeAt = DateTime.Now;
            Interlocked.Exchange(ref _warnLatch, 0);   // 成功即复位，下次失败还能再报一条

            if (!RunRecordStore.IsHealthy)
            {
                WarnOnce("运行记录数据库不可用，清理被跳过（" + days + " 天留存）",
                    new InvalidOperationException("RunRecordStore.IsHealthy=false"));
                return 0;
            }

            if (deleted > 0)
            {
                try
                {
                    LogBus.Info("运行记录清理",
                        "删除 " + deleted + " 条超过 " + days + " 天的记录（" + reason + "）");
                }
                catch { }
            }
            return deleted;
        }

        /// <summary>
        /// 退出前排空写队列（有界等待，最多 2 秒）。失败静默 —— 退出路径不允许抛。
        /// </summary>
        public static void Shutdown()
        {
            try { _cleanupSub?.Dispose(); } catch { }
            _cleanupSub = null;
            // ★ t10：退出时关闭所有进行中的 BGI 状态段（S：避免留下永远打开的段），
            //   先闭段再排空写队列，闭段操作才能落库。
            try { RunRecordCollector.ShutdownSpans(); } catch { }
            try
            {
                var flush = RunRecordStore.FlushAsync();
                flush.Wait(2000);
            }
            catch { }
        }

        /// <summary>同一个原因只写一条警告（连续失败不刷爆 error.log）。</summary>
        private static void WarnOnce(string what, Exception? ex)
        {
            if (Interlocked.Exchange(ref _warnLatch, 1) != 0) return;
            try { LogBus.Warn(what, ex?.Message ?? ""); } catch { }
            try { if (ex != null) App.AppendErrorLog("RunRecordHost", ex); } catch { }
        }
    }
}
