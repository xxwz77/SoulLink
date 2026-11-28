using System;
using SoulLink.Controls;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SoulLink.Services;

namespace SoulLink.Views
{
    /// <summary>
    /// 更新 BGI 页：选更新包 → 勾账号 → 解压覆盖。
    ///
    /// ★ 只处理选中账号的进程（按会话过滤），不碰其他用户。
    /// </summary>
    public partial class BgiUpdatePage : UserControl
    {
        private sealed class UserRowVm
        {
            public string UserName = "";
            public string BgiPath = "";
            public CheckBox? Check;
            public TextBlock? PathText;
        }

        private readonly List<UserRowVm> _rows = new List<UserRowVm>();
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _lookupCts;
        private CancellationTokenSource? _localScanCts;
        private sealed record LocalPackage(string Path)
        {
            public string DisplayLabel => System.IO.Path.GetFileName(Path);
        }
        private bool _running;
        private readonly List<string> _logLines = new List<string>();

        public BgiUpdatePage()
        {
            InitializeComponent();
            CoordinatorAddressBox.Text = App.Config.Settings.BgiCoordinatorUrl;
            DeleteDownloadedPackageCheck.IsChecked = App.Config.Settings.BgiDeleteDownloadedPackageAfterSuccess;
            DeleteDownloadedPackageCheck.Checked += DeleteDownloadedPackage_Changed;
            DeleteDownloadedPackageCheck.Unchecked += DeleteDownloadedPackage_Changed;
            LocalPackageBox.SelectionChanged += (_, _) => UpdateButtonEnabled();
            UpdatePackageDirectoryLabel();
            DeleteDownloadedPackageCheck.IsEnabled = false;
            UpdateSourceBox.SelectionChanged += (_, _) => RefreshUpdateSource();
            OnlinePackageBox.SelectionChanged += (_, _) => UpdateButtonEnabled();
            CoordinatorAddressBox.TextChanged += (_, _) => InvalidateOnlineVersion("地址已更改，按 Enter 或点击刷新");
            CoordinatorAddressBox.KeyDown += async (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                await CheckOnlineSourceAsync();
            };
            Loaded += (_, __) =>
            {
                Reload();
                if (!UsesOnline) _ = RefreshLocalPackagesAsync();
            };
            BgiDataCenter.AccountDataChanged += OnSharedDataChanged;
            Unloaded += (_, __) => BgiDataCenter.AccountDataChanged -= OnSharedDataChanged;
        }

        private void OnSharedDataChanged(string userName)
        {
            Dispatcher.InvokeAsync(() => { if (IsVisible) Reload(); });
        }

        // ══════════ 加载 ══════════

        public void Reload()
        {
            try
            {
                UserList.Children.Clear();
                _rows.Clear();

                var cfg = App.Config;
                var map = cfg.Settings.BgiPaths ?? new Dictionary<string, string>();

                // 用户来源：配置里的账号 + BgiPaths 里有的
                var users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in AccountScope.Accounts(cfg))
                    if (!string.IsNullOrWhiteSpace(a.UserName)) users.Add(a.UserName.Trim());
                foreach (var k in map.Keys)
                    if (!string.IsNullOrWhiteSpace(k)) users.Add(k.Trim());

                if (users.Count == 0)
                {
                    SubText.Text = "还没有配置任何账号。点「+ 添加」手动加一个。";
                    return;
                }

                // ★ 统一解析一次：手动指定 > 正在运行 > 记忆。
                //   原来这里只读配置字典，所以"启动台日志能自动发现 BGI，
                //   但更新页看不到地址"（用户反馈）。现在两边同源。
                var resolved = BgiPathResolver.ResolveMany(users);

                foreach (var u in users.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    string path = "";
                    if (resolved.TryGetValue(u, out var info) && info != null)
                        path = info.Path;
                    else
                    {
                        try { map.TryGetValue(u, out path); } catch { }
                    }

                    var vm = new UserRowVm { UserName = u, BgiPath = path ?? "" };
                    _rows.Add(vm);
                    UserList.Children.Add(BuildRow(vm));
                }

                int valid = _rows.Count(r => r.BgiPath.Length > 0 && File.Exists(r.BgiPath));
                SubText.Text = "共 " + _rows.Count + " 个账号，其中 " + valid
                             + " 个已配置好路径 · 没路径的填好才能勾选";
            }
            catch (Exception ex)
            {
                SubText.Text = "加载失败：" + ex.Message;
            }
        }

