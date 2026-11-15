using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SoulLink.Controls;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Views
{
    /// <summary>
    /// 中控台。结构分两层：
    ///   ① 配置页：左侧用户列表 + 右侧该用户的 RDP 参数
    ///   ② 画面页：每个用户一个，顶部标签卡切换
    /// 
    /// 关键设计：
    ///   · 打开页面**不自动连接**，用户点「打开画面」才接；
    ///   · 画面页控件常驻，切走只改 Visibility，不重建；
    ///   · 配置改动即时保存，但画面要重连才生效（会在界面提示）。
    /// </summary>
    public partial class ConsolePage : UserControl
    {
        private sealed class UserRow
        {
            public Account Account = new Account();
            public RdpConsole? Console;
            public Border? Chip;
            public TextBlock? ChipText;
            public TextBlock? ChipDot;
        }

        private readonly List<UserRow> _rows = new List<UserRow>();
        private UserRow? _editing;    // 配置页正在编辑的用户
        private UserRow? _viewing;    // 画面页正在显示的用户
        private bool _initialized;
        private bool _logoffInProgress;
        private bool _busyVisible;

        public MainWindow? MainWindowRef { get; set; }
        public event Action? AddRequested;

        public ConsolePage()
        {
            InitializeComponent();
            Loaded += (_, __) =>
            {
                var window = Window.GetWindow(this);
                if (window != null)
                {
                    window.Deactivated += (_, __) => Dispatcher.InvokeAsync(() => BusyPopup.IsOpen = false);
                    window.Activated += (_, __) => Dispatcher.InvokeAsync(() => { if (_busyVisible) BusyPopup.IsOpen = true; });
                }
            };
            // ★ 连接遮罩必须跟着「画面视口」走，不是跟着整个页面走。
            //   原来固定 900x620、只在 ConsolePage.SizeChanged 时更新，
            //   切页/侧栏展开/窗口缩放时视口变了它不变，遮罩就和画面不重合
            //   （用户反馈：连接动画没有自适应窗口大小）。
            //   改成监听 ViewportHost 自身的 SizeChanged，并补一次首帧对齐。
            ViewportHost.SizeChanged += (_, __) => SyncBusyOverlaySize();
            Loaded += (_, __) => SyncBusyOverlaySize();
            Loaded += (_, __) => Activate();

            double sens = App.Config.Settings.LookSensitivity > 0 ? App.Config.Settings.LookSensitivity : 4.0;
            if (SensSlider != null)
            {
                // ★ 必须先屏蔽 ValueChanged 再赋值：
                //   工具栏浮层里的 SensSlider 和「RDP 通用设置」共用同一个设置项，
                //   而 XAML 里写死 Value="4"。构造时直接赋值会触发 SensSlider_ValueChanged，
                //   它无脑 ConfigStore.Save(...) —— 结果用户保存的灵敏度在下次启动时被覆盖。
                //   （实测：写入 9.5 → 重启后变成 1/4）
                _suppressSensSave = true;
                SensSlider.Value = Math.Min(12, Math.Max(1, sens));
                if (SensText != null) SensText.Text = sens.ToString("0.0") + "x";
                _suppressSensSave = false;
            }
            if (HotkeyBtn != null)
            {
                HotkeyBtn.SetSpec(HotkeySpec.Parse(App.Config.Settings.LockHotkey)
                    ?? HotkeySpec.Parse("Ctrl+Alt+Q"));
                HotkeyBtn.Recorded += OnHotkeyRecorded;
            }
        }

        /// <summary>
        /// 让连接遮罩严格铺满画面视口。
        /// Popup 的内容不会自动跟随 PlacementTarget 伸缩，必须手动同步宽高，
        /// 否则遮罩会比画面小一圈或大一圈（用户反馈：没有自适应窗口大小）。
        /// </summary>
        private void SyncBusyOverlaySize()
        {
            try
            {
                double w = ViewportHost.ActualWidth;
                double h = ViewportHost.ActualHeight;
                if (w <= 0 || h <= 0) return;

                if (BusyOverlay != null)
                {
                    BusyOverlay.Width = w;
                    BusyOverlay.Height = h;
                }
                // Placement=Relative 时偏移归零，遮罩左上角与视口左上角重合
                if (BusyPopup != null)
                {
                    BusyPopup.HorizontalOffset = 0;
                    BusyPopup.VerticalOffset = 0;
                }
            }
            catch { }
        }

        public void Activate()
        {
            RebuildUserList();
            RenderTabs();
            _initialized = true;
            SyncBusyOverlaySize();
        }

        // ── 标签卡 ───────────────────────────────────────────────────────────

        /// <summary>已经打开过画面的用户（用户要求：打开之后才出现标签卡）。</summary>
        private readonly HashSet<string> _openedIds = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>多选：被勾选的用户（可以批量打开画面）。</summary>
        private readonly HashSet<string> _checkedIds = new HashSet<string>(StringComparer.Ordinal);

        private void RenderTabs()
        {
            TabBar.Children.Clear();

            // 「配置」页签始终在第一位
            TabBar.Children.Add(MakeChip("配置", null));

            // 只为「打开过」的用户显示页签，没打开的不占位
            foreach (var row in _rows)
            {
                if (!_openedIds.Contains(row.Account.Id)) continue;
                TabBar.Children.Add(MakeChip(row.Account.Caption, row));
            }
        }

        private UIElement MakeChip(string text, UserRow? row)
        {
            bool on = row == null ? _viewing == null : ReferenceEquals(_viewing, row);

            var border = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(11, 6, 11, 6),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(on
                    ? Color.FromArgb(0x2E, 0x7C, 0x5C, 0xFF)
                    : Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(on
                    ? Color.FromArgb(0x77, 0x7C, 0x5C, 0xFF)
                    : Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1)
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            if (row != null)
            {
                var dot = new TextBlock
                {
                    Text = "\u25CF",
                    FontSize = 7,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    Foreground = new SolidColorBrush(row.Console?.IsConnected == true
                        ? Color.FromRgb(0x41, 0xF0, 0xC0)
                        : Color.FromRgb(0x6B, 0x7C, 0x9C))
                };
                sp.Children.Add(dot);
                row.ChipDot = dot;
            }

            var label = new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = on ? (Brush)FindResource("TextPrimary") : (Brush)FindResource("TextSecondary")
            };
            sp.Children.Add(label);
            border.Child = sp;

            border.MouseLeftButtonUp += (_, __) =>
            {
                if (row == null) ShowConfigPage();
                else { _showGeneralSettings = false; _ = ShowViewPageAsync(row); }
            };

            if (row != null) { row.Chip = border; row.ChipText = label; }
            return border;
        }

        // ── 页面切换 ─────────────────────────────────────────────────────────

        private void ShowConfigPage()
        {
            _viewing = null;
            ConfigPage.Visibility = Visibility.Visible;
            SetBusyVisible(false);
            ViewPage.Visibility = Visibility.Collapsed;
            ViewOps.Visibility = Visibility.Collapsed;
            RenderTabs();
            RebuildUserList();
        }

        private async Task ShowViewPageAsync(UserRow row)
        {
            _viewing = row;
            _openedIds.Add(row.Account.Id);   // 打开过的才进标签卡
            ConfigPage.Visibility = Visibility.Collapsed;
            ViewPage.Visibility = Visibility.Visible;
            ViewOps.Visibility = Visibility.Visible;
            SetBusyVisible(!row.Console?.IsConnected ?? true);
            RenderTabs();

            ViewportHost.Children.Clear();
            if (row.Console == null) InitializeConsole(row);
            ViewportHost.Children.Add(row.Console);
            row.Console.OnShown();

            if (!row.Console.IsConnected) await ConnectAsync(row);
        }

        private async Task ConnectAsync(UserRow row)
        {
            if (row.Console == null) return;
            SetBusyVisible(true);
            BusyText.Text = "正在连接 " + row.Account.Caption + " …";

            try
            {
                var sessions = await SessionService.QueryAsync();
                var s = SessionService.Find(sessions, row.Account.UserName);

                // ★ 会话不存在**不是错误**——RDP 会用它自己的登录流程新建一个会话。
                //   （HANDOFF 10.2 实测：用户注销时 Connect() 会自动登录并建出新会话）
                //   所以这里不再拦截，只用它来决定「要不要先踢下线」和日志用。
                int sessionId = s?.Id ?? 0;

                if (s == null)
                {
                    BusyText.Text = "用户「" + row.Account.Caption + "」当前未登录，正在尝试用保存的凭据登录…";
                    Debug("会话不存在，走 RDP 自动登录流程");
                }

                var (ok, msg) = await row.Console.ConnectAsync(row.Account, App.Config.Settings, sessionId);
                SetBusyVisible(!ok);
                if (!ok) BusyText.Text = msg;
                RenderTabs();
            }
            catch (Exception ex)
            {
                BusyText.Text = "连接出错：" + ex.Message;
                ShowBusyError();
                App.AppendErrorLog("ConsolePage.Connect", ex);
            }
        }

        private void SetBusyVisible(bool visible)
        {
            if (visible) SyncBusyOverlaySize();   // 显示前再对一次，避免用了旧尺寸
            _busyVisible = visible;
            var owner = Window.GetWindow(this);
            BusyPopup.IsOpen = visible && ViewPage.Visibility == Visibility.Visible
                && owner != null && owner.IsVisible && owner.IsActive && owner.WindowState != WindowState.Minimized;
            if (!visible) BtnBusyReconnect.Visibility = Visibility.Collapsed;
        }

        private static void Debug(string msg)
        {
            try
            {
                System.IO.Directory.CreateDirectory(ConfigStore.ConfigDir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(ConfigStore.ConfigDir, "rdp-debug.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  [Console] " + msg + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>
        /// 连接失败时把遮罩变成「错误态」：允许退出、给一个重连按钮。
        /// 否则用户会卡在全黑画面上不知道发生了什么。
        /// </summary>
        private void ShowBusyError()
        {
            SetBusyVisible(true);
            BtnBusyReconnect.Visibility = Visibility.Visible;
            SetBusyVisible(true);
        }

        private async void BtnBusyReconnect_Click(object sender, RoutedEventArgs e)
        {
            BtnBusyReconnect.Visibility = Visibility.Collapsed;
            BusyText.Text = "正在重试…";
            var row = _viewing;
            if (row == null) return;
            try { row.Console?.Disconnect(); } catch { }
            await ConnectAsync(row);
        }

        // ── 用户列表 ─────────────────────────────────────────────────────────

        private void RebuildUserList()
        {
            var accounts = App.Config.Accounts.Select((a, index) => new { a, index })
                .OrderBy(x => x.a.SortOrder <= 0 ? x.index + 1 : x.a.SortOrder).Select(x => x.a).ToList();

            // 保留已有 Console，按账号 Id 对齐
            var old = _rows.ToDictionary(r => r.Account.Id);
            _rows.Clear();
            foreach (var a in accounts)
            {
                if (old.TryGetValue(a.Id, out var r)) { r.Account = a; _rows.Add(r); }
                else _rows.Add(new UserRow { Account = a });
            }

            if (_editing != null && !_rows.Contains(_editing)) _editing = null;
            if (_viewing != null && !_rows.Contains(_viewing)) _viewing = null;

            var panel = new StackPanel();

            // ★ 第一条固定是「通用设置」卡片，样式与用户卡片一致
            panel.Children.Add(BuildSettingsRow());

            foreach (var row in _rows) panel.Children.Add(BuildRow(row));
            UserList.ItemsSource = new[] { panel };

            NoUserHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // 多选批量按钮只在有勾选时出现
            if (BtnOpenSelected != null)
            {
                BtnOpenSelected.Visibility = _checkedIds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (OpenSelectedText != null)
                    OpenSelectedText.Text = "打开勾选的 " + _checkedIds.Count + " 个画面";
            }

            if (_showGeneralSettings) GeneralSettingsPanel.Visibility = Visibility.Visible;
            else if (_editing != null) LoadConfigFields(_editing);
            else ShowNoSelection();
        }

        private UIElement BuildRow(UserRow row)
        {
            bool on = ReferenceEquals(row, _editing);
            bool isChecked = _checkedIds.Contains(row.Account.Id);

            var border = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8, 7, 8, 7),
                Margin = new Thickness(0, 0, 0, 5),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(on
                    ? Color.FromArgb(0x2E, 0x7C, 0x5C, 0xFF)
                    : Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(on
                    ? Color.FromArgb(0x77, 0x7C, 0x5C, 0xFF)
                    : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                // 去掉焦点虚线框（用户反馈）
                FocusVisualStyle = null,
                AllowDrop = true
            };

            // ★ 三列布局，各司其职：
            //   [勾选框 26px] [用户名+地址 *] [状态灯 Auto]
            //   状态灯之前和勾选框挤在同一格，看起来像"勾选框里有个点"。
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });

            // ── 第 1 列：勾选框（大一点，好点）──
            var check = new CheckBox
            {
                Width = 28,
                Height = 32,
                IsChecked = isChecked,
                Style = (Style)FindResource("GlassCheckBox"),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = isChecked ? "取消选择 " + row.Account.Caption : "选择 " + row.Account.Caption,
            };
            check.Click += (_, ev) =>
            {
                ToggleCheck(row.Account.Id);
                ev.Handled = true;
            };
            System.Windows.Automation.AutomationProperties.SetName(check, "选择 " + row.Account.Caption);
            Grid.SetColumn(check, 0);
            grid.Children.Add(check);

            // ── 第 2 列：用户名 + 地址（只显示一次）──
            var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textCol.Children.Add(new TextBlock
            {
                Text = row.Account.Caption,
                FontSize = 11.5,
                FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource(on ? "TextPrimary" : "TextSecondary")
            });

            string hostText = string.IsNullOrWhiteSpace(row.Account.RdpHost)
                ? "自动地址"
                : row.Account.RdpHost + ":" + (row.Account.RdpPort > 0 ? row.Account.RdpPort : 3389);
            textCol.Children.Add(new TextBlock
            {
                Text = hostText,
                FontSize = 9,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("TextMuted")
            });
            Grid.SetColumn(textCol, 1);
            grid.Children.Add(textCol);

            // ── 第 3 列：连接状态灯（独立位置，不再占用勾选框）──
            var dot = new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 2, 0),
                Background = new SolidColorBrush(row.Console?.IsConnected == true
                    ? Color.FromRgb(0x41, 0xF0, 0xC0)
                    : Color.FromArgb(0x55, 0x8B, 0x9C, 0xC8)),
                ToolTip = row.Console?.IsConnected == true ? "画面已连接" : "未连接"
            };
            Grid.SetColumn(dot, 2);
            grid.Children.Add(dot);

            var dragHandle = new Border
            {
                Width = 24,
                Height = 32,
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeAll,
                ToolTip = "拖动调整顺序",
                AllowDrop = true
            };
            System.Windows.Automation.AutomationProperties.SetName(dragHandle, "拖动排序 " + row.Account.Caption);
            dragHandle.Child = new TextBlock
            {
                Text = "⋮⋮",
                FontSize = 13,
                Foreground = (Brush)FindResource("TextMuted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(dragHandle, 3);
            grid.Children.Add(dragHandle);
            dragHandle.PreviewMouseLeftButtonDown += (_, ev) =>
            {
                ev.Handled = true;
                try { DragDrop.DoDragDrop(dragHandle, new DataObject("SoulLinkAccount", row.Account.Id), DragDropEffects.Move); } catch { }
            };

            // 单独设置空白卡片的焦点样式；内部 CheckBox 不能替换为 null 模板。
            border.FocusVisualStyle = (Style)FindResource("NoFocusVisual");
            border.Child = grid;

            // 卡片空白区可拖动排序；单击、勾选和右键仍保持各自行为。
            Point dragStart = default;
            border.PreviewMouseLeftButtonDown += (_, ev) =>
            {
                dragStart = ev.GetPosition(border);
            };
            border.MouseMove += (_, ev) =>
            {
                if (ev.LeftButton != MouseButtonState.Pressed || ev.Handled) return;
                var delta = ev.GetPosition(border) - dragStart;
                if (Math.Abs(delta.X) < 7 && Math.Abs(delta.Y) < 7) return;
                try { DragDrop.DoDragDrop(border, row.Account.Id, DragDropEffects.Move); } catch { }
            };
            border.DragOver += (_, ev) =>
            {
                if (ev.Data.GetDataPresent("SoulLinkAccount"))
                {
                    ev.Effects = DragDropEffects.Move;
                    border.BorderBrush = (Brush)FindResource("CyanBrush");
                }
                ev.Handled = true;
            };
            border.DragLeave += (_, __) => border.BorderBrush = new SolidColorBrush(on ? Color.FromArgb(0x77, 0x7C, 0x5C, 0xFF) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            border.Drop += (_, ev) =>
            {
                if (!ev.Data.GetDataPresent("SoulLinkAccount")) return;
                string sourceId = (string)ev.Data.GetData("SoulLinkAccount");
                int from = App.Config.Accounts.FindIndex(a => a.Id == sourceId);
                int to = App.Config.Accounts.FindIndex(a => a.Id == row.Account.Id);
                if (from < 0 || to < 0 || from == to) return;
                var moved = App.Config.Accounts[from];
                App.Config.Accounts.RemoveAt(from);
                App.Config.Accounts.Insert(to, moved);
                for (int i = 0; i < App.Config.Accounts.Count; i++) App.Config.Accounts[i].SortOrder = i + 1;
                ConfigStore.Save(App.Config);
                RebuildUserList();
                LogBus.Ok("用户顺序已保存", moved.Caption + " → 第 " + (to + 1) + " 位");
                ev.Handled = true;
            };

            // ── 行点击 ──
            border.MouseLeftButtonUp += (_, ev) =>
            {
                // Ctrl / Shift = 切换勾选
                if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
                {
                    ToggleCheck(row.Account.Id);
                    return;
                }

                _showGeneralSettings = false;
                GeneralSettingsPanel.Visibility = Visibility.Collapsed;
                _editing = row;
                LoadConfigFields(row);
                RebuildUserList();
            };

            border.MouseLeftButtonDown += (_, ev) =>
            {
                if (ev.ClickCount == 2)
                {
                    ev.Handled = true;
                    _ = ShowViewPageAsync(row);
                }
            };

            // ── 右键菜单（用户要求：复制 / 删除）──
            border.ContextMenu = BuildUserContextMenu(row);

            return border;
        }

        /// <summary>
        /// 用户列表第一条：通用设置。样式跟用户卡片一样，点了右侧显示设置内容。
        /// </summary>
        private bool _showGeneralSettings;
        private UIElement BuildSettingsRow()
        {
            bool on = _showGeneralSettings;

            var border = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand,
                FocusVisualStyle = (Style)FindResource("NoFocusVisual"),
                Background = new SolidColorBrush(on
                    ? Color.FromArgb(0x2E, 0x7C, 0x5C, 0xFF)
                    : Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(on
                    ? Color.FromArgb(0x77, 0x7C, 0x5C, 0xFF)
                    : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1)
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock
            {
                Text = "\uE713",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 9, 0),
                Foreground = (Brush)FindResource(on ? "TextPrimary" : "TextSecondary")
            });
            sp.Children.Add(new TextBlock
            {
                Text = "通用设置",
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = (Brush)FindResource(on ? "TextPrimary" : "TextSecondary")
            });

            border.Child = sp;
            border.MouseLeftButtonUp += (_, __) =>
            {
                ShowGeneralSettings();
            };
            return border;
        }

        /// <summary>
        /// 在右侧显示「通用设置」。
        /// 复用配置区的表单，但隐藏所有"用户级"字段，只留灵敏度与热键。
        /// </summary>
        public void ShowGeneralSettings()
        {
            _showGeneralSettings = true;
            _editing = null;
            _viewing = null;
            ConfigPage.Visibility = Visibility.Visible;
            ViewPage.Visibility = Visibility.Collapsed;
            ViewOps.Visibility = Visibility.Collapsed;
            NoSelectionHint.Visibility = Visibility.Collapsed;
            ConfigPanel.Visibility = Visibility.Collapsed;
            GeneralSettingsPanel.Visibility = Visibility.Visible;
            SetBusyVisible(false);
            RebuildUserList();
            RenderTabs();
        }

        /// <summary>切换某个用户的勾选状态（只改勾选，不触发打开画面）。</summary>
        private void ToggleCheck(string accountId)
        {
            if (_checkedIds.Contains(accountId)) _checkedIds.Remove(accountId);
            else _checkedIds.Add(accountId);
            RebuildUserList();
        }

        /// <summary>用户行的右键菜单。</summary>
        private ContextMenu BuildUserContextMenu(UserRow row)
        {
            var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

            var miOpen = new MenuItem { Header = "打开画面" };
            miOpen.Click += (_, __) => _ = ShowViewPageAsync(row);
            menu.Items.Add(miOpen);

            var miConfig = new MenuItem { Header = "编辑配置" };
            miConfig.Click += (_, __) => { _showGeneralSettings = false; GeneralSettingsPanel.Visibility = Visibility.Collapsed; _editing = row; RebuildUserList(); };
            menu.Items.Add(miConfig);

            menu.Items.Add(new Separator());

            var miCopy = new MenuItem { Header = "复制一份" };
            miCopy.Click += (_, __) => DuplicateUser(row);
            menu.Items.Add(miCopy);

            var miCheck = new MenuItem
            {
                Header = _checkedIds.Contains(row.Account.Id) ? "取消勾选" : "勾选"
            };
            miCheck.Click += (_, __) => ToggleCheck(row.Account.Id);
            menu.Items.Add(miCheck);

            menu.Items.Add(new Separator());

            var miDel = new MenuItem { Header = "删除用户" };
            miDel.Click += (_, __) => RemoveUser(row);
            menu.Items.Add(miDel);

            return menu;
        }

        /// <summary>复制一个用户配置（便于批量建号）。</summary>
        private void DuplicateUser(UserRow row)
        {
            var src = row.Account;
            var copy = new Account
            {
                UserName = src.UserName,
                DisplayName = src.Caption + " (副本)",
                PasswordCipher = src.PasswordCipher,
                SessionId = src.SessionId,
                Note = src.Note,
                RdpHost = src.RdpHost,
                RdpPort = src.RdpPort,
                RdpUserName = src.RdpUserName,
                IconBase64 = src.IconBase64,
                RdpWidth = src.RdpWidth,
                RdpHeight = src.RdpHeight,
                RdpScaleMode = src.RdpScaleMode,
                RdpColorDepth = src.RdpColorDepth,
                RdpAutoDisconnect = src.RdpAutoDisconnect,
                RdpLowBandwidth = src.RdpLowBandwidth,
                RdpAudioMode = src.RdpAudioMode,
                RdpAudioCapture = src.RdpAudioCapture,
                RdpNetworkConnectionType = src.RdpNetworkConnectionType,
                RdpPerformanceMode = src.RdpPerformanceMode,
                RdpKeyboardHookMode = src.RdpKeyboardHookMode,
                RdpRedirectPrinters = src.RdpRedirectPrinters,
                RdpRedirectClipboard = src.RdpRedirectClipboard,
                RdpRedirectSmartCards = src.RdpRedirectSmartCards,
                RdpRedirectPorts = src.RdpRedirectPorts,
                RdpRedirectDrives = src.RdpRedirectDrives
            };

            App.Config.Accounts.Add(copy);
            ConfigStore.Save(App.Config);

            var newRow = new UserRow { Account = copy };
            _rows.Add(newRow);
            _editing = newRow;
            RebuildUserList();
            LogBus.Ok("已复制用户配置：" + copy.DisplayName);
        }

        private void ShowNoSelection()
        {
            NoSelectionHint.Visibility = Visibility.Visible;
            ConfigPanel.Visibility = Visibility.Collapsed;
            GeneralSettingsPanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 用户配置页里"改了就立刻存"的控件。
        ///
        /// ★ 为什么需要：这些下拉框/复选框原来**没有绑定任何事件**，
        ///   改完不点「保存配置」就等于没改，用户反馈"设置好了下次打开又没了"。
        ///   这里统一挂上自动保存，跟通用设置页的行为保持一致。
        /// </summary>
        private void HookAutoSave()
        {
            if (_autoSaveHooked) return;
            _autoSaveHooked = true;

            foreach (var box in new[] { DepthCombo, ScaleCombo, PerformanceCombo, NetworkCombo, AudioCombo, KeyboardCombo })
                box.SelectionChanged += (_, __) => AutoSaveConfig();

            foreach (var check in new[] { AutoDiscCheck, AudioCaptureCheck, ClipboardCheck, PrintersCheck,
                                          SmartCardsCheck, PortsCheck, DrivesCheck })
            {
                check.Checked += (_, __) => AutoSaveConfig();
                check.Unchecked += (_, __) => AutoSaveConfig();
            }
        }

        private bool _autoSaveHooked;
        private bool _suppressAutoSave;
        private bool _loadingConfig;

        /// <summary>
        /// 把当前表单里的"非文本"选项写回账号并保存。
        /// 文本类字段（名字/地址/端口/分辨率）仍需点「保存配置」——避免边输边存把密码框清空。
        /// </summary>
        private void AutoSaveConfig()
        {
            if (_suppressAutoSave || _loadingConfig) return;
            var row = _editing;
            if (row == null) return;

            var a = row.Account;
            a.RdpColorDepth = TagOf(DepthCombo, a.RdpColorDepth > 0 ? a.RdpColorDepth : 32);
            a.RdpScaleMode = TagOf(ScaleCombo, a.RdpScaleMode);
            a.RdpPerformanceMode = TagOf(PerformanceCombo, a.RdpPerformanceMode);
            a.RdpNetworkConnectionType = TagOf(NetworkCombo, a.RdpNetworkConnectionType);
            a.RdpAudioMode = TagOf(AudioCombo, a.RdpAudioMode);
            a.RdpKeyboardHookMode = TagOf(KeyboardCombo, a.RdpKeyboardHookMode);

            a.RdpAutoDisconnect = AutoDiscCheck.IsChecked == true;
            a.RdpAudioCapture = AudioCaptureCheck.IsChecked == true;
            a.RdpRedirectClipboard = ClipboardCheck.IsChecked == true;
            a.RdpRedirectPrinters = PrintersCheck.IsChecked == true;
            a.RdpRedirectSmartCards = SmartCardsCheck.IsChecked == true;
            a.RdpRedirectPorts = PortsCheck.IsChecked == true;
            a.RdpRedirectDrives = DrivesCheck.IsChecked == true;

            ConfigStore.Save(App.Config);
            try { ConsoleView_ApplyToOpenConsoles(a); } catch { }
            CfgSub.Text = "已保存 · " + DateTime.Now.ToString("HH:mm:ss");
        }

        /// <summary>让已打开的画面立刻用上新参数（只对同一账号生效）。</summary>
        private void ConsoleView_ApplyToOpenConsoles(SoulLink.Models.Account account)
        {
            foreach (Window window in Application.Current.Windows)
                foreach (var console in FindConsoles(window))
                    if (console.Account?.Id == account.Id) console.ApplyGlobalSettings();
        }

        private static System.Collections.Generic.IEnumerable<RdpConsole> FindConsoles(DependencyObject root)
        {
            if (root is RdpConsole console) yield return console;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var nested in FindConsoles(System.Windows.Media.VisualTreeHelper.GetChild(root, i)))
                    yield return nested;
        }

        private void LoadConfigFields(UserRow row)
        {
            NoSelectionHint.Visibility = Visibility.Collapsed;
            GeneralSettingsPanel.Visibility = Visibility.Collapsed;
            _showGeneralSettings = false;
            ConfigPanel.Visibility = Visibility.Visible;

            HookAutoSave();

            // 填表期间不要触发自动保存，否则会把刚读出来的值原样写回去、
            // 还可能把 CfgSub 改成"已保存"。
            _loadingConfig = true;

            var a = row.Account;
            CfgTitle.Text = a.Caption;
            CfgSub.Text = "用户 " + a.UserName;

            // 从通用设置切回用户配置时，恢复所有输入框
            NameBox.IsEnabled = true;
            HostBox.IsEnabled = true;
            PortBox.IsEnabled = true;
            UserBox.IsEnabled = true;

            NameBox.Text = a.DisplayName ?? "";
            HostBox.Text = a.RdpHost ?? "";
            PortBox.Text = (a.RdpPort > 0 ? a.RdpPort : 3389).ToString();
            UserBox.Text = a.EffectiveRdpUserName;
            WidthBox.Text = (a.RdpWidth > 0 ? a.RdpWidth : 1280).ToString();
            HeightBox.Text = (a.RdpHeight > 0 ? a.RdpHeight : 720).ToString();

            SelectByTag(DepthCombo, (a.RdpColorDepth > 0 ? a.RdpColorDepth : 32).ToString());
            SelectByTag(ScaleCombo, (a.RdpScaleMode >= 0 ? a.RdpScaleMode : 0).ToString());
            SelectByTag(PerformanceCombo, a.RdpPerformanceMode.ToString());
            SelectByTag(NetworkCombo, a.RdpNetworkConnectionType.ToString());
            SelectByTag(AudioCombo, a.RdpAudioMode.ToString());
            SelectByTag(KeyboardCombo, a.RdpKeyboardHookMode.ToString());
            AudioCaptureCheck.IsChecked = a.RdpAudioCapture;
            ClipboardCheck.IsChecked = a.RdpRedirectClipboard;
            PrintersCheck.IsChecked = a.RdpRedirectPrinters;
            SmartCardsCheck.IsChecked = a.RdpRedirectSmartCards;
            PortsCheck.IsChecked = a.RdpRedirectPorts;
            DrivesCheck.IsChecked = a.RdpRedirectDrives;

            AutoDiscCheck.IsChecked = a.RdpAutoDisconnect;
            PasswordBox.Password = "";

            _loadingConfig = false;
        }

        private static void SelectByTag(ComboBox box, string tag)
        {
            foreach (var it in box.Items.OfType<ComboBoxItem>())
                if ((it.Tag as string) == tag) { box.SelectedItem = it; return; }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }

        private static int TagOf(ComboBox box, int fallback)
        {
            if (box.SelectedItem is ComboBoxItem it && it.Tag is string s && int.TryParse(s, out int v)) return v;
            return fallback;
        }

        // ── 配置操作 ─────────────────────────────────────────────────────────

        private void SaveConfig_Click(object sender, RoutedEventArgs e)
        {
            var row = _editing;
            if (row == null) return;
            var a = row.Account;

            string login = (UserBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(login))
            {
                CfgSub.Text = "请先填写 Windows / RDP 登录名";
                UserBox.Focus();
                return;
            }
            a.UserName = login;
            a.RdpUserName = login;
            if (PasswordBox.Password.Length > 0)
            {
                ConfigStore.SetPassword(a, PasswordBox.Password);
            }
            PasswordBox.Password = "";
            a.DisplayName = (NameBox.Text ?? "").Trim();
            a.RdpHost = (HostBox.Text ?? "").Trim();
            a.RdpPort = int.TryParse((PortBox.Text ?? "").Trim(), out int p) && p > 0 && p <= 65535 ? p : 3389;

            string u = (UserBox.Text ?? "").Trim();
            a.RdpUserName = u.Length == 0 ? a.UserName : u;

            a.RdpWidth = int.TryParse((WidthBox.Text ?? "").Trim(), out int w) && w >= 200 && w <= 8192 ? w : 1280;
            a.RdpHeight = int.TryParse((HeightBox.Text ?? "").Trim(), out int h) && h >= 200 && h <= 8192 ? h : 720;
            a.RdpColorDepth = TagOf(DepthCombo, 32);
            a.RdpScaleMode = TagOf(ScaleCombo, 0);
            a.RdpAutoDisconnect = AutoDiscCheck.IsChecked == true;
            a.RdpPerformanceMode = TagOf(PerformanceCombo, 3);
            a.RdpNetworkConnectionType = TagOf(NetworkCombo, 6);
            a.RdpAudioMode = TagOf(AudioCombo, 0);
            a.RdpKeyboardHookMode = TagOf(KeyboardCombo, 1);
            a.RdpAudioCapture = AudioCaptureCheck.IsChecked == true;
            a.RdpRedirectClipboard = ClipboardCheck.IsChecked == true;
            a.RdpRedirectPrinters = PrintersCheck.IsChecked == true;
            a.RdpRedirectSmartCards = SmartCardsCheck.IsChecked == true;
            a.RdpRedirectPorts = PortsCheck.IsChecked == true;
            a.RdpRedirectDrives = DrivesCheck.IsChecked == true;

            ConfigStore.Save(App.Config);
            RebuildUserList();
            RenderTabs();
            LogBus.Ok("已保存 " + a.Caption + " 的配置", "画面参数需要重连后生效");
        }

        /// <summary>删除一个用户配置（右键菜单与按钮共用）。</summary>
        private void RemoveUser(UserRow row)
        {
            if (!Dialogs.Confirm("确定移除用户「" + row.Account.Caption + "」吗？\n\n只删除这里的配置，不影响该用户的实际会话。", "移除用户", true)) return;

            try { row.Console?.Dispose(); } catch { }
            _openedIds.Remove(row.Account.Id);
            _checkedIds.Remove(row.Account.Id);
            App.Config.Accounts.RemoveAll(x => x.Id == row.Account.Id);
            ConfigStore.Save(App.Config);

            if (ReferenceEquals(_viewing, row))
            {
                _viewing = null;
                ViewportHost.Children.Clear();
                ShowConfigPage();
            }
            if (ReferenceEquals(_editing, row)) _editing = null;

            _rows.Remove(row);
            RebuildUserList();
            RenderTabs();
            LogBus.Ok("已删除用户配置：" + row.Account.Caption);
        }

        private void RemoveUser_Click(object sender, RoutedEventArgs e)
        {
            var row = _editing;
            if (row == null) return;
            if (!Dialogs.Confirm("要从列表移除 " + row.Account.Caption + " 吗？\n只删除配置，不影响该用户会话。", "移除用户", true)) return;

            try { row.Console?.Dispose(); } catch { }
            _openedIds.Remove(row.Account.Id);
            App.Config.Accounts.RemoveAll(x => x.Id == row.Account.Id);
            ConfigStore.Save(App.Config);
            _editing = null;
            if (ReferenceEquals(_viewing, row)) { _viewing = null; ViewportHost.Children.Clear(); ShowConfigPage(); }
            RebuildUserList();
            RenderTabs();
        }

        private void BtnOpenView_Click(object sender, RoutedEventArgs e)
        {
            if (_editing != null) _ = ShowViewPageAsync(_editing);
        }

        /// <summary>为所有勾选的用户同时打开画面（逐个接入，最后一个留在前台）。</summary>
        private async void BtnOpenSelected_Click(object sender, RoutedEventArgs e)
        {
            var targets = _rows.Where(r => _checkedIds.Contains(r.Account.Id)).ToList();
            if (targets.Count == 0) return;

            LogBus.Info("批量接入 " + targets.Count + " 个用户画面");

            foreach (var row in targets)
            {
                try
                {
                    _openedIds.Add(row.Account.Id);
                    if (row.Console == null) InitializeConsole(row);
                    row.Console!.OnShown();
                    if (!row.Console.IsConnected) await ConnectAsync(row);
                }
                catch (Exception ex)
                {
                    LogBus.Error("接入 " + row.Account.Caption + " 失败", ex.Message);
                }
            }

            // 最后显示第一个勾选的
            if (targets.Count > 0) await ShowViewPageAsync(targets[0]);
            RenderTabs();
        }

        /// <summary>创建一个用户对应的 RdpConsole（尚未连接）。</summary>
        private void InitializeConsole(UserRow row)
        {
            var c = new RdpConsole();
            c.InputActivity += (_, t) =>
                Dispatcher.InvokeAsync(() => { if (ReferenceEquals(row, _viewing)) InputText.Text = t; });
            c.InputModeChanged += (_, m) =>
                Dispatcher.InvokeAsync(() => { if (ReferenceEquals(row, _viewing)) ApplyModeUi(m); });
            row.Console = c;
        }

        /// <summary>
        /// 新增一个用户配置条目（不是从在线会话里挑，用户明确要求过）。
        /// 新建后直接进入编辑态，填完保存即可。
        /// </summary>
        private void BtnAddUser_Click(object sender, RoutedEventArgs e)
        {
            var account = new Account
            {
                UserName = "新用户",
                DisplayName = "新用户",
                SortOrder = App.Config.Accounts.Count + 1,
                RdpHost = "127.0.0.3",
                RdpPort = 3389,
                RdpUserName = ""
            };

            App.Config.Accounts.Add(account);
            ConfigStore.Save(App.Config);

            // 重建列表并选中新条目 → 右侧展开空白配置
            var row = new UserRow { Account = account };
            _rows.Add(row);
            _editing = row;
            RebuildUserList();

            // 确保右侧是配置页（不是画面页）
            if (_viewing != null) { _viewing = null; ShowConfigPage(); }

            // 焦点放到用户名输入框
            Dispatcher.InvokeAsync(() =>
            {
                NameBox?.Focus();
                NameBox?.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Background);

            LogBus.Info("已新增用户配置，填写后点「保存配置」");
        }

        // ── 画面工具栏 ───────────────────────────────────────────────────────

        private void BtnMode_Click(object sender, RoutedEventArgs e)
        {
            var c = _viewing?.Console;
            if (c == null) return;
            var next = c.InputMode == ConsoleInputMode.FreeLook
                ? ConsoleInputMode.Pointer : ConsoleInputMode.FreeLook;
            var (ok, msg) = c.SetInputMode(next);
            if (!ok) { InputText.Text = msg; return; }
            ApplyModeUi(next);
        }

        private void ApplyModeUi(ConsoleInputMode mode)
        {
            string hk = _viewing?.Console?.ToggleHotkey ?? "Ctrl+Alt+Q";
            if (mode == ConsoleInputMode.FreeLook)
            {
                BtnMode.Style = (Style)FindResource("PrimaryButton");
                ModeLabel.Text = "自由视角中";
                InputText.Text = "鼠标已锁定 · 移动即转视角 · " + hk + " 解锁";
            }
            else
            {
                BtnMode.Style = (Style)FindResource("GhostButton");
                ModeLabel.Text = "自由视角";
                InputText.Text = "";
            }
        }

        private void BtnSens_Click(object sender, RoutedEventArgs e) => ShowGeneralSettings();

        /// <summary>构造/同步期间屏蔽工具栏灵敏度滑块的写回，避免把默认值覆盖掉已保存的设置。</summary>
        private bool _suppressSensSave;

        private void SensSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SensText == null) return;
            double v = Math.Round(e.NewValue, 1);
            SensText.Text = v.ToString("0.0") + "x";
            if (_suppressSensSave) return;

            if (_viewing?.Console != null) _viewing.Console.LookSensitivity = v;
            try { App.Config.Settings.LookSensitivity = v; ConfigStore.Save(App.Config); } catch { }
        }

        private void OnHotkeyRecorded(HotkeySpec? spec)
        {
            if (spec == null) return;
            var c = _viewing?.Console;
            if (c == null) { InputText.Text = "先打开一个用户画面"; return; }
            c.ToggleHotkey = spec.ToString();
            App.Config.Settings.LockHotkey = spec.ToString();
            try { ConfigStore.Save(App.Config); } catch { }
            InputText.Text = "锁定热键 → " + spec.ToString();
        }

        private async void BtnReconnect_Click(object sender, RoutedEventArgs e)
        {
            var row = _viewing;
            if (row == null) return;
            try { row.Console?.Disconnect(); } catch { }
            await ConnectAsync(row);
        }

        /// <summary>
        /// 注销用户并关闭其所有程序。
        /// ★ 这是不可恢复操作，必须二次确认，并且明确告诉用户会丢什么。
        /// </summary>
        private async void BtnLogoff_Click(object sender, RoutedEventArgs e)
        {
            var row = _viewing;
            if (row == null || _logoffInProgress) return;
            _logoffInProgress = true;
            BtnLogoff.IsEnabled = false;

            SessionInfo? s = null;
            try
            {
                var sessions = await SessionService.QueryAsync();
                s = SessionService.Find(sessions, row.Account.UserName);
            }
            catch (Exception ex)
            {
                LogBus.Error("注销前读取会话失败", ex.Message);
            }
            if (s == null)
            {
                // 没有可确认的有效会话时，注销按钮只关闭画面，不执行 logoff。
                CloseViewPage(row);
                _logoffInProgress = false;
                BtnLogoff.IsEnabled = true;
                return;
            }

            var confirm = new LogoffConfirmWindow(row.Account.Caption, row.Account.UserName, s.Id)
            {
                Owner = Window.GetWindow(this)
            };
            if (confirm.ShowDialog() != true)
            {
                _logoffInProgress = false;
                BtnLogoff.IsEnabled = true;
                return;
            }

            ViewOps.IsEnabled = false;
            LogoffTransitionWindow? transition = null;
            try
            {
                // 独立 HWND 才能盖住 RDP 空域；先淡入，再断开和销毁控件，遮住默认白底。
                transition = new LogoffTransitionWindow(ViewportHost, row.Account.Caption, () => ReferenceEquals(_viewing, row));
                await transition.FadeInAsync();

                try { row.Console?.SetInputMode(ConsoleInputMode.Pointer); } catch { }
                try { row.Console?.Disconnect(); } catch { }

                var (ok, msg) = await SessionPrepare.LogoffAsync(s.Id);
                if (!ok) throw new InvalidOperationException(msg);
                LogBus.Ok("已注销 " + row.Account.Caption, msg);

                // 过场仍盖住画面时销毁 RDP，避免露出 WindowsFormsHost 白底。
                _openedIds.Remove(row.Account.Id);
                ViewportHost.Children.Clear();
                try { row.Console?.Dispose(); } catch { }
                row.Console = null;
                if (ReferenceEquals(_viewing, row))
                {
                    _viewing = null;
                    SetBusyVisible(false);
                    ShowConfigPage();
                }
                transition.SetCompleted(row.Account.Caption);
                await Task.Delay(220);
            }
            catch (Exception ex)
            {
                LogBus.Error("注销失败", ex.Message);
                InputText.Text = "注销失败：" + ex.Message;
            }
            finally
            {
                if (transition != null) await transition.FadeOutAndCloseAsync();
                _logoffInProgress = false;
                ViewOps.IsEnabled = true;
                BtnLogoff.IsEnabled = true;
            }

            RebuildUserList();
            RenderTabs();
        }

        private void CloseViewPage(UserRow row)
        {
            try { row.Console?.SetInputMode(ConsoleInputMode.Pointer); } catch { }
            try { row.Console?.Disconnect(); } catch { }
            SetBusyVisible(false);
            _openedIds.Remove(row.Account.Id);
            ViewportHost.Children.Clear();
            try { row.Console?.Dispose(); } catch { }
            row.Console = null;
            if (ReferenceEquals(_viewing, row))
            {
                _viewing = null;
                ShowConfigPage();
            }
            RebuildUserList();
            RenderTabs();
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            if (_viewing == null) return;
            CloseViewPage(_viewing);
        }

        /// <summary>从「在线会话」页加一个用户进来。</summary>
        public async Task AddSessionAsync(SessionInfo session)
        {
            var account = App.Config.Accounts.FirstOrDefault(x =>
                string.Equals(x.UserName, session.UserName, StringComparison.OrdinalIgnoreCase));
            if (account == null)
            {
                account = new Account
                {
                    UserName = session.UserName,
                    RdpUserName = session.UserName,
                    DisplayName = session.UserName,
                    RdpHost = session.Id > 1 && session.Id < 10 ? "127.0.0." + session.Id : "127.0.0.3"
                };
                App.Config.Accounts.Add(account);
                ConfigStore.Save(App.Config);
            }
            Activate();
            var row = _rows.FirstOrDefault(r => r.Account.Id == account.Id);
            if (row != null) { _editing = row; RebuildUserList(); }
        }

        public void RefreshBadges() { RenderTabs(); }

        public void DisposeConsoles()
        {
            foreach (var r in _rows) { try { r.Console?.Dispose(); } catch { } }
            _rows.Clear();
            ViewportHost.Children.Clear();
        }
    }
}
