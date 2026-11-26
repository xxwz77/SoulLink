using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SoulLink.Models;
using System.Threading;

namespace SoulLink.Services
{
    /// <summary>
    /// BGI 日志数据中心：**唯一**读取 BGI 日志的地方。
    ///
    /// ── 为什么重写（2.17.44 实测）──────────────────────────────────
    ///   旧版 BusinessDayData + AccountStateCenter 在 A1 满速写日志时：
    ///     每 1.5 秒把「今天 53 MB + 昨天 61 MB」拼接成 88 MB 字符串，
    ///     Split 成 150 万行，逐行正则 + DateTime.TryParse，再 Tail(50MB)。
    ///   代价：88% 单核、1.1 GB 工作集、每秒 1.1 GB 垃圾对象。
    ///   同一条日志单遍流式扫描只要 130 ms、分配 1 MB。
    ///
    /// ── 现在的模型 ────────────────────────────────────────────────
    ///   · 每个账号一个**字节游标**，从 cursor 读到 EOF，读过的字节永不再读；
    ///   · 启动时（或首次见到该文件）一次全量扫，之后只吃增量；
    ///   · 不保留原文，只留「关键信息 + 很小的尾窗」；
    ///   · 扫完立刻丢掉缓冲，必要时压缩 LOH（内存及时还给系统）。
    ///
    /// ── 未绑定 BGI 路径的账号 ─────────────────────────────────────
    ///   零 IO。不探测、不开文件、不建跟随器，界面按「未绑定」显示。
    /// </summary>
    public static class BgiLogCenter
    {
        // ── 预算 ────────────────────────────────────────────────────
        /// <summary>尾窗最多保留多少行（界面展示 + 日志条件匹配用）。</summary>
        public const int TailMaxLines = 800;
        /// <summary>尾窗最多保留多少字符（硬上限，防止超长行撑爆内存）。</summary>
        public const int TailBudgetChars = 200_000;
        /// <summary>单次 tick 最多读多少字节；超了下个 tick 继续，避免长卡顿。</summary>
        ///
        /// ★★ t63（architect 审查 P0-1，卡死元凶之一）：
        ///   原来是 32MB —— 首次全量扫描时，一个账号一个 tick 就可能读满 32MB，
        ///   而 ProcessChunk 要跑 5 个解析器 × ~15 个正则：
        ///     32MB × 15 正则 × 5 解析器 ≈ 单账号 10 秒
        ///   而 ServiceTick 是**串行 foreach 所有账号、无单轮超时**：
        ///     3 个账号 × 10 秒 = 30 秒卡死一轮
        ///   用户实测「有配置就卡死、无配置就流畅」正是这个原因（无账号时 Tick 直接 return）。
        ///
        ///   2MB 的依据：真实 A1 4MB chunk 实测扫描约 1.3s，2MB 约 0.65s；
        ///   3 个账号串行约 2s，在一秒节拍内可接受；积压靠字节游标幂等续读，不会丢数据。
        private const int MaxBytesPerTick = 2 * 1024 * 1024;

        /// <summary>
        /// ★★ t63：单轮 ServiceTick 的总耗时预算。
        ///   即使每账号只读 2MB，账号多时仍会超时；超预算就让出，剩下的下 tick 继续。
        ///   （字节游标保证幂等，绝不会漏读。）
        /// </summary>
        private const int ServiceTickBudgetMs = 1200;
        /// <summary>进程/日志目录探测的缓存时间。</summary>
        private static readonly TimeSpan DiscoverTtl = TimeSpan.FromSeconds(2);

        private sealed class Entry
        {
            public string AccountId = "";
            public string UserName = "";
            public string BgiExe = "";
            public string LogFile = "";
            public long Cursor;
            public long FileLength;
            public DateTime LastWrite = DateTime.MinValue;

            /// <summary>上一行没写完的残字节（等下次补齐再解析）。</summary>
            public byte[] Pending = new byte[64 * 1024];
            public int PendingLen;

            /// <summary>
            /// 该账号日志文件的 FileSystemWatcher（事件驱动）。
            /// ★ 有了它，状态判断不再依赖 AccountStateCenter 的轮询周期：
            ///   BGI 一写日志（实测 flush 约 0.16 秒）这里就醒，
            ///   延迟从 10 秒降到 0.2 秒左右。
            /// ★ 兜底仍保留：watcher 在某些场景会漏事件（网络盘、
            ///   缓冲区溢出、文件被整体替换），所以 Tick() 的轮询不清除。
            /// </summary>
            public FileSystemWatcher? Watcher;
            /// <summary>
            /// watcher 只置这个标记，真正的解析交给 1 秒合并窗口做。
            /// ★ 关键：不在 watcher 回调里直接解析。
            ///   BGI 满速写日志时每秒可触发几十次事件，
            ///   每次都跑一遍解析会让 CPU 飙到 25%（实测）。
            /// </summary>
            public int Dirty;
            /// <summary>BGI 进程 PID（用于低成本判活，GetProcessById 仅 0.012ms）。</summary>
            public int BgiPid;
            /// <summary>上次判活的时间（避免每秒都查）。</summary>
            public DateTime LastAliveCheck = DateTime.MinValue;

            /// <summary>
            /// 当前**正在运行**的配置组名。
            /// ★ 粘性：出现新的才替换，跨天滚动时继承。
            /// ★ 但日志一旦出现「停止」（执行结束 / 未成功完成 / 取消 / 停止快捷键），
            ///   立刻清空 —— 用户要求"判断为停止，不再保留运行状态"。
            /// </summary>
            public string CurrentGroup = "";
            public DateTime CurrentGroupAt = DateTime.MinValue;
            /// <summary>最近一次结束/停止的配置组（仅供参考，不代表在运行）。</summary>
            public string LastGroup = "";
            /// <summary>最近一次判定为"停止"的时刻。</summary>
            public DateTime StoppedAt = DateTime.MinValue;
            public string OneDragon = "";

            public readonly ScriptGroupTracker Groups = new();
            public DailyStats Today = new DailyStats();
            public readonly Dictionary<string, Dictionary<string, int>> PicksByGroup
                = new(StringComparer.OrdinalIgnoreCase);
            public int ErrorCount, WarnCount, FailCount;

            /// <summary>尾窗（按行，环形增长到上限后丢弃最旧）。</summary>
            public readonly Queue<BgiLogLine> Tail = new();
            public int TailChars;

            /// <summary>
            /// 该账号日志流的下一个行序号（单调递增，跨天滚动/文件重建后不归零）。
            /// ★ 日志触点的「事件锁存」靠它识别「新增行」，与时间窗无关。
            ///   仅当整份 Entries 被清空（清账号）时才真正归零。
            /// </summary>
            public long NextSeq = 1;

            public BgiStatus Status = new();

            /// <summary>最近一条带时间戳的日志时刻（无时间戳的续行沿用）。</summary>
            public DateTime LastEventAt = DateTime.MinValue;

            /// <summary>是否已经做过首次全量扫描。</summary>
            public bool Primed;

            /// <summary>t55：本进程内是否已尝试过历史回填（每账号一次）。</summary>
            public bool HistoryBackfilled;
            /// <summary>t52：本次启动是否因持久化游标跳过了全量重扫（可观测）。</summary>
            public bool SkippedRescan;
            /// <summary>t52：跳过重扫时起跳的游标字节数（本次启动零读取的历史字节）。</summary>
            public long SkippedRescanBytes;

