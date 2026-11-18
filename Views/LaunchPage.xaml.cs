using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SoulLink.Models;

// 只引入需要的图形类型，避免 Path 与 System.IO.Path 冲突
using Ellipse = System.Windows.Shapes.Ellipse;
using SoulLink.Services;

namespace SoulLink.Views
{
    public partial class LaunchPage : UserControl
    {
        public event Action<bool, string>? StatusChanged;

        private readonly ObservableCollection<ActionVm> _actions = new ObservableCollection<ActionVm>();
        private readonly ObservableCollection<SessionVm> _sessions = new ObservableCollection<SessionVm>();
        private bool _busy;

        /// <summary>
        /// 在线会话的定时刷新。
        /// ★ 原来启动台只在 Activate()（切到本页）时刷新一次，
        ///   停在启动台时账号上下线看不到变化，必须手动点刷新或切页才更新
        ///   （用户反馈："为什么还要按刷新才能看到变绿灯"）。
        /// </summary>
        private System.Windows.Threading.DispatcherTimer? _sessionTimer;
        private bool _refreshingSessions;

        public LaunchPage()
        {
            InitializeComponent();
            ActionList.ItemsSource = _actions;
            SessionList.ItemsSource = _sessions;

            _sessionTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _sessionTimer.Tick += (_, __) =>
            {
                // 只在本页可见时刷新，切走就跳过，省开销
                if (!IsVisible) return;
                RefreshSessions();
                // 自动化卡片也跟随刷新，否则必须切页才能看到状态/名称变化。
                RefreshAutomationList();
                // 右侧原始日志：跟随点击的会话
                if (_follower == null) RefreshSelectedLog();
                // 左侧：主会话显示总览，其它账号显示单账号状态
                if (_logSession != null && _logSession.IsConsoleSession)
                {
                    // ★ 每轮都调用 RefreshOverview：它内部会比对"当前会话集合"
                    //   与已有卡片，账号增删/会话号变化（注销重登会换号）时自动重建，
                    //   否则出现过一次卡片后就再也不重建，新登录的账号永远不显示。
                    RefreshOverview();
                    if (_overviewRows.Count > 0) UpdateOverviewTexts();
                }
                else if (_statusFollower == null && _logSession != null)
                {
                    StartStatusFollowFor(_logSession);
                }
            };
            // ★ 定时器启动不能只靠 Loaded：UserControl 在页面切换时
            //   会触发 Unloaded 停表，再切回来 Loaded 不一定再触发，
            //   导致定时器永久停摆（表现：状态/会话列表都不再刷新）。
            //   改成 Loaded + Activate 双保险，并让 Activate 每次都确保启动。
            Loaded += (_, __) => _sessionTimer?.Start();
            Unloaded += (_, __) =>
            {
                _sessionTimer?.Stop();
                // 页面卸载时停止跟随，避免后台一直持有文件句柄
                StopFollower();
                StopStatusFollower();
            };
        }

        public void Activate()
        {
            ReloadActions();
            // 确保定时器在运行（页面切回来时 Loaded 不一定触发）
            if (_sessionTimer != null && !_sessionTimer.IsEnabled)
                _sessionTimer.Start();
            RefreshSessions();
            RefreshAutomationList();
        }

        // ══════════ 数据加载 ══════════

        public void ReloadActions()
        {
            string? keepId = (ActionList.SelectedItem as ActionVm)?.Model.Id;
            _actions.Clear();
            foreach (var a in App.Config.Actions)
                _actions.Add(new ActionVm(a, App.Config));

            EmptyHintHost.Visibility = _actions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var sel = _actions.FirstOrDefault(v => v.Model.Id == keepId) ?? _actions.FirstOrDefault();
            if (sel != null) ActionList.SelectedItem = sel;
            else UpdateCommandPreview(null);
        }

        public void RefreshSessions()
        {
            // ★ 防重入：定时器 3 秒触发一次，而一次查询要 ~30ms；
            //   若上次还没回来就再发，会堆积请求并让列表反复重建（闪烁）。
            if (_refreshingSessions) return;
            _refreshingSessions = true;

            var spin = FindResource("SpinRefresh") as System.Windows.Media.Animation.Storyboard;
            try { spin?.Begin(RefreshGlyph); } catch { }

            _ = Task.Run(async () =>
            {
                try
                {
                    // ★ 优先读账号状态中心快照：它每秒统一刷新一次，
                    //   本页原来每 3 秒自己 query user + 枚举 BGI 进程，
                    //   与其他页面重复。快照太旧时才自己查。
                    List<SessionInfo> all;
                    var snap = AccountStateCenter.Sessions;
                    bool fresh = snap.Count > 0
                        && (DateTime.Now - AccountStateCenter.LastRefresh).TotalSeconds < 5;
                    if (fresh) all = snap;
                    else all = await SessionService.QueryAsync().ConfigureAwait(false);

                    var users = SessionService.FilterUsers(all); // 总览是导航入口，主用户必须始终可见
                    await Dispatcher.InvokeAsync(() => ApplySessions(users));
                }
                catch { }
                finally { _refreshingSessions = false; }
            });
        }

        /// <summary>
        /// 增量更新会话列表：只增删有变化的项，不整体 Clear。
        /// ★ 原来每次 RefreshSessions 都先 _sessions.Clear() 再全量重建，
        ///   定时刷新时会造成明显闪烁，而且选中项会丢失。
        /// </summary>
        private void ApplySessions(List<SessionInfo> users)
        {
            try
            {
                // ★ 拿到各会话的 BGI 日志最后写入时间，用于"空闲"分级。
                //   没有 BGI 的会话留空 → BgiLevel 判定为红（按需求：BGI 没启动也算空闲）。
                Dictionary<int, DateTime> bgiTimes;
                HashSet<int> channelSessions;
                try
                {
                    bgiTimes = new Dictionary<int, DateTime>();
                    // ★ 快照里已有每账号 BGI 的日志写入时间，优先用它；
                    //   快照不可用时才现枚举（枚举进程较慢，不该每页都做）。
                    bool usedSnapshot = false;
                    try
                    {
                        if ((DateTime.Now - AccountStateCenter.LastRefresh).TotalSeconds < 5)
                        {
                            foreach (var st in AccountStateCenter.All())
                            {
                                if (st.BgiRunning && st.HasSession && st.BgiLogWrite != DateTime.MinValue)
                                    bgiTimes[st.SessionId] = st.BgiLogWrite;
                            }
                            usedSnapshot = true;
                        }
                    }
                    catch { }

                    if (!usedSnapshot)
                        foreach (var kv in BgiLogWatcher.Discover())
                            bgiTimes[kv.Key] = kv.Value.LastWrite;

                    // 以“能连通活代理”为准，避免残留管道让状态反复跳动。
                    channelSessions = BgiLogWatcher.DiscoverLiveAgentSessions();
                }
                catch
                {
                    bgiTimes = new Dictionary<int, DateTime>();
                    channelSessions = new HashSet<int>();
                }

                // 移除已消失的会话（按 会话ID + 用户名 判定）
                for (int i = _sessions.Count - 1; i >= 0; i--)
                {
                    var cur = _sessions[i].Session;
                    bool still = false;
                    foreach (var u in users)
                        if (u.Id == cur.Id && string.Equals(u.UserName, cur.UserName, StringComparison.OrdinalIgnoreCase))
                        { still = true; break; }
                    if (!still) _sessions.RemoveAt(i);
                }

                // 新增或就地更新
                foreach (var u in users)
                {
                    SessionVm? exist = null;
                    foreach (var vm in _sessions)
                        if (vm.Session.Id == u.Id &&
                            string.Equals(vm.Session.UserName, u.UserName, StringComparison.OrdinalIgnoreCase))
                        { exist = vm; break; }

                    // 注入该会话的 BGI 活跃时间（没有则为 null → 判定为红）
                    DateTime? bgiTime = bgiTimes.TryGetValue(u.Id, out var bt) ? bt : (DateTime?)null;
                    bool hasChannel = channelSessions.Contains(u.Id);

                    // 备注名 + 真实大小写：都从账号配置里取
                    string remark = "";
                    string realName = "";
                    foreach (var acc in App.Config.Accounts)
                        if (string.Equals(acc.UserName, u.UserName, StringComparison.OrdinalIgnoreCase))
                        {
                            remark = acc.DisplayName ?? "";
                            realName = acc.UserName ?? "";   // 配置里的大小写为准
                            break;
                        }

                    if (exist == null)
                    {
                        var newVm = new SessionVm(u)
                        {
                            BgiLastWrite = bgiTime,
                            HasChannel = hasChannel
                        };
                        if (hasChannel) newVm.ChannelError = "";
                        newVm.SetRealName(realName);
                        newVm.SetRemark(remark);
                        _sessions.Add(newVm);
                    }
                    else
                    {
                        exist.BgiLastWrite = bgiTime;
                        if (hasChannel) exist.ChannelError = "";
                        exist.HasChannel = hasChannel;
                        exist.SetRealName(realName);
                        exist.SetRemark(remark);
                        exist.Session = u;      // 就地替换，触发绑定刷新
                    }
                }

                TipText.Text = users.Count == 0
                    ? "当前没有任何用户登录会话。请先让目标账号登录（RDP 或切换用户）。"
                    : "共 " + users.Count + " 个会话在线 · 点击查看其中运行的程序。";

                // ★ 首次进入时默认选中主会话，让左栏显示"所有账号总览"。
                if (SessionList.SelectedItem == null && _sessions.Count > 0)
                {
                    SessionVm? mainVm = null;
                    foreach (var vm in _sessions)
                        if (vm.Session.IsConsoleSession) { mainVm = vm; break; }
                    if (mainVm != null) SessionList.SelectedItem = mainVm;
                }
            }
            catch { }
        }

        // ══════════ 选择 / 预览 ══════════

        private void ActionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateCommandPreview(ActionList.SelectedItem as ActionVm);
        }

        /// <summary>
        /// 选中动作时记忆它（下次启动恢复），并刷新上次执行结果提示。
        /// ★ 底部"执行/命令预览"区已移除：执行、编辑、删除都在卡片上直接点，
        ///   不需要先选中再点下面的按钮。
        /// </summary>
        private void UpdateCommandPreview(ActionVm? vm)
        {
            if (vm == null) return;

            var state = ConfigStore.LoadState();
            state.SelectedActionId = vm.Model.Id;
            ConfigStore.SaveState(state);
        }

