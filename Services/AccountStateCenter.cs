using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

        /// <summary>
        /// 尾窗的结构化行（含单调行序号），供日志触点「事件锁存」逐行消费。
        /// ★ RecentLog 是给界面/旧电平触点看的拼接文本；锁存游标必须用带
        ///   序号的整行列表推进，否则环形队列把旧行挤掉后无法判「新增」。
        /// </summary>
        public IReadOnlyList<BgiLogLine> LogTail = Array.Empty<BgiLogLine>();

        /// <summary>LogTail 里最大的行序号；也是该账号日志流的当前游标上界。</summary>
        public long LogTailSeq;

        /// <summary>LogTail 来源的日志文件（跨天滚动后文件名变化，用来重建游标）。</summary>
        public string LogTailFile = "";

        /// <summary>今日累计统计（经验/摩拉/拾取/狗粮路线），由 BgiLogCenter 维护。</summary>
        public DailyStats Today = new DailyStats();

        // ── 配置组运行状态（来自 BgiLogCenter 快照，只读）────────────
        /// <summary>
        /// 今天各配置组的运行状态（从未运行/执行中/已结束/失败/已取消）。
        /// ★ 由 BgiLogCenter 单遍流式解析维护；这里只是快照引用的拷贝，
        ///   不要再往这里写数据，也不要在 UI 线程重建它。
        /// </summary>
        public IReadOnlyList<ScriptGroupRun> Groups = Array.Empty<ScriptGroupRun>();
        public string GroupsSource = "";
        public DateTime GroupsUpdatedAt = DateTime.MinValue;
        public string GroupsReadError = "";

        /// <summary>是否已绑定 BGI 路径。未绑定 = 不读日志、界面显示"未绑定"。</summary>
        public bool Bound;

        /// <summary>
        /// **当前运行中的配置组名**（粘性快照：出现新的才替换，跨天滚动时继承）。
        /// ★ 这是"当前配置组"的唯一权威来源，其它地方不要再各扫一遍日志。
        /// </summary>
        public string CurrentGroup = "";
        /// <summary>CurrentGroup 最后一次被日志确认的时间。</summary>
        public DateTime CurrentGroupAt = DateTime.MinValue;
        /// <summary>最近一次结束/停止的配置组（不代表在运行）。</summary>
        public string LastGroup = "";
        /// <summary>最近一次判定为"停止"的时刻。</summary>
        public DateTime StoppedAt = DateTime.MinValue;
        /// <summary>当前是否真的有一个配置组在跑（停止后为 false）。</summary>
        public bool GroupRunning => CurrentGroup.Length > 0;
        /// <summary>当前一条龙配置名。</summary>
        public string OneDragon = "";

        /// <summary>今日报错 / 告警 / 失败计数（来自同一次单遍扫描）。</summary>
        public int ErrorCount, WarnCount, FailCount;

        /// <summary>当前日志文件长度。</summary>
        public long LogBytes;
        /// <summary>BgiLogCenter 常驻字符总量（内存哨兵）。</summary>
        public long CenterChars;

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
        /// <summary>
        /// 上次**读取** records 文件的时刻（每轮刷新都更新）。
        /// ★ 注意：它不代表数据新鲜度 —— 数据只在该文件被脚本写入时才变。
        ///   界面上要同时显示 <see cref="DogfoodFileWriteAt"/>，否则用户会误以为
        ///   「读数在跳 = 数据在更新」，实际可能已停了半天（2026-11-26 用户误解点）。
        /// </summary>
        public DateTime DogfoodReadAt = DateTime.MinValue;
        /// <summary>
        /// records 文件的**最后写入时刻**（= 数据真实新鲜度）。
        /// 文件不存在时为 MinValue。界面按此显示「数据更新于 …（N 小时前）」。
        /// </summary>
        public DateTime DogfoodFileWriteAt = DateTime.MinValue;
        public string DogfoodRecordStamp = "";

        /// <summary>
        /// 狗粮 records 的读取结果（04:00 业务日）。
        /// ★ 直接从 DogfoodRecordReader 拿，不再经过 BusinessDayData —— 那份
        ///   投影为了日志统计把原文也一起缓存了，而狗粮只需要这几条记录。
        /// </summary>
        public List<DogfoodRecord> DogfoodRecords = new List<DogfoodRecord>();

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
        private static readonly Lock _sync = new();
        private static readonly Dictionary<string, AccountState> _states
            = new Dictionary<string, AccountState>(StringComparer.OrdinalIgnoreCase);

        private static System.Threading.Timer? _timer;
        private static AppConfig? _timerConfig;
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
                    _timerConfig = cfg;
                    _timer = new System.Threading.Timer(_ => RefreshOnce(cfg), null,
                        dueTime: 200, period: RefreshMs);
                }

                // ★ 2026-11-26：狗粮 records 变化后的快速响应。
                //   慢档 30 秒轮询 + watcher 命中 = 最多 30 秒延迟；
                //   这里加一个 5 秒兜底拍：只要 watcher 报过脏就立刻走一轮刷新
                //   （RefreshOnce 自带重入保护，不会堆积）。
                //   ★ 走 TickHub 而不是新建 DispatcherTimer（AGENTS.md 性能约束）。
                try
                {
                    _dogfoodTickSub ??= TickHub.RegisterBackground("AccountStateCenter.DogfoodWatch", 5, () =>
                    {
                        try
                        {
                            var c = _timerConfig;
                            if (c == null) return;
                            // 任一账号的狗粮文件报过变化 → 触发一轮完整刷新。
                            // ★ 这里用 PeekDirty（只读不清）：清除动作必须留给
                            //   RefreshOnce 里的 ConsumeDirty，否则标记被这里吃掉，
                            //   那一轮刷新就检测不到「文件变了」而跳过重解析。
                            foreach (var kv in _states)
                                if (kv.Value != null && DogfoodWatcher.PeekDirty(kv.Value.UserName))
                                {
                                    RefreshOnce(c);
                                    return;
                                }
                        }
                        catch { }
                    });
                }
                catch { }
            }
            catch (Exception ex) { App.AppendErrorLog("AccountStateCenter.Start", ex); }
        }

        /// <summary>狗粮 watcher 的 5 秒兜底拍的订阅句柄。</summary>
        private static IDisposable? _dogfoodTickSub;

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
            // ★ 释放狗粮 records 的 FileSystemWatcher 系统句柄。
            //   本仓库踩过「Entries.Remove 没 Dispose 导致句柄泄漏」的坑
            //   （见 BgiLogCenter.cs 的 StopAllWatchers），这里同样必须收口。
            try { _dogfoodTickSub?.Dispose(); _dogfoodTickSub = null; } catch { }
            try { DogfoodWatcher.StopAll(); } catch { }
        }

        // ── 读 ────────────────────────────────────────────────────────

        /// <summary>取某个账号的状态快照；没有则返回 null。</summary>
        public static AccountState? Get(string accountId)
        {
            if (string.IsNullOrWhiteSpace(accountId)) return null;
            lock (_sync)
                return _states.TryGetValue(accountId, out var s) ? s : null;
        }

        /// <summary>
        /// 按用户名取（很多调用方只有用户名）。
        ///
        /// ★★ 2.17.49：兼容"展示名"。★★
        ///   界面上的行标题是 "A2 · 山客"（用户名 + 备注名），
        ///   曾经有调用方直接把这个串传进来 —— 精确比较永远不命中，
        ///   拿到的 null 被当成"未绑定"，于是**已绑定的账号显示「BGI 未绑定」**
        ///   （用户实测截图：A2 · 山客 / A3 · 玄月 都写未绑定）。
        ///   这里做一层兜底：精确匹配不到时，再按分隔符切成候选名逐个比。
        ///   这是**防御**，不是鼓励传展示名 —— 调用方仍应传真实用户名。
        /// </summary>
        public static AccountState? GetByUser(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) return null;
            lock (_sync)
            {
                foreach (var s in _states.Values)
                    if (string.Equals(s.UserName, userName, StringComparison.OrdinalIgnoreCase))
                        return s;

                // 展示名兜底："A2 · 山客" / "A2 山客" / "A2·山客" 都取第一段。
                string bare = BareName(userName);
                if (bare.Length > 0 && !string.Equals(bare, userName, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var s in _states.Values)
                        if (string.Equals(s.UserName, bare, StringComparison.OrdinalIgnoreCase))
                            return s;
                }
            }
            return null;
        }

        /// <summary>把 "A2 · 山客" 这类展示串还原成 Windows 用户名（取第一个分隔符之前）。</summary>
        internal static string BareName(string shown)
        {
            string s = (shown ?? "").Trim();
            if (s.Length == 0) return "";
            int cut = s.IndexOfAny(new[] { '·', '\u00B7', '\u30FB' });
            if (cut < 0) cut = s.IndexOf(' ');
            return cut > 0 ? s.Substring(0, cut).Trim() : s;
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

        /// <summary>
        /// 用户主动刷新：强制真查一次系统会话，并把后续切成快档一小段时间。
        ///
        /// ★ 与 RefreshNow 的区别：这个方法在后台线程执行、可 await，
        ///   调用方（界面）不会因此卡住；而 RefreshNow 是同步的。
        /// ★ 内部等待"上一轮若正在跑"最多一小会儿，保证拿到的是点击之后的数据。
        /// </summary>
        public static async System.Threading.Tasks.Task ForceRefreshAsync(AppConfig cfg)
        {
            try
            {
                // 让状态中心进入快档（覆盖"用户刚点了刷新、接下来几秒还在看"的场景）
                Kick(cfg, "用户主动刷新");

                // 等正在跑的那一轮结束（最多 3 秒），再补一轮，确保数据在点击之后
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (Volatile.Read(ref _refreshing) == 1 && DateTime.UtcNow < deadline)
                    await System.Threading.Tasks.Task.Delay(50).ConfigureAwait(false);

                await System.Threading.Tasks.Task.Run(() => RefreshOnce(cfg)).ConfigureAwait(false);
            }
            catch { }
        }

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
                {
                    if (alive.Contains(id)) continue;
                    // ★ 账号被移除 → 先释放它的狗粮 watcher 句柄，再丢状态（防泄漏）。
                    try { if (_states.TryGetValue(id, out var dead) && dead != null) DogfoodWatcher.StopFor(dead.UserName); } catch { }
                    _states.Remove(id);
                }
            }
            catch { }
        }


        /// <summary>把快照尾窗拼成一段文本（供界面展示与日志条件匹配）。</summary>
        private static string BuildTailText(BgiLogSnapshot snap)
        {
            return snap?.TailText ?? "";
        }

        /// <summary>兜底读文件尾部（只在走不了快照的极少数情况下使用）。</summary>
        private static string FallbackReadHead(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                long take = Math.Min(fs.Length, 4L * 1024 * 1024);
                fs.Seek(fs.Length - take, SeekOrigin.Begin);
                var buf = new byte[take];
                int got = fs.Read(buf, 0, buf.Length);
                return Encoding.UTF8.GetString(buf, 0, got);
            }
            catch { return ""; }
        }

        // ── 会话检测策略：事件驱动 + 慢速兜底 ──────────────────────────
        //
        // ★★ 为什么不是周期高频：★★
        //   每轮都要起 query.exe + qwinsta 两个子进程（实测各约 50ms），
        //   并持续产生句柄（探针实测 +3~7 个/轮）。24 小时挂机时这是纯浪费——
        //   会话状态绝大部分时间根本不变。
        //
        // ★★ 实际使用节奏（用户描述）：★★
        //   启动 → 连 RDP → 投送 → 进入挂机；
        //   之后状态长期不变，唯一需要的是「发现掉线」。
        //
        //   所以：
        //     · 启动时            → 查一次
        //     · RDP 重连 / 新登录  → 立刻查一次（事件触发）
        //     · 需要判断在线状态时 → 按需查（RefreshNow）
        //     · 其余时间          → 慢速兜底（默认 30 秒），只用来发现掉线
        //
        //   面板显示不依赖这个频率：它读的是快照，30 秒更新一次完全够用。

        /// <summary>兜底轮询间隔（毫秒）。仅用于发现掉线，不做高频刷新。</summary>
        private const int IdleRefreshMs = 30000;
        // ★ 回到 30 秒：BGI 日志状态改由 FileSystemWatcher 事件驱动
        //   （BgiLogCenter 自己跟文件，实测延迟约 0.2 秒），
        //   这里的轮询只用于「发现会话掉线」和 watcher 漏事件时的兜底。
        //   会话掉线本来就不是秒级需求，30 秒足够。
        /// <summary>启动后的快档持续时间：连 RDP、投送都发生在这段时间里。</summary>
        private const int StartupFastMs = 1000;
        private static readonly TimeSpan StartupFastWindow = TimeSpan.FromSeconds(60);
        private static DateTime _startedAt = DateTime.MinValue;
        /// <summary>事件触发的窗口：触发后这段时间内保持快档。</summary>
        private static DateTime _lastKickAt = DateTime.MinValue;
        private static readonly TimeSpan KickFastWindow = TimeSpan.FromSeconds(45);
        // 原 20 秒。实测 RDP 连接后的完整链路（代理启动 → 建管道 → 投送 →
        // 代理连上）经常超过 20 秒，窗口一到期就退回慢档，
        // 界面看起来像"卡在中间不动"。45 秒覆盖整条链路。

        /// <summary>
        /// 事件触发：RDP 重连、新登录、投送前后调一次。
        /// 会把后续 45 秒切成快档，覆盖"登录后状态还在变"的窗口期。
        ///
        /// ★★ 2026-11-22 性能修复：本方法**绝不阻塞调用方**。★★
        ///
        ///   原来是同步调用 RefreshNow() → RefreshOnce()，而后者是一整轮重活：
        ///   查会话 + 枚举 BGI 进程（跨进程读 MainModule）+ 遍历全系统进程
        ///   + BgiLogCenter 的文件 IO。耗时几十到上百毫秒。
        ///
        ///   四个调用点全部在 **UI 线程**上（见下），于是"连上 RDP / 投送代理"
        ///   这几个用户操作最密集的时刻，界面正好冻结 —— 表现就是"偶尔卡卡的"。
        ///
        ///      · RdpConsole.ConnectAsync      （ConfigureAwait(true)）
        ///      · RdpConsole.BeginInputCore ×2 （ConfigureAwait(true)）
        ///      · DataCenterPage 的刷新按钮
        ///
        ///   实测依据：docs/research/PERF-3.3.4-20261122.md §4.1。
        ///
        ///   现在：只置"快档"标记，把真正的一轮刷新丢到线程池。
        ///   调用方立刻返回；RefreshOnce 自带 _refreshing 重入保护，
        ///   连续 Kick 多次也只会串行跑，不会堆积。
        /// </summary>
        public static void Kick(AppConfig? cfg, string reason)
        {
            try
            {
                _lastKickAt = DateTime.UtcNow;
                if (RefreshMs != StartupFastMs) ApplyInterval(StartupFastMs);

                var c = cfg ?? _timerConfig;
                if (c != null)
                {
                    // ★ 后台执行，不等待。异常在 RefreshOnce 内部已经兜住，
                    //   这里再包一层防止线程池任务把异常抛到未观察状态。
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { RefreshOnce(c); } catch { }
                    });
                }

                // ★ 诊断留痕：原来用 AppendErrorLog 塞了一个**假的** InvalidOperationException，
                //   导致 error.log 里混满「投送成功 会话19」这种正常事件的"异常"记录，
                //   真正的故障反而被淹没。改成普通诊断日志。
                App.AppendDiagLog("AccountStateCenter.Kick: " + reason);
            }
            catch { }
        }

        // ── 界面可见性 ────────────────────────────────────────────────
        //
        // ★ 窗口最小化到托盘时：
        //     数据刷新继续（BgiLogCenter.Tick 是自动化流程触发源，不能停）
        //     界面通知停止（没人看，重绘纯浪费）
        // ★ 恢复窗口时：
        //     调用 SetUiVisible(true)，内部会强制刷一轮并通知 UI ——
        //     否则界面停在最小化前的旧数据，用户不知道发生了什么。

        private static volatile bool _uiVisible = true;

        /// <summary>当前是否有可见窗口消费刷新结果。</summary>
        public static bool UiVisible() => _uiVisible;

        /// <summary>
        /// 由主窗口在显示/隐藏时调用。
        /// 由隐藏变可见时会**立刻**查一轮并通知 UI（恢复窗口不显示旧数据）。
        /// </summary>
        public static void SetUiVisible(bool visible)
        {
            bool wasHidden = !_uiVisible;
            _uiVisible = visible;
            if (!visible) return;
            if (!wasHidden) return;      // 一直可见，不用重复刷

            try
            {
                var cfg = _timerConfig ?? App.Config;
                if (cfg == null) return;

                // 恢复窗口要立刻看到真实状态：强制查一次并推给界面。
                //
                // ★ 只刷一次，**不进快档**。
                //   原来这里走的是 Kick()，它会顺带开启 KickFastWindow（45 秒）
                //   的 1 秒/轮快刷。而"恢复窗口"是浏览行为，不是状态剧烈变化 ——
                //   实测这条路径让恢复后 20-40 秒的 CPU 达到 16.9% 单核，
                //   45 秒窗口过期后立刻回落到 0.7%。
                //   快档只应由真实变化触发（RDP 连接成功 / 投送 / 代理启动）。
                RefreshNow(cfg);
            }
            catch { }
        }

        /// <summary>切换轮询周期（仅在变化时重建定时器）。</summary>
        private static void ApplyInterval(int newMs)
        {
            try
            {
                lock (_sync)
                {
                    if (newMs == RefreshMs) return;
                    var cfg = _timerConfig;
                    _timer?.Dispose();
                    RefreshMs = newMs;
                    _timer = new System.Threading.Timer(_ => { var c = _timerConfig; if (c != null) RefreshOnce(c); },
                        null, dueTime: newMs, period: newMs);
                }
            }
            catch { }
        }

        /// <summary>根据当前所处阶段决定兜底周期。</summary>
        private static int DesiredIdleInterval()
        {
            var now = DateTime.UtcNow;
            if (_startedAt != DateTime.MinValue && (now - _startedAt) < StartupFastWindow)
                return StartupFastMs;
            if (_lastKickAt != DateTime.MinValue && (now - _lastKickAt) < KickFastWindow)
                return StartupFastMs;
            return IdleRefreshMs;
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

                // ★ 按阶段决定兜底周期：启动/事件触发后走快档，其余时间走慢档。
                try
                {
                    if (_startedAt == DateTime.MinValue) _startedAt = DateTime.UtcNow;
                    int want = DesiredIdleInterval();
                    if (want != RefreshMs) ApplyInterval(want);
                }
                catch { }

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
                // ★ 按需刷新：只有启用中的流程真的用到「进程检查」时才枚举全进程。
                //   否则每秒遍历 550 个进程纯属浪费，且会持续产生句柄。
                if (ProcessOwnerSnapshot.IsNeeded(cfg)) ProcessOwnerSnapshot.Refresh();
                __tProc = __sw.ElapsedMilliseconds - __s3;

                // ★ BGI 日志：唯一读取点。只在**已绑定**的账号上做 IO，
                //   单遍流式消费到字节游标，不保留原文。
                var __s4 = __sw.ElapsedMilliseconds;
                try { BgiLogCenter.Tick(AccountScope.Accounts(cfg)); }
                catch (Exception ex) { App.AppendErrorLog("BgiLogCenter.Tick", ex); }
                __tBgi = __sw.ElapsedMilliseconds - __s4;
                bool anyChanged = false;

                List<AccountState> snapshot;
                lock (_sync) snapshot = _states.Values.ToList();

                foreach (var st in snapshot)
                {
                    try { RefreshAccount(st, sessions, bgi, cfg, ref anyChanged); }
                    catch { }
                }

                lock (_sync) _lastRefresh = DateTime.Now;

                // ★ 没有可见窗口时不要通知 UI。
                //   数据刷新必须继续（BgiLogCenter.Tick 是自动化流程的触发源，
                //   窗口最小化到托盘时停了它，挂机就停了），
                //   但界面重绘没人看，纯属浪费 CPU。
                //   恢复窗口时会强制刷一轮（见 KickOnShow），不会出现空白。
                if (anyChanged && UiVisible())
                {
                    try { Changed?.Invoke(); } catch { }
                }

                // ★ 临时诊断：每轮各阶段耗时（只在开启全量日志时输出）。
                if (SoulLink.Controls.RdpConsole.VerboseLog)
                {
                    InputTrace.Write("REFRESH_TIME", "本轮总耗时=" + __sw.ElapsedMilliseconds + "ms"
                        + " query=" + __tQuery + "ms 进程快照=" + __tProc + "ms BGI日志=" + __tBgi + "ms"
                        + " 刷新计数=" + ProcessOwnerSnapshot.RefreshCount);
                }
            }
            catch (Exception ex) { App.AppendErrorLog("AccountStateCenter.Refresh", ex); }
            finally { Interlocked.Exchange(ref _refreshing, 0); }
        }

        /// <summary>
        /// 对外提供的全量读取（仅供无法走快照的兜底路径使用）。
        /// ★ 主流程不再调用它 —— 所有日志数据走 BgiLogCenter 的单遍流式快照。
        /// </summary>
        public static string ReadLogFull(string path) => FallbackReadHead(path);

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
                st.LogTail = Array.Empty<BgiLogLine>();
                st.LogTailSeq = 0;
                st.LogTailFile = "";
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
            //
            // ★ 日界：狗粮**保持 04:00**（用户确认）。
            //   它读的是 AAA 脚本写的独立 records 文件，不随 BGI 日志按天滚动，
            //   所以不能跟着日志改成 00:00，否则 00:00–04:00 这四小时会取不到记录。
            try
            {
                if (string.IsNullOrWhiteSpace(st.BgiPath))
                {
                    st.DogfoodRecords = new List<DogfoodRecord>();
                    st.DogfoodRoute = DogfoodEndingRoute.Unknown;
                    st.DogfoodValid = false; st.DogfoodReason = "未配置 BGI 路径";
                    st.DogfoodExp = 0; st.DogfoodMora = 0; st.DogfoodExpReachedLimit = false;
                }
                else
                {
                    string recordFile = DogfoodRecordReader.ResolveRecordPath(st.BgiPath, DogfoodRecordReader.ResolveAccountName(st.BgiPath));

                    // ★ 2026-11-26：给 records 文件挂 FileSystemWatcher。
                    //   原来只能靠 30 秒慢档轮询发现变化 → 脚本刚写完要等最多 30 秒。
                    //   watcher 只置脏标记（零 IO），这里消费标记决定是否重读。
                    DogfoodWatcher.Ensure(st.UserName, recordFile);

                    var info = new FileInfo(recordFile);
                    string stamp = info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks : "missing";
                    // ★ 记录文件真实写入时刻（数据新鲜度），供界面区分「读取时刻」与「数据时刻」。
                    st.DogfoodFileWriteAt = info.Exists ? info.LastWriteTime : DateTime.MinValue;

                    // ★ 两条触发重读的条件，任一成立即重新解析：
                    //   ① 文件戳变了（大小/修改时间）；② watcher 报过变化（防御 stat 精度/缓存）。
                    bool fileChanged = stamp != st.DogfoodRecordStamp;
                    bool watched = DogfoodWatcher.ConsumeDirty(st.UserName);
                    if (fileChanged || watched)
                    {
                        st.DogfoodRecords = DogfoodRecordReader.ReadAllForBgi(st.BgiPath, out recordFile);
                        st.DogfoodRecordStamp = stamp;
                    }
                    st.DogfoodRecordPath = recordFile;
                    // 04:00 业务日取值（与改造前口径一致）。
                    var rec = DogfoodRecordReader.ForDay(
                        st.DogfoodRecords.FirstOrDefault(r => DogfoodRecordReader.ForDay(r, DateTime.Now, 4).IsValid)
                            ?? st.DogfoodRecords.FirstOrDefault() ?? new DogfoodRecord(),
                        DateTime.Now, 4);
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

            // ── 日志内容：**统一从 BgiLogCenter 的不可变快照读** ─────────
            //
            // ★ 2.17.45 彻底重写。旧实现在这里做了四件极贵的事：
            //     ① 每次文件变动 BusinessDayData.Version++ 让整份投影失效；
            //     ② 把「今天 53 MB + 昨天 61 MB」拼成 88 MB 字符串；
            //     ③ Split 成 150 万行逐行正则 + DateTime.TryParse；
            //     ④ Tail(50 MB) 再拆一遍。
            //   实测 A1 满速写日志时：88% 单核、1.1 GB 工作集、每秒 1.1 GB 垃圾。
            //   现在日志只在 BgiLogCenter 里被**单遍流式**消费一次（130ms / 1MB 分配），
            //   这里只是一次无锁的结构拷贝。
            var snap = BgiLogCenter.Snapshot(st.AccountId);
            if (snap != null)
            {
                bool changed = snap.CurrentGroup != st.CurrentGroup
                            || snap.LastGroup != st.LastGroup
                            || snap.StoppedAt != st.StoppedAt
                            || snap.OneDragon != st.OneDragon
                            || snap.LogFile != st.BgiLogFile
                            || snap.ErrorCount != st.ErrorCount
                            || snap.WarnCount != st.WarnCount
                            || snap.FailCount != st.FailCount
                            || snap.Error != st.GroupsReadError;

                st.Bound = snap.Bound;
                st.CurrentGroup = snap.CurrentGroup;
                st.CurrentGroupAt = snap.CurrentGroupAt;
                st.LastGroup = snap.LastGroup;
                st.StoppedAt = snap.StoppedAt;
                st.OneDragon = snap.OneDragon;
                st.ErrorCount = snap.ErrorCount;
                st.WarnCount = snap.WarnCount;
                st.FailCount = snap.FailCount;
                st.GroupsReadError = snap.Error ?? "";

                // 配置组运行记录 / 今日统计 / 尾窗，全部换成快照里的只读视图。
                st.Groups = snap.Groups;
                st.Today = snap.Today;
                st.RecentLog = BuildTailText(snap);
                st.RecentLogAt = snap.UpdatedAt;
                st.LogTail = snap.Tail;
                st.LogTailSeq = snap.LastSeq;
                st.LogTailFile = snap.LogFile ?? "";
                st.GroupsUpdatedAt = snap.UpdatedAt;
                st.GroupsSource = snap.LogFile ?? "";
                st.LogBytes = snap.FileLength;
                st.CenterChars = snap.ResidentChars;

                if (!string.IsNullOrEmpty(snap.LogFile)) st.BgiLogFile = snap.LogFile;
                if (snap.LastWrite != DateTime.MinValue) st.BgiLogWrite = snap.LastWrite;

                if (changed) { anyChanged = true; try { AccountChanged?.Invoke(st); } catch { } }
            }

            st.UpdatedAt = DateTime.Now;
        }
    }
}