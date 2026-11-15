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

        public void Activate()
        {
            _loading = true;
            TrayCheck.IsChecked = App.Config.Settings.MinimizeToTray;
            RemoteCheck.IsChecked = App.Config.Settings.AllowRemoteInvoke;
            FallbackCheck.IsChecked = App.Config.Settings.AllowCurrentSessionFallback;
            ConfigPathText.Text = ConfigStore.ConfigPath;
            VersionText.Text = "SoulLink v" + typeof(App).Assembly.GetName().Version?.ToString(3) +
                               " · " + (Environment.IsPrivilegedProcess ? "管理员权限" : "普通权限");
            _loading = false;

            RefreshAccounts();
            _ = DetectAsync();
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