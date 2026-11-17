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

        /// <summary>启动方式：0=无参数 1=启动 2=一条龙 3=配置组 4=自定义。</summary>
        private int _presetIndex;

        /// <summary>选中的配置组（可多选，BGI 支持一次传多个）。</summary>
        private readonly List<string> _scriptGroups = new List<string>();

        /// <summary>
        /// 给含空格的参数值加引号。
        /// BGI 的配置名/一条龙名可能带空格，不加引号会被拆成多个参数。
        /// </summary>
        private static string Quote(string s)
            => s.IndexOf(' ') >= 0 ? ("\"" + s + "\"") : s;

        /// <summary>选中的一条龙配置名（User 下的 OneDragon 目录）。</summary>
        private string _oneDragonName = "";

        /// <summary>选中的配置组名（User 下的 ScriptGroup 目录）。</summary>
        private string _scriptGroupName = "";
        private bool _buildingArgs;
        /// <summary>构造函数跑完之前，ComboBox 的 IsSelected 也会触发 SelectionChanged</summary>
        private bool _ready;

        public ActionItem Result => _result;

        public ActionEditorWindow(ActionItem? existing, AppConfig cfg)
        {
            InitializeComponent();
            _cfg = cfg;
            _accounts = cfg.Accounts;

            // 用户名由用户直接输入，执行时再按用户名匹配会话。
            // 不做保存时校验，避免"想给还没登录的账号预先配动作"时被挡住。

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
                    DelaySeconds = existing.DelaySeconds,
                    // ★ 必须带上这些字段，否则编辑时界面回填成空值，
                    //   一保存就把原有设置冲掉（定时设置"总是不生效"的根因之一）。
                    StopBgiFirst = existing.StopBgiFirst,
                    CloseGenshin = existing.CloseGenshin,
                    StopWaitSeconds = existing.StopWaitSeconds,
                    // Schedule 用深拷贝：避免编辑中途取消时把运行进度改坏
                    Schedule = CloneSchedule(existing.Schedule)
                };

            HeaderText.Text = existing == null ? "新建快捷动作" : "编辑快捷动作";

            // 下拉只作为"已有账号"的快捷提示，用户也可以直接输入其他用户名。
            // 执行时会按用户名去匹配当前会话，不要求这里选中过。
            // 下拉内容由 UserDrop_Click 动态构建（已保存账号）

            _loading = true;
            if (!string.IsNullOrEmpty(_result.AccountId))
            {
                var acc = _accounts.FirstOrDefault(a => a.Id == _result.AccountId);
                if (acc != null) UserBox.Text = acc.UserName;
            }
            NameBox.Text = _result.Name == "新动作" ? "" : _result.Name;
            ExeBox.Text = _result.ExePath;
            DelayBox.Text = _result.DelaySeconds.ToString();
            if (StopBgiCheck != null) StopBgiCheck.IsChecked = _result.StopBgiFirst;
            if (CloseGenshinCheck != null) CloseGenshinCheck.IsChecked = _result.CloseGenshin;
            if (StopWaitBox != null) StopWaitBox.Text = _result.StopWaitSeconds.ToString();

            // ★ 必须先置 _ready：ApplyScheduleToUi 内部依赖 _ready 才会执行
            //   ScheduleKind_Checked / ScheduleEnabled_Changed 的完整逻辑，
            //   否则界面会停在"定时未启用 + 仅运行时"的错误状态。
            _ready = true;
            ApplyScheduleToUi(_result.Schedule);
            // 会话 ID 不再手工维护：执行时统一按用户名查找当前会话
            KindOpen.IsChecked = _result.Kind == ActionKind.Launch;
            KindClose.IsChecked = _result.Kind == ActionKind.Close;
            KindRestart.IsChecked = _result.Kind == ActionKind.Restart;

            // 已经有参数时自动识别预设：--startOneDragon xxx → 一条龙；其它 → 自定义
            ApplyExistingArgs(_result.Arguments);
            _loading = false;
            ApplyPresetUi();

            Loaded += (_, __) => { NameBox.Focus(); UpdateHints(); };
        }

        /// <summary>从会话列表直接带入用户名。</summary>
        public void PresetAccount(string userName)
        {
            _loading = true;
            UserBox.Text = userName;
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

        private void UserBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            ClearError();          // 用户开始改输入 → 收起上次的错误提示
            UpdateHints();
        }

        /// <summary>
        /// 点右侧下拉按钮：弹出已保存账号的菜单，选中后填入用户名。
        /// 左侧是普通 TextBox，任何时候都能自由输入；
        /// 这个按钮只是"从已有账号里挑一个"的快捷方式。
        /// </summary>
        private void UserDrop_Click(object sender, RoutedEventArgs e)
        {
            UserPopupList.Children.Clear();

            if (_accounts.Count == 0)
            {
                UserPopupList.Children.Add(new TextBlock
                {
                    Text = "还没有保存过账号，直接输入用户名即可",
                    FontSize = 11.5,
                    Margin = new Thickness(10, 8, 10, 8),
                    Foreground = (System.Windows.Media.Brush)FindResource("TextMuted")
                });
            }
            else
            {
                foreach (var acc in _accounts)
                {
                    UserPopupList.Children.Add(BuildAccountRow(acc));
                }
            }

            UserPopup.IsOpen = true;
        }

        /// <summary>
        /// 下拉里的一行：用户名（主）+ 备注名（次），整行可点。
        /// 样式跟随主题：透明底、悬停高亮、圆角。
        /// </summary>
        private Border BuildAccountRow(Account acc)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            sp.Children.Add(new TextBlock
            {
                Text = acc.UserName,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary")
            });

            if (!string.IsNullOrWhiteSpace(acc.DisplayName))
            {
                sp.Children.Add(new TextBlock
                {
                    Text = "　" + acc.DisplayName,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary")
                });
            }

            var hover = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x1F, 0x4F, 0xE3, 0xFF));
            hover.Freeze();

            var row = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 7, 10, 7),
                Background = System.Windows.Media.Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = sp
            };

            // 悬停高亮（跟主题一致的淡青）
            row.MouseEnter += (_, __) => row.Background = hover;
            row.MouseLeave += (_, __) => row.Background = System.Windows.Media.Brushes.Transparent;

            row.MouseLeftButtonUp += (_, __) =>
            {
                UserBox.Text = acc.UserName;
                UserPopup.IsOpen = false;
                UpdateHints();
            };

            return row;
        }

        /// <summary>上一次自动生成的名称（用于判断用户是否手动改过）。</summary>
        private string _lastAutoName = "";

        /// <summary>深拷贝定时计划（编辑时用，避免动到原对象的运行进度）。</summary>
        private static ScheduleSpec? CloneSchedule(ScheduleSpec? s)
        {
            if (s == null) return null;
            return new ScheduleSpec
            {
                Enabled = s.Enabled,
                UseWindowsTask = s.UseWindowsTask,
                Kind = s.Kind,
                Hour = s.Hour,
                Minute = s.Minute,
                Second = s.Second,
                WeekDays = new List<int>(s.WeekDays),
                DayOfMonth = s.DayOfMonth,
                Month = s.Month,
                IntervalSeconds = s.IntervalSeconds,
                OnceAt = s.OnceAt,
                PostRunDelaySeconds = s.PostRunDelaySeconds,
                PostRunDelayUnit = s.PostRunDelayUnit,
                RunCount = s.RunCount,
                LastRunAt = s.LastRunAt,
                Paused = s.Paused,
                PauseReason = s.PauseReason
            };
        }

        // ── 定时执行 ──────────────────────────────────────────────────────

        /// <summary>启用/禁用定时区块。</summary>
        private void ScheduleEnabled_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ScheduleBody == null) return;
                bool on = ScheduleEnabledCheck?.IsChecked == true;
                ScheduleBody.IsEnabled = on;
                ScheduleBody.Opacity = on ? 1.0 : 0.45;
                RefreshScheduleSummary();
                // ★ 勾上/取消时也要重算"下次执行"，
                //   否则这块会一直停在"定时未启用"（用户实测）。
                UpdateNextRunHint();
            }
            catch { }
        }

        /// <summary>
        /// 切换定时类型。
        ///
        /// ★ 修复"点了每周之后切不动"：
        ///   原来在本事件里根据 CurrentScheduleKind() 回写所有 ToggleButton 的 IsChecked，
        ///   而回写又会再次触发 Checked → 递归互锁，其他按钮被强制取消后点不动。
        ///   现在改为：**只认触发事件的那个按钮**，把其他按钮静默取消（用 _kindSyncing 防重入）。
        /// </summary>
        private bool _kindSyncing;

        private void ScheduleKind_Checked(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_ready || TimeRow == null) return;
                if (_kindSyncing) return;

                // 谁是触发者就是当前类型
                ScheduleKind k;
                if (ReferenceEquals(sender, KindWeekly)) k = ScheduleKind.Weekly;
                else if (ReferenceEquals(sender, KindMonthly)) k = ScheduleKind.Monthly;
                else if (ReferenceEquals(sender, KindYearly)) k = ScheduleKind.Yearly;
                else if (ReferenceEquals(sender, KindInterval)) k = ScheduleKind.Interval;
                else if (ReferenceEquals(sender, KindOnce)) k = ScheduleKind.Once;
                else k = ScheduleKind.Daily;

                // 静默取消其他按钮（不触发它们的事件）
                _kindSyncing = true;
                try
                {
                    if (k != ScheduleKind.Daily && KindDaily.IsChecked == true) KindDaily.IsChecked = false;
                    if (k != ScheduleKind.Weekly && KindWeekly.IsChecked == true) KindWeekly.IsChecked = false;
                    if (k != ScheduleKind.Monthly && KindMonthly.IsChecked == true) KindMonthly.IsChecked = false;
                    if (k != ScheduleKind.Yearly && KindYearly.IsChecked == true) KindYearly.IsChecked = false;
                    if (k != ScheduleKind.Interval && KindInterval.IsChecked == true) KindInterval.IsChecked = false;
                    if (k != ScheduleKind.Once && KindOnce.IsChecked == true) KindOnce.IsChecked = false;
                }
                finally { _kindSyncing = false; }

                TimeRow.Visibility = (k == ScheduleKind.Interval || k == ScheduleKind.Once)
                    ? Visibility.Collapsed : Visibility.Visible;
                WeekRow.Visibility = k == ScheduleKind.Weekly ? Visibility.Visible : Visibility.Collapsed;
                DayOfMonthRow.Visibility = (k == ScheduleKind.Monthly || k == ScheduleKind.Yearly)
                    ? Visibility.Visible : Visibility.Collapsed;
                IntervalRow.Visibility = k == ScheduleKind.Interval ? Visibility.Visible : Visibility.Collapsed;
                OnceRow.Visibility = k == ScheduleKind.Once ? Visibility.Visible : Visibility.Collapsed;

                bool showMonth = k == ScheduleKind.Yearly;
                MonthLabel.Visibility = showMonth ? Visibility.Visible : Visibility.Collapsed;
                MonthBox.Visibility = showMonth ? Visibility.Visible : Visibility.Collapsed;
                MonthUnit.Visibility = showMonth ? Visibility.Visible : Visibility.Collapsed;
                if (MonthLabel != null)
                    MonthLabel.Text = showMonth ? "日期" : "日期";

                RefreshScheduleSummary();
                UpdateNextRunHint();
            }
            catch { }
        }

        private ScheduleKind CurrentScheduleKind()
        {
            if (KindWeekly?.IsChecked == true) return ScheduleKind.Weekly;
            if (KindMonthly?.IsChecked == true) return ScheduleKind.Monthly;
            if (KindYearly?.IsChecked == true) return ScheduleKind.Yearly;
            if (KindInterval?.IsChecked == true) return ScheduleKind.Interval;
            if (KindOnce?.IsChecked == true) return ScheduleKind.Once;
            return ScheduleKind.Daily;
        }

        /// <summary>从界面读出一个完整计划。</summary>
        private ScheduleSpec ReadScheduleFromUi()
        {
            var spec = new ScheduleSpec
            {
                Enabled = ScheduleEnabledCheck?.IsChecked == true,
                UseWindowsTask = false,     // 已废弃：只保留进程内调度
                Kind = CurrentScheduleKind()
            };

            int Hour = ParseInt(HourBox?.Text, 4, 0, 23);
            int Minute = ParseInt(MinuteBox?.Text, 0, 0, 59);
            int Second = ParseInt(SecondBox?.Text, 0, 0, 59);
            spec.Hour = Hour; spec.Minute = Minute; spec.Second = Second;

            spec.DayOfMonth = ParseInt(DayBox?.Text, 1, 1, 31);
            spec.Month = ParseInt(MonthBox?.Text, 1, 1, 12);

            // 星期
            spec.WeekDays.Clear();
            foreach (var child in WeekDayPanel.Children)
            {
                if (child is CheckBox cb && cb.IsChecked == true
                    && cb.Tag is string tag && int.TryParse(tag, out int d))
                    spec.WeekDays.Add(d);
            }

            // 间隔
            int iv = ParseInt(IntervalBox?.Text, 60, 1, 86400 * 30);
            int unit = 1;
            if (IntervalUnitCombo?.SelectedItem is ComboBoxItem it && it.Tag is string ut
                && int.TryParse(ut, out int u)) unit = u;
            spec.IntervalSeconds = Math.Max(1, iv * unit);

            // 只一次
            if (DateTime.TryParse((OnceDateBox?.Text ?? "").Trim() + " " + (OnceTimeBox?.Text ?? "").Trim(),
                    out DateTime once))
                spec.OnceAt = once;
            else spec.OnceAt = DateTime.Now.AddHours(1);

            // 每次延后
            int pd = ParseInt(PostDelayBox?.Text, 0, 0, 86400 * 365);
            int pdUnit = 60;
            if (PostDelayUnitCombo?.SelectedItem is ComboBoxItem pu && pu.Tag is string pt
                && int.TryParse(pt, out int pv)) pdUnit = pv;
            spec.PostRunDelaySeconds = Math.Max(0, pd * Math.Max(1, pdUnit));
            spec.PostRunDelayUnit = pdUnit switch { 86400 => 3, 3600 => 2, 60 => 1, _ => 0 };

            return spec;
        }

        /// <summary>把计划回填到界面。</summary>
        private void ApplyScheduleToUi(ScheduleSpec? spec)
        {
            _scheduleUpdating = true;
            try
            {
                if (spec == null)
                {
                    if (ScheduleEnabledCheck != null) ScheduleEnabledCheck.IsChecked = false;
    
                    ScheduleEnabled_Changed(null, null);
                    return;
                }

                if (ScheduleEnabledCheck != null) ScheduleEnabledCheck.IsChecked = spec.Enabled;

                if (HourBox != null) HourBox.Text = spec.Hour.ToString();
                if (MinuteBox != null) MinuteBox.Text = spec.Minute.ToString();
                if (SecondBox != null) SecondBox.Text = spec.Second.ToString();
                if (DayBox != null) DayBox.Text = spec.DayOfMonth.ToString();
                if (MonthBox != null) MonthBox.Text = spec.Month.ToString();

                // 回填类型：先静默清空再设一个，避免触发互锁
                _kindSyncing = true;
                try
                {
                    KindDaily.IsChecked = false;
                    KindWeekly.IsChecked = false;
                    KindMonthly.IsChecked = false;
                    KindYearly.IsChecked = false;
                    KindInterval.IsChecked = false;
                    KindOnce.IsChecked = false;
                }
                finally { _kindSyncing = false; }

                switch (spec.Kind)
                {
                    case ScheduleKind.Weekly: KindWeekly.IsChecked = true; break;
                    case ScheduleKind.Monthly: KindMonthly.IsChecked = true; break;
                    case ScheduleKind.Yearly: KindYearly.IsChecked = true; break;
                    case ScheduleKind.Interval: KindInterval.IsChecked = true; break;
                    case ScheduleKind.Once: KindOnce.IsChecked = true; break;
                    default: KindDaily.IsChecked = true; break;
                }

                foreach (var child in WeekDayPanel.Children)
                    if (child is CheckBox cb && cb.Tag is string tag
                        && int.TryParse(tag, out int d))
                        cb.IsChecked = spec.WeekDays.Contains(d);

                if (IntervalBox != null) IntervalBox.Text = Math.Max(1, spec.IntervalSeconds).ToString();
                if (OnceDateBox != null) OnceDateBox.Text = spec.OnceAt.ToString("yyyy-MM-dd");
                if (OnceTimeBox != null) OnceTimeBox.Text = spec.OnceAt.ToString("HH:mm:ss");

                if (PostDelayBox != null)
                {
                    int unitSec = ScheduleSpec.UnitToSeconds(spec.PostRunDelayUnit);
                    PostDelayBox.Text = (spec.PostRunDelaySeconds / Math.Max(1, unitSec)).ToString();
                    PostDelayUnitCombo.SelectedIndex = spec.PostRunDelayUnit switch
                    { 3 => 3, 2 => 2, 1 => 1, _ => 0 };
                }

                ScheduleKind_Checked(null, null);
                ScheduleEnabled_Changed(null, null);
            }
            catch { }
            finally
            {
                _scheduleUpdating = false;
                RefreshScheduleSummary();
                UpdateNextRunHint();     // 回填完成后立刻算一次
            }
        }

        /// <summary>刷新折叠标题右侧的摘要。</summary>
        private void RefreshScheduleSummary()
        {
            try
            {
                if (ScheduleSummary == null) return;
                if (ScheduleEnabledCheck?.IsChecked != true)
                {
                    ScheduleSummary.Text = "（未启用）";
                    return;
                }
                var spec = ReadScheduleFromUi();
                ScheduleSummary.Text = "（" + spec.Display + "）";
            }
            catch { }
        }

        /// <summary>回填界面时的重入保护。</summary>
        private bool _scheduleUpdating;

        /// <summary>
        /// 任何定时参数变化 → 实时重算"下次执行"。
        /// ★ 原来只在切换类型时算一次，改了时间/延后都不刷新，
        ///   界面一直显示"下次执行：—"。
        /// </summary>
        private void ScheduleInput_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_ready || _scheduleUpdating) return;
            RefreshScheduleSummary();
            UpdateNextRunHint();
        }

        /// <summary>下拉（单位）变化 → 同样重算。</summary>
        private void ScheduleUnit_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _scheduleUpdating) return;
            RefreshScheduleSummary();
            UpdateNextRunHint();
        }

        /// <summary>刷新"下次执行"预览。</summary>
        private void UpdateNextRunHint()
        {
            try
            {
                if (NextRunHint == null) return;
                if (ScheduleEnabledCheck?.IsChecked != true)
                {
                    NextRunHint.Text = "定时未启用";
                    return;
                }
                var spec = ReadScheduleFromUi();
                var next = ScheduleCalculator.NextRun(spec);
                NextRunHint.Text = next == null
                    ? "下次执行：不会执行（可能已过期或未选星期）"
                    : "下次执行：" + next.Value.ToString("yyyy-MM-dd HH:mm:ss")
                      + (spec.PostRunDelaySeconds > 0 ? "　·　" + spec.PostRunDelayDisplay : "");
            }
            catch { }
        }

        private static int ParseInt(string? s, int fallback, int min, int max)
        {
            if (int.TryParse((s ?? "").Trim(), out int v))
                return Math.Max(min, Math.Min(max, v));
            return fallback;
        }

        /// <summary>
        /// 按当前选择拼自动名称，例如：
        ///   A2 打开 BetterGIGI
        ///   A2 一条龙 启动
        ///   A2 配置组 CD采集-全采集
        ///   A2 启动
        /// </summary>
        private string BuildAutoName(string user)
        {
            string u = SessionService.BareName(user);
            string exe = Path.GetFileNameWithoutExtension((ExeBox.Text ?? "").Trim().Trim('"'));

            switch (_presetIndex)
            {
                case 1:   // 启动
                    return exe.Length > 0 ? (u + " 启动 " + exe) : (u + " 启动");

                case 2:   // 一条龙
                {
                    string d = _oneDragonName;
                    if (string.IsNullOrWhiteSpace(d) && DragonCombo?.SelectedItem is string ds) d = ds;
                    return string.IsNullOrWhiteSpace(d)
                        ? (u + " 一条龙")
                        : (u + " 一条龙 " + d);
                }

                case 3:   // 配置组
                    return _scriptGroups.Count > 0
                        ? (u + " 配置组 " + _scriptGroups[0]
                           + (_scriptGroups.Count > 1 ? " 等" + _scriptGroups.Count + "组" : ""))
                        : (u + " 配置组");

                case 4:   // 自定义
                    return exe.Length > 0 ? (u + " 自定义参数 " + exe) : (u + " 自定义参数");

                default:  // 无参数
                {
                    string kind = KindClose.IsChecked == true ? "关闭"
                                : KindRestart.IsChecked == true ? "重启" : "打开";
                    return exe.Length > 0 ? (u + " " + kind + " " + exe) : (u + " " + kind);
                }
            }
        }

        /// <summary>
        /// 清掉红色错误提示。
        /// ★ 原来错误只在"保存"时清除，用户改完东西后那条警告还挂着，
        ///   看起来像一直有错。现在任何输入变化都会把它收起来。
        /// </summary>
        private void ClearError()
        {
            try
            {
                if (ErrorText != null) ErrorText.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        private void UpdateHints()
        {
            if (UserBox == null || UserHint == null || CmdHint == null || NameBox == null) return;
            string user = (UserBox.Text ?? "").Trim();
            var acc = MatchAccount(user);

            if (acc != null)
            {
                string saved = ConfigStore.GetPassword(acc);
                UserHint.Text = string.IsNullOrEmpty(saved)
                    ? "账号已选中；没有保存密码（RDP 连接时可能需要手动输入）。"
                    : "账号已选中，RDP 连接时会用已保存的密码自动登录。";
                UserHint.Foreground = (System.Windows.Media.Brush)FindResource("TextMuted");
            }
            else
            {
                UserHint.Text = user.Length == 0
                    ? "输入 Windows 用户名（不带域名），已有的账号会自动带出。"
                    : "账号：" + user + " —— 保存后请到通用设置中配置凭据。";
                UserHint.Foreground = (System.Windows.Media.Brush)FindResource("TextMuted");
            }

            // ★ 自动命名：名称空着或还是上次自动生成的就跟着刷新，
            //   这样"选一条龙 / 选配置组 / 换动作类型"之后名字都会自动变，
            //   用户手动改过的名字则不会被覆盖。
            if (user.Length > 0)
            {
                string auto = BuildAutoName(user);
                if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text == _lastAutoName)
                {
                    if (NameBox.Text != auto) NameBox.Text = auto;
                    _lastAutoName = auto;
                }
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
                _presetIndex = 0;
                return;
            }

            if (raw.StartsWith("--startGroups", StringComparison.OrdinalIgnoreCase))
            {
                _presetIndex = 2;
                // 多个组名用空格分隔
                _scriptGroups.Clear();
                foreach (var g in raw.Substring("--startGroups".Length)
                             .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    _scriptGroups.Add(g.Trim().Trim('"'));
                }
                _presetIndex = 3;
                return;
            }

            if (raw.StartsWith("--startOneDragon", StringComparison.OrdinalIgnoreCase))
            {
                _presetIndex = 2;
                _oneDragonName = raw.Substring("--startOneDragon".Length).Trim().Trim('"');
                return;
            }

            if (string.Equals(raw, "start", StringComparison.OrdinalIgnoreCase))
            {
                _presetIndex = 1;
                return;
            }

            _presetIndex = 4;
            ArgsBox.Text = raw;
        }

        /// <summary>
        /// 按当前选择切换显示：
        ///   0 无参数   → 只显示"打开/关闭/重启"
        ///   1 一条龙   → 显示一条龙下拉（User 下的 OneDragon 目录）
        ///   2 配置组   → 显示配置组下拉（User 下的 ScriptGroup 目录）
        ///   3 自定义   → 显示参数输入框
        /// </summary>
        private void ApplyPresetUi()
        {
            if (!_ready || PresetCombo == null || ArgsHint == null ||
                ArgsTitle == null || ArgsRow == null) return;

            _buildingArgs = true;
            try
            {
                PresetCombo.SelectedIndex = Math.Max(0, Math.Min(4, _presetIndex));

                // 索引：0=无参数 1=启动 2=一条龙 3=配置组 4=自定义
                bool dragon = _presetIndex == 2;
                bool group = _presetIndex == 3;
                bool custom = _presetIndex == 4;

                if (DragonPickShell != null) DragonPickShell.Visibility = dragon ? Visibility.Visible : Visibility.Collapsed;
                if (GroupPickShell != null) GroupPickShell.Visibility = group ? Visibility.Visible : Visibility.Collapsed;
                if (ArgsBox != null) ArgsBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;

                // ★ 选了一条龙 / 配置组时，"打开/关闭/重启"整块隐藏：
                //   这两种方式的语义就是"启动"某个任务，不存在关闭或重启的概念。
                //   隐藏而不是禁用，能省出整栏宽度给下拉列表。
                bool hideKind = dragon || group;
                if (KindShell != null) KindShell.Visibility = hideKind ? Visibility.Collapsed : Visibility.Visible;
                if (KindTitle != null) KindTitle.Visibility = hideKind ? Visibility.Collapsed : Visibility.Visible;

                // ★ 动作隐藏后，把启动参数那栏**横跨整行**：
                //   否则中间还留着 14px 间隔 + 右侧空列，下拉框被限制在半宽，
                //   看起来"没变大"。
                if (ArgsRow != null)
                {
                    Grid.SetColumnSpan(ArgsRow, hideKind ? 3 : 1);
                }

                // 隐藏动作时，强制按"打开"处理，避免残留的"关闭"导致参数被忽略
                if (hideKind && _ready && KindOpen != null)
                {
                    KindOpen.IsChecked = true;
                }

                if (dragon) LoadDragonChoices();
                if (group) LoadGroupChoices();

                if (ArgsHint != null)
                {
                    ArgsHint.Text = _presetIndex switch
                    {
                        1 => "传入 start：等同于打开 BGI 后点「启动」按钮。",
                        2 => "传入 --startOneDragon：不选名称则执行 BGI 页面上选定的那条；选了名称则执行指定的一条龙。",
                        3 => "传入 --startGroups 组名1 组名2…：可多选，BGI 会按顺序执行这些配置组。",
                        4 => "直接把完整参数写进输入框，程序不再添加任何前缀。",
                        _ => "不带任何参数，直接启动程序。"
                    };
                }

                // 关闭动作时参数没意义，淡出
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

        /// <summary>加载该账号 UserOneDragon 下的一条龙配置名。</summary>
        private void LoadDragonChoices()
        {
            try
            {
                if (DragonCombo == null) return;
                var names = ListConfigNames("OneDragon");
                DragonCombo.ItemsSource = names;
                if (!string.IsNullOrEmpty(_oneDragonName) && names.Contains(_oneDragonName))
                    DragonCombo.SelectedItem = _oneDragonName;
                else if (names.Count > 0)
                    DragonCombo.SelectedIndex = 0;
            }
            catch { }
        }

        /// <summary>
        /// 配置组是多选的，这里只刷新摘要文字；
        /// 候选列表在点下拉时按当前 exe 路径现场扫描。
        /// </summary>
        private void LoadGroupChoices()
        {
            try
            {
                RefreshGroupSummary();
            }
            catch { }
        }

        /// <summary>
        /// 列出目标账号 BGI 目录下指定子目录里的配置名（去掉 .json）。
        /// 目录从 ExeBox 的程序路径推出来 —— 即 exe 同级的 User\&lt;sub&gt;。
        /// </summary>
        private List<string> ListConfigNames(string sub)
        {
            var list = new List<string>();
            try
            {
                string exe = (ExeBox?.Text ?? "").Trim().Trim('"');
                if (exe.Length == 0) return list;

                string? dir = Path.GetDirectoryName(exe);
                if (string.IsNullOrEmpty(dir)) return list;

                string target = Path.Combine(dir, "User", sub);
                if (!Directory.Exists(target)) return list;

                foreach (var f in Directory.GetFiles(target, "*.json"))
                {
                    string n = Path.GetFileNameWithoutExtension(f);
                    if (!string.IsNullOrWhiteSpace(n)) list.Add(n);
                }
                list.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return list;
        }

        /// <summary>程序路径变化时刷新配置组/一条龙候选（它们都从 exe 同级 User 目录读）。</summary>
        private void ExeBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_buildingArgs) return;
            ClearError();
            try
            {
                if (_presetIndex == 1) LoadDragonChoices();
                else if (_presetIndex == 2) LoadGroupChoices();
            }
            catch { }
        }

        /// <summary>
        /// BGI 快捷按钮：填入该用户的 BetterGI 路径。
        ///
        /// ★ 首次点击：弹文件选择框，让用户指定 BGI 在哪，
        ///   然后把"用户名 → 路径"记进配置；
        /// ★ 以后点击：同一个用户直接回填记住的路径，不用再选。
        /// ★ 若该用户的 BGI 正在运行，优先用运行中的真实路径（最准）。
        /// </summary>
        private void PickBgi_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string user = SessionService.BareName((UserBox?.Text ?? "").Trim());

                // ★ 必须先填用户名：BGI 路径是"某个用户的"，不填就无从判断该用谁的。
                //   否则会在所有会话里随便找一个运行中的 BGI 填进来，
                //   造成 A2 的动作被填成 A1 的路径这种串号错误。
                if (user.Length == 0)
                {
                    ShowError("请先填写用户名，再点 BGI 自动填入该用户的程序路径。");
                    try { UserBox?.Focus(); } catch { }
                    return;
                }

                // ① 优先：该用户正在运行的 BetterGI 进程路径
                string running = FindRunningBgiPath(user);
                if (!string.IsNullOrEmpty(running))
                {
                    ExeBox.Text = running;
                    SaveBgiPath(user, running);
                    UpdateHints();
                    return;
                }

                // ② 其次：之前记住的路径
                string saved = LoadBgiPath(user);
                if (!string.IsNullOrEmpty(saved) && File.Exists(saved))
                {
                    ExeBox.Text = saved;
                    UpdateHints();
                    return;
                }

                // ③ 首次：让用户指定
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = user.Length > 0
                        ? ("指定「" + user + "」的 BetterGI.exe 位置")
                        : "指定 BetterGI.exe 的位置",
                    Filter = "BetterGI (BetterGI.exe)|BetterGI.exe|程序 (*.exe)|*.exe",
                    CheckFileExists = true
                };
                if (dlg.ShowDialog() != true) return;

                ExeBox.Text = dlg.FileName;
                SaveBgiPath(user, dlg.FileName);
                UpdateHints();
            }
            catch (Exception ex)
            {
                ShowError("选择 BGI 路径失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 找**指定用户会话**里正在运行的 BetterGI 路径。
        /// ★ user 为空时直接返回空 —— 绝不跨会话乱拿别人的 BGI，
        ///   否则会把 A1 的路径填给 A2 的动作。
        /// </summary>
        private static string FindRunningBgiPath(string user)
        {
            if (string.IsNullOrWhiteSpace(user)) return "";
            try
            {
                // 先把这个用户对应的会话号查出来，避免在循环里反复 query user
                var sessions = SessionService.QueryAsync().GetAwaiter().GetResult();
                var s = SessionService.Find(sessions, user);
                if (s == null) return "";
                int wantSession = s.Id;

                foreach (var p in System.Diagnostics.Process.GetProcessesByName("BetterGI"))
                {
                    try
                    {
                        if (p.SessionId != wantSession) continue;
                        string? exe = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(exe)) return exe;
                    }
                    catch { }
                }
            }
            catch { }
            return "";
        }

        /// <summary>按"用户名 → BGI 路径"从配置里读回。</summary>
        private string LoadBgiPath(string user)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(user)) return "";
                // ★ 统一走解析器：手动指定 > 正在运行 > 记忆，避免各处看到不同结果
                return BgiPathResolver.GetPath(user);
            }
            catch { return ""; }
        }

        /// <summary>记住"用户名 → BGI 路径"。</summary>
        private void SaveBgiPath(string user, string path)
        {
            try
            {
                if (user.Length == 0 || string.IsNullOrWhiteSpace(path)) return;
                var cfg = App.Config.Settings;
                // ★ 用户手填 = 明确指定，走 Pin（自动探测之后不再覆盖）
                BgiPathResolver.Pin(user, path);
                ConfigStore.Save(App.Config);
            }
            catch { }
        }

        private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _buildingArgs) return;
            if (PresetCombo.SelectedIndex < 0) return;
            _presetIndex = PresetCombo.SelectedIndex;
            ApplyPresetUi();
        }

        private void DragonCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_buildingArgs) return;
            if (DragonCombo.SelectedItem is string s) _oneDragonName = s;
            UpdateHints();
        }

        private void ArgsBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_buildingArgs) return;
            UpdateHints();
        }

        /// <summary>
        /// 点"配置组"右侧下拉：弹出可勾选列表（BGI 支持一次传多个组名，空格分隔）。
        /// </summary>
        private void GroupPick_Click(object sender, RoutedEventArgs e)
        {
            GroupPopupList.Children.Clear();

            var names = ListConfigNames("ScriptGroup");
            if (names.Count == 0)
            {
                GroupPopupList.Children.Add(new TextBlock
                {
                    Text = "没有找到配置组（请先确认上面的程序路径是 BetterGI.exe）",
                    FontSize = 11.5,
                    Margin = new Thickness(10, 8, 10, 8),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextMuted")
                });
            }
            else
            {
                foreach (var n in names)
                {
                    var cb = new CheckBox
                    {
                        Content = n,
                        FontSize = 12,
                        Margin = new Thickness(9, 5, 9, 5),
                        IsChecked = _scriptGroups.Contains(n),
                        Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary")
                    };
                    string captured = n;
                    cb.Checked += (_, __) =>
                    {
                        if (!_scriptGroups.Contains(captured)) _scriptGroups.Add(captured);
                        RefreshGroupSummary();
                    };
                    cb.Unchecked += (_, __) =>
                    {
                        _scriptGroups.Remove(captured);
                        RefreshGroupSummary();
                    };
                    GroupPopupList.Children.Add(cb);
                }
            }

            GroupPopup.IsOpen = true;
        }

        /// <summary>刷新"已选配置组"的摘要文字。</summary>
        private void RefreshGroupSummary()
        {
            try
            {
                if (GroupSummary == null) return;
                GroupSummary.Text = _scriptGroups.Count == 0
                    ? "（未选择）"
                    : string.Join("  ", _scriptGroups);
                UpdateHints();
            }
            catch { }
        }

        /// <summary>
        /// 拼出最终传给 BGI 的命令行参数（依据官方文档
        /// bettergi.com/feats/command/introduce.html）：
        ///
        ///   无参数     → ""
        ///   启动       → "start"
        ///   一条龙     → "--startOneDragon"（不带名称 = 执行页面上选定的那条）
        ///                "--startOneDragon 配置名称"（指定，需 0.45.3+）
        ///   配置组     → "--startGroups 组名1 组名2 …"（多个用空格分隔）
        ///   自定义     → 原样
        /// </summary>
        private string BuildArguments()
        {
            switch (_presetIndex)
            {
                case 1:
                    // 启动：等同双击后点「启动」按钮
                    return "start";

                case 2:
                {
                    string dragon = _oneDragonName;
                    if (string.IsNullOrWhiteSpace(dragon) && DragonCombo?.SelectedItem is string ds) dragon = ds;
                    // 不填名称时 BGI 会用页面上选定的那条，合法
                    return string.IsNullOrWhiteSpace(dragon)
                        ? "--startOneDragon"
                        : "--startOneDragon " + Quote(dragon.Trim());
                }

                case 3:
                {
                    if (_scriptGroups.Count == 0) return "--startGroups";
                    var parts = new List<string>();
                    foreach (var g in _scriptGroups) parts.Add(Quote(g));
                    return "--startGroups " + string.Join(" ", parts);
                }

                case 4:
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
            if (UserBox == null || NameBox == null || CmdHint == null) return;

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
            string user = SessionService.BareName((UserBox.Text ?? "").Trim());
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

            string user = SessionService.BareName((UserBox.Text ?? "").Trim());
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
            // ★ 不再保存固定会话号：会话号每次登录都会变，存下来反而容易指向错误会话。
            //   执行时按用户名现查（ActionService.Resolve 已有这条路径）。
            account.SessionId = 0;

            _result.AccountId = account.Id;
            _result.ExePath = exe;
            _result.Arguments = BuildArguments();
            _result.Kind = CurrentKind;
            _result.DelaySeconds = Math.Max(0, delay);
            _result.Schedule = ReadScheduleFromUi();
            _result.StopBgiFirst = StopBgiCheck?.IsChecked == true;
            _result.CloseGenshin = CloseGenshinCheck?.IsChecked == true;
            int stopWait;
            _result.StopWaitSeconds = int.TryParse(StopWaitBox?.Text, out stopWait) ? Math.Max(0, stopWait) : 2;
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
