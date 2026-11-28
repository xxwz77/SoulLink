using System;
using SoulLink.Controls;
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
        private string? _managedActionAccountId;
        private bool _findManagedActionSession;
        private bool _selectingManagedActionSession;
        private bool _busy;
        private bool _compactLayout;
        private ScrollViewer? _sessionScroller;

        /// <summary>
        /// 在线会话的定时刷新。
        /// ★ 原来启动台只在 Activate()（切到本页）时刷新一次，
        ///   停在启动台时账号上下线看不到变化，必须手动点刷新或切页才更新
        ///   （用户反馈："为什么还要按刷新才能看到变绿灯"）。
        /// </summary>
        private IDisposable? _sessionSub;
        /// <summary>自动化卡片的 1 秒轻量刷新（只为状态灯的颜色交替）。</summary>
        private IDisposable? _automationSub;
        private bool _refreshingSessions;

        private void UpdateResponsiveLayout()
        {
            bool compact = ActualWidth > 0 && ActualWidth < 760;
            _compactLayout = compact;
            var columns = LayoutRoot.ColumnDefinitions;
            var rows = LayoutRoot.RowDefinitions;
            if (rows.Count == 0)
            {
                rows.Add(new RowDefinition { Height = GridLength.Auto });
                rows.Add(new RowDefinition { Height = GridLength.Auto });
                rows.Add(new RowDefinition { Height = GridLength.Auto });
            }

            // 空间不足时不再强行保持两列，改为可滚动的上下布局。
            // 两列下右侧会话区需要的最小宽度会把内容挤到窗口之外。
            Grid.SetColumn(SessionPanel, compact ? 0 : 2);
            Grid.SetRow(SessionPanel, compact ? 2 : 0);
            Grid.SetRowSpan(SessionPanel, 1);
            columns[0].Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(3, GridUnitType.Star);
            columns[1].Width = new GridLength(compact ? 0 : 18);
            columns[2].Width = compact ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
            SessionPanel.Padding = new Thickness(compact ? 10 : 16, 16, compact ? 10 : 16, 16);

            if (compact)
            {
                if (_sessionScroller == null)
                {
                    var content = SessionPanel.Child;
                    SessionPanel.Child = null;
                    _sessionScroller = new ScrollViewer
                    {
                        MaxHeight = 360,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = content
                    };
                    SessionPanel.Child = _sessionScroller;
                }
            }
            else if (_sessionScroller != null)
            {
                var content = _sessionScroller.Content;
                _sessionScroller.Content = null;
                SessionPanel.Child = content as UIElement;
                _sessionScroller = null;
            }
        }

        public LaunchPage()
        {
            InitializeComponent();
            Loaded += (_, __) => UpdateResponsiveLayout();
            SizeChanged += (_, __) => UpdateResponsiveLayout();
            ActionList.ItemsSource = _actions;
            SessionList.ItemsSource = _sessions;

            // ★ 改用统一节拍（TickHub）：3 秒一跳。
            //   原来每个页面各建 DispatcherTimer，各自唤醒 Dispatcher，
            //   线程池因此长期处于自旋状态（空闲 CPU 偏高的主因之一）。
            _sessionSub = Services.TickHub.Register("LaunchPage.Session", 3, () =>
            {
            // 只在本页可见时刷新，切走就跳过，省开销
            if (!IsVisible) return;
            RefreshSessions();
            // 自动化卡片由下面的 1 秒节拍和账号选择事件刷新。
            // 右侧原始日志：跟随点击的会话
            // ★ 架构修正后没有本地 LogFollower 了，改用"是否订阅数据中心"判断。
            //   内容更新由 OnLogCenterChanged 推送，这里只负责"换了会话要重新绑"。
            if (!_logCenterSubscribed) RefreshSelectedLog();
            // 左侧：主会话显示总览，其它账号显示单账号状态
            if (_logSession != null && _logSession.IsConsoleSession)
            {
                // ★ 每轮都调用 RefreshOverview：它内部会比对"当前会话集合"
                //   与已有卡片，账号增删/会话号变化（注销重登会换号）时自动重建，
                //   否则出现过一次卡片后就再也不重建，新登录的账号永远不显示。
                RefreshOverview();
                if (_overviewRows.Count > 0) UpdateOverviewTexts();
            }
            else if (!_logCenterSubscribed && _logSession != null)
            {
                StartStatusFollowFor(_logSession);
            }
            });
            // 业务日和结果灯会随时间变化，即便流程字段没有被写入，也要比较显示状态。
            _automationSub = Services.TickHub.Register("LaunchPage.Automation", 1, () =>
            {
                if (IsVisible) RefreshAutomationList();
            });
            Loaded += (_, __) => { };
            Unloaded += (_, __) => { };

            // ★ 定时器启动不能只靠 Loaded：UserControl 在页面切换时
            //   会触发 Unloaded 停表，再切回来 Loaded 不一定再触发，
            //   导致定时器永久停摆（表现：状态/会话列表都不再刷新）。
            //   改成 Loaded + Activate 双保险，并让 Activate 每次都确保启动。
            Loaded += (_, __) => { };
            Unloaded += (_, __) =>
            {
                // TickHub 自行管理节拍，这里只处理跟随器
                // （页面卸载时停止跟随，避免后台一直持有文件句柄）
                StopFollower();
                StopStatusFollower();
            };

            // ★ 投送成功时立刻刷新通道状态，不等 3 秒轮询。
            //
            //   实测现象：用户投送后能用自由视角（代理已在跑），
            //   但主界面仍显示「未投送」，切回主界面才变成「投送成功」。
            //   原因是投送分两步（先起代理进程、后建 relay 管道），
            //   而轮询间隔 3 秒，中间那段窗口期界面看到的是旧状态。
            BgiLogWatcher.ChannelStateMayHaveChanged += OnChannelStateMayHaveChanged;
        }

        public void Activate(string? actionAccountId = null)
        {
            if (_managedActionAccountId != actionAccountId) RunHint.Visibility = Visibility.Collapsed;
            _managedActionAccountId = actionAccountId;
            _findManagedActionSession = actionAccountId != null && !SelectManagedActionSession();
            if (_findManagedActionSession) SessionList.SelectedItem = null;
            ReloadActions();
            // TickHub 自行管理节拍，无需手动启停。
            RefreshSessions();
            RefreshAutomationList();
        }

        /// <summary>
        /// 通道状态可能变化（投送成功 / 代理退出）→ 立刻刷新。
        /// ★ 事件可能来自后台线程，必须 marshal 回 UI 线程。
        /// </summary>
        private void OnChannelStateMayHaveChanged()
        {
            try
            {
                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (!IsLoaded) return;
                        RefreshSessions();
                    }
                    catch (Exception ex) { App.AppendErrorLog("LaunchPage.OnChannelStateMayHaveChanged", ex); }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { }
        }

        // ══════════ 数据加载 ══════════

        private Account? FindActionAccount()
        {
            var accounts = AccountScope.Accounts();
            if (_managedActionAccountId is { } id) return accounts.FirstOrDefault(account => account.Id == id);
            if (SessionList.SelectedItem is not SessionVm session) return null;

            string user = SessionService.BareName(session.Session.UserName);
            if (string.IsNullOrWhiteSpace(user)) return null;
            var matches = accounts.Where(account => !string.IsNullOrWhiteSpace(account.Id) &&
                string.Equals(SessionService.BareName(account.UserName), user, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        private bool CanManageAction(ActionItem action)
            => FindActionAccount() is { } account &&
                action.AccountId == account.Id && App.Config.Actions.Contains(action);

        private bool SelectManagedActionSession()
        {
            if (FindActionAccount() is not { } account) return false;
            var session = _sessions.FirstOrDefault(vm => string.Equals(SessionService.BareName(vm.Session.UserName),
                SessionService.BareName(account.UserName), StringComparison.OrdinalIgnoreCase));
            if (session == null) return false;
            _selectingManagedActionSession = true;
            try { SessionList.SelectedItem = session; }
            finally { _selectingManagedActionSession = false; }
            return true;
        }

        public void ReloadActions()
        {
            var config = App.Config;
            var account = FindActionAccount();
            string? keepId = (ActionList.SelectedItem as ActionVm)?.Model.Id;
            _actions.Clear();
            if (account != null)
                foreach (var action in config.Actions.Where(action => action?.AccountId == account.Id))
                    _actions.Add(new ActionVm(action));

            ActionHeading.Text = account == null ? "快捷动作" : $"快捷动作 · {account.Caption}";
            ActionHeading.ToolTip = account?.Caption;
            BtnNewAction.IsEnabled = account != null;
            EmptyActionTitle.Text = account != null ? "当前账号暂无快捷动作"
                : SessionList.SelectedItem == null && _managedActionAccountId == null ? "请选择一个账号" : "当前账号未配置";
            EmptyActionHelp.Visibility = BtnNewAction.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            EmptyHintHost.Visibility = _actions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var sel = _actions.FirstOrDefault(v => v.Model.Id == keepId) ?? _actions.FirstOrDefault();
            if (sel != null) ActionList.SelectedItem = sel;
            else UpdateCommandPreview(null);
        }

        /// <param name="force">
        /// true = 用户主动点击刷新，**强制重新查询系统**（起 query.exe + qwinsta）。
        /// false = 定时刷新，优先复用 AccountStateCenter 的快照，省资源。
        ///
        /// ★ 为什么要区分：
        ///   定时刷新每几秒一次，走快照足够（快照本身就是后台查来的）。
        ///   而用户点"刷新"就是想立刻看到最新状态 —— 如果还拿旧快照，
        ///   按钮看起来"点了没反应"（实测快照 5 秒内就直接复用，完全不查系统）。
        /// </param>
        public void RefreshSessions(bool force = false)
        {
            // ★ 防重入：定时器 3 秒触发一次，而一次查询要 ~30ms；
            //   若上次还没回来就再发，会堆积请求并让列表反复重建（闪烁）。
            if (_refreshingSessions) return;
            _refreshingSessions = true;

            var spin = ThemeManager.AnimationsDisabled
                ? null : FindResource("SpinRefresh") as System.Windows.Media.Animation.Storyboard;
            try { spin?.Begin(RefreshGlyph); } catch { }

            _ = Task.Run(async () =>
            {
                try
                {
                    // ★ 优先读账号状态中心快照：它每秒统一刷新一次，
                    //   本页原来每 3 秒自己 query user + 枚举 BGI 进程，
                    //   与其他页面重复。快照太旧时才自己查。
                    List<SessionInfo> all;
                    if (force)
                    {
                        // 用户主动点击：真查一次系统，并让状态中心也走一轮快档，
                        // 这样其他页面（数据中心/会话页）同时拿到最新数据。
                        try { await AccountStateCenter.ForceRefreshAsync(App.Config).ConfigureAwait(false); }
                        catch { }
                        all = AccountStateCenter.Sessions;
                        if (all == null || all.Count == 0)
                            all = await SessionService.QueryAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        var snap = AccountStateCenter.Sessions;
                        bool fresh = snap.Count > 0
                            && (DateTime.Now - AccountStateCenter.LastRefresh).TotalSeconds < 5;
                        if (fresh) all = snap;
                        else all = await SessionService.QueryAsync().ConfigureAwait(false);
                    }

                    // ★★ 只保留「配置里有的账号」+ 主会话 ★★
                    //
                    //   用户要求：「RDP 配置里没有的用户直接隐藏」。
                    //
                    //   原来只做了 FilterUsers（滤掉 services/UMFD/DWM），
                    //   系统里任何登录过的人都会列出来 —— 实测 a1/a4 已经从
                    //   RDP 配置里删掉，但会话还在，于是列表里一直有它们。
                    //
                    //   这些会话不属于我们管理的挂机账号：
                    //     · 不该读它们的 BGI 日志
                    //     · 不该显示"通道未投送"
                    //     · 更不该出现在运行状态总览里
                    //
                    //   主会话（console）保留：它是"总览页面"的导航入口。
                    var users = SessionService.FilterUsers(all);
                    var accounts = App.Config?.Accounts;
                    if (accounts != null && accounts.Count > 0)
                    {
                        var managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var a in accounts)
                        {
                            if (a == null) continue;
                            managed.Add(SessionService.BareName(a.UserName));
                        }

                        users = users.Where(s =>
                        {
                            // 主会话始终保留（总览入口）
                            if (s.IsConsoleSession) return true;
                            return managed.Contains(SessionService.BareName(s.UserName));
                        }).ToList();
                    }

                    await Dispatcher.InvokeAsync(async () => await ApplySessions(users)).Task.Unwrap();
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
        private async Task ApplySessions(List<SessionInfo> users)
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

                    // 以"能连通活代理"为准，避免残留管道让状态反复跳动。
                    //
                    // ★★ 必须在后台线程执行。★★
                    //   DiscoverLiveAgentSessions 内部要枚举全部命名管道，
                    //   系统管道数正常时几毫秒；但第三方软件泄漏管道时
                    //   （实测累积到 3 万个）单次枚举要数秒。
                    //   本方法由 3 秒定时器经 Dispatcher.InvokeAsync 回到 UI 线程调用，
                    //   在这里同步枚举会直接把界面冻住（实测点击无响应、
                    //   主线程 100% + UIAutomation 事件积压）。
                    channelSessions = await System.Threading.Tasks.Task.Run(
                        () => BgiLogWatcher.DiscoverLiveAgentSessions()).ConfigureAwait(true);
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

                    // ★ 是否已绑定 BGI 路径。未绑定 → 界面显示「BGI 未绑定」，
                    //   而不是断言「BGI 未运行」（我们根本没有它的日志路径）。
                    bool bound = BgiPathResolver.IsPinned(u.UserName);

                    // ★ 停止状态也要传进来，否则右侧会把它显示成"BGI 空闲 N 分钟"。
                    var stForUser = AccountStateCenter.GetByUser(u.UserName);
                    var stoppedAt = stForUser?.StoppedAt ?? DateTime.MinValue;
                    bool groupRunning = (stForUser?.CurrentGroup.Length ?? 0) > 0;

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
                            BgiBound = bound,
                            BgiStoppedAt = stoppedAt,
                            BgiGroupRunning = groupRunning,
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
                        exist.BgiBound = bound;
                        exist.BgiStoppedAt = stoppedAt;
                        exist.BgiGroupRunning = groupRunning;
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

                if (_findManagedActionSession)
                {
                    _findManagedActionSession = false;
                    SelectManagedActionSession();
                }

                // ★ 首次进入时默认选中主会话，让左栏显示"所有账号总览"。
                if (SessionList.SelectedItem == null && _managedActionAccountId == null && _sessions.Count > 0)
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
            if (e.Source != SessionList) return;
            RunHint.Visibility = Visibility.Collapsed;
            // ★ 点击会话不再跳转到"会话进程"页，改为在本页下方显示该账号的 BGI 实时日志。
            //   挂机场景下看日志比看进程列表更有用，也避免来回切页。
            if (SessionList.SelectedItem is SessionVm vm)
            {
                if (!_selectingManagedActionSession) _managedActionAccountId = null;
                _findManagedActionSession = false;
                _logSession = vm.Session;
                _logText = "";
                LogText.Text = "";


                StatusPanel.Visibility = Visibility.Visible;
                ApplyLeftPanelMode(vm.Session);
                RefreshSelectedLog(force: true);
            }
            else
            {
                _logSession = null;
                _logText = "";
                LogText.Text = "";
                LogTitle.Text = "BGI 实时日志";
                LogMeta.Text = "请选择在线会话";
                StatusPanel.Visibility = Visibility.Collapsed;
                PageSubtitle.Text = "选择在线会话查看其 BGI 运行情况";
                StopFollower();
                StopStatusFollower();
            }
            ReloadActions();
            RefreshAutomationList();
        }

        /// <summary>按所选会话显示运行总览或单账号状态。</summary>
        private void ApplyLeftPanelMode(SessionInfo sess)
        {
            try
            {
                bool isMain = sess.IsConsoleSession;

                // 页面标题固定为"运行状态"（左栏主题），只切换副标题
                if (isMain)
                {
                    PageSubtitle.Text = "所有挂机账号的运行情况";

                    // 总览模式
                    OverviewPanel.Visibility = Visibility.Visible;
                    SingleStatusGrid.Visibility = Visibility.Collapsed;
                    StopStatusFollower();
                    RefreshOverview();
                }
                else
                {
                    PageSubtitle.Text = sess.UserName + " · 该账号的 BGI 运行情况";

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
            /// <summary>界面上显示的名字，含备注名，形如 "A2 · 山客"。</summary>
            public string User = "";
            /// <summary>
            /// 真实 Windows 用户名（不含备注名）。
            /// ★★ 2.17.49：所有查表都必须用它。★★
            ///   原来直接拿 <see cref="User"/> 去调 AccountStateCenter.GetByUser，
            ///   而那是 "A2 · 山客" 这种展示串 —— 查不到 → accountState 为 null →
            ///   bound 判定成 false → 界面把**已绑定**的账号写成「BGI 未绑定」
            ///   （用户实测截图：A2 · 山客 / A3 · 玄月 都显示未绑定）。
            ///   用 Split('·') 临时拆也不可靠：备注名本身可能带 '·'。
            /// </summary>
            public string UserName = "";
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
                    var bgi = BgiLogCenter.SharedInstances();
                    Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            // ★ 只列出**已绑定 BGI 路径**的账号（主会话除外）。
                            //
                            //   用户要求：未绑定的直接不显示，把空间让出来。
                            //   这跟"今天没跑过"是两回事 —— 未绑定 = 我们根本没有它的
                            //   日志路径，列出来只能写个"未绑定"，纯占地方。
                            //   要看某个未绑定账号，去「设置 → BetterGI 路径」绑定即可。
                            var wanted = new List<(SessionInfo sess, BgiLogWatcher.Instance? inst)>();
                            foreach (var vm in _sessions)
                            {
                                var s = vm.Session;
                                if (!AccountScope.Allows(s.UserName)) continue;
                                if (!BgiPathResolver.IsPinned(s.UserName)) continue;   // ★ 未绑定 → 不显示
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

                                    var row = new OverviewRow { User = shown, UserName = s.UserName, SessionId = s.Id };

                                    var card = new System.Windows.Controls.Border
                                    {
                                        Margin = new Thickness(0, 0, 0, 6),
                                        Padding = new Thickness(10, 8, 10, 8),
                                        CornerRadius = new CornerRadius(8),
                                    };
                                    card.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
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
                                        Foreground = UiBrush.Get("MintBrush"),
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
                                        Foreground = UiBrush.Get("TextPrimary")
                                    };
                                    var l2 = new System.Windows.Controls.TextBlock
                                    {
                                        FontSize = 10,
                                        Margin = new Thickness(0, 3, 0, 0),
                                        TextWrapping = TextWrapping.Wrap,
                                        Foreground = UiBrush.Get("CyanBrush")
                                    };
                                    var l3 = new System.Windows.Controls.TextBlock
                                    {
                                        FontSize = 9.5,
                                        Margin = new Thickness(0, 2, 0, 0),
                                        TextWrapping = TextWrapping.Wrap,
                                        Foreground = UiBrush.Get("TextMuted")
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

        /// <summary>更新总览里每个账号的进度文字（**只读 BgiLogCenter 快照**）。</summary>
        private void UpdateOverviewTexts()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    // ★ 2.17.45：改用 BgiLogCenter 的共享探测结果（2 秒缓存），
                    //   不再每次自己 Discover()。一次刷新里原来要跑好几遍全进程枚举。
                    var bgi = BgiLogCenter.SharedInstances();

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
                            row.UserName = vm.Session.UserName;   // ★ 真实用户名，查表只用它
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
                                string bgiExe = LoadBgiPath(row.UserName);
                                if (!string.IsNullOrEmpty(bgiExe))
                                    ranToday = !string.IsNullOrEmpty(BgiLogWatcher.FindTodayLog(bgiExe));
                            }
                            catch { }
                            results.Add((row, new BgiStatus(), false, ranToday));
                            continue;
                        }

                        // ★ 2.17.45：**不再从磁盘深读 16MB**。
                        //
                        //   旧实现每轮（2 秒）TailLinesDeep(4000 行 / 16MB) 重新解析一遍，
                        //   再叠加 CountDailyErrors 全文件扫描 —— 在 A1 满速写日志时
                        //   这是持续性的大头开销。
                        //   现在结构性字段（任务名/配置组/路线/轮次）由 BgiLogCenter 的
                        //   单遍扫描增量维护（它从文件头扫到尾巴，不会漏掉"只在任务开始时
                        //   写一次"的行），这里只做一次结构拷贝。
                        var snap0 = BgiLogCenter.SnapshotByUser(row.UserName);
                        var st0 = new BgiStatus();
                        if (snap0 != null)
                        {
                            st0.MergeStructuralFrom(snap0.Status);
                            st0.ErrorCount = snap0.ErrorCount;
                            st0.WarnCount = snap0.WarnCount;
                            st0.FailCount = snap0.FailCount;
                            if (snap0.CurrentGroup.Length > 0)
                            { st0.ConfigGroup = snap0.CurrentGroup; st0.PicksRoute = snap0.CurrentGroup; }
                        }
                        results.Add((row, st0, true, true));
                    }

                    Dispatcher.InvokeAsync(() =>
                    {
                        foreach (var (row, st, running, ranToday) in results)
                        {
                            try
                            {
                                // ★ 必须用真实用户名查：row.User 是 "A2 · 山客" 这种展示串，
                                //   拿它查永远 null → bound=false → 已绑定也被写成「BGI 未绑定」。
                                var accountState = AccountStateCenter.GetByUser(row.UserName);
                                if (accountState != null)
                                {
                                    // ★ 当前配置组只认 AccountState.CurrentGroup。
                                    //   它是"运行中的那个"：出现新的才替换，跨天滚动时继承，
                                    //   但日志出现停止锚点后立刻清空 —— 停止就是停止，
                                    //   不再保留运行状态（用户明确要求）。
                                    string currentName = accountState.CurrentGroup;
                                    if (currentName.Length > 0)
                                    {
                                        st.ConfigGroup = currentName;
                                        st.PicksRoute = currentName;
                                        st.TaskName = "";
                                    }
                                    else if (accountState.StoppedAt != DateTime.MinValue)
                                    {
                                        st.ConfigGroup = "";
                                    }
                                }
                                // ★ 没跑 BGI：先分**未绑定**和已绑定两种情况。
                                //
                                //   未绑定 = 我们根本没有它的日志路径，**什么都不知道**。
                                //   这时说"今日还未运行 BetterGI / 今天运行过"是在编造结论
                                //   （实测 A2/A3 从来没绑定过，界面却在断言它们跑没跑）。
                                //   用户要求：直接显示「BGI 未绑定」。
                                if (!running)
                                {
                                    if (row.Line1 != null && row.Line1.Text != row.User)
                                        row.Line1.Text = row.User;

                                    bool bound = accountState?.Bound == true;
                                    if (row.Line2 != null)
                                    {
                                        string txt = !bound
                                            ? "BGI 未绑定"
                                            : ranToday ? "今天运行过，当前已退出"
                                                       : "今日还未运行 BetterGI";
                                        if (row.Line2.Text != txt) row.Line2.Text = txt;
                                        row.Line2.Foreground = UiBrush.Get(!bound || ranToday ? "AmberBrush" : "TextSecondary");
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

                                // ★ 已停止：BGI 进程还在，但日志里已经出现停止锚点
                                //   （执行结束 / 未成功完成 / 取消 / 停止快捷键）。
                                //   这时**不能再显示轮次/角色/路线**——那些是停止前的残留，
                                //   照抄过来就是"脚本明明停了，卡片还写着第 2/2 轮"。
                                if (accountState != null
                                    && accountState.CurrentGroup.Length == 0
                                    && accountState.StoppedAt != DateTime.MinValue)
                                {
                                    if (row.Line1 != null)
                                    {
                                        string stoppedHead = row.User + "  ·  已停止";
                                        if (accountState.LastGroup.Length > 0)
                                            stoppedHead += "（" + accountState.LastGroup + "）";
                                        if (row.Line1.Text != stoppedHead) row.Line1.Text = stoppedHead;
                                    }
                                    if (row.Line2 != null)
                                    {
                                        string txt2 = accountState.StoppedAt.ToString("HH:mm:ss") + " 停止 · BGI 仍在运行";
                                        if (row.Line2.Text != txt2) row.Line2.Text = txt2;
                                        row.Line2.Foreground = UiBrush.Get("AmberBrush");   // 橙：已停
                                        row.Line2.Visibility = Visibility.Visible;
                                    }
                                    if (row.Line3 != null)
                                    {
                                        row.Line3.Text = "";
                                        row.Line3.Visibility = Visibility.Collapsed;
                                    }
                                    continue;
                                }

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
                                        row.Line2.Foreground = UiBrush.Get("AmberBrush");
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
                                        row.Line2.Foreground = UiBrush.Get("CyanBrush");
                                        row.Line2.Visibility = rl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                                    }

                                    string prob = st.ProblemText;
                                    if (row.Line3 != null)
                                    {
                                        if (row.Line3.Text != prob) row.Line3.Text = prob;
                                        row.Line3.Foreground = st.FailedGroup.Length > 0 || st.LastError.Length > 0
                                            ? UiBrush.Get("RoseBrush")
                                            : UiBrush.Get("TextSecondary");
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
                                    row.Line2.Foreground = UiBrush.Get("CyanBrush");
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
                                    row.Line3.Foreground = UiBrush.Get("TextMuted");
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
                            _logText = "该会话中没有正在运行的 BetterGI 进程。\n\n" +
                                       "启动 BGI 后这里会自动显示它的实时日志。";
                            LogText.Text = _logText;
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
                        LogMeta.Text = "PID " + pid + " · 数据中心";
                        _logText = "";
                        LogText.Text = "";

                        // ★★ 架构：日志的唯一读取者是 BgiLogCenter ★★
                        //
                        //   用户要求（反复强调过）：
                        //     「访问日志的权限是数据中心，应用内其余地方只能从数据中心拿数据！」
                        //
                        //   BgiLogSnapshot.cs 的文件头也写着同样的硬性要求：
                        //     「所有需要 BGI 日志的地方统一从这里取，不再各自读文件」
                        //
                        //   原来这里 new LogFollower(日志文件) 自己开 FileSystemWatcher，
                        //   等于同一份日志被读两遍：
                        //     · BgiLogCenter 单遍流式扫描（游标只进不退）
                        //     · 这里再独立跟随一次
                        //   → CPU 翻倍，而且未绑定账号也可能被读到。
                        //
                        //   现在只读数据中心的不可变快照 + 订阅它的 Changed 事件。
                        string initial = TailTextOf(BgiLogCenter.SnapshotByUser(user));
                        _logText = initial.Length > 0 ? initial : "（等待数据中心采集…）";
                        LogText.Text = _logText;
                        try { LogScroll.ScrollToEnd(); } catch { }
                        SubscribeLogCenter();
                    });
                }
                catch { }
            });
        }

        /// <summary>日志文本最多保留的行数（超出丢弃最早的部分）。</summary>
        private const int MaxLogLines = 400;

        /// <summary>
        /// 把界面上的日志压回与 _logText 一致（去掉最早的约 1/3）。
        ///
        /// ★★ 2026-11-22：这里的"压回"是必须的，不是优化。★★
        ///
        ///   增量追加是把新内容 Add 成新的 Run，Run 会**一直累积**。
        ///   而 _logText 每秒都会从数据中心重新取一份（快照本身有上限），
        ///   两者长度就会越差越多 —— 界面越跑越长、内存只增不减、
        ///   排版成本也跟着涨。所以每次追加后都要把 Inlines 压回等价于 _logText。
        ///
        ///   快路径：Run 数量不多且总长没超上限时直接返回，什么都不做。
        /// </summary>
        private void TrimLogText()
        {
            try
            {
                var inlines = LogText.Inlines;

                // 估算界面当前文本长度：Run 数量少时直接汇总（Run 通常只有几个）。
                bool rewrite = false;
                int total = 0;
                if (inlines.Count > 64)
                {
                    rewrite = true;                    // Run 太碎，合并成一段
                }
                else
                {
                    foreach (var inl in inlines)
                    {
                        if (inl is System.Windows.Documents.Run r) total += r.Text.Length;
                        else total += 1;
                    }
                    if (total > 60000) rewrite = true;
                }

                if (!rewrite) return;

                // 丢弃最早约 1/3，保留 _logText 的尾部（与 _logText 保持一致）。
                string keep = _logText;
                if (keep.Length > 40000)
                {
                    int cut = keep.IndexOf('\n', keep.Length / 3);
                    if (cut > 0) keep = keep.Substring(cut + 1);
                }
                _logText = keep;
                inlines.Clear();
                inlines.Add(new System.Windows.Documents.Run(keep));
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

            var totals = st.CollectedDogfood;
            long totalExp = totals?.Day == DateTime.Today ? totals.Exp : 0;
            long totalMora = totals?.Day == DateTime.Today ? totals.Mora : 0;
            if (totalExp > 0 || totalMora > 0)
            {
                var gain = new List<string>();
                if (totalExp > 0) gain.Add("经验 +" + FormatNum(totalExp));
                if (totalMora > 0) gain.Add("摩拉 +" + FormatNum(totalMora));
                parts.Add("汇总 " + string.Join(" · ", gain));
            }

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
                        _currentStatusUser = user;
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
                        _currentStatusUser = user;        // ★ 快照抄取用
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
                        StatusMeta.Text = user + " · 数据中心";

                        // ★★ 架构：不再自己开 LogFollower ★★
                        //
                        //   用户硬性要求：
                        //     「访问日志的权限是数据中心，应用内其余地方只能从数据中心拿数据！」
                        //
                        //   原来这里 new LogFollower(logFile, ..., deepBackfill: true)，
                        //   会对几十 MB 的日志做**深度回填扫描**，而 BgiLogCenter
                        //   已经在做同样的事（单遍流式、游标只进不退）——
                        //   同一份日志被完整解析两遍。
                        //
                        //   现在只抄数据中心快照里的结构字段。
                        //   结构性字段（任务名/配置组/路线/轮次）由数据中心增量维护，
                        //   它从文件头一路扫到尾巴，不会漏掉"只在任务开始时写一次"的行。
                        try
                        {
                            ApplyCenterSnapshot();
                            SubscribeLogCenter();
                        }
                        catch (Exception ex)
                        {
                            SoulLink.Controls.RdpConsole.Debug("应用数据中心快照异常："
                                + ex.GetType().Name + " - " + ex.Message);
                            StatusEta.Text = "读取数据中心失败：" + ex.Message;
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
                        Foreground = UiBrush.Get("TextMuted")
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
                        Foreground = UiBrush.Get("CyanBrush"),
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
                        Foreground = UiBrush.Get("TextSecondary"),
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

            // ★ 2.17.45：**不再从磁盘重读 16MB**。
            //
            //   旧实现每 3 秒 TailLinesDeep(file, 4000, 16MB)，也就是每次把最多
            //   16MB 读进来重新解析 —— 在 A1 满速写日志时这是持续性的主要开销之一。
            //   现在"任务名/配置组/路线"这些结构性字段由 BgiLogCenter 的单遍扫描
            //   增量维护（它从文件头一路扫到尾巴，永远不会漏掉"只在任务开始时写一次"
            //   的那些行），这里只做一次快照抄写。
            _statusRebuildRunning = true;
            try
            {
                ApplyCenterSnapshot();
            }
            finally { _statusRebuildRunning = false; }
        }

        /// <summary>
        /// 把 BgiLogCenter 快照里的"全天"统计抄到界面状态上。
        /// ★ 只有一个数据源：报错计数 / 分组拾取 / 当前配置组 / 今日累计，
        ///   全部来自那一次单遍流式扫描，界面不再自己读文件。
        /// </summary>
        private void ApplyCenterSnapshot()
        {
            try
            {
                var st = AccountStateCenter.GetByUser(_currentStatusUser);
                if (st == null) return;

                _bgiStatus.ErrorCount = st.ErrorCount;
                _bgiStatus.WarnCount = st.WarnCount;
                _bgiStatus.FailCount = st.FailCount;

                var snap = BgiLogCenter.Snapshot(st.AccountId);
                if (snap != null)
                {
                    _bgiStatus.DailyByGroup.Clear();
                    foreach (var g in snap.PicksByGroup)
                        _bgiStatus.DailyByGroup[g.Key] = new System.Collections.Generic.Dictionary<string, int>(
                            g.Value, StringComparer.OrdinalIgnoreCase);

                    if (snap.CurrentGroup.Length > 0)
                    {
                        _bgiStatus.ConfigGroup = snap.CurrentGroup;
                        _bgiStatus.PicksRoute = snap.CurrentGroup;
                        if (snap.PicksByGroup.TryGetValue(snap.CurrentGroup, out var cur))
                        {
                            _bgiStatus.RoutePicks.Clear();
                            foreach (var kv in cur) _bgiStatus.RoutePicks[kv.Key] = kv.Value;
                        }
                    }
                }
                ApplyStatusToUi();
            }
            catch { }
        }

        /// <summary>当前状态面板对应的用户名。</summary>
        private string _currentStatusUser = "";

        private void ApplyStatusToUi()
        {
            try
            {
                var currentState = _sessions.FirstOrDefault(x => x.Session.Id == _statusSessionId)?.Session;
                if (currentState != null)
                {
                    var accountState = AccountStateCenter.GetByUser(currentState.UserName);
                    string currentName = accountState?.CurrentGroup ?? "";
                    if (currentName.Length > 0)
                    { _bgiStatus.ConfigGroup = currentName; _bgiStatus.PicksRoute = currentName; _bgiStatus.TaskName = ""; }
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
                // ★ 停止优先：日志里已经出现停止锚点（执行结束 / 未成功完成 /
                //   取消 / 停止快捷键 F12）时，不再显示"当前配置组"，
                //   直接写明已停止 + 停的是哪个组（用户要求不再保留运行状态）。
                var stStop = AccountStateCenter.GetByUser(_currentStatusUser);
                bool stopped = stStop != null && stStop.StoppedAt != DateTime.MinValue
                               && stStop.CurrentGroup.Length == 0;

                if (stopped)
                {
                    SetText(StatusGroup, stStop!.LastGroup.Length > 0
                        ? "已停止（上一个：" + stStop.LastGroup + "）"
                        : "已停止");
                    SetText(StatusRound, "—");
                    SetText(StatusRoute, "—");
                    SetText(StatusEta, "脚本已停止 · " + stStop.StoppedAt.ToString("HH:mm:ss")
                        + (stStop.BgiRunning ? "（BGI 仍在运行）" : "（BGI 已退出）"));
                }
                else
                {
                    SetText(StatusGroup, _bgiStatus.GroupDisplay);
                    SetText(StatusRound, string.IsNullOrWhiteSpace(_bgiStatus.SafeRoundDisplay)
                        ? "—" : _bgiStatus.SafeRoundDisplay);
                    SetText(StatusRoute, _bgiStatus.RouteDisplay);
                    SetText(StatusEta, _bgiStatus.EtaDisplay);
                }

                // 今日报错：只显示错误/失败（纯警告太多，不占版面）
                if (_bgiStatus.ErrorLevel >= 2)
                {
                    string et = "错误 " + _bgiStatus.ErrorCount + " 次";
                    if (_bgiStatus.FailCount > 0) et += " · 失败 " + _bgiStatus.FailCount + " 次";
                    SetText(StatusError, et);
                    StatusError.Foreground = _bgiStatus.FailCount > 0
                        ? UiBrush.Get("RoseBrush")
                        : UiBrush.Get("AmberBrush");
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

        /// <summary>停止当前日志跟随（架构修正后改为退订数据中心）。</summary>
        private void StopFollower()
        {
            try { _follower?.Dispose(); } catch { }
            _follower = null;
            UnsubscribeLogCenter();
        }

        // ══════════ 数据中心订阅 ══════════
        //
        //  ★★ 用户硬性要求 ★★
        //    「访问日志的权限是数据中心，应用内其余地方只能从数据中心拿数据！」
        //
        //  BgiLogSnapshot.cs 文件头同样写着：
        //    「所有需要 BGI 日志的地方统一从这里取，不再各自读文件」
        //    「只读已绑定 BGI 路径的账号，未绑定的一律零 IO」
        //
        //  所以本页不再打开任何日志文件，只做两件事：
        //    ① 读 BgiLogCenter 的不可变快照（SnapshotByUser / Tail）；
        //    ② 订阅 BgiLogCenter.Changed，数据变了再刷新界面。
        //
        //  这样一来"未绑定不读日志"由数据中心统一保证，
        //  不需要在 UI 每个入口各写一遍判断（也就不会再漏）。

        private bool _logCenterSubscribed;

        private void SubscribeLogCenter()
        {
            if (_logCenterSubscribed) return;
            try
            {
                BgiLogCenter.Changed += OnLogCenterChanged;
                _logCenterSubscribed = true;
            }
            catch { }
        }

        private void UnsubscribeLogCenter()
        {
            if (!_logCenterSubscribed) return;
            try
            {
                BgiLogCenter.Changed -= OnLogCenterChanged;
                _logCenterSubscribed = false;
            }
            catch { }
        }

        /// <summary>把快照尾窗拼成文本（数据中心只提供不可变快照）。</summary>
        private static string TailTextOf(BgiLogSnapshot? snap)
        {
            try
            {
                if (snap == null || snap.Tail.Count == 0) return "";
                var sb = new System.Text.StringBuilder(snap.Tail.Count * 96);
                foreach (var line in snap.Tail) sb.AppendLine(line.Text);
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>数据中心有新数据 → 拉最新快照刷新界面（完全不碰文件）。</summary>
        private void OnLogCenterChanged()
        {
            try
            {
                var sess = _logSession;
                if (sess != null)
                {
                    // ★★ 2026-11-22 性能修复：不要再把整块文本重新赋值。★★
                    //
                    //   原来每次都把最多 400 行（60,000 字符）拼成一个新串
                    //   整体赋给 LogText.Text，再 ScrollToEnd()。
                    //   LogText 是 TextWrapping="Wrap" 的 TextBlock，一次赋值要：
                    //     断行全部文本 → 更新自动化子树 → Arrange → OnRender
                    //     → SimpleTextLine.Create
                    //   BGI 满速写日志时这个 Changed 每秒触发，
                    //   于是**每秒一次整窗文本重排**。
                    //
                    //   实测指纹（docs/research/PERF-3.3.4-20261122.md §3.2）：
                    //     AutomationPeer.UpdateSubtree      1.75%
                    //     Visual.PrecomputeRecursive        2.34%
                    //     TextBlock.OnRender → SimpleTextLine.Create
                    //
                    //   现在改成**增量追加**：只 append 比上次多出来的部分，
                    //   内容没变就一个字符都不碰。滚动只在用户本来就贴着底部时跟随。
                    Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            if (_logSession == null) return;
                            string text = TailTextOf(BgiLogCenter.SnapshotByUser(_logSession.UserName));
                            if (text.Length == 0) return;
                            if (text == _logText) return;          // 内容没变：零开销

                            // ★ 快照是**滑动尾窗**（BgiLogCenter.TailMaxLines = 800 行 /
                            //   20 万字符），老行会被从头部丢掉，所以不能指望
                            //   "上次内容是新内容的前缀"—— 窗口一滑就永远不成立。
                            //
                            //   正确做法：求出「已显示内容的尾部」与「新内容开头」的重叠长度，
                            //   只把重叠之后多出来的部分追加进去。日志面板本来就该保留历史，
                            //   所以旧行被快照丢掉时我们**不从界面删除**，只由 TrimLogText 封顶。
                            int overlap = Services.LogTailAppend.Overlap(_logText, text);
                            if (overlap > 0 && text.Length > overlap)
                            {
                                string added = text.Substring(overlap);
                                _logText = _logText + added;

                                // 用户正贴着底部才跟随滚动，否则别抢滚动位置
                                bool atBottom = false;
                                try
                                {
                                    atBottom = LogScroll.VerticalOffset
                                               >= LogScroll.ScrollableHeight - 24;
                                }
                                catch { }

                                LogText.Inlines.Add(new System.Windows.Documents.Run(added));
                                if (atBottom) { try { LogScroll.ScrollToEnd(); } catch { } }
                                TrimLogText();
                            }
                            else
                            {
                                // 结构变了（轮转/裁剪）：整块替换一次
                                _logText = text;
                                LogText.Text = text;
                                TrimLogText();
                                try { LogScroll.ScrollToEnd(); } catch { }
                            }
                        }
                        catch { }
                    });
                }

                // 左侧状态面板也从快照刷新（节流由 ScheduleStatusRebuild 保证）
                if (_statusSessionId > 0)
                    Dispatcher.InvokeAsync(() => { try { ApplyCenterSnapshot(); } catch { } });
            }
            catch { }
        }

        public MainWindow? MainWindowRef { get; set; }

        // ══════════ 执行 ══════════

        private async void RunAction_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is ActionVm vm && CanManageAction(vm.Model))
            {
                ActionList.SelectedItem = vm;
                await RunAsync(vm.Model, showRunHint: true);
            }
        }

        internal Task<(bool Ok, string Message)> RunOverviewActionAsync(ActionItem action)
            => RunAsync(action, showRunHint: false);

        internal void CreateOverviewAction(Account account) => ShowEditor(null, account);

        internal void EditOverviewFlow(AutomationFlow flow, Account account) => OpenFlowEditor(flow, account);

        private async Task<(bool Ok, string Message)> RunAsync(ActionItem action, bool showRunHint)
        {
            if (_busy) return (false, "已有动作正在执行");
            _busy = true;
            // 执行结果统一走日志总线 + 顶部状态提示（底部操作区已移除）
            if (showRunHint) SetRunHint("执行中…「" + action.Name + "」", ok: null);

            try
            {
                LogBus.Write(LogLevel.Run, "执行「" + action.Name + "」");
                var result = await Task.Run(() => ActionService.ExecuteAsync(action, App.Config));
                LogBus.Write(result.Ok ? LogLevel.Ok : LogLevel.Error, result.Message,
                    result.Command + Environment.NewLine + "耗时 " + result.ElapsedMs + " ms");

                if (showRunHint && CanManageAction(action))
                    SetRunHint((result.Ok ? "✓ " : "✕ ") + action.Name + " · " + result.Message, result.Ok);

                if (result.Ok && action.Kind != ActionKind.Launch)
                    RefreshSessions();
                return (result.Ok, result.Message);
            }
            catch (Exception ex)
            {
                LogBus.Error("执行失败：" + ex.Message);
                if (showRunHint && CanManageAction(action)) SetRunHint("✕ " + ex.Message, false);
                return (false, ex.Message);
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
                RunHint.Foreground = UiBrush.Get(ok == null ? "TextMuted" : ok.Value ? "MintBrush" : "RoseBrush");
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

        private void NewAction_Click(object sender, RoutedEventArgs e) => ShowManagedActionEditor(null);

        private void EditAction_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is ActionVm vm) ShowManagedActionEditor(vm.Model);
        }

        private void ShowManagedActionEditor(ActionItem? existing)
        {
            if (existing != null && !CanManageAction(existing)) return;
            if (FindActionAccount() is { } account) ShowEditor(existing, account);
        }

        private void ShowEditor(ActionItem? existing, Account? scopedAccount)
        {
            var dlg = new ActionEditorWindow(existing, App.Config, scopedAccount) { Owner = Window.GetWindow(this) };
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

        // Compare the rendered values, including command targets (configuration objects can be replaced).
        private readonly record struct CardState(AutomationFlow Flow, string Name, string Summary, AutomationStatusPresentation Status,
            bool Paused, int Ok, int Fail);
        private Account? _renderedAccount;
        private List<CardState>? _renderedCards;

        private void RefreshAutomationList()
        {
            try
            {
                if (AutomationList == null) return;
                var session = _logSession;
                var account = session == null ? null : App.Config.Accounts.FirstOrDefault(a =>
                    string.Equals(a.UserName, session.UserName, StringComparison.OrdinalIgnoreCase));
                var cards = new List<CardState>();
                if (account != null)
                {
                    var now = DateTime.Now;
                    foreach (var f in App.Config.Automations)
                    {
                        if (f.AccountId != account.Id) continue;
                        var (ok, fail) = f.TodayCounts(now);
                        var status = AutomationStatusPresentation.ForFlow(f, now);
                        cards.Add(new(f, f.DisplayName, AutomationSummary(f, status.Text), status, f.Paused || !f.Enabled, ok, fail));
                    }
                }
                var meta = session == null ? "（先选一个账号）" : account == null ? "该账号还没有配置"
                    : cards.Count == 0 ? "" : "共 " + cards.Count + " 个流程";
                if (ReferenceEquals(account, _renderedAccount) && AutomationMeta.Text == meta
                    && _renderedCards != null && cards.SequenceEqual(_renderedCards)) return;

                _renderedCards = null; // Retry on the next tick if building a card fails.
                AutomationList.Children.Clear();
                AutomationMeta.Text = meta;
                if (account != null)
                {
                    if (cards.Count == 0)
                        AutomationList.Children.Add(new TextBlock { Text = "暂无自动化任务", FontSize = 10.5, Foreground = UiBrush.Get("TextMuted"), Margin = new Thickness(4,5,4,5) });
                    foreach (var card in cards)
                        AutomationList.Children.Add(BuildAutomationCard(card, account));
                }
                _renderedAccount = account;
                _renderedCards = cards;
            }
            catch { }
        }

        private Border BuildAutomationCard(CardState state, Account account)
        {
            var f = state.Flow;
            bool paused = state.Paused;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Ellipse
            {
                Width = 8, Height = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = state.Status.Indicator
            };
            grid.Children.Add(dot);

            // 名称 + 说明
            var sp = new StackPanel { Margin = new Thickness(9, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

            // 名称行：流程名 + 今日成功/失败次数（同一行，小字，颜色区分）
            // ★ 2.17.31 用户要求："在流程名称后面，要小字显示今日成功次数与失败次数。
            //   成功用绿色数字，失败用红色数字。"
            var nameLine = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            nameLine.Children.Add(new TextBlock
            {
                Text = state.Name,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiBrush.Get("TextPrimary"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            //   ★ 顺序固定为「成功 → 失败」，数字用等宽感更强的小字号；
            //     两个都是 0 时不显示（避免新流程一上来就挂两个 0 显得像报错）。
            var (todayOk, todayFail) = (state.Ok, state.Fail);   // 按业务日读取，过期视为 0
            if (todayOk > 0 || todayFail > 0)
            {
                nameLine.Children.Add(new TextBlock
                {
                    Text = "  " + todayOk,
                    FontSize = 9.5, FontWeight = FontWeights.SemiBold,
                    Foreground = UiBrush.Get("MintBrush"),   // 绿 = 今日成功次数
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "今日成功 " + todayOk + " 次"
                });
                nameLine.Children.Add(new TextBlock
                {
                    Text = " / " + todayFail,
                    FontSize = 9.5, FontWeight = FontWeights.SemiBold,
                    Foreground = UiBrush.Get("RoseBrush"),   // 红 = 今日失败次数
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "今日失败 " + todayFail + " 次"
                });
            }
            sp.Children.Add(nameLine);
            sp.Children.Add(new TextBlock
            {
                Text = state.Summary,
                FontSize = 9.5,
                Foreground = UiBrush.Get("TextMuted"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(sp, 1);
            grid.Children.Add(sp);

            // 按钮组
            var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            if (state.Status.DoneToday)
            {
                var btnToday = new AutomationTodayButton
                {
                    Margin = new Thickness(0, 0, 6, 0),
                    IsEnabled = state.Status.CanClearToday
                };
                btnToday.Click += (_, _) =>
                {
                    AutomationStatusPresentation.ClearToday(App.Config, f, DateTime.Now);
                    RefreshAutomationList();
                };
                btns.Children.Add(btnToday);
            }

            // ★★ 2.17.25：「自动运行」开关与「手动触发」按钮必须一眼分开 ★★
            //
            //   原来两个按钮都是 \uE768（播放），只是 ToolTip 不同
            //   （"恢复"/"暂停" vs "立即运行一次"），用户实测分不清，
            //   也分不清"这个流程到底会不会自己跑"。
            //
            //   现在：
            //     · 自动开关 = 带文字的胶囊开关（"自动运行"/"已停止"），
            //       开=绿色实心、关=灰色空心 —— 它表示的是一个**持续状态**；
            //     · 手动触发 = 单独的闪电图标 + "运行一次"文字，
            //       永远表示**一次性的立即执行**，与开关状态互不影响。
            var btnPause = new Button
            {
                // ★ 用 PillButton：IconButton 的模板在 hover 时会把 Background 覆盖掉，
                //   "开=绿/关=灰"的状态色就看不见了。
                Style = (Style)FindResource("PillButton"),
                Height = 26, MinWidth = 78, Padding = new Thickness(9, 0, 9, 0),
                Content = BuildAutoSwitchContent(!paused),
                Background = paused
                    ? UiBrush.Get("TextPrimary", 0x22)
                    : UiBrush.Get("MintBrush", 0x30),
                BorderBrush = paused
                    ? UiBrush.Get("TextPrimary", 0x40)
                    : UiBrush.Get("MintBrush", 0x90),
                ToolTip = paused
                    ? "自动运行：已停止。点击恢复——之后会按条件自动执行，不需要再点。".Replace("恢复", "开启")
                    : "自动运行：开启中。条件满足时自动执行；点击可停止（停止后只能手动运行一次）。",
                Tag = f
            };
            btnPause.Click += (_, __) =>
            {
                f.ToggleAutomaticRun();
                ConfigStore.Save(App.Config);
                RefreshAutomationList();
            };
            btns.Children.Add(btnPause);

            // 手动触发：闪电图标 + 「运行一次」，表示"现在就跑一遍"（一次性）。
            // 与上面的自动开关是两个维度：停止状态下也能用，用来验证流程是否正常。
            var btnRun = new Button
            {
                Style = (Style)FindResource("PillButton"),
                Height = 26, MinWidth = 78, Padding = new Thickness(9, 0, 9, 0),
                Content = BuildRunOnceContent(),
                ToolTip = "手动触发：立即执行一次（不影响「自动运行」开关）",
                Tag = f,
                Margin = new Thickness(6, 0, 0, 0)
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
                Cursor = Cursors.Hand,
                Child = grid,
                Tag = f
            };
            card.SetResourceReference(Border.BackgroundProperty, "PanelBackground");

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

        /// <summary>
        /// 自动化卡片的一行摘要。
        ///
        /// ★ 被「仅管理挂机账号」排除的账号（主用户）：只显示**结构**（几个条件、几个动作），
        ///   不显示**运行数据**（实时触点满足数、今日运行状态）。
        ///
        ///   用户明确要求：「自动化应该要的，不然怎么控制，只是不显示运行数据之类的」
        ///   —— 流程列表是控制入口必须保留，但主用户不该被呈现成挂机目标。
        /// </summary>
        private static string AutomationSummary(AutomationFlow f, string state)
        {
            int contacts = f.Nodes.Count(n => n.IsContact);
            int outs = f.Nodes.Count(n => n.Kind == FlowNodeKind.Action);
            string structure = contacts + " 个条件 · " + outs + " 个动作";

            // 主用户（被全局排除）不显示**触点实时满足数**（那是挂机目标的运行数据），
            // 但"上次执行成功还是失败"是控制状态，必须给出来。
            if (!AccountScope.Allows(OwnerUserNameOf(f)))
                return structure + " · " + state;

            int lit = f.RuntimeStates.Count(kv => kv.Value.Value && f.FindNode(kv.Key)?.IsContact == true);
            return contacts + " 个条件（满足 " + lit + "） · " + outs + " 个动作 · " + state;
        }

        /// <summary>「自动运行」开关的按钮内容：图标 + 文字，开=绿、关=灰。</summary>
        private static FrameworkElement BuildAutoSwitchContent(bool on)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock
            {
                Text = on ? "\uE769" : "\uE768",
                FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = on ? UiBrush.Get("MintBrush") : UiBrush.Get("TextSecondary")
            });
            sp.Children.Add(new TextBlock
            {
                Text = on ? "自动运行" : "已停止",
                FontSize = 10.5, Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = on ? UiBrush.Get("MintBrush") : UiBrush.Get("TextSecondary")
            });
            return sp;
        }

        /// <summary>「运行一次」按钮的内容：闪电图标 + 文字（中性色，不表示持续状态）。</summary>
        private static FrameworkElement BuildRunOnceContent()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock
            {
                Text = "\uE945",   // LightningBolt：一次性触发
                FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiBrush.Get("TextSecondary")
            });
            sp.Children.Add(new TextBlock
            {
                Text = "运行一次",
                FontSize = 10.5, Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiBrush.Get("TextSecondary")
            });
            return sp;
        }
        /// <summary>取流程所属账号的用户名（找不到返回空，视为不在管理范围内）。</summary>
        private static string OwnerUserNameOf(AutomationFlow f)
        {
            try
            {
                foreach (var a in App.Config.Accounts)
                    if (a != null && a.Id == f.AccountId) return a.UserName ?? "";
            }
            catch { }
            return "";
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

                CreateOverviewFlow(account);
            }
            catch (Exception ex)
            {
                MessageBox.Show("新建流程失败：" + ex.Message, "SoulLink");
            }
        }

        internal void CreateOverviewFlow(Account account)
        {
            var flow = new AutomationFlow
            {
                AccountId = account.Id,
                Name = account.UserName + " 自动化",
                CreatedAt = DateTime.Now,
                Enabled = true,
                Paused = false
            };
            OpenFlowEditor(flow, account, isNew: true);
            RefreshAutomationList();
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
            if (sender is not Button { Tag: ActionVm vm } || !CanManageAction(vm.Model)) return;
            var r = MessageBox.Show("确定删除动作「" + vm.Model.Name + "」？", "SoulLink",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK || !CanManageAction(vm.Model) || !App.Config.Actions.Remove(vm.Model)) return;
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

        private void RefreshSessions_Click(object sender, RoutedEventArgs e) => RefreshSessions(force: true);

    }

    // ══════════ 视图模型 ══════════

    public class ActionVm
    {
        public ActionItem Model { get; }
        public ActionVm(ActionItem model) => Model = model;

        public string Name => Model.Name;
        public string AccessibleName => $"执行快捷动作，{Name}";

        public string ExeName => string.IsNullOrWhiteSpace(Model.ExePath) ? "未设置程序" : Path.GetFileName(Model.ExePath);

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
                    case ActionKind.Close: return UiBrush.Get("RoseBrush", 0x33);
                    case ActionKind.Restart: return UiBrush.Get("AmberBrush", 0x33);
                    default: return UiBrush.Get("MintBrush", 0x33);
                }
            }
        }

        public Brush BadgeFg
        {
            get
            {
                switch (Model.Kind)
                {
                    case ActionKind.Close: return UiBrush.Get("RoseBrush");
                    case ActionKind.Restart: return UiBrush.Get("AmberBrush");
                    default: return UiBrush.Get("MintBrush");
                }
            }
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
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DotBrush)));
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
        public SolidColorBrush DotBrush => UiBrush.Get(Session.IsConsoleSession || Session.IsActive
            ? "MintBrush" : Session.IsDisconnected ? "AmberBrush" : "TextMuted");
        public Color DotColor => DotBrush.Color;

        /// <summary>
        /// BGI 日志最后写入时间（由外部按会话号填充）。
        /// null = 该会话没有运行 BGI。
        /// </summary>
        public DateTime? BgiLastWrite { get; set; }

        /// <summary>
        /// 该账号是否**已绑定** BGI 路径（由外部填充）。
        /// ★ 未绑定 = 我们根本没有它的日志路径，连"跑没跑"都不知道，
        ///   不能说成"BGI 未运行"（那是编造结论）。
        /// </summary>
        public bool BgiBound { get; set; }

        /// <summary>最近一次判定为"停止"的时刻（由外部填充）。</summary>
        public DateTime BgiStoppedAt { get; set; } = DateTime.MinValue;

        /// <summary>当前是否真的有一个配置组在跑（由外部填充）。</summary>
        public bool BgiGroupRunning { get; set; }

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

                // ★ 未绑定：不是"异常红"，而是"未知"——用黄色，避免误导成故障。
                if (!BgiBound) return 1;

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
        public System.Windows.Media.Brush StateBrush => DotBrush;

        /// <summary>
        /// 该用户是否在 **RDP 配置的账号列表**里。
        /// ★ 用户要求：「RDP 配置里没有的用户直接隐藏」。
        ///   系统里可能有别人登录的会话（a1/a4 之类的残留、临时登录），
        ///   它们不属于我们管理的挂机账号，不该出现在会话列表里。
        /// </summary>
        public bool IsManaged
        {
            get
            {
                try
                {
                    string bare = Services.SessionService.BareName(Session.UserName);
                    var accounts = App.Config?.Accounts;
                    if (accounts == null) return false;
                    foreach (var a in accounts)
                    {
                        if (a == null) continue;
                        if (string.Equals(Services.SessionService.BareName(a.UserName),
                                bare, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                catch { }
                return false;
            }
        }

        /// <summary>
        /// 主会话不显示 BGI 段。
        /// ★ 用户要求：「主用户不需要检测 BGI，应该直接未绑定」。
        ///   再加一条：**不在配置里的账号**也不检测 BGI（它们根本不是挂机账号）。
        /// </summary>
        public System.Windows.Visibility BgiVisibility
            => Session.IsConsoleSession || !IsManaged
                ? System.Windows.Visibility.Collapsed
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
            // ★ 绑定状态变了也要刷新 BGI 那一行的文字/颜色。
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(BgiText)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(BgiBrush)));
        }

        /// <summary>
        /// 三种状态：就绪 / 未投送 / 投送失败。
        /// ★ 用户要求：「主用户这里也不需要检测投送」。
        ///   主会话是操作者自己的桌面，从不投送代理，显示"通道未投送"
        ///   既无意义又误导（看起来像出了问题）。
        ///   不在 RDP 配置里的账号同理。
        /// </summary>
        public string ChannelText
        {
            get
            {
                if (Session.IsConsoleSession) return "当前会话";
                if (!IsManaged) return "";
                return HasChannel ? "通道就绪"
                     : ChannelError.Length > 0 ? ("投送失败：" + ShortError(ChannelError))
                     : "通道未投送";
            }
        }

        private static string ShortError(string msg)
        {
            var m = (msg ?? "").Replace("\n", " ").Trim();
            const string prefix = "投送注入代理失败：";
            if (m.StartsWith(prefix, StringComparison.Ordinal)) m = m.Substring(prefix.Length);
            return m.Length <= 28 ? m : m.Substring(0, 28) + "…";
        }

        /// <summary>通道状态颜色：就绪绿、未投送灰蓝、失败红。</summary>
        public System.Windows.Media.Brush ChannelBrush => UiBrush.Get(HasChannel ? "MintBrush" : ChannelError.Length > 0 ? "RoseBrush" : "TextMuted");

        /// <summary>
        /// **BGI 活跃度**文字（单行显示时用，与连接状态用固定颜色圆点隔开）。
        /// 主会话不显示（用户在操作，不需要看 BGI）。
        /// </summary>
        public string BgiText
        {
            get
            {
                if (Session.IsConsoleSession) return "";
                // ★ 未绑定优先：没绑定就不知道它跑没跑，不能说"未运行"。
                if (!BgiBound) return "BGI 未绑定";
                if (BgiLastWrite == null) return "BGI 未运行";

                // ★ 已停止：日志里出现停止锚点后，不能再说"空闲 N 分钟"——
                //   空闲是"还在跑但没输出"，停止是"根本不在跑"，两者含义完全不同。
                if (BgiStoppedAt != DateTime.MinValue && BgiGroupRunning == false)
                    return "BGI 已停止 " + BgiStoppedAt.ToString("HH:mm");

                var span = DateTime.Now - BgiLastWrite.Value;
                if (span.TotalSeconds < 5) return "BGI 刚活动";
                if (span.TotalSeconds < 60) return "BGI 空闲 " + (int)span.TotalSeconds + " 秒";
                if (span.TotalMinutes < 60) return "BGI 空闲 " + (int)span.TotalMinutes + " 分钟";
                return "BGI 空闲 " + (int)span.TotalHours + " 小时";
            }
        }

        /// <summary>BGI 行的颜色，按活跃度分级。</summary>
        public System.Windows.Media.Brush BgiBrush => UiBrush.Get(BgiLevel switch
        {
            0 => "MintBrush",
            1 => "AmberBrush",
            _ => "RoseBrush"
        });

        /// <summary>"空闲"文字的颜色，按 BGI 活跃度分级。</summary>
        public System.Windows.Media.Brush IdleBrush => BgiBrush;
    }
}
