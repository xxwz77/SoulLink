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

        public void Activate() => LoadSessions();

        /// <summary>指定要查看的会话（从「在线会话」页跳转过来）。</summary>
        public void FocusSession(SessionInfo s)
        {
            _ = LoadSessionsAsync(s);
        }

        public void LoadSessions() => _ = LoadSessionsAsync(null);

        private async Task LoadSessionsAsync(SessionInfo? focus)
        {
            var all = await SessionService.QueryAsync().ConfigureAwait(true);
            _sessions = SessionService.FilterUsers(all);

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