        /// <summary>构建一行：复选框 + 账号名 + BGI 路径 + 改 / 移除。</summary>
        private Border BuildRow(UserRowVm vm)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 复选框
            // ★ 没有有效 BGI 路径的账号不能勾选（勾了也更新不了）
            bool hasValidPath = vm.BgiPath.Length > 0 && File.Exists(vm.BgiPath);

            var chk = new CheckBox
            {
                Style = (Style)FindResource("GlassCheckBox"),
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = hasValidPath,
                IsEnabled = hasValidPath,
                ToolTip = hasValidPath ? "勾选以更新" : "先设置 BGI 路径才能勾选"
            };
            if (!hasValidPath)
                chk.Opacity = 0.4;
            vm.Check = chk;
            grid.Children.Add(chk);

            // 账号名
            var name = new TextBlock
            {
                Text = vm.UserName,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiBrush.Get("TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            // 路径
            var pathText = new TextBlock
            {
                Text = vm.BgiPath.Length > 0
                    ? vm.BgiPath
                    : "未设置路径 · 点右边「改」填一下",
                FontSize = 10.5,
                FontFamily = new FontFamily("Consolas"),
                Foreground = (vm.BgiPath.Length > 0 && File.Exists(vm.BgiPath))
                    ? UiBrush.Get("TextMuted")
                    : UiBrush.Get("AmberBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 10, 0)
            };
            vm.PathText = pathText;
            Grid.SetColumn(pathText, 2);
            grid.Children.Add(pathText);

            // 按钮组
            var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var btnEdit = new Button
            {
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(10, 4, 10, 4),
                Content = new TextBlock { Text = "改", FontSize = 10.5 }
            };
            btnEdit.Click += (_, __) => EditPath(vm);
            btns.Children.Add(btnEdit);

            var btnDel = new Button
            {
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(6, 0, 0, 0),
                Content = new TextBlock { Text = "移除", FontSize = 10.5 }
            };
            btnDel.Click += (_, __) => RemoveUser(vm);
            btns.Children.Add(btnDel);

            Grid.SetColumn(btns, 3);
            grid.Children.Add(btns);

            var card = new Border
            {
                Padding = new Thickness(10, 7, 10, 7),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 0, 5),
                Child = grid
            };
            card.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
            return card;
        }

        // ══════════ 账号操作 ══════════

        private void EditPath(UserRowVm vm)
        {
            var dlg = new InputDialogWindow("BGI 路径",
                "填该账号的 BetterGI.exe 完整路径", vm.BgiPath);
            if (Window.GetWindow(this) is Window owner) dlg.Owner = owner;
            if (dlg.ShowDialog() != true) return;

            string p = (dlg.ResultText ?? "").Trim().Trim('"');
            if (p.Length == 0) return;

            // 允许填目录，自动补 exe
            if (Directory.Exists(p)) p = Path.Combine(p, "BetterGI.exe");

            if (!File.Exists(p))
            {
                if (!ThemedMessage.Confirm(Window.GetWindow(this), "路径不存在",
                        "这个路径找不到文件：\n\n" + p + "\n\n仍然保存吗？"))
                    return;
            }

            try
            {
                vm.BgiPath = p;
                // ★ 用户手填 = 明确指定，走 Pin（之后自动探测不再覆盖）
                BgiDataCenter.SetPath(vm.UserName, p);
                ConfigStore.Save(App.Config);

                // 路径改了 → 整页重载，让复选框状态跟着更新
                Reload();
            }
            catch (Exception ex)
            {
                ThemedMessage.Error(Window.GetWindow(this), "保存失败", ex.Message);
            }
        }

        private void AddUser_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new InputDialogWindow("添加账号",
                "填 Windows 账号名（例如 A1、A2）", "");
            if (Window.GetWindow(this) is Window owner) dlg.Owner = owner;
            if (dlg.ShowDialog() != true) return;

            string u = (dlg.ResultText ?? "").Trim();
            if (u.Length == 0) return;

            if (_rows.Any(r => string.Equals(r.UserName, u, StringComparison.OrdinalIgnoreCase)))
            {
                ThemedMessage.Info(Window.GetWindow(this), "已存在", "账号 " + u + " 已经在列表里了。");
                return;
            }

            var vm = new UserRowVm { UserName = u, BgiPath = "" };
            _rows.Add(vm);
            UserList.Children.Add(BuildRow(vm));
            SubText.Text = "共 " + _rows.Count + " 个账号";
        }

