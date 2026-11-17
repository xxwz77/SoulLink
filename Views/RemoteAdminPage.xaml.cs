using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Views
{
    /// <summary>
    /// 远程管理页：本机 Windows 账户 + RDP Wrapper（TermWrap）。
    ///
    /// 对应原来 bat 脚本的功能：
    ///   1 新增远程桌面用户 → 这里（新建用户，可自动入组、自动生成连接配置）
    ///   2 远程桌面端口开关 → 保留在用户与账号区（见端口开关）
    ///   3 家庭版加组策略   → 未实现（当前系统是专业版，本身就有组策略）
    ///   4 打开组策略编辑器 → 系统工具
    ///   5 sfc 系统修复     → 系统工具
    ///   6 显隐用户         → 这里（每行的显示/隐藏按钮）
    ///   7 删除用户         → 这里（危险操作，二次确认）
    /// 另加：密码永不过期、RDP Wrapper 管理
    /// </summary>
    public partial class RemoteAdminPage : UserControl
    {
        private List<LocalUserInfo> _users = new();
        private bool _busy;

        public RemoteAdminPage()
        {
            InitializeComponent();
            Loaded += (_, __) => { if (_loaded) return; _loaded = true; _ = ReloadAsync(); };
        }

        private bool _loaded;

        // ── 日志 ─────────────────────────────────────────────────────────

        private void Log(string msg, bool isError = false)
        {
            try
            {
                var line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
                LogText.Text += (LogText.Text.Length == 0 ? "" : Environment.NewLine) + line;
                LogScroll.ScrollToEnd();
                if (isError) LogBus.Error("远程管理", msg);
            }
            catch { }
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e) => LogText.Text = "";

        // ── 加载 ─────────────────────────────────────────────────────────

        public async Task ReloadAsync()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                SubTitle.Text = "管理本机账户与远程桌面包装器 · 当前系统：" + Environment.MachineName;

                if (!LocalUserService.IsElevated())
                    Log("警告：当前不是管理员权限，多数操作会失败", true);

                _users = await LocalUserService.ListAsync();
                RenderUsers();

                WrapDirBox.Text = string.IsNullOrWhiteSpace(WrapDirBox.Text)
                    ? RdpWrapService.DefaultWrapDir
                    : WrapDirBox.Text;
                RefreshWrapStatus();
            }
            catch (Exception ex)
            {
                Log("加载失败：" + ex.Message, true);
            }
            finally { _busy = false; }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            try { await ReloadAsync(); Log("已刷新"); }
            catch (Exception ex) { Log("刷新失败：" + ex.Message, true); }
        }

        // ── 用户列表 ─────────────────────────────────────────────────────

        private void RenderUsers()
        {
            UserList.Children.Clear();

            if (_users.Count == 0)
            {
                NoUserHint.Visibility = Visibility.Visible;
                NoUserHint.Text = "没有读到本地用户。";
                UserSummary.Text = "";
                return;
            }

            NoUserHint.Visibility = Visibility.Collapsed;
            int hiddenCount = _users.Count(u => u.Hidden);
            int remoteCount = _users.Count(u => u.IsRemoteUser);
            UserSummary.Text = "共 " + _users.Count + " 个账户 · "
                + remoteCount + " 个可远程登录 · "
                + hiddenCount + " 个已隐藏";

            foreach (var u in _users) UserList.Children.Add(BuildRow(u));
        }

        private UIElement BuildRow(LocalUserInfo u)
        {
            var grid = new Grid { Height = 34, Background = Brushes.Transparent };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 用户名
            var name = new TextBlock
            {
                Text = u.Name,
                FontSize = 11.5,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = (FontFamily)FindResource("MonoFont"),
                Foreground = (Brush)FindResource("TextPrimary")
            };
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            // 远程桌面 勾
            var rem = MakeMark(u.IsRemoteUser, "可远程登录", "未加入 Remote Desktop Users");
            Grid.SetColumn(rem, 1);
            grid.Children.Add(rem);

            // 管理员 勾
            var adm = MakeMark(u.IsAdmin, "管理员", "普通用户");
            Grid.SetColumn(adm, 2);
            grid.Children.Add(adm);

            // 密码永不过期
            var pwd = new TextBlock
            {
                Text = u.PasswordNeverExpires ? "永不过期" : "会过期",
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(u.PasswordNeverExpires ? "TextSecondary" : "TextMuted")
            };
            Grid.SetColumn(pwd, 3);
            grid.Children.Add(pwd);

            // 状态
            var status = new TextBlock
            {
                Text = (u.Enabled ? "启用" : "已禁用") + (u.Hidden ? " · 已隐藏" : "")
                        + (u.IsBuiltIn ? " · 内置" : ""),
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(u.Hidden || !u.Enabled ? "TextMuted" : "TextSecondary")
            };
            Grid.SetColumn(status, 4);
            grid.Children.Add(status);

            // 操作
            var ops = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            ops.Children.Add(MiniButton(u.Hidden ? "显示" : "隐藏",
                u.Hidden ? "让该账户重新出现在登录界面" : "从登录界面隐藏该账户",
                () => ToggleHiddenAsync(u)));
            ops.Children.Add(MiniButton(u.PasswordNeverExpires ? "恢复过期" : "永不过期",
                "设置密码是否永不过期",
                () => ToggleExpireAsync(u)));
            if (!u.IsBuiltIn)
                ops.Children.Add(MiniButton("删除", "删除该账户（不可恢复）",
                    () => DeleteUserAsync(u), danger: true));
            Grid.SetColumn(ops, 5);
            grid.Children.Add(ops);

            var wrap = new Border { Child = grid, BorderBrush = new SolidColorBrush(Color.FromArgb(0x10, 255, 255, 255)),
                                    BorderThickness = new Thickness(0, 0, 0, 1) };
            return wrap;
        }

        private UIElement MakeMark(bool on, string yes, string no)
        {
            return new TextBlock
            {
                Text = on ? "\u2714" : "\u2014",
                ToolTip = on ? yes : no,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = on ? new SolidColorBrush(Color.FromRgb(0x41, 0xF0, 0xC0))
                                : (Brush)FindResource("TextMuted")
            };
        }

        private Button MiniButton(string text, string tip, Func<Task> onClick, bool danger = false)
        {
            var b = new Button
            {
                Content = text,
                ToolTip = tip,
                FontSize = 10,
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(4, 0, 0, 0),
                Cursor = Cursors.Hand,
                Style = (Style)FindResource(danger ? "DangerButton" : "GhostButton")
            };
            b.Click += async (_, __) =>
            {
                try { b.IsEnabled = false; await onClick(); }
                catch (Exception ex) { Log("操作失败：" + ex.Message, true); }
                finally { b.IsEnabled = true; }
            };
            return b;
        }

        // ── 用户操作 ─────────────────────────────────────────────────────

        private async Task ToggleHiddenAsync(LocalUserInfo u)
        {
            var r = LocalUserService.SetHidden(u.Name, !u.Hidden);
            Log((r.Ok ? "✔ " : "✘ ") + u.Name + "：" + r.Output, !r.Ok);
            await ReloadAsync();
        }

        private async Task ToggleExpireAsync(LocalUserInfo u)
        {
            var r = await LocalUserService.SetPasswordNeverExpiresAsync(u.Name, !u.PasswordNeverExpires);
            Log((r.Ok ? "✔ " : "✘ ") + u.Name + " 密码策略：" + r.Output, !r.Ok);
            await ReloadAsync();
        }

        private async Task DeleteUserAsync(LocalUserInfo u)
        {
            if (!Dialogs.Confirm(
                "确定删除账户「" + u.Name + "」吗？\n\n" +
                "这会永久删除该用户的配置文件与个人数据，且不可恢复。\n" +
                "如果该用户正在挂机，请先关闭其会话。",
                "删除用户（危险操作）", true))
                return;

            var r = await LocalUserService.DeleteAsync(u.Name);
            Log((r.Ok ? "✔ " : "✘ ") + "删除 " + u.Name + "：" + r.Output, !r.Ok);
            await ReloadAsync();
        }

        // ── 新建用户 ─────────────────────────────────────────────────────

        private void BtnNewUser_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new NewUserWindow { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && dlg.Result != null)
            {
                Log("已创建账户：" + dlg.Result.Name
                    + (dlg.SavedAccount != null
                        ? " · 已生成连接配置 " + dlg.SavedAccount.RdpHost
                        : ""));
                _ = ReloadAsync();
            }
        }

        // ── RDP Wrapper ──────────────────────────────────────────────────

        private void RefreshWrapStatus()
        {
            try
            {
                var st = RdpWrapService.GetStatus(WrapDirBox.Text?.Trim() ?? "");
                WrapStatusText.Text = st.Summary;
                WrapStatusText.Foreground = new SolidColorBrush(st.Installed && st.DllLoaded
                    ? Color.FromRgb(0x41, 0xF0, 0xC0)
                    : st.Installed ? Color.FromRgb(0xFF, 0xC8, 0x5B)
                                   : Color.FromRgb(0xFF, 0x8A, 0x5B));

                WrapDetail.Text =
                    (st.DllExists ? "DLL✓" : "DLL✗") +
                    " · 服务" + (st.ServiceRunning ? "运行" : "停止") +
                    " · 已加载" + (st.DllLoaded ? "✓" : "✗") +
                    " · " + (string.IsNullOrWhiteSpace(st.CurrentServiceDll) ? "未设置" : st.CurrentServiceDll) +
                    (RdpWrapService.HasBackup() ? "  [已备份原始值]" : "  [无备份]");
            }
            catch (Exception ex) { Log("读取 RDP Wrapper 状态失败：" + ex.Message, true); }
        }

        private void BtnBrowseWrap_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "选择 RDP Wrap 所在目录（应包含 TermWrap.dll）",
                    SelectedPath = System.IO.Directory.Exists(WrapDirBox.Text)
                        ? WrapDirBox.Text : RdpWrapService.DefaultWrapDir
                };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    WrapDirBox.Text = dlg.SelectedPath;
                    RefreshWrapStatus();
                }
            }
            catch (Exception ex) { Log("选择目录失败：" + ex.Message, true); }
        }

        private async void BtnInstallWrap_Click(object sender, RoutedEventArgs e)
        {
            string dir = WrapDirBox.Text?.Trim() ?? "";
            if (!Dialogs.Confirm(
                "将把 TermService 的 ServiceDll 指向：" + Environment.NewLine
                + System.IO.Path.Combine(dir, "TermWrap.dll") + Environment.NewLine + Environment.NewLine +
                "随后会**立即重启远程桌面服务**，当前所有 RDP 会话都会断开重连。\n\n" +
                "请确认现在没有正在挂机的会话。继续吗？",
                "安装 RDP Wrapper", true))
                return;

            Log("开始安装 RDP Wrapper：" + dir);
            var (ok, msg) = await RdpWrapService.InstallAsync(dir, autoRestart: true);
            Log((ok ? "✔ " : "✘ ") + msg, !ok);
            RefreshWrapStatus();
        }


        private async void BtnRestoreWrap_Click(object sender, RoutedEventArgs e)
        {
            if (!Dialogs.Confirm(
                "恢复 TermService 为系统默认的 termsrv.dll？\n\n" +
                "这会关闭多用户同时远程登录的能力，并重启服务（当前 RDP 会话会断开）。",
                "恢复默认配置", true))
                return;

            Log("恢复默认配置…");
            var (ok, msg) = await RdpWrapService.RestoreAsync(autoRestart: true);
            Log((ok ? "✔ " : "✘ ") + msg, !ok);
            RefreshWrapStatus();
        }
    }
}