            /// <summary>
            /// 数据版本：任何可见变化都 +1。
            /// ★ 快照按版本缓存，读侧不再每次深拷贝 800 行尾窗 + 上百条拾取。
            ///   原来 Build() 在每次 Snapshot() 里都 ToArray + 复制字典，
            ///   而 Snapshot 每账号每秒被调用多次（刷新循环 + 界面），
            ///   等于每秒造几万个对象 —— 这是自找的 GC 压力。
            /// </summary>
            public long Version;

            /// <summary>按 Version 缓存的不可变快照。</summary>
            public BgiLogSnapshot? Cached;
            public long CachedVersion = -1;
            public string Error = "";

            public void ResetForNewFile(string file)
            {
                Version++;
                LogFile = file;
                // ★ t45 断点续传：换文件/新文件时从持久化游标继续，而不是从 0 重扫。
                //   首次见到该文件（无持久化记录）LoadCursor 返回 -1 → 落回 0。
                long persisted = IngestCursorStore.LoadCursor(AccountId, file);
                Cursor = persisted >= 0 ? persisted : 0;
                // ★ t52：持久化游标 > 0 意味着「本次启动不会全量重扫」，标记并计数（可观测）。
                //   注意：Primed 仍置 false，因为 Tracker 内存状态确需重建，但重建走 SQLite 而非日志扫描。
                if (persisted > 0 && !SkippedRescan)
                {
                    SkippedRescan = true;
                    SkippedRescanBytes = persisted;
                    Interlocked.Increment(ref _skippedRescanCount);
                    Interlocked.Add(ref _skippedRescanTotalBytes, persisted);
                    LogBus.Ok("采集·跳过重扫", UserName + " 持久化游标 " + persisted + " 字节，启动不读历史日志");
                }
                FileLength = 0;
                PendingLen = 0;
                // ★ 注意：CurrentGroup / OneDragon **不清空**。
                //   用户要求：当前配置组做快照，跨天滚动时延续，出现新的才丢弃。
                //   但如果它已经被"停止"标记清空过，这里保持空 —— 停止状态同样跨天延续。
                Groups.Reset();
                Today = new DailyStats();
                PicksByGroup.Clear();
                ErrorCount = WarnCount = FailCount = 0;
                Tail.Clear(); TailChars = 0;
                Status = new BgiStatus();
                LastEventAt = DateTime.MinValue;
                Primed = false;
                // 行序号**不**归零：跨天滚动后新文件首行接着上一行的序号，
                // 触点事件游标才不会被「同一天的文件重建」误判成回退。
                _ = NextSeq;
            }
        }

        private static readonly Lock Sync = new();
        private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

        // ── t52 启动跳过重扫可观测 + 手动全量重扫 ────────────────────────
        /// <summary>本次启动跳过重扫的账号数（冷启动后累计；供 /api/bgi/ingest/status 反映优化是否生效）。</summary>
        public static int SkippedRescanCount => Interlocked.CompareExchange(ref _skippedRescanCount, 0, 0);
        private static int _skippedRescanCount;
        /// <summary>本次启动因持久化游标而免于全量读取的历史字节总数。</summary>
        public static long SkippedRescanTotalBytes => Interlocked.Read(ref _skippedRescanTotalBytes);
        private static long _skippedRescanTotalBytes;
        private static Dictionary<int, BgiLogWatcher.Instance> _instances = new();
        private static DateTime _instancesAt = DateTime.MinValue;

        /// <summary>快照变更通知（在后台线程触发）。</summary>
        public static event Action? Changed;

        // ── 正则（只在初始化时编译一次）──────────────────────────────
        private static readonly Regex RxAnyGroup = new(
            @"配置组\s*=\s*""(?<a>[^""]+)""" +
            @"|配置组\s*:\s*""(?<b>[^""]+)""" +
            @"|配置组\s*""(?<c>[^""]+)""" +
            @"|总文件夹\s*=\s*""(?<d>[^""]+)""" +
            @"|启用一条龙配置\s*[:：]\s*(?<e>.+?)\s*$",
            RegexOptions.Compiled);

        /// <summary>
        /// 「脚本/配置组停了」的日志锚点（实测自 20261120 日志）：
        ///   配置组 "X" 执行结束
        ///   配置组 "X" 未成功完成，结果: Cancelled / Failed
        ///   取消执行配置组: "X"
        ///   检测到您配置的停止快捷键"F12"按下，停止当前执行任务
        ///   任务已停止 / 任务结束 / → "任务结束"
        ///
        /// ★ 用户要求：日志出现停止 → 判断为停止，**不再保留运行状态**。
        ///   这比"粘着上一次的组名"更符合事实：脚本都停了，
        ///   界面还挂着"当前：CD采集-全采集"就是在骗人。
        /// </summary>
        private static readonly Regex RxStopped = new(
            @"配置组s*""[^""]+""s*(?:执行结束|未成功完成)" +
            @"|取消执行配置组s*[:：]" +
            @"|停止快捷键.*?按下" +
            @"|停止当前执行任务" +
            @"|任务已停止|任务结束|→s*""任务结束""" +
            @"|已停止运行|中断执行",
            RegexOptions.Compiled);

        private static readonly Regex RxTs = new(
            @"^\[(?<date>\d{4}-\d{2}-\d{2}[ T])?(?<time>\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)\]\s*(?:\[(?<level>[A-Za-z]{3,5})\])?",
            RegexOptions.Compiled);

        // ── 对外读接口（全部无锁读不可变快照）────────────────────────

        /// <summary>取某账号的快照；没有则返回 null（= 未绑定或尚未扫描）。</summary>
        public static BgiLogSnapshot? Snapshot(string accountId)
        {
            if (string.IsNullOrWhiteSpace(accountId)) return null;
            lock (Sync) return Entries.TryGetValue(accountId, out var e) ? Cached(e) : null;
        }

        public static BgiLogSnapshot? SnapshotByUser(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) return null;
            lock (Sync)
            {
                foreach (var e in Entries.Values)
                    if (string.Equals(e.UserName, userName, StringComparison.OrdinalIgnoreCase)) return Cached(e);
            }
            return null;
        }

        /// <summary>尾窗拼成的原文（供日志条件匹配；按时间窗裁剪由调用方负责）。</summary>
        public static string RecentText(string accountId)
        {
            var snap = Snapshot(accountId);
            if (snap == null || snap.Tail.Count == 0) return "";
            var sb = new StringBuilder(snap.Tail.Count * 96);
            foreach (var l in snap.Tail) sb.AppendLine(l.Text);
            return sb.ToString();
        }

        /// <summary>数据中心当前常驻字符总量（界面哨兵）。</summary>
        public static long ResidentChars()
        {
            lock (Sync)
            {
                long n = 0;
                foreach (var e in Entries.Values)
                {
                    n += e.TailChars;
                    n += e.CurrentGroup.Length + e.OneDragon.Length;
                    foreach (var g in e.PicksByGroup.Values) n += g.Count * 32;
                }
                return n;
            }
        }

        // ── 事件驱动：文件变化即刻解析 ────────────────────────────────

