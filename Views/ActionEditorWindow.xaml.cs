using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Views
{
    public partial class ActionEditorWindow : Window
    {
        private readonly AppConfig _cfg;
        private readonly ActionItem _result;
        private readonly List<Account> _accounts;
        private bool _loading;

        /// <summary>内置的启动参数预设（目前只有一个「一条龙」）。</summary>
        private sealed class ArgPreset
        {
            public string Label { get; init; } = "";
            public string Prefix { get; init; } = "";
            /// <summary>前缀后面是否还要用户再填一段（一条龙的配置名）</summary>
            public bool NeedsValue { get; init; }
            /// <summary>用户是否要自己写完整参数</summary>
            public bool IsCustom { get; init; }
            /// <summary>给用户的说明</summary>
            public string Hint { get; init; } = "";
        }

        private static readonly ArgPreset[] Presets =
        {
            new ArgPreset { Label = "无参数（默认）", Prefix = "", Hint = "直接启动，不带任何参数。" },
            new ArgPreset
            {
                Label = "一条龙", Prefix = "--startOneDragon", NeedsValue = true,
                Hint = "前缀 --startOneDragon 已自动带上，后面填你在 BetterGI 里配置的一条龙名称，例如「启动」「B线一条龙」；留空则按默认一条龙启动。"
            },
            new ArgPreset { Label = "自定义…", Prefix = "", IsCustom = true, Hint = "直接把完整参数写进输入框，程序不再添加任何前缀。" }
        };

        private ArgPreset _preset = Presets[0];
        private bool _buildingArgs;
        /// <summary>构造函数跑完之前，ComboBox 的 IsSelected 也会触发 SelectionChanged</summary>
        private bool _ready;

        public ActionItem Result => _result;

        public ActionEditorWindow(ActionItem? existing, AppConfig cfg)
        {
            InitializeComponent();
            _cfg = cfg;
            _accounts = cfg.Accounts;

            _result = existing == null
                ? new ActionItem()
                : new ActionItem
                {
                    Id = existing.Id,
                    Name = existing.Name,
                    AccountId = existing.AccountId,
                    ExePath = existing.ExePath,
                    Arguments = existing.Arguments,
                    Kind = existing.Kind,
                    DelaySeconds = existing.DelaySeconds
                };

            HeaderText.Text = existing == null ? "新建快捷动作" : "编辑快捷动作";

            UserCombo.ItemsSource = _accounts;

            _loading = true;
            if (!string.IsNullOrEmpty(_result.AccountId))
            {
                var acc = _accounts.FirstOrDefault(a => a.Id == _result.AccountId);
                if (acc != null) UserCombo.Text = acc.UserName;
            }
            NameBox.Text = _result.Name == "新动作" ? "" : _result.Name;
            ExeBox.Text = _result.ExePath;
            DelayBox.Text = _result.DelaySeconds.ToString();
            SessionBox.Text = "0";
            if (!string.IsNullOrEmpty(_result.AccountId))
            {
                var a = _accounts.FirstOrDefault(x => x.Id == _result.AccountId);
                if (a != null) SessionBox.Text = a.SessionId.ToString();
            }
            KindOpen.IsChecked = _result.Kind == ActionKind.Launch;
            KindClose.IsChecked = _result.Kind == ActionKind.Close;
            KindRestart.IsChecked = _result.Kind == ActionKind.Restart;

            // 已经有参数时自动识别预设：--startOneDragon xxx → 一条龙；其它 → 自定义
            ApplyExistingArgs(_result.Arguments);
            _loading = false;
            _ready = true;
            ApplyPresetUi();

            Loaded += (_, __) => { NameBox.Focus(); UpdateHints(); };
        }

        /// <summary>从会话列表直接带入用户名。</summary>
        public void PresetAccount(string userName)
        {
            _loading = true;
            UserCombo.Text = userName;
            _loading = false;
            UpdateHints();
            NameBox.Focus();
        }

        private void TitleRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); } catch { }
        }

        // ── 账号联动 ─────────────────────────────────────────────────────────

        private Account? MatchAccount(string user)
        {
            string bare = SessionService.BareName((user ?? "").Trim());
            if (bare.Length == 0) return null;
            return _accounts.FirstOrDefault(a =>
                string.Equals(a.UserName, bare, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(SessionService.BareName(a.UserName), bare, StringComparison.OrdinalIgnoreCase));
        }

        private void UserCombo_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            UpdateHints();
        }

        private void UpdateHints()
        {
            if (UserCombo == null || UserHint == null || CmdHint == null || NameBox == null) return;
            string user = (UserCombo.Text ?? "").Trim();
            var acc = MatchAccount(user);

            if (acc != null)
            {
                string saved = ConfigStore.GetPassword(acc);
                UserHint.Text = string.IsNullOrEmpty(saved)
                    ? "账号尚未保存凭据；请到通用设置 → 已保存的账号中修改密码。"
                    : "账号已选中，执行时直接复用已加密保存的凭据。";
                UserHint.Foreground = (System.Windows.Media.Brush)FindResource("TextMuted");
            }
            else
            {
                UserHint.Text = user.Length == 0
                    ? "输入 Windows 用户名（不带域名），已有的账号会自动带出。"
                    : "账号：" + user + " —— 保存后请到通用设置中配置凭据。";
                UserHint.Foreground = (System.Windows.Media.Brush)FindResource("TextMuted");
            }

            if (string.IsNullOrWhiteSpace(NameBox.Text) && user.Length > 0)
            {
                string exe = Path.GetFileName((ExeBox.Text ?? "").Trim());
                string kind = KindClose.IsChecked == true ? "关闭" : KindRestart.IsChecked == true ? "重启" : "打开";
                NameBox.Text = exe.Length > 0
                    ? SessionService.BareName(user) + " " + kind + " " + exe.Replace(".exe", "")
                    : SessionService.BareName(user) + " " + kind;
            }

            CmdHint.Text = CommandBuilder.ForTarget(SessionService.BareName(user),
                string.IsNullOrWhiteSpace(ExeBox.Text) ? "程序路径" : ExeBox.Text.Trim(), CurrentKind, null,
                BuildArguments());
        }

        // ── 启动参数／预设 ───────────────────────────────────────────────────

        /// <summary>反推已有参数属于哪个预设。</summary>
        private void ApplyExistingArgs(string? args)
        {
            string raw = (args ?? "").Trim();
            if (raw.Length == 0)
            {
                _preset = Presets[0];
                return;
            }

            if (raw.StartsWith("--startOneDragon", StringComparison.OrdinalIgnoreCase))
            {
                _preset = Presets[1];
                DragonNameBox.Text = raw.Substring("--startOneDragon".Length).Trim();
                return;
            }

            _preset = Presets[2];
            ArgsBox.Text = raw;
        }

        private void ApplyPresetUi()
        {
            // XAML 解析阶段的选中事件要忽略，此时其它控件还没实例化
            if (!_ready || PresetCombo == null || DragonNameShell == null || ArgsBox == null ||
                ArgsHint == null || ArgsTitle == null || ArgsRow == null) return;

            _buildingArgs = true;
            try
            {
                switch (Presets.ToList().IndexOf(_preset))
                {
                    case 1: PresetCombo.SelectedIndex = 1; break;
                    case 2: PresetCombo.SelectedIndex = 2; break;
                    default: PresetCombo.SelectedIndex = 0; break;
                }

                DragonNameShell.Visibility = _preset.NeedsValue ? Visibility.Visible : Visibility.Collapsed;
                ArgsBox.Visibility = _preset.IsCustom ? Visibility.Visible : Visibility.Collapsed;
                ArgsHint.Text = _preset.Hint;

                // 关闭 / 重启时参数没意义，直接淡出提示
                bool argsUsed = CurrentKind != ActionKind.Close;
                ArgsTitle.Opacity = argsUsed ? 1 : 0.4;
                ArgsRow.IsEnabled = argsUsed;
                ArgsRow.Opacity = argsUsed ? 1 : 0.4;
            }
            finally
            {
                _buildingArgs = false;
            }
            UpdateHints();
        }

        private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            if (PresetCombo.SelectedIndex < 0 || PresetCombo.SelectedIndex >= Presets.Length) return;
            _preset = Presets[PresetCombo.SelectedIndex];
            ApplyPresetUi();
        }

        private void DragonNameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (DragonPlaceholder != null)
                DragonPlaceholder.Visibility = string.IsNullOrEmpty(DragonNameBox.Text)
                    ? Visibility.Visible : Visibility.Collapsed;
            if (_buildingArgs) return;
            UpdateHints();
        }

        private void ArgsBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_buildingArgs) return;
            UpdateHints();
        }

        /// <summary>把「预设前缀 + 用户输入」拼成最终参数。</summary>
        private string BuildArguments()
        {
            switch (PresetCombo.SelectedIndex)
            {
                case 1:
                    string name = (DragonNameBox.Text ?? "").Trim();
                    return name.Length == 0 ? _preset.Prefix : _preset.Prefix + " " + name;
                case 2:
                    return (ArgsBox.Text ?? "").Trim();
                default:
                    return "";
            }
        }

        private ActionKind CurrentKind =>
            KindClose.IsChecked == true ? ActionKind.Close :
            KindRestart.IsChecked == true ? ActionKind.Restart : ActionKind.Launch;

        private void Kind_Checked(object sender, RoutedEventArgs e)
        {
            // XAML 解析阶段（IsChecked="True" 写在标记里）也可能触发，此时其它控件还是 null
            if (_loading || KindOpen == null || KindClose == null || KindRestart == null) return;
            if (UserCombo == null || NameBox == null || CmdHint == null) return;

            if (sender == KindOpen) { KindClose.IsChecked = false; KindRestart.IsChecked = false; }
            else if (sender == KindClose) { KindOpen.IsChecked = false; KindRestart.IsChecked = false; }
            else if (sender == KindRestart) { KindOpen.IsChecked = false; KindClose.IsChecked = false; }

            if (ArgsRow != null)
            {
                bool argsUsed = CurrentKind != ActionKind.Close;
                ArgsTitle.Opacity = argsUsed ? 1 : 0.4;
                ArgsRow.IsEnabled = argsUsed;
                ArgsRow.Opacity = argsUsed ? 1 : 0.4;
            }
            UpdateHints();
        }

        // ── 选择程序 ─────────────────────────────────────────────────────────

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择程序（路径必须在目标用户那边也存在）",
                Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                CheckFileExists = false
            };
            if (File.Exists(ExeBox.Text))
                dlg.InitialDirectory = Path.GetDirectoryName(ExeBox.Text);

            if (dlg.ShowDialog() == true)
            {
                ExeBox.Text = dlg.FileName;
                if (string.IsNullOrWhiteSpace(NameBox.Text)) { NameBox.Clear(); UpdateHints(); }
            }
        }

        private void PickRunning_Click(object sender, RoutedEventArgs e)
        {
            string user = SessionService.BareName((UserCombo.Text ?? "").Trim());
            if (user.Length == 0)
            {
                ShowError("请先填写用户名，才能读取该用户正在运行的程序。");
                return;
            }

            var picker = new ProcessPickerWindow(user) { Owner = this };
            if (picker.ShowDialog() == true && picker.Selected != null)
            {
                string path = string.IsNullOrEmpty(picker.Selected.ExePath)
                    ? picker.Selected.Name
                    : picker.Selected.ExePath;
                ExeBox.Text = path;
                if (string.IsNullOrWhiteSpace(NameBox.Text)) UpdateHints();
            }
        }

        // ── 保存 ─────────────────────────────────────────────────────────────

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            ErrorText.Visibility = Visibility.Collapsed;

            string user = SessionService.BareName((UserCombo.Text ?? "").Trim());
            if (user.Length == 0) { ShowError("请填写用户名。"); return; }

            string exe = (ExeBox.Text ?? "").Trim().Trim('"');
            if (exe.Length == 0) { ShowError("请选择或填写要操作的程序路径，例如 D:\\AAA挂机专区\\A2\\BetterGI\\BetterGI.exe"); return; }

            var account = MatchAccount(user);
            if (account == null)
            {
                account = new Account { UserName = user };
                _cfg.Accounts.Add(account);
            }

            int delay = 0;
            int.TryParse(DelayBox.Text, out delay);
            int sid = 0;
            int.TryParse(SessionBox.Text, out sid);
            account.SessionId = Math.Max(0, sid);

            _result.AccountId = account.Id;
            _result.ExePath = exe;
            _result.Arguments = BuildArguments();
            _result.Kind = CurrentKind;
            _result.DelaySeconds = Math.Max(0, delay);
            _result.Name = string.IsNullOrWhiteSpace(NameBox.Text)
                ? user + " " + (CurrentKind == ActionKind.Close ? "关闭" : CurrentKind == ActionKind.Restart ? "重启" : "打开") + " " + Path.GetFileNameWithoutExtension(exe)
                : NameBox.Text.Trim();

            // 名称唯一性
            var clash = _cfg.Actions.FirstOrDefault(a => a.Id != _result.Id &&
                string.Equals(a.Name, _result.Name, StringComparison.OrdinalIgnoreCase));
            if (clash != null) _result.Name += " (" + DateTime.Now.ToString("HHmm") + ")";

            DialogResult = true;
        }

        private void CopyCmd_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(CmdHint.Text);
                CmdHint.ToolTip = "已复制";
            }
            catch { }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void ShowError(string msg)
        {
            ErrorText.Text = "⚠ " + msg;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}