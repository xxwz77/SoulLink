using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>
    /// 一个账号的**当前状态快照**。
    /// ★ 只读数据，任何模块想了解这个账号的情况都从这里取，不要自己去查。
    /// </summary>
    public sealed class AccountState
    {
        /// <summary>账号 Id（Account.Id）。</summary>
        public string AccountId = "";
        /// <summary>Windows 用户名。</summary>
        public string UserName = "";
        /// <summary>显示名（昵称）。</summary>
        public string Caption = "";

        // ── 会话 ──────────────────────────────────────────────────────
        /// <summary>当前会话号；0 = 没有会话。</summary>
        public int SessionId;
        /// <summary>会话状态字符串，例如 Active / Disc。</summary>
        public string SessionState = "";
        /// <summary>是否有可用会话（Id>0）。</summary>
        public bool HasSession => SessionId > 0;

        // ── BetterGI ─────────────────────────────────────────────────
        /// <summary>BGI 是否在运行。</summary>
        public bool BgiRunning;
        /// <summary>BGI 进程 Id。</summary>
        public int BgiPid;
        /// <summary>BGI 可执行文件路径（解析后的最终路径）。</summary>
        public string BgiPath = "";
        /// <summary>当前跟随的日志文件。</summary>
        public string BgiLogFile = "";
        /// <summary>日志文件最后修改时间。</summary>
        public DateTime BgiLogWrite;

        // ── 解析出的 BGI 状态 ─────────────────────────────────────────
        /// <summary>
        /// 最近若干行**原始日志**（约 400 行）。
        ///
        /// ★ 这里刻意**不提供解析后的"任务名/进度"等语义字段**：
        ///   BGI 的任务启动格式随配置组作者和版本变化（实测至少十几种），
        ///   中央解析必然出现"明明在跑却显示未在跑"的错误。
        ///   各使用方（流程条件、启动台、数据中心页）自己按需匹配这段原文即可。
        /// </summary>
        public string RecentLog = "";

        /// <summary>RecentLog 的采集时间。</summary>
        public DateTime RecentLogAt = DateTime.MinValue;

        /// <summary>今日累计统计（经验/摩拉/拾取/狗粮路线），由 DailyStatsCache 维护。</summary>
        public DailyStats Today = new DailyStats();

        // ── 配置组运行状态 ───────────────────────────────────────────
        /// <summary>
        /// 今天各配置组的运行状态（从未运行/执行中/已结束/失败/已取消）。
        /// ★ 由日志增量解析维护，所有条件共享这一份结果，
        ///   避免每次判定都去扫几十 MB 的日志。
        /// </summary>
        public ScriptGroupTracker Groups = new ScriptGroupTracker();
        public string GroupsSource = "";
        public DateTime GroupsUpdatedAt = DateTime.MinValue;
        public string GroupsReadError = "";

        // ── 狗粮当日结论（来自 AAA 脚本的 records 文件）──────────────
        /// <summary>今天的收尾路线：A=走联机，B=自己跑。</summary>
        public DogfoodEndingRoute DogfoodRoute = DogfoodEndingRoute.Unknown;
        /// <summary>今日狗粮获得的经验（records 里的值）。</summary>
        public long DogfoodExp;
        /// <summary>今日狗粮获得的摩拉（仅展示，不参与判断）。</summary>
        public long DogfoodMora;
        /// <summary>records 记录日期文本。</summary>
        public string DogfoodDate = "";
        /// <summary>狗粮结论是否有效（严格当日）。</summary>
        public bool DogfoodValid;
        /// <summary>无效原因。</summary>
        public string DogfoodReason = "";
        /// <summary>
        /// 经验是否已达该路线参考上限（A=13万 / B=5万）。
        /// ★ 达到后不再解析日志统计收益，避免持续读取大文件。
        /// </summary>
        public bool DogfoodExpReachedLimit;
        public string DogfoodRecordPath = "";
        public DateTime DogfoodReadAt = DateTime.MinValue;
        public string DogfoodRecordStamp = "";

        /// <summary>Today 统计已解析到该日志文件的哪个字节位置（增量更新用）。</summary>
        public string StatsFile = "";
        /// <summary>Today 统计已消费到的文件偏移。</summary>
        public long StatsOffset;

        /// <summary>
        /// 日志里最近一条"看起来像任务启动"的行原文（仅供展示，不做语义归类）。
        /// ★ 只是给数据中心页面显示用，匹配规则宽松，匹配不到就是空。
        /// </summary>
        public string LastTaskLine = "";

        // ── 触发用 ────────────────────────────────────────────────────
        /// <summary>本次启动 BGI 的时间（用于"启动后 N 秒"这类判断）。</summary>
        public DateTime? BgiStartedAt;
        /// <summary>是否在跑游戏（原神）。</summary>
        public bool GameRunning;

        /// <summary>本快照的生成时间。</summary>
        public DateTime UpdatedAt = DateTime.Now;

        /// <summary>供日志/UI 显示的简短描述。</summary>
        public override string ToString()
            => Caption + "[" + (HasSession ? "会话" + SessionId : "无会话") + "]"
               + (BgiRunning ? " BGI#" + BgiPid : " 无BGI");
    }

    /// <summary>
    /// 账号状态中心。
    ///
    /// ★ 为什么要有它：
    ///   原来每个模块（触发器、条件、发车、启动台、设置页）都自己调
    ///   SessionService.QueryAsync + BgiLogWatcher.Discover + 读日志文件。
    ///   调度器 1 秒一轮、每个流程的每个条件都走一遍，开销随条件数线性增长，
    ///   而且各处拿到的快照时间点不同，容易出现不一致。
    ///
    /// ★ 设计：
    ///   · 每个账号一份 AccountState，集中刷新。
    ///   · 所有读取方只读快照，**不再自己发起查询**。
    ///   · 日志文件用「最后修改时间」判断要不要重读，BGI 空闲时零开销。
    ///   · 刷新由单一后台循环驱动，周期可配（默认 1 秒）。
    /// </summary>
    public static class AccountStateCenter
    {
        private static readonly object _sync = new object();
        private static readonly Dictionary<string, AccountState> _states
            = new Dictionary<string, AccountState>(StringComparer.OrdinalIgnoreCase);

        private static System.Threading.Timer? _timer;
        private static int _refreshing;
        private static DateTime _lastRefresh = DateTime.MinValue;

        /// <summary>刷新周期（毫秒）。</summary>
        public static int RefreshMs { get; private set; } = 1000;

        /// <summary>快照整体更新后触发（在后台线程）。</summary>
        public static event Action? Changed;

        /// <summary>某个账号状态发生有意义的变化时触发。</summary>
        public static event Action<AccountState>? AccountChanged;
        private static int _configurationDirty;
        /// <summary>路径/配置变更后，让状态中心下一轮立即按新路径读取。</summary>
        public static void MarkConfigurationDirty() => Interlocked.Exchange(ref _configurationDirty, 1);

        // ── 生命周期 ──────────────────────────────────────────────────

        /// <summary>启动后台刷新循环。重复调用无副作用。</summary>
        public static void Start(AppConfig cfg, int refreshMs = 1000)
        {
            try
            {
                RefreshMs = Math.Max(500, refreshMs);
                lock (_sync)
                {
                    SyncAccounts(cfg);
                    if (_timer != null) return;
                    _timer = new System.Threading.Timer(_ => RefreshOnce(cfg), null,
                        dueTime: 200, period: RefreshMs);
                }
            }
            catch (Exception ex) { App.AppendErrorLog("AccountStateCenter.Start", ex); }
        }

        /// <summary>停止刷新（程序退出时调用）。</summary>
        public static void Stop()
        {
            try
            {
                lock (_sync)
                {
                    _timer?.Dispose();
                    _timer = null;
                }
            }
            catch { }
        }

        // ── 读 ────────────────────────────────────────────────────────

        /// <summary>取某个账号的状态快照；没有则返回 null。</summary>
        public static AccountState? Get(string accountId)
        {
            if (string.IsNullOrWhiteSpace(accountId)) return null;
            lock (_sync)
                return _states.TryGetValue(accountId, out var s) ? s : null;
        }

        /// <summary>按用户名取（很多调用方只有用户名）。</summary>
        public static AccountState? GetByUser(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) return null;
            lock (_sync)
            {
                foreach (var s in _states.Values)
                    if (string.Equals(s.UserName, userName, StringComparison.OrdinalIgnoreCase))
                        return s;
            }
            return null;
        }

        /// <summary>全部快照（只读副本）。</summary>
        public static List<AccountState> All()
        {
            lock (_sync) return _states.Values.ToList();
        }

        /// <summary>
        /// 最近一次刷新拿到的**完整会话列表**（含没有配成账号的用户）。
        /// ★ 会话页需要全部会话，而账号快照只覆盖配置里的账号，所以单独保留一份。
        /// </summary>
        public static List<SessionInfo> Sessions
        {
            get { lock (_sync) return _sessions.ToList(); }
        }

        private static List<SessionInfo> _sessions = new List<SessionInfo>();

        /// <summary>上次刷新时间。</summary>
        public static DateTime LastRefresh { get { lock (_sync) return _lastRefresh; } }

        /// <summary>强制刷新一次（测试或需要立即拿到最新值时用）。</summary>
        public static void RefreshNow(AppConfig cfg) => RefreshOnce(cfg);

        // ── 内部 ──────────────────────────────────────────────────────

        /// <summary>让账号列表与配置保持一致。</summary>
        private static void SyncAccounts(AppConfig cfg)
        {
            try
            {
                var accounts = cfg?.Accounts ?? new List<Models.Account>();
                var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var a in accounts)
                {
                    if (string.IsNullOrWhiteSpace(a.Id)) continue;
                    alive.Add(a.Id);
                    if (!_states.TryGetValue(a.Id, out var st))
                    {
                        st = new AccountState { AccountId = a.Id };
                        _states[a.Id] = st;
                    }
                    st.UserName = a.UserName ?? "";
                    st.Caption = a.Caption ?? a.UserName ?? "";
                }

                foreach (var id in _states.Keys.ToList())
                    if (!alive.Contains(id)) _states.Remove(id);
            }
            catch { }
        }

        /// <summary>刷新一轮。单轮不重入，避免卡住时堆叠。</summary>
        private static void RefreshOnce(AppConfig cfg)
        {
            if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
            {
                // ★ 临时诊断：跳过次数要留痕，否则"看起来在跑其实全跳过"无从发现。
                InputTrace.Write("REFRESH_SKIP", "上一轮未完成，本轮跳过");
                return;
            }
            var __sw = System.Diagnostics.Stopwatch.StartNew();
            long __tQuery = 0, __tBgi = 0, __tProc = 0, __tAcct = 0;
            try
            {
                SyncAccounts(cfg);
                if (Interlocked.Exchange(ref _configurationDirty, 0) != 0)
                {
                    lock (_sync) foreach (var st in _states.Values) { st.BgiPath = ""; st.BgiLogWrite = DateTime.MinValue; }
                    BgiDataCenter.RefreshAll(AccountScope.Accounts(cfg).Select(a => a.UserName));
                }

                // ① 一次查询会话（所有账号共用）
                var __s1 = __sw.ElapsedMilliseconds;
                List<Models.SessionInfo> sessions;
                try { sessions = SessionService.QueryAsync().GetAwaiter().GetResult(); }
                catch { sessions = new List<SessionInfo>(); }
                __tQuery = __sw.ElapsedMilliseconds - __s1;
                lock (_sync) _sessions = sessions;

                // ② 一次枚举 BGI 进程（所有账号共用）
                Dictionary<int, BgiLogWatcher.Instance> bgi;
                try { bgi = BgiLogWatcher.Discover(); }
                catch { bgi = new Dictionary<int, BgiLogWatcher.Instance>(); }

                // ★ 先把"会话号 → 用户名"映射更新好，再刷新进程快照。
                //   进程枚举只拿得到 SessionId，用户名要靠这份映射补；
                //   而会话列表本来就在上面①查过了，直接复用，不额外起进程。
                ProcessOwnerSnapshot.SessionUsers.Set(sessions);

                // 一次枚举进程（内存枚举，约 20ms）；流程触点不再各自启动 tasklist。
                var __s3 = __sw.ElapsedMilliseconds;
                ProcessOwnerSnapshot.Refresh();
                __tProc = __sw.ElapsedMilliseconds - __s3;
                bool anyChanged = false;

                List<AccountState> snapshot;
                lock (_sync) snapshot = _states.Values.ToList();

                foreach (var st in snapshot)
                {
                    try { RefreshAccount(st, sessions, bgi, cfg, ref anyChanged); }
                    catch { }
                }

                lock (_sync) _lastRefresh = DateTime.Now;

                if (anyChanged)
                {
                    try { Changed?.Invoke(); } catch { }
                }

                // ★ 临时诊断：每轮各阶段耗时（只在开启全量日志时输出）。
                if (SoulLink.Controls.RdpConsole.VerboseLog)
                {
                    InputTrace.Write("REFRESH_TIME", "本轮总耗时=" + __sw.ElapsedMilliseconds + "ms"
                        + " query=" + __tQuery + "ms 进程快照=" + __tProc + "ms"
                        + " 刷新计数=" + ProcessOwnerSnapshot.RefreshCount);
                }
            }
            catch (Exception ex) { App.AppendErrorLog("AccountStateCenter.Refresh", ex); }
            finally { Interlocked.Exchange(ref _refreshing, 0); }
        }

        /// <summary>
        /// 全量读取时最多读多少字节。
        /// ★ 日志按天滚动，正常一天 20~60MB；这里留 128MB 余量，
        ///   超过时只读尾部（并会体现在日志里），避免异常大的文件拖慢启动。
        /// </summary>
        private const long FullScanLimitBytes = 128L * 1024 * 1024;

        /// <summary>
        /// 对外提供的全量读取（供条件判定在快照不可用时兜底）。
        /// ★ 与内部首次重建用的是同一套逻辑，保证结果一致。
        /// </summary>
        public static string ReadLogFull(string path) => ReadLogHead(path, FullScanLimitBytes);

        /// <summary>
        /// 读取日志用于一次性重建（只读使用，不占用写锁）。
        /// 超过 limit 时只读尾部，并丢弃首行防止截断。
        /// </summary>
        private static string PreviousBusinessLogPath(string currentLog)
        {
            string dir = Path.GetDirectoryName(currentLog) ?? "";
            string previous = DateTime.Today.AddDays(-1).ToString("yyyyMMdd");
            return Path.Combine(dir, "better-genshin-impact" + previous + ".log");
        }

        private static string ReadLogHead(string path, long limit)
        {
            try
            {
                if (!File.Exists(path)) return "";
                var fi = new FileInfo(path);
                long take = Math.Min(fi.Length, limit);
                long start = fi.Length - take;

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (start > 0) fs.Seek(start, SeekOrigin.Begin);

                // 分段读，避免一次性分配几十 MB 的 byte[]
                var sb = new System.Text.StringBuilder((int)Math.Min(take, 4 * 1024 * 1024));
                var buf = new byte[1024 * 1024];
                int got;
                while ((got = fs.Read(buf, 0, buf.Length)) > 0)
                    sb.Append(System.Text.Encoding.UTF8.GetString(buf, 0, got));

                string text = sb.ToString();
                if (start > 0)
                {
                    int nl = text.IndexOf('\n');
                    if (nl >= 0 && nl + 1 < text.Length) text = text.Substring(nl + 1);
                }
                return text;
            }
            catch { return ""; }
        }

        private static void RefreshAccount(AccountState st,
            List<Models.SessionInfo> sessions,
            Dictionary<int, BgiLogWatcher.Instance> bgi,
            AppConfig cfg, ref bool anyChanged)
        {
            // ── 会话 ──
            Models.SessionInfo? sess = null;
            try { sess = SessionService.Find(sessions, st.UserName); } catch { }
            int newSid = sess?.Id ?? 0;
            string newState = sess?.State ?? "";
            if (newSid != st.SessionId || newState != st.SessionState)
            {
                st.SessionId = newSid;
                st.SessionState = newState;
                anyChanged = true;
            }

            // ── BGI 进程 ──
            BgiLogWatcher.Instance? inst = null;
            if (newSid > 0) bgi.TryGetValue(newSid, out inst);

            bool wasRunning = st.BgiRunning;
            st.BgiRunning = inst != null;
            st.BgiPid = inst?.Pid ?? 0;
            if (inst != null)
            {
                if (!wasRunning) st.BgiStartedAt = DateTime.Now;
                if (st.BgiLogFile != inst.LogFile)
                {
                    st.BgiLogFile = inst.LogFile;
                    st.BgiLogWrite = DateTime.MinValue;   // 强制重读
                }
            }
            else if (wasRunning)
            {
                st.BgiStartedAt = null;
                st.BgiLogFile = "";
                st.BgiLogWrite = DateTime.MinValue;
                st.RecentLog = "";
                st.RecentLogAt = DateTime.MinValue;
                st.LastTaskLine = "";
            }

            // ── BGI 路径（统一解析，供各处取用）──
            try
            {
                var info = BgiPathResolver.Resolve(st.UserName, sessions, persist: false);
                if (!string.Equals(st.BgiPath, info.Path, StringComparison.OrdinalIgnoreCase))
                {
                    st.BgiPath = info.Path;
                    anyChanged = true;
                }
            }
            catch { }

            // ── 狗粮当日结论（读 records 文件，很轻量）──
            try
            {
                if (string.IsNullOrWhiteSpace(st.BgiPath))
                {
                    BusinessDayData.SetDogfood(st.AccountId, "", new List<DogfoodRecord>());
                    st.DogfoodRoute = DogfoodEndingRoute.Unknown;
                    st.DogfoodValid = false; st.DogfoodReason = "未配置 BGI 路径";
                    st.DogfoodExp = 0; st.DogfoodMora = 0; st.DogfoodExpReachedLimit = false;
                }
                else
                {
                    string recordFile = DogfoodRecordReader.ResolveRecordPath(st.BgiPath, DogfoodRecordReader.ResolveAccountName(st.BgiPath));
                    var info = new FileInfo(recordFile);
                    string stamp = info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks : "missing";
                    if (stamp != st.DogfoodRecordStamp)
                    {
                        var records = DogfoodRecordReader.ReadAllForBgi(st.BgiPath, out recordFile);
                        st.DogfoodRecordStamp = stamp;
                        BusinessDayData.SetDogfood(st.AccountId, recordFile, records);
                    }
                    st.DogfoodRecordPath = recordFile;
                    var rec = BusinessDayData.GetView(st.AccountId, 4, DateTime.Now).Dogfood;
                    st.DogfoodReadAt = DateTime.Now;
                    bool changed = st.DogfoodRoute != rec.Route
                                || st.DogfoodExp != rec.Exp
                                || st.DogfoodValid != rec.IsValid;
                    st.DogfoodRoute = rec.Route;
                    st.DogfoodExp = rec.Exp;
                    st.DogfoodMora = rec.Mora;
                    st.DogfoodDate = rec.Date;
                    st.DogfoodValid = rec.IsValid;
                    st.DogfoodReason = rec.Reason;
                    st.DogfoodExpReachedLimit = rec.ExpReachedLimit;
                    if (changed) anyChanged = true;
                }
            }
            catch { }

            // ── 日志内容（只在文件有变化时重读）──
            //
            // ★ 今日收益已达该路线参考上限（A=13万 / B=5万）后，
            //   不再解析日志统计 —— 收益不会再明显增长，没有必要继续读大文件。
            // 收益达到上限只影响收益统计，不能阻断 Groups / RecentLog / 快照时间。
            // 原实现 return 会让所有配置组与日志触点永远停在旧值。
            if (st.BgiRunning && !string.IsNullOrWhiteSpace(st.BgiLogFile))
            {
                DateTime write;
                try { write = File.GetLastWriteTime(st.BgiLogFile); }
                catch { write = DateTime.MinValue; }

                if (write != st.BgiLogWrite)
                {
                    st.BgiLogWrite = write;
                    try
                    {
                        // ★ 数据中心只负责"读一次原文，给大家共用"，**不做语义解析**。
                        //
                        //   原因（实测）：配置组来自不同作者，任务启动格式至少有十几种
                        //   （开始执行地图追踪任务 / 选用配队 / 配置组加载完成 /
                        //    开始执行JS脚本 / 锄地一条龙任务启动 / 开始执行路线 …），
                        //   而且随版本变化。中央解析永远追不上，只会出现
                        //   "A1 明明在跑，却显示未在跑任务"这种错误。
                        //
                        //   由各使用方自己匹配原文最准：流程条件本来就是用户配的正则/关键字。
                        st.RecentLog = BgiLogWatcher.TailLinesDeep(st.BgiLogFile, 400);
                        st.RecentLogAt = DateTime.Now;

                        // ★ 维护"今日统计"：经验 / 摩拉 / 拾取 / 狗粮路线。
                        //   首次见到该文件 → 整份重建；之后只喂增量，避免重复计数。
                        try
                        {
                            if (!string.Equals(st.StatsFile, st.BgiLogFile, StringComparison.OrdinalIgnoreCase))
                            {
                                // ★ 首次见到这份日志（程序启动 / 跨天换文件）：
                                //   把**当天全部日志**读一遍，一次性重建所有衍生数据。
                                //   实测 21MB 全量读取+解析约 58ms，只在这一时刻发生，
                                //   之后永远只吃增量（每次几 KB，2ms 以内）。
                                //
                                //   这样好处是：程序半夜重启、或早上才打开，
                                //   也能立刻拿到今天完整的配置组状态和收益，
                                //   不会因为"只读最后 4000 行"而漏掉早上跑过的配置组。
                                st.StatsFile = st.BgiLogFile;
                                st.StatsOffset = 0;

                                string full = ReadLogHead(st.BgiLogFile, FullScanLimitBytes);

                                // ① 今日收益统计
                                string previousFile = PreviousBusinessLogPath(st.BgiLogFile);
                                string previousText = File.Exists(previousFile) ? ReadLogHead(previousFile, FullScanLimitBytes) : "";
                                BusinessDayData.Reset(st.AccountId, st.BgiLogFile, full, previousText, previousFile);

                                // ② 配置组运行状态
                                try
                                {
                                    st.Groups.Reset();
                                    if (!string.IsNullOrEmpty(full)) st.Groups.Update(full);
                                    st.GroupsSource = st.BgiLogFile; st.GroupsUpdatedAt = DateTime.Now; st.GroupsReadError = "";
                                }
                                catch { }

                                // ③ 其他"当天累积"类数据以后都挂到这里，避免各自扫日志

                                st.StatsOffset = new FileInfo(st.BgiLogFile).Length;
                            }
                            else
                            {
                                long len = new FileInfo(st.BgiLogFile).Length;
                                if (len > st.StatsOffset)
                                {
                                    // 只读新增部分
                                    using (var fs = new FileStream(st.BgiLogFile, FileMode.Open,
                                        FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                                    {
                                        fs.Seek(st.StatsOffset, SeekOrigin.Begin);
                                        var buf = new byte[len - st.StatsOffset];
                                        int got = fs.Read(buf, 0, buf.Length);
                                        if (got > 0)
                                        {
                                            string chunk = System.Text.Encoding.UTF8
                                                .GetString(buf, 0, got);
                                            DailyStatsCache.Update(st.UserName, chunk);
                                            BusinessDayData.Append(st.AccountId, st.BgiLogFile, chunk);
                                            // 配置组状态也走同一份增量，避免重复读文件
                                            st.Groups.Update(chunk);
                                            st.GroupsSource = st.BgiLogFile; st.GroupsUpdatedAt = DateTime.Now; st.GroupsReadError = "";
                                        }
                                    }
                                    st.StatsOffset = len;
                                }
                            }
                            st.Today = BusinessDayData.GetView(st.AccountId, 4, DateTime.Now).Stats;
                        }
                        catch { }
                    }
                    catch { }
                    anyChanged = true;
                    try { AccountChanged?.Invoke(st); } catch { }
                }
            }

            st.UpdatedAt = DateTime.Now;
        }
    }
}
