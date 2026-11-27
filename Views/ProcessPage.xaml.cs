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
        private SessionInfo? _current;
        private SessionSlimmer.SessionStat? _summary;
        private int _sessionRequest, _processRequest;
        private bool _rebuildingSessions;
        private bool _loadingSessions;
        private bool _loading;
        private bool _cleanupInProgress;

        public ProcessPage()
        {
            InitializeComponent();
            UserCombo.ItemsSource = _users;
            CleanupAccounts.ItemsSource = _users;
        }

        public void Activate()
        {
            if (StartupTab.IsChecked == true)
            {
                StartupView.Activate();
                return;
            }
            if (SystemTab.IsChecked == true)
            {
                RefreshServiceStates();
                RebuildPolicyRows();
                RefreshPolicySwitches();
                return;
            }
            if (DefenderTab.IsChecked == true)
            {
                RefreshDefenderExclusions();
                return;
            }
            RefreshWhitelistText();
            try
            {
                _loadingPolicySwitch = true;
                KillEnabledCheck.IsChecked = App.Config?.Settings?.SessionSlimKillEnabled != false;
            }
            finally { _loadingPolicySwitch = false; }
            LoadSessions();
        }

        private void ProcessGrid_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (Grid.Template.FindName("DG_ScrollViewer", Grid) is ScrollViewer inner
                && (e.Delta < 0 ? inner.VerticalOffset < inner.ScrollableHeight : inner.VerticalOffset > 0)) return;
            e.Handled = true;
            PageScroll.ScrollToVerticalOffset(PageScroll.VerticalOffset - e.Delta / 2.0);
        }

        /// <summary>将机器级和用户级策略开关与当前配置和注册表状态对齐。</summary>
        private void RefreshPolicySwitches()
        {
            try
            {
                _loadingPolicySwitch = true;
                bool machine = App.Config?.Settings?.SessionSlimMachinePolicyEnabled == true
                               || Services.SessionSlimmer.IsMachinePolicyEnabled();
                if (MachinePolicyCheck != null) MachinePolicyCheck.IsChecked = machine;
                if (UserPolicyCheck != null)
                    UserPolicyCheck.IsChecked = App.Config?.Settings?.SessionSlimUserPolicyEnabled == true;
            }
            catch { }
            finally { _loadingPolicySwitch = false; }
        }

        /// <summary>清理会话外围进程总开关（SessionSlimKillEnabled）。</summary>
        private void KillEnabledCheck_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                if (KillEnabledCheck == null || _loadingPolicySwitch) return;
                bool enable = KillEnabledCheck.IsChecked == true;
                if (App.Config?.Settings != null)
                {
                    App.Config.Settings.SessionSlimKillEnabled = enable;
                    ConfigStore.Save(App.Config);
                }
                UpdateCleanupButtons();
                Services.LogBus.Write(Services.LogLevel.Ok, "会话瘦身总开关",
                    enable ? "已开启：可清理外围进程" : "已关闭：不再清理任何外围进程");
            }
            catch (Exception ex) { Services.LogBus.Error("会话瘦身总开关设置失败", ex.Message); }
        }

        /// <summary>指定要查看的会话（从「在线会话」页跳转过来）。</summary>
        public void FocusSession(SessionInfo s)
        {
            ProcessTab.IsChecked = true;
            _ = LoadSessionsAsync(s);
        }

        public void LoadSessions() => _ = LoadSessionsAsync(null);

        private async Task LoadSessionsAsync(SessionInfo? focus)
        {
            int request = ++_sessionRequest;
            _loadingSessions = true;
            UpdateCleanupButtons();
            List<SessionInfo> all;
            try { all = await SessionService.QueryAsync().ConfigureAwait(true); }
            catch (Exception ex)
            {
                if (request == _sessionRequest)
                {
                    _loadingSessions = false;
                    LogBus.Error("读取会话失败", ex.Message);
                    UpdateCleanupButtons();
                }
                return;
            }
            if (request != _sessionRequest) return;
            _loadingSessions = false;

            var preferred = focus ?? _current;
            var checkedSessions = _users.Where(item => item.IsCleanupSelected).Select(item => item.Session).ToArray();
            _rebuildingSessions = true;
            try
            {
                _users.Clear();
                foreach (var s in SessionService.FilterUsers(all).Where(SessionSlimmer.CanSelectSession).DistinctBy(s => s.Id))
                    _users.Add(new SessionItem(s) { IsCleanupSelected = checkedSessions.Any(previous => IsSameSession(previous, s)) });

                UserCombo.SelectedItem = preferred == null
                    ? _users.FirstOrDefault(u => u.Session.IsActive) ?? _users.FirstOrDefault()
                    : _users.FirstOrDefault(u => IsSameSession(u.Session, preferred));
            }
            finally { _rebuildingSessions = false; }
            await SelectSessionAsync();
        }

        private static bool IsSameSession(SessionInfo? first, SessionInfo? second)
            => first != null && second != null && first.Id == second.Id
                && string.Equals(first.UserName, second.UserName, StringComparison.OrdinalIgnoreCase);

        private async void UserCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_rebuildingSessions && Grid != null) await SelectSessionAsync();
        }

        private Task SelectSessionAsync()
        {
            _current = (UserCombo.SelectedItem as SessionItem)?.Session;
            _summary = null;
            _all.Clear();
            Grid.ItemsSource = null;
            ProcessCountText.Text = "";
            return LoadProcessesAsync();
        }

        private async Task LoadProcessesAsync(string? cleanupResult = null)
        {
            var target = _current;
            int request = ++_processRequest;
            _summary = null;
            _loading = target != null;
            SubText.Text = target == null
                ? (_users.Count == 0 ? "当前没有可选的挂机会话" : "请选择要查看的会话")
                : target.UserName + " · 会话 #" + target.Id;
            SlimSummary.Text = target == null ? "选择会话后显示可清理的进程" : "正在扫描当前会话…";
            UpdateCleanupButtons();
            if (target == null) return;

            try
            {
                var (processes, stats) = await Task.Run(() =>
                    (ProcessService.List(target.Id, includeMemory: true), SessionSlimmer.Count(target.Id, target.UserName)));
                if (request != _processRequest || !ReferenceEquals(_current, target)) return;
                _all = processes;
                ApplyFilter();
                ProcessCountText.Text = "（共 " + _all.Count + " 个）";
                _summary = stats;
                SlimSummary.Text = FormatSummary(stats, cleanupResult);
            }
            catch (Exception ex)
            {
                if (request == _processRequest) SlimSummary.Text = "读取进程失败：" + ex.Message;
            }
            finally
            {
                if (request == _processRequest)
                {
                    _loading = false;
                    UpdateCleanupButtons();
                }
            }
        }

        /// <summary>账号策略行（界面绑定）。</summary>
        public sealed class PolicyRow : System.ComponentModel.INotifyPropertyChanged
        {
            private bool _enabled;
            private string _state = "";

            public string AccountId { get; init; } = "";
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

        /// <summary>清理名单一行（界面绑定）。</summary>
        public sealed class KillItem
        {
            public string Name { get; set; } = "";
            public string SourceText { get; set; } = "";
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

                var effective = Services.SessionSlimmer.EffectiveKillNames();

                foreach (var name in effective.OrderBy(x => x))
                {
                    bool isBuiltIn = Services.SessionSlimmer.IsBuiltIn(name);
                    _killItems.Add(new KillItem
                    {
                        Name = name,
                        SourceText = isBuiltIn ? "内置" : "自定义"
                    });
                }
                var optional = SessionSlimmer.OptionalProcesses
                    .Where(item => !effective.Contains(item.Name, StringComparer.OrdinalIgnoreCase)).ToList();
                OptionalKillChoices.ItemsSource = optional;
                OptionalKillChoices.Visibility = optional.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        private void PolicyAccount_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is CheckBox { DataContext: PolicyRow row } box)
                {
                    row.Enabled = box.IsChecked == true;
                    row.StateText = "";
                    var settings = App.Config.Settings;
                    settings.SessionSlimEdgeAccounts ??= new();
                    settings.SessionSlimEdgeAccounts[row.AccountId] = row.Enabled;
                    ConfigStore.Save(App.Config);
                }
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
                RefreshAfterListChange();
            }
            catch (Exception ex) { Services.LogBus.Error("移除清理项失败", ex.Message); }
        }

        /// <summary>恢复默认：清空自定义名单，只保留内置。</summary>
        private void ResetList_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cfg = App.Config;
                if (cfg?.Settings == null) return;
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
                RefreshAfterListChange();
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
                    if (!SessionSlimmer.CanSelectUser(a.UserName)) continue;
                    bool enabled = cfg.Settings.SessionSlimEdgeAccounts?.TryGetValue(a.Id, out bool selected) == true && selected;

                    _policyRows.Add(new PolicyRow
                    {
                        AccountId = a.Id,
                        UserName = a.UserName,
                        Enabled = enabled
                    });
                }

                PolicyHint.Text = _policyRows.Count == 0 ? "没有可配置的挂机账号。"
                    : "对勾选的账号禁用 Edge 预启动与后台运行。";
            }
            catch { }
        }

        private void RefreshAfterListChange()
        {
            RefreshWhitelistText();
            _ = LoadProcessesAsync();
        }

        private static string FormatSummary(SessionSlimmer.SessionStat stat, string? cleanupResult)
        {
            string total = "共 " + stat.Total + " 个进程 · 专用提交 " + stat.TotalBytes / 1048576 + " MiB";
            string cleanup = stat.OwnerVerified
                ? "可清理 " + stat.Killable + " 个 · " + stat.KillableBytes / 1048576 + " MiB"
                    + (stat.Unverified > 0 ? " · " + stat.Unverified + " 个名单内进程未通过身份或权限核验" : "")
                : "无法确认会话归属";
            return total + "　" + cleanup
                + (stat.PolicyOnly > 0 ? "　搜索和小组件 " + stat.PolicyOnly + " 个" : "")
                + (cleanupResult != null ? "\n" + cleanupResult : "");
        }

        private void UpdateCleanupButtons()
        {
            if (Grid == null || BtnSlim == null || BtnKillAdvanced == null || BtnAddToList == null) return;
            string? blocked = _loadingSessions ? "正在刷新会话"
                : _current == null ? "请选择要清理的会话"
                : _cleanupInProgress ? "正在清理"
                : _loading ? "正在扫描当前会话"
                : App.Config?.Settings?.SessionSlimKillEnabled != true ? "请先启用进程清理"
                : _summary == null ? "未取得进程扫描结果，请刷新"
                : !_summary.OwnerVerified ? "当前会话归属未核验"
                : null;
            var checkedCount = _users.Count(item => item.IsCleanupSelected);
            bool usesCurrentSummary = checkedCount == 0
                || (checkedCount == 1 && _users.Any(item => item.IsCleanupSelected && IsSameSession(item.Session, _current)));
            string? bulkBlocked = usesCurrentSummary
                ? blocked ?? (_summary?.Killable > 0 ? null
                    : _summary?.Unverified > 0 ? "名单内的进程未通过身份或权限核验"
                    : "当前会话里没有正在运行的名单内进程")
                : _cleanupInProgress ? "正在清理"
                    : _loadingSessions ? "正在刷新会话"
                    : App.Config?.Settings?.SessionSlimKillEnabled != true ? "请先启用进程清理"
                    : null;
            bool hasSelection = Grid.SelectedItems.Cast<RemoteProcess>().Any(p => !SessionSlimmer.IsProtected(p.Name));
            BtnSlim.IsEnabled = bulkBlocked == null;
            BtnSlim.ToolTip = bulkBlocked ?? "预览并确认要清理的进程";
            BtnSlim.Content = checkedCount switch
            {
                0 => "清理外围进程",
                1 => "清理所选会话",
                _ => "清理所选 " + checkedCount + " 个会话"
            };
            BtnKillAdvanced.IsEnabled = blocked == null && hasSelection;
            BtnAddToList.IsEnabled = !_loadingSessions && !_loading && !_cleanupInProgress && hasSelection;
            if (KillListItems != null) KillListItems.IsEnabled = !_cleanupInProgress;
            if (OptionalKillChoices != null) OptionalKillChoices.IsEnabled = !_cleanupInProgress;
            if (BtnResetList != null) BtnResetList.IsEnabled = !_cleanupInProgress;
            if (CleanupAccountPicker != null)
            {
                CleanupAccountPicker.Visibility = _users.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                CleanupAccountPicker.IsEnabled = !_loadingSessions && !_cleanupInProgress;
                CleanupAccountHint.Text = checkedCount == 0
                    ? "批量清理账号（可多选）。未勾选时仅清理正在查看的会话。"
                    : "已选 " + checkedCount + " 个会话。进程列表仍显示正在查看的会话。";
                UserCombo.IsEnabled = !_cleanupInProgress;
                RefreshSessionsButton.IsEnabled = !_cleanupInProgress;
            }
        }

        private void CleanupAccount_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox { DataContext: SessionItem item } box) return;
            bool selected = box.IsChecked == true;
            if (item.IsCleanupSelected == selected) return;
            item.IsCleanupSelected = selected;
            UpdateCleanupButtons();
        }

        private async void Slim_Click(object sender, RoutedEventArgs e)
        {
            if (!BtnSlim.IsEnabled) return;
            var targets = _users.Where(item => item.IsCleanupSelected).Select(item => item.Session).ToArray();
            if (targets.Length == 0 && _current is { } current) targets = [current];
            if (targets.Length == 0) return;
            int request = _sessionRequest;
            _cleanupInProgress = true;
            UpdateCleanupButtons();
            try
            {
                var requested = targets.Select(target => (target.Id, target.UserName)).ToArray();
                var preview = await Task.Run(() => SessionSlimmer.CountSessions(requested));
                if (request != _sessionRequest || !IsVisible || ProcessTab.IsChecked != true) return;
                var details = targets.Select(target =>
                {
                    var stat = preview[target.Id];
                    string affected = stat.Killable == 0
                        ? (stat.OwnerVerified ? "当前无可清理进程。" : "无法确认会话归属，将跳过。")
                        : string.Join("、", stat.KillableDetail.OrderByDescending(item => item.Value.Bytes)
                            .Select(item => item.Key + " ×" + item.Value.Count));
                    return target.UserName + " · 会话 #" + target.Id + "\n" + affected;
                });
                string summary = string.Join("\n\n", details);
                if (preview.Values.All(stat => stat.Killable == 0))
                {
                    ThemedMessage.Info(Window.GetWindow(this), "没有可清理进程", summary);
                    await LoadProcessesAsync();
                    return;
                }
                if (!ThemedMessage.Confirm(Window.GetWindow(this), "清理外围进程",
                    summary + "\n\n匹配的应用可能关闭，未保存内容可能丢失。", true)) return;
                if (request != _sessionRequest || !IsVisible || ProcessTab.IsChecked != true) return;

                int ended = 0;
                foreach (var target in targets.Where(target => preview[target.Id].Killable > 0))
                {
                    var result = await Task.Run(() => SessionSlimmer.Slim(target.Id, target.UserName, preview[target.Id].CleanupTargets));
                    ended += result.Killed;
                    LogBus.Write(result.Any ? LogLevel.Ok : LogLevel.Info, "会话瘦身 · " + target.UserName, result.Message);
                }
                if (IsVisible && ProcessTab.IsChecked == true)
                    await LoadProcessesAsync("已请求结束 " + ended + " 个外围进程。");
            }
            catch (Exception ex) { LogBus.Error("会话瘦身失败", ex.Message); }
            finally { _cleanupInProgress = false; UpdateCleanupButtons(); }
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
                    "将对以下账号关闭「Edge 预启动 / 后台」的自动启动：\n\n"
                    + "  " + names + "\n\n"
                    + "只影响这些账号，其他用户完全不受影响。\n"
                    + "小组件和任务栏搜索使用全机策略。");
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

        private async void MachinePolicyCheck_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                if (MachinePolicyCheck == null) return;
                bool enable = MachinePolicyCheck.IsChecked == true;
                // ★ 首次刷新时 IsChecked 赋值也会触发事件，用 _loadingPolicySwitch 挡住回写
                if (_loadingPolicySwitch) return;

                if (enable)
                {
                    bool ok = Views.ThemedMessage.Confirm(Window.GetWindow(this),
                        "启用全机设置",
                        "将写入机器级组策略（HKLM，需管理员）：\n\n"
                        + "  · 关闭小组件面板（Win+W 失效、任务栏小组件图标消失）\n"
                        + "  · 关闭任务栏搜索（搜索框消失、开始菜单打字搜索失效）\n\n"
                        + "影响这台机器的所有用户。已有系统策略保留，Windows 可能在重新登录后应用。");
                    if (!ok) { RefreshPolicySwitches(); return; }

                    var r = await Task.Run(() => Services.SessionSlimmer.ApplyMachinePolicy(true)).ConfigureAwait(true);
                    if (App.Config?.Settings != null) { App.Config.Settings.SessionSlimMachinePolicyEnabled = r.Ok; ConfigStore.Save(App.Config); }
                    Services.LogBus.Write(r.Ok ? Services.LogLevel.Ok : Services.LogLevel.Error, "全机设置", r.Message);
                    Views.ThemedMessage.Info(Window.GetWindow(this), "全机设置", r.Message);
                    if (!r.Ok) RefreshPolicySwitches();
                }
                else
                {
                    var r = await Task.Run(() => Services.SessionSlimmer.ApplyMachinePolicy(false)).ConfigureAwait(true);
                    if (App.Config?.Settings != null) { App.Config.Settings.SessionSlimMachinePolicyEnabled = false; ConfigStore.Save(App.Config); }
                    Services.LogBus.Write(r.Ok ? Services.LogLevel.Ok : Services.LogLevel.Error, "全机设置回滚", r.Message);
                    Views.ThemedMessage.Info(Window.GetWindow(this), "全机设置回滚", r.Message);
                    RefreshPolicySwitches();
                }
            }
            catch (Exception ex)
            {
                Services.LogBus.Error("全机设置失败", ex.Message);
                RefreshPolicySwitches();
            }
        }

        private async void RevertMachine_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var r = await Task.Run(() => Services.SessionSlimmer.ApplyMachinePolicy(false)).ConfigureAwait(true);
                if (App.Config?.Settings != null) { App.Config.Settings.SessionSlimMachinePolicyEnabled = false; ConfigStore.Save(App.Config); }
                RefreshPolicySwitches();
                Services.LogBus.Write(r.Ok ? Services.LogLevel.Ok : Services.LogLevel.Error, "全机设置回滚", r.Message);
                Views.ThemedMessage.Info(Window.GetWindow(this), "全机设置回滚", r.Message);
            }
            catch (Exception ex)
            {
                Services.LogBus.Error("全机设置回滚失败", ex.Message);
                RefreshPolicySwitches();
            }
        }

        private void UserPolicyCheck_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                if (UserPolicyCheck == null || _loadingPolicySwitch) return;
                bool enable = UserPolicyCheck.IsChecked == true;
                if (App.Config?.Settings != null)
                {
                    App.Config.Settings.SessionSlimUserPolicyEnabled = enable;
                    ConfigStore.Save(App.Config);
                }
                Services.LogBus.Write(Services.LogLevel.Ok, "Edge 预启动策略",
                    enable ? "已开启：注入代理下次启动时写入该账号 HKCU" : "已关闭：注入代理下次启动时删值恢复默认");
            }
            catch (Exception ex) { Services.LogBus.Error("Edge 预启动策略设置失败", ex.Message); }
        }

        private bool _loadingPolicySwitch;

        private void ApplyFilter()
        {
            // 搜索框已移除（本页定位改为「会话瘦身」），直接展示全部。
            //
            // ★ 同名进程合并成一行并标注数量 —— msedgewebview2 这类
            //   一个宿主就拉起十几个渲染进程，逐行列出全是重复内容，
            //   既难扫读又占满列表。合并后仍保留全部 PID（见 PidText），
            //   「结束进程」按合并前的实例集合执行，行为不变。
            var rows = _all
                .GroupBy(p => p.ExePath, StringComparer.OrdinalIgnoreCase)
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
                        PrivateBytes = g.Sum(p => p.PrivateBytes),
                    };
                    merged.DuplicatePids = pids;
                    return merged;
                })
                .OrderByDescending(p => p.PrivateBytes)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
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


        private void Refresh_Click(object sender, RoutedEventArgs e) => LoadSessions();

        /// <summary>打开「会话瘦身说明」弹窗。</summary>
        private void OpenSlimHelp_Click(object sender, RoutedEventArgs e)
        {
            try { SessionsSlimPage.ShowFor(Window.GetWindow(this)); }
            catch (Exception ex) { Services.LogBus.Error("打开会话瘦身说明失败", ex.Message); }
        }

        private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCleanupButtons();

        private void AddOptional_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: SessionSlimmer.OptionalProcess item }) return;
            if (SessionSlimmer.EffectiveKillNames().Contains(item.Name, StringComparer.OrdinalIgnoreCase))
            {
                ThemedMessage.Info(Window.GetWindow(this), "已在清理名单", item.Label + " 已在清理名单中。");
                return;
            }
            if (!ThemedMessage.Confirm(Window.GetWindow(this), "加入清理名单", item.Label + "\n\n" + item.Impact)) return;
            if (!SessionSlimmer.AddToList(item.Name)) return;
            ConfigStore.Save(App.Config);
            RefreshAfterListChange();
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

                    if (!SessionSlimmer.AddToList(name)) continue;
                    effective.Add(name);
                    added.Add(name);
                }

                if (added.Count > 0)
                {
                    Services.ConfigStore.Save(cfg);
                    Services.LogBus.Write(Services.LogLevel.Ok, "清理名单已更新",
                        "已加入：" + string.Join("、", added));
                    RefreshAfterListChange();
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

                Views.ThemedMessage.Info(Window.GetWindow(this), "清理名单", msg.ToString());
            }
            catch (Exception ex)
            {
                Services.LogBus.Error("加入清理名单失败", ex.Message);
            }
        }

        private async void Kill_Click(object sender, RoutedEventArgs e)
        {
            var session = _current;
            if (session == null || !BtnKillAdvanced.IsEnabled) return;
            int request = _processRequest;
            var pids = Grid.SelectedItems.Cast<RemoteProcess>()
                .Where(row => !SessionSlimmer.IsProtected(row.Name))
                .SelectMany(row => row.DuplicatePids.Count > 0 ? (IEnumerable<int>)row.DuplicatePids : new[] { row.Pid })
                .ToHashSet();
            var targets = _all.Where(row => pids.Contains(row.Pid)).ToList();
            if (targets.Count == 0 || !ThemedMessage.Confirm(Window.GetWindow(this), "结束进程",
                session.UserName + " · 会话 #" + session.Id + "\n\n"
                + "结束 " + targets.Count + " 个实例 · " + string.Join("、", targets.Select(t => t.Name).Distinct())
                + "\n\n未保存内容可能丢失。", true)) return;
            if (request != _processRequest || !ReferenceEquals(session, _current)) return;

            _cleanupInProgress = true;
            UpdateCleanupButtons();
            try
            {
                var (count, errors) = await Task.Run(() => SessionSlimmer.EndSelected(session.Id, session.UserName, targets));
                LogBus.Write(errors.Count == 0 ? LogLevel.Ok : LogLevel.Warn, "结束进程 · " + session.UserName,
                    "已请求结束 " + count + " 个" + (errors.Count == 0 ? "" : "\n" + string.Join("\n", errors)));
                if (IsSameSession(session, _current)) await LoadProcessesAsync();
            }
            catch (Exception ex) { LogBus.Error("结束进程失败", ex.Message); }
            finally { _cleanupInProgress = false; UpdateCleanupButtons(); }
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
        public bool IsCleanupSelected { get; set; }
        public string Label => Session.UserName + "   (#" + Session.Id + " " + Session.Badge + ")";
        public string TagText => "#" + Session.Id + " · " + Session.Badge;
    }
}
