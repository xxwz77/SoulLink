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
            RefreshWhitelistText();
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

            // ★ 只列出「配置里填过」的账号，其余一概不显示。
            //   AccountScope.Sessions 只过滤主用户，会把 A1/A4 这种
            //   没配置的账号也列出来 —— 本页是会话瘦身，不该让用户
            //   对着一个不属于自己管理的会话乱点。
            var configured = (App.Config?.Accounts ?? new List<Models.Account>())
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.UserName))
                .Select(a => a.UserName.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _sessions = AccountScope.Sessions(SessionService.FilterUsers(all))
                .Where(s => s != null && configured.Contains(s.UserName ?? ""))
                .ToList();

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
            try
            {
                var sid = _current.Id;
                _all = await Task.Run(() => ProcessService.List(sid)).ConfigureAwait(true);
                ApplyFilter();          // ★ 必须调用：把数据推给 DataGrid，否则列表永远是空的
                SubText.Text = _current.UserName + " · 会话 #" + _current.Id + " · 共 " + _all.Count + " 个可见进程";
                RefreshSlimSummary(sid);
                if (ProcessCountText != null) ProcessCountText.Text = "（共 " + _all.Count + " 个）";
            }
            catch (Exception ex)
            {
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

        /// <summary>
        /// 该账号是否就是"当前正在使用的那个用户"（主用户）。
        ///
        /// ★ 判定：与 SoulLink 自身处于同一 Windows 会话号。
        ///   主用户不参与会话瘦身 —— 他开着桌面、跑着日常程序，
        ///   动他的启动项会影响正常使用。列表里直接不显示，不给误操作的机会。
        /// </summary>
        private static bool IsMainUserAccount(string userName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(userName)) return false;
                int mine = System.Diagnostics.Process.GetCurrentProcess().SessionId;
                var sessions = Services.SessionService.QueryAsync().GetAwaiter().GetResult();
                foreach (var s in sessions)
                {
                    if (s == null || s.Id != mine || s.UserName.Length == 0) continue;
                    return string.Equals(s.UserName, userName, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// 白名单展示：把「会清理什么 / 不清理什么」直接写在界面上。
        /// 用户点清理之前应该清楚会动哪些进程，而不是盲点。
        /// </summary>
        /// <summary>清理名单一行（界面绑定）。</summary>
        public sealed class KillItem
        {
            public string Name { get; set; } = "";
            /// <summary>内置 / 自定义。</summary>
            public string SourceText { get; set; } = "";
            /// <summary>内置项不允许移除（想撤掉请用「恢复默认」，或它本来就在白名单里）。</summary>
            public System.Windows.Visibility RemoveVisibility { get; set; } = System.Windows.Visibility.Visible;
        }

        private readonly System.Collections.ObjectModel.ObservableCollection<KillItem> _killItems = new();

        /// <summary>
        /// 刷新「清理范围」展示。
        /// 上半部是逐项可移除的当前名单；下半部是固定说明（不清理什么）。
        /// </summary>
        private void RefreshWhitelistText()
        {
            try
            {
                KillListItems.ItemsSource = _killItems;
                _killItems.Clear();

                var builtIn = Services.SessionSlimmer.BuiltInKillNames;
                var effective = Services.SessionSlimmer.EffectiveKillNames();

                foreach (var name in effective.OrderBy(x => x))
                {
                    bool isBuiltIn = Services.SessionSlimmer.IsBuiltIn(name);
                    _killItems.Add(new KillItem
                    {
                        Name = name,
                        SourceText = isBuiltIn ? "内置" : "自定义",
                        // ★ 内置项也允许移除：默认勾选的东西不该绑架用户。
                        //   移除的内置项记入 SessionSlimDisabled，「恢复默认」可还原。
                        RemoveVisibility = System.Windows.Visibility.Visible
                    });
                }

                var sb = new System.Text.StringBuilder();
                sb.Append("不清理：游戏/挂机进程、SoulLink 自身、输入法、");
                sb.Append("explorer/桌面、RDP 连接组件，以及所有未列出的进程。");
                sb.AppendLine();
                sb.Append("注：开始菜单搜索、小组件属于系统组件，结束后会被系统自动拉起，");
                sb.Append("需用「启动项优化」从注册表层面关闭。");
                WhitelistText.Text = sb.ToString();
            }
            catch { }
        }

        /// <summary>
        /// 点击账号胶囊切换是否参与优化。
        /// 用整个 Border 接收点击（圆点开关本身不是独立控件），
        /// 这样点击面积更大，符合"整行可点"的直觉。
        /// </summary>
        private void PolicyRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if ((sender as System.Windows.FrameworkElement)?.DataContext is PolicyRow row)
                    row.Enabled = !row.Enabled;
            }
            catch { }
        }

        /// <summary>移除一项自定义清理进程。</summary>
        private void RemoveOne_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? name = (sender as System.Windows.FrameworkElement)?.Tag as string;
                if (string.IsNullOrWhiteSpace(name)) return;

                bool changed = Services.SessionSlimmer.RemoveFromList(name);
                if (changed)
                    Services.LogBus.Write(Services.LogLevel.Info, "清理名单已更新", "已移除：" + name);
                RefreshWhitelistText();
                RefreshSlimSummary(_current?.Id ?? 0);
            }
            catch (Exception ex) { Services.LogBus.Error("移除清理项失败", ex.Message); }
        }

        /// <summary>恢复默认：清空自定义名单，只保留内置。</summary>
        private void ResetList_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cfg = App.Config;
                if (cfg?.Settings?.SessionSlimKillList == null) { RefreshWhitelistText(); return; }
                int custom = cfg.Settings.SessionSlimKillList?.Count ?? 0;
                int disabled = cfg.Settings.SessionSlimDisabled?.Count ?? 0;
                if (custom == 0 && disabled == 0)
                {
                    Views.ThemedMessage.Info(Window.GetWindow(this), "已是默认",
                        "当前清理范围就是内置白名单，没有改动过。");
                    return;
                }

                bool ok = Views.ThemedMessage.Confirm(Window.GetWindow(this), "恢复默认",
                    "将清空 " + custom + " 个自定义项，并恢复 " + disabled +
                    " 个被移除的内置项。\n\n确定吗？");
                if (!ok) return;

                Services.SessionSlimmer.ResetList();
                RefreshWhitelistText();
                RefreshSlimSummary(_current?.Id ?? 0);
                Services.LogBus.Write(Services.LogLevel.Info, "清理名单已恢复默认", "");
            }
            catch (Exception ex) { Services.LogBus.Error("恢复默认失败", ex.Message); }
        }

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
                    // ★ 主用户（当前控制台会话）**完全不出现在列表里**：
                    //   他开着桌面、跑着用户自己在用的程序，改他的启动项会影响日常使用。
                    //   判定依据：与当前进程同一会话号 = 主用户。
                    if (IsMainUserAccount(a.UserName)) continue;

                    _policyRows.Add(new PolicyRow
                    {
                        UserName = a.UserName,
                        // 列表里只剩挂机账号，默认全选
                        Enabled = true,
                        StateText = "可优化"
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
                    + "只影响这些账号，其他用户完全不受影响。");
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
            // 搜索框已移除（本页定位改为「会话瘦身」），直接展示全部。
            //
            // ★ 同名进程合并成一行并标注数量 —— msedgewebview2 这类
            //   一个宿主就拉起十几个渲染进程，逐行列出全是重复内容，
            //   既难扫读又占满列表。合并后仍保留全部 PID（见 PidText），
            //   「结束进程」按合并前的实例集合执行，行为不变。
            var rows = _all
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var first = g.First();
                    var pids = g.Select(x => x.Pid).ToList();
                    if (pids.Count == 1) return first;

                    // 多个同名进程：合成一条，Name 保持原名，Additional 记录数量
                    var merged = new RemoteProcess
                    {
                        Pid = pids[0],
                        Name = first.Name,
                        ExePath = first.ExePath,
                        Title = first.Title,
                        StartTime = first.StartTime,
                    };
                    merged.DuplicatePids = pids;
                    return merged;
                })
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 用 DataGrid.ItemsSource 迭代会触发“同一个可枚举不能被同时使用”，这里走默认视图
            try
            {
                Grid.ItemsSource = null;
                var view = new System.Windows.Data.ListCollectionView(rows);
                Grid.ItemsSource = view;
                Services.LogBus.Write(Services.LogLevel.Info, "进程列表已更新",
                    "行数=" + (rows?.Count ?? 0) + " ItemsSource=" + (Grid.ItemsSource != null));
            }
            catch (Exception ex)
            {
                Services.LogBus.Error("进程列表渲染失败", ex.GetType().Name + ": " + ex.Message);
            }
        }


        private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadSessionsAsync(_current);

        private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool has = Grid.SelectedItems.Count > 0;
            var kill = FindName("BtnKillAdvanced") as System.Windows.Controls.Button;
            if (kill != null) kill.IsEnabled = has;
            var add = FindName("BtnAddToList") as System.Windows.Controls.Button;
            if (add != null) add.IsEnabled = has;
        }

        /// <summary>
        /// 把选中的进程加入用户自定义清理名单。
        ///
        /// ★ 保护名单里的进程不允许加入 —— 按钮会直接拒绝并说明原因，
        ///   而不是"加进去了但实际不生效"（那样用户会困惑）。
        /// </summary>
        private void AddToList_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picked = Grid.SelectedItems.Cast<RemoteProcess>().ToList();
                if (picked.Count == 0) return;

                var added = new List<string>();      // 这次真的加进去的
                var alreadyIn = new List<string>();  // 本来就在清理名单里
                var refused = new List<string>();    // 系统保护，不允许

                var cfg = App.Config;
                if (cfg?.Settings == null) return;
                cfg.Settings.SessionSlimKillList ??= new List<string>();

                // 当前已生效的全部清理项（内置 + 自定义 - 被移除的）
                var effective = new HashSet<string>(
                    Services.SessionSlimmer.EffectiveKillNames(),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var p in picked)
                {
                    string name = Services.SessionSlimmer.Normalize(p.Name);
                    if (name.Length == 0) continue;

                    if (Services.SessionSlimmer.IsProtected(name))
                    {
                        refused.Add(name);
                        continue;
                    }

                    // ★ 关键修正：如果它已经在生效名单里（无论是内置还是之前加过），
                    //   就明确说"已在名单中"，绝不能报"已加入"——
                    //   原来这里跳过之后仍然弹成功提示，用户以为加上了其实没变化。
                    if (effective.Contains(name))
                    {
                        alreadyIn.Add(name);
                        continue;
                    }

                    cfg.Settings.SessionSlimKillList.Add(name);
                    effective.Add(name);
                    added.Add(name);
                }

                if (added.Count > 0)
                {
                    Services.ConfigStore.Save(cfg);
                    Services.LogBus.Write(Services.LogLevel.Ok, "清理名单已更新",
                        "已加入：" + string.Join("、", added));
                }

                var msg = new System.Text.StringBuilder();
                if (added.Count > 0)
                    msg.Append("已加入：" + string.Join("、", added));
                if (alreadyIn.Count > 0)
                {
                    if (msg.Length > 0) msg.Append("\n\n");
                    msg.Append("已在名单中（无需重复添加）：" + string.Join("、", alreadyIn));
                }
                if (refused.Count > 0)
                {
                    if (msg.Length > 0) msg.Append("\n\n");
                    msg.Append("未加入（系统保护，不允许清理）：" + string.Join("、", refused));
                }
                if (msg.Length == 0) msg.Append("没有可添加的进程。");

                RefreshWhitelistText();
                RefreshSlimSummary(_current?.Id ?? 0);
                Views.ThemedMessage.Info(Window.GetWindow(this), "清理名单", msg.ToString());
            }
            catch (Exception ex)
            {
                Services.LogBus.Error("加入清理名单失败", ex.Message);
            }
        }

        private async void Kill_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || Grid.SelectedItems.Count == 0) return;

            var picked = Grid.SelectedItems.Cast<RemoteProcess>().ToList();

            // ★ 合并行要展开成全部 PID 再杀，否则用户以为清理干净了、
            //   实际同一个进程还剩十几个实例。
            var targets = new List<RemoteProcess>();
            foreach (var p in picked)
            {
                if (p.DuplicatePids.Count > 0)
                {
                    foreach (var pid in p.DuplicatePids)
                        targets.Add(new RemoteProcess { Pid = pid, Name = p.Name, ExePath = p.ExePath });
                }
                else targets.Add(p);
            }

            string names = string.Join("、", picked.Take(3).Select(t => t.Name))
                         + (picked.Count > 3 ? " 等" : "");
            string detail = targets.Count != picked.Count
                ? "\n\n共 " + targets.Count + " 个进程实例（含同名合并项）"
                : "";
            if (!Dialogs.Confirm("确定结束 " + targets.Count + " 个进程？\n\n" + names + detail,
                                 "结束进程", true)) return;

            int ok = 0;
            var errors = new List<string>();
            await Task.Run(() =>
            {
                foreach (var t in targets)
                {
                    try
                    {
                        using var p = System.Diagnostics.Process.GetProcessById(t.Pid);
                        p.Kill();
                        ok++;
                    }
                    catch (Exception ex) { errors.Add(t.Name + "(" + t.Pid + ")：" + ex.Message); }
                }
            }).ConfigureAwait(true);

            Services.LogBus.Write(errors.Count == 0 ? Services.LogLevel.Ok : Services.LogLevel.Warn,
                "结束进程", "成功 " + ok + " 个" + (errors.Count > 0 ? "，失败 " + errors.Count + " 个" : ""));

            await LoadProcessesAsync().ConfigureAwait(true);
        }

        private void OpenNew_Click(object sender, RoutedEventArgs e)
        {
            // 该按钮已从界面移除，保留方法避免 XAML 引用报错。
        }
    }

    /// <summary>会话下拉框的一项。由 ProcessPage 内部使用。</summary>
    internal sealed class SessionItem
    {
        public SessionInfo Session { get; }
        public SessionItem(SessionInfo s) { Session = s; }
        public string Label => Session.UserName + "   (#" + Session.Id + " " + Session.Badge + ")";
        public string TagText => "#" + Session.Id + " · " + Session.Badge;
    }
}
