using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Views
{
    /// <summary>
    /// 路线更新页：云端下载 / 本地包 → 多个路线根目录 → 只替换同名第一层文件夹。
    ///
    /// ★ 关键约束（用户明确要求，改动前先读）：
    ///   · 包内第一层文件夹就是更新单位，**不剥包裹层**
    ///   · 目标目录里只删**同名**文件夹，其余一律不动
    ///   · 手动更新与定时更新走同一个 RouteUpdater.RunAsync 入口
    ///
    /// ★ 定时只在程序运行期间生效（与动作调度一致），不做 Windows 计划任务。
    /// </summary>
    public partial class RouteUpdatePage : UserControl
    {
        private sealed class TargetRowVm
        {
            public RouteUpdateTarget Model = new RouteUpdateTarget();
            public CheckBox? Check;
            public TextBox? Box;
        }

        private readonly List<TargetRowVm> _rows = new List<TargetRowVm>();
        private CancellationTokenSource? _cts;
        private bool _running;
        private bool _loading;
        private readonly List<string> _logLines = new List<string>();
        private int _inspectVersion;

        private RouteUpdateSettings Settings => App.Config.Settings.RouteUpdate;

        public RouteUpdatePage()
        {
            InitializeComponent();
            Loaded += (_, __) => Reload();
        }

        // ══════════ 加载 ══════════

        public void Reload()
        {
            if (_running) return;

            _loading = true;
            try
            {
                var s = Settings;

                ZipBox.Text = s.LocalArchive ?? "";
                BackupCheck.IsChecked = s.BackupBeforeReplace;

                // 目标列表
                TargetList.Children.Clear();
                _rows.Clear();
                foreach (var t in s.Targets)
                {
                    var vm = new TargetRowVm { Model = t };
                    _rows.Add(vm);
                    TargetList.Children.Add(BuildTargetRow(vm));
                }
                if (_rows.Count == 0) AddTargetRow(new RouteUpdateTarget { Enabled = true }, focus: false);

                // 定时
                var spec = s.Schedule ?? new ScheduleSpec();
                SchedEnableCheck.IsChecked = spec.Enabled;
                SchedKindBox.SelectedIndex = spec.Kind == ScheduleKind.Interval ? 1 : 0;
                HourBox.Text = spec.Hour.ToString("00");
                MinuteBox.Text = spec.Minute.ToString("00");
                IntervalBox.Text = Math.Max(1, spec.IntervalSeconds / 3600).ToString();

                ApplyScheduleUi();
                RefreshRootStatus();
            }
            catch (Exception ex)
            {
                SubText.Text = "加载失败：" + ex.Message;
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>刷新标题下的说明：有几个有效目标。</summary>
        private void RefreshRootStatus()
        {
            var targets = Settings.ActiveTargets();
            if (targets.Count == 0)
            {
                SubText.Text = @"先添加至少一个路线根目录，例如 D:\AAA挂机专区\A2\BetterGI\User\AutoPathing";
                return;
            }

            int good = 0;
            foreach (var t in targets)
                if (RouteUpdater.ValidateTargetRoot(t, out _).Length == 0) good++;

            string sched = DescribeSchedule();
            SubText.Text = "已勾选 " + targets.Count + " 个目标，其中 " + good + " 个目录可用"
                         + (sched.Length > 0 ? "　·　" + sched : "");
        }

        // ══════════ 目标行 ══════════

        private Border BuildTargetRow(TargetRowVm vm)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var chk = new CheckBox
            {
                Style = (Style)FindResource("GlassCheckBox"),
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = vm.Model.Enabled,
                ToolTip = "勾选后本次更新会处理这个目标"
            };
            chk.Checked += (_, __) => { vm.Model.Enabled = true; if (!_loading) OnTargetsChanged(); };
            chk.Unchecked += (_, __) => { vm.Model.Enabled = false; if (!_loading) OnTargetsChanged(); };
            vm.Check = chk;
            grid.Children.Add(chk);

            var box = new TextBox
            {
                Style = (Style)FindResource("GlassTextBox"),
                Height = 32,
                Padding = new Thickness(10, 0, 10, 0),
                FontSize = 11.5,
                VerticalContentAlignment = VerticalAlignment.Center,
                Text = vm.Model.Path ?? "",
                Margin = new Thickness(8, 0, 8, 0),
                ToolTip = @"路线库根目录，通常是 <BGI 目录>\User\AutoPathing"
            };
            box.LostFocus += (_, __) => { vm.Model.Path = box.Text?.Trim() ?? ""; if (!_loading) OnTargetsChanged(); };
            vm.Box = box;
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var btnBrowse = new Button
            {
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(10, 4, 10, 4),
                Content = new TextBlock { Text = "浏览", FontSize = 10.5 }
            };
            btnBrowse.Click += (_, __) => BrowseTarget(vm);
            btns.Children.Add(btnBrowse);

            var btnDel = new Button
            {
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(6, 0, 0, 0),
                Content = new TextBlock { Text = "移除", FontSize = 10.5 }
            };
            btnDel.Click += (_, __) => RemoveTarget(vm);
            btns.Children.Add(btnDel);

            Grid.SetColumn(btns, 2);
            grid.Children.Add(btns);

            return new Border
            {
                Padding = new Thickness(6, 5, 6, 5),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(0, 0, 0, 5),
                Child = grid
            };
        }

        private void AddTargetRow(RouteUpdateTarget model, bool focus)
        {
            var vm = new TargetRowVm { Model = model };
            _rows.Add(vm);
            Settings.Targets.Add(model);

            var row = BuildTargetRow(vm);
            TargetList.Children.Add(row);

            if (focus && vm.Box != null)
            {
                vm.Box.Focus();
                vm.Box.CaretIndex = vm.Box.Text.Length;
            }
        }

        private void AddTarget_Click(object sender, RoutedEventArgs e)
        {
            AddTargetRow(new RouteUpdateTarget { Enabled = true }, focus: true);
            OnTargetsChanged();
        }

        private void RemoveTarget(TargetRowVm vm)
        {
            _rows.Remove(vm);
            Settings.Targets.Remove(vm.Model);

            // 重建列表，保持界面与模型一致
            TargetList.Children.Clear();
            var keep = new List<TargetRowVm>(_rows);
            _rows.Clear();
            foreach (var k in keep)
            {
                _rows.Add(k);
                TargetList.Children.Add(BuildTargetRow(k));
            }

            OnTargetsChanged();
        }

        private void BrowseTarget(TargetRowVm vm)
        {
            try
            {
                var dlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = @"选择路线库根目录（通常是 BGI 的 User\AutoPathing）",
                    ShowNewFolderButton = true
                };

                string current = vm.Box?.Text?.Trim() ?? "";
                if (Directory.Exists(current)) dlg.SelectedPath = current;

                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                if (vm.Box != null) vm.Box.Text = dlg.SelectedPath;
                vm.Model.Path = dlg.SelectedPath;
                OnTargetsChanged();
            }
            catch (Exception ex)
            {
                ThemedMessage.Error(Window.GetWindow(this), "选择目录失败", ex.Message);
            }
        }

        /// <summary>从已配置的账号 BGI 路径推导出各自的 User\AutoPathing。</summary>
        private void PickFromAccounts_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cfg = App.Config;
                var map = cfg.Settings.BgiPaths ?? new Dictionary<string, string>();
                if (map.Count == 0)
                {
                    ThemedMessage.Info(Window.GetWindow(this), "没有可用账号",
                        "还没有配置任何 BGI 路径。\n\n请先在「设置」或「更新 BGI」页配置账号的 BetterGI.exe 路径。");
                    return;
                }

                var added = new List<string>();
                foreach (var kv in map.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    string exe = kv.Value ?? "";
                    if (exe.Length == 0) continue;

                    string root = Path.GetDirectoryName(exe) ?? "";
                    if (root.Length == 0) continue;

                    string autoPathing = Path.Combine(root, "User", "AutoPathing");
                    if (!Directory.Exists(autoPathing)) continue;

                    if (Settings.Targets.Any(t => string.Equals((t.Path ?? "").Trim(), autoPathing,
                            StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var model = new RouteUpdateTarget { Path = autoPathing, Enabled = true };
                    Settings.Targets.Add(model);
                    var vm = new TargetRowVm { Model = model };
                    _rows.Add(vm);
                    TargetList.Children.Add(BuildTargetRow(vm));
                    added.Add(kv.Key + " → " + autoPathing);
                }

                if (added.Count == 0)
                {
                    ThemedMessage.Info(Window.GetWindow(this), "没有新增",
                        @"这些账号的 User\AutoPathing 目录要么不存在，要么已经在列表里了。");
                }
                else
                {
                    OnTargetsChanged();
                    ThemedMessage.Info(Window.GetWindow(this), "已添加",
                        "新增 " + added.Count + " 个目标：\n\n" + string.Join("\n", added));
                }
            }
            catch (Exception ex)
            {
                ThemedMessage.Error(Window.GetWindow(this), "生成失败", ex.Message);
            }
        }

        private void OnTargetsChanged()
        {
            RefreshRootStatus();
            SaveSettings();
            InvalidateInspect();
        }

        // ══════════ 路线包 ══════════

        /// <summary>
        /// 选择本地路线包。
        ///
        /// ★ 初始目录 = 云端下载缓存目录（历史包都在这里，直接就能选来回档）。
        ///   用户一旦手选过别的目录，下次优先回到那个目录。
        /// </summary>
        private void PickZip_Click(object sender, RoutedEventArgs e)
        {
            string startDir = "";

            // ① 上次选过的文件所在目录（用户明确用过的位置优先）
            string last = (Settings.LocalArchive ?? "").Trim();
            if (last.Length > 0)
            {
                try
                {
                    string d = Path.GetDirectoryName(last) ?? "";
                    if (d.Length > 0 && Directory.Exists(d)) startDir = d;
                }
                catch { }
            }

            // ② 否则用下载缓存目录（历史包都在这里）
            if (startDir.Length == 0)
            {
                try
                {
                    string cache = RouteUpdater.CacheRoot;
                    if (Directory.Exists(cache)) startDir = cache;
                }
                catch { }
            }

            var dlg = new OpenFileDialog
            {
                Title = "选择路线包（历史包在下载目录里，可用来回档）",
                Filter = "压缩包 (*.zip;*.7z;*.rar;*.tar;*.gz)|*.zip;*.7z;*.rar;*.tar;*.gz|" +
                         "ZIP (*.zip)|*.zip|7Z (*.7z)|*.7z|RAR (*.rar)|*.rar|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                InitialDirectory = startDir.Length > 0 ? startDir : ""
            };
            if (dlg.ShowDialog() != true) return;

            ZipBox.Text = dlg.FileName;
            Settings.LocalArchive = dlg.FileName;
            SaveSettings();
            _ = InspectAsync();
        }

        /// <summary>打开下载目录（历史包所在位置），方便直接回档。</summary>
        private void OpenCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = RouteUpdater.CacheRoot;
                Directory.CreateDirectory(dir);

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ThemedMessage.Error(Window.GetWindow(this), "打开失败", ex.Message);
            }
        }

        private void ClearZip_Click(object sender, RoutedEventArgs e)
        {
            ZipBox.Text = "";
            Settings.LocalArchive = "";
            SaveSettings();
            _ = InspectAsync();
        }

        /// <summary>
        /// 检查云端是否有新版本。
        ///
        /// ★ 界面只显示「有没有更新」，**绝不显示地址、主机名、包名路径**。
        /// </summary>
        private async void CheckCloud_Click(object sender, RoutedEventArgs e)
        {
            BtnCheckCloud.IsEnabled = false;
            SetCloudState("正在检查…", false);

            try
            {
                // ★ 未配置服务器地址时不发请求，并给出明确提示
                if (!ServerEndpoints.IsConfigured)
                {
                    SetCloudState("未配置服务器地址（设置 → 服务器）", true);
                    return;
                }

                var s = Settings;
                var (ok, _, version, _) = await RouteUpdater.ProbeAsync(s.EffectiveCloudUrl)
                                                          .ConfigureAwait(true);

                if (!ok)
                {
                    SetCloudState("无法连接更新源", true);
                    return;
                }

                if (version.Length > 0) s.CloudVersion = version;

                if (version.Length > 0
                    && string.Equals(version, s.LastAppliedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    SetCloudState("已是最新版本，无需更新", false);
                }
                else if (version.Length > 0)
                {
                    SetCloudState("发现新版本，可以更新", false);
                }
                else
                {
                    SetCloudState("连接正常", false);
                }

                SaveSettings();
            }
            catch
            {
                SetCloudState("检查失败", true);
            }
            finally
            {
                BtnCheckCloud.IsEnabled = true;
            }
        }

        /// <summary>更新云端状态那一行（只写状态文案，不含任何地址）。</summary>
        private void SetCloudState(string text, bool error)
        {
            try
            {
                CloudStatus.Text = text;
                CloudStatus.Foreground = error
                    ? new SolidColorBrush(Color.FromRgb(0xF0, 0x6B, 0x6B))
                    : (Brush)FindResource("TextSecondary");

                if (CloudDot != null)
                    CloudDot.Fill = error
                        ? new SolidColorBrush(Color.FromRgb(0xF0, 0x6B, 0x6B))
                        : (Brush)FindResource("MintBrush");
            }
            catch { }
        }

        private void InvalidateInspect()
        {
            FolderList.Children.Clear();
            _inspectVersion++;
        }

        /// <summary>
        /// 核对包内第一层文件夹。本地包优先；没有本地包时若填了云端地址，
        /// 会先把云端包下载到缓存再核对（缓存目录留最近 5 个）。
        /// </summary>
        private async Task InspectAsync()
        {
            string local = ZipBox.Text?.Trim() ?? "";

            // 没有本地包时用云端更新源（不显示地址）
            if (local.Length == 0 && Settings.EffectiveCloudUrl.Length == 0)
            {
                InvalidateInspect();
                return;
            }

            int version = ++_inspectVersion;

            FolderList.Children.Clear();
            FolderList.Children.Add(InfoRow("核对中…（下载/解压到临时目录，不动目标目录）", false));
            BtnRescan.IsEnabled = false;

            try
            {
                string archive = local;

                // ★ 只核对本地包。
                //   云端包不在核对阶段下载：那样会在"无新版本"时白白下载一次
                //   （用户明确要求：无更新就不下载）。云端包在真正更新时才取。
                if (archive.Length == 0 || !File.Exists(archive))
                {
                    if (version == _inspectVersion)
                    {
                        FolderList.Children.Clear();
                        FolderList.Children.Add(InfoRow(
                            "未选择本地路线包。云端包会在「开始更新」时检查并下载，这里只核对本地包。", false));
                        BtnRescan.IsEnabled = true;
                        SetStatus("等待云端更新");
                    }
                    return;
                }

                // 用第一个有效目标判断"替换 / 新增"
                string probe = Settings.ActiveTargets().FirstOrDefault() ?? "";
                var (folders, loose) = await Task.Run(
                    () => RouteUpdater.InspectAsync(archive, probe)).ConfigureAwait(true);

                if (version != _inspectVersion) return;

                FolderList.Children.Clear();

                if (folders.Count == 0)
                {
                    FolderList.Children.Add(InfoRow(
                        "⚠ 没找到第一层文件夹。路线包应为：压缩包 → 文件夹 → 路线文件。", true));
                }
                else
                {
                    foreach (var f in folders) FolderList.Children.Add(BuildFolderRow(f));
                    FolderList.Children.Add(InfoRow(
                        "「替换 / 新增」按第一个勾选的目标准确判断，其余目标在更新时各自比对。", false));
                }

                if (loose.Count > 0)
                {
                    FolderList.Children.Add(InfoRow(
                        "⚠ 包内第一层还有 " + loose.Count + " 个散落文件，按规则不处理："
                        + string.Join("、", loose.Names.Take(5)), true));
                }

                int replace = folders.Count(f => f.Replaces);
                SetStatus(folders.Count + " 个文件夹，其中 " + replace + " 个与目标同名（会被替换）");
            }
            catch (Exception ex)
            {
                if (version != _inspectVersion) return;
                FolderList.Children.Clear();
                FolderList.Children.Add(InfoRow("✕ 核对失败：" + ex.Message, true));
                SetStatus("核对失败");
            }
            finally
            {
                if (version == _inspectVersion) BtnRescan.IsEnabled = true;
            }
        }

        private void Rescan_Click(object sender, RoutedEventArgs e) => _ = InspectAsync();

        private Border BuildFolderRow(RouteUpdater.PackageFolder f)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = f.Name,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(10, 0, 10, 0)
            };
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            var info = new TextBlock
            {
                Text = f.FileCount + " 个文件 · " + RouteUpdater.Size(f.TotalBytes),
                FontSize = 10.5,
                FontFamily = new FontFamily("Consolas"),
                Foreground = (Brush)FindResource("TextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(info, 1);
            grid.Children.Add(info);

            var tag = new TextBlock
            {
                Text = f.Replaces ? "替换同名" : "新增",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = f.Replaces
                    ? new SolidColorBrush(Color.FromRgb(0xF0, 0xC4, 0x6B))
                    : new SolidColorBrush(Color.FromRgb(0x6B, 0xD8, 0xC4)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(tag, 2);
            grid.Children.Add(tag);

            return new Border
            {
                Padding = new Thickness(0, 7, 0, 7),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(0, 0, 0, 5),
                Child = grid
            };
        }

        private Border InfoRow(string text, bool warn)
        {
            return new Border
            {
                Padding = new Thickness(10, 7, 10, 7),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(0, 0, 0, 5),
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = warn
                        ? new SolidColorBrush(Color.FromRgb(0xF0, 0xC4, 0x6B))
                        : (Brush)FindResource("TextSecondary")
                }
            };
        }

        // ══════════ 定时 ══════════

        private void SchedEnable_Changed(object sender, RoutedEventArgs e)
        {
            ApplyScheduleUi();
            if (!_loading) { SaveScheduleFromUi(); }
        }

        private void SchedKind_Changed(object sender, SelectionChangedEventArgs e)
        {
            ApplyScheduleUi();
            if (!_loading) { SaveScheduleFromUi(); }
        }

        private void ApplyScheduleUi()
        {
            bool on = SchedEnableCheck.IsChecked == true;
            if (SchedDetail != null) SchedDetail.IsEnabled = on;

            bool interval = SchedKindBox?.SelectedIndex == 1;
            if (DailyPanel != null) DailyPanel.Visibility = interval ? Visibility.Collapsed : Visibility.Visible;
            if (IntervalPanel != null) IntervalPanel.Visibility = interval ? Visibility.Visible : Visibility.Collapsed;

            if (SchedNextText != null)
                SchedNextText.Text = on ? DescribeSchedule() : "未启用";
        }

        /// <summary>把界面上的定时设置写回配置。</summary>
        private void SaveScheduleFromUi()
        {
            try
            {
                var spec = Settings.Schedule ?? (Settings.Schedule = new ScheduleSpec());

                spec.Enabled = SchedEnableCheck.IsChecked == true;
                spec.Kind = SchedKindBox?.SelectedIndex == 1 ? ScheduleKind.Interval : ScheduleKind.Daily;

                if (int.TryParse(HourBox.Text?.Trim(), out int h)) spec.Hour = Math.Clamp(h, 0, 23);
                if (int.TryParse(MinuteBox.Text?.Trim(), out int m)) spec.Minute = Math.Clamp(m, 0, 59);
                spec.Second = 0;

                if (int.TryParse(IntervalBox.Text?.Trim(), out int hours))
                    spec.IntervalSeconds = Math.Max(1, Math.Min(hours, 24 * 30)) * 3600;

                // ★ 改了时刻/间隔就把进度重置，否则旧的 RunCount 会把下次时刻推到奇怪的位置
                ScheduleCalculator.ResetProgress(spec);

                SaveSettings();
                ApplyScheduleUi();
            }
            catch (Exception ex)
            {
                App.AppendErrorLog("RouteUpdatePage.SaveSchedule", ex);
            }
        }

        /// <summary>给用户看的一句话：什么时候跑下一次。</summary>
        private string DescribeSchedule()
        {
            var spec = Settings.Schedule;
            if (spec == null || !spec.Enabled) return "";

            try
            {
                var next = ScheduleCalculator.NextRun(spec, DateTime.Now);
                string when = spec.Kind == ScheduleKind.Interval
                    ? "每 " + Math.Max(1, spec.IntervalSeconds / 3600) + " 小时"
                    : "每天 " + spec.Hour.ToString("00") + ":" + spec.Minute.ToString("00");

                if (next == null) return when + "（暂无下次时刻）";

                var span = next.Value - DateTime.Now;
                string left = span.TotalDays >= 1
                    ? ((int)span.TotalDays) + " 天 " + span.Hours + " 小时后"
                    : span.Hours > 0
                        ? span.Hours + " 小时 " + span.Minutes + " 分后"
                        : Math.Max(0, span.Minutes) + " 分后";

                return "定时：" + when + "　下次 " + next.Value.ToString("MM-dd HH:mm") + "（" + left + "）";
            }
            catch { return "定时已启用"; }
        }

        // ══════════ 保存 ══════════

        private void SaveSettings()
        {
            try
            {
                Settings.LocalArchive = ZipBox.Text?.Trim() ?? "";
                Settings.BackupBeforeReplace = BackupCheck.IsChecked == true;

                ConfigStore.Save(App.Config);
            }
            catch (Exception ex)
            {
                App.AppendErrorLog("RouteUpdatePage.SaveSettings", ex);
            }
        }

        // ══════════ 开始更新 ══════════

        private async void StartUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;

            SaveScheduleFromUi();
            SaveSettings();

            var targets = Settings.ActiveTargets();
            if (targets.Count == 0)
            {
                ThemedMessage.Info(Window.GetWindow(this), "没有目标", "请先添加并勾选至少一个路线根目录。");
                return;
            }

            // 逐个校验目标
            var bad = new List<string>();
            foreach (var t in targets)
            {
                string problem = RouteUpdater.ValidateTargetRoot(t, out _);
                if (problem.Length > 0) bad.Add(t + "\n  → " + problem);
            }
            if (bad.Count > 0)
            {
                ThemedMessage.Error(Window.GetWindow(this), "目标目录有问题",
                    "这些问题必须先解决：\n\n" + string.Join("\n\n", bad));
                return;
            }

            string local = ZipBox.Text?.Trim() ?? "";
            bool hasLocal = local.Length > 0 && File.Exists(local);
            bool hasCloud = Settings.EffectiveCloudUrl.Length > 0;

            if (!hasLocal && !hasCloud)
            {
                ThemedMessage.Info(Window.GetWindow(this), "缺少路线包",
                    "请选择本地路线包，或配置云端更新源。");
                return;
            }

            if (!ThemedMessage.Confirm(Window.GetWindow(this), "开始更新路线",
                    "路线包：" + (hasLocal ? Path.GetFileName(local) : "云端下载") + "\n\n" +
                    "目标（" + targets.Count + " 个）：\n" + string.Join("\n", targets) + "\n\n" +
                    "只删除**与包内同名**的第一层文件夹，其它文件夹和文件不会改动。\n\n继续吗？"))
                return;

            _cts = new CancellationTokenSource();
            _logLines.Clear();
            if (LogText != null) LogText.Text = "";
            SetRunning(true);
            AppendLog("═══ 开始更新路线 ═══");
            AppendLog(hasLocal ? "路线包：" + Path.GetFileName(local) : "更新源：云端");
            AppendLog("目标数：" + targets.Count);
            AppendLog("");

            var progress = new Progress<RouteUpdater.Progress>(p =>
            {
                if (p.Percent >= 0) SetProgress(p.Percent, p.Text);
                if (p.IsLog) AppendLog(p.Text);
                if (p.Text.Length > 0) SetStatus(p.Text);
            });

            RouteUpdater.BatchResult result;
            try
            {
                result = await RouteUpdater.RunAsync(Settings, AppendLog, progress, _cts.Token)
                                            .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                result = new RouteUpdater.BatchResult { Ok = false, Message = ex.Message };
            }

            SetRunning(false);

            Settings.LastRunAt = DateTime.Now;
            Settings.LastResult = result.Message;
            SaveSettings();

            if (result.Ok)
            {
                SetProgress(100, "完成");
                SetStatus(result.Message);
                ThemedMessage.Info(Window.GetWindow(this), "更新完成", result.Message);
            }
            else
            {
                SetProgress(0, "未完成");
                SetStatus(result.Message);
                ThemedMessage.Error(Window.GetWindow(this), "更新未全部成功", result.Message);
            }

            RefreshRootStatus();
            _ = InspectAsync();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            try { _cts?.Cancel(); } catch { }
            AppendLog("⏹ 已请求停止…");
            SetStatus("正在停止…");
        }

        // ══════════ 状态 ══════════

        private void SetRunning(bool running)
        {
            _running = running;
            BtnApply.IsEnabled = !running;
            BtnCancel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            ZipBox.IsEnabled = !running;
            BtnRescan.IsEnabled = !running;
            if (BtnCheckCloud != null) BtnCheckCloud.IsEnabled = !running;
        }

        private void SetStatus(string text)
        {
            try { StatusText.Text = text; } catch { }
        }

        private void SetProgress(int percent, string text)
        {
            try
            {
                if (ProgressText != null) ProgressText.Text = text;

                if (ProgressFill != null && ProgressFill.Parent is Grid g)
                {
                    double w = percent < 0
                        ? g.ActualWidth * 0.35
                        : g.ActualWidth * Math.Max(0, Math.Min(100, percent)) / 100.0;
                    ProgressFill.Width = Math.Max(0, w);
                }
            }
            catch { }
        }

        /// <summary>追加一行日志（只保留最近 500 行）。</summary>
        private void AppendLog(string line)
        {
            try
            {
                if (LogText == null) return;

                _logLines.Add(line);
                if (_logLines.Count > 500)
                    _logLines.RemoveRange(0, _logLines.Count - 500);

                LogText.Text = string.Join("\n", _logLines);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { LogScroll?.ScrollToEnd(); } catch { }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { }
        }
    }
}
