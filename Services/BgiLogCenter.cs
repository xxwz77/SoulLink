using System;
using System.Collections.Generic;
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
        private const int MaxBytesPerTick = 32 * 1024 * 1024;
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
                Cursor = 0;
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

            bool any = false;
            foreach (var e in work)
            {
                try
                {
                    // 取出并清标记：期间新来的事件会重新置位，下一拍处理。
                    int dirty = Interlocked.Exchange(ref e.Dirty, 0);

                    // ★ 日志没动时做「低成本判活」：
                    //   GetProcessById 只要 0.012ms（枚举全部进程要 23.7ms）。
                    //   活着 → 什么都不做；死了 → 标记脏，让 Step 更新状态。
                    if (dirty == 0)
                    {
                        if (e.BgiPid > 0 && IsAlive(e.BgiPid))
                        {
                            // 正常挂机：BGI 活着但没写日志，不需要任何动作
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
            var alive = new HashSet<string>(list.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
            lock (Sync)
            {
                foreach (var id in Entries.Keys.ToList())
                    if (!alive.Contains(id)) Entries.Remove(id);
            }

            if (any) { try { Changed?.Invoke(); } catch { } }
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
                    return had;
                }
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
                }
            }

            // ★ 日志文件确定后建立 watcher：之后 BGI 一写入就立刻解析，
            //   不再等 AccountStateCenter 的轮询周期。
            EnsureWatcher(e);

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
                    ProcessChunk(e, chunk);
                    changed = true;

                    pos += got;
                    _ = carry;
                }
                lock (Sync) { e.Cursor = end; e.FileLength = len; e.LastWrite = write; }
                bool wasPrimed = e.Primed;
                e.Primed = true;

                // ★ 首次全量扫完：把刚才那几十 MB 的临时字符串/数组彻底还回去。
                //   不这么做的话 LOH 会一直挂着碎片（旧实现 Priv 1.5GB 就是这么来的）。
                if (!wasPrimed && budget > 8 * 1024 * 1024)
                {
                    try
                    {
                        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                        GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);
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
        private static void ProcessChunk(Entry e, string chunk)
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

        public static void Clear()
        {
            // ★ 必须先释放 watcher：Entries.Clear() 只是丢引用，
            //   FileSystemWatcher 内部持有系统句柄，不 Dispose 会泄漏。
            StopAllWatchers();
            lock (Sync) Entries.Clear();
        }
    }
}