        /// <summary>
        /// 为某账号的日志文件建立/更新 FileSystemWatcher。
        ///
        /// ★ 只在「已绑定且日志文件确定」时调用；未绑定账号零 IO。
        /// ★ 文件路径变化（换配置组、跨天滚动）时自动重建 watcher。
        /// </summary>
        private static void EnsureWatcher(Entry e)
        {
            try
            {
                string file = e.LogFile;
                if (string.IsNullOrWhiteSpace(file)) { StopWatcher(e); return; }
                string dir = Path.GetDirectoryName(file) ?? "";
                if (dir.Length == 0 || !Directory.Exists(dir)) { StopWatcher(e); return; }

                // 已经在跟同一个文件 → 不动
                if (e.Watcher != null)
                {
                    string cur = e.Watcher.Path ?? "";
                    string curFilter = e.Watcher.Filter ?? "";
                    if (string.Equals(Path.Combine(cur, curFilter), file, StringComparison.OrdinalIgnoreCase))
                        return;
                    StopWatcher(e);
                }

                var w = new FileSystemWatcher(dir)
                {
                    Filter = Path.GetFileName(file),
                    NotifyFilter = NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.FileName,
                    IncludeSubdirectories = false,
                    // ★ 不用 InternalBufferSize 默认值（8KB）：BGI 满速写日志时
                    //   容易被撑爆丢事件。32KB 足够，内存代价可忽略。
                    InternalBufferSize = 32 * 1024,
                };
                FileSystemEventHandler onChanged = (_, __) => PumpByEvent(e);
                RenamedEventHandler onRenamed = (_, __) => PumpByEvent(e);
                w.Changed += onChanged;
                w.Created += onChanged;
                w.Renamed += onRenamed;
                w.Error += (_, __) =>
                {
                    // 缓冲区溢出等：重建 watcher，靠轮询兜底期间不会丢数据
                    try { StopWatcher(e); EnsureWatcher(e); } catch { }
                };
                w.EnableRaisingEvents = true;
                e.Watcher = w;
            }
            catch
            {
                // watcher 建不起来不是致命问题：轮询仍会读，
                // 只是延迟回到轮询周期。
                StopWatcher(e);
            }
        }

        private static void StopWatcher(Entry e)
        {
            try
            {
                if (e.Watcher == null) return;
                e.Watcher.EnableRaisingEvents = false;
                e.Watcher.Dispose();
            }
            catch { }
            finally { e.Watcher = null; }
        }

        /// <summary>
        /// watcher 触发：**只置标记**，不做任何 IO 或解析。
        ///
        /// ★ 为什么不在这里直接解析：
        ///   BGI 满速写日志时每秒能触发几十次 Changed 事件，
        ///   每次解析都要读文件 + 正则匹配 + 更新统计（实测约 8ms），
        ///   叠加起来 CPU 直接飙到 25%。实测数据：日志每秒只写 1KB，
        ///   却消耗 25% 单核 —— 全是重复解析的开销。
        ///
        /// ★ 现在的做法：置标记后立刻返回（纳秒级），
        ///   由 ServiceTick() 每 1 秒统一消费一次。
        ///   无论写入多频繁，最多每秒解析一次，且**不会丢数据** ——
        ///   Pump() 用字节游标，从 Cursor 一路读到 EOF，中间不跳过任何字节。
        /// </summary>
        private static void PumpByEvent(Entry e)
        {
            try { Interlocked.Exchange(ref e.Dirty, 1); } catch { }
        }

        /// <summary>
        /// 1 秒合并窗口：消费所有待解析的账号。
        ///
        /// 调用方：TickHub 每 1 秒一次（见 EnsureServiceTick）。
        /// 与 Tick() 的区别：Tick 会顺便发现新账号/新文件；
        /// 这里只处理「已知且文件在动」的账号，开销极小。
        /// </summary>
        private static void ServiceTick()
        {
            List<Entry>? work = null;
            lock (Sync)
            {
                foreach (var e in Entries.Values)
                {
                    if (e.Dirty == 0 && e.LogFile.Length == 0) continue;
                    (work ??= new List<Entry>()).Add(e);
                }
            }
            if (work == null) return;

            // ★★ t63（P0-1）：单轮耗时预算 —— 超预算立刻让出，剩下的下 tick 继续。
            //   字节游标（e.Cursor）保证幂等续读，让出绝不会漏数据。
            //   没有这个预算时，3 个账号首扫能把一轮 Tick 拖到 30 秒，整个界面卡死。
            var budgetSw = System.Diagnostics.Stopwatch.StartNew();

            bool any = false;
            foreach (var e in work)
            {
                if (budgetSw.ElapsedMilliseconds > ServiceTickBudgetMs)
                {
                    // 超预算：本 tick 到此为止，剩下的账号下一拍继续（保持 Dirty 未清则下拍会重来）
                    _ = Interlocked.Exchange(ref e.Dirty, 1);
                    break;
                }
                try
                {
                    // 取出并清标记：期间新来的事件会重新置位，下一拍处理。
                    int dirty = Interlocked.Exchange(ref e.Dirty, 0);

                    // ★ 日志没动时做「低成本判活」：
                    //   GetProcessById 只要 0.012ms（枚举全部进程要 23.7ms）。
                    //   活着 → 什么都不做；死了 → 标记脏，让 Step 更新状态。
                    if (dirty == 0)
                    {
                        // ★ t10：判活节流 ≥30s（用既有 LastAliveCheck 字段，避免每秒查）。
                        //   判活结果同时喂给运行记录采集器（BGI 状态时段：idle/not_running），
                        //   只在空闲拍（dirty==0）做，本身已是低频，无新进程枚举。
                        if (e.BgiPid > 0 && (DateTime.Now - e.LastAliveCheck).TotalSeconds >= 30)
                        {
                            e.LastAliveCheck = DateTime.Now;
                            bool alive = IsAlive(e.BgiPid);
                            try { RunRecordCollector.ObserveBgiState(e.UserName, 0, alive, e.LogFile); } catch { }
                        }
                        if (e.BgiPid > 0 && IsAlive(e.BgiPid))
                        {
                            // ★★ t56 修复（回填不推进的根因）：
                            //   原来这里直接 continue，跳过 Step() —— 但 **Step 里现在挂着历史回填**
                            //   （PumpBackfill）。BGI 正常挂机时文件不动 = dirty==0 = 每秒都走这条分支
                            //   → Step 永不执行 → 回填永远停在原地（实测卡在 11-21 的 7.3MB/39.1MB）。
                            //   改为：**先推进回填**（低优先级、限量），再 continue。
                            //   PumpBackfill 内部只读 1MB/tick，且会自行判断是否还有待办，开销可忽略。
                            try { PumpBackfill(e, e.BgiExe); } catch { }
                            continue;
                        }
                        if (e.BgiPid > 0)
                        {
                            // 进程没了：让下游知道（BgiRunning 由 AccountStateCenter 判断，
                            // 这里只保证日志侧状态也被刷新一次）
                            dirty = 1;
                        }
                        else
                        {
                            continue;
                        }
                    }

                    var acc = new Account { Id = e.AccountId, UserName = e.UserName };
                    if (Step(acc)) any = true;
                }
                catch { }
            }

            if (any)
            {
                try { Changed?.Invoke(); } catch { }
            }
        }

