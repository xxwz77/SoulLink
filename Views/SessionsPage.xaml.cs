using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Views
{
    public partial class SessionsPage : UserControl
    {
        private readonly ObservableCollection<SessionVm> _items = new ObservableCollection<SessionVm>();
        private readonly DispatcherTimer _timer;
        private bool _refreshing;

        public event Action<SessionInfo>? OpenProcessesRequested;
        public event Action<SessionInfo>? SaveAsActionRequested;
        public event Action<SessionInfo>? ConnectConsoleRequested;

        public SessionsPage()
        {
            InitializeComponent();
            Grid.ItemsSource = _items;

            // 3 秒：实测每次 query user 约 28ms，3 秒间隔下每天约 2.9 万次进程创建，
            // 累计 CPU 时间分散在全天，对挂机场景足够且开销可控
            // （2 秒会把进程创建量推到 4.3 万次，收益不明显）。
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _timer.Tick += (_, __) =>
            {
                if (AutoRefresh.IsChecked == true && IsVisible && !_refreshing) Refresh();
            };
            Loaded += (_, __) => _timer.Start();
            Unloaded += (_, __) => _timer.Stop();
        }

        public void Activate()
        {
            if (_items.Count == 0) Refresh();
        }

        public async void Refresh()
        {
            if (_refreshing) return;
            _refreshing = true;
            try
            {
                string? keep = (Grid.SelectedItem as SessionVm)?.Session.UserName;

                // ★ 优先读状态中心的快照（它每秒统一刷新一次，所有页面共用）。
                //   只有中心还没启动/太旧时才自己查，避免首帧空白。
                List<SessionInfo> all;
                var snap = AccountStateCenter.Sessions;
                bool snapFresh = snap.Count > 0
                    && (DateTime.Now - AccountStateCenter.LastRefresh).TotalSeconds < 5;
                if (snapFresh) all = snap;
                else all = await SessionService.QueryAsync().ConfigureAwait(true);

                var users = AccountScope.Sessions(SessionService.FilterUsers(all));

                _items.Clear();
                foreach (var s in users) _items.Add(new SessionVm(s));

                CountText.Text = users.Count == 0
                    ? "当前没有用户会话"
                    : "共 " + users.Count + " 个会话在线 · 每 3 秒自动刷新";

                if (keep != null)
                {
                    var again = _items.FirstOrDefault(i => i.Session.UserName == keep);
                    if (again != null) Grid.SelectedItem = again;
                }
                else if (_items.Count > 0)
                {
                    Grid.SelectedItem = _items[0];
                }
            }
            finally
            {
                _refreshing = false;
            }
        }

        public SessionInfo? SelectedSession => (Grid.SelectedItem as SessionVm)?.Session;

        private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var s = SelectedSession;
            if (s == null)
            {
                DetailUser.Text = "—";
                DetailSub.Text = "未选择会话";
                DetailSid.Text = "—";
                DetailConn.Text = "—";
                return;
            }
            DetailUser.Text = s.UserName;
            DetailSub.Text = s.Badge + " · 空闲 " + s.IdleDisplay;
            DetailSid.Text = "#" + s.Id;
            DetailConn.Text = string.IsNullOrWhiteSpace(s.SessionName) ? "(无)" : s.SessionName;
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

        private void OpenProgram_Click(object sender, RoutedEventArgs e)
        {
            var s = SelectedSession;
            if (s == null) { Toast("请先选择一个会话。"); return; }

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "要在这个用户桌面上打开哪个程序？",
                Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                CheckFileExists = false
            };
            if (dlg.ShowDialog() != true) return;

            RunOpen(s, dlg.FileName);
        }

        private async void RunOpen(SessionInfo s, string exe, string? args = null)
        {
            // 不再需要密码：投送走 SYSTEM 计划任务 + WTSQueryUserToken
            var account = App.Config.Accounts.FirstOrDefault(a =>
                string.Equals(a.UserName, s.UserName, StringComparison.OrdinalIgnoreCase));
            if (account == null)
            {
                account = new Account { UserName = s.UserName };
                App.Config.Accounts.Add(account);
                ConfigStore.Save(App.Config);
            }

            var target = new ActionService.Target { Account = account, Session = s };
            var tool = await Task.Run(() => ActionService.EnsureToolAsync(App.Config));
            if (!tool) { Toast("需要管理员权限才能投送进程。"); return; }

            LogBus.Write(LogLevel.Run, "在 " + s.UserName + " 的会话中打开 " + System.IO.Path.GetFileName(exe));
            var r = await Task.Run(() => ActionService.LaunchAsync(target, exe, args));
            LogBus.Write(r.Ok ? LogLevel.Ok : LogLevel.Error, r.Message);
            Toast(r.Message);
        }

        private void ConnectConsole_Click(object sender, RoutedEventArgs e)
        {
            var s = SelectedSession;
            if (s == null) { Toast("请先选择一个会话。"); return; }
            ConnectConsoleRequested?.Invoke(s);
        }

        private void ViewProcesses_Click(object sender, RoutedEventArgs e)
        {
            var s = SelectedSession;
            if (s == null) { Toast("请先选择一个会话。"); return; }
            OpenProcessesRequested?.Invoke(s);
        }

        private void SaveAsAction_Click(object sender, RoutedEventArgs e)
        {
            var s = SelectedSession;
            if (s == null) { Toast("请先选择一个会话。"); return; }
            SaveAsActionRequested?.Invoke(s);
        }

        private void Toast(string msg)
        {
            ToolTip tip = new ToolTip { Content = msg, PlacementTarget = this, IsOpen = true };
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (_, __) => { tip.IsOpen = false; t.Stop(); };
            t.Start();
        }
    }
}
