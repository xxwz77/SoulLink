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

        public LaunchPage()
        {
            InitializeComponent();
            ActionList.ItemsSource = _actions;
            SessionList.ItemsSource = _sessions;
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
            var spin = FindResource("SpinRefresh") as System.Windows.Media.Animation.Storyboard;
            try { spin?.Begin(RefreshGlyph); } catch { }

            _ = Task.Run(async () =>
            {
                var all = await SessionService.QueryAsync().ConfigureAwait(false);
                var users = SessionService.FilterUsers(all);
                await Dispatcher.InvokeAsync(() =>
                {
                    _sessions.Clear();
                    foreach (var s in users) _sessions.Add(new SessionVm(s));
                    if (users.Count == 0)
                        TipText.Text = "当前没有任何用户登录会话。请先让目标账号登录（RDP 或切换用户）。";
                    else if (_sessions.Count > 0)
                        TipText.Text = "共 " + users.Count + " 个会话在线 · 点击查看其中运行的程序。";
                });
            });
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
            if (SessionList.SelectedItem is SessionVm vm && MainWindowRef != null)
                MainWindowRef.OpenProcessesOf(vm.Session);
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

    public class SessionVm
    {
        public SessionInfo Session { get; }

        public SessionVm(SessionInfo s) { Session = s; }

        public string UserName => Session.UserName;
        public string SideText => "#" + Session.Id;
        public bool IsActive => Session.IsActive;

        public Color DotColor => Session.IsActive
            ? Color.FromRgb(0x41, 0xF0, 0xC0)
            : Session.IsDisconnected
                ? Color.FromRgb(0xFF, 0xC4, 0x6B)
                : Color.FromRgb(0x8B, 0x9C, 0xC8);

        public string SubText => Session.Badge + " · 空闲 " + (string.IsNullOrWhiteSpace(Session.IdleTime) ? "-" : Session.IdleTime);
    }
}