using System;
using SoulLink.Controls;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Threading;
using System.Threading.Tasks;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Views
{
    /// <summary>
    /// 数据中心：把账号状态中心的每账号快照直接可视化。
    ///
    /// ★ 用途是**核对**，不是操作：
    ///   自动化跑之前先来这里看一眼，确认"哪个账号 → 哪条会话 → 哪个 BGI → 当前任务"
    ///   都对得上，避免因为状态错位导致操作串到别的账号。
    /// ★ 只读：这个页面不修改任何数据。
    /// </summary>
    public partial class DataCenterPage : UserControl
    {
        private IDisposable? _refreshTick;
        private string? _focusedAccountId;
        private string? _shownFocusUser;
        private readonly Dictionary<string, BgiAccountData> _bgiSnapshots = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _bgiSnapshotGate = new(1, 1);
        private DateTime _lastBgiSnapshotAt;

        /// <summary>上一轮渲染依据的数据签名；相同就不重建视觉树。</summary>
        private string _lastSignature = "";
        /// <summary>t64：上次整页重建的时刻 —— 用于强制冷却，防止签名高频变化导致每秒重建。</summary>
        private DateTime _lastRebuildAt = DateTime.MinValue;
        /// <summary>t64：两次整页重建之间的最小间隔（毫秒）。3 秒是实测安全的：状态显示延迟可接受，DWM 压力可忽略。</summary>
        private const int RebuildMinIntervalMs = 3000;

        // ══ t2 问题1：米游社摘要「每秒闪一次」═══════════════════════════════
        //
        // 根因（实测确认）：
        //   BuildMiyousheRow 每次都先把 TextBlock 建成「…」，
        //   然后 _ = RefreshMiyousheAsync(status, ...) 异步回来再改 Text；
        //   而页面每秒（或每次数据变化）都要 Rebuild() → Children.Clear() → 重建，
        //   于是每重建一次就重演一遍「… → 已存储 · 12***34」，
        //   用户看到的就是「一会儿省略号、一会儿 uid，一闪一闪」。
        //
        // 修法（三层一起）：
        //   ① 缓存结果：重建时直接用上次的结果渲染，不再先画占位；
        //   ② 只在缓存为空时才发起异步读取（原来每重建一次就多一个在途读取）；
        //   ③ 异步回来后值没变就不碰 UI；值变了也只改那一个 TextBlock，
        //      不整页 Rebuild —— 整页重建本身就是一次可见的重绘。
        /// <summary>账号 → 上次读到的米游社摘要（重建时直接按它渲染）。</summary>
        private readonly Dictionary<string, MiyousheView> _miyoushe
            = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>账号 → 在途读取标记（同一账号同时只允许一次读取）。</summary>
        private readonly HashSet<string> _miyousheLoading = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>t59：每账号的原神角色列表缓存（账号 → 角色 + 默认角色）。</summary>
        private readonly Dictionary<string, (List<MiyousheRole> Roles, string DefaultRole, string Reason)> _miyousheRoles
            = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _miyousheRolesLoading = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ComboBox> _miyousheRoleBoxes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<ComboBox> _fillingMiyousheRoles = new();
        /// <summary>账号 → 当前活在视觉树里的状态文本块（异步回来只改它自己）。</summary>
        private readonly Dictionary<string, TextBlock> _miyousheBlocks
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>米游社摘要的渲染快照：文案 + 是否已存储（决定颜色）+ 是否已拿到真值。</summary>
        private sealed class MiyousheView
        {
            public string Text = "…";
            public bool Stored;
            /// <summary>false = 还只是占位，没读到过真值。</summary>
            public bool Ready;
        }

        // ══ t2 问题2：配置组卡片按组名合并 ═══════════════════════════════════
        /// <summary>已展开历史的配置组 key（重建后保持展开）。</summary>
        private readonly HashSet<string> _expandedGroups = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>今日某个配置组的一次运行（SQLite 与实时增量统一成这个形状）。</summary>
        private sealed class GroupRunView
        {
            public string Name = "";
            public ScriptGroupState State;
            public DateTime StartedAt = DateTime.MinValue;
            public DateTime EndedAt = DateTime.MinValue;
            public int ScriptCount;
            public int Picks;
            public string Detail = "";
            // ── 轮次（2026-11-26）──────────────────────────────────────────
            //   锄地类：round_total>0，显示「N 轮」；好感/CD采集等无轮次概念，显示「N 次启动」。
            public int RoundIndex;
            public int RoundTotal;
            public int RoundCount;
            public string RoundHost = "";
            /// <summary>该组是否有轮次概念（锄地多世界）。</summary>
            public bool HasRounds => RoundTotal > 0 || RoundCount > 0;
            public TimeSpan? Duration
                => EndedAt > StartedAt && StartedAt != DateTime.MinValue
                   ? EndedAt - StartedAt : (TimeSpan?)null;
        }

        /// <summary>同名配置组今日的全部运行（Runs[0] = 最后一次）。</summary>
        private sealed class GroupAggregate
        {
            public string Name = "";
            public List<GroupRunView> Runs = new();
            public GroupRunView Last => Runs[0];
        }

        // ── t52：主程序数据中心改用 SQLite 作为「今日配置组」权威数据源 ──
        //   首帧显示 run.db 的今日记录（历史+当前），Tracker 只负责实时增量，
        //   异步加载、幂等合并（同组名+同时刻去重），UI 线程零 IO。
        private readonly Dictionary<string, IReadOnlyList<SoulLink.Services.RunRecord>> _sqliteTodayRuns
            = new(StringComparer.OrdinalIgnoreCase);
        private bool _sqliteLoading;
        private bool _sqliteLoaded;   // t62：是否已成功加载过一次今日 run
        /// <summary>t71：上次查库的时刻 —— 节流用，避免每次 Rebuild 都打 DB。</summary>
        private DateTime _lastSqliteQuery = DateTime.MinValue;
        /// <summary>t71：查库最小间隔。3 秒足够跟上「配置组切换」这类低频变化。</summary>
        private const int SqliteQueryMinIntervalMs = 3000;


        // ── t70：轻量原地刷新时间戳（不重建整页）──────────────────────

        /// <summary>
        /// 记录每个账号「日志文件」「数据中心占用」两行的 TextBlock 引用，
        /// 由 RefreshLiveStamps 原地改 Text —— **不触发任何布局重建**。
        /// 
        /// ★ 为什么这样做：这两个字段每秒都在变（日志在写），
        ///   放进签名会导致每秒整页 Rebuild（就是 t64 修掉的卡死元凶），
        ///   不放又会「画面不刷新」（用户实测反馈）。
        ///   唯一正确的解：**只改这两个 Text 的字符串**，视觉树结构完全不动。
        ///   WPF 对一个 TextBlock 改 Text 只重排那一个元素，开销可忽略。
        /// </summary>
        private readonly Dictionary<string, (TextBlock LogFile, TextBlock Center)> _liveStamps
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>原地刷新所有账号的时间戳文本（每次 Tick 调，开销 ~微秒级）。</summary>
        private void RefreshLiveStamps(IEnumerable<Account> accounts)
        {
            if (_liveStamps.Count == 0) return;
            try
            {
                foreach (var acc in accounts)
                {
                    if (string.IsNullOrWhiteSpace(acc.UserName)) continue;
                    if (!_liveStamps.TryGetValue(acc.UserName, out var refs)) continue;
                    var st = GetDisplayState(acc);
                    if (st == null) continue;

                    string logText = string.IsNullOrWhiteSpace(st.BgiLogFile) ? "—"
                        : (System.IO.Path.GetFileName(st.BgiLogFile) + "  " + st.BgiLogWrite.ToString("HH:mm:ss"));
                    if (refs.LogFile.Text != logText) refs.LogFile.Text = logText;

                    string centerText = (st.Bound ? "已绑定" : "未绑定（不读日志）")
                        + "  ·  " + FormatSize(BgiLogCenter.ResidentChars() * 2L) + "（全部账号）"
                        + "  ·  尾窗 " + CountLines(st.RecentLog) + " 行"
                        + "  ·  " + (st.RecentLogAt == DateTime.MinValue
                            ? "未采集"
                            : st.RecentLogAt.ToString("HH:mm:ss") + " 更新");
                    if (refs.Center.Text != centerText) refs.Center.Text = centerText;
                }
            }
            catch { }
        }

        /// <summary>
        /// 把"界面会显示的东西"压成一个字符串。
        /// ★ 只要它没变，重建出来的视觉树和现在完全一样，纯属白烧 CPU。
        /// </summary>
        private string BuildSignature(IEnumerable<Account> accounts)
        {
            var sb = new System.Text.StringBuilder(512);
            try
            {
                foreach (var a in accounts)
                {
                    sb.Append(a.Id).Append('|').Append(a.UserName).Append('|').Append(a.Caption).Append('|');
                    var st = AccountStateCenter.Get(a.Id);
                    if (st == null) { sb.Append("null;"); continue; }
                    sb.Append(st.SessionId).Append(',')
                      .Append(st.SessionState).Append(',')
                      .Append(st.BgiRunning).Append(',')
                      .Append(st.BgiPid).Append(',')
                      .Append(st.Bound).Append(',')
                      .Append(st.CurrentGroup).Append(',')
                      .Append(st.OneDragon).Append(',')
                      .Append(st.Groups.Count).Append(',')
                      // ★★ t64 根治：**移除 ErrorCount/WarnCount/FailCount**。
                      //   它们是高频变化的计数器 —— A1 的 37MB 日志里报错很多，
                      //   这三个数每秒都在涨 → 签名每秒都变 → 每秒整页 Rebuild。
                      //   而整页 Rebuild（Children.Clear + 重建全部卡片）会产生海量
                      //   视觉树节点重排 → DWM 合成队列积压 → 整个桌面黑屏。
                      //
                      //   代价：这三个数字在界面上的更新会延迟到「其它字段变化时」才刷新。
                      //   这是**正确的取舍** —— 报错计数不值得用整页重建来换实时性。
                      .Append(st.DogfoodRoute).Append(',')
                      .Append(st.DogfoodValid).Append(',')
                      .Append(st.BgiPath).Append(';');
                    // ★★ t70：**时间戳不放进签名**（会每秒变 → 每秒重建 → 回到卡死老路）。
                    //   改成 Rebuild 之后「原地刷新时间戳文本」，见 RefreshLiveStamps()。
                    // ★ 绝对不要把 st.UpdatedAt 放进签名：它在每轮刷新里都会被
                    //   赋成 DateTime.Now，等于签名每秒都变，去重完全失效
                    //   （实测：加了它 CPU 从 13% 反弹到 52%）。

                    // ★★ t61：角色数据也要进签名 —— 否则角色列表读回来后签名没变，
                    //   界面不会刷新，下拉框永远停在「读取中…」。
                    if (_miyousheRoles.TryGetValue(a.UserName, out var rv))
                        sb.Append("roles=").Append(rv.Roles.Count).Append('/').Append(rv.DefaultRole).Append(';');
                }

                // ★★ t64 根治（系统卡死/黑屏的元凶）：展开态**必须排序后**拼进签名。
                //   HashSet<string> 的枚举顺序在 .NET 里**不保证稳定**（尤其是插入/删除混合操作后），
                //   每个 tick 拼出来的字符串都可能不同 → 签名永远「变了」→ **每秒整页 Rebuild**。
                //   而整页 Rebuild = CardList.Children.Clear() + 重建 4 个账号的全部卡片
                //   （每卡片 11 行 AddRow + 2 个表格 + 米游社行 + 配置组网格），
                //   相当于每秒丢弃并重建上百个视觉树节点 → WPF 全量 Measure/Arrange/Render
                //   → DWM 合成队列被灌满 → **整个桌面黑屏**（DWM 是所有窗口的合成器）。
                if (_expandedGroups.Count > 0)
                {
                    var keys = _expandedGroups.ToArray();
                    Array.Sort(keys, StringComparer.Ordinal);   // 稳定顺序才算得出稳定签名
                    foreach (string k in keys) sb.Append("exp=").Append(k).Append(';');
                }
            }
            catch { }
            return sb.ToString();
        }

        public DataCenterPage()
        {
            InitializeComponent();
            Unloaded += (_, _) => OnPageDeactivated();
        }

        private void RefreshVisibleData()
        {
            if (!IsVisible) return;
            _ = RefreshBgiSnapshotsAsync();

            var accounts = AccountScope.Accounts(App.Config);
            var focused = FindFocusedAccount(accounts);
            // 删除或管理范围变化时优先移除旧卡片，常规状态更新仍使用重建冷却时间。
            if (_focusedAccountId != null && focused?.UserName != _shownFocusUser)
            {
                Rebuild();
                return;
            }

            // 时间戳原地更新，限制整页重建频率以避免持续触发 DWM 合成。
            IEnumerable<Account> displayAccounts = _focusedAccountId == null ? accounts
                : focused == null ? Array.Empty<Account>() : new[] { focused };
            RefreshLiveStamps(displayAccounts);
            string signature = BuildSignature(displayAccounts);
            if (signature == _lastSignature) return;
            var now = DateTime.UtcNow;
            if ((now - _lastRebuildAt).TotalMilliseconds < RebuildMinIntervalMs) return;
            _lastSignature = signature;
            _lastRebuildAt = now;
            Rebuild();
        }

        private Account? FindFocusedAccount(IEnumerable<Account> accounts)
            => _focusedAccountId == null ? null : accounts.FirstOrDefault(account =>
                account.Id == _focusedAccountId && !string.IsNullOrWhiteSpace(account.UserName));

        private AccountState? GetDisplayState(Account account)
        {
            var state = AccountStateCenter.Get(account.Id);
            if (_focusedAccountId == null) return state ?? AccountStateCenter.GetByUser(account.UserName);
            return state != null && string.Equals(state.UserName, account.UserName, StringComparison.OrdinalIgnoreCase) ? state : null;
        }

        private static AccountState EmptyState(Account account)
            => new() { AccountId = account.Id, UserName = account.UserName, Caption = account.Caption };

        internal void OnPageActivated(string? accountId)
        {
            if (_focusedAccountId != accountId) AccountCardsScroll.ScrollToTop();
            _focusedAccountId = accountId;
            _refreshTick ??= TickHub.Register("DataCenter", 1, RefreshVisibleData);
            Activate();
        }

        internal void OnPageDeactivated()
        {
            _refreshTick?.Dispose();
            _refreshTick = null;
        }

        private void ReturnToOverview_Click(object sender, RoutedEventArgs e)
            => (Window.GetWindow(this) as MainWindow)?.NavigateTo("overview");

        private void ShowAllAccounts_Click(object sender, RoutedEventArgs e)
            => (Window.GetWindow(this) as MainWindow)?.NavigateTo("datacenter");

        private bool _loadingMoraInterval;

        public void Activate()
        {
            _loadingMoraInterval = true;
            try
            {
                int minutes = App.Config?.Settings?.MiyousheLedgerIntervalMinutes ?? 60;
                MoraIntervalBox.SelectedIndex = MiyousheHost.LedgerIntervalHours(minutes) - 1;
            }
            finally { _loadingMoraInterval = false; }

            _ = RefreshBgiSnapshotsAsync();
            _lastSignature = "";
            Rebuild();
        }

        private void MoraInterval_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingMoraInterval || MoraIntervalBox.SelectedIndex < 0 || App.Config?.Settings == null) return;

            int minutes = (MoraIntervalBox.SelectedIndex + 1) * 60;
            if (App.Config.Settings.MiyousheLedgerIntervalMinutes == minutes) return;
            App.Config.Settings.MiyousheLedgerIntervalMinutes = minutes;
            ConfigStore.Save(App.Config);
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            BtnRefresh.IsEnabled = false;
            try
            {
                await RefreshBgiSnapshotsAsync(force: true);
                _miyoushe.Clear();
                _miyousheRoles.Clear();
                _lastSignature = "";
            }
            finally { BtnRefresh.IsEnabled = true; }
        }

        // ── 渲染 ─────────────────────────────────────────────────────

        private void Rebuild()
        {
            try
            {
                // 账号是否显示由配置和管理范围决定。离线账号也保留卡片。
                var accounts = AccountScope.Accounts(App.Config);
                var allStates = accounts
                    .Where(a => !string.IsNullOrWhiteSpace(a.UserName))
                    .GroupBy(a => a.UserName, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .Select(a => AccountStateCenter.Get(a.Id) ?? AccountStateCenter.GetByUser(a.UserName)
                        ?? EmptyState(a))
                    .OrderBy(s => s.Caption, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var focused = FindFocusedAccount(accounts);
                _shownFocusUser = focused?.UserName;
                AccountScopeBar.Visibility = _focusedAccountId == null ? Visibility.Collapsed : Visibility.Visible;
                AccountScopeText.Text = _focusedAccountId == null ? "" : focused == null ? "当前账号不可查看"
                    : $"当前账号：{focused.Caption}";
                AccountScopeText.ToolTip = focused?.UserName;

                var states = _focusedAccountId == null ? allStates
                    : focused == null ? Array.Empty<AccountState>() : new[] { GetDisplayState(focused) ?? EmptyState(focused) };

                var refresh = AccountStateCenter.LastRefresh;
                double age = (DateTime.Now - refresh).TotalSeconds;
                SubTitle.Text = states.Length + " 个账号 · 快照"
                    + (refresh == DateTime.MinValue ? "尚未生成" : age.ToString("0.0") + " 秒前更新")
                    + " · 每 " + (AccountStateCenter.RefreshMs / 1000.0).ToString("0.#") + " 秒刷新";

                FootNote.Text = "这里汇总每个账号的运行状态、BGI 路径、配置目录和快捷键。流程编辑器、启动台、更新页使用同一份账号数据。\n"
                    + "更换账号或路径会重新读取对应目录；BGI 的配置文件变更也会自动刷新。";

                // ★ t2：清掉「在树上的米游社状态块」索引 —— 旧块即将被丢弃，
                //   留着只会让异步结果写到一个已脱离视觉树的 TextBlock（既无效又占内存）。
                //   缓存 _miyoushe 本身**不清**：它就是用来跨重建保持值稳定的。
                _miyousheBlocks.Clear();
                _miyousheRoleBoxes.Clear();
                _liveStamps.Clear();   // t70：旧 TextBlock 即将丢弃，引用不能留
                CardList.Children.Clear();

                if (states.Length == 0)
                {
                    CardList.Children.Add(new TextBlock
                    {
                        Text = _focusedAccountId == null
                            ? "还没有账号状态。确认配置里有账号，且状态中心已启动。"
                            : "所选账号已移除、超出当前管理范围，或未配置 Windows 用户名。",
                        Margin = new Thickness(4, 10, 4, 10),
                        FontSize = 11.5,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = UiBrush.Get("TextMuted")
                    });
                }

                // ★ t52：异步预载 run.db 今日记录（不阻塞首帧）。
                // ★ t71：加 3 秒节流 —— 原来每次 Rebuild 都查一遍 DB，
                //   而签名变化时 Rebuild 可能连续触发（配置组切换、状态变化…），
                //   查询过于频繁。3 秒足够跟上「配置组切换」这种低频变化。
                if (allStates.Length > 0 && (DateTime.UtcNow - _lastSqliteQuery).TotalMilliseconds >= SqliteQueryMinIntervalMs)
                {
                    _lastSqliteQuery = DateTime.UtcNow;
                    _ = EnsureSqliteTodayRunsAsync(allStates);
                }

                foreach (var st in states) CardList.Children.Add(BuildCard(st));
            }
            catch (Exception ex)
            {
                SubTitle.Text = "渲染失败：" + ex.Message;
            }
        }

        /// <summary>是否主用户（当前进程所在会话、或 Windows console 会话）。</summary>
        private static bool IsMainUser(AccountState st, int selfSessionId)
        {
            try
            {
                if (selfSessionId > 0 && st.HasSession && st.SessionId == selfSessionId) return true;

                // 兜底：与当前登录用户同名，且没有独立会话
                var cur = SessionService.CurrentUser;
                if (!string.IsNullOrWhiteSpace(cur)
                    && string.Equals(SessionService.BareName(st.UserName),
                                     SessionService.BareName(cur), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
            return false;
        }

        private async Task RefreshBgiSnapshotsAsync(bool force = false)
        {
            if (force)
                await _bgiSnapshotGate.WaitAsync();
            else if ((DateTime.UtcNow - _lastBgiSnapshotAt).TotalSeconds < 5 || !_bgiSnapshotGate.Wait(0))
                return;
            _lastBgiSnapshotAt = DateTime.UtcNow;
            try
            {
                var config = App.Config;
                string[] users = AccountScope.Accounts(config).Select(a => a.UserName)
                    .Where(user => !string.IsNullOrWhiteSpace(user)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var snapshots = await Task.Run(() =>
                {
                    if (force) AccountStateCenter.RefreshNow(config);
                    return users.Select(user => force ? BgiDataCenter.Refresh(user) : BgiDataCenter.Get(user)).ToArray();
                });
                bool changed = _bgiSnapshots.Count != snapshots.Length;
                foreach (var snapshot in snapshots)
                {
                    if (!_bgiSnapshots.TryGetValue(snapshot.UserName, out var previous)
                        || !string.Equals(SnapshotSignature(previous), SnapshotSignature(snapshot), StringComparison.Ordinal))
                        changed = true;
                }

                _bgiSnapshots.Clear();
                foreach (var snapshot in snapshots) _bgiSnapshots[snapshot.UserName] = snapshot;
                if (changed) _lastSignature = "";
            }
            catch (Exception ex) { App.AppendErrorLog("DataCenter.BgiSnapshots", ex); }
            finally { _bgiSnapshotGate.Release(); }
        }

        private static string SnapshotSignature(BgiAccountData data)
            => data.ExePath + "|" + data.CancelTaskHotkey + "|" + data.Error
                + "|" + string.Join(",", data.OneDragonConfigs) + "|" + string.Join(",", data.ScriptGroups)
                + "|" + string.Join(",", data.DisplayHotkeys.Select(item => item.Name + "=" + item.Key));

        private UIElement BuildCard(AccountState st)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderBrush = UiBrush.Get("TextPrimary", 0x20),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 8)
            };
            card.SetResourceReference(Border.BackgroundProperty, "DesktopPanel");

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ── 标题行：昵称 + 用户名 + 状态灯 ──
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new Ellipse
            {
                Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Fill = st.BgiRunning ? UiBrush.Get("MintBrush")
                                     : UiBrush.Get("TextSecondary")
            });
            head.Children.Add(new TextBlock
            {
                Text = st.Caption,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiBrush.Get("TextPrimary")
            });
            if (!string.Equals(st.Caption, st.UserName, StringComparison.OrdinalIgnoreCase))
            {
                head.Children.Add(new TextBlock
                {
                    Text = "（" + st.UserName + "）",
                    FontSize = 11,
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = UiBrush.Get("TextSecondary")
                });
            }
            Grid.SetRow(head,0);
            root.Children.Add(head);

            var columns = new Grid { Margin = new Thickness(0,10,0,0) };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360), MinWidth = 280 });
            Grid.SetRow(columns,1);

            // ── 左侧基本信息 ──
            var grid = new Grid { Margin = new Thickness(0) };
            Grid.SetColumn(grid,0);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 11; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            int row = 0;
            AddRow(grid, row++, "会话", st.HasSession
                ? ("#" + st.SessionId + "  " + st.SessionState)
                : "无会话（未登录）");
            AddRow(grid, row++, "BGI", st.BgiRunning
                ? ("运行中  PID " + st.BgiPid)
                : "未运行");
            var shared = _bgiSnapshots.TryGetValue(st.UserName, out var snapshot)
                ? snapshot : new BgiAccountData { UserName = st.UserName, ExePath = st.BgiPath };
            AddRow(grid, row++, "BGI 路径", shared.ExePath.Length == 0 ? "未设置" : shared.ExePath, mono: true);
            AddRow(grid, row++, "配置目录", shared.RootPath.Length == 0 ? "—" : shared.DirectoryOf("User"), mono: true);
            // ★ t61：恢复完整名单（我 t60 擅自改成只显示数量，用户要求还原）。
            AddRow(grid, row++, "配置组", shared.ScriptGroups.Count + " 个：" + (shared.ScriptGroups.Count == 0 ? "未发现" : string.Join("、", shared.ScriptGroups)));
            AddRow(grid, row++, "一条龙", shared.OneDragonConfigs.Count + " 个：" + (shared.OneDragonConfigs.Count == 0 ? "未发现" : string.Join("、", shared.OneDragonConfigs)));
            AddRow(grid, row++, "停止快捷键", shared.HotkeyText);
            AddRow(grid, row++, "配置读取", shared.Error.Length > 0 ? shared.Error : (shared.ConfigExists ? "正常" : "尚未读取") + " · " + shared.ReadAt.ToString("HH:mm:ss"));
            int rowLogFile = row;   // 记住行号，稍后取回 TextBlock 引用（t70 原地刷新用）
            AddRow(grid, row++, "日志文件", string.IsNullOrWhiteSpace(st.BgiLogFile) ? "—"
                : (System.IO.Path.GetFileName(st.BgiLogFile) + "  " + st.BgiLogWrite.ToString("HH:mm:ss")), mono: true);
            AddRow(grid, row++, "任务原文", ExtractTaskLine(st), mono: true);

            // ── 数据中心占用（回归哨兵：改造后应稳定在百 KB 级）──
            //   ★ 2.17.45 起不再保留日志原文，这里改成数据中心常驻量；
            //     如果这个数字开始涨到 MB 级，说明尾窗预算失守。
            //   ★ t61：用户要求恢复 —— 我 t60 时未经确认就删了它，是我越权了。
            long centerChars = BgiLogCenter.ResidentChars();
            long centerBytes = centerChars * 2L;
            int rowCenter = row;
            AddRow(grid, row++, "数据中心占用",
                (st.Bound ? "已绑定" : "未绑定（不读日志）")
                + "  ·  " + FormatSize(centerBytes) + "（全部账号）"
                + "  ·  尾窗 " + CountLines(st.RecentLog) + " 行"
                + "  ·  " + (st.RecentLogAt == DateTime.MinValue
                    ? "未采集"
                    : st.RecentLogAt.ToString("HH:mm:ss") + " 更新"));

            // ★ t70：取回这两行的值 TextBlock 引用，供 RefreshLiveStamps 原地改 Text
            try
            {
                TextBlock? tbLog = FindCell(grid, rowLogFile);
                TextBlock? tbCen = FindCell(grid, rowCenter);
                if (tbLog != null && tbCen != null)
                    _liveStamps[st.UserName] = (tbLog, tbCen);
            }
            catch { }

            columns.Children.Add(grid);
            var hotkeys = BuildHotkeyTable(shared);
            Grid.SetColumn(hotkeys,1);
            columns.Children.Add(hotkeys);
            root.Children.Add(columns);
            var automation = BuildAutomationData(st);
            Grid.SetRow(automation,2);
            root.Children.Add(automation);
            var miyoushe = BuildMiyousheRow(st);
            Grid.SetRow(miyoushe,3);
            root.Children.Add(miyoushe);
            card.Child = root;
            return card;
        }

        /// <summary>
        /// 米游社摘要行（t33 需求1 + t2 修复闪烁）。只放「一行放得下」的摘要 + 操作入口，
        /// 详情全部在网页。
        ///
        /// ★ t2 关键：**先按缓存渲染**，只有缓存为空时才异步读取。
        ///   旧实现每次都先画「…」再异步改，配合每秒整页重建 = 每秒闪一次。
        /// </summary>
        private UIElement BuildMiyousheRow(AccountState st)
        {
            var border = new Border
            {
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(0, 8, 0, 0),
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = UiBrush.Get("TextPrimary", 0x20)
            };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            panel.Children.Add(new TextBlock
            {
                Text = "米游社",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Foreground = UiBrush.Get("TextPrimary")
            });

            // ★ t2：有缓存就按缓存值渲染（不先画占位，因此没有「…→uid」的跳变）
            bool cached = _miyoushe.TryGetValue(st.UserName, out var view) && view != null;
            var status = new TextBlock
            {
                Text = cached ? view!.Text : "…",
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = cached && view!.Stored
                    ? UiBrush.Get("MintBrush")
                    : UiBrush.Get("TextSecondary")
            };
            panel.Children.Add(status);
            // 记下「当前活在视觉树里」的那个块：异步回来只改它，不改整页。
            _miyousheBlocks[st.UserName] = status;

            // 操作按钮（扫码登录 / 一键导入 / 删除凭据 / 查看详情→）
            var btnQr = new Button
            {
                Content = "扫码登录",
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(10, 0, 6, 0),
                FontSize = 10.5
            };
            btnQr.Click += async (_, __) => await OpenQrAsync(st.UserName);
            panel.Children.Add(btnQr);

            var btnImport = new Button
            {
                Content = "一键导入",
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                FontSize = 10.5
            };
            btnImport.Click += async (_, __) => await ImportAsync(st.UserName);
            panel.Children.Add(btnImport);

            var btnDel = new Button
            {
                Content = "删除凭据",
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                FontSize = 10.5
            };
            btnDel.Click += (_, __) =>
            {
                MiyousheCredentialStore.Delete(st.UserName, "cn");
                MiyousheCredentialStore.Delete(st.UserName, "os");
                // 删除后强制重读一次（缓存作废，但**不**整页重建）
                _miyoushe.Remove(st.UserName);
                _miyousheRoles.Remove(st.UserName);
                _ = RefreshMiyousheAsync(st.UserName, force: true);
            };
            panel.Children.Add(btnDel);

            // ★★ t59：**原神角色切换**（用户明确要求「绑定多个原神账号应该可以切换、设为默认」）。
            //   之前只做了网页版（#/miyoushe），主程序这里一行都没有 —— 用户在主程序里完全找不到。
            //   设计：一个 ComboBox 列出该米游社账号绑定的所有原神角色；
            //   选中即调 POST /api/miyoushe/roles 设为默认（持久化到 Settings.MiyousheDefaultRole）。
            var roleBox = new ComboBox
            {
                MinWidth = 170,
                MaxWidth = 240,
                FontSize = 10.5,
                Margin = new Thickness(10, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "选择用来拉取摩拉流水的默认原神角色",
                Visibility = Visibility.Collapsed   // 读到角色后再显示
            };
            _miyousheRoleBoxes[st.UserName] = roleBox;
            roleBox.SelectionChanged += async (_, __) =>
            {
                if (_fillingMiyousheRoles.Contains(roleBox)) return;
                if (roleBox.SelectedItem is ComboBoxItem item && item.Tag is string uid && uid.Length > 0)
                {
                    if (string.Equals(MiyousheService.GetDefaultRole(st.UserName), uid, StringComparison.OrdinalIgnoreCase))
                        return;
                    roleBox.IsEnabled = false;
                    try
                    {
                        bool saved = await MiyousheService.SetDefaultRoleAsync(st.UserName, uid, default).ConfigureAwait(true);
                        if (!saved) ThemedMessage.Error(Window.GetWindow(this), "米游社", "设置默认角色失败。");
                        await RefreshMiyousheRolesAsync(st.UserName).ConfigureAwait(true);
                    }
                    catch (Exception ex) { App.AppendErrorLog("DataCenter.DefaultRole", ex); }
                    finally { roleBox.IsEnabled = true; }
                }
            };
            panel.Children.Add(roleBox);

            border.Child = panel;

            // ★★ t61 修复（角色下拉框刷新时消失）：
            //   旧代码无条件走异步（除非已有缓存），而 Rebuild 每次重建 roleBox 都是新的、
            //   初始 Visibility=Collapsed，要等异步回来才 Visible —— 期间它就是「消失」状态。
            //   而每秒的定时器只要签名变了一次就会 Rebuild → 下拉框反复消失/出现，
            //   用户根本点不中（这也解释了他说的「无法切换」）。
            //
            //   现在的修法：
            //     ① 有缓存 → **同步**填充 + 显示（重建当拍就有内容，零等待）；
            //     ② 无缓存 → 先给一个「读取中…」占位项让它**立刻可见**，异步回来再替换；
            //     ③ 角色数据参与 BuildSignature（见下方），避免无意义的整页重建。
            if (_miyousheRoles.TryGetValue(st.UserName, out var cachedRoles))
            {
                FillRoleBox(roleBox, cachedRoles.Roles, cachedRoles.DefaultRole);   // 同步，当拍可见
            }
            else
            {
                // 立刻可见的占位（避免「先消失再出现」）
                roleBox.Items.Clear();
                roleBox.Items.Add(new ComboBoxItem { Content = "（读取角色中…）", IsEnabled = false });
                roleBox.SelectedIndex = 0;
                roleBox.Visibility = Visibility.Visible;
                _ = RefreshMiyousheRolesAsync(st.UserName);
            }
            // ★ t2：只有「还没有缓存」或「强制刷新」时才发起异步读取。
            //   原来无条件 `_ = RefreshMiyousheAsync(status, ...)`：页面每次重建都多发一次
            //   读取，回来又各自改一次 Text —— 闪烁的直接来源。
            if (!cached || !view!.Ready) _ = RefreshMiyousheAsync(st.UserName);
            return border;
        }


        /// <summary>
        /// t59：把角色列表填进 ComboBox。有多个角色才显示，只有一个角色时隐藏（没什么可切换的）。
        /// ★ 填充时先解绑事件，避免程序化选中触发「设为默认」请求。
        /// </summary>
        private void FillRoleBox(ComboBox box, List<MiyousheRole> roles, string defaultRole)
        {
            _fillingMiyousheRoles.Add(box);
            try
            {
                box.Items.Clear();
                if (roles.Count == 0)
                {
                    box.Visibility = Visibility.Collapsed;
                    return;
                }

                ComboBoxItem? pick = null;
                foreach (var r in roles)
                {
                    // ★ t61：用户明确「cn_gf01 是多余的」——只显示昵称 + 等级，
                    //   区服代码（cn_gf01/cn_qd01）是技术内码，对用户没有意义。
                    string label = r.Nickname + "  Lv." + r.Level;
                    var item = new ComboBoxItem { Content = label, Tag = r.GameUid };
                    box.Items.Add(item);
                    if (!string.IsNullOrEmpty(defaultRole)
                        && string.Equals(r.GameUid, defaultRole, StringComparison.OrdinalIgnoreCase))
                        pick = item;
                }
                if (pick == null)
                {
                    pick = new ComboBoxItem { Content = "请选择默认角色", IsEnabled = false };
                    box.Items.Insert(0, pick);
                }
                box.SelectedItem = pick;
                box.Visibility = Visibility.Visible;
            }
            finally { _fillingMiyousheRoles.Remove(box); }
        }

        /// <summary>
        /// t59：异步读角色并填充。不整页重建（沿用 t2 的不闪烁原则）。
        /// 同一账号只允许一次在途读取。
        /// </summary>
        private async Task RefreshMiyousheRolesAsync(string account)
        {
            if (string.IsNullOrWhiteSpace(account)) return;
            if (!_miyousheRolesLoading.Add(account)) return;   // 在途去重
            try
            {
                var (roles, reason) = await MiyousheService.GetRolesForUiAsync(account, default).ConfigureAwait(true);
                string defaultRole = MiyousheService.GetDefaultRole(account);
                _miyousheRoles[account] = (roles, defaultRole, reason);
                if (_miyousheRoleBoxes.TryGetValue(account, out var box)) FillRoleBox(box, roles, defaultRole);
            }
            catch { }
            finally { _miyousheRolesLoading.Remove(account); }
        }

        /// <summary>
        /// 读一次米游社登录状态并写进缓存。
        ///
        /// ★ t2：签名从 (TextBlock, account) 改成 (account, force)。
        ///   旧签名把「改哪个控件」当成参数传进来，导致异步结果只能写死到
        ///   建它时那个 TextBlock —— 页面一重建，那个块已经不在树上了，
        ///   新块又画回占位，于是每重建一次就闪一次。
        ///   现在只更新缓存，然后找**当前**在树上的块改文字；值没变就什么都不做。
        /// </summary>
        private async Task RefreshMiyousheAsync(string account, bool force = false)
        {
            if (string.IsNullOrWhiteSpace(account)) return;
            if (!force && _miyousheLoading.Contains(account)) return;   // 同一账号只允许一次在途读取
            _miyousheLoading.Add(account);
            try
            {
                var st = await MiyousheService.LoginStatusAsync(account, default).ConfigureAwait(true);
                string stateText = st.State switch
                {
                    "stored" => "已存储",
                    "needs_login" => "需重扫",
                    _ => "未登录"
                };
                string uid = st.AidMasked.Length > 0
                    ? st.AidMasked
                    : (st.Uid.Length > 0 ? MiyousheCredentialStore.Mask(st.Uid) : "");
                ApplyMiyousheView(account, stateText + (uid.Length > 0 ? " · " + uid : ""),
                    st.State == "stored", ready: true);
            }
            catch
            {
                // ★ 读取失败**不**覆盖已缓存的好值：网络抖动不该把「已存储」变成「读取失败」，
                //   那同样是一次可见的闪烁。只有从来没读到过才显示失败。
                if (!_miyoushe.TryGetValue(account, out var cachedView) || cachedView == null || !cachedView.Ready)
                    ApplyMiyousheView(account, "状态读取失败", false, ready: true);
            }
            finally { _miyousheLoading.Remove(account); }
        }

        /// <summary>
        /// 把一次读取结果落到缓存，并**只在值变化时**改当前在树上的那个 TextBlock。
        /// ★ 刻意不调用 Rebuild()：整页重建本身是一次全量重绘，正是闪烁的表现形式。
        /// </summary>
        private void ApplyMiyousheView(string account, string text, bool stored, bool ready)
        {
            if (_miyoushe.TryGetValue(account, out var prev) && prev != null
                && prev.Text == text && prev.Stored == stored && prev.Ready == ready)
                return;                                     // 值没变：连缓存都不动

            _miyoushe[account] = new MiyousheView { Text = text, Stored = stored, Ready = ready };

            TextBlock? live = null;
            try { _miyousheBlocks.TryGetValue(account, out live); } catch { }
            if (live == null) return;                       // 卡片不在树上：下次重建自然用缓存
            live.Text = text;
            try
            {
                live.Foreground = stored
                    ? UiBrush.Get("MintBrush")
                    : UiBrush.Get("TextSecondary");
            }
            catch { }
        }

        private async Task OpenQrAsync(string account)
        {
            try
            {
                var w = new MiyousheQrWindow(account, "cn") { Owner = Window.GetWindow(this) };
                w.ShowDialog();
                // 扫码后凭据可能变了：作废缓存并强制重读（一次性用户动作）
                // ★ t62：**不再主动 Rebuild** —— 与 EnsureSqliteTodayRunsAsync 的死循环教训一致：
                //   任何「异步回来又触发整页重建」的路径都可能自激。这里只作废缓存，
                //   下一次定时器刷新（1 秒、签名去重）自然会用新数据。最多晚 1 秒，绝不会卡死。
                _miyoushe.Remove(account);
                _miyousheRoles.Remove(account);
                _ = RefreshMiyousheAsync(account, force: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show("扫码窗口打开失败：" + ex.Message, "米游社", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task ImportAsync(string account)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择 BetterGI 或 ABGI 目录（只读探测）" };
                if (dlg.ShowDialog() != true) return;
                string path = dlg.FolderName;
                var probe = MiyousheImporter.Probe(account, path);
                if (!probe.Ok)
                {
                    MessageBox.Show("未识别到有效凭据：\n" + probe.Detail, "一键导入", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var confirm = MessageBox.Show("探测到 " + probe.OkCount + " 项凭据，是否导入？\n（不会显示 cookie 内容）", "一键导入", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;
                var outcome = await MiyousheImporter.ImportAsync(account, path, true, default).ConfigureAwait(true);
                MessageBox.Show("导入完成：" + outcome.OkCount + " 成功 / " + outcome.FailedCount + " 失败\n" + outcome.Detail, "一键导入", MessageBoxButton.OK, MessageBoxImage.Information);
                // 导入后凭据变了：作废缓存并强制重读（同样不主动 Rebuild，理由同上）
                _miyoushe.Remove(account);
                _miyousheRoles.Remove(account);
                _ = RefreshMiyousheAsync(account, force: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导入失败：" + ex.Message, "一键导入", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private UIElement BuildAutomationData(AccountState st)
        {
            var panel = new StackPanel { Margin = new Thickness(0,14,0,0) };
            panel.Children.Add(new TextBlock { Text="自动化使用的数据", FontSize=11.5, FontWeight=FontWeights.SemiBold, Foreground=UiBrush.Get("TextPrimary"), Margin=new Thickness(0,0,0,7) });
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(360), MinWidth=280 });
            grid.Children.Add(BuildGroupRuns(st));
            var dogfood = BuildDogfood(st); Grid.SetColumn(dogfood,1); grid.Children.Add(dogfood);
            panel.Children.Add(grid); return panel;
        }

        /// <summary>
        /// t52：异步加载各账号「今日」的 run.db 记录（run 表），不阻塞 UI。
        /// 加载完刷新一次（若页面可见）。SQLite 查询走 RunRecordStore 后台队列。
        /// </summary>
        private async System.Threading.Tasks.Task EnsureSqliteTodayRunsAsync(System.Collections.Generic.IReadOnlyList<AccountState> states)
        {
            if (_sqliteLoading) return;
            _sqliteLoading = true;
            try
            {
                bool changed = false;
                foreach (var st in states)
                {
                    if (string.IsNullOrWhiteSpace(st.UserName)) continue;
                    try
                    {
                        _sqliteTodayRuns.TryGetValue(st.UserName, out var previous);
                        var snapshot = await Task.Run(async () =>
                        {
                            var runs = await RunRecordStore.QueryRunsAsync(st.UserName, 1).ConfigureAwait(false);
                            long today0 = new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds();
                            var current = runs.Where(run => run.StartAtMs >= today0
                                || (run.EndAtMs ?? run.StartAtMs) >= today0).ToList();
                            return (Current: current, Changed: previous == null || !previous.SequenceEqual(current));
                        }).ConfigureAwait(true);
                        changed |= snapshot.Changed;
                        _sqliteTodayRuns[st.UserName] = snapshot.Current;


                    }
                    catch { _sqliteTodayRuns[st.UserName] = Array.Empty<SoulLink.Services.RunRecord>(); }
                }
                // ★★ t62 教训 + t71 补全：
                //
                //   t62 修的是「Rebuild -> 查DB -> 回来 -> Rebuild -> 查DB」的无限递归。
                //   当时把「回来后 Rebuild」整个删掉了，但**删过了头**：
                //   缓存更新了、界面却不知道，于是配置组卡片永远停在启动那一刻的数据
                //   （用户实测：卡片停在 08:32，标题却显示当前在跑 09:45 的组）。
                //
                //   正确的做法不是「不通知」，而是**只通知一次、且不递归**：
                //     · 读回数据后若内容确实变了 → 让签名失效；
                //     · 但**不直接调 Rebuild**，而是交给下一次 Tick 的签名比对去决定；
                //     · 加「同一轮内只标记一次」的标志，杜绝自激。
                _sqliteLoaded = true;
                if (changed) _lastSignature = "";   // 让下一拍 Tick 自然重建（不在这里 Rebuild）
            }
            finally { _sqliteLoading = false; }
        }

        /// <summary>t52：本地 DateTime → epoch ms（幂等合并去重用）。</summary>
        private static long ToMsLocal(DateTime dt)
            => dt == DateTime.MinValue ? 0 : new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local)).ToUnixTimeMilliseconds();

        /// <summary>
        /// t52/t2：把 SQLite 的 RunRecord 映射成页面用的运行视图。
        /// ★ t2 起多带 Picks（用户要求卡片显示「拾取」），并保留 Detail 供 tooltip。
        /// </summary>
        private static GroupRunView ToGroupRunView(SoulLink.Services.RunRecord r)
        {
            var state = r.State switch
            {
                "Running" => ScriptGroupState.Running,
                "Finished" => ScriptGroupState.Finished,
                "Failed" => ScriptGroupState.Failed,
                "Cancelled" => ScriptGroupState.Cancelled,
                "Interrupted" => ScriptGroupState.Interrupted,
                _ => ScriptGroupState.Never,
            };
            return new GroupRunView
            {
                Name = r.GroupName ?? "",
                State = state,
                StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.StartAtMs).LocalDateTime,
                EndedAt = r.EndAtMs.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(r.EndAtMs.Value).LocalDateTime
                    : DateTime.MinValue,
                ScriptCount = r.ScriptCount,
                Picks = r.Picks,
                Detail = r.Detail ?? "",
                RoundIndex = r.RoundIndex,
                RoundTotal = r.RoundTotal,
                RoundCount = r.RoundCount,
                RoundHost = r.RoundHost ?? "",
            };
        }

        private UIElement BuildGroupRuns(AccountState st)
        {
            var panel = new StackPanel();

            // ★ 未绑定 BGI 路径：不读任何文件，明确告诉用户"没绑定"而不是"没数据"。
            if (!st.Bound)
            {
                panel.Children.Add(new TextBlock { Text="配置组", FontSize=11, FontWeight=FontWeights.SemiBold, Foreground=UiBrush.Get("TextPrimary") });
                panel.Children.Add(new TextBlock
                {
                    Text = "未绑定 BGI 路径 · 不读取日志\n请到「设置 → BetterGI 路径」点一次「从进程取」",
                    FontSize=10.5, TextWrapping=TextWrapping.Wrap,
                    Foreground=UiBrush.Get("TextMuted"), Margin=new Thickness(0,5,0,0)
                });
                return panel;
            }

            // ★ t52：今日配置组改用 SQLite（run.db）为权威数据源，重启后历史不丢、不扫 36MB 日志。
            //   Tracker 只负责「启动后实时增量」，与 SQLite 历史做幂等合并（同名+同时刻去重）。
            // ★ t2：合并后再**按组名折叠**，同一组只出一张卡片（见 BuildGroupAggregates）。
            //
            // ★★ 2026-11-26 修复「同一次运行显示成两条（今日 2 次却列 3 行）」：
            //   两个数据源解析同一条日志锚点的毫秒值**不可能完全相等** ——
            //     · SQLite：走 RunRecordCollector.ResolveTs，毫秒来自 char 偏移换算；
            //     · Tracker：走 ScriptGroupTracker.EventTime，毫秒来自正则 capture。
            //   旧去重键用**精确毫秒**，两者差 1ms 就被判成两次不同运行 → 多出一行。
            //   改为**按秒对齐**（同组 + 同一秒 = 同一次运行）。日志锚点秒级精度已足够，
            //   而同一配置组不可能在同一秒内启动两次。
            var merged = new List<GroupRunView>();
            var seen = new HashSet<(string Name, long StartSec)>();
            long SecOf(DateTime dt) => dt == DateTime.MinValue ? 0 : ToMsLocal(dt) / 1000;
            if (_sqliteTodayRuns.TryGetValue(st.UserName, out var sqlRuns) && sqlRuns != null)
            {
                foreach (var r in sqlRuns)
                {
                    var v = ToGroupRunView(r);
                    if (IsBogusGroupName(v.Name)) continue;           // 误识别的英文异常消息不进卡片
                    if (seen.Add((v.Name, SecOf(v.StartedAt)))) merged.Add(v);
                }
            }
            foreach (var tr in st.Groups)
            {
                if (IsBogusGroupName(tr.Name)) continue;
                if (seen.Add((tr.Name, SecOf(tr.StartedAt))))
                    merged.Add(new GroupRunView
                    {
                        Name = tr.Name, State = tr.State, StartedAt = tr.StartedAt,
                        EndedAt = tr.EndedAt, ScriptCount = tr.ScriptCount, Detail = tr.Detail ?? "",
                    });
            }
            var runs = merged.OrderByDescending(x => x.StartedAt).ToList();
            var groups = BuildGroupAggregates(runs);
            string groupsKey = st.UserName;

            // 「当前执行组」：优先取 SQLite 的 Running 记录（重启后 Tracker 白纸也能正确显示）；
            //   SQLite 无 Running 时回退 Tracker 的 CurrentGroup。
            string currentFromSql = "";
            if (_sqliteTodayRuns.TryGetValue(st.UserName, out var sr2))
            {
                var running = sr2?.FirstOrDefault(r => r.State == "Running");
                if (running != null) currentFromSql = running.GroupName;
            }
            string currentGroup = currentFromSql.Length > 0 ? currentFromSql : st.CurrentGroup;
            if (IsBogusGroupName(currentGroup)) currentGroup = "";          // 当前组也可能是误识别值
            // ★ t2：标题按「按组名合并后的卡片数」报，括号里补今日运行总次数，
            //   这样「22 个组 / 79 次运行」一眼可见，不再是 79 张看起来一样的卡片。
            string head = "今日配置组（" + groups.Count + " 个 · " + runs.Count + " 次运行）"
                + (_sqliteTodayRuns.ContainsKey(st.UserName) ? " · SQLite" : "");
            if (currentGroup.Length > 0)
                head += " · 当前：" + currentGroup
                     + (st.CurrentGroupAt == DateTime.MinValue ? "" : "（" + st.CurrentGroupAt.ToString("HH:mm:ss") + "）");
            else if (st.StoppedAt != DateTime.MinValue)
                head += " · 已停止"
                     + (st.LastGroup.Length > 0 ? "（上一个：" + st.LastGroup + "）" : "")
                     + "（" + st.StoppedAt.ToString("HH:mm:ss") + "）";
            panel.Children.Add(new TextBlock { Text=head, FontSize=11, FontWeight=FontWeights.SemiBold, TextWrapping=TextWrapping.Wrap, Foreground=UiBrush.Get("TextPrimary") });
            panel.Children.Add(new TextBlock { Text="来源：run.db（今日运行记录）" + (st.GroupsSource.Length>0 ? " · 实时增量 " + st.GroupsUpdatedAt.ToString("HH:mm:ss") : ""), FontSize=9.5, TextWrapping=TextWrapping.Wrap, Foreground=UiBrush.Get("TextMuted"), Margin=new Thickness(0,3,0,5) });
            if (groups.Count == 0)
            { panel.Children.Add(new TextBlock { Text="今日暂无配置组运行记录", FontSize=10.5, Foreground=UiBrush.Get("TextMuted") }); return panel; }
            const int columns = 3;
            var grid = new Grid();
            for (int i=0;i<columns;i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
            for (int i=0;i<(groups.Count+columns-1)/columns;i++) grid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            for (int i=0;i<groups.Count;i++)
            {
                var item = BuildGroupCard(groups[i], groupsKey);
                Grid.SetRow(item,i/columns); Grid.SetColumn(item,i%columns); grid.Children.Add(item);
            }
            panel.Children.Add(grid); return panel;
        }

        /// <summary>
        /// 把「今日运行列表」按组名折叠：同名只留一张卡片，Runs 按时间倒序（[0] = 最后一次）。
        /// ★ t2 用户诉求：「配置组卡片太多了，能不能合并或者展示最后状态？」
        ///   实测 22 个组 79 次运行（其中一个组跑 44 次），合并后 79 张 → 22 张。
        /// </summary>
        private static List<GroupAggregate> BuildGroupAggregates(List<GroupRunView> runsDesc)
        {
            var map = new Dictionary<string, GroupAggregate>(StringComparer.OrdinalIgnoreCase);
            var order = new List<GroupAggregate>();
            foreach (var run in runsDesc)
            {
                if (string.IsNullOrWhiteSpace(run.Name)) continue;
                if (!map.TryGetValue(run.Name, out var agg))
                {
                    agg = new GroupAggregate { Name = run.Name };
                    map[run.Name] = agg;
                    order.Add(agg);
                }
                agg.Runs.Add(run);
            }
            // 卡片顺序：按「最后一次运行时间」倒序（最活跃/最新的组在最上面）
            order.Sort((a,b) => b.Last.StartedAt.CompareTo(a.Last.StartedAt));
            return order;
        }

        /// <summary>
        /// 过滤被当成配置组名的框架异常消息（**展示层兜底**）。
        /// ★ 采集层 RunRecordCollector.ValidGroup 已经拦了，这里再拦一次是因为
        ///   run.db 里**历史已有**这些行（采集不回头删库）；用户看到的界面不能有它们。
        /// </summary>
        private static bool IsBogusGroupName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;   // 空名交给上层处理（不额外吞掉）
            string n = name.Trim();
            if (n.Contains("Exception", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.Contains("was canceled", StringComparison.OrdinalIgnoreCase)
                || n.Contains("was cancelled", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 单张「按组名合并」的配置组卡片：
        ///   组名 + 今日 N 次 · 最后一次 <状态> / 起止时刻 / 时长 / 脚本数 / 拾取
        ///   + 点击展开该组今日全部运行（可选项，用户要求「展开看历史」）。
        /// </summary>

        /// <summary>
        /// t60：构建「分组卡片的内容」（组名 + 摘要 + 可选的历史列表）。
        /// ★ 单独提出来是为了**只重建这一张卡片**，而不是整页 Rebuild ——
        ///   旧实现点展开时整页 Children.Clear()，把米游社的 ComboBox 也销毁了，
        ///   导致下拉框选不中、视觉上「展开又收回」。
        /// </summary>
        /// <summary>
        /// ★ 2026-11-26 用户要求：「有轮次要记录轮次，没轮次只记录配置组启动次数」。
        ///   · 有轮次（锄地多世界，日志有 `[第 N/M 轮 房主]`）→「今日 N 轮」
        ///   · 无轮次（好感/CD采集/钓鱼等）→「今日 N 次」（配置组启动次数）
        ///   判定用 RoundTotal/RoundCount（都来自日志真实锚点），绝不用组名猜。
        /// </summary>
        private static string BuildCountText(GroupAggregate agg)
        {
            int withRounds = agg.Runs.Count(r => r.HasRounds);
            if (withRounds > 0)
            {
                int rounds = agg.Runs.Sum(r => r.RoundCount > 0 ? r.RoundCount : r.RoundTotal);
                if (rounds <= 0) rounds = agg.Runs.Max(r => r.RoundTotal);
                return "今日 " + agg.Runs.Count + " 次启动" + (rounds > 0 ? " · " + rounds + " 轮" : "");
            }
            return "今日 " + agg.Runs.Count + " 次";
        }

        private List<UIElement> BuildGroupCardChildren(GroupAggregate agg, string accountKey)
        {
            var last = agg.Last;
            Brush accent = GroupAccent(last.State);
            string state = StateText(last.State);
            string times = TimeText(last);
            // ★ 2026-11-26 用户要求：「有轮次要记录轮次，没轮次只记录配置组启动次数」。
            //   锄地类有 `[第 N/M 轮]` 锚点 → 报「今日 N 轮」；
            //   好感/CD采集等无轮次概念 → 报「今日 N 次」（=配置组启动次数）。
            string countText = BuildCountText(agg);
            string meta = countText + " · 最后一次 " + state
                + (times.Length > 0 ? " · " + times : "")
                + (last.ScriptCount > 0 ? " · " + last.ScriptCount + " 脚本" : "")
                + (last.Duration.HasValue ? " · " + FmtDur(last.Duration.Value) : "")
                + (last.Picks > 0 ? " · 拾取 " + last.Picks : "");

            var list = new List<UIElement>();
            list.Add(new TextBlock
            {
                Text = agg.Name, FontSize = 10.5, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, Foreground = accent
            });
            list.Add(new TextBlock
            {
                Text = meta, FontSize = 9.5, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0), Foreground = UiBrush.Get("TextSecondary")
            });
            string key = accountKey + "\u001f" + agg.Name;
            if (_expandedGroups.Contains(key) && agg.Runs.Count > 1)
                list.Add(BuildGroupHistory(agg));
            return list;
        }

        private Border BuildGroupCard(GroupAggregate agg, string accountKey)
        {
            var last = agg.Last;
            Brush accent = GroupAccent(last.State);

            var stack = new StackPanel();
            foreach (UIElement child in BuildGroupCardChildren(agg, accountKey)) stack.Children.Add(child);

            string key = accountKey + "\u001f" + agg.Name;
            var card = new Border
            {
                Padding = new Thickness(8,5,8,5), Margin = new Thickness(0,3,7,3),
                BorderBrush = accent, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                ToolTip = last.Detail.Length > 0 ? last.Detail : (agg.Runs.Count > 1 ? "点击展开今日全部运行" : null),
                Child = stack
            };
            card.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
            card.Cursor = System.Windows.Input.Cursors.Hand;
            // ★★ t60 修复（展开后自动收起 + 无法操作下拉框）：
            //
            //   旧实现：点击 → `_lastSignature = ""` + `Rebuild()`（整页 Children.Clear + 重建）。
            //   后果有两个：
            //     ① 整页重建会销毁**所有**卡片（包括米游社那行的 ComboBox），
            //        所以下拉框刚选完就被销毁重建 → 选不中、闪一下、看似「自动收起」；
            //     ② 重建期间滚动位置/鼠标捕获全部丢失，视觉上就是「点开又收回去了」。
            //
            //   新实现：**只更新这一张卡片自己的 Child**，不碰整页。
            //   展开态仍记在 _expandedGroups 里（跨 Rebuild 保持），但展开动作本身不触发 Rebuild。
            card.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;   // 阻止冒泡到上层（原来可能被父级再处理一次 = 立刻又折回去）
                bool nowExpanded = _expandedGroups.Add(key);
                if (!nowExpanded) _expandedGroups.Remove(key);
                try
                {
                    // 重建这张卡片的内容，而不是整页
                    var fresh = BuildGroupCardChildren(agg, accountKey);
                    if (card.Child is Panel oldPanel)
                    {
                        oldPanel.Children.Clear();
                        foreach (UIElement child in fresh) oldPanel.Children.Add(child);
                    }
                }
                catch
                {
                    // 万一卡片结构异常，退回整页重建（保证至少能用）
                    Rebuild();
                }
            };

            // ★★ 2026-11-26 修复「展开后历史列表重复显示两遍」：
            //   这里原来又 add 了一次 BuildGroupHistory —— 而 BuildGroupCardChildren
            //   （第 1071 行附近的 `if (_expandedGroups.Contains(key) && agg.Runs.Count > 1)`）
            //   已经加过一次了。两处都加 = 展开后「今日全部运行」整块出现两遍。
            //   展开列表只由 BuildGroupCardChildren 负责，此处不再重复添加。

            if (last.State == ScriptGroupState.Running && !ThemeManager.AnimationsDisabled)
            {
                var outline = new SolidColorBrush();
                UiBrush.BindColor(outline, SolidColorBrush.ColorProperty, "DesktopAccent");
                card.BorderBrush = outline;
                var pulse = new System.Windows.Media.Animation.DoubleAnimation
                { From=0.82, To=1, Duration=TimeSpan.FromMilliseconds(900), AutoReverse=true, RepeatBehavior=System.Windows.Media.Animation.RepeatBehavior.Forever };
                card.IsVisibleChanged += (_, _) => outline.BeginAnimation(Brush.OpacityProperty,
                    card.IsVisible && !ThemeManager.AnimationsDisabled ? pulse : null);
                if (card.IsVisible) outline.BeginAnimation(Brush.OpacityProperty, pulse);
            }
            return card;
        }

        /// <summary>展开后的历史列表：该组今日每一次运行一行（新的在上）。</summary>
        private UIElement BuildGroupHistory(GroupAggregate agg)
        {
            var list = new StackPanel { Margin = new Thickness(0,5,0,0) };
            list.Children.Add(new TextBlock
            {
                Text = "今日全部运行（" + agg.Runs.Count + " 次）",
                FontSize = 9.5, Foreground = UiBrush.Get("TextMuted"), Margin = new Thickness(0,0,0,2)
            });
            int shown = Math.Min(agg.Runs.Count, 40);          // 兜底：单组最多列 40 行
            for (int i = 0; i < shown; i++)
            {
                var r = agg.Runs[i];
                // 每行带上该次运行的轮次（锄地类才有）；无轮次的组不显示。
                string roundText = "";
                if (r.HasRounds)
                {
                    int rc = r.RoundCount > 0 ? r.RoundCount : r.RoundTotal;
                    roundText = " · " + rc + " 轮";
                    if (r.RoundHost.Length > 0) roundText += "（房主 " + r.RoundHost + "）";
                }
                string line = TimeText(r) + " · " + StateText(r.State)
                    + roundText
                    + (r.Duration.HasValue ? " · " + FmtDur(r.Duration.Value) : "")
                    + (r.Picks > 0 ? " · 拾取 " + r.Picks : "");
                list.Children.Add(new TextBlock
                {
                    Text = line, FontSize = 9.5, TextWrapping = TextWrapping.Wrap,
                    Foreground = GroupAccent(r.State)
                });
            }
            if (agg.Runs.Count > shown)
                list.Children.Add(new TextBlock
                {
                    Text = "… 另有 " + (agg.Runs.Count - shown) + " 次未列出",
                    FontSize = 9.5, Foreground = UiBrush.Get("TextMuted")
                });
            return list;
        }

        private static string StateText(ScriptGroupState state) => state switch
        {
            ScriptGroupState.Running => "执行中",
            ScriptGroupState.Finished => "已完成",
            ScriptGroupState.Failed => "失败",
            ScriptGroupState.Cancelled => "已取消",
            ScriptGroupState.Interrupted => "已中断",
            _ => "未运行",
        };

        private static string TimeText(GroupRunView run)
        {
            string start = run.StartedAt == DateTime.MinValue ? "" : run.StartedAt.ToString("HH:mm:ss");
            string end = run.EndedAt == DateTime.MinValue ? "" : run.EndedAt.ToString("HH:mm:ss");
            if (start.Length == 0) return end;
            return end.Length > 0 ? start + " → " + end : start;
        }

        private static string FmtDur(TimeSpan d)
            => d.TotalHours >= 1 ? ((int)d.TotalHours) + ":" + d.ToString(@"mm\:ss") : d.ToString(@"mm\:ss");

        private static Brush GroupAccent(ScriptGroupState state)
        {
            return state switch
            {
                ScriptGroupState.Running => UiBrush.Get("DesktopAccent"),
                ScriptGroupState.Finished => UiBrush.Get("MintBrush"),
                ScriptGroupState.Failed => UiBrush.Get("RoseBrush"),
                ScriptGroupState.Cancelled or ScriptGroupState.Interrupted => UiBrush.Get("AmberBrush"),
                _ => UiBrush.Get("TextSecondary")
            };
        }

        /// <summary>
        /// ★ 2026-11-26 修复「读取时刻」误导：
        ///   用户看到「读取：14:08:52」在跳，以为数据在更新，实际 records 文件已 9.6 小时没动。
        ///   「读取时刻」只说明程序去读了，「文件写入时刻」才是数据真正的年龄。
        ///   两者同时显示，用户一眼就能判断是「程序卡住」还是「源文件没变」。
        /// </summary>
        private static string DescribeDogfoodFreshness(AccountState st)
        {
            string readText = st.DogfoodReadAt == DateTime.MinValue ? "—" : st.DogfoodReadAt.ToString("HH:mm:ss");
            if (st.DogfoodFileWriteAt == DateTime.MinValue)
                return "读取：" + readText + " · 源文件不存在";

            var age = DateTime.Now - st.DogfoodFileWriteAt;
            string ageText = age.TotalMinutes < 1 ? "刚刚"
                : age.TotalHours < 1 ? (int)age.TotalMinutes + " 分钟前"
                : age.TotalDays < 1 ? (int)age.TotalHours + " 小时前"
                : (int)age.TotalDays + " 天前";

            // 超过 1 小时没更新 → 明确提示「源文件未更新」，避免用户以为卡死
            string warn = age.TotalHours >= 1 ? "（源文件未更新）" : "";
            return "数据更新于 " + st.DogfoodFileWriteAt.ToString("HH:mm:ss")
                 + "（" + ageText + "）" + warn
                 + " · 读取：" + readText;
        }

        private UIElement BuildDogfood(AccountState st)
        {
            var panel = new StackPanel { Margin=new Thickness(20,0,0,0) };
            // ★ 狗粮保持 04:00 业务日（它读的是独立 records 文件，不随日志滚动）；
            //   原来这里还取了一份 00:00 视图做对照，现在日志侧统一 00:00、
            //   狗粮侧统一 04:00，双视图已无意义，去掉后少一份缓存。
            var rec = DogfoodRecordReader.ForDay(
                st.DogfoodRecords.FirstOrDefault(r => DogfoodRecordReader.ForDay(r, DateTime.Now, 4).IsValid)
                    ?? st.DogfoodRecords.FirstOrDefault() ?? new DogfoodRecord(),
                DateTime.Now, 4);
            panel.Children.Add(new TextBlock { Text="狗粮记录 · 日界 04:00", FontSize=11, FontWeight=FontWeights.SemiBold, Foreground=UiBrush.Get("TextPrimary") });
            string route = rec.Route switch { DogfoodEndingRoute.EndingA=>"A：走联机",DogfoodEndingRoute.EndingB=>"B：自己跑",_=>"无有效记录" };
            string dayKey = rec.BusinessDay == DateTime.MinValue ? "—" : rec.BusinessDay.ToString("yyyy-MM-dd");
            panel.Children.Add(new TextBlock
            {
                Text = "业务日 " + dayKey + "：" + (rec.IsValid ? "有效" : "不可用") + " · " + route
                     + "\n日期：" + (rec.Date.Length == 0 ? "—" : rec.Date)
                     + "\n经验：" + rec.Exp.ToString("N0") + " · 摩拉：" + rec.Mora.ToString("N0")
                     + "\n" + (rec.Reason.Length == 0 ? "记录读取正常" : rec.Reason)
                     + "\n" + DescribeDogfoodFreshness(st)
                     + "\n" + st.DogfoodRecordPath,
                FontSize = 10.5, TextWrapping = TextWrapping.Wrap,
                Foreground = UiBrush.Get("TextSecondary"), Margin = new Thickness(0, 5, 0, 0)
            });

            // ── 今日各组收益分解（2026-11-26 用户要求：两个数据分别显示 + 汇总，共三行）──
            //   数据来自 run.db 的 dogfood_exp/dogfood_mora（采集层按日志格式解析）。
            //   ★ 不写死组名：哪个组产出了收益就列哪个组（用户：别人组名可能不一样）。
            var breakdown = BuildDogfoodBreakdown(st);
            if (breakdown != null) panel.Children.Add(breakdown);

            return panel;
        }

        /// <summary>
        /// ★ 2026-11-26 用户要求：把各组的狗粮收益「分别显示 + 汇总」，一共三行。
        ///   数据来自 run.db（采集层按日志格式 `分解可获得经验` / `收益：…摩拉 N` 解析，
        ///   取每组最后一次 = 本轮真实收益，第一次是清空背包不算）。
        ///   ★ 组名完全来自数据，不写死 —— 用户换了组名照样能显示。
        /// </summary>
        private UIElement? BuildDogfoodBreakdown(AccountState st)
        {
            try
            {
                if (!_sqliteTodayRuns.TryGetValue(st.UserName, out var runs) || runs == null || runs.Count == 0)
                    return null;

                // 只取有狗粮收益的 run，按组聚合（同组多次 → 累加，因为每次都是独立的一轮收益）
                var byGroup = new Dictionary<string, (long Exp, long Mora)>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in runs)
                {
                    if (r.DogfoodExp <= 0 && r.DogfoodMora <= 0) continue;
                    string g = string.IsNullOrWhiteSpace(r.GroupName) ? "（未标注组）" : r.GroupName;
                    byGroup.TryGetValue(g, out var cur);
                    byGroup[g] = (cur.Exp + r.DogfoodExp, cur.Mora + r.DogfoodMora);
                }
                if (byGroup.Count == 0) return null;

                var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                int row = 0;
                void AddRow(string label, long exp, long mora, bool bold)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var brush = bold ? UiBrush.Get("TextPrimary") : UiBrush.Get("TextSecondary");
                    var w = bold ? FontWeights.SemiBold : FontWeights.Normal;
                    var cells = new[]
                    {
                        label,
                        exp > 0 ? exp.ToString("N0") : "—",
                        mora > 0 ? mora.ToString("N0") : "—",
                    };
                    for (int c = 0; c < cells.Length; c++)
                    {
                        var tb = new TextBlock
                        {
                            Text = cells[c], FontSize = 10.5, Foreground = brush, FontWeight = w,
                            Margin = new Thickness(0, 2, 8, 2),
                        };
                        Grid.SetRow(tb, row); Grid.SetColumn(tb, c);
                        grid.Children.Add(tb);
                    }
                    row++;
                }

                // 表头
                AddRow("组（经验 / 摩拉）", 0, 0, false);
                // 各组一行
                long totalExp = 0, totalMora = 0;
                foreach (var kv in byGroup.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    AddRow(kv.Key, kv.Value.Exp, kv.Value.Mora, false);
                    totalExp += kv.Value.Exp;
                    totalMora += kv.Value.Mora;
                }
                // 合计行
                AddRow("合计", totalExp, totalMora, true);

                return grid;
            }
            catch { return null; }
        }

        private UIElement BuildHotkeyTable(BgiAccountData data)
        {
            var panel = new StackPanel { Margin = new Thickness(20,0,0,0) };
            panel.Children.Add(new TextBlock { Text="快捷键", FontSize=11, FontWeight=FontWeights.SemiBold, Foreground=UiBrush.Get("TextPrimary"), Margin=new Thickness(0,0,0,5) });
            var entries = data.DisplayHotkeys.Where(x => x.Name.Length > 0 && x.Key.Length > 0).OrderBy(x => x.Name,StringComparer.OrdinalIgnoreCase).ToList();
            if (entries.Count == 0)
            {
                panel.Children.Add(new TextBlock { Text="没有已设置的快捷键", FontSize=10.5, Foreground=UiBrush.Get("TextMuted") });
                return panel;
            }
            var table = new Grid();
            table.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(150) });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
            for (int i=0;i<entries.Count;i++)
            {
                table.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
                var name = new TextBlock { Text=entries[i].Name, FontSize=10.5, Foreground=UiBrush.Get("TextSecondary"), Margin=new Thickness(0,3,8,3) };
                var key = new TextBlock { Text=entries[i].Key, FontSize=11, FontWeight=FontWeights.SemiBold, Foreground=UiBrush.Get("TextPrimary"), Margin=new Thickness(0,3,0,3) };
                Grid.SetRow(name,i); Grid.SetRow(key,i); Grid.SetColumn(key,1);
                table.Children.Add(name); table.Children.Add(key);
            }
            panel.Children.Add(table); return panel;
        }

        private static string HotkeyName(string key) => key switch
        {
            "cancelTaskHotkey" => "停止当前脚本",
            "bgiEnabledHotkey" => "启动或暂停 BGI",
            "autoPickEnabledHotkey" => "自动拾取",
            "autoSkipEnabledHotkey" => "自动跳过剧情",
            "autoSkipHangoutEnabledHotkey" => "自动跳过邀约",
            "autoWoodHotkey" => "自动伐木",
            "enhanceArtifactHotkey" => "强化圣遗物",
            "quickBuyHotkey" => "快捷购买",
            "oneKeyClaimRewardHotkey" => "一键领取奖励",
            "quickSereniteaPotHotkey" => "快捷尘歌壶",
            "quickTeleportEnabledHotkey" => "快捷传送",
            "quickTeleportTickHotkey" => "连续传送",
            "skillCdEnabledHotkey" => "技能冷却提示",
            "takeScreenshotHotkey" => "截图",
            "recognitionTemplateEditorHotkey" => "识别模板编辑器",
            "turnAroundHotkey" => "原地转圈",
            "clickGenshinConfirmButtonHotkey" => "点击确认按钮",
            "clickGenshinCancelButtonHotkey" => "点击取消按钮",
            "oneKeyFightHotkey" => "一键战斗",
            "mapPosRecordHotkey" => "记录地图位置",
            "autoMusicGameHotkey" => "自动音游",
            "autoFishingGameHotkey" => "自动钓鱼",
            "autoCookGameHotkey" => "自动烹饪",
            _ => "其它功能"
        };

        /// <summary>
        /// 从日志原文里取出最后一条"看起来像任务启动"的行。
        /// ★ 只做展示，不做语义归类 —— BGI 的任务格式随配置组作者和版本变化，
        ///   归类必然出错。真正需要判断的地方请自行匹配 RecentLog。
        /// </summary>
        private static string ExtractTaskLine(AccountState st)
        {
            try
            {
                var log = st.RecentLog;
                if (string.IsNullOrEmpty(log)) return "—";

                // 常见锚点（不追求穷举，只是取一行给人看）
                string[] anchors =
                {
                    "开始执行地图追踪任务", "选用配队", "开始执行JS脚本",
                    "锄地一条龙任务启动", "配对界面切换角色任务启动",
                    "开始执行独立任务", "配置组 \"", "开始执行路线", "开始第"
                };

                var lines = log.Split('\n');
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string ln = lines[i].Trim();
                    if (ln.Length == 0) continue;
                    foreach (var a in anchors)
                        if (ln.IndexOf(a, StringComparison.Ordinal) >= 0)
                            return TrimNoise(ln);
                }
                return "—";
            }
            catch { return "—"; }
        }

        /// <summary>去掉日志行前缀（时间戳、级别、命名空间），只留正文。</summary>
        private static string TrimNoise(string line)
        {
            try
            {
                // [21:52:52.484] [INF] [Primary:S2:P...] Namespace.Class
                int cut = line.LastIndexOf(']');
                if (cut >= 0 && cut + 1 < line.Length)
                {
                    string rest = line.Substring(cut + 1).Trim();
                    // 有些行后面还跟着命名空间，去掉它
                    int sp = rest.LastIndexOf("BetterGenshinImpact", StringComparison.Ordinal);
                    if (sp >= 0 && sp + 20 < rest.Length) rest = rest.Substring(sp + 20).Trim();
                    if (rest.Length > 0) return rest.Length > 110 ? rest.Substring(0, 110) + "…" : rest;
                }
                return line.Length > 110 ? line.Substring(0, 110) + "…" : line;
            }
            catch { return line; }
        }

        private static int CountLines(string? s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int n = 0;
            foreach (var c in s) if (c == '\n') n++;
            return n + 1;
        }

        /// <summary>把字节数格式化成易读形式。</summary>
        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.00") + " MB";
        }


        /// <summary>
        /// ★ t70：从 Grid 里取出「指定行、第 1 列」的 TextBlock（即 AddRow 建的值控件）。
        /// AddRow 没有返回值，而改它的签名会影响十几处调用点；
        /// 用这个查找代替，改动面最小。
        /// </summary>
        private static TextBlock? FindCell(Grid grid, int rowIdx)
        {
            try
            {
                foreach (UIElement child in grid.Children)
                {
                    if (child is TextBlock tb
                        && Grid.GetRow(tb) == rowIdx
                        && Grid.GetColumn(tb) == 1)
                        return tb;
                }
            }
            catch { }
            return null;
        }

        private void AddRow(Grid grid, int rowIdx, string label, string value, bool mono = false)
        {
            var lab = new TextBlock
            {
                Text = label,
                FontSize = 10.5,
                Margin = new Thickness(0, 2, 0, 2),
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = UiBrush.Get("TextSecondary")
            };
            Grid.SetRow(lab, rowIdx);
            Grid.SetColumn(lab, 0);
            grid.Children.Add(lab);

            var val = new TextBlock
            {
                Text = value,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 2),
                VerticalAlignment = VerticalAlignment.Top,
                TextWrapping = TextWrapping.Wrap,
                Foreground = UiBrush.Get("TextPrimary")
            };
            if (mono) val.FontFamily = (FontFamily)FindResource("MonoFont");
            Grid.SetRow(val, rowIdx);
            Grid.SetColumn(val, 1);
            grid.Children.Add(val);
        }
    }
}
