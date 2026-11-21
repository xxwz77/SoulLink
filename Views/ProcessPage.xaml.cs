using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Views
{
    public partial class ProcessPage : UserControl
    {
        private readonly ObservableCollection<SessionItem> _users = new ObservableCollection<SessionItem>();
        private List<RemoteProcess> _all = new List<RemoteProcess>();
        private List<SessionInfo> _sessions = new List<SessionInfo>();
        private SessionInfo? _current;
        private bool _loading;

        public ProcessPage()
        {
            InitializeComponent();
            UserCombo.ItemsSource = _users;
        }

        public void Activate()
        {
            RebuildPolicyRows();
            LoadSessions();
        }

        /// <summary>指定要查看的会话（从「在线会话」页跳转过来）。</summary>
        public void FocusSession(SessionInfo s)
        {
            _ = LoadSessionsAsync(s);
        }

        public void LoadSessions() => _ = LoadSessionsAsync(null);

        private async Task LoadSessionsAsync(SessionInfo? focus)
        {
            var all = await SessionService.QueryAsync().ConfigureAwait(true);
            _sessions = AccountScope.Sessions(SessionService.FilterUsers(all));

            _users.Clear();
            foreach (var s in _sessions)
                _users.Add(new SessionItem(s));

            if (_sessions.Count == 0)
            {
                SubText.Text = "当前没有用户登录会话";
                _all.Clear();
                Grid.ItemsSource = null;
                return;
            }

            var pick = focus != null ? _users.FirstOrDefault(u => u.Session.Id == focus.Id) : null;
            UserCombo.SelectedItem = pick ?? _users.FirstOrDefault();
        }

        private async void UserCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (UserCombo.SelectedItem is SessionItem item)
            {
                _current = item.Session;
                await LoadProcessesAsync();
            }
        }

        private async Task LoadProcessesAsync()
        {
            if (_current == null) return;
            _loading = true;
            StatusText.Text = "正在读取会话 " + _current.Id + " 的进程…";
            try
            {
                var sid = _current.Id;
                _all = await Task.Run(() => ProcessService.List(sid)).ConfigureAwait(true);
                ApplyFilter();
                SubText.Text = _current.UserName + " · 会话 #" + _current.Id + " · 共 " + _all.Count + " 个可见进程";
                StatusText.Text = _all.Count == 0
                    ? "该会话当前没有可见的用户程序（管理员进程可能不显示）。"
                    : "已加载 " + _all.Count + " 个进程 · 可多选后点「结束进程」";
                RefreshSlimSummary(sid);
            }
            catch (Exception ex)
            {
                StatusText.Text = "读取失败：" + ex.Message;
            }
            finally
            {
                _loading = false;
            }
        }

        // ── 会话瘦身 ────────────────────────────────────────────────────
        //
        // 界面上的两块功能：
        //   ① 清理外围进程：只杀白名单里、且**不会自动重启**的进程
        //      （msedge / 手机连接 / 游戏栏）。SearchHost、Widgets 会自己回来，
        //      单纯杀毫无意义，要靠注册表策略。
        //   ② 启动项优化：对勾选的账号写注册表策略，关闭小组件/Edge 预启动/
        //      手机连接的自动启动。**只影响勾选的账号**，其余完全不动。

        /// <summary>账号策略行（界面绑定）。</summary>
        public sealed class PolicyRow : System.ComponentModel.INotifyPropertyChanged
        {
            private bool _enabled;
            private string _state = "";

            public string UserName { get; set; } = "";
            public bool Enabled
            {
                get => _enabled;
                set { if (_enabled != value) { _enabled = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Enabled))); } }
            }
            public string StateText
            {
                get => _state;
                set { if (_state != value) { _state = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(StateText))); } }
            }
            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        }

        private readonly System.Collections.ObjectModel.ObservableCollection<PolicyRow> _policyRows = new();

        /// <summary>构建账号策略列表：只列举配置里填了用户名的账号。</summary>
        private void RebuildPolicyRows()
        {
            try
            {
                PolicyList.ItemsSource = _policyRows;
                _policyRows.Clear();

                var cfg = App.Config;
                if (cfg?.Accounts == null) return;

                foreach (var a in cfg.Accounts)
                {
                    if (string.IsNullOrWhiteSpace(a.UserName)) continue;
                    bool loggedOn = Services.SessionSlimmer.IsUserLoggedOn(a.UserName);
                    _policyRows.Add(new PolicyRow
                    {
                        UserName = a.UserName,
                        // 默认勾选：页面配置好的账号就是用户想优化的对象
                        Enabled = true,
                        StateText = loggedOn ? "已登录（需注销后才能改）" : "可优化"
                    });
                }

                if (_policyRows.Count == 0)
                    PolicyHint.Text = "配置里没有填用户名的账号，无法应用启动项优化。";
            }
            catch { }
        }

        /// <summary>刷新瘦身统计。</summary>
        private void RefreshSlimSummary(int sessionId)
        {
            try
            {
                if (sessionId <= 0) { SlimSummary.Text = "选择会话后显示进程构成"; return; }
                var st = Services.SessionSlimmer.Count(sessionId);
                long totalMb = st.TotalBytes / 1024 / 1024;
                long killMb = st.KillableBytes / 1024 / 1024;
                long policyMb = st.PolicyOnlyBytes / 1024 / 1024;

                var parts = new System.Text.StringBuilder();
                parts.Append("总计 ").Append(st.Total).Append(" 个 / ").Append(totalMb).Append(" MB");
                parts.Append("　可立即清理 ").Append(st.Killable).Append(" 个 / ").Append(killMb).Append(" MB");
                if (st.PolicyOnly > 0)
                    parts.Append("　需启动项优化 ").Append(st.PolicyOnly).Append(" 个 / ").Append(policyMb).Append(" MB");
                SlimSummary.Text = parts.ToString();

                BtnSlim.IsEnabled = st.Killable > 0;
            }
            catch (Exception ex) { SlimSummary.Text = "统计失败：" + ex.Message; }
        }

        private async void Slim_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_current == null || _current.Id <= 0) return;
                int sid = _current.Id;

                BtnSlim.IsEnabled = false;
                var before = Services.SessionSlimmer.Count(sid);
                long beforeMb = before.TotalBytes / 1024 / 1024;

                var r = await Task.Run(() => Services.SessionSlimmer.Slim(sid)).ConfigureAwait(true);

                // 给系统两秒回收
                await Task.Delay(2000).ConfigureAwait(true);
                await LoadProcessesAsync().ConfigureAwait(true);

                var after = Services.SessionSlimmer.Count(sid);
                long afterMb = after.TotalBytes / 1024 / 1024;
                long freed = beforeMb - afterMb;

                Services.LogBus.Write(r.Any ? Services.LogLevel.Ok : Services.LogLevel.Info,
                    "会话瘦身 · 会话 " + sid,
                    r.Message + "　内存 " + beforeMb + " MB → " + afterMb + " MB（" +
                    (freed >= 0 ? "减少 " : "增加 ") + Math.Abs(freed) + " MB）");

                SlimSummary.Text = r.Message + "　内存 " + beforeMb + " MB → " + afterMb + " MB";
                BtnSlim.IsEnabled = after.Killable > 0;
            }
            catch (Exception ex)
            {
                Services.LogBus.Error("会话瘦身失败", ex.Message);
                BtnSlim.IsEnabled = true;
            }
        }

        private async void Policy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picked = _policyRows.Where(x => x.Enabled).ToList();
                if (picked.Count == 0)
                {
                    Views.ThemedMessage.Info(Window.GetWindow(this), "未选择账号",
                        "请先勾选要应用启动项优化的账号。");
                    return;
                }

                // 明确告知影响范围，用户确认后才执行
                var names = string.Join("、", picked.Select(x => x.UserName));
                bool ok = Views.ThemedMessage.Confirm(Window.GetWindow(this),
                    "应用启动项优化",
                    "将对以下账号关闭「小组件 / Edge 预启动 / 手机连接」的自动启动：\n\n"
                    + "  " + names + "\n\n"
                    + "只影响这些账号，其他用户完全不受影响。\n"
                    + "该设置需要对应账号**注销后重新登录**才生效。\n\n"
                    + "是否继续？");
                if (!ok) return;

                BtnPolicy.IsEnabled = false;
                var results = new List<string>();
                foreach (var row in picked)
                {
                    var r = await Task.Run(() =>
                        Services.SessionSlimmer.ApplyStartupPolicy(row.UserName)).ConfigureAwait(true);
                    results.Add(row.UserName + "：" + r.Message);
                    row.StateText = r.Ok ? "已应用" : r.Message;
                }
                BtnPolicy.IsEnabled = true;

                Services.LogBus.Write(Services.LogLevel.Ok, "启动项优化", string.Join("\n", results));
                Views.ThemedMessage.Info(Window.GetWindow(this), "应用结果", string.Join("\n", results));
            }
            catch (Exception ex)
            {
                BtnPolicy.IsEnabled = true;
                Services.LogBus.Error("启动项优化失败", ex.Message);
            }
        }

        private void ApplyFilter()
        {
            string q = (SearchBox.Text ?? "").Trim();
            var rows = string.IsNullOrEmpty(q)
                ? _all
                : _all.Where(p => p.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                  || p.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                  || p.ExePath.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            // 用 DataGrid.ItemsSource 迭代会触发“同一个可枚举不能被同时使用”，这里走默认视图
            Grid.ItemsSource = null;
            var view = new System.Windows.Data.ListCollectionView(rows);
            Grid.ItemsSource = view;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadSessionsAsync(_current);

        private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            BtnKill.IsEnabled = Grid.SelectedItems.Count > 0;
        }

        private async void Kill_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || Grid.SelectedItems.Count == 0) return;

            var targets = Grid.SelectedItems.Cast<RemoteProcess>().ToList();
            string names = string.Join("、", targets.Take(3).Select(t => t.Name)) + (targets.Count > 3 ? " 等" : "");
            if (!Dialogs.Confirm("确定结束 " + targets.Count + " 个进程？\n\n" + names, "结束进程", true)) return;

            int ok = 0;
            var errors = new List<string>();
            foreach (var t in targets)
            {
                var r = await Task.Run(() => ProcessService.KillAsync(t));
                if (r.Ok) ok++;
                else errors.Add(r.Message);
            }

            LogBus.Write(ok > 0 ? LogLevel.Ok : LogLevel.Error,
                "结束进程 " + ok + "/" + targets.Count + " · 会话 #" + _current.Id,
                string.Join(Environment.NewLine, errors));

            StatusText.Text = "已结束 " + ok + " 个进程" + (errors.Count > 0 ? "，" + errors.Count + " 个失败" : "");
            await LoadProcessesAsync();
        }

        private async void OpenNew_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) { StatusText.Text = "请先选择一个用户会话。"; return; }

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "在 " + _current.UserName + " 的桌面打开程序",
                Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                CheckFileExists = false
            };
            if (dlg.ShowDialog() != true) return;

            // 不再需要密码：投送走 SYSTEM 计划任务 + WTSQueryUserToken
            var account = App.Config.Accounts.FirstOrDefault(a =>
                string.Equals(a.UserName, _current.UserName, StringComparison.OrdinalIgnoreCase));
            if (account == null)
            {
                account = new Account { UserName = _current.UserName };
                App.Config.Accounts.Add(account);
                ConfigStore.Save(App.Config);
            }

            var target = new ActionService.Target { Account = account, Session = _current };
            await Task.Run(() => ActionService.EnsureToolAsync(App.Config));
            var r = await Task.Run(() => ActionService.LaunchAsync(target, dlg.FileName, null));
            LogBus.Write(r.Ok ? LogLevel.Ok : LogLevel.Error, r.Message);
            StatusText.Text = r.Message;
            await LoadProcessesAsync();
        }
    }

    public class SessionItem
    {
        public SessionInfo Session { get; }
        public SessionItem(SessionInfo s) { Session = s; }
        public string Label => Session.UserName + "   (#" + Session.Id + " " + Session.Badge + ")";
        public string TagText => "#" + Session.Id + " · " + Session.Badge;
    }
}