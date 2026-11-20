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

        /// <summary>
        /// 检测投送引擎状态。
        ///
        /// 现在的引擎是 **SYSTEM 计划任务通道**（schtasks /ru SYSTEM → CreateProcessAsUser），
        /// 不再依赖任何外部工具，唯一的硬性条件是「以管理员身份运行」。
        /// </summary>
        private async Task DetectAsync()
        {
            // ★ 三选一：手动代理优先于 Broker（手动模式下不投送任何东西）
            ApplyDeliveryModeToUi();
            EngineState.Text = "检测中…";
            bool elevated = SystemLauncher.IsElevated;
            if (App.Config.Settings.UseBrokerChannel && elevated)
            {
                var health = await BrokerProtocol.SendAsync(new BrokerProtocol.Request { Command = "health" }, 2500);

                // ★ 版本不一致时：**照常可用，但要明说**。
                //   用户实测：服务跑 2.11.0、客户端 2.17.11，协议都是 v1，
                //   所以这里原本显示"服务可用"，可投送全部失败，非常误导。
                //   用户选择「只提示、不拦截」。
                string? versionNote = health.Ok
                    ? BrokerProtocol.DescribeAppVersionMismatch(health)
                    : null;

                EngineDot.Color = !health.Ok ? Color.FromRgb(0xFF, 0x9B, 0x9B)
                                 : versionNote != null ? Color.FromRgb(0xFF, 0xC4, 0x6B)   // 琥珀：可用但版本不符
                                 : Color.FromRgb(0x41, 0xF0, 0xC0);

                EngineState.Text = !health.Ok ? "Broker 服务不可用"
                                 : versionNote != null ? "Broker 服务可用（版本不一致）"
                                 : "Broker 服务可用";

                EnginePath.Text = !health.Ok ? health.Message
                                : versionNote ?? "服务健康；账号通道仍需单独投送";

                if (versionNote != null)
                    LogBus.Warn("Broker 版本不一致", versionNote);

                StatusChanged?.Invoke(health.Ok, versionNote ?? health.Message);
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