        private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // ★ 点击会话不再跳转到"会话进程"页，改为在本页下方显示该账号的 BGI 实时日志。
            //   挂机场景下看日志比看进程列表更有用，也避免来回切页。
            if (SessionList.SelectedItem is SessionVm vm)
            {
                _logSession = vm.Session;
                _logText = "";
                LogText.Text = "";


                // ★ 左栏内容随所选会话切换：
                //   · 主会话（用户自己的桌面）→ 显示"快捷动作"（动作列表 + 状态）
                //   · 其他会话（挂机账号）    → 隐藏快捷动作，只显示该账号的运行状态
                //   避免"看着 A1，左边却是主用户的快捷动作"这种误导。
                ApplyLeftPanelMode(vm.Session);
                RefreshAutomationList();

                RefreshSelectedLog(force: true);
            }
        }

        /// <summary>
        /// 按所选会话切换左栏内容。
        ///
        /// ★ 主会话（console，用户自己的桌面）：
        ///     显示快捷动作 + **所有挂机账号的运行监控总览**。
        ///     主会话本身不跑 BGI，看它自己的状态没有意义。
        ///   其他会话（挂机账号）：
        ///     隐藏快捷动作，只显示该账号的详细运行状态。
        /// </summary>
        private void ApplyLeftPanelMode(SessionInfo sess)
        {
            try
            {
                bool isMain = sess.IsConsoleSession;

                ActionPanel.Visibility = isMain ? Visibility.Visible : Visibility.Collapsed;

                // 页面标题固定为"运行状态"（左栏主题），只切换副标题
                if (isMain)
                {
                    PageSubtitle.Text = "所有挂机账号的运行情况 · 下方为快捷动作";
                    BtnNewAction.Visibility = Visibility.Visible;

                    // 总览模式
                    OverviewPanel.Visibility = Visibility.Visible;
                    SingleStatusGrid.Visibility = Visibility.Collapsed;
                    StopStatusFollower();
                    RefreshOverview();
                }
                else
                {
                    PageSubtitle.Text = sess.UserName + " · 该账号的 BGI 运行情况";
                    BtnNewAction.Visibility = Visibility.Collapsed;

                    // 单账号模式
                    OverviewPanel.Visibility = Visibility.Collapsed;
                    SingleStatusGrid.Visibility = Visibility.Visible;
                    StartStatusFollowFor(sess);
                }
            }
            catch { }
        }

        // ── 总览：所有挂机账号的运行状态 ────────────────────────────────────

        private sealed class OverviewRow
        {
            public string User = "";
            public int SessionId;
            public string Group = "—";
            public string Round = "";
            public string Route = "";
            public BgiStatus Status = new BgiStatus();
            public System.Windows.Controls.Border? Card;
            public System.Windows.Controls.TextBlock? Line1;
            public System.Windows.Controls.TextBlock? Line2;
            public System.Windows.Controls.TextBlock? Line3;
            /// <summary>右上角的"今日统计"文字（经验/摩拉/拾取/狗粮）。</summary>
            public System.Windows.Controls.TextBlock? TodayLine;
            /// <summary>卡片右上角的"每日报错"角标。</summary>

        }

        private readonly List<OverviewRow> _overviewRows = new List<OverviewRow>();

        /// <summary>刷新总览列表（哪些账号在跑 BGI、各自进度）。</summary>
        private void RefreshOverview()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var bgi = BgiLogWatcher.Discover();
                    Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            // ★ 列出**所有**远程账号（主会话除外），
                            //   没跑 BGI 的也要显示，否则用户看不出"这个号今天没上工"。
                            //   inst 为 null 表示该会话没有 BetterGI 进程。
                            var wanted = new List<(SessionInfo sess, BgiLogWatcher.Instance? inst)>();
                            foreach (var vm in _sessions)
                            {
                                var s = vm.Session;
                                if (!AccountScope.Allows(s.UserName)) continue;
                                bgi.TryGetValue(s.Id, out var inst);
                                wanted.Add((s, inst));
                            }

                            // ★ 判断是否需要重建：行数变化，或账号集合变化。
                            //   原来只比行数，A4 这种"晚一步被发现"的账号会被漏掉
                            //   （首次建了 A1，之后两个账号时行数仍判断为变化但集合没同步）。
                            bool needRebuild = wanted.Count != _overviewRows.Count;
                            if (!needRebuild)
                            {
                                foreach (var (s, _) in wanted)
                                {
                                    bool found = false;
                                    foreach (var row in _overviewRows)
                                        if (row.SessionId == s.Id) { found = true; break; }
                                    if (!found) { needRebuild = true; break; }
                                }
                            }

                            if (needRebuild)
                            {
                                _overviewRows.Clear();
                                OverviewList.Children.Clear();
                                foreach (var (s, inst) in wanted)
                                {
                                    // 备注名：中控台填的 DisplayName，形如 "A2 · 山客"
                                    string rm = "";
                                    foreach (var acc in App.Config.Accounts)
                                        if (string.Equals(acc.UserName, s.UserName, StringComparison.OrdinalIgnoreCase))
                                        { rm = acc.DisplayName ?? ""; break; }
                                    string shown = string.IsNullOrWhiteSpace(rm)
                                        ? s.UserName : (s.UserName + " · " + rm);

                                    var row = new OverviewRow { User = shown, SessionId = s.Id };

                                    var card = new System.Windows.Controls.Border
                                    {
                                        Margin = new Thickness(0, 0, 0, 6),
                                        Padding = new Thickness(10, 8, 10, 8),
                                        CornerRadius = new CornerRadius(8),
                                        Background = new System.Windows.Media.SolidColorBrush(
                                            System.Windows.Media.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF))
                                    };
                                    // 三行结构（与单账号详情一致）：
                                    //   ① A1 · 联机锄地-融合      （账号 · 配置组/脚本）
                                    //   ② 第 2/3 轮 · 思          （轮次 / 角色）
                                    //   ③ 路线 60/78 · 03-xxx     （当前路线）
                                    // 卡片内三行信息 + 右上角"今日统计"。
                                    // （报错角标已移除：报错在「运行状态」详情面板里有完整展示）
                                    var inner = new System.Windows.Controls.Grid();
                                    inner.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
                                    { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
                                    inner.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
                                    { Width = System.Windows.GridLength.Auto });

                                    var todayLine = new System.Windows.Controls.TextBlock
                                    {
                                        FontSize = 9.5,
                                        VerticalAlignment = System.Windows.VerticalAlignment.Top,
                                        HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                                        Margin = new Thickness(8, 1, 0, 0),
                                        Foreground = new System.Windows.Media.SolidColorBrush(
                                            System.Windows.Media.Color.FromRgb(0x9F, 0xE8, 0xC8)),
                                        Visibility = Visibility.Collapsed
                                    };
                                    System.Windows.Controls.Grid.SetColumn(todayLine, 1);
                                    inner.Children.Add(todayLine);

                                    // ★ 用 Grid 而不是 StackPanel：StackPanel 水平方向给的可用宽度是
                                    //   无限大，TextWrapping 不生效，长路线名会撑出卡片。
                                    //   Grid 会把真实宽度传下去，换行才能正常。
                                    var sp = new System.Windows.Controls.StackPanel
                                    {
                                        // 关键：告诉 StackPanel 它的宽度由父级决定
                                        HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
                                    };
                                    System.Windows.Controls.Grid.SetColumn(sp, 0);

                                    inner.Children.Add(sp);

                                    var l1 = new System.Windows.Controls.TextBlock
                                    {
                                        FontSize = 11.5,
                                        FontWeight = System.Windows.FontWeights.SemiBold,
                                        TextWrapping = TextWrapping.Wrap,
                                        Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary")
                                    };
                                    var l2 = new System.Windows.Controls.TextBlock
                                    {
                                        FontSize = 10,
                                        Margin = new Thickness(0, 3, 0, 0),
                                        TextWrapping = TextWrapping.Wrap,
                                        Foreground = (System.Windows.Media.Brush)FindResource("CyanBrush")
                                    };
                                    var l3 = new System.Windows.Controls.TextBlock
                                    {
                                        FontSize = 9.5,
                                        Margin = new Thickness(0, 2, 0, 0),
                                        TextWrapping = TextWrapping.Wrap,
                                        Foreground = (System.Windows.Media.Brush)FindResource("TextMuted")
                                    };
                                    sp.Children.Add(l1);
                                    sp.Children.Add(l2);
                                    sp.Children.Add(l3);
                                    card.Child = inner;

                                    row.Card = card; row.Line1 = l1; row.Line2 = l2; row.Line3 = l3;
                                    row.TodayLine = todayLine;
                                    _overviewRows.Add(row);
                                    OverviewList.Children.Add(card);
                                }
                            }

                            StatusMeta.Text = wanted.Count == 0
                                ? "暂无挂机账号在运行 BGI"
                                : ("共 " + wanted.Count + " 个挂机账号");


                        }
                        catch { }
                    });
                }
                catch { }
            });
        }

        /// <summary>从设置里读"用户名 → BGI 路径"（动作编辑窗口的 BGI 按钮会记住）。</summary>
        private static string LoadBgiPath(string user)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(user)) return "";
                // ★ 统一走解析器：手动指定 > 正在运行 > 记忆
                return BgiDataCenter.Get(user).ExePath;
            }
            catch { return ""; }
        }

        /// <summary>更新总览里每个账号的进度文字（读各自日志尾部）。</summary>
        private void UpdateOverviewTexts()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var bgi = BgiLogWatcher.Discover();

                    // ★ 每次刷新都重算备注名：中控台里改了备注名后，
                    //   卡片不会重建（账号集合没变），必须在更新时同步。
                    foreach (var row in _overviewRows.ToArray())
                    {
                        foreach (var vm in _sessions)
                        {
                            if (vm.Session.Id != row.SessionId) continue;
                            string rm = "";
                            foreach (var acc in App.Config.Accounts)
                                if (string.Equals(acc.UserName, vm.Session.UserName, StringComparison.OrdinalIgnoreCase))
                                { rm = acc.DisplayName ?? ""; break; }
                            row.User = string.IsNullOrWhiteSpace(rm)
                                ? vm.Session.UserName
                                : (vm.Session.UserName + " · " + rm);
                            break;
                        }
                    }

                    var results = new List<(OverviewRow row, BgiStatus st, bool running, bool ranToday)>();
                    foreach (var row in _overviewRows.ToArray())
                    {
                        // ★ 没有 BGI 进程也要出结果（running=false）。
                        //   再用"今天有没有日志文件"区分两种情况：
                        //     有日志 → 今天跑过，当前已退出
                        //     没日志 → 今天还没启动过 / 该账号今天没上工
                        if (!bgi.TryGetValue(row.SessionId, out var inst))
                        {
                            bool ranToday = false;
                            try
                            {
                                // 从该账号配置的 BGI 路径推断今天是否有日志
                                string uname = row.User.Split('·')[0].Trim();
                                string bgiExe = LoadBgiPath(uname);
                                if (!string.IsNullOrEmpty(bgiExe))
                                    ranToday = !string.IsNullOrEmpty(BgiLogWatcher.FindTodayLog(bgiExe));
                            }
                            catch { }
                            results.Add((row, new BgiStatus(), false, ranToday));
                            continue;
                        }

                        // ★ 性能优化：原来每次刷新都把整份日志完整读两遍
                        //   （TailLinesDeep 最多 16MB + CountDailyErrors 扫全文件）。
                        //   A1 的当天日志有 33MB，每秒刷一次 = 每秒处理几十 MB，
                        //   日志读取明显卡顿（用户实测）。
                        //
                        //   现在按 (文件, 最后写入时间, 长度) 缓存：
                        //   文件没变就直接复用上次结果，只有真正追加了内容才重新解析。
                        var fi = new FileInfo(inst.LogFile);
                        var key = inst.LogFile + "|" + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length;

                        StatusCacheEntry entry;
                        lock (_statusCache)
                        {
                            if (!_statusCache.TryGetValue(inst.LogFile, out entry)
                                || entry.Key != key)
                            {
                                // ★ 深读一段再解析：
                                //   「开始执行地图追踪任务」这类行只在任务开始时写一次，
                                //   而 BGI 每秒写 ~11 行，尾部 300 行只覆盖约 30 秒 ——
                                //   过了这个窗口就永远解析不到任务名/路线，
                                //   表现就是"路线名显示一下又消失"（用户实测）。
                                //   这里读 4000 行 / 最多 16MB，与左侧状态面板一致。
                                var fresh = new BgiStatus();
                                string text = BgiLogWatcher.TailLinesDeep(
                                    inst.LogFile, 4000, 16L * 1024 * 1024);
                                fresh.Update(text);

                                var (errs, warns, fails) = BgiLogWatcher.CountDailyErrors(inst.LogFile);
                                if (errs > fresh.ErrorCount) fresh.ErrorCount = errs;
                                if (warns > fresh.WarnCount) fresh.WarnCount = warns;
                                if (fails > fresh.FailCount) fresh.FailCount = fails;

                                // ★ 兜底：如果这次没解析到任务/路线，沿用该文件上一次解析到的结构字段。
                                //   日志滚出深读窗口后仍能保持显示，不会"闪一下就没了"。
                                lock (_statusCache)
                                {
                                    if (_structCache.TryGetValue(inst.LogFile, out var prev) && prev != null)
                                        fresh.MergeStructuralFrom(prev);
                                }
                                var keep = new BgiStatus();
                                keep.MergeStructuralFrom(fresh);
                                lock (_statusCache) _structCache[inst.LogFile] = keep;

                                entry = new StatusCacheEntry { Key = key, Status = fresh };
                                _statusCache[inst.LogFile] = entry;
                            }
                        }

                        var st = entry.Status;
                        results.Add((row, st, true, true));
                    }

                    Dispatcher.InvokeAsync(() =>
                    {
                        foreach (var (row, st, running, ranToday) in results)
                        {
                            try
                            {
                                var accountState = AccountStateCenter.GetByUser(row.User);
                                if (accountState != null)
                                {
                                    var current = accountState.Groups.All().Where(g => g.State == ScriptGroupState.Running)
                                        .OrderByDescending(g => g.StartedAt).FirstOrDefault()
                                        ?? accountState.Groups.All().OrderByDescending(g => g.StartedAt).FirstOrDefault();
                                    if (current != null && current.Name.Length > 0)
                                    {
                                        st.ConfigGroup = current.Name;
                                        st.PicksRoute = current.Name;
                                        st.TaskName = "";
                                    }
                                }
                                // ★ 没跑 BGI：分两种说法
                                //   今天跑过（有当天日志）→ "今天运行过，当前已退出"
                                //   今天没跑过（无日志）  → "今日还未运行 BetterGI"
                                if (!running)
                                {
                                    if (row.Line1 != null && row.Line1.Text != row.User)
                                        row.Line1.Text = row.User;
                                    if (row.Line2 != null)
                                    {
                                        string txt = ranToday
                                            ? "今天运行过，当前已退出"
                                            : "今日还未运行 BetterGI";
                                        if (row.Line2.Text != txt) row.Line2.Text = txt;
                                        row.Line2.Foreground = new System.Windows.Media.SolidColorBrush(
                                            ranToday
                                                ? System.Windows.Media.Color.FromRgb(0xF0, 0xC4, 0x6B)   // 橙：跑过但退出了
                                                : System.Windows.Media.Color.FromRgb(0x8B, 0x9C, 0xC8)); // 灰：今天没跑
                                        row.Line2.Visibility = Visibility.Visible;
                                    }
                                    if (row.Line3 != null)
                                    {
                                        row.Line3.Text = "";
                                        row.Line3.Visibility = Visibility.Collapsed;   // ★ 没内容就彻底不占高度
                                    }
                                    continue;
                                }

                                // ① 账号 · 配置组/脚本（账号处已带备注名）
                                // ★ 备注名可能在建卡之后才填（中控台改配置），
                                //   所以每次更新都重新取一次，而不是只在重建时算。
                                string head = row.User;
                                string group = st.GroupDisplay;
                                // ★ 只有"当前动作"没有配置组/路线时，用它当主信息
                                if (group == "—" && st.CurrentAction.Length > 0)
                                    group = st.CurrentAction;
                                if (!string.IsNullOrEmpty(group) && group != "—")
                                    head += "  ·  " + group;
                                if (row.Line1 != null && row.Line1.Text != head) row.Line1.Text = head;

                                // ★ 有 BGI 进程，但今天这份日志里没有任何状态信息
                                //   （刚启动、或还没开始跑任务）→ 也要给个明确说法。
                                if (!st.HasData)
                                {
                                    if (row.Line2 != null)
                                    {
                                        // 有"当前动作"就显示动作，否则才是等待提示
                                        string waitText = st.CurrentAction.Length > 0
                                            ? st.CurrentAction
                                            : "BGI 已启动，等待任务开始";
                                        if (row.Line2.Text != waitText)
                                            row.Line2.Text = waitText;
                                        row.Line2.Foreground = new System.Windows.Media.SolidColorBrush(
                                            System.Windows.Media.Color.FromRgb(0xF0, 0xC4, 0x6B));
                                        row.Line2.Visibility = Visibility.Visible;
                                    }
                                    if (row.Line3 != null)
                                    {
                                        row.Line3.Text = "";
                                        row.Line3.Visibility = Visibility.Collapsed;   // ★ 没内容就彻底不占高度
                                    }
                                    continue;
                                }

                                // 异常状态：轮次照常显示在第二行，
                                // **失败原因单独放第三行（红色）** —— 与单账号详情一致。
                                if (st.HasProblem)
                                {
                                    string rl = "";
                                    if (!string.IsNullOrEmpty(st.Round)) rl = "第 " + st.Round + " 轮";
                                    if (!string.IsNullOrEmpty(st.RoleName))
                                        rl += (rl.Length > 0 ? "  ·  " : "") + st.RoleName;
                                    if (row.Line2 != null)
                                    {
                                        if (row.Line2.Text != rl) row.Line2.Text = rl;
                                        row.Line2.Foreground = (System.Windows.Media.Brush)FindResource("CyanBrush");
                                        row.Line2.Visibility = rl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                                    }

                                    string prob = st.ProblemText;
                                    if (row.Line3 != null)
                                    {
                                        if (row.Line3.Text != prob) row.Line3.Text = prob;
                                        row.Line3.Foreground = st.FailedGroup.Length > 0 || st.LastError.Length > 0
                                            ? new System.Windows.Media.SolidColorBrush(
                                                System.Windows.Media.Color.FromRgb(0xFF, 0x7B, 0x7B))
                                            : new System.Windows.Media.SolidColorBrush(
                                                System.Windows.Media.Color.FromRgb(0x8B, 0x9C, 0xC8));
                                    }
                                    continue;
                                }

                                // ② 轮次/任务（有轮次显示轮次，无轮次显示任务名）
                                // ★ 兜底不要再写"等待日志中的状态"：那会让人以为没解析到，
                                //   实际可能只是这类任务本身没有轮次概念（地图追踪等）。
                                //   有任务名就显示任务名，实在没有就留空。
                                string roundLine = st.SafeRoundDisplay;
                                if (string.IsNullOrWhiteSpace(roundLine))
                                    roundLine = (st.GroupDisplay == "—") ? "" : st.GroupDisplay;
                                if (row.Line2 != null)
                                {
                                    if (row.Line2.Text != roundLine) row.Line2.Text = roundLine;
                                    row.Line2.Foreground = (System.Windows.Media.Brush)FindResource("CyanBrush");
                                    row.Line2.Visibility = Visibility.Visible;
                                }

                                // ②b 今日狗粮收益（右上角）
                                //     ★ 数据源是狗粮脚本自己写的 records 文件，
                                //       不是从日志累加 —— 准确且不受日志滚动影响。
                                //       达到路线参考上限（A=13万/B=5万）后中心会停止统计。
                                if (row.TodayLine != null)
                                {
                                    var st2 = accountState;
                                    string todayText = FormatTodayForAccount(st2);
                                    if (row.TodayLine.Text != todayText)
                                        row.TodayLine.Text = todayText;
                                    row.TodayLine.Visibility = todayText.Length > 0
                                        ? Visibility.Visible : Visibility.Collapsed;
                                }

                                // ③ 路线 / 进度（三种格式都由 RouteDisplay 统一处理）
                                string routeLine = st.RouteDisplay;
                                if (!string.IsNullOrEmpty(st.EtaDisplay))
                                    routeLine += (routeLine == "—" ? "" : "  ·  ") + st.EtaDisplay;
                                if (routeLine == "—") routeLine = "";
                                if (row.Line3 != null)
                                {
                                    if (row.Line3.Text != routeLine) row.Line3.Text = routeLine;
                                    row.Line3.Foreground = (System.Windows.Media.Brush)FindResource("TextMuted");
                                    // ★ 没有路线信息就折叠，避免卡片留一块空白
                                    row.Line3.Visibility = routeLine.Length > 0
                                        ? Visibility.Visible : Visibility.Collapsed;
                                }
                            }
                            catch { }
                        }
                    });
                }
                catch { }
            });
        }

        // ── BGI 实时日志 ─────────────────────────────────────────────────

        private SessionInfo? _logSession;
        private string _logText = "";
        private string _logFile = "";
        private DateTime _logLastWrite = DateTime.MinValue;

        /// <summary>刷新当前选中会话的 BGI 日志（内容有变化才更新界面）。</summary>
        /// <summary>
        /// 切换/刷新当前选中会话的 BGI 日志。
        ///
        /// ★ 用 LogFollower（FileSystemWatcher）跟随文件：
        ///   BGI 一写入就被唤醒追加，不再靠 3 秒轮询 —— 这才是"实时"。
        ///   只在切换会话或发现 BGI 进程变化时重建跟随器。
        /// </summary>
        private void RefreshSelectedLog(bool force = false)
        {
            var sess = _logSession;
            if (sess == null) return;

            _ = Task.Run(() =>
            {
                try
                {
                    var map = BgiLogWatcher.Discover();
                    if (!map.TryGetValue(sess.Id, out var inst))
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            StopFollower();
                            LogMeta.Text = sess.UserName + " · 未发现 BetterGI";
                            LogText.Text = "该会话中没有正在运行的 BetterGI 进程。\n\n" +
                                           "启动 BGI 后这里会自动显示它的实时日志。";
                            LogTitle.Text = "BGI 实时日志";
                        });
                        return;
                    }

                    // 目标文件没变 + 跟随器还活着 → 什么都不用做（跟随器会自己推）
                    if (!force && inst.LogFile == _logFile && _follower != null)
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            LogMeta.Text = "PID " + inst.Pid + " · 实时跟随中";
                        });
                        return;
                    }

                    // 重新建立跟随器（换会话 / 换文件 / 首次）
                    _logFile = inst.LogFile;
                    int pid = inst.Pid;
                    string user = sess.UserName;

                    Dispatcher.InvokeAsync(() =>
                    {
                        StopFollower();
                        LogTitle.Text = "BGI 实时日志 · " + user;
                        LogMeta.Text = "PID " + pid + " · 实时跟随中";
                        LogText.Text = "";

                        try
                        {
                            _follower = new LogFollower(
                                _logFile, Dispatcher,
                                chunk =>
                                {
                                    // ① 解析运行状态并刷新面板
                                    _bgiStatus.Update(chunk);
                                    ApplyStatusToUi();

                                    // ② 原始日志仍然累积（默认折叠，排查时展开）
                                    LogText.Text += chunk;
                                    TrimLogText();
                                    try { LogScroll.ScrollToEnd(); } catch { }
                                },
                                initialLines: 200);   // 回填多一些，保证状态能解析到
                        }
                        catch (Exception ex)
                        {
                            LogText.Text = "无法跟随日志：" + ex.Message;
                        }
                    });
                }
                catch { }
            });
        }

        /// <summary>日志文本最多保留的行数（超出丢弃最早的部分）。</summary>
        private const int MaxLogLines = 400;

        /// <summary>裁剪日志文本，避免无限增长。</summary>
        private void TrimLogText()
        {
            try
            {
                string t = LogText.Text;
                if (t.Length < 60000) return;          // 字符数上限，约 400 行
                int cut = t.IndexOf('\n', t.Length / 3);
                if (cut > 0) LogText.Text = t.Substring(cut + 1);
            }
            catch { }
        }

        /// <summary>
        /// 每个日志文件的状态解析缓存（按"最后写入时间+长度"判断是否需要重算）。
        /// 避免每秒重复扫描几十 MB 的日志。
        /// </summary>
        /// <summary>
        /// 主界面右上角的"今日"信息：走哪条线 + 今日收益。
        /// ★ 收益来自狗粮脚本的 records（准确），不是日志累加。
        /// </summary>
        private static string FormatToday(DailyStats? td)
        {
            try
            {
                // 这里只有 DailyStats，真正的路线与收益在 AccountState 上，
                // 由调用方通过 FormatTodayForAccount 提供。
                if (td == null) return "";
                var parts = new List<string>();
                if (td.PickTotal > 0) parts.Add("拾取 " + FormatNum(td.PickTotal));
                return string.Join(" · ", parts);
            }
            catch { return ""; }
        }

        /// <summary>按账号状态格式化"今日：线路 + 收益"。</summary>
        private static string FormatTodayForAccount(AccountState? st)
        {
            if (st == null) return "";
            var parts = new List<string>();

            // ① 今日走哪条线（只有当日记录才显示）
            if (st.DogfoodValid)
            {
                string route = st.DogfoodRoute == DogfoodEndingRoute.EndingA
                    ? "收尾A·联机"
                    : "收尾B·自跑";
                parts.Add(route);
            }

            // ② 今日收益
            if (st.DogfoodExp > 0) parts.Add("经验 +" + FormatNum(st.DogfoodExp));
            if (st.DogfoodMora > 0) parts.Add("摩拉 +" + FormatNum(st.DogfoodMora));

            return string.Join(" · ", parts);
        }

        /// <summary>大数字加千分位（摩拉量级到千万，不加分隔看不清）。</summary>
        private static string FormatNum(long v)
            => v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// 按日志文件记住"上一次解析到的结构字段"（任务名/路线/配置组/轮次…）。
        /// ★ 用途：任务启动行（"开始执行地图追踪任务"）只在任务开始时写一次，
        ///   而 BGI 每秒写约 11 行，滚动很快。若那段内容滚出读取窗口，
        ///   界面就会"闪一下变空"。这里保留上次结果用于补齐。
        /// </summary>
        private readonly Dictionary<string, BgiStatus> _structCache
            = new Dictionary<string, BgiStatus>();

        private sealed class StatusCacheEntry
        {
            public string Key = "";
            public BgiStatus Status = new BgiStatus();
        }

        private readonly Dictionary<string, StatusCacheEntry> _statusCache
            = new Dictionary<string, StatusCacheEntry>();

        private LogFollower? _follower;

        /// <summary>从日志解析出的运行状态（跟随器推送增量时持续更新）。</summary>
        private BgiStatus _bgiStatus = new BgiStatus();

        // ── 左侧运行状态：只跟随主会话 ──────────────────────────────────────

        private LogFollower? _statusFollower;
        private string _statusFile = "";

        /// <summary>
        /// 刷新左侧运行状态。
        /// ★ 选中哪个账号就跟随哪个账号的日志。
        ///   主会话不参与单账号跟随（它显示的是总览，见 ApplyLeftPanelMode）。
        /// </summary>
        /// <summary>当前正在跟随的会话号（0 = 未跟随）。用于避免重复启动导致状态被反复重置。</summary>
        private int _statusSessionId;

        private void StartStatusFollowFor(SessionInfo sess)
        {
            if (sess == null) return;

            // ★ 已经在跟随同一个会话 → 直接返回。
            //   原来每 2 秒定时器都会进来一次，走到"文件没变"分支也会重写 StatusMeta，
            //   而更糟的是：期间若 Discover() 一时读不到 BGI，就会把状态重置成"—"，
            //   造成界面"一会儿文字一会儿横杠"地闪。
            //
            // ★ 但不能一刀切返回：BGI 跨天会把日志切到新文件（23:59:59 → 00:00:00），
            //   如果死守旧文件，状态就永远停在昨天。所以这里每轮都轻量检查
            //   当前应该跟的文件名，变了就重连。
            if (_statusSessionId == sess.Id && _statusFollower != null)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        var map = BgiLogWatcher.Discover();
                        if (!map.TryGetValue(sess.Id, out var cur)) return;
                        if (cur.LogFile == _statusFile) return;   // 没变

                        // 跨天切换：换文件
                        SoulLink.Controls.RdpConsole.Debug(
                            "日志跨天切换：会话 " + sess.Id + " → " + System.IO.Path.GetFileName(cur.LogFile));
                        _statusFollower?.Dispose();
                        _statusFollower = null;
                        _statusSessionId = 0;
                    }
                    catch { }
                });
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    int sid = sess.Id;
                    string user = sess.UserName;

                    var map = BgiLogWatcher.Discover();
                    if (!map.TryGetValue(sid, out var inst))
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            StopStatusFollower();
                            StatusMeta.Text = user + " · 未运行 BGI";
                            StatusGroup.Text = "—";
                            StatusRound.Text = "—";
                            StatusRoute.Text = "—";
                            StatusEta.Text = "该账号没有正在运行的 BetterGI";
                        });
                        return;
                    }

                    // 文件没变 + 跟随器活着 → 不重建
                    if (inst.LogFile == _statusFile && _statusFollower != null)
                    {
                        Dispatcher.InvokeAsync(() => StatusMeta.Text = user + " · 实时跟随中");
                        return;
                    }

                    // ★ 用局部变量传文件路径：_statusFile 会被 StopStatusFollower() 清空，
                    //   原来直接把字段传进 new LogFollower，导致目录为空 →
                    //   "The directory name is invalid"，跟随器永远建不起来。
                    string logFile = inst.LogFile;
                    int pid = inst.Pid;

                    // ★ 竞态修复：会话号必须**立刻**记下来。
                    //   原来只在 Dispatcher.InvokeAsync 内部赋值，而那是异步的；
                    //   2 秒定时器如果在这之前进来，会看到 _statusSessionId 还是 0，
                    //   于是又走一遍"发现 BGI → 新建跟随器 → 清空状态"，
                    //   表现就是状态闪一下就变回横杠、越刷越乱。
                    _statusSessionId = sid;

                    Dispatcher.InvokeAsync(() =>
                    {
                        StopStatusFollower();             // 内部会把 _statusSessionId/_statusFile 清 0
                        _statusSessionId = sid;           // ★ 清完必须重新写回
                        _statusFile = logFile;            // ★ 同上，用局部变量恢复
                        _bgiStatus = new BgiStatus();

                        // ★ 先用该文件上次解析到的结构字段预填，避免"闪一下横杠"。
                        //   换账号/重连时如果缓存里有记录，就立刻显示上次的任务名与路线，
                        //   深读结果回来后再覆盖成最新的。
                        try
                        {
                            lock (_statusCache)
                            {
                                if (_structCache.TryGetValue(logFile, out var cached) && cached != null)
                                    _bgiStatus.MergeStructuralFrom(cached);
                            }
                        }
                        catch { }

                        _statusEverResolved = _bgiStatus.HasData;   // 有缓存就不算"未解析"
                        StatusPicksBox.Visibility = Visibility.Collapsed;
                        ApplyStatusToUi();
                        StatusMeta.Text = user + " · PID " + pid;

                        try
                        {
                            _statusFollower = new LogFollower(
                                logFile, Dispatcher,
                                chunk =>
                                {
                                    _bgiStatus.Update(chunk);
                                    ApplyStatusToUi();
                                    // ★ 每次有新增日志，都安排一次"全量重解析"。
                                    //   原因：配置组 / 任务名只在任务开始时写一次，
                                    //   如果那段内容不在初始回填范围内（文件 33MB，
                                    //   回填只读最后 512KB），增量里永远拿不到 →
                                    //   界面一直停在"等待日志中的状态信息"（用户实测）。
                                    ScheduleStatusRebuild(logFile);
                                },
                                initialLines: 400,
                                deepBackfill: true);   // ★ 状态行可能埋在几十万行之前（实测 20261107）

                            // ★ 每日报错统计：挂上跟随器后，后台扫一遍全天日志。
                            //   增量解析只能覆盖最近内容，无法回溯全天。
                            string lf = logFile;
                            _ = Task.Run(() =>
                            {
                                try
                                {
                                    var (errs, warns, fails) = BgiLogWatcher.CountDailyErrors(lf);
                                    // 拾取也要扫全天，并且按配置组分类
                                    var (byGroup, lastGroup) = BgiLogWatcher.CountDailyPicksByGroup(lf);
                                    Dispatcher.InvokeAsync(() =>
                                    {
                                        _bgiStatus.ErrorCount = errs;
                                        _bgiStatus.WarnCount = warns;
                                        _bgiStatus.FailCount = fails;

                                        // 全天分组数据
                                        _bgiStatus.DailyByGroup.Clear();
                                        foreach (var g in byGroup)
                                            _bgiStatus.DailyByGroup[g.Key] = g.Value;

                                        // 本轮 = 最后一个（当前）配置组的拾取
                                        if (!string.IsNullOrEmpty(lastGroup)
                                            && byGroup.TryGetValue(lastGroup, out var cur))
                                        {
                                            _bgiStatus.RoutePicks.Clear();
                                            foreach (var kv in cur) _bgiStatus.RoutePicks[kv.Key] = kv.Value;
                                            _bgiStatus.PicksRoute = lastGroup;
                                        }
                                        ApplyStatusToUi();
                                    });
                                }
                                catch { }
                            });
                        }
                        catch (Exception ex)
                        {
                            // ★ 原来这里是 catch { }，把创建失败吞掉了，
                            //   结果 _statusFollower 一直是 null，守卫失效、
                            //   状态永远显示不出来。现在把原因记下来。
                            SoulLink.Controls.RdpConsole.Debug("状态跟随器创建异常：" + ex.GetType().Name
                                + " - " + ex.Message);
                            StatusEta.Text = "跟随失败：" + ex.Message;
                        }
                    });
                }
                catch (Exception ex)
                {
                    SoulLink.Controls.RdpConsole.Debug("[状态] 跟随异常：" + ex.Message);
                }
            });
        }

        private void StopStatusFollower()
        {
            try { _statusFollower?.Dispose(); } catch { }
            _statusFollower = null;
            _statusFile = "";
            _statusSessionId = 0;
        }

        /// <summary>拾取面板是否已展开全天详情。</summary>
        private bool _picksExpanded;

        /// <summary>点击"本轮拾取"标题：展开/收起全天按配置组的统计。</summary>
        private void PicksHeader_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _picksExpanded = !_picksExpanded;
            DailyPicksPanel.Visibility = _picksExpanded ? Visibility.Visible : Visibility.Collapsed;
            PicksExpandHint.Text = _picksExpanded ? "▴ 收起" : "▾ 展开全天";
            if (_picksExpanded) RefreshDailyPicksText();
        }

        /// <summary>
        /// 刷新"全天 / 按配置组"列表。
        /// ★ 用 StackPanel 动态生成，而不是一个 TextBlock 拼多行 ——
        ///   这样组名可以用粗体+亮色、材料用细体+暗色，视觉上能分清层级。
        /// </summary>
        private void RefreshDailyPicksText()
        {
            try
            {
                DailyPicksList.Children.Clear();

                if (_bgiStatus.DailyByGroup.Count == 0)
                {
                    DailyPicksList.Children.Add(new System.Windows.Controls.TextBlock
                    {
                        Text = "今天还没有拾取记录。",
                        FontSize = 10,
                        Foreground = (System.Windows.Media.Brush)FindResource("TextMuted")
                    });
                    return;
                }

                // 按总件数从多到少排配置组，主要的排前面
                var groups = new List<(string name, Dictionary<string, int> bag, int total)>();
                foreach (var g in _bgiStatus.DailyByGroup)
                {
                    int total = 0;
                    foreach (var kv in g.Value) total += kv.Value;
                    if (total > 0) groups.Add((g.Key, g.Value, total));
                }
                groups.Sort((a, b) => b.total.CompareTo(a.total));

                bool first = true;
                foreach (var (name, bag, total) in groups)
                {
                    // 组名：粗体 + 亮色，独占一行
                    var title = new System.Windows.Controls.TextBlock
                    {
                        Text = name + "　" + total + " 件",
                        FontSize = 10,
                        FontWeight = System.Windows.FontWeights.SemiBold,
                        Foreground = (System.Windows.Media.Brush)FindResource("CyanBrush"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, first ? 0 : 10, 0, 2)
                    };
                    DailyPicksList.Children.Add(title);
                    first = false;

                    // 材料：细体 + 暗色，可换行
                    var items = new List<(string n, int c)>();
                    foreach (var kv in bag) items.Add((kv.Key, kv.Value));
                    items.Sort((a, b) => b.c.CompareTo(a.c));

                    var parts = new List<string>();
                    foreach (var it in items)
                        parts.Add(it.c > 1 ? (it.n + "×" + it.c) : it.n);

                    var body = new System.Windows.Controls.TextBlock
                    {
                        Text = string.Join(" · ", parts),
                        FontSize = 10.5,
                        FontWeight = System.Windows.FontWeights.Normal,
                        Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary"),
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 17
                    };
                    DailyPicksList.Children.Add(body);
                }
            }
            catch { }
        }

        /// <summary>
        /// 把解析出的状态刷到界面。
        ///
        /// ★ 闪烁修复：原实现在没解析到数据时每次把文字重置成固定文案，
        ///   而日志还在持续到达、状态时有时无，导致界面反复在两种文案间跳。
        ///   现在改成"只在内容真的变化时才写"（比较后再赋值），
        ///   WPF 的 TextBlock 也就不会反复重排。
        /// </summary>
        private void SetText(System.Windows.Controls.TextBlock tb, string value)
        {
            if (tb.Text != value) tb.Text = value;
        }

        /// <summary>是否已经成功解析出过一次状态。有了就不再回退成占位符。</summary>
        private bool _statusEverResolved;

        // ── 全量重解析（应对"关键信息只在任务开始时写一次"）──

        private DateTime _lastStatusRebuild = DateTime.MinValue;
        private bool _statusRebuildRunning;

        /// <summary>
        /// 安排一次全量重解析（节流：最多 3 秒一次）。
        /// 从文件尾部读一大段（含配置组行），重新构建 BgiStatus。
        /// </summary>
        private void ScheduleStatusRebuild(string logFile)
        {
            if (_statusRebuildRunning) return;
            if ((DateTime.Now - _lastStatusRebuild).TotalSeconds < 3) return;

            _lastStatusRebuild = DateTime.Now;
            _statusRebuildRunning = true;

            string lf = logFile;
            _ = Task.Run(() =>
            {
                try
                {
                    // 读足够大的一段（16MB 上限），保证能覆盖任务开始那几行
                    string text = BgiLogWatcher.TailLinesDeep(lf, 4000, 16L * 1024 * 1024);
                    if (string.IsNullOrEmpty(text)) return;

                    var fresh = new BgiStatus();
                    fresh.Update(text);

                    Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            // 换个账号了就别写回去
                            if (!string.Equals(_statusFile, lf, StringComparison.OrdinalIgnoreCase)) return;

                            // ★ 只补齐"结构性"字段（配置组/任务名/一条龙/轮次/路线），
                            //   拾取与报错统计由别的流程维护，避免互相覆盖。
                            _bgiStatus.MergeStructuralFrom(fresh);
                            ApplyStatusToUi();
                        }
                        catch { }
                    });
                }
                catch { }
                finally { _statusRebuildRunning = false; }
            });
        }

        private void ApplyStatusToUi()
        {
            try
            {
                var currentState = _sessions.FirstOrDefault(x => x.Session.Id == _statusSessionId)?.Session;
                if (currentState != null)
                {
                    var accountState = AccountStateCenter.GetByUser(currentState.UserName);
                    var current = accountState?.Groups.All().Where(g => g.State == ScriptGroupState.Running)
                        .OrderByDescending(g => g.StartedAt).FirstOrDefault()
                        ?? accountState?.Groups.All().OrderByDescending(g => g.StartedAt).FirstOrDefault();
                    if (current != null && current.Name.Length > 0)
                    { _bgiStatus.ConfigGroup = current.Name; _bgiStatus.PicksRoute = current.Name; _bgiStatus.TaskName = ""; }
                }
                if (!_bgiStatus.HasData)
                {
                    // ★ 关键：一旦解析成功过，就**不要再退回横杠**。
                    //   日志是持续到达的，某一批增量里没有状态行很正常；
                    //   之前每次没解析到就把文字重置成 "—"，造成"一会儿文字一会儿横杠"。
                    if (_statusEverResolved) return;

                    // 只有从来没解析到过，才显示占位
                    SetText(StatusGroup, "—");
                    SetText(StatusRound, "—");
                    SetText(StatusRoute, "—");
                    SetText(StatusEta, "等待日志中的状态信息…");
                    StatusProblemBox.Visibility = Visibility.Collapsed;
                    StatusPicksBox.Visibility = Visibility.Collapsed;
                    return;
                }

                _statusEverResolved = true;

                // 三类任务都能正确显示：
                //   有轮次 → "配置组 / 第 2/3 轮 · 思 / 路线"
                //   无轮次 → "每日采集 / 每日采集 / 03-霜仙花… 共1440条"
                //   一条龙 → "灭绝提瓦特 / 灭绝提瓦特 / 一条龙：灭绝提瓦特"
                SetText(StatusGroup, _bgiStatus.GroupDisplay);
                SetText(StatusRound, string.IsNullOrWhiteSpace(_bgiStatus.SafeRoundDisplay)
                    ? "—" : _bgiStatus.SafeRoundDisplay);
                SetText(StatusRoute, _bgiStatus.RouteDisplay);
                SetText(StatusEta, _bgiStatus.EtaDisplay);

                // 今日报错：只显示错误/失败（纯警告太多，不占版面）
                if (_bgiStatus.ErrorLevel >= 2)
                {
                    string et = "错误 " + _bgiStatus.ErrorCount + " 次";
                    if (_bgiStatus.FailCount > 0) et += " · 失败 " + _bgiStatus.FailCount + " 次";
                    SetText(StatusError, et);
                    StatusError.Foreground = _bgiStatus.FailCount > 0
                        ? new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(0xFF, 0x7B, 0x7B))
                        : new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(0xFF, 0xD5, 0x4A));
                    StatusErrorTitle.Visibility = Visibility.Visible;
                    StatusError.Visibility = Visibility.Visible;
                }
                else
                {
                    StatusErrorTitle.Visibility = Visibility.Collapsed;
                    StatusError.Visibility = Visibility.Collapsed;
                }

                // 本轮拾取（当前配置组）：有拾取才显示整块
                string picks = _bgiStatus.PicksDisplay;
                if (picks.Length > 0)
                {
                    string gname = string.IsNullOrEmpty(_bgiStatus.PicksRoute)
                        ? "本次拾取" : ("本轮拾取 · " + _bgiStatus.PicksRoute);
                    SetText(StatusPicksTitle, gname + "　" + _bgiStatus.PickTotal + " 件");
                    SetText(StatusPicks, picks);
                    StatusPicksBox.Visibility = Visibility.Visible;
                    if (_picksExpanded) RefreshDailyPicksText();
                }
                else
                {
                    StatusPicksBox.Visibility = Visibility.Collapsed;
                }

                // ★ 异常单独占一行（红色框）：失败原因比进度更需要被看到，
                //   混在"当前路线"里会被忽略。
                if (_bgiStatus.HasProblem)
                {
                    var lines = new List<string>();
                    if (!string.IsNullOrEmpty(_bgiStatus.FailedGroup))
                        lines.Add("配置组「" + _bgiStatus.FailedGroup + "」执行失败");
                    if (!string.IsNullOrEmpty(_bgiStatus.LastError))
                        lines.Add(_bgiStatus.LastError);
                    if (lines.Count == 0 && _bgiStatus.TaskEnded)
                        lines.Add("任务已结束，当前没有在跑");

                    SetText(StatusProblem, string.Join(Environment.NewLine, lines));
                    StatusProblemBox.Visibility = Visibility.Visible;
                }
                else
                {
                    StatusProblemBox.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
        }

        /// <summary>停止当前日志跟随。</summary>
        private void StopFollower()
        {
            try { _follower?.Dispose(); } catch { }
            _follower = null;
        }

        public MainWindow? MainWindowRef { get; set; }

        // ══════════ 执行 ══════════

        private async void RunSelected_Click(object sender, RoutedEventArgs e)
        {
            if (ActionList.SelectedItem is not ActionVm vm) return;
            await RunAsync(vm);
        }

        private async void RunAction_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is ActionVm vm)
            {
                ActionList.SelectedItem = vm;
                await RunAsync(vm);
            }
        }

        private async Task RunAsync(ActionVm vm)
        {
            if (_busy) return;
            _busy = true;
            // 执行结果统一走日志总线 + 顶部状态提示（底部操作区已移除）
            SetRunHint("执行中…「" + vm.Model.Name + "」", ok: null);

            try
            {
                LogBus.Write(LogLevel.Run, "执行「" + vm.Model.Name + "」");
                var result = await Task.Run(() => ActionService.ExecuteAsync(vm.Model, App.Config));
                LogBus.Write(result.Ok ? LogLevel.Ok : LogLevel.Error, result.Message,
                    result.Command + Environment.NewLine + "耗时 " + result.ElapsedMs + " ms");

                SetRunHint((result.Ok ? "✓ " : "✕ ") + vm.Model.Name + " · " + result.Message, result.Ok);
                vm.Refresh();
                RefreshBadges();

                if (result.Ok && vm.Model.Kind != ActionKind.Launch)
                    RefreshSessions();
            }
            catch (Exception ex)
            {
                LogBus.Error("执行失败：" + ex.Message);
                SetRunHint("✕ " + ex.Message, false);
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>
        /// 在执行按钮旁边给一句即时反馈。
        /// ok = null 表示"进行中"（灰色），true/false 表示成功/失败（绿/红）。
        /// </summary>
        private void SetRunHint(string text, bool? ok)
        {
            try
            {
                RunHint.Text = text;
                RunHint.Foreground = (Brush)FindResource(
                    ok == null ? "TextMuted" : (ok.Value ? "MintBrush" : "RoseBrush"));
                RunHint.Visibility = Visibility.Visible;

                // 成功提示 6 秒后自动淡出，避免一直占位
                if (ok != null)
                {
                    var t = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromSeconds(6) };
                    t.Tick += (_, __) =>
                    {
                        t.Stop();
                        try { RunHint.Visibility = Visibility.Collapsed; } catch { }
                    };
                    t.Start();
                }
            }
            catch { }
        }

        // ══════════ 编辑动作 ══════════

        private void NewAction_Click(object sender, RoutedEventArgs e) => ShowEditor(null);

        private void EditAction_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is ActionVm vm) ShowEditor(vm.Model);
        }

        /// <summary>
        /// 暂停 / 恢复定时执行。
        /// ★ 暂停只是把 Paused 置 true，不动原有计划；
        ///   恢复时如果计划任务已被手动删除，会尝试重建。
        /// </summary>
        private void PauseSchedule_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not ActionVm vm) return;
            var spec = vm.Model.Schedule;
            if (spec == null) return;

            try
            {
                spec.Paused = !spec.Paused;
                spec.PauseReason = spec.Paused ? "手动暂停" : "";
                ConfigStore.Save(App.Config);

                LogBus.Ok(spec.Paused
                    ? "已暂停「" + vm.Model.Name + "」的定时执行"
                    : "已恢复「" + vm.Model.Name + "」的定时执行");

                ReloadActions();
            }
            catch (Exception ex)
            {
                LogBus.Error("切换定时状态失败：" + ex.Message);
            }
        }



        private void ShowEditor(ActionItem? existing)
        {
            var dlg = new ActionEditorWindow(existing, App.Config) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true)
            {
                if (existing == null)
                {
                    App.Config.Actions.Add(dlg.Result);
                }
                else
                {
                    // ★ 编辑已有动作：把窗口里改好的值**写回原对象**。
                    //   原来这里什么都不做，只有新建才保存，导致
                    //   "编辑后保存无效"（定时设置尤其明显，用户实测）。
                    //   注意保留 Id（dlg.Result 里已带上原 Id）。
                    var target = App.Config.Actions.FirstOrDefault(a => a.Id == existing.Id);
                    if (target != null)
                    {
                        target.Name = dlg.Result.Name;
                        target.AccountId = dlg.Result.AccountId;
                        target.ExePath = dlg.Result.ExePath;
                        target.Arguments = dlg.Result.Arguments;
                        target.Kind = dlg.Result.Kind;
                        target.DelaySeconds = dlg.Result.DelaySeconds;
                        target.StopBgiFirst = dlg.Result.StopBgiFirst;
                        target.CloseGenshin = dlg.Result.CloseGenshin;
                        target.StopWaitSeconds = dlg.Result.StopWaitSeconds;
                        target.Schedule = dlg.Result.Schedule;
                    }
                    else
                    {
                        App.Config.Actions.Add(dlg.Result);
                    }
                }

                ReloadActions();
                ConfigStore.Save(App.Config);
                ActionList.SelectedItem = _actions.FirstOrDefault(v => v.Model.Id == dlg.Result.Id);

            }
        }

        /// <summary>

        /// <summary>

        // ══════════ 自动化流程 ══════════

        /// <summary>刷新当前账号的自动化流程列表。</summary>
        private void RefreshAutomationList()
        {
            try
            {
                if (AutomationList == null) return;
                AutomationList.Children.Clear();

                var sess = _logSession;
                if (sess == null)
                {
                    AutomationMeta.Text = "（先选一个账号）";
                    return;
                }

                // 找到该账号（按用户名匹配配置里的账号）
                Account? account = null;
                foreach (var a in App.Config.Accounts)
                    if (string.Equals(a.UserName, sess.UserName, StringComparison.OrdinalIgnoreCase))
                    { account = a; break; }

                if (account == null)
                {
                    AutomationMeta.Text = "该账号还没有配置";
                    return;
                }

                // 该账号的所有流程
                var flows = new List<AutomationFlow>();
                foreach (var f in App.Config.Automations)
                    if (f.AccountId == account.Id) flows.Add(f);

                AutomationMeta.Text = flows.Count == 0 ? "" : ("共 " + flows.Count + " 个流程");
                if (flows.Count == 0)
                    AutomationList.Children.Add(new TextBlock { Text = "暂无自动化任务", FontSize = 10.5, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(4,5,4,5) });

                foreach (var f in flows)
                    AutomationList.Children.Add(BuildAutomationCard(f, account));
            }
            catch { }
        }

        private Border BuildAutomationCard(AutomationFlow f, Account account)
        {
            bool paused = f.Paused || !f.Enabled;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 暂停灰 / END 琥珀 / 有触点成立青绿 / 全灭灰。
            bool anyLit = f.RuntimeStates.Any(kv => kv.Value.Value && f.FindNode(kv.Key)?.IsContact == true);
            Color dotColor = paused ? Color.FromRgb(0x8B,0x9C,0xC8)
                : f.IsDailyComplete ? Color.FromRgb(0xFF,0xB1,0x4D)
                : anyLit ? Color.FromRgb(0x41,0xF0,0xC0) : Color.FromRgb(0x65,0x75,0x91);

            var dot = new Ellipse
            {
                Width = 8, Height = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(dotColor)
            };
            Grid.SetColumn(dot, 0);
            grid.Children.Add(dot);

            // 名称 + 说明
            var sp = new StackPanel { Margin = new Thickness(9, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock
            {
                Text = f.DisplayName,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimary"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            sp.Children.Add(new TextBlock
            {
                Text = AutomationSummary(f),
                FontSize = 9.5,
                Foreground = (Brush)FindResource("TextMuted"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(sp, 1);
            grid.Children.Add(sp);

            // 按钮组
            var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var btnPause = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Width = 26, Height = 26,
                Content = new TextBlock
                {
                    Text = paused ? "\uE768" : "\uE769",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 10
                },
                ToolTip = paused ? "恢复" : "暂停",
                Tag = f
            };
            btnPause.Click += (_, __) =>
            {
                if (!f.Enabled) { f.Enabled = true; f.Paused = false; }
                else f.Paused = !f.Paused;
                ConfigStore.Save(App.Config);
                RefreshAutomationList();
            };
            btns.Children.Add(btnPause);

            var btnRun = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Width = 26, Height = 26,
                Content = new TextBlock
                {
                    Text = "\uE768", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10
                },
                ToolTip = "立即运行一次",
                Tag = f,
                Margin = new Thickness(4, 0, 0, 0)
            };
            btnRun.Click += async (_, __) => await RunFlowNowAsync(f);
            btns.Children.Add(btnRun);

            // ★ 编辑按钮（用户反馈：已建立的自动化没有编辑入口）
            var btnEdit = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Width = 26, Height = 26,
                Content = new TextBlock
                {
                    Text = "\uE70F", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10
                },
                ToolTip = "编辑这个自动化",
                Tag = f,
                Margin = new Thickness(4, 0, 0, 0)
            };
            btnEdit.Click += (_, __) =>
            {
                // 找到这个流程绑定的账号
                Account? acc = null;
                foreach (var a in App.Config.Accounts)
                    if (a.Id == f.AccountId) { acc = a; break; }
                if (acc == null) acc = new Account { Id = f.AccountId, UserName = "未知" };

                OpenFlowEditor(f, acc);
            };
            btns.Children.Add(btnEdit);

            var btnDel = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Width = 26, Height = 26,
                Content = new TextBlock
                {
                    Text = "\uE74D", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10
                },
                ToolTip = "删除流程",
                Tag = f,
                Margin = new Thickness(4, 0, 0, 0)
            };
            btnDel.Click += (_, __) =>
            {
                if (!ThemedMessage.Confirm(Window.GetWindow(this), "删除流程",
                        "确定删除流程「" + f.DisplayName + "」？")) return;
                App.Config.Automations.RemoveAll(x => x.Id == f.Id);
                ConfigStore.Save(App.Config);
                RefreshAutomationList();
            };
            btns.Children.Add(btnDel);

            Grid.SetColumn(btns, 2);
            grid.Children.Add(btns);

            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(9, 7, 8, 7),
                Margin = new Thickness(0, 0, 0, 5),
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                Cursor = Cursors.Hand,
                Child = grid,
                Tag = f
            };

            // 点卡片 → 打开编辑器
            card.MouseLeftButtonUp += (_, ev) =>
            {
                if (ev.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;
                OpenFlowEditor(f, account);
            };

            return card;
        }

        private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        private static string AutomationSummary(AutomationFlow f)
        {
            int contacts = f.Nodes.Count(n => n.IsContact);
            int outs = f.Nodes.Count(n => n.Kind == FlowNodeKind.Action);
            int lit = f.RuntimeStates.Count(kv => kv.Value.Value && f.FindNode(kv.Key)?.IsContact == true);
            return contacts + " 个条件（满足 " + lit + "） · " + outs + " 个动作 · " + f.TodayRunState().Text;
        }

        private void NewAutomation_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sess = _logSession;
                if (sess == null)
                {
                    MessageBox.Show("请先在右侧选一个账号。", "SoulLink");
                    return;
                }

                Account? account = null;
                foreach (var a in App.Config.Accounts)
                    if (string.Equals(a.UserName, sess.UserName, StringComparison.OrdinalIgnoreCase))
                    { account = a; break; }

                if (account == null)
                {
                    // 没有配置过这个账号：自动建一条
                    account = new Account { UserName = sess.UserName };
                    App.Config.Accounts.Add(account);
                    ConfigStore.Save(App.Config);
                }

                var flow = new AutomationFlow
                {
                    AccountId = account.Id,
                    Name = account.UserName + " 自动化",
                    // ★ 记录创建时间：定时触发器据此判断"这个时间点是否属于本流程"，
                    //   避免"10:00 新建 04:00 的流程，当天立刻补跑"。
                    CreatedAt = DateTime.Now
                };

                // ★ 用户要求：只有点了保存才真正入库，
                //   不要在主页留下空白流程。
                //   所以这里先**不加**进 App.Config，由编辑器在保存时添加。
                bool isNew = true;

                OpenFlowEditor(flow, account, isNew);
                RefreshAutomationList();
            }
            catch (Exception ex)
            {
                MessageBox.Show("新建流程失败：" + ex.Message, "SoulLink");
            }
        }

        private void OpenFlowEditor(AutomationFlow flow, Account account, bool isNew = false)
        {
            try
            {
                var dlg = new FlowEditorWindow(flow, account, App.Config, isNew)
                {
                    Owner = Window.GetWindow(this)
                };
                dlg.ShowDialog();
                ConfigStore.Save(App.Config);
                RefreshAutomationList();
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开编辑器失败：" + ex.Message, "SoulLink");
            }
        }

        /// <summary>
        /// 卡片上的 ▶ 按钮：**手动**请求运行一次 → 弹窗显示结果。
        /// ★ 自动触发（调度器）完全不经过这里，那边只写日志、不弹窗。
        /// </summary>
        private async Task RunFlowNowAsync(AutomationFlow flow)
        {
            try
            {
                var sess = _logSession;
                if (sess == null) return;

                SetRunHint("正在运行「" + flow.DisplayName + "」…", true);

                var result = await FlowRunner.RunAsync(flow, sess.Id, App.Config, manual: true);
                ConfigStore.Save(App.Config);
                string detail = string.Join(Environment.NewLine, result.Steps);

                // 手动点击 → 弹窗（主题样式）
                ThemedMessage.Info(Window.GetWindow(this), "执行结果",
                    result.Message + (detail.Length > 0 ? ("\n\n" + detail) : ""));

                LogBus.Write(result.Passed ? LogLevel.Ok : LogLevel.Info,
                    "手动运行「" + flow.DisplayName + "」", result.Message);

                SetRunHint(result.Message, result.Passed);
                RefreshAutomationList();
            }
            catch (Exception ex)
            {
                LogBus.Error("手动运行失败：" + ex.Message);
                ThemedMessage.Error(Window.GetWindow(this), "运行失败", ex.Message);
            }
        }

        private void DeleteAction_Click(object sender, RoutedEventArgs e)
        {
            if (ActionList.SelectedItem is not ActionVm vm) return;
            var r = MessageBox.Show("确定删除动作「" + vm.Model.Name + "」？", "SoulLink",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;
            App.Config.Actions.RemoveAll(a => a.Id == vm.Model.Id);
            ConfigStore.Save(App.Config);
            ReloadActions();
        }

        private void BrowseExe_Click(object sender, RoutedEventArgs e)
        {
            if (ActionList.SelectedItem is not ActionVm vm) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择目标程序（路径必须是目标用户那边也存在的路径）",
                Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                CheckFileExists = false
            };
            if (File.Exists(vm.Model.ExePath))
                dlg.InitialDirectory = Path.GetDirectoryName(vm.Model.ExePath);

            if (dlg.ShowDialog() == true)
            {
                vm.Model.ExePath = dlg.FileName;
                vm.Refresh();
                ConfigStore.Save(App.Config);
                // 刷新列表显示
                var idx = _actions.IndexOf(vm);
                if (idx >= 0)
                {
                    var selected = vm.Model.Id;
                    ReloadActions();
                    ActionList.SelectedItem = _actions.FirstOrDefault(v => v.Model.Id == selected);
                }
            }
        }

        private void CopyCmd_Click(object sender, RoutedEventArgs e)
        {
            if (ActionList.SelectedItem is not ActionVm vm) return;
            try
            {
                Clipboard.SetText(CommandBuilder.ForAction(vm.Model));
                TipText.Text = "命令已复制到剪贴板，可直接放进计划任务或批处理。";
            }
            catch { }
        }

        private void RefreshSessions_Click(object sender, RoutedEventArgs e) => RefreshSessions();

        /// <summary>列表项绑定不会自动感知属性变化，这里就地重建一次以刷新显示。</summary>
        private void RefreshBadges()
        {
            var selected = ActionList.SelectedItem;
            var items = _actions.ToList();
            _actions.Clear();
            foreach (var i in items) _actions.Add(i);
            if (selected != null)
            {
                var again = _actions.FirstOrDefault(v => v.Model.Id == ((ActionVm)selected).Model.Id);
                if (again != null) ActionList.SelectedItem = again;
            }
        }
    }

    // ══════════ 视图模型 ══════════

    public class ActionVm
    {
        public ActionItem Model { get; }
        private readonly AppConfig _cfg;

        public ActionVm(ActionItem model, AppConfig cfg)
        {
            Model = model;
            _cfg = cfg;
        }

        public string Name => Model.Name;

        public string AccountName
        {
            get
            {
                var a = _cfg.Accounts.FirstOrDefault(x => x.Id == Model.AccountId);
                return a == null ? "未绑定账号" : a.UserName;
            }
        }

        public string ExeName => string.IsNullOrWhiteSpace(Model.ExePath) ? "未设置程序" : Path.GetFileName(Model.ExePath);

        // ── 定时执行（只有配了定时的动作才显示相关元素）──────────────

        /// <summary>是否配置了定时（决定界面是否显示定时相关元素）。</summary>
        public bool HasSchedule => Model.Schedule != null && Model.Schedule.Enabled;

        /// <summary>定时相关控件的可见性：非定时任务不显示。</summary>
        public System.Windows.Visibility ScheduleVisibility
            => HasSchedule ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        /// <summary>定时是否暂停。</summary>
        public bool SchedulePaused => Model.Schedule?.Paused == true;

        /// <summary>显示用：下一次执行时刻。</summary>
        public string NextRunText
        {
            get
            {
                if (!HasSchedule) return "";
                if (SchedulePaused)
                {
                    string reason = Model.Schedule?.PauseReason ?? "";
                    return string.IsNullOrEmpty(reason) ? "已暂停" : ("已暂停（" + reason + "）");
                }
                // 计划任务在系统里被禁用：提示但不算暂停
                if (Model.Schedule?.PauseReason == "系统里已禁用")
                    return "系统里已禁用";
                var next = Services.ScheduleCalculator.NextRun(Model.Schedule);
                if (next == null) return "已暂停（无后续执行）";
                return "下次 " + FormatNext(next.Value);
            }
        }

        /// <summary>下次执行的文字颜色：暂停→灰，临期→青，其余→次要色。</summary>
        public System.Windows.Media.Brush NextRunBrush
        {
            get
            {
                if (!HasSchedule) return System.Windows.Media.Brushes.Transparent;
                if (SchedulePaused)
                    return new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x8B, 0x9C, 0xC8));
                var next = Services.ScheduleCalculator.NextRun(Model.Schedule);
                if (next == null)
                    return new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x8B, 0x9C, 0xC8));
                var span = next.Value - DateTime.Now;
                if (span.TotalMinutes <= 10)
                    return new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x41, 0xF0, 0xC0));   // 快到了：绿
                return new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xB4, 0xC2, 0xE8));       // 常态：淡蓝
            }
        }

        /// <summary>暂停按钮图标：未暂停显示暂停⏸，已暂停显示播放▶。</summary>
        public string PauseGlyph => SchedulePaused ? "\uE768" : "\uE769";

        /// <summary>暂停按钮提示。</summary>
        public string PauseTip => SchedulePaused ? "恢复定时执行" : "暂停定时执行";

        /// <summary>把时间格式化成"今天 04:00:00 / 明天 04:01:00 / 11-18 04:02:00"。</summary>
        private static string FormatNext(DateTime t)
        {
            string hms = t.ToString("HH:mm:ss");
            var today = DateTime.Now.Date;
            if (t.Date == today) return "今天 " + hms;
            if (t.Date == today.AddDays(1)) return "明天 " + hms;
            if (t.Date == today.AddDays(2)) return "后天 " + hms;
            return t.ToString("MM-dd") + " " + hms;
        }

        public string Icon
        {
            get
            {
                switch (Model.Kind)
                {
                    case ActionKind.Close: return "\uE71A";
                    case ActionKind.Restart: return "\uE777";
                    default: return "\uE768";
                }
            }
        }

        public string KindText
        {
            get
            {
                switch (Model.Kind)
                {
                    case ActionKind.Close: return "关闭";
                    case ActionKind.Restart: return "重启";
                    default: return "打开";
                }
            }
        }

        public Brush BadgeBg
        {
            get
            {
                switch (Model.Kind)
                {
                    case ActionKind.Close: return new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0x6B, 0x8B));
                    case ActionKind.Restart: return new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xC4, 0x6B));
                    default: return new SolidColorBrush(Color.FromArgb(0x33, 0x41, 0xF0, 0xC0));
                }
            }
        }

        public Brush BadgeFg
        {
            get
            {
                switch (Model.Kind)
                {
                    case ActionKind.Close: return new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0xC4));
                    case ActionKind.Restart: return new SolidColorBrush(Color.FromRgb(0xFF, 0xDC, 0x9E));
                    default: return new SolidColorBrush(Color.FromRgb(0x9D, 0xFF, 0xE0));
                }
            }
        }

        public void Refresh()
        {
            // 简单起见由父级重建列表；这里保留占位以避免模板绑定失效
        }
    }

    public class SessionVm : System.ComponentModel.INotifyPropertyChanged
    {
        private SessionInfo _session;
        public SessionInfo Session
        {
            get => _session;
            set
            {
                _session = value;
                // ★ 就地更新必须通知界面：否则定时刷新时
                //   数据变了但绿灯/文字不刷新（原来只能靠整体 Clear 重建列表）。
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Session)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(UserName)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SideText)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsActive)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DotColor)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IdleBrush)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(BgiLevel)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(StateText)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(StateBrush)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(BgiText)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(BgiBrush)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(BgiVisibility)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasChannel)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ChannelText)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ChannelBrush)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public SessionVm(SessionInfo s) { _session = s; }

        public string UserName => Session.UserName;
        public string SideText => "#" + Session.Id;

        /// <summary>
        /// 显示名：中控台里给账号填了备注名就用备注名。
        /// ★ 用户要求左右两侧（会话列表、运行状态）都用这个，
        ///   只填一个备注名即可全局生效，不用每处单独改。
        /// </summary>
        public string DisplayName
        {
            get
            {
                string name = RealName;
                if (string.IsNullOrWhiteSpace(_remark)) return name;
                return name + " · " + _remark;
            }
        }

        /// <summary>
        /// 真实大小写的用户名。
        /// ★ 兜底：如果会话名是 query user 给的小写（a2），
        ///   而账号配置里登记的是大写（A2），就用配置里的写法。
        ///   这样即使 qwinsta 校正失败，界面也不会大小写混杂。
        /// </summary>
        public string RealName
        {
            get
            {
                if (!string.IsNullOrEmpty(_realName)) return _realName;
                return Session.UserName;
            }
        }

        private string _realName = "";
        private string _remark = "";

        /// <summary>设置真实大小写的用户名。</summary>
        public void SetRealName(string name)
        {
            if (string.Equals(_realName, name, StringComparison.Ordinal)) return;
            _realName = name ?? "";
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(RealName)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayName)));
        }

        /// <summary>设置备注名（来自中控台账号配置的 DisplayName）。</summary>
        public void SetRemark(string remark)
        {
            if (_remark == remark) return;
            _remark = remark ?? "";
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayName)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayNameOnly)));
        }

        /// <summary>仅备注名（没有则回退用户名）。</summary>
        public string DisplayNameOnly
            => string.IsNullOrWhiteSpace(_remark) ? RealName : _remark;
        public bool IsActive => Session.IsActive;

        /// <summary>
        /// 指示灯颜色：**只反映连接状态**，与 BGI 活跃度无关。
        ///   在线 → 绿色
        ///   断开 → 橙色
        ///   其它 → 灰蓝
        /// </summary>
        public Color DotColor
        {
            get
            {
                // 主会话始终按"在线"处理（用户就在这个桌面上操作）
                if (Session.IsConsoleSession || Session.IsActive)
                    return Color.FromRgb(0x41, 0xF0, 0xC0);                         // 绿
                if (Session.IsDisconnected) return Color.FromRgb(0xFF, 0xC4, 0x6B); // 断开：橙
                return Color.FromRgb(0x8B, 0x9C, 0xC8);                             // 其它：灰蓝
            }
        }

        /// <summary>
        /// BGI 日志最后写入时间（由外部按会话号填充）。
        /// null = 该会话没有运行 BGI。
        /// </summary>
        public DateTime? BgiLastWrite { get; set; }

        /// <summary>
        /// BGI 活跃度等级（决定"空闲"文字的颜色）：
        ///   0 = 绿：1 分钟内有日志（脚本正常运行）
        ///   1 = 黄：1~5 分钟无日志（可能卡住或任务间隙较长）
        ///   2 = 红：超过 5 分钟无日志，或 BGI 未启动
        /// </summary>
        public int BgiLevel
        {
            get
            {
                // 主会话不参与 BGI 活跃度判断，固定绿色（用户正在操作）
                if (Session.IsConsoleSession) return 0;

                if (BgiLastWrite == null) return 2;                 // 没跑 BGI → 红
                double mins = (DateTime.Now - BgiLastWrite.Value).TotalMinutes;
                if (mins < 1) return 0;
                if (mins < 5) return 1;
                return 2;
            }
        }

        /// <summary>
        /// **连接状态**文字（会话在线 / 会话离线 / 当前会话）。
        ///
        /// ★ 这一段只描述连接状态，与 BGI 完全无关：
        ///   之前把 BGI 活跃度混进同一行并染色，会让人误以为"离线"了（用户反馈）。
        /// </summary>
        public string StateText
        {
            get
            {
                if (Session.IsConsoleSession) return "当前会话";
                if (Session.IsActive) return "会话在线";
                if (Session.IsDisconnected) return "会话离线";
                return "会话" + (string.IsNullOrWhiteSpace(Session.State) ? "未知" : Session.State);
            }
        }

        /// <summary>连接状态文字的颜色 —— 跟随系统状态，**不受 BGI 影响**。</summary>
        public System.Windows.Media.Brush StateBrush
        {
            get
            {
                Color c;
                if (Session.IsConsoleSession || Session.IsActive)
                    c = Color.FromRgb(0x8F, 0xE8, 0xD0);      // 在线/当前会话：绿
                else if (Session.IsDisconnected)
                    c = Color.FromRgb(0xFF, 0xD5, 0x4A);      // 离线：黄
                else
                    c = Color.FromRgb(0x8B, 0x9C, 0xC8);      // 其它：灰蓝
                var b = new System.Windows.Media.SolidColorBrush(c);
                b.Freeze();
                return b;
            }
        }

        /// <summary>主会话不显示 BGI 段。</summary>
        public System.Windows.Visibility BgiVisibility
            => Session.IsConsoleSession ? System.Windows.Visibility.Collapsed
                                         : System.Windows.Visibility.Visible;

        private bool _hasChannel;
        /// <summary>该会话是否已有可用注入通道（由外部按活代理探测结果填充）。</summary>
        public bool HasChannel
        {
            get => _hasChannel;
            set
            {
                if (_hasChannel == value) return;
                _hasChannel = value;
                if (value) { ChannelError = ""; }
                NotifyChannel();
            }
        }

        private string _channelError = "";
        /// <summary>最近一次投送失败的原因；空表示没有失败记录。</summary>
        public string ChannelError
        {
            get => _channelError;
            set
            {
                if (_channelError == value) return;
                _channelError = value ?? "";
                NotifyChannel();
            }
        }

        private void NotifyChannel()
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasChannel)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ChannelText)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ChannelBrush)));
        }

        /// <summary>三种状态：就绪 / 未投送 / 投送失败。</summary>
        public string ChannelText
            => HasChannel ? "通道就绪"
               : ChannelError.Length > 0 ? ("投送失败：" + ShortError(ChannelError))
               : "通道未投送";

        private static string ShortError(string msg)
        {
            var m = (msg ?? "").Replace("\n", " ").Trim();
            const string prefix = "投送注入代理失败：";
            if (m.StartsWith(prefix, StringComparison.Ordinal)) m = m.Substring(prefix.Length);
            return m.Length <= 28 ? m : m.Substring(0, 28) + "…";
        }

        /// <summary>通道状态颜色：就绪绿、未投送灰蓝、失败红。</summary>
        public System.Windows.Media.Brush ChannelBrush
        {
            get
            {
                Color c = HasChannel
                    ? Color.FromRgb(0x8F, 0xE8, 0xD0)     // 就绪：绿
                    : ChannelError.Length > 0
                        ? Color.FromRgb(0xFF, 0x9B, 0x9B)  // 失败：红
                        : Color.FromRgb(0x8B, 0x9C, 0xC8); // 未投送：灰蓝
                var b = new System.Windows.Media.SolidColorBrush(c);
                b.Freeze();
                return b;
            }
        }

        /// <summary>
        /// **BGI 活跃度**文字（单行显示时用，与连接状态用固定颜色圆点隔开）。
        /// 主会话不显示（用户在操作，不需要看 BGI）。
        /// </summary>
        public string BgiText
        {
            get
            {
                if (Session.IsConsoleSession) return "";
                if (BgiLastWrite == null) return "BGI 未运行";
                var span = DateTime.Now - BgiLastWrite.Value;
                if (span.TotalSeconds < 5) return "BGI 刚活动";
                if (span.TotalSeconds < 60) return "BGI 空闲 " + (int)span.TotalSeconds + " 秒";
                if (span.TotalMinutes < 60) return "BGI 空闲 " + (int)span.TotalMinutes + " 分钟";
                return "BGI 空闲 " + (int)span.TotalHours + " 小时";
            }
        }

        /// <summary>BGI 行的颜色，按活跃度分级。</summary>
        public System.Windows.Media.Brush BgiBrush
        {
            get
            {
                Color c;
                switch (BgiLevel)
                {
                    case 0: c = Color.FromRgb(0x8F, 0xE8, 0xD0); break;   // 绿
                    case 1: c = Color.FromRgb(0xFF, 0xD5, 0x4A); break;   // 黄
                    default: c = Color.FromRgb(0xFF, 0x7B, 0x7B); break;  // 红
                }
                var b = new System.Windows.Media.SolidColorBrush(c);
                b.Freeze();
                return b;
            }
        }

        /// <summary>"空闲"文字的颜色，按 BGI 活跃度分级。</summary>
        public System.Windows.Media.Brush IdleBrush
        {
            get
            {
                Color c;
                switch (BgiLevel)
                {
                    case 0: c = Color.FromRgb(0x8F, 0xE8, 0xD0); break;   // 绿
                    case 1: c = Color.FromRgb(0xFF, 0xD5, 0x4A); break;   // 黄
                    default: c = Color.FromRgb(0xFF, 0x7B, 0x7B); break;  // 红
                }
                var b = new System.Windows.Media.SolidColorBrush(c);
                b.Freeze();
                return b;
            }
        }
    }
}