        private void RemoveUser(UserRowVm vm)
        {
            if (!ThemedMessage.Confirm(Window.GetWindow(this), "移除账号",
                    "从列表移除「" + vm.UserName + "」？\n\n（只删列表记录，不影响 BGI 文件）"))
                return;

            _rows.Remove(vm);
            Reload();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            // 只勾选路径有效的（无路径的复选框本身是禁用的）
            foreach (var r in _rows)
                if (r.Check != null && r.Check.IsEnabled) r.Check.IsChecked = true;
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var r in _rows) if (r.Check != null) r.Check.IsChecked = false;
        }

        // ══════════ 选包 ══════════

        private BgiOnlineSources.Source SelectedSource =>
            UpdateSourceBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse<BgiOnlineSources.Source>(tag, out var source) ? source : BgiOnlineSources.Source.Local;

        private bool UsesOnline => SelectedSource != BgiOnlineSources.Source.Local;

        private void RefreshUpdateSource()
        {
            LocalSourcePanel.Visibility = UsesOnline ? Visibility.Collapsed : Visibility.Visible;
            OnlineSourcePanel.Visibility = UsesOnline ? Visibility.Visible : Visibility.Collapsed;
            DeleteDownloadedPackageCheck.IsEnabled = !_running && UsesOnline;
            CoordinatorPanel.Visibility = SelectedSource == BgiOnlineSources.Source.Coordinator
                ? Visibility.Visible : Visibility.Collapsed;
            SubText.Text = UsesOnline ? "查询所选来源的云端版本，更新前请退出目标 BGI"
                                     : "把本地更新包解压覆盖到各账号的 BGI 目录";
            InvalidateOnlineVersion("");
            if (UsesOnline)
            {
                _localScanCts?.Cancel();
                _ = CheckOnlineSourceAsync();
            }
            else _ = RefreshLocalPackagesAsync();
        }

        private void InvalidateOnlineVersion(string status)
        {
            _lookupCts?.Cancel();
            _lookupCts = null;
            OnlinePackageBox.ItemsSource = null;
            OnlinePackageBox.Visibility = Visibility.Collapsed;
            OnlineVersionText.Text = status;
            UpdateButtonEnabled();
        }

        private async void CheckOnlineSource_Click(object sender, RoutedEventArgs e) => await CheckOnlineSourceAsync();

        private async Task CheckOnlineSourceAsync()
        {
            var source = SelectedSource;
            if (_running || source == BgiOnlineSources.Source.Local) return;
            InvalidateOnlineVersion("正在查询云端版本…");
            string coordinator = CoordinatorAddressBox.Text.Trim();
            if (source == BgiOnlineSources.Source.Coordinator && coordinator.Length == 0)
            {
                OnlineVersionText.Text = "请填写 BGI 协调服务器地址";
                return;
            }

            using var lookup = new CancellationTokenSource();
            _lookupCts = lookup;
            try
            {
                var versions = await BgiOnlineSources.GetCatalogAsync(source, coordinator, lookup.Token);
                if (_lookupCts != lookup) return;
                if (versions.Count == 0)
                {
                    OnlineVersionText.Text = "此来源暂无可下载的云端版本";
                    return;
                }
                OnlineVersionText.Text = "最新云端版本 " + versions[0].Version;
                OnlinePackageBox.ItemsSource = versions;
                OnlinePackageBox.SelectedIndex = 0;
                OnlinePackageBox.Visibility = Visibility.Visible;
                if (source == BgiOnlineSources.Source.Coordinator && App.Config.Settings.BgiCoordinatorUrl != coordinator)
                {
                    App.Config.Settings.BgiCoordinatorUrl = coordinator;
                    ConfigStore.Save(App.Config);
                }
            }
            catch (OperationCanceledException)
            {
                if (_lookupCts == lookup) OnlineVersionText.Text = "查询云端版本超时，可刷新重试";
            }
            catch (HttpRequestException ex)
            {
                if (_lookupCts != lookup) return;
                OnlineVersionText.Text = ex.StatusCode switch
                {
                    System.Net.HttpStatusCode.Forbidden when ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) => "GitHub 查询次数暂时达到上限，请稍后刷新",
                    System.Net.HttpStatusCode.Forbidden => "此更新源拒绝访问（403）",
                    System.Net.HttpStatusCode.NotFound => "此更新源没有提供版本清单或安装包（404）",
                    { } status => $"查询云端版本失败（HTTP {(int)status}）",
                    _ => "无法连接此更新源，可刷新重试"
                };
            }
            catch (Exception ex)
            {
                if (_lookupCts == lookup) OnlineVersionText.Text = "查询失败 · " + ex.Message;
            }
            finally
            {
                if (_lookupCts == lookup)
                {
                    _lookupCts = null;
                    UpdateButtonEnabled();
                }
            }
        }

        private string PackageDirectory => string.IsNullOrWhiteSpace(App.Config.Settings.BgiPackageDirectory)
            ? BgiLocalPackages.DefaultDirectory : App.Config.Settings.BgiPackageDirectory;

        private void UpdatePackageDirectoryLabel()
        {
            PackageDirectoryLabel.Text = "安装包目录 " + PackageDirectory;
            PackageDirectoryLabel.ToolTip = PackageDirectory;
        }

        private void DeleteDownloadedPackage_Changed(object sender, RoutedEventArgs e)
        {
            App.Config.Settings.BgiDeleteDownloadedPackageAfterSuccess = DeleteDownloadedPackageCheck.IsChecked == true;
            ConfigStore.Save(App.Config);
        }

        private void BrowsePackageDirectory_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;
            var dlg = new OpenFolderDialog { Title = "选择 BGI 安装包目录", InitialDirectory = PackageDirectory };
            if (dlg.ShowDialog() != true) return;
            App.Config.Settings.BgiPackageDirectory = dlg.FolderName;
            ConfigStore.Save(App.Config);
            UpdatePackageDirectoryLabel();
            if (!UsesOnline) _ = RefreshLocalPackagesAsync();
        }

        private void UpdateButtonEnabled() => BtnUpdate.IsEnabled = !_running && (UsesOnline
            ? OnlinePackageBox.SelectedItem is BgiOnlineSources.Package
            : _localScanCts == null && LocalPackageBox.SelectedItem is LocalPackage);

        private async void RefreshLocalPackages_Click(object sender, RoutedEventArgs e) => await RefreshLocalPackagesAsync();

        private async Task RefreshLocalPackagesAsync(string? preferred = null)
        {
            if (UsesOnline) return;
            preferred ??= (LocalPackageBox.SelectedItem as LocalPackage)?.Path;
            _localScanCts?.Cancel();
            using var scan = new CancellationTokenSource();
            _localScanCts = scan;
            string directory = PackageDirectory;
            var pinned = App.Config.Settings.BgiPickedPackagePaths?.ToArray() ?? [];
            LocalPackageStatus.Text = "正在查找本地安装包…";
            UpdateButtonEnabled();
            try
            {
                var paths = await Task.Run(() => BgiLocalPackages.Scan(directory, pinned, scan.Token), scan.Token);
                if (_localScanCts != scan || UsesOnline) return;
                var packages = paths.Select(path => new LocalPackage(path)).ToArray();
                LocalPackageBox.ItemsSource = packages;
                LocalPackageBox.SelectedItem = packages.FirstOrDefault(p =>
                    string.Equals(p.Path, preferred, StringComparison.OrdinalIgnoreCase)) ?? packages.FirstOrDefault();
                LocalPackageStatus.Text = packages.Length == 0 ? "这里还没有 BGI 安装包，可以选择磁盘上的压缩包。" : "";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                if (_localScanCts == scan && !UsesOnline)
                {
                    LocalPackageBox.ItemsSource = null;
                    LocalPackageStatus.Text = "无法读取安装包目录，可以更改目录或选择文件。";
                }
            }
            finally
            {
                if (_localScanCts == scan) _localScanCts = null;
                UpdateButtonEnabled();
            }
        }

        private async void PickZip_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;
            var dlg = new OpenFileDialog
            {
                Title = "选择 BGI 安装包",
                InitialDirectory = PackageDirectory,
                Filter = "支持的压缩包 (*.zip;*.7z;*.rar;*.tar;*.gz)|*.zip;*.7z;*.rar;*.tar;*.gz",
                CheckFileExists = true
            };
            if (dlg.ShowDialog() != true) return;
            var picked = App.Config.Settings.BgiPickedPackagePaths ??= new List<string>();
            if (!picked.Contains(dlg.FileName, StringComparer.OrdinalIgnoreCase))
            {
                picked.Insert(0, dlg.FileName);
                ConfigStore.Save(App.Config);
            }
            await RefreshLocalPackagesAsync(dlg.FileName);
        }

        // ══════════ 开始更新 ══════════

        private async void StartUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;

            bool usesOnline = UsesOnline;
            var release = OnlinePackageBox.SelectedItem as BgiOnlineSources.Package;
            if (usesOnline && release == null) return;
            string zip = (LocalPackageBox.SelectedItem as LocalPackage)?.Path ?? "";
            if (!usesOnline && (zip.Length == 0 || !File.Exists(zip)))
            {
                ThemedMessage.Info(Window.GetWindow(this), "缺少更新包", "请先选择一个更新包（zip / 7z / rar）。");
                return;
            }

            var targets = _rows
                .Where(r => r.Check?.IsChecked == true)
                .Select(r => (r.UserName, r.BgiPath))
                .ToList();

            if (targets.Count == 0)
            {
                ThemedMessage.Info(Window.GetWindow(this), "没选账号", "请至少勾选一个要更新的账号。");
                return;
            }

            // 检查路径
            var bad = targets.Where(t => string.IsNullOrWhiteSpace(t.BgiPath) || !File.Exists(t.BgiPath)).ToList();
            if (bad.Count > 0)
            {
                string names = string.Join("、", bad.Select(b => b.UserName));
                if (!ThemedMessage.Confirm(Window.GetWindow(this), "路径有问题",
                        "这些账号没有有效的 BGI 路径：\n\n" + names
                        + "\n\n跳过它们继续更新其他账号吗？"))
                    return;
                targets = targets.Except(bad).ToList();
                if (targets.Count == 0) return;
            }

            using var cancellation = new CancellationTokenSource();
            _cts = cancellation;
            _logLines.Clear();
            if (LogText != null) LogText.Text = "";
            SetRunning(true);
            AppendLog("═══ 开始更新 ═══");
            AppendLog(usesOnline ? "更新源：" + BgiOnlineSources.Label(SelectedSource) : "更新包：" + Path.GetFileName(zip));
            AppendLog("目标账号：" + string.Join("、", targets.Select(t => t.UserName)));
            AppendLog("");

            bool backupUser = BackupUserCheck?.IsChecked == true;

            // ★ 改为批量更新：解压一次，并行复制
            //   （原来每个账号各自解压同一个包，CPU 满载、风扇狂转）
            var list = targets.Select(t => new BgiUpdater.Target
            {
                UserName = t.UserName,
                BgiExePath = t.BgiPath
            }).ToList();

            var progress = new Progress<BgiUpdater.Progress>(p =>
            {
                if (p.Percent >= 0) SetProgress(p.Percent, p.Text);
                if (p.IsLog) AppendLog(p.Text);
                if (p.Text.Length > 0) SetStatus(p.Text);
            });

            DirectoryInfo? downloadDirectory = null;
            BgiLocalPackages.DownloadedPackage? savedDownload = null;
            string packageDirectory = PackageDirectory;
            bool deleteDownloaded = DeleteDownloadedPackageCheck.IsChecked == true;
            try
            {
                if (usesOnline && release != null)
                {
                    string accounts = string.Join("、", targets.Select(target => target.UserName));
                    if (!ThemedMessage.Confirm(Window.GetWindow(this), "确认应用云端版本",
                            $"下载并应用 {release.Version} 到 {accounts} 的 BetterGI 目录？请先退出目标 BGI。"))
                    {
                        SetStatus("已取消");
                        return;
                    }

                    await Task.Run(() => BgiUpdater.EnsureUpdateTargetsClosed(list), cancellation.Token);
                    downloadDirectory = Directory.CreateTempSubdirectory("SoulLinkBgi-");
                    AppendLog("下载：" + release.Name);
                    SetProgress(0, "正在下载并校验便携包…");
                    zip = await BgiOnlineSources.DownloadAsync(release, downloadDirectory.FullName,
                        new Progress<int>(percent => SetProgress(percent, "下载并校验便携包 · " + percent + "%")),
                        cancellation.Token);
                    savedDownload = await BgiLocalPackages.SaveDownloadedAsync(zip, packageDirectory,
                        release.Name, cancellation.Token);
                    zip = savedDownload.Path;
                    AppendLog("安装包已保存：" + zip);
                }

                IProgress<string> log = new Progress<string>(AppendLog);
                var results = await BgiUpdater.UpdateBatchAsync(list, zip, backupUser,
                    log.Report, progress, cancellation.Token, requireClosedBgi: usesOnline);

                int ok = results.Count(r => r.Ok);
                int fail = results.Count - ok;
                SetProgress(100, cancellation.IsCancellationRequested ? "已停止" : "完成");
                SetStatus("成功 " + ok + " 个，失败 " + fail + " 个");
                AppendLog("═══ 结束：成功 " + ok + "，失败 " + fail + " ═══");

                if (deleteDownloaded && savedDownload != null && ok == list.Count && fail == 0 &&
                    !cancellation.IsCancellationRequested)
                {
                    try
                    {
                        bool removed = await Task.Run(savedDownload.DeleteIfUnchanged);
                        AppendLog(removed ? "已清理本次下载的安装包。" : "本次下载文件已不在原位或发生变化。");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        AppendLog("无法清理本次安装包：" + ex.Message);
                    }
                }

                if (fail == 0 && ok > 0)
                    ThemedMessage.Info(Window.GetWindow(this), "更新完成",
                        ok + " 个账号更新成功。可以重新启动 BGI 了。");
                else if (fail > 0)
                    ThemedMessage.Info(Window.GetWindow(this), "更新结束",
                        "成功 " + ok + " 个，失败 " + fail + " 个。\n\n详情看右侧日志。");
            }
            catch (OperationCanceledException)
            {
                string message = cancellation.IsCancellationRequested ? "已停止" : "连接更新源或下载超时";
                SetProgress(0, message);
                SetStatus(message);
                AppendLog(message);
            }
            catch (Exception ex)
            {
                SetProgress(0, "更新失败");
                SetStatus("更新失败");
                AppendLog("✕ 失败：" + ex.Message);
                ThemedMessage.Error(Window.GetWindow(this), "BGI 更新失败", ex.Message);
            }
            finally
            {
                if (downloadDirectory != null)
                {
                    try { downloadDirectory.Delete(recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                _cts = null;
                SetRunning(false);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            try { _cts?.Cancel(); } catch { }
            AppendLog("⏹ 已请求停止…");
            SetStatus("正在停止…");
        }

        /// <summary>打开备份目录。</summary>
        private void OpenBackup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = BgiUpdater.BackupRoot;
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

        // ══════════ 状态 ══════════

        private void SetRunning(bool running)
        {
            _running = running;
            UpdateButtonEnabled();
            BtnCancel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            UpdateSourceBox.IsEnabled = !running;
            LocalSourcePanel.IsEnabled = !running;
            OnlineSourcePanel.IsEnabled = !running;
            BtnBrowsePackageDirectory.IsEnabled = !running;
            DeleteDownloadedPackageCheck.IsEnabled = !running && UsesOnline;
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

        /// <summary>
        /// 追加一行日志。
        /// ★ 只保留最近 500 行，避免日志越积越多把界面拖慢
        ///   （2244 个文件的解压日志会很长）。
        /// </summary>
        private void AppendLog(string line)
        {
            try
            {
                if (LogText == null) return;

                _logLines.Add(line);
                if (_logLines.Count > 500)
                    _logLines.RemoveRange(0, _logLines.Count - 500);

                LogText.Text = string.Join("\n", _logLines);

                // 滚动到底（用 Dispatcher 避免回调里重入）
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { LogScroll?.ScrollToEnd(); } catch { }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { }
        }
    }
}
