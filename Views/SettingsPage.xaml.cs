using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Views
{
    public partial class SettingsPage : UserControl
    {
        public event Action<bool, string>? StatusChanged;

        private readonly ObservableCollection<AccountVm> _accounts = new ObservableCollection<AccountVm>();
        private bool _loading;

        public SettingsPage()
        {
            InitializeComponent();
            LogList.ItemsSource = LogBus.Entries;
            VersionText.Text = "SoulLink v1.0 · " + (Environment.IsPrivilegedProcess ? "管理员权限" : "普通权限");
        }

        /// <summary>输入框为空时显示占位文字。</summary>
        private void UpdateLobbyPlaceholder()
        {
            try
            {
                if (LobbyServerPlaceholder == null) return;
                LobbyServerPlaceholder.Visibility =
                    string.IsNullOrWhiteSpace(LobbyServerBox.Text)
                        ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        private void LobbyServerBox_TextChanged(object sender, TextChangedEventArgs e)
            => UpdateLobbyPlaceholder();

        /// <summary>测试联机大厅服务器是否可达。</summary>
        private async void LobbyServerTest_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string url = (LobbyServerBox.Text ?? "").Trim();
                if (url.Length == 0) { LobbyServerStatus.Text = "地址不能为空"; return; }
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    url = "http://" + url;

                LobbyServerStatus.Text = "测试中…";

                string old = Services.LobbyApi.BaseUrl;
                Services.LobbyApi.BaseUrl = url;
                var (ok, msg) = await Services.LobbyApi.PingAsync().ConfigureAwait(true);
                if (!ok) Services.LobbyApi.BaseUrl = old;

                LobbyServerStatus.Text = ok ? ("✓ " + msg) : ("✕ " + msg);
                LobbyServerStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    ok ? System.Windows.Media.Color.FromRgb(0x6E, 0xF0, 0xC8)
                       : System.Windows.Media.Color.FromRgb(0xFF, 0x9B, 0x9B));
            }
            catch (Exception ex)
            {
                LobbyServerStatus.Text = "✕ " + ex.Message;
            }
        }

        /// <summary>保存联机大厅服务器地址。</summary>
        private void LobbyServerSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string url = (LobbyServerBox.Text ?? "").Trim();
                if (url.Length == 0) { LobbyServerStatus.Text = "地址不能为空"; return; }
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    url = "http://" + url;

                Services.LobbyApi.SaveToConfig(url);
                // ★ 回填用户填的值，而不是"生效地址"（后者可能是内置默认值）
                LobbyServerBox.Text = Services.LobbyApi.ConfiguredUrl;
                UpdateLobbyPlaceholder();

                LobbyServerStatus.Text = "✓ 已保存";
                LobbyServerStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x6E, 0xF0, 0xC8));

                LogBus.Ok("联机大厅服务器地址已更新");
            }
            catch (Exception ex)
            {
                LobbyServerStatus.Text = "✕ " + ex.Message;
            }
        }

        public void Activate()
        {
            _loading = true;
            TrayCheck.IsChecked = App.Config.Settings.MinimizeToTray;
            RemoteCheck.IsChecked = App.Config.Settings.AllowRemoteInvoke;
            FallbackCheck.IsChecked = App.Config.Settings.AllowCurrentSessionFallback;
            ConfigPathText.Text = ConfigStore.ConfigPath;

            // 联机大厅服务器地址
            try
            {
                Services.LobbyApi.LoadFromConfig();
                // ★ 只显示用户配置过的地址。内置默认地址属于实现细节，
                //   不能回填到输入框里（否则用户会以为那是他自己填的）。
                LobbyServerBox.Text = Services.LobbyApi.ConfiguredUrl;
                UpdateLobbyPlaceholder();
                LobbyServerStatus.Text = "";
            }
            catch { }
            VersionText.Text = "SoulLink v" + typeof(App).Assembly.GetName().Version?.ToString(3) +
                               " · " + (Environment.IsPrivilegedProcess ? "管理员权限" : "普通权限");
            _loading = false;

            RefreshAccounts();
            RebuildBgiPathList();
            _ = DetectAsync();
            _ = RefreshAutoStartAsync();
        }

        // ── BetterGI 路径（每账号一行） ──────────────────────────────────
        //
        // ★ 唯一数据源：Settings.BgiPaths（用户名 → 路径）。
        //   留空 = 自动（由 BgiPathResolver 从运行中的进程识别）。
        //   填了 = 固定使用该值（BgiPinnedUsers 里会记录该用户，自动探测不再覆盖）。

        private void RebuildBgiPathList()
        {
            if (BgiPathList == null) return;
            BgiPathList.Children.Clear();

            if (App.Config.Accounts.Count == 0)
            {
                BgiPathList.Children.Add(new TextBlock
                {
                    Text = "尚未配置任何 RDP 账号",
                    Style = (Style)FindResource("Caption")
                });
                if (BgiPathStatus != null) BgiPathStatus.Text = "";
                return;
            }

            int auto = 0, manual = 0;
            foreach (var a in App.Config.Accounts)
            {
                bool pinned = BgiPathResolver.IsPinned(a.UserName);
                if (pinned) manual++; else auto++;
                BgiPathList.Children.Add(BuildBgiRow(a, pinned));
            }

            if (BgiPathStatus != null)
                BgiPathStatus.Text = "共 " + App.Config.Accounts.Count + " 个账号："
                    + manual + " 个手动指定 · " + auto + " 个自动识别";
        }

        private UIElement BuildBgiRow(Account a, bool pinned)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 用户名 + 昵称
            var name = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(a.DisplayName) || a.DisplayName == a.UserName
                    ? a.UserName
                    : a.DisplayName + "（" + a.UserName + "）",
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("TextPrimary")
            };
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            // 路径框
            string saved = "";
            try { App.Config.Settings.BgiPaths.TryGetValue(a.UserName, out saved); } catch { }
            var box = new TextBox
            {
                Text = saved ?? "",
                Height = 30,
                Style = (Style)FindResource("InlineTextBox"),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 11,
                FontFamily = (FontFamily)FindResource("MonoFont"),
                ToolTip = pinned ? "手动指定：一直使用此路径" : "留空 = 自动从运行中的进程识别",
                Tag = a.UserName,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);

            // 状态
            var state = new TextBlock
            {
                Text = pinned ? "手动" : (string.IsNullOrWhiteSpace(saved) ? "自动" : "记忆"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Foreground = pinned
                    ? new SolidColorBrush(Color.FromRgb(0x7C, 0x5C, 0xFF))
                    : (Brush)FindResource("TextMuted")
            };
            Grid.SetColumn(state, 2);
            grid.Children.Add(state);

            // 按钮：从进程取 / 清除
            var ops = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
            var btnProbe = new Button
            {
                Content = "从进程取",
                FontSize = 10,
                Padding = new Thickness(8, 3, 8, 3),
                Style = (Style)FindResource("GhostButton"),
                Tag = a.UserName,
                ToolTip = "读取该账号当前正在运行的 BetterGI 路径"
            };
            btnProbe.Click += BgiProbe_Click;
            ops.Children.Add(btnProbe);

            var btnClear = new Button
            {
                Content = "自动",
                FontSize = 10,
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(4, 0, 0, 0),
                Style = (Style)FindResource("GhostButton"),
                Tag = a.UserName,
                ToolTip = "清除手动指定，恢复自动识别"
            };
            btnClear.Click += BgiClear_Click;
            ops.Children.Add(btnClear);

            Grid.SetColumn(ops, 3);
            grid.Children.Add(ops);

            return grid;
        }

        /// <summary>从正在运行的进程读取该账号的 BGI 路径并填入。</summary>
        private async void BgiProbe_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string user) return;
            try
            {
                b.IsEnabled = false;
                var info = await Task.Run(() => BgiPathResolver.Resolve(user));
                if (!info.Running || !info.HasPath)
                {
                    if (BgiPathStatus != null)
                        BgiPathStatus.Text = user + "：没有找到正在运行的 BetterGI";
                    return;
                }

                BgiPathResolver.Pin(user, info.Path);
                ConfigStore.Save(App.Config);
                RebuildBgiPathList();
                if (BgiPathStatus != null)
                    BgiPathStatus.Text = user + "：已设为 " + info.Path;
            }
            catch (Exception ex)
            {
                if (BgiPathStatus != null) BgiPathStatus.Text = "读取失败：" + ex.Message;
            }
            finally { b.IsEnabled = true; }
        }

        /// <summary>清除手动指定，恢复自动识别。</summary>
        private void BgiClear_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string user) return;
            try
            {
                BgiPathResolver.Unpin(user);
                try { App.Config.Settings.BgiPaths.Remove(user); } catch { }
                ConfigStore.Save(App.Config);
                RebuildBgiPathList();
                if (BgiPathStatus != null) BgiPathStatus.Text = user + "：已恢复自动识别";
            }
            catch (Exception ex)
            {
                if (BgiPathStatus != null) BgiPathStatus.Text = "操作失败：" + ex.Message;
            }
        }

        private void RefreshAccounts()
        {
            _accounts.Clear();
            foreach (var a in App.Config.Accounts)
                _accounts.Add(new AccountVm(a));
        }

        /// <summary>
        /// 检测投送引擎状态。
        ///
        /// 现在的引擎是 **SYSTEM 计划任务通道**（schtasks /ru SYSTEM → CreateProcessAsUser），
        /// 不再依赖任何外部工具，唯一的硬性条件是「以管理员身份运行」。
        /// </summary>
        private async Task DetectAsync()
        {
            EngineState.Text = "检测中…";
            var (ok, detail) = await Task.Run(() =>
            {
                SystemLauncher.EnablePrivileges();
                bool elevated = SystemLauncher.IsElevated;
                string msg = elevated
                    ? "SYSTEM 计划任务通道 · 计划任务 " + (CanUseSchtasks() ? "可用" : "受限")
                    : "需要管理员权限";
                return (elevated, msg);
            });

            EngineDot.Color = ok ? Color.FromRgb(0x41, 0xF0, 0xC0) : Color.FromRgb(0xFF, 0xC4, 0x6B);
            EngineState.Text = ok ? "可用" : "需要管理员权限";
            EnginePath.Text = detail;
            StatusChanged?.Invoke(ok, detail);
        }

        /// <summary>确认 schtasks 存在（SYSTEM 通道要用）。</summary>
        private static bool CanUseSchtasks()
        {
            try
            {
                string sys = Environment.SystemDirectory;
                return File.Exists(Path.Combine(sys, "schtasks.exe"));
            }
            catch { return false; }
        }

        // ── 引擎 ─────────────────────────────────────────────────────────────

        private async void Redetect_Click(object sender, RoutedEventArgs e) => await DetectAsync();

        /// <summary>以管理员身份重启自己（跨会话投送必须提权）。</summary>
        private void RestartAsAdmin_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string exe = Environment.ProcessPath ?? "";
                if (string.IsNullOrEmpty(exe)) return;

                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = "--page settings --show"
                };
                Process.Start(psi);

                // 交给新的管理员实例，本进程退出
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                LogBus.Error("无法以管理员身份重启", ex.Message);
            }
        }

        // ── 账号 ─────────────────────────────────────────────────────────────

        private void ChangePwd_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not AccountVm vm) return;
            var dlg = new PasswordWindow(vm.UserName) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true)
            {
                ConfigStore.SetPassword(vm.Model, dlg.Password);
                ConfigStore.Save(App.Config);
                RefreshAccounts();
                LogBus.Ok("已更新 " + vm.UserName + " 的密码。");
            }
        }
        private void ChangeHost_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not AccountVm vm) return;

            var dlg = new RdpAddressWindow(vm.Model)
            {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() == true)
            {
                ConfigStore.Save(App.Config);
                RefreshAccounts();
                LogBus.Ok("已更新 " + vm.UserName + " 的 RDP 地址",
                    string.IsNullOrWhiteSpace(vm.Model.RdpHost) ? "留空 = 自动探测" : vm.Model.RdpHost);
            }
        }

        private void RemoveAccount_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not AccountVm vm) return;
            bool used = App.Config.Actions.Any(a => a.AccountId == vm.Model.Id);
            if (used)
            {
                Dialogs.Info("这个账号还在被快捷动作使用，请先删除或修改那些动作。", "无法移除账号");
                return;
            }
            if (!Dialogs.Confirm("确定移除账号 " + vm.UserName + " 吗？", "移除账号", true)) return;
            App.Config.Accounts.Remove(vm.Model);
            ConfigStore.Save(App.Config);
            RefreshAccounts();
        }

        // ── 选项 ─────────────────────────────────────────────────────────────

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            App.Config.Settings.MinimizeToTray = TrayCheck.IsChecked == true;
            App.Config.Settings.AllowRemoteInvoke = RemoteCheck.IsChecked == true;
            App.Config.Settings.AllowCurrentSessionFallback = FallbackCheck.IsChecked == true;
            ConfigStore.Save(App.Config);
        }

        // ── 配置 ─────────────────────────────────────────────────────────────

        // ── 开机自启 ────────────────────────────────────────────────────────

        private bool _autoStartBusy;

        /// <summary>填充启动账户与自动连接列表，并回显当前自启状态。</summary>
        private async Task RefreshAutoStartAsync()
        {
            try
            {
                // ① 启动账户候选：当前登录用户 + 本机所有已启用的本地账户
                AutoStartUserCombo.Items.Clear();
                var users = new List<string>();
                users.Add(AutoStart.CurrentUser);   // 默认第一项 = 当前用户
                // 从账号配置里补充（这些是本机能登录的 RDP 用户）
                foreach (var a in App.Config.Accounts)
                {
                    string n = string.IsNullOrWhiteSpace(a.RdpUserName) ? a.UserName : a.RdpUserName;
                    if (!string.IsNullOrWhiteSpace(n) &&
                        !users.Contains(n, StringComparer.OrdinalIgnoreCase))
                        users.Add(n);
                }
                foreach (var u in users) AutoStartUserCombo.Items.Add(u);

                // 选上次保存的；没有就选当前用户（默认）
                string saved = App.Config.Settings.AutoStartUser;
                int idx = 0;
                if (!string.IsNullOrWhiteSpace(saved))
                    for (int i = 0; i < AutoStartUserCombo.Items.Count; i++)
                        if (string.Equals(AutoStartUserCombo.Items[i]?.ToString(), saved,
                                          StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
                AutoStartUserCombo.SelectedIndex = idx;

                // ② 自动连接的 RDP 用户（多选）
                AutoConnectList.Children.Clear();
                var selected = App.Config.Settings.AutoConnectAccountIds ?? new List<string>();
                if (App.Config.Accounts.Count == 0)
                {
                    AutoConnectList.Children.Add(new TextBlock
                    {
                        Text = "尚未配置任何 RDP 账号",
                        Style = (Style)FindResource("Caption")
                    });
                }
                foreach (var a in App.Config.Accounts)
                {
                    var cb = new CheckBox
                    {
                        Content = a.Caption + "（" + a.UserName + "）",
                        Tag = a.Id,
                        Style = (Style)FindResource("GlassCheckBox"),
                        Margin = new Thickness(0, 2, 0, 2),
                        IsChecked = selected.Contains(a.Id)
                    };
                    cb.Checked += AutoConnectItem_Changed;
                    cb.Unchecked += AutoConnectItem_Changed;
                    AutoConnectList.Children.Add(cb);
                }

                // ③ 状态回显
                bool on = AutoStart.IsEnabled();
                AutoStartButtonText.Text = on ? "关闭开机自启" : "启用开机自启";
                if (on)
                {
                    string? bound = AutoStart.GetBoundUser();
                    AutoStartStatus.Text = "计划任务：已开启"
                        + (string.IsNullOrWhiteSpace(bound) ? "" : "（" + bound + " 登录时启动）");
                }
                else AutoStartStatus.Text = "计划任务：未开启（登录时不会自动启动）";

                UpdateAutoConnectHint();
                await Task.CompletedTask;
            }
            catch (Exception ex) { AutoStartStatus.Text = "读取自启状态失败：" + ex.Message; }
        }

        /// <summary>
        /// 只更新"启动后自动连接"这一块的提示。
        /// ★ 原来是写进 AutoStartStatus 的，导致"开机自启"状态栏里
        ///   混着自动连接的信息，用户容易以为按钮也在管自动连接。
        ///   现在两块彻底独立：
        ///     · AutoStartStatus  —— 只描述计划任务（开/关、启动账户）
        ///     · AutoConnectStatus —— 只描述勾选了几个账号
        /// </summary>
        private void UpdateAutoConnectHint()
        {
            try
            {
                int n = 0;
                foreach (var c in AutoConnectList.Children)
                    if (c is CheckBox cb && cb.IsChecked == true) n++;

                if (AutoConnectStatus == null) return;   // 兼容旧 XAML
                AutoConnectStatus.Text = n > 0
                    ? ("已选 " + n + " 个账号：程序启动后会自动连接并投送通道。")
                    : "未选择账号：程序启动后不会自动连接。";
            }
            catch { }
        }

        private void AutoConnectItem_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            try
            {
                var ids = new List<string>();
                foreach (var c in AutoConnectList.Children)
                    if (c is CheckBox cb && cb.IsChecked == true && cb.Tag is string id)
                        ids.Add(id);
                App.Config.Settings.AutoConnectAccountIds = ids;
                ConfigStore.Save(App.Config);
                UpdateAutoConnectHint();
            }
            catch { }
        }

        private async void AutoStart_Click(object sender, RoutedEventArgs e)
        {
            if (_autoStartBusy) return;
            _autoStartBusy = true;
            try
            {
                bool on = AutoStart.IsEnabled();
                if (on)
                {
                    AutoStartStatus.Text = "正在关闭…";
                    var (ok, msg) = await AutoStart.DisableAsync();
                    AutoStartStatus.Text = msg;
                    if (ok) AutoStartButtonText.Text = "启用开机自启";
                }
                else
                {
                    string user = AutoStartUserCombo.SelectedItem as string;
                    if (string.IsNullOrWhiteSpace(user)) user = AutoStart.CurrentUser;
                    string exe = Environment.ProcessPath ?? "";

                    App.Config.Settings.AutoStartUser = user;
                    ConfigStore.Save(App.Config);

                    AutoStartStatus.Text = "正在创建计划任务…";
                    var (ok, msg) = await AutoStart.EnableAsync(user, exe);
                    AutoStartStatus.Text = msg;
                    if (ok) AutoStartButtonText.Text = "关闭开机自启";
                }
            }
            catch (Exception ex) { AutoStartStatus.Text = "操作失败：" + ex.Message; }
            finally { _autoStartBusy = false; }
        }

        private void OpenConfig_Click(object sender, RoutedEventArgs e)
        {
            string dir = ConfigStore.ConfigDir;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出配置（含加密后的密码）",
                FileName = "soullink-config-" + DateTime.Now.ToString("yyyyMMdd") + ".json",
                Filter = "JSON (*.json)|*.json"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var clean = new AppConfig
                {
                    Settings = App.Config.Settings,
                    Accounts = App.Config.Accounts,
                    Actions = App.Config.Actions
                };
                File.WriteAllText(dlg.FileName,
                    System.Text.Json.JsonSerializer.Serialize(clean,
                        new System.Text.Json.JsonSerializerOptions
                        {
                            WriteIndented = true,
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
                        }),
                    new UTF8Encoding(false));
                LogBus.Ok("配置已导出到 " + dlg.FileName);
            }
            catch (Exception ex) { LogBus.Error("导出失败：" + ex.Message); }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "导入配置", Filter = "JSON (*.json)|*.json" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var cfg = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(dlg.FileName),
                    new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
                    });
                if (cfg == null) { LogBus.Error("配置文件内容无效。"); return; }

                App.Config.Accounts = cfg.Accounts ?? new List<Account>();
                App.Config.Actions = cfg.Actions ?? new List<ActionItem>();
                ConfigStore.Save(App.Config);

                RefreshAccounts();
                LogBus.Ok("配置已导入：" + App.Config.Accounts.Count + " 个账号，" +
                          App.Config.Actions.Count + " 个动作。");
            }
            catch (Exception ex) { LogBus.Error("导入失败：" + ex.Message); }
        }

        private void OpenReadme_Click(object sender, RoutedEventArgs e) => ReadmeWindow.ShowFor(Window.GetWindow(this));

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogBus.Entries.Clear();
        }
    }

    public class AccountVm
    {
        public Account Model { get; }
        public AccountVm(Account model) { Model = model; }

        /// <summary>账号行右侧显示的 RDP 地址摘要。</summary>
        public string RdpState => string.IsNullOrWhiteSpace(Model.RdpHost)
            ? "地址 自动"
            : Model.RdpHost + ":" + (Model.RdpPort > 0 ? Model.RdpPort : 3389);
        public string UserName => Model.UserName;
        public string PwdState => string.IsNullOrEmpty(ConfigStore.GetPassword(Model)) ? "（未保存密码）" : "已加密保存";
    }
}