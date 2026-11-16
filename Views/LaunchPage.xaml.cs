using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoulLink.Models;
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
                // BGI 日志跟着同一个节拍刷新（内部会判断文件是否变化）
                RefreshSelectedLog();
            };
            Loaded += (_, __) => _sessionTimer?.Start();
            Unloaded += (_, __) => _sessionTimer?.Stop();
        }

        public void Activate()
        {
            ReloadActions();
            RefreshSessions();
        }

        // ══════════ 数据加载 ══════════

        public void ReloadActions()
        {
            string? keepId = (ActionList.SelectedItem as ActionVm)?.Model.Id;
            _actions.Clear();
            foreach (var a in App.Config.Actions)
                _actions.Add(new ActionVm(a, App.Config));

            EmptyHint.Visibility = _actions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

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
                    var all = await SessionService.QueryAsync().ConfigureAwait(false);
                    var users = SessionService.FilterUsers(all);
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
                    foreach (var kv in BgiLogWatcher.Discover())
                        bgiTimes[kv.Key] = kv.Value.LastWrite;
                    channelSessions = BgiLogWatcher.DiscoverChannelSessions();
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

                    if (exist == null)
                    {
                        _sessions.Add(new SessionVm(u)
                        {
                            BgiLastWrite = bgiTime,
                            HasChannel = hasChannel
                        });
                    }
                    else
                    {
                        exist.BgiLastWrite = bgiTime;
                        exist.HasChannel = hasChannel;
                        exist.Session = u;      // 就地替换，触发绑定刷新
                    }
                }

                TipText.Text = users.Count == 0
                    ? "当前没有任何用户登录会话。请先让目标账号登录（RDP 或切换用户）。"
                    : "共 " + users.Count + " 个会话在线 · 点击查看其中运行的程序。";
            }
            catch { }
        }

        // ══════════ 选择 / 预览 ══════════

        private void ActionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateCommandPreview(ActionList.SelectedItem as ActionVm);
        }

        private void UpdateCommandPreview(ActionVm? vm)
        {
            if (vm == null)
            {
                CmdPreview.Text = "—";
                BtnRun.IsEnabled = false;
                return;
            }
            BtnRun.IsEnabled = !_busy;
            CmdPreview.Text = CommandBuilder.ForAction(vm.Model);

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
                RefreshSelectedLog(force: true);
            }
        }

        // ── BGI 实时日志 ─────────────────────────────────────────────────

        private SessionInfo? _logSession;
        private string _logText = "";
        private string _logFile = "";
        private DateTime _logLastWrite = DateTime.MinValue;

        /// <summary>刷新当前选中会话的 BGI 日志（内容有变化才更新界面）。</summary>
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
                            LogMeta.Text = sess.UserName + " · 未发现 BetterGI";
                            LogText.Text = "该会话中没有正在运行的 BetterGI 进程。\n\n" +
                                           "启动 BGI 后这里会自动显示它的实时日志。";
                            LogTitle.Text = "BGI 实时日志";
                        });
                        return;
                    }

                    // 文件没变化就不重读、不刷界面（避免滚动位置被打断）
                    if (!force && inst.LogFile == _logFile && inst.LastWrite == _logLastWrite)
                        return;

                    var (text, lastWrite) = BgiLogWatcher.TailLines(inst.LogFile, 60);
                    _logFile = inst.LogFile;
                    _logLastWrite = lastWrite;

                    string ago;
                    var span = DateTime.Now - lastWrite;
                    if (span.TotalSeconds < 5) ago = "刚刚";
                    else if (span.TotalMinutes < 1) ago = (int)span.TotalSeconds + " 秒前";
                    else if (span.TotalHours < 1) ago = (int)span.TotalMinutes + " 分钟前";
                    else ago = (int)span.TotalHours + " 小时前";

                    Dispatcher.InvokeAsync(() =>
                    {
                        LogTitle.Text = "BGI 实时日志 · " + sess.UserName;
                        LogMeta.Text = "最后写入 " + ago + " · PID " + inst.Pid;
                        if (text != _logText)
                        {
                            _logText = text;
                            LogText.Text = text;
                            // 自动滚到底部（最新日志）
                            try { LogScroll.ScrollToEnd(); } catch { }
                        }
                    });
                }
                catch { }
            });
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
            BtnRun.IsEnabled = false;
            LastRunText.Text = "执行中…";
            LastRunText.Foreground = (Brush)FindResource("TextMuted");

            try
            {
                LogBus.Write(LogLevel.Run, "执行「" + vm.Model.Name + "」");
                var result = await Task.Run(() => ActionService.ExecuteAsync(vm.Model, App.Config));
                LogBus.Write(result.Ok ? LogLevel.Ok : LogLevel.Error, result.Message,
                    result.Command + Environment.NewLine + "耗时 " + result.ElapsedMs + " ms");

                LastRunText.Text = (result.Ok ? "✓ " : "✕ ") + result.Message;
                LastRunText.Foreground = (Brush)FindResource(result.Ok ? "MintBrush" : "RoseBrush");
                vm.Refresh();
                RefreshBadges();

                if (result.Ok && vm.Model.Kind != ActionKind.Launch)
                    RefreshSessions();
            }
            catch (Exception ex)
            {
                LogBus.Error("执行失败：" + ex.Message);
                LastRunText.Text = "✕ " + ex.Message;
                LastRunText.Foreground = (Brush)FindResource("RoseBrush");
            }
            finally
            {
                _busy = false;
                BtnRun.IsEnabled = true;
            }
        }

        // ══════════ 编辑动作 ══════════

        private void NewAction_Click(object sender, RoutedEventArgs e) => ShowEditor(null);

        private void EditAction_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is ActionVm vm) ShowEditor(vm.Model);
        }

        private void ShowEditor(ActionItem? existing)
        {
            var dlg = new ActionEditorWindow(existing, App.Config) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true)
            {
                if (existing == null) App.Config.Actions.Add(dlg.Result);
                ReloadActions();
                ConfigStore.Save(App.Config);
                ActionList.SelectedItem = _actions.FirstOrDefault(v => v.Model.Id == dlg.Result.Id);
            }
        }

        private void ChangePassword_Click(object sender, RoutedEventArgs e)
        {
            if (ActionList.SelectedItem is not ActionVm vm) return;
            var account = App.Config.Accounts.FirstOrDefault(a => a.Id == vm.Model.AccountId);
            if (account == null)
            {
                MessageBox.Show("该动作没有绑定账号，请重新编辑动作。", "SoulLink");
                return;
            }
            var dlg = new PasswordWindow(account.UserName) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true)
            {
                ConfigStore.SetPassword(account, dlg.Password);
                ConfigStore.Save(App.Config);
                LogBus.Ok("已更新账号 " + account.UserName + " 的密码（DPAPI 加密保存）。");
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

        /// <summary>该会话是否已有注入通道（由外部按管道枚举结果填充）。</summary>
        public bool HasChannel { get; set; }

        /// <summary>投送通道状态文字。</summary>
        public string ChannelText => HasChannel ? "通道就绪" : "通道未投送";

        /// <summary>通道状态颜色：就绪绿、未投送灰蓝（不是错误，只是还没做）。</summary>
        public System.Windows.Media.Brush ChannelBrush
        {
            get
            {
                Color c = HasChannel
                    ? Color.FromRgb(0x8F, 0xE8, 0xD0)     // 就绪：绿
                    : Color.FromRgb(0x8B, 0x9C, 0xC8);    // 未投送：灰蓝
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