        /// <summary>
        /// 用 PID 判断进程是否还活着。
        /// ★ 0.012ms，比枚举全部进程（23.7ms）便宜约 2000 倍。
        /// ★ PID 可能被复用（极端情况），但配合 30 秒一次的
        ///   AccountStateCenter 全量刷新足以兜住。
        /// </summary>
        private static bool IsAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch { return false; }
        }

        // ── 1 秒节拍（TickHub）──────────────────────────────────────

        private static IDisposable? _tickSub;

        /// <summary>启动 1 秒合并窗口。幂等，重复调用无副作用。</summary>
        public static void EnsureServiceTick()
        {
            if (_tickSub != null) return;
            // ★ 后台订阅：ServiceTick 内部是同步文件读 + 行解析，跑在 UI 线程
            //   会每秒卡一次界面。迁到线程池；Changed 事件本身在后台线程抛，
            //   订阅方（LaunchPage.OnLogCenterChanged）已用 Dispatcher.InvokeAsync 切回 UI。
            try { _tickSub = TickHub.RegisterBackground("BgiLogCenter.Tick", 1, ServiceTick); } catch { }
        }

        /// <summary>停止节拍（退出时调用）。</summary>
        public static void StopServiceTick()
        {
            try { _tickSub?.Dispose(); } catch { }
            _tickSub = null;
        }

        /// <summary>账号是否已绑定 BGI 路径（= 用户填过）。</summary>
        public static bool IsBound(string userName) => BgiPathResolver.IsPinned(userName);

        // ── 主循环 ──────────────────────────────────────────────────

        /// <summary>
        /// 刷新一轮。只处理**已绑定**的账号；其余账号零 IO。
        /// 由 AccountStateCenter 的 1 Hz 循环调用。
        /// </summary>
        public static void Tick(IEnumerable<Account> accounts)
        {
            var list = accounts?.Where(a => a != null).ToList() ?? new List<Account>();
            if (list.Count == 0) return;

            // 首次调用时启动 1 秒合并窗口（幂等）
            EnsureServiceTick();

            // ★ 不再枚举进程：日志路径由配置推导，PID 缓存在 Entry 里。
            bool any = false;
            foreach (var acc in list)
            {
                try
                {
                    MissCheck(acc);   // ★ t45 遗漏检查（读游标+文件长度，无 IO 放大）
                    if (Step(acc)) any = true;
                }
                catch (Exception ex)
                {
                    lock (Sync)
                    {
                        if (!Entries.TryGetValue(acc.Id, out var e)) { e = new Entry { AccountId = acc.Id }; Entries[acc.Id] = e; }
                        e.UserName = acc.UserName ?? "";
                        e.Error = ex.GetType().Name + ": " + ex.Message;
                    }
                }
            }

            // 清掉已删除账号的条目，避免越积越多。
            // ★★ t63（architect 审查 P0-3，watcher 泄漏）：
            //   原来直接 Entries.Remove(id) —— 但该 Entry 的 FileSystemWatcher 没 Dispose！
            //   watcher 持有内核句柄 + 线程池回调，且 onChanged 闭包捕获 Entry
            //   （钉住 64KB Pending + Tail + PicksByGroup）→ 反复改配置会只增不减。
            var alive = new HashSet<string>(list.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
            var toRemove = new List<Entry>();
            lock (Sync)
            {
                foreach (var kv in Entries.ToList())
                    if (!alive.Contains(kv.Key)) { toRemove.Add(kv.Value); Entries.Remove(kv.Key); }
            }
            // ★ 必须在锁外 Dispose（StopWatcher 内部会 Stop/Timer 操作，避免死锁）
            foreach (var dead in toRemove) { try { StopWatcher(dead); } catch { } }

            if (any) { try { Changed?.Invoke(); } catch { } }
        }

        /// <summary>
        /// t45 启动/每轮遗漏检查：对比持久化 cursor 与当前文件长度，计算未读区间。
        /// 结果写入 LogBus（可观测）；补采由 Pump 按 MaxBytesPerTick 预算分批消化，不阻塞启动。
        /// </summary>
        private static void MissCheck(Account acc)
        {
            if (!BgiPathResolver.IsPinned(acc.UserName ?? "")) return;
            string exe = BgiPathResolver.GetBoundPath(acc.UserName ?? "");
            if (string.IsNullOrWhiteSpace(exe)) return;
            string logFile = BgiLogWatcher.FindTodayLog(exe) ?? "";
            if (logFile.Length == 0)
            {
                // 日志文件不存在（被删/移走）→ 记「缺失」
                if (acc.UserName != null)
                    LogBus.Warn("采集·缺失", acc.UserName + " 今日日志文件不存在（未绑定或已被移走）");
                return;
            }
            try
            {
                long len = new FileInfo(logFile).Length;
                long persisted = IngestCursorStore.LoadCursor(acc.Id, logFile);
                long pending = persisted < 0 ? len : Math.Max(0, len - persisted);
                if (pending > 8 * 1024 * 1024)
                {
                    // 未读区间过大 → 记「补采中」，分多个 tick 消化
                    LogBus.Info("采集·补采中", acc.UserName + " 未读 " + (pending / 1024 / 1024) + " MB，分批补采（不阻塞启动）");
                }
                else if (pending > 0)
                {
                    LogBus.Ok("采集·断点续传", acc.UserName + " 从断点续读，待补 " + pending + " 字节");
                }
            }
            catch { }
        }

        // ── t55 历史回填 ─────────────────────────────────────────────
        //
        //  ★ 设计取舍：**不占用 Pump 的预算**，而是独立一次性扫（每份日志最多一次）。
        //    理由：历史日志是「补账」，不影响实时跟随；如果混进 Pump 会和今天的日志抢预算，
        //    导致实时数据延迟。这里按「每份文件一个标记」保证只扫一次。
        //  ★ 只回填 cursor 不存在的文件（= 从未采过）。已采过的靠断点续传，不重复。
        //  ★ 失败绝不抛出：回填是尽力而为，坏了不能影响主采集。
        // ── t56：回填改成「每 tick 一小片」，低优先级，不阻塞实时采集与 UI ──
        //
        //  为什么改：v1~v3 是**一次性同步跑完**（7 份日志约 250MB / 40 秒，CPU 满载），
        //  用户体感「刚启动就卡」。数据中心原则是「不能影响正常使用」。
        //
        //  新模型：
        //    · 状态机挂在每次 Step 里（1Hz），每 tick 最多处理 BackfillBytesPerTick 字节；
        //    · 默认 1MB/tick ≈ 每秒 1MB ≈ 250MB 约 4 分钟补齐，期间界面/游戏完全不受影响；
        //    · 优先级天然低于实时：Pump（今天的日志）先跑，回填用剩余时间；
        //    · 每片处理完立即落 cursor，断电/退出最多丢 1MB（幂等，下次续）。
        private const int BackfillBytesPerTick = 1024 * 1024;   // 1MB / 秒

        /// <summary>回填状态（按账号/文件推进，跨 tick 保持）。</summary>
        private sealed class BackfillState
        {
            public List<string> Pending = new();   // 待回填文件（按日期升序）
            public int FileIndex;
            public long Offset;                    // 当前文件内的读取偏移
            public bool Initialized;               // 是否已扫描过待办列表
        }

        private static readonly Dictionary<string, BackfillState> Backfills =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 推进一小片历史回填（每次 Step 调一次）。返回 true 表示本次有进展（供 Changed 事件）。
        /// 设计：**绝不长时间占用线程** —— 单次最多读 BackfillBytesPerTick 字节。
        /// </summary>
        private static bool PumpBackfill(Entry e, string exe)
        {
            if (string.IsNullOrWhiteSpace(exe)) return false;

            BackfillState bs;
            lock (Sync)
            {
                if (!Backfills.TryGetValue(e.AccountId, out var found))
                {
                    found = new BackfillState();
                    Backfills[e.AccountId] = found;
                }
                bs = found;
            }

            // ① 首次：扫描待回填文件列表（只扫一次，很轻）
            if (!bs.Initialized)
            {
                bs.Initialized = true;
                try
                {
                    // ★ t66：回填窗口用独立配置 BgiBackfillDays（默认 7，可调 1~60）。
                //   原来复用 RunDbRetentionDays（留存天数），两者语义不同：
                //   想多采历史但不留那么久，就会被绑死。
                int days = Math.Clamp(App.Config?.Settings?.BgiBackfillDays ?? 7, 1, 60);
                    var files = BgiLogWatcher.ListRecentLogs(exe, days);
                    string todayName = "better-genshin-impact" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log";
                    foreach (string f in files)
                    {
                        if (string.Equals(Path.GetFileName(f), todayName, StringComparison.OrdinalIgnoreCase)) continue;

                        // ★★ t66 修复（11-25 卡在 9/36.9 MB 的根因）：
                        //
                        //   原逻辑：只有「完全没有游标」的文件才加入待办。
                        //   后果：**采到一半中断的文件永远补不上**。
                        //   实测：11-25 的日志 cursor=9.44MB / file_length=36.89MB，
                        //   因为昨天 21:14 回填被中断，之后再启动就认为「已采过」直接跳过，
                        //   剩下 27.4 MB 永远进不了库 —— 那天只有 14 条 run（其它天 28~42 条）。
                        //
                        //   正确逻辑：**游标 < 文件长度 就要继续**（断点续传）。
                        //   LoadCursor 返回 -1 表示没游标，从 0 开始；
                        //   返回 >=0 且 < 长度 表示没采完，从游标处继续；
                        //   返回 == 长度 才是真采完了。
                        long cur = IngestCursorStore.LoadCursor(e.AccountId, f);
                        long lenNow = 0;
                        try { lenNow = new FileInfo(f).Length; } catch { }
                        if (cur < 0 || (lenNow > 0 && cur < lenNow)) bs.Pending.Add(f);
                    }
                    if (bs.Pending.Count > 0)
                        LogBus.Ok("采集·历史回填", e.UserName + " 待回填 " + bs.Pending.Count
                            + " 份历史日志（低优先级后台进行，每秒约 1MB）");
                }
                catch { }
                return false;   // 本 tick 只做初始化，不干活
            }

            // ② 没有待办 → 结束
            if (bs.FileIndex >= bs.Pending.Count) return false;

            string current = bs.Pending[bs.FileIndex];
            try
            {
                var fi = new FileInfo(current);
                if (!fi.Exists || fi.Length <= 0)
                {
                    bs.FileIndex++; bs.Offset = 0;
                    IngestCursorStore.Advance(e.AccountId, current, 0, 0, 0);
                    return false;
                }
                long len = fi.Length;

                // ③ 读一小片（严格限量，不阻塞）
                long remain = len - bs.Offset;
                if (remain <= 0)
                {
                    // 本文件读完 → 下一个
                    List<string> doneList;
                    lock (Sync) doneList = new(bs.Pending);
                    bs.FileIndex++; bs.Offset = 0;
                    LogBus.Ok("采集·历史回填完成", e.UserName + " " + Path.GetFileName(current));
                    return true;
                }

                int want = (int)Math.Min(remain, BackfillBytesPerTick);
                byte[] buf = new byte[want];
                int got;
                using (var fs = new FileStream(current, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
                {
                    fs.Seek(bs.Offset, SeekOrigin.Begin);
                    got = fs.Read(buf, 0, want);
                }
                if (got <= 0) { bs.FileIndex++; bs.Offset = 0; return false; }

                // 只取到最后一个换行，避免把半行喂进去
                int lastNl = Array.LastIndexOf(buf, (byte)'\n', got - 1);
                int use = lastNl >= 0 ? lastNl + 1 : 0;
                if (use == 0)
                {
                    // 这一片里没有完整行（超长行）→ 跳过这一片
                    bs.Offset += got;
                    IngestCursorStore.Advance(e.AccountId, current, bs.Offset, len, 0);
                    return false;
                }

                // ④ 首次进这个文件时建立该文件的时钟基线（每份文件独立）
                if (bs.Offset == 0)
                {
                    using (RunRecordCollector.EnterBackfillMode())
                        RunRecordCollector.OnFileChanged(e.UserName, current);
                }

                string chunk = Encoding.UTF8.GetString(buf, 0, use);
                // ★ t56：回填必须进「回填模式」——否则 PendingSplit 的 90s 墙钟兜底
                //   会把历史跨天分段全部判成「来晚了」，导致后续日志的 run 不建。
                using (RunRecordCollector.EnterBackfillMode())
                {
                    RunRecordCollector.Feed(e.UserName, chunk, current, len, 0);
                }

                // ★★ t63（architect 审查 P0-2，卡死元凶之二）：
                //   原来这里是同步 WaitForWritesDrain(10s) —— Task.Wait 会**阻塞当前线程最多 10 秒**，
                //   而它跑在 ServiceTick 的串行 foreach 里：
                //     A2 回填阻塞 10 秒 → A3/A1 的实时采集全被拖住
                //   多账号回填 = 逐账号串行等 = 累计几十秒 → 界面无响应。
                //
                //   改法：**不做阻塞等待**，改为「看队列深度」决定是否让出。
                //   队列是 FIFO 单线程消费，只要每块喂完检查一下水位，
                //   太高就本 tick 停止回填（下 tick 继续，游标幂等），
                //   既不阻塞采集线程，也不会让队列无限堆积。
                //
                //   关于外键时序：RunRecordStore 的写队列是**单线程按入队顺序执行**的，
                //   AddRun 一定先于同批 AddPickEvent 落库 —— 原来担心的时序问题不存在，
                //   那个 Wait 只是当初为排查外键失败加的保守措施，现在可以去掉。
                int qDepth = RunRecordStore.PendingWriteCount;
                if (qDepth > 2000)
                {
                    // 队列积压过多：**本 tick 不再继续回填**，让写线程追上来。
                    // 不推进游标（offset 没加），下一拍会重读这一块 —— 幂等，不会重复入库。
                    return true;
                }

                // ⑥ 队列不健康 → 停回填，保住已有数据
                if (!RunRecordStore.IsHealthy)
                {
                    LogBus.Warn("采集·历史回填中止", e.UserName + " 写队列不健康，暂停回填");
                    bs.FileIndex = bs.Pending.Count;   // 本次运行不再尝试
                    return false;
                }

                bs.Offset += use;
                IngestCursorStore.Advance(e.AccountId, current, bs.Offset, len, 0);

                // 本文件读完 → 收尾 + 下一 tick 自动进入下一个
                if (bs.Offset >= len)
                {
                    // ★★ t56 修复：**每份历史文件处理完必须收尾**。
                    //   否则该文件里最后一个没写「执行结束」的组会永远停在 Running
                    //   （实测残留 4 条 end=NULL：11-20 的联机锄地/小怪、11-21 的CD、11-22 的混合精英）。
                    //   这是回填**特有**的问题：实时采集时文件不会中途换，组的状态由 BGI 的下一个
                    //   锚点自然关闭；而回填是「按文件推进」，跨文件时上一个文件的 open 组无人关闭。
                    bool isLast = bs.FileIndex >= bs.Pending.Count - 1;
                    RunRecordCollector.CloseOpenForBackfill(e.UserName, current, isLast);

                    bs.FileIndex++; bs.Offset = 0;
                    LogBus.Ok("采集·历史回填完成", e.UserName + " " + Path.GetFileName(current));
                }
                return true;
            }
            catch (Exception ex)
            {
                LogBus.Warn("采集·历史回填失败", e.UserName + " " + Path.GetFileName(current)
                    + "：" + ex.GetType().Name);
                bs.FileIndex++; bs.Offset = 0;   // 跳过这份，不卡住整个流程
                return false;
            }
        }

        /// <summary>返回 true 表示该账号的可见数据发生了变化。</summary>
        private static bool Step(Account acc)
        {
            string user = acc.UserName ?? "";
            bool bound = BgiPathResolver.IsPinned(user);

            Entry e;
            lock (Sync)
            {
                if (!Entries.TryGetValue(acc.Id, out var found))
                {
                    found = new Entry { AccountId = acc.Id };
                    Entries[acc.Id] = found;
                }
                e = found;
            }

            bool wasUnbound = false;
            lock (Sync)
            {
                e.UserName = user;
                if (!bound)
                {
                    // ★ 未绑定：清空并**不做任何 IO**。
                    bool had = e.Primed || e.BgiExe.Length > 0 || e.LogFile.Length > 0;
                    if (had) e.Version++;
                    e.BgiExe = "";
                    e.LogFile = "";
                    e.Cursor = 0;
                    e.FileLength = 0;
                    e.Primed = false;
                    e.Error = "";
                    if (had) { e.Tail.Clear(); e.TailChars = 0; }
                    wasUnbound = true;
                }
            }
            if (wasUnbound)
            {
                // ★★ t63（P0-3）：未绑定必须停掉 watcher —— 否则它继续监听一个
                //   已不该采集的文件，每次写入都触发回调（白烧 CPU），且句柄不释放。
                try { StopWatcher(e); } catch { }
                return true;
            }

            // 绑定了：解析路径（不写回配置），再找该会话里正在跑的 BGI 日志。
            string exe = BgiPathResolver.GetBoundPath(user);
            string logFile = "";
            int sessionId = 0;

            if (!string.IsNullOrWhiteSpace(exe))
            {
                // ★ 优先从**配置的 exe 路径**推导今天的日志（同目录 log\ 下按日期命名）。
                //   这一步只要 0.017ms（一次 FileInfo）。
                //
                //   原来是反过来的：先枚举全部进程（23.7ms）拿 inst.LogFile，
                //   拿不到才退回推导。而用户既然已经填了 BGI 路径，
                //   推导几乎总能成功 —— 等于每次都白花 23.7ms。
                //   watcher 事件里也调这个，日志每秒写 1KB 就烧掉 25% CPU，
                //   根源就在这里。
                logFile = BgiLogWatcher.FindTodayLog(exe) ?? "";

                // 记录 BGI 的 PID，供之后低成本判活（GetProcessById 0.012ms）。
                // ★ 只有这里需要一次进程枚举；而且缓存进 Entry 后不用重复。
                var state = AccountStateCenter.Get(acc.Id);
                sessionId = state?.SessionId ?? 0;
                if (sessionId > 0 && e.BgiPid <= 0)
                {
                    try
                    {
                        var inst = BgiLogWatcher.DiscoverForSession(sessionId, exe);
                        if (inst != null)
                        {
                            if (inst.Pid > 0) e.BgiPid = inst.Pid;
                            // 推导失败时才用进程给的文件（极少数目录结构特殊的情况）
                            if (logFile.Length == 0 && !string.IsNullOrWhiteSpace(inst.LogFile))
                                logFile = inst.LogFile;
                        }
                    }
                    catch { }
                }
            }

            lock (Sync)
            {
                e.BgiExe = exe ?? "";
                if (logFile.Length == 0)
                {
                    // 绑定了但今天还没有日志：保留上次的粘性配置组与统计，只是不前进。
                    e.Error = exe.Length == 0 ? "BGI 路径为空" : "";
                    return false;
                }
                if (!string.Equals(e.LogFile, logFile, StringComparison.OrdinalIgnoreCase))
                {
                    e.ResetForNewFile(logFile);
                    e.Version++;

                    // ★ 3.3.41 跨天补全入口（契约 §4.4）：文件滚动 / 新一天日志开始文件时，
                    //   让运行记录采集器判断「内存里的 open 窗口还算不算同一次运行」——
                    //   gap ≤ 5 分钟 = 续跑（保留 start 时刻，跨天仍旧 1 条）；
                    //   gap > 5 分钟 = close(Interrupted) 兜底，绝不虚构成跨天完成。
                    //   纯内存判定（无 IO、无锁竞争、不碰文件），异常吞掉不影响日志解析。
                    try { RunRecordCollector.OnFileChanged(e.UserName, logFile); } catch { }
                }
            }

            // ★ 日志文件确定后建立 watcher：之后 BGI 一写入就立刻解析，
            //   不再等 AccountStateCenter 的轮询周期。
            EnsureWatcher(e);

            // ★★ t55 历史回填：首次接入/清库后，把最近 N 天「还没采过」的日志补一遍。
            //   为什么必须做：FindTodayLog 只看今天那份 —— 昨天/前天的运行记录永远补不回来，
            //   与「留存 7 天」需求冲突。每份日志的日期由文件名唯一确定（用户原则：
            //   同一份日志就是同一天），所以按文件回填是安全且确定的。
            //   只在「该文件的持久化 cursor 不存在（-1）」时补，采过的不重复。
            PumpBackfill(e, exe);

            return Pump(e);
        }

        /// <summary>把文件从 cursor 读到 EOF。返回是否有可见变化。</summary>
        private static bool Pump(Entry e)
        {
            long len;
            DateTime write;
            try
            {
                var fi = new FileInfo(e.LogFile);
                if (!fi.Exists) return false;
                len = fi.Length;
                write = fi.LastWriteTime;
            }
            catch { return false; }

            // 文件变短 = 被轮转 / 重建 → 从头重扫，但保留粘性配置组。
            if (len < e.Cursor)
            {
                lock (Sync) { e.Cursor = 0; e.PendingLen = 0; }
                // ★ t45 轮转处理：重置持久化游标并记录一次轮转事件（可观测）。
                IngestCursorStore.Reset(e.AccountId, e.LogFile);
                LogBus.Warn("采集·轮转", e.UserName + " 日志被轮转/变小，游标已重置从头补采");
            }
            if (len == e.Cursor && write == e.LastWrite) return false;

            long budget = Math.Min(len - e.Cursor, MaxBytesPerTick);
            if (budget <= 0) { lock (Sync) e.LastWrite = write; return false; }

            var buf = new byte[Math.Min(budget, 4 * 1024 * 1024)];
            bool changed = false;
            try
            {
                using var fs = new FileStream(e.LogFile, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                long pos = e.Cursor;
                long end = e.Cursor + budget;
                while (pos < end)
                {
                    int want = (int)Math.Min(buf.Length, end - pos);
                    fs.Seek(pos, SeekOrigin.Begin);
                    int got = fs.Read(buf, 0, want);
                    if (got <= 0) break;

                    // 补上上一块留下的残行，拼成本次可解析的完整行。
                    int start = 0, carry = 0;
                    byte[]? combined = null;
                    if (e.PendingLen > 0)
                    {
                        combined = new byte[e.PendingLen + got];
                        Buffer.BlockCopy(e.Pending, 0, combined, 0, e.PendingLen);
                        Buffer.BlockCopy(buf, 0, combined, e.PendingLen, got);
                        start = 0; carry = e.PendingLen;
                    }

                    byte[] work = combined ?? buf;
                    int total = combined != null ? combined.Length : got;
                    if (total <= 0) { pos += got; continue; }
                    int lastNl = Array.LastIndexOf(work, (byte)'\n', total - 1);
                    if (lastNl < 0)
                    {
                        // 整块都没有换行：先攒着（超长行直接丢弃，防爆内存）。
                        if (total < e.Pending.Length) { Buffer.BlockCopy(work, 0, e.Pending, 0, total); e.PendingLen = total; }
                        else e.PendingLen = 0;
                        pos += got;
                        continue;
                    }

                    int completeLen = lastNl + 1;
                    // 只剩"残行"部分留到下块。
                    int restLen = total - completeLen;
                    if (restLen > 0 && restLen < e.Pending.Length)
                    {
                        Buffer.BlockCopy(work, completeLen, e.Pending, 0, restLen);
                        e.PendingLen = restLen;
                    }
                    else e.PendingLen = 0;

                    string chunk = Encoding.UTF8.GetString(work, start, completeLen);
                    ProcessChunk(e, chunk, len, write.Ticks);
                    changed = true;

                    pos += got;
                    _ = carry;
                }
                lock (Sync) { e.Cursor = end; e.FileLength = len; e.LastWrite = write; }
                // ★ t45/t52 断点续传：持久化「行对齐游标」= end - 残行字节。
                //   内存 e.Cursor 仍 = end（残行 bytes 在 e.Pending 里，下次 Pump 拼回）；
                //   持久化必须回到上一个完整换行之后，否则重启后 cursor 落在半行中间，
                //   从半行起读会解析出乱码/错锚点。ResetForNewFile 时 PendingLen=0，
                //   重新从行首读这半行，天然对齐。
                long aligned = Math.Max(0, end - e.PendingLen);
                IngestCursorStore.Advance(e.AccountId, e.LogFile, aligned, len, write.Ticks);
                bool wasPrimed = e.Primed;
                e.Primed = true;

                // ★ 首次全量扫完：请求「下一次自然 GC 时压缩 LOH」。
                //
                // ★★ t65（architect 审查 P1-1）：**不再主动调 GC.Collect(2)**。
                //
                //   原实现：CompactOnce + GC.Collect(2, Optimized, blocking:false, compacting:true)
                //   问题：
                //     · GC.Collect(2) 即使 blocking:false 也会安排一次**全代**回收，
                //       而 full GC 有 STW 阶段；它发生在 Pump() 里，而 Pump 在
                //       ServiceTick 的串行路径上 —— 首扫大日志时正好把该线程钉住。
                //     · 叠加「启动首帧」就是用户看到的启动期卡顿。
                //
                //   现在：**只设 CompactOnce 标志就返回**，让它在下一次
                //   .NET 自己认为合适的时候顺带压缩 —— 我们不主动打断运行时。
                //   效果一样（LOH 碎片会被清掉），但不再由我们制造停顿。
                //
                //   至于「不压缩 LOH 会涨到 Priv 1.5GB」的旧问题：那来自旧实现的
                //   大缓冲未释放；如今 buf 在 finally 里立刻置空，碎片自然少得多。
                if (!wasPrimed && budget > 8 * 1024 * 1024)
                {
                    try
                    {
                        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    }
                    catch { }
                }
            }
            finally
            {
                // ★ 大缓冲用完立刻放开，并压一次 LOH，让内存及时还给系统。
                buf = Array.Empty<byte>();
            }
            return changed;
        }

        /// <summary>解析一段完整的日志文本（行尾已闭合）。</summary>
        private static void ProcessChunk(Entry e, string chunk, long fileLen, long writeTicks)
        {
            if (chunk.Length == 0) return;
            e.Version++;
            var now = DateTime.Now;
            string today = now.ToString("yyyy-MM-dd");

            // ① 结构化状态：交给既有解析器，它们本来就只吃文本。
            try { e.Status.Update(chunk); } catch { }
            try { e.Groups.Update(chunk); } catch { }
            try { DailyStatsCache.Accumulate(e.Today, chunk); } catch { }
            // ★ 台账：把"配置组运行事件 × 收益事件"按时间窗归因成一条条记录。
            //   只做内存归因，文件写 IO 由 RouteLedger 后台线程承担（不在 UI 线程）。
            //   挂在这里 = 挂在现有增量解析路径上，不新增定时器。
            try { RouteLedger.Feed(e.UserName, chunk); } catch { }

            // ★★ 3.3.41 运行记录（SQLite）：紧邻 RouteLedger 的第二个归因订阅者。
            //   同一条增量 chunk、同一份字节游标 —— **不新增日志扫描链**，
            //   只多跑若干已编译正则 + 内存状态机 + 入队（写盘全在 RunRecordStore 后台线程）。
            //   异常绝不能影响日志解析主流程（Feed 内部已 try/catch，这里再兜一层）。
            //   srcFile 用于取文件名日期（跨天时间轴）与来源留痕，不做任何文件 IO。
            try { RunRecordCollector.Feed(e.UserName, chunk, e.LogFile, fileLen, writeTicks); } catch { }

            // ② 逐行处理：粘性配置组 / 拾取分组 / 报错计数 / 尾窗。
            var lines = chunk.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                if (raw.Length == 0) continue;
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;

                DateTime at = now;
                string level = "";
                var m = RxTs.Match(line);
                if (m.Success)
                {
                    if (TryStamp(m, now, out var parsed)) { at = parsed; e.LastEventAt = parsed; }
                    level = m.Groups["level"].Value;
                }
                else
                {
                    // 没有时间戳的行（BGI 的换行续行）按"上一次时间"处理。
                    at = e.LastEventAt == DateTime.MinValue ? now : e.LastEventAt;
                }

                // 报错 / 告警 / 失败计数（与 CountDailyErrors 同一口径）。
                if (line.IndexOf("[ERR]", StringComparison.Ordinal) >= 0) e.ErrorCount++;
                else if (line.IndexOf("[WRN]", StringComparison.Ordinal) >= 0) e.WarnCount++;
                if (line.IndexOf("执行失败", StringComparison.Ordinal) >= 0
                    || line.IndexOf("未成功完成", StringComparison.Ordinal) >= 0
                    || line.IndexOf("一条龙前置动作未成功", StringComparison.Ordinal) >= 0) e.FailCount++;

                // ── 停止判定：先看"停了没有"，再看"有没有新的" ──────────
                //
                // ★ 用户要求：「日志出现停止，则不需要保留，判断为停止。
                //   出现快捷键停止也是这个逻辑，不再保留运行状态。」
                //   所以 CurrentGroup 不是"永久粘着"，而是"运行中的那个"：
                //   出现停止锚点就清空，直到下一条配置组行重新填上。
                bool stopped = RxStopped.IsMatch(line);
                if (stopped)
                {
                    if (e.CurrentGroup.Length > 0) e.LastGroup = e.CurrentGroup;
                    e.CurrentGroup = "";
                    e.CurrentGroupAt = DateTime.MinValue;
                    e.StoppedAt = at;
                    e.OneDragon = "";
                }

                // 粘性当前配置组：出现新的才替换（用户明确要求）。
                // 注意顺���：同一行既有"结束"又有"开始"时（例如
                // 「配置组 "A" 执行结束」后紧跟「配置组 "B" 加载完成」），
                // 各自落在各自的行上，不会互相覆盖。
                var gm = RxAnyGroup.Match(line);
                if (gm.Success)
                {
                    string g = "";
                    string onedragon = "";
                    if (gm.Groups["a"].Success) g = gm.Groups["a"].Value.Trim();
                    else if (gm.Groups["b"].Success) g = gm.Groups["b"].Value.Trim();
                    else if (gm.Groups["c"].Success) g = gm.Groups["c"].Value.Trim();
                    else if (gm.Groups["d"].Success) g = gm.Groups["d"].Value.Trim();
                    else if (gm.Groups["e"].Success) onedragon = gm.Groups["e"].Value.Trim();

                    // ★ 被"取消执行配置组: ..."标记成取消的那种行，配置组名是
                    //   "The operation was canceled."，它不是真正的组名，直接忽略。
                    bool bogus = g.Length == 0
                        || g == "无"
                        || g.StartsWith("The operation was canceled", StringComparison.OrdinalIgnoreCase)
                        || g.Contains("Exception", StringComparison.OrdinalIgnoreCase)
                        || g.Contains("取消", StringComparison.Ordinal);

                    if (onedragon.Length > 0) e.OneDragon = onedragon;
                    if (!bogus)
                    {
                        // 只有当这一行不是"结束/停止"行时，才算"开始运行这个组"。
                        if (!stopped && !string.Equals(e.CurrentGroup, g, StringComparison.Ordinal))
                        {
                            e.CurrentGroup = g;
                            e.CurrentGroupAt = at;
                            e.StoppedAt = DateTime.MinValue;
                        }
                        // 拾取按配置组归类。
                        if (!e.PicksByGroup.TryGetValue(g, out var bag))
                        { bag = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); e.PicksByGroup[g] = bag; }
                    }
                }

                // 拾取行：归到当前配置组。
                int pi = line.IndexOf("交互或拾取", StringComparison.Ordinal);
                if (pi >= 0)
                {
                    int q1 = line.IndexOf('"', pi);
                    int q2 = q1 >= 0 ? line.IndexOf('"', q1 + 1) : -1;
                    if (q1 >= 0 && q2 > q1)
                    {
                        string item = line.Substring(q1 + 1, q2 - q1 - 1).Trim();
                        if (item.Length > 0 && item != "无有效结果")
                        {
                            string key = e.CurrentGroup.Length == 0 ? "（未标注配置组）" : e.CurrentGroup;
                            if (!e.PicksByGroup.TryGetValue(key, out var bag))
                            { bag = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); e.PicksByGroup[key] = bag; }
                            bag.TryGetValue(item, out int c); bag[item] = c + 1;
                        }
                    }
                }

                // 尾窗：只保留今天 + 最近一段。
                PushTail(e, at, level, line, today);
            }
        }

        private static void PushTail(Entry e, DateTime at, string level, string text, string today)
        {
            // 太长的行截断，避免单行撑爆预算。
            if (text.Length > 400) text = text.Substring(0, 400) + "…";
            e.Tail.Enqueue(new BgiLogLine(at, level, text, e.NextSeq++));
            e.TailChars += text.Length + 24;
            while (e.Tail.Count > TailMaxLines || e.TailChars > TailBudgetChars)
            {
                var dropped = e.Tail.Dequeue();
                e.TailChars -= dropped.Text.Length + 24;
                if (e.Tail.Count == 0) e.TailChars = 0;
            }
        }

        private static bool TryStamp(Match m, DateTime now, out DateTime at)
        {
            at = default;
            string date = m.Groups["date"].Value.Trim();
            string time = m.Groups["time"].Value;
            string text = (date.Length > 0 ? date + " " : now.ToString("yyyy-MM-dd") + " ") + time;
            if (!DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces, out at)) return false;
            if (date.Length == 0 && at > now.AddSeconds(2)) at = at.AddDays(-1);
            return true;
        }

        /// <summary>
        /// 共用的 BGI 进程探测结果（2 秒缓存）。
        /// ★ 界面不要再各自调 BgiLogWatcher.Discover()：那是
        ///   "枚举全部进程 + 扫每个 BGI 目录找最新日志"，很贵。
        ///   一次刷新里被调用多次时会明显叠开销（实测占 4.8%）。
        /// </summary>
        public static Dictionary<int, BgiLogWatcher.Instance> SharedInstances() => DiscoverCached();

        private static Dictionary<int, BgiLogWatcher.Instance> DiscoverCached()
        {
            if (DateTime.UtcNow - _instancesAt < DiscoverTtl) return _instances;
            try { _instances = BgiLogWatcher.Discover(); } catch { _instances = new(); }
            _instancesAt = DateTime.UtcNow;
            return _instances;
        }

        /// <summary>按版本取缓存快照；版本没变就直接复用同一个对象。</summary>
        private static BgiLogSnapshot Cached(Entry e)
        {
            if (e.Cached != null && e.CachedVersion == e.Version) return e.Cached;
            e.Cached = Build(e);
            e.CachedVersion = e.Version;
            return e.Cached;
        }

        private static BgiLogSnapshot Build(Entry e)
        {
            var picks = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in e.PicksByGroup)
                picks[kv.Key] = new Dictionary<string, int>(kv.Value, StringComparer.OrdinalIgnoreCase);

            return new BgiLogSnapshot
            {
                AccountId = e.AccountId,
                UserName = e.UserName,
                Bound = BgiPathResolver.IsPinned(e.UserName),
                BgiExe = e.BgiExe,
                LogFile = e.LogFile,
                Cursor = e.Cursor,
                FileLength = e.FileLength,
                LastWrite = e.LastWrite,
                CurrentGroup = e.CurrentGroup,
                CurrentGroupAt = e.CurrentGroupAt,
                LastGroup = e.LastGroup,
                StoppedAt = e.StoppedAt,
                OneDragon = e.OneDragon,
                Status = e.Status,
                Groups = e.Groups.All(),
                Today = e.Today,
                PicksByGroup = picks,
                ErrorCount = e.ErrorCount,
                WarnCount = e.WarnCount,
                FailCount = e.FailCount,
                Tail = e.Tail.ToArray(),
                LastSeq = e.NextSeq - 1,
                UpdatedAt = DateTime.Now,
                Error = e.Error
            };
        }

        /// <summary>立即释放所有常驻（退出/切账号时用）。</summary>
        /// <summary>停止全部 watcher（退出 / 重置时调用）。</summary>
        private static void StopAllWatchers()
        {
            lock (Sync)
            {
                foreach (var e in Entries.Values) StopWatcher(e);
            }
        }

        /// <summary>
        /// t52 手动全量重扫：清除某账号（或全部）的持久化游标并重置内存状态，
        /// 下次 Pump 会从 0 重新全量读该账号日志。默认关闭，仅用户怀疑数据不对时用。
        /// 传 null/空 = 全部账号。
        /// </summary>
        public static void ForceFullRescan(string? account)
        {
            lock (Sync)
            {
                foreach (var e in Entries.Values)
                {
                    if (!string.IsNullOrWhiteSpace(account)
                        && !string.Equals(e.AccountId, account, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(e.UserName, account, StringComparison.OrdinalIgnoreCase))
                        continue;
                    IngestCursorStore.Reset(e.AccountId, e.LogFile);
                    e.Cursor = 0;
                    e.PendingLen = 0;
                    e.Primed = false;
                    e.SkippedRescan = false;
                    e.SkippedRescanBytes = 0;
                }
            }
            LogBus.Info("采集·手动重扫", string.IsNullOrWhiteSpace(account) ? "全部账号已重置游标，将全量重扫" : account + " 已重置游标，将全量重扫");
        }

        public static void Clear()
        {
            // ★ 必须先释放 watcher：Entries.Clear() 只是丢引用，
            //   FileSystemWatcher 内部持有系统句柄，不 Dispose 会泄漏。
            StopAllWatchers();
            // ★ t45 退出前强制落盘游标，保证下次启动从断点继续。
            lock (Sync)
            {
                foreach (var e in Entries.Values)
                    if (e.LogFile.Length > 0)
                        IngestCursorStore.Flush(e.AccountId, e.LogFile, Math.Max(0, e.Cursor - e.PendingLen), e.FileLength, e.LastWrite.Ticks);
            }
            lock (Sync) Entries.Clear();
        }
    }
}