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
using System.Windows.Input;
using System.Windows.Media;
using SoulLink.Models;
using SoulLink.Services;
// ★ P0-2：XAML 字段 EngineState（TextBlock）会遮蔽同名枚举 EngineState，
//   本文件里所有 5 态判断统一用别名 EngState 引用枚举，避免二义性编译错误。
using EngState = SoulLink.Services.EngineState;

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

            // ★ 网页访问是**全局**功能（不只录像），所以放应用设置
            Loaded += (_, __) => LoadWebSettings();
        }

        // ══════════ 网页访问 ══════════

        private void LoadWebSettings()
        {
            try
            {
                _loading = true;
                int port = App.Config.Settings.WebPort;
                WebPortBox.Text = port > 0 ? port.ToString() : "";
                WebPasswordBox.Text = App.Config.Settings.WebPassword ?? "";
                _loading = false;
                UpdateWebStatus();
            }
            catch (Exception ex) { App.AppendErrorLog("LoadWebSettings", ex); _loading = false; }
        }

        private void WebPortBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            try
            {
                string txt = (WebPortBox.Text ?? "").Trim();
                if (txt.Length == 0)
                {
                    App.Config.Settings.WebPort = 0;
                    ConfigStore.Save(App.Config);
                    // ★ 3.3.45：改设置即时生效 —— 由 WebHost 统一停/起
                    //   （它会同时处理回环端口与独立服务子进程）。
                    Services.WebHost.Apply(0, App.Config.Settings.WebInternalPort, App.Config.Settings.WebPassword);
                    UpdateWebStatus();
                    return;
                }
                if (!int.TryParse(txt, out int port)) { UpdateWebStatus(); return; }
                if (port < 1024 || port > 65535)
                {
                    if (WebStatus != null)
                        WebStatus.Text = "端口需在 1024~65535 之间（当前填的是 " + port + "）";
                    return;
                }
                App.Config.Settings.WebPort = port;
                ConfigStore.Save(App.Config);
                // ★ 3.3.45：端口变更 → 重新分配回环内部端口并重启独立服务。
                Services.WebHost.Apply(port, App.Config.Settings.WebInternalPort, App.Config.Settings.WebPassword);
                UpdateWebStatus();
            }
            catch (Exception ex) { App.AppendErrorLog("WebPortBox_TextChanged", ex); }
        }

        private void WebPasswordBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            try
            {
                string newPw = (WebPasswordBox.Text ?? "").Trim();
                string oldPw = App.Config.Settings.WebPassword ?? "";
                App.Config.Settings.WebPassword = newPw;
                // ★ 改了口令就作废旧 Cookie（否则改了密码但旧浏览器还免密，等于没改）
                if (!string.Equals(newPw, oldPw, StringComparison.Ordinal))
                    App.Config.Settings.WebAuthToken = Guid.NewGuid().ToString("N");
                ConfigStore.Save(App.Config);
                // ★ 3.3.45：口令变更要让独立服务重新读取（它按口令校验远程访问）
                if (!string.Equals(newPw, oldPw, StringComparison.Ordinal)
                    && App.Config.Settings.WebPort > 0)
                    Services.WebHost.Apply(App.Config.Settings.WebPort,
                        App.Config.Settings.WebInternalPort, newPw);
                UpdateWebStatus();
            }
            catch (Exception ex) { App.AppendErrorLog("WebPasswordBox_TextChanged", ex); }
        }

        private void WebOpen_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int port = App.Config.Settings.WebPort;
                if (port <= 0)
                {
                    if (WebStatus != null) WebStatus.Text = "先填一个端口号（如 8080）再打开。";
                    return;
                }
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("http://127.0.0.1:" + port + "/")
                    { UseShellExecute = true });
            }
            catch (Exception ex) { App.AppendErrorLog("WebOpen_Click", ex); }
        }

        private void UpdateWebStatus()
        {
            try
            {
                if (WebStatus == null) return;
                int port = App.Config.Settings.WebPort;
                // ★ 3.3.45：对外端口由独立服务监听，回环端口是主程序自己的
                int loopback = Services.WebHost.InternalPort;
                if (port <= 0)
                {
                    WebStatus.Text = "未开启。填一个端口号（如 8080），然后用 http://本机IP:端口 访问。";
                    return;
                }
                if (loopback > 0)
                {
                    // ★ 3.3.45：不再提示「未设口令 = 任何人可访问」——
                    //   独立服务对非回环访问**强制口令**，没设口令时会自动生成令牌。
                    string auth = string.IsNullOrWhiteSpace(App.Config.Settings.WebPassword)
                        ? "　远程访问需输入启动时生成的令牌（或在此设口令）"
                        : "　已启用口令";
                    string svc = Services.WebHost.ServiceRunning ? "" : "　⚠ 独立服务未运行（仅本机可访问）";
                    WebStatus.Text = "已开启：" + Services.WebServer.LanUrl(port) + auth + svc;
                }
                else
                {
                    WebStatus.Text = "端口 " + port + " 未能监听，可能被占用或权限不足，换个端口试试。";
                }
            }
            catch { }
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

                LogBus.Ok("服务器地址已更新");
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
            try
            {
                int tier = Math.Clamp(App.Config.Settings.EffectTier, 0, 2);
                EffectTierCombo.SelectedIndex = tier;
                EffectTierHint.Text = DescribeTier(tier);
            }
            catch { }
            RemoteCheck.IsChecked = App.Config.Settings.AllowRemoteInvoke;
            FallbackCheck.IsChecked = App.Config.Settings.AllowCurrentSessionFallback;
            OnlyAutomationCheck.IsChecked = App.Config.Settings.OnlyAutomationAccounts;
            ConfigPathText.Text = ConfigStore.ConfigPath;
            ShowDataDirInfo();

            // 服务器地址（联机大厅 / 路线更新 / 客户端更新共用）
            try
            {
                Services.LobbyApi.LoadFromConfig();
                // ★ 只显示用户配置过的地址；未配置就是空（不会把任何默认地址印出来）。
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
        // ★ 2.17.45 语义变更（用户要求）：
        //   只有**已绑定**（BgiPinnedUsers 里存在）的账号才会被读取日志。
        //   未绑定 = 零 IO，界面显示"未绑定"，日志类条件也不会列出该账号。
        //   改这个行为的原因：旧版 Resolve(persist:true) 会自动把探测到的路径
        //   写进 BgiPaths，"用户填没填"根本无法判断（实测 A1/A4 全被污染）。

        private void RebuildBgiPathList()
        {
            if (BgiPathList == null) return;
            BgiPathList.Children.Clear();

            if (AccountScope.Accounts(App.Config).Count == 0)
            {
                BgiPathList.Children.Add(new TextBlock
                {
                    Text = "尚未配置任何 RDP 账号",
                    Style = (Style)FindResource("Caption")
                });
                if (BgiPathStatus != null) BgiPathStatus.Text = "";
                return;
            }

            int unbound = 0, bound = 0;
            foreach (var a in AccountScope.Accounts(App.Config))
            {
                bool pinned = BgiPathResolver.IsPinned(a.UserName);
                if (pinned) bound++; else unbound++;
                BgiPathList.Children.Add(BuildBgiRow(a, pinned));
            }

            if (BgiPathStatus != null)
                BgiPathStatus.Text = "共 " + AccountScope.Accounts(App.Config).Count + " 个账号："
                    + bound + " 个已绑定（会读取日志） · " + unbound + " 个未绑定（不读取任何文件）"
                    + "。未绑定账号在日志类条件里也不会被列出。";
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
            // ★ 未绑定但配置里还残留旧路径（2.17.44 的 Resolve 自动写入的）：
            //   显示成灰字提示，不要让它看起来"已经生效"。
            var box = new TextBox
            {
                Text = saved ?? "",
                Foreground = pinned ? (Brush)FindResource("TextPrimary")
                                    : (Brush)FindResource("TextMuted"),
                Height = 30,
                Style = (Style)FindResource("InlineTextBox"),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 11,
                FontFamily = (FontFamily)FindResource("MonoFont"),
                ToolTip = pinned
                    ? "已绑定：会读取该路径下的 BGI 日志"
                    : "未绑定：不会读取任何文件。输入路径后回车即可绑定",
                Tag = a.UserName,
                Margin = new Thickness(8, 0, 0, 0)
            };
            // ★ 输入框现在**真的会提交**：回车或失焦即绑定/解绑。
            //   改造前这个框是死的（没有任何 LostFocus/KeyDown 处理），
            //   用户打了路径也不生效，只能靠"从进程取"按钮——这也是
            //   BgiPinnedUsers 一直为空的原因。
            box.KeyDown += (_, e2) =>
            {
                if (e2.Key != Key.Enter) return;
                CommitBgiPath(a.UserName, box.Text);
                e2.Handled = true;
            };
            box.LostFocus += (_, __) => CommitBgiPath(a.UserName, box.Text);

            Grid.SetColumn(box, 1);
            grid.Children.Add(box);

            // 状态：只有"已绑定/未绑定"两种，因为**只有绑定的账号才会被读日志**。
            // ★ 原来的"手动/自动/记忆"三态让用户以为自动识别也能读日志，
            //   而实际门禁只看 BgiPinnedUsers（见 BgiPathResolver.GetBoundPath）。
            var state = new TextBlock
            {
                Text = pinned ? "已绑定" : "未绑定",
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Foreground = pinned
                    ? new SolidColorBrush(Color.FromRgb(0x7C, 0x5C, 0xFF))
                    : new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0x7A))
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

            // ★ 2.17.45：去掉了「自动」按钮。
            //
            //   那个按钮原本是"清除手动指定，恢复自动识别"，但改造后
            //   **不存在"自动识别"这条路径** —— 未绑定就是零 IO、不读日志。
            //   留着它会让用户以为点一下就会自动恢复，实际是变成未绑定。
            //   现在只有两种明确动作：「从进程取」（绑定）和输入框清空（解绑）。
            if (pinned)
            {
                var btnClear = new Button
                {
                    Content = "解绑",
                    FontSize = 10,
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(4, 0, 0, 0),
                    Style = (Style)FindResource("GhostButton"),
                    Tag = a.UserName,
                    ToolTip = "解除绑定：不再读取该账号的任何日志"
                };
                btnClear.Click += BgiClear_Click;
                ops.Children.Add(btnClear);
            }

            Grid.SetColumn(ops, 3);
            grid.Children.Add(ops);

            return grid;
        }

        /// <summary>
        /// 提交手动输入的 BGI 路径：空白 = 解绑；有效路径 = 绑定。
        /// </summary>
        private void CommitBgiPath(string user, string text)
        {
            try
            {
                string v = (text ?? "").Trim();
                if (v.Length == 0)
                {
                    BgiPathResolver.Unpin(user);
                    ConfigStore.Save(App.Config);
                    BgiDataCenter.Invalidate(user);
                    RebuildBgiPathList();
                    if (BgiPathStatus != null) BgiPathStatus.Text = user + "：已解绑（不再读取该账号的日志）";
                    return;
                }
                if (!System.IO.File.Exists(v))
                {
                    if (BgiPathStatus != null) BgiPathStatus.Text = user + "：路径不存在 → " + v;
                    return;
                }
                if (BgiPathResolver.IsPinned(user)
                    && string.Equals(BgiPathResolver.GetBoundPath(user), BgiPathResolver.Normalize(v), StringComparison.OrdinalIgnoreCase))
                    return;   // 没变化，避免每次失焦都写盘

                BgiDataCenter.SetPath(user, v);   // 内部会 Pin + 保存 + 失效缓存
                RebuildBgiPathList();
                if (BgiPathStatus != null) BgiPathStatus.Text = user + "：已绑定 → " + v;
            }
            catch (Exception ex)
            {
                if (BgiPathStatus != null) BgiPathStatus.Text = user + "：绑定失败：" + ex.Message;
            }
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

                BgiDataCenter.SetPath(user, info.Path);
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

        /// <summary>解绑：不再读取该账号的任何日志。</summary>
        private void BgiClear_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string user) return;
            try
            {
                BgiPathResolver.Unpin(user);
                try { App.Config.Settings.BgiPaths.Remove(user); } catch { }
                ConfigStore.Save(App.Config);
                BgiDataCenter.Invalidate(user);
                RebuildBgiPathList();
                if (BgiPathStatus != null) BgiPathStatus.Text = user + "：已解绑（不再读取该账号的日志）";
            }
            catch (Exception ex)
            {
                if (BgiPathStatus != null) BgiPathStatus.Text = "操作失败：" + ex.Message;
            }
        }

        private void RefreshAccounts()
        {
            _accounts.Clear();
            foreach (var a in AccountScope.Accounts(App.Config))
                _accounts.Add(new AccountVm(a));
        }

        /// <summary>兜底：手动点「修复注入代理」触发 AgentSync 修复。</summary>
        private async void RepairAgent_Click(object sender, RoutedEventArgs e)
        {
            if (BtnRepairAgent != null) BtnRepairAgent.IsEnabled = false;
            EngineState.Text = "正在更新引擎组件…";
            try
            {
                var (ok, msg) = await AgentSync.RepairAsync().ConfigureAwait(true);
                await DetectAsync();
                EnginePath.Text = msg + " · " + EnginePath.Text;
                if (ok) LogBus.Ok("引擎组件", msg);
                else LogBus.Info("引擎组件", msg);
            }
            catch (Exception ex)
            {
                EngineState.Text = "引擎组件更新失败";
                EnginePath.Text = ex.Message;
                EngineDot.Color = Color.FromRgb(0xFF, 0x6B, 0x6B);
            }
            finally { if (BtnRepairAgent != null) BtnRepairAgent.IsEnabled = true; }
        }

        /// <summary>
        /// 检测投送引擎状态。
        ///
        /// 现在的引擎是 **SYSTEM 计划任务通道**（schtasks /ru SYSTEM → CreateProcessAsUser），
        /// 不再依赖任何外部工具，唯一的硬性条件是「以管理员身份运行」。
        ///
        /// ★ 2026-11-24 P0-2：Broker 分支的「引擎可用/绿色」必须以 AgentSync.Inspect 的
        ///   统一判据为准（服务在跑 + agent 存在 + 版本通过），health 探测不再单独放绿。
        ///   版本三方行/Tooltip 数据同样取自 Inspect（含 LastVerdict 依据来源）。
        /// </summary>
        private async Task DetectAsync()
        {
            // ★ 三选一：手动代理优先于 Broker（手动模式下不投送任何东西）
            ApplyDeliveryModeToUi();
            EngineState.Text = "检测中…";
            // （字段名与枚举同名：5 态判断用 EngState 别名，控件操作用 EngineState 字段）
            bool elevated = SystemLauncher.IsElevated;
            if (App.Config.Settings.UseBrokerChannel)
            {
                var health = elevated ? await BrokerProtocol.SendAsync(new BrokerProtocol.Request { Command = "health" }, 2500) : null;

                // ★ 版本不一致时：**照常可用，但要明说**。
                //   用户实测：服务跑 2.11.0、客户端 2.17.11，协议都是 v1，
                //   所以这里原本显示"服务可用"，可投送全部失败，非常误导。
                //   用户选择「只提示、不拦截」。
                string? versionNote = health?.Ok == true
                    ? BrokerProtocol.DescribeAppVersionMismatch(health)
                    : null;
                if (versionNote != null)
                    LogBus.Warn("Broker 版本不一致", versionNote);

                // ★ P0-2：5 态由 AgentSync.Inspect 统一给出（t1 收敛的单一判据）：
                //   服务未安装→NotInstalled；非 Running→NotRunning；agent 缺失/判据不过→
                //   AgentNeedsUpdate；全过→Ready。绿灯只有 Ready 一条路。
                //   Inspect 纯读取（ServiceController + 文件元数据），放后台线程不卡 UI。
                string agentDetail = "";
                string verLine = "";
                EngState state = await Task.Run(() =>
                {
                    EngState s = AgentSync.Inspect(out agentDetail, out verLine);
                    return s;
                });

                string healthNote = health == null ? "当前程序未以管理员身份运行"
                    : !health.Ok ? health.Message : versionNote ?? "服务健康；账号通道仍需单独投送";

                // health 探测失败（管道/超时）而服务管理器说 Running → 引擎异常（可能是
                // 服务假死），这是 Inspect 覆盖不到的维度，只有这里能发现。
                EngState finalState = state;
                if (health?.Ok == false && state == EngState.Ready)
                    finalState = EngState.Error;

                EngineDot.Color = finalState switch
                {
                    EngState.Ready => Color.FromRgb(0x41, 0xF0, 0xC0),
                    EngState.NotInstalled => Color.FromRgb(0xFF, 0xC4, 0x6B),
                    EngState.NotRunning => Color.FromRgb(0xFF, 0xC4, 0x6B),
                    EngState.AgentNeedsUpdate or EngState.ServiceNeedsUpdate or EngState.ComponentsNeedUpdate => Color.FromRgb(0xFF, 0x9B, 0x4D),
                    EngState.ChannelDown => Color.FromRgb(0xFF, 0xC4, 0x6B),   // ★ P0-A：通道断非绿
                    _ => Color.FromRgb(0xFF, 0x6B, 0x6B),
                };

                EngineState.Text = finalState switch
                {
                    EngState.Ready => "引擎就绪",
                    EngState.NotInstalled => "引擎待安装（Broker 未安装）",
                    EngState.NotRunning => "引擎未运行（服务已安装未启动）",
                    EngState.AgentNeedsUpdate => "注入代理待更新",
                    EngState.ServiceNeedsUpdate => "Windows 服务待更新",
                    EngState.ComponentsNeedUpdate => "服务与代理待更新",
                    EngState.ChannelDown => "引擎运行中 · 通道未建立",
                    _ => "引擎异常",
                };

                EnginePath.Text = finalState == EngState.Ready
                    ? (versionNote ?? healthNote)
                    : (agentDetail.Length > 0 ? agentDetail + " · " + healthNote : healthNote);
                BtnRepairAgent.Visibility = AgentSync.RequiresUpdate(state) ? Visibility.Visible : Visibility.Collapsed;

                // ★ P0-2：三方版本行 + Tooltip（数据源 = AgentSync，不另算口径）。
                RenderEngineVerFromVerdict(verLine);

                StatusChanged?.Invoke(finalState == EngState.Ready, EnginePath.Text);
                return;
            }
            var (ok, detail) = await Task.Run(() =>
            {
                SystemLauncher.EnablePrivileges();
                bool elevated = SystemLauncher.IsElevated;
                string msg = elevated
                    ? (App.Config.Settings.UseBrokerChannel ? "SYSTEM Broker · 管理员代理（测试）" : "SYSTEM 计划任务通道 · 计划任务 " + (CanUseSchtasks() ? "可用" : "受限"))
                    : "需要管理员权限";
                return (elevated, msg);
            });

            BtnRepairAgent.Visibility = Visibility.Collapsed;
            EngineDot.Color = ok ? Color.FromRgb(0x41, 0xF0, 0xC0) : Color.FromRgb(0xFF, 0xC4, 0x6B);
            EngineState.Text = ok ? "可用" : "需要管理员权限";
            EnginePath.Text = detail;
            StatusChanged?.Invoke(ok, detail);
        }

        /// <summary>
        /// ★ 2026-11-24 P0-2：把三方版本写到 EngineVer 行 + EngineState Tooltip。
        ///   正文用 Inspect 返回的三方版本行（「当前程序 X · 服务 Y · 注入代理 Z · 判据 …（依据…）」，
        ///   它由 LastVerdict 同源数据拼成，且在服务未安装/未运行的早退分支同样新鲜）；
        ///   判据/依据补充段读 AgentSync.LastVerdict。
        ///   ★ 硬约束：本方法不自行读 FileVersionInfo、不做任何版本比较（判据唯一来源 = AgentSync）。
        /// </summary>
        private void RenderEngineVerFromVerdict(string inspectTooltip)
        {
            try
            {
                var v = Services.AgentSync.LastVerdict;
                string line = string.IsNullOrWhiteSpace(inspectTooltip)
                    ? "当前程序 " + (Services.AgentSync.SelfVersion.Length > 0 ? Services.AgentSync.SelfVersion : "?")
                      + " · 服务 缺失 · 注入代理 缺失"
                    : inspectTooltip;
                // ★ LastVerdict 只在判据分支（服务 Running 且 agent 存在）更新；
                //   早退分支（未安装/未运行）它保持空 —— 此时 Inspect 的 tooltip 正文
                //   已含「判据 …（依据…）」结论，不再追加空的 verdictNote 免得误导。
                string verdictNote = v.Source.Length > 0
                    ? "判据 " + (v.Passed ? "通过" : "不通过")
                      + "（依据：" + v.Source + "）"
                    : "";
                if (EngineVer != null)
                {
                    EngineVer.Text = line;
                    if (verdictNote.Length > 0) EngineVer.ToolTip = verdictNote;
                }
                if (EngineState != null && verdictNote.Length > 0)
                    EngineState.ToolTip = line + "\n" + verdictNote;
            }
            catch { }
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

        // ── 投送方式（三选一）────────────────────────────────────────────────
        //
        // ★ 为什么从两个 CheckBox 改成下拉单选：
        //   原来「Broker」和「手动代理」是两个独立复选框，可以同时勾上，
        //   但它们的语义是互斥的（一个自动投送、一个拒绝自动投送）。
        //   用户实测反馈：「现在能同时勾选多种」。
        //   下拉单选从结构上杜绝这种非法组合。

        /// <summary>当前投送方式：0=SYSTEM 计划任务 1=Broker 2=手动代理。</summary>
        private int CurrentDeliveryMode()
        {
            if (App.Config.Settings.ManualAgentMode) return 2;
            if (App.Config.Settings.UseBrokerChannel) return 1;
            return 0;
        }

        /// <summary>把配置反映到界面（不触发保存逻辑）。</summary>
        private void ApplyDeliveryModeToUi()
        {
            if (DeliveryModeCombo == null) return;
            int mode = CurrentDeliveryMode();
            if (DeliveryModeCombo.SelectedIndex != mode)
                DeliveryModeCombo.SelectedIndex = mode;
            UpdateDeliveryPanels(mode);
        }

        /// <summary>按模式显示对应的操作按钮与说明。</summary>
        private void UpdateDeliveryPanels(int mode)
        {
            if (BrokerPanel != null) BrokerPanel.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (ManualPanel != null) ManualPanel.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;

            if (DeliveryModeHint == null) return;
            DeliveryModeHint.Text = mode switch
            {
                1 => "Broker 通道需要先用「安装 / 管理服务」装好后台服务。所有跨会话启动的程序强制管理员权限，不降级。",
                2 => "手动模式：不自动投送、清理自动代理，只在本机准备管道等待。随后在目标账号桌面里运行 SoulLink.Agent.exe 即可接入。",
                _ => "默认通道：用 SYSTEM 计划任务启动代理。所有跨会话启动的程序和管理员代理均强制管理员权限；无法取得管理员令牌时拒绝执行，不降级。"
            };
        }

        private async void DeliveryMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (DeliveryModeCombo == null || DeliveryModeCombo.SelectedIndex < 0) return;
                int wanted = DeliveryModeCombo.SelectedIndex;
                int current = CurrentDeliveryMode();
                UpdateDeliveryPanels(wanted);
                if (wanted == current) return;   // 初始化赋值不重复处理

                bool wantManual = wanted == 2;
                bool wantBroker = wanted == 1;

                // ① 先落盘两个互斥开关
                App.Config.Settings.UseBrokerChannel = wantBroker;
                ConfigStore.Save(App.Config);

                // ② 手动模式的启停要走它自己的流程（会清理/恢复自动代理）
                string? message = null;
                if (wantManual != App.Config.Settings.ManualAgentMode)
                    message = await Services.ManualAgentMode.SetEnabledAsync(wantManual);

                Services.LogBus.Info("投送方式已切换",
                    "现在是：" + DeliveryModeCombo.Text + (message != null ? "\n" + message : ""));

                if (DeliveryModeHint != null && message != null)
                    DeliveryModeHint.Text = message.Replace("\n\n", "  ");

                // ③ 切回自动模式时立刻重新检测，让状态栏反映真实可用性
                await DetectAsync();
            }
            catch (Exception ex)
            {
                Services.LogBus.Error("切换投送方式失败", ex.Message);
                ThemedMessage.Info(Window.GetWindow(this), "切换失败", ex.Message);
            }
        }

        private void BrokerManage_Click(object sender, RoutedEventArgs e) => new BrokerWindow { Owner = Window.GetWindow(this) }.Show();

        /// <summary>界面动效档位变更：立即保存并重启动画（无需重启程序）。</summary>
        private void EffectTier_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            try
            {
                // ★ 初始化时不要写盘：RefreshAll 里设 SelectedIndex 会触发本方法，
                //   而下拉框默认索引是 0（高），会把用户原本的低档设置覆盖成高档
                //   （实测踩过：配置从 2 变成 0，用户没动过设置）。
                if (_loading) return;

                int tier = Math.Clamp(EffectTierCombo.SelectedIndex, 0, 2);
                App.Config.Settings.EffectTier = tier;
                EffectTierHint.Text = DescribeTier(tier);
                ConfigStore.Save(App.Config);
                // ★ 2.17.47：CardShadow 现在跟随档位（低档关闭），
                //   必须重新 Apply 一次主题才能让资源生效。
                ThemeManager.Apply();
                ThemeManager.RefreshEffects();
            }
            catch (Exception ex) { LogBus.Warn("切换界面动效档位失败", ex.Message); }
        }

        private static string DescribeTier(int tier) => tier switch
        {
            0 => "完整动效：动态背景 + 呼吸灯。实测窗口在前台时约占 1 个核心；失去焦点后自动暂停。",
            2 => "全部静止：无动态背景、呼吸灯常亮、卡片阴影关闭。挂机最省电。",
            _ => "推荐档位：关闭动态背景，保留呼吸灯。外观接近原版，开销明显低于「高」。"
        };

        /// <summary>
        /// Broker 服务被安装 / 更新后，重新检测并刷新状态栏。
        ///
        /// ★ 用户实测：「安装服务后状态没刷新」——
        ///   服务已经换成新版（日志 SERVICE_RUNNING），
        ///   但设置页那段文字还停在"服务版本 2.17.12.0 低于最低要求 2.17.13.0"，
        ///   因为这里从来没订阅过服务更新事件，只能等手动点「重新检测」。
        /// </summary>
        private void OnBrokerServiceUpdatedForUi()
        {
            try
            {
                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        Services.LogBus.Info("检测到 Broker 服务已更新", "正在重新检测通道状态");
                        await DetectAsync();
                    }
                    catch (Exception ex) { App.AppendErrorLog("OnBrokerServiceUpdatedForUi", ex); }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { }
        }

        /// <summary>
        /// 导出手动代理：生成一个 asInvoker 副本，用户放到目标账号的远程桌面里双击即可。
        /// 不写服务、不建计划任务、不需要管理员权限。
        /// </summary>
        private void ExportAgent_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 默认导到主程序旁边的 agent 目录，便于直接从远程桌面访问共享路径。
                string self = Services.SystemLauncher.ResolveSelfExe();
                string baseDir = string.IsNullOrWhiteSpace(self)
                    ? AppContext.BaseDirectory
                    : (System.IO.Path.GetDirectoryName(self) ?? AppContext.BaseDirectory);
                string target = System.IO.Path.Combine(baseDir, "agent");

                var (ok, message) = Services.ManualAgent.Export(target);
                string tip = ok
                    ? "已生成手动代理：\n" + message +
                      "\n\n用法：把这个 exe 复制到目标账号的远程桌面里，双击运行即可。\n" +
                      "它会自动找到本机等待中的宿主管道（按会话号匹配），并在该会话内接管鼠标注入。\n\n" +
                      "注意：运行前请先在主程序里对该账号发起过「连接」或「投送」，让宿主把管道建好。"
                    : message;
                ThemedMessage.Info(Window.GetWindow(this), ok ? "导出手动代理" : "导出失败", tip);
            }
            catch (Exception ex)
            {
                ThemedMessage.Info(Window.GetWindow(this), "导出失败", ex.Message);
            }
        }
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
            App.Config.Settings.OnlyAutomationAccounts = OnlyAutomationCheck.IsChecked == true;

            // ★ 2.21.0：备份保留次数。空 = 0 = 不限制。
            App.Config.Settings.BgiBackupKeep = ReadBackupKeep();
            // ★ 2.21.2：缓存保留天数。空 = 0 = 不限制。
            App.Config.Settings.RouteCacheKeepDays = ReadCacheKeepDays();

            ConfigStore.Save(App.Config);
            RefreshAccounts(); RebuildBgiPathList(); _ = RefreshAutoStartAsync();
            if (Application.Current?.MainWindow is MainWindow window)
            { window.LaunchView.Activate(); window.DataCenterView.Activate(); window.ConsoleView.RebuildUserList(); }
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
                if (AccountScope.Allows(AutoStart.CurrentUser)) users.Add(AutoStart.CurrentUser);
                // 从账号配置里补充（这些是本机能登录的 RDP 用户）
                foreach (var a in AccountScope.Accounts(App.Config))
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
                if (AccountScope.Accounts(App.Config).Count == 0)
                {
                    AutoConnectList.Children.Add(new TextBlock
                    {
                        Text = "尚未配置任何 RDP 账号",
                        Style = (Style)FindResource("Caption")
                    });
                }
                foreach (var a in AccountScope.Accounts(App.Config))
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

        /// <summary>
        /// 显示数据目录位置与占用。
        ///
        /// ★ 2.20.0：数据目录从 %LOCALAPPDATA% 改到 exe 同目录下。
        ///   老用户的旧数据仍在 C 盘旧位置，这里主动提示，并给出旧目录路径，
        ///   免得用户以为配置丢了。
        /// </summary>
        private void ShowDataDirInfo()
        {
            try
            {
                string dir = AppIdentity.DataDir;
                if (AppIdentity.IsPortable)
                {
                    DataDirModeText.Text = "数据目录：程序所在文件夹（便携模式）";
                }
                else
                {
                    DataDirModeText.Text =
                        "数据目录：%LOCALAPPDATA%（程序目录不可写，已自动回退）。"
                        + "想放到程序旁边请把 SoulLink 移到有写权限的文件夹。";
                }

                // 占用统计：BGI 备份 + 路线更新备份（两个都会无限增长）
                try
                {
                    int keep = App.Config.Settings.BgiBackupKeep;
                    long bgi = Services.BgiUpdater.GetBackupSize();
                    long route = 0;
                    try { route = Services.RouteUpdater.GetBackupSize(); } catch { }

                    long cache = 0;
                    try { cache = Services.RouteUpdater.GetCacheSize(); } catch { }

                    string mode = keep > 0 ? ("保留最近 " + keep + " 次") : "不限制";
                    int days = App.Config.Settings.RouteCacheKeepDays;
                    string cacheMode = days > 0 ? (days + " 天") : "不限制";

                    string txt = "BGI 备份 " + FormatSize(bgi) + "（" + mode + "）"
                               + "   ·   路线备份 " + FormatSize(route) + "（" + mode + "）"
                               + "   ·   包缓存 " + FormatSize(cache) + "（" + cacheMode + "）";
                    StorageUsageText.Text = txt;
                }
                catch { StorageUsageText.Text = ""; }

                // 旧数据迁移：只在「旧目录有数据」且「新旧不是同一个位置」时显示
                try
                {
                    var plan = Services.DataMigration.Inspect();
                    if (plan.CanMigrate)
                    {
                        LegacyDataHint.Text = plan.LegacyDir + "\n"
                            + "共 " + plan.LegacyFiles.ToString("N0") + " 个文件 / "
                            + Services.DataMigration.FormatSize(plan.LegacyBytes) + "。\n"
                            + "复制到当前数据目录（不会删除旧数据，也不会覆盖已存在的文件）。";
                        LegacyDataPanel.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        LegacyDataPanel.Visibility = Visibility.Collapsed;
                    }
                }
                catch { LegacyDataPanel.Visibility = Visibility.Collapsed; }

                // 备份保留次数（空 = 不限制）
                try
                {
                    int keep = App.Config.Settings.BgiBackupKeep;
                    BackupKeepBox.Text = keep > 0 ? keep.ToString() : "";
                }
                catch { }

                // 路线包缓存保留天数（空 = 不限制）
                try
                {
                    int days = App.Config.Settings.RouteCacheKeepDays;
                    CacheKeepBox.Text = days > 0 ? days.ToString() : "";
                }
                catch { }
            }
            catch
            {
                DataDirModeText.Text = "";
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024 / 1024).ToString("F1") + " GB";
            if (bytes >= 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("F1") + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            return bytes + " B";
        }

        /// <summary>
        /// 读取「备份保留次数」输入框。
        /// ★ 留空 = 0 = 不限制，这是用户明确要求的行为。
        /// </summary>
        private int ReadBackupKeep()
        {
            try
            {
                string t = (BackupKeepBox.Text ?? "").Trim();
                if (t.Length == 0) return 0;                 // 空 = 不限制
                if (int.TryParse(t, out int v) && v > 0) return v;
                return 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 备份保留次数输入框失焦时立即保存。
        /// ★ 不只依赖 Option_Changed：用户可能只改了个数字就切走，
        ///   不打任何开关的话那次修改就白填了。
        /// </summary>
        private void BackupKeepBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            try
            {
                int keep = ReadBackupKeep();
                if (App.Config.Settings.BgiBackupKeep != keep)
                {
                    App.Config.Settings.BgiBackupKeep = keep;
                    ConfigStore.Save(App.Config);
                }
            }
            catch { }
        }

        /// <summary>读取「缓存保留天数」。留空 = 0 = 不限制。</summary>
        private int ReadCacheKeepDays()
        {
            try
            {
                string t = (CacheKeepBox.Text ?? "").Trim();
                if (t.Length == 0) return 0;
                if (int.TryParse(t, out int v) && v > 0) return v;
                return 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 缓存天数输入框失焦即保存。
        /// ★ 和备份次数同理：用户可能只改个数字就切走。
        /// </summary>
        private void CacheKeepBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            try
            {
                int days = ReadCacheKeepDays();
                if (App.Config.Settings.RouteCacheKeepDays != days)
                {
                    App.Config.Settings.RouteCacheKeepDays = days;
                    ConfigStore.Save(App.Config);
                }
            }
            catch { }
        }

        /// <summary>手动清理路线包缓存（按天）。</summary>
        private void PruneCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int days = ReadCacheKeepDays();
                if (days <= 0)
                {
                    MessageBox.Show(
                        "当前设置为「不限制」，不会自动清理缓存。\n\n"
                        + "如果确实想清理，请先在左边填入要保留的天数（比如 30）。",
                        "清理缓存", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                int n = Services.RouteUpdater.PruneCache(days);
                ShowDataDirInfo();
                MessageBox.Show(
                    n > 0 ? ("已清理 " + n + " 个超过 " + days + " 天的缓存文件。")
                          : ("没有超过 " + days + " 天的缓存文件。"),
                    "清理缓存", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("清理失败：" + ex.Message, "清理缓存",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>手动清理旧备份。</summary>
        private void PruneBackups_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int keep = ReadBackupKeep();
                if (keep <= 0)
                {
                    MessageBox.Show(
                        "当前设置为「不限制」，不会自动清理任何备份。\n\n"
                        + "如果确实想清理，请先在左边填入要保留的次数（比如 3）。",
                        "清理旧备份", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                int nBgi = Services.BgiUpdater.PruneBackups(keep);
                int nRoute = 0;
                try { nRoute = Services.RouteUpdater.PruneBackups(keep); } catch { }

                ShowDataDirInfo();
                int total = nBgi + nRoute;
                MessageBox.Show(
                    total > 0
                        ? ("已清理 " + total + " 份旧备份（BGI " + nBgi + " 份，路线 " + nRoute + " 份），"
                           + "每项保留最近 " + keep + " 次。")
                        : ("没有需要清理的旧备份（每项保留最近 " + keep + " 次）。"),
                    "清理旧备份", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("清理失败：" + ex.Message, "清理旧备份",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>打开旧版数据目录，让用户先看一眼再决定是否迁移。</summary>
        private void OpenLegacy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = AppIdentity.LegacyConfigDir;
                if (System.IO.Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                else
                    MessageBox.Show("旧数据目录已不存在：" + dir, "打开旧目录",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开失败：" + ex.Message, "打开旧目录",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 把旧版数据迁移到当前数据目录。
        ///
        /// ★ 复制而非移动：旧数据保留，用户确认新的能用之后自己删。
        ///   宁可多占一份空间，也不能让用户丢失唯一一份数据。
        /// ★ 迁移在后台线程跑，完成后回到 UI 线程刷新并询问是否删旧目录。
        /// </summary>
        private async void MigrateLegacy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var plan = Services.DataMigration.Inspect();
                if (!plan.CanMigrate)
                {
                    MessageBox.Show(plan.Reason, "迁移", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var confirm = MessageBox.Show(
                    "将把以下数据复制到当前数据目录：\n\n"
                    + "来源：" + plan.LegacyDir + "\n"
                    + "目标：" + plan.TargetDir + "\n"
                    + "共 " + plan.LegacyFiles.ToString("N0") + " 个文件 / "
                    + Services.DataMigration.FormatSize(plan.LegacyBytes) + "\n\n"
                    + "· 复制，不移动：旧数据会保留\n"
                    + "· 不覆盖已存在的文件\n"
                    + "· 完成后可以自己决定是否删除旧目录\n\n"
                    + "现在开始吗？",
                    "迁移旧数据", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;

                MigrateProgressText.Visibility = Visibility.Visible;
                MigrateProgressText.Text = "正在准备…";

                var progress = new Progress<string>(s =>
                {
                    try { MigrateProgressText.Text = s; } catch { }
                });

                var result = await Task.Run(() =>
                    Services.DataMigration.Migrate(progress, System.Threading.CancellationToken.None));

                // ★ 2.21.1：迁移过来的老格式备份（目录形态）顺手转成 zip，
                //   否则「改压缩包」这个改动对老用户等于没生效。
                if (result.Ok)
                {
                    try
                    {
                        MigrateProgressText.Text = "正在把旧格式备份转成压缩包…";
                        string conv = await Task.Run(() =>
                            Services.DataMigration.ConvertNewDirBackups(progress,
                                System.Threading.CancellationToken.None));
                        if (!string.IsNullOrEmpty(conv)) result.Message += "\n" + conv;
                    }
                    catch { }
                }

                MigrateProgressText.Text = result.Message;
                ShowDataDirInfo();

                if (result.Ok)
                {
                    var clean = MessageBox.Show(
                        result.Message + "\n\n现在删除旧数据目录吗？\n"
                        + "（建议先重启程序确认配置都在，再删除）\n\n"
                        + "旧目录：" + plan.LegacyDir,
                        "迁移完成", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (clean == MessageBoxResult.Yes)
                    {
                        var del = Services.DataMigration.DeleteLegacy();
                        MessageBox.Show(del.Message, "删除旧数据",
                            MessageBoxButton.OK,
                            del.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
                        ShowDataDirInfo();
                    }
                }
                else
                {
                    MessageBox.Show(result.Message, "迁移失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("迁移出错：" + ex.Message, "迁移",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                try { MigrateProgressText.Visibility = Visibility.Collapsed; } catch { }
            }
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
