using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Threading.Tasks;
using SoulLink.Services;

namespace SoulLink.Views
{
    /// <summary>
    /// 数据中心：把账号状态中心的每账号快照直接可视化。
    ///
    /// ★ 用途是**核对**，不是操作：
    ///   自动化跑之前先来这里看一眼，确认"哪个账号 → 哪条会话 → 哪个 BGI → 当前任务"
    ///   都对得上，避免因为状态错位导致操作串到别的账号。
    /// ★ 只读：这个页面不修改任何数据。
    /// </summary>
    public partial class DataCenterPage : UserControl
    {
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        private bool _active;
        private readonly Dictionary<string, string> _sharedSignatures = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>上一轮渲染依据的数据签名；相同就不重建视觉树。</summary>
        private string _lastSignature = "";

        // ══ t2 问题1：米游社摘要「每秒闪一次」═══════════════════════════════
        //
        // 根因（实测确认）：
        //   BuildMiyousheRow 每次都先把 TextBlock 建成「…」，
        //   然后 _ = RefreshMiyousheAsync(status, ...) 异步回来再改 Text；
        //   而页面每秒（或每次数据变化）都要 Rebuild() → Children.Clear() → 重建，
        //   于是每重建一次就重演一遍「… → 已存储 · 12***34」，
        //   用户看到的就是「一会儿省略号、一会儿 uid，一闪一闪」。
        //
        // 修法（三层一起）：
        //   ① 缓存结果：重建时直接用上次的结果渲染，不再先画占位；
        //   ② 只在缓存为空时才发起异步读取（原来每重建一次就多一个在途读取）；
        //   ③ 异步回来后值没变就不碰 UI；值变了也只改那一个 TextBlock，
        //      不整页 Rebuild —— 整页重建本身就是一次可见的重绘。
        /// <summary>账号 → 上次读到的米游社摘要（重建时直接按它渲染）。</summary>
        private readonly Dictionary<string, MiyousheView> _miyoushe
            = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>账号 → 在途读取标记（同一账号同时只允许一次读取）。</summary>
        private readonly HashSet<string> _miyousheLoading = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>t59：每账号的原神角色列表缓存（账号 → 角色 + 默认角色）。</summary>
        private readonly Dictionary<string, (List<MiyousheRole> Roles, string DefaultRole, string Reason)> _miyousheRoles
            = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _miyousheRolesLoading = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>账号 → 当前活在视觉树里的状态文本块（异步回来只改它自己）。</summary>
        private readonly Dictionary<string, TextBlock> _miyousheBlocks
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>米游社摘要的渲染快照：文案 + 是否已存储（决定颜色）+ 是否已拿到真值。</summary>
        private sealed class MiyousheView
        {
            public string Text = "…";
            public bool Stored;
            /// <summary>false = 还只是占位，没读到过真值。</summary>
            public bool Ready;
        }

        // ══ t2 问题2：配置组卡片按组名合并 ═══════════════════════════════════
        /// <summary>已展开历史的配置组 key（重建后保持展开）。</summary>
        private readonly HashSet<string> _expandedGroups = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>今日某个配置组的一次运行（SQLite 与实时增量统一成这个形状）。</summary>
        private sealed class GroupRunView
        {
            public string Name = "";
            public ScriptGroupState State;
            public DateTime StartedAt = DateTime.MinValue;
            public DateTime EndedAt = DateTime.MinValue;
            public int ScriptCount;
            public int Picks;
            public string Detail = "";
            public TimeSpan? Duration
                => EndedAt > StartedAt && StartedAt != DateTime.MinValue
                   ? EndedAt - StartedAt : (TimeSpan?)null;
        }

        /// <summary>同名配置组今日的全部运行（Runs[0] = 最后一次）。</summary>
        private sealed class GroupAggregate
        {
            public string Name = "";
            public List<GroupRunView> Runs = new();
            public GroupRunView Last => Runs[0];
        }

        // ── t52：主程序数据中心改用 SQLite 作为「今日配置组」权威数据源 ──
        //   首帧显示 run.db 的今日记录（历史+当前），Tracker 只负责实时增量，
        //   异步加载、幂等合并（同组名+同时刻去重），UI 线程零 IO。
        private readonly Dictionary<string, IReadOnlyList<SoulLink.Services.RunRecord>> _sqliteTodayRuns
            = new(StringComparer.OrdinalIgnoreCase);
        private bool _sqliteLoading;

        /// <summary>
        /// 把"界面会显示的东西"压成一个字符串。
        /// ★ 只要它没变，重建出来的视觉树和现在完全一样，纯属白烧 CPU。
        /// </summary>
        private string BuildSignature()
        {
            var sb = new System.Text.StringBuilder(512);
            try
            {
                foreach (var a in AccountScope.Accounts(App.Config))
                {
                    var st = AccountStateCenter.Get(a.Id);
                    if (st == null) { sb.Append(a.UserName).Append("|null;"); continue; }
                    sb.Append(a.UserName).Append('|')
                      .Append(st.SessionId).Append(',')
                      .Append(st.SessionState).Append(',')
                      .Append(st.BgiRunning).Append(',')
                      .Append(st.BgiPid).Append(',')
                      .Append(st.Bound).Append(',')
                      .Append(st.CurrentGroup).Append(',')
                      .Append(st.OneDragon).Append(',')
                      .Append(st.Groups.Count).Append(',')
                      .Append(st.ErrorCount).Append(',')
                      .Append(st.WarnCount).Append(',')
                      .Append(st.FailCount).Append(',')
                      .Append(st.DogfoodRoute).Append(',')
                      .Append(st.DogfoodValid).Append(',')
                      .Append(st.BgiPath).Append(';');
                    // ★ 绝对不要把 st.UpdatedAt 放进签名：它在每轮刷新里都会被
                    //   赋成 DateTime.Now，等于签名每秒都变，去重完全失效
                    //   （实测：加了它 CPU 从 13% 反弹到 52%）。
                }
            }
            catch { }
            return sb.ToString();
        }

        public DataCenterPage()
        {
            InitializeComponent();
            Loaded += (_, __) => { _timer.Start(); };
            Unloaded += (_, __) => { _timer.Stop(); };
            _timer.Tick += (_, __) =>
            {
                if (!IsVisible) return;
                SharedDataChanged();
                // ★ 2.17.45：只有数据真的变了才重建视觉树。
                //   原来每秒无条件 Rebuild() —— 整个页面的卡片、边框、文本块
                //   全部丢弃重建，WPF 每次都要重新测量/排版/生成字形，
                //   即使内容一个字都没变。这是纯浪费的 UI 线程开销。
                string sig = BuildSignature();
                if (sig == _lastSignature) return;
                _lastSignature = sig;
                Rebuild();
            };
        }

        public void Activate()
        {
            try { _timer.Start(); } catch { }
            Rebuild();
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AccountStateCenter.RefreshNow(App.Config);
                BgiDataCenter.RefreshAll(AccountScope.Accounts(App.Config).Select(a => a.UserName));
                _sharedSignatures.Clear();
                // ★ t2：用户点「刷新」= 显式要求重读，连米游社缓存一起作废
                //   （正常情况下缓存只增不改，否则又回到「每次重建都重读」的闪烁老路）。
                _miyoushe.Clear();
            } catch { }
            Rebuild();
        }

        // ── 渲染 ─────────────────────────────────────────────────────

        private void Rebuild()
        {
            try
            {
                // ★ 不显示"主用户"（SoulLink 自己运行所在会话的那个账号）：
                //   它不是挂机目标，列出来只会干扰核对。
                // 配置中心以保存的账号为权威：离线、没有独立会话、当前主账号也显示。
                // 在线状态只作为卡片字段，不能决定这个账号是否存在。
                var states = AccountScope.Accounts(App.Config)
                    .Where(a => !string.IsNullOrWhiteSpace(a.UserName))
                    .GroupBy(a => a.UserName, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .Select(a => AccountStateCenter.Get(a.Id) ?? AccountStateCenter.GetByUser(a.UserName)
                        ?? new AccountState { AccountId = a.Id, UserName = a.UserName, Caption = a.Caption })
                    .OrderBy(s => s.Caption, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var refresh = AccountStateCenter.LastRefresh;
                double age = (DateTime.Now - refresh).TotalSeconds;
                SubTitle.Text = states.Count + " 个账号 · 快照"
                    + (refresh == DateTime.MinValue ? "尚未生成" : age.ToString("0.0") + " 秒前更新")
                    + " · 每 " + (AccountStateCenter.RefreshMs / 1000.0).ToString("0.#") + " 秒刷新";

                FootNote.Text = "这里汇总每个账号的运行状态、BGI 路径、配置目录和快捷键。流程编辑器、启动台、更新页使用同一份账号数据。\n"
                    + "更换账号或路径会重新读取对应目录；BGI 的配置文件变更也会自动刷新。";

                // ★ t2：清掉「在树上的米游社状态块」索引 —— 旧块即将被丢弃，
                //   留着只会让异步结果写到一个已脱离视觉树的 TextBlock（既无效又占内存）。
                //   缓存 _miyoushe 本身**不清**：它就是用来跨重建保持值稳定的。
                _miyousheBlocks.Clear();
                CardList.Children.Clear();

                if (states.Count == 0)
                {
                    CardList.Children.Add(new TextBlock
                    {
                        Text = "还没有账号状态。确认配置里有账号，且状态中心已启动。",
                        Margin = new Thickness(4, 10, 4, 10),
                        FontSize = 11.5,
                        Foreground = (Brush)FindResource("TextMuted")
                    });
                    return;
                }

                // ★ t52：异步预载 run.db 今日记录（不阻塞首帧；回来后再刷一次界面）。
                _ = EnsureSqliteTodayRunsAsync(states);

                foreach (var st in states) CardList.Children.Add(BuildCard(st));
            }
            catch (Exception ex)
            {
                SubTitle.Text = "渲染失败：" + ex.Message;
            }
        }

        /// <summary>是否主用户（当前进程所在会话、或 Windows console 会话）。</summary>
        private static bool IsMainUser(AccountState st, int selfSessionId)
        {
            try
            {
                if (selfSessionId > 0 && st.HasSession && st.SessionId == selfSessionId) return true;

                // 兜底：与当前登录用户同名，且没有独立会话
                var cur = SessionService.CurrentUser;
                if (!string.IsNullOrWhiteSpace(cur)
                    && string.Equals(SessionService.BareName(st.UserName),
                                     SessionService.BareName(cur), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
            return false;
        }

        private bool SharedDataChanged()
        {
            bool changed = false;
            foreach (var st in AccountStateCenter.All())
            {
                var d = BgiDataCenter.Get(st.UserName);
                string signature = d.ExePath + "|" + d.CancelTaskHotkey + "|" + d.Error
                    + "|" + string.Join(",",d.OneDragonConfigs) + "|" + string.Join(",",d.ScriptGroups);
                if (_sharedSignatures.TryGetValue(st.UserName,out var old) && old == signature) continue;
                _sharedSignatures[st.UserName] = signature; changed = true;
            }
            return changed;
        }

        private UIElement BuildCard(AccountState st)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ── 标题行：昵称 + 用户名 + 状态灯 ──
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new Ellipse
            {
                Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Fill = st.BgiRunning ? new SolidColorBrush(Color.FromRgb(0x41, 0xF0, 0xC0))
                                     : new SolidColorBrush(Color.FromRgb(0x6B, 0x7C, 0x9C))
            });
            head.Children.Add(new TextBlock
            {
                Text = st.Caption,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TextPrimary")
            });
            if (!string.Equals(st.Caption, st.UserName, StringComparison.OrdinalIgnoreCase))
            {
                head.Children.Add(new TextBlock
                {
                    Text = "（" + st.UserName + "）",
                    FontSize = 11,
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("TextSecondary")
                });
            }
            Grid.SetRow(head,0);
            root.Children.Add(head);

            var columns = new Grid { Margin = new Thickness(0,10,0,0) };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360), MinWidth = 280 });
            Grid.SetRow(columns,1);

            // ── 左侧基本信息 ──
            var grid = new Grid { Margin = new Thickness(0) };
            Grid.SetColumn(grid,0);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 8; i++)   // t60：字段从 11 减到 8（去掉 3 个调试字段）
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            int row = 0;
            AddRow(grid, row++, "会话", st.HasSession
                ? ("#" + st.SessionId + "  " + st.SessionState)
                : "无会话（未登录）");
            AddRow(grid, row++, "BGI", st.BgiRunning
                ? ("运行中  PID " + st.BgiPid)
                : "未运行");
            var shared = BgiDataCenter.Get(st.UserName);
            AddRow(grid, row++, "BGI 路径", shared.ExePath.Length == 0 ? "未设置" : shared.ExePath, mono: true);
            AddRow(grid, row++, "配置目录", shared.RootPath.Length == 0 ? "—" : shared.DirectoryOf("User"), mono: true);
            // ★★ t60：配置组/一条龙**只显示数量**，不再拼接完整名单。
            //   原来 39 个组名铺满 3~4 行，把真正有用的信息（会话/BGI/日志）挤到看不见。
            //   要看完整名单 → 点「配置管理」页，那里是专门的地方。
            AddRow(grid, row++, "配置组", shared.ScriptGroups.Count + " 个" + (shared.ScriptGroups.Count == 0 ? "（未发现）" : ""));
            AddRow(grid, row++, "一条龙", shared.OneDragonConfigs.Count + " 个" + (shared.OneDragonConfigs.Count == 0 ? "（未发现）" : ""));
            AddRow(grid, row++, "停止快捷键", shared.HotkeyText);
            AddRow(grid, row++, "日志文件", string.IsNullOrWhiteSpace(st.BgiLogFile) ? "—"
                : (System.IO.Path.GetFileName(st.BgiLogFile) + "  " + st.BgiLogWrite.ToString("HH:mm:ss")), mono: true);

            // ★★ t60：移除三个**纯调试字段**（用户反馈「多余无用字段」）：
            //   · 「配置读取」（正常 · 02:13:50）—— 只有排查配置读不出时才需要；
            //   · 「任务原文」（截断的日志行）—— 界面看这个没意义，日志里有完整的；
            //   · 「数据中心占用」（70.7 KB / 尾窗 353 行）—— 这是给开发看的内存哨兵，
            //     用户完全看不懂，而且它和「这个账号在干什么」毫无关系。
            //   需要排查时看 设置 → 诊断日志，那里有完整信息。

            columns.Children.Add(grid);
            var hotkeys = BuildHotkeyTable(shared);
            Grid.SetColumn(hotkeys,1);
            columns.Children.Add(hotkeys);
            root.Children.Add(columns);
            var automation = BuildAutomationData(st);
            Grid.SetRow(automation,2);
            root.Children.Add(automation);
            var miyoushe = BuildMiyousheRow(st);
            Grid.SetRow(miyoushe,3);
            root.Children.Add(miyoushe);
            card.Child = root;
            return card;
        }

        /// <summary>
        /// 米游社摘要行（t33 需求1 + t2 修复闪烁）。只放「一行放得下」的摘要 + 操作入口，
        /// 详情全部在网页。
        ///
        /// ★ t2 关键：**先按缓存渲染**，只有缓存为空时才异步读取。
        ///   旧实现每次都先画「…」再异步改，配合每秒整页重建 = 每秒闪一次。
        /// </summary>
        private UIElement BuildMiyousheRow(AccountState st)
        {
            var border = new Border
            {
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(0, 8, 0, 0),
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF))
            };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            panel.Children.Add(new TextBlock
            {
                Text = "米游社",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Foreground = (Brush)FindResource("TextPrimary")
            });

            // ★ t2：有缓存就按缓存值渲染（不先画占位，因此没有「…→uid」的跳变）
            bool cached = _miyoushe.TryGetValue(st.UserName, out var view) && view != null;
            var status = new TextBlock
            {
                Text = cached ? view!.Text : "…",
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = cached && view!.Stored
                    ? new SolidColorBrush(Color.FromRgb(0x6E, 0xF0, 0xC8))
                    : (Brush)FindResource("TextSecondary")
            };
            panel.Children.Add(status);
            // 记下「当前活在视觉树里」的那个块：异步回来只改它，不改整页。
            _miyousheBlocks[st.UserName] = status;

            // 操作按钮（扫码登录 / 一键导入 / 删除凭据 / 查看详情→）
            var btnQr = new Button
            {
                Content = "扫码登录",
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(10, 0, 6, 0),
                FontSize = 10.5
            };
            btnQr.Click += async (_, __) => await OpenQrAsync(st.UserName);
            panel.Children.Add(btnQr);

            var btnImport = new Button
            {
                Content = "一键导入",
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                FontSize = 10.5
            };
            btnImport.Click += async (_, __) => await ImportAsync(st.UserName);
            panel.Children.Add(btnImport);

            var btnDel = new Button
            {
                Content = "删除凭据",
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                FontSize = 10.5
            };
            btnDel.Click += (_, __) =>
            {
                MiyousheCredentialStore.Delete(st.UserName, "cn");
                MiyousheCredentialStore.Delete(st.UserName, "os");
                // 删除后强制重读一次（缓存作废，但**不**整页重建）
                _miyoushe.Remove(st.UserName);
                _ = RefreshMiyousheAsync(st.UserName, force: true);
            };
            panel.Children.Add(btnDel);

            // ★★ t59：**原神角色切换**（用户明确要求「绑定多个原神账号应该可以切换、设为默认」）。
            //   之前只做了网页版（#/miyoushe），主程序这里一行都没有 —— 用户在主程序里完全找不到。
            //   设计：一个 ComboBox 列出该米游社账号绑定的所有原神角色；
            //   选中即调 POST /api/miyoushe/roles 设为默认（持久化到 Settings.MiyousheDefaultRole）。
            var roleBox = new ComboBox
            {
                MinWidth = 170,
                MaxWidth = 240,
                FontSize = 10.5,
                Margin = new Thickness(10, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "该米游社账号绑定的原神角色；选中即设为默认（签到/拉取优先用它）",
                Visibility = Visibility.Collapsed   // 读到角色后再显示
            };
            roleBox.SelectionChanged += async (_, __) =>
            {
                if (roleBox.SelectedItem is ComboBoxItem item && item.Tag is string uid && uid.Length > 0)
                {
                    try
                    {
                        await MiyousheService.SetDefaultRoleAsync(st.UserName, uid, default).ConfigureAwait(true);
                        _miyousheRoles.Remove(st.UserName);       // 作废缓存，下次重读
                        _ = RefreshMiyousheRolesAsync(st.UserName, roleBox);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("设置默认角色失败：" + ex.Message, "米游社",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            };
            panel.Children.Add(roleBox);

            border.Child = panel;

            // 角色列表异步填充（有缓存直接填，不闪）
            if (_miyousheRoles.TryGetValue(st.UserName, out var cachedRoles) && cachedRoles.Roles.Count > 0)
                FillRoleBox(roleBox, cachedRoles.Roles, cachedRoles.DefaultRole);
            else
                _ = RefreshMiyousheRolesAsync(st.UserName, roleBox);
            // ★ t2：只有「还没有缓存」或「强制刷新」时才发起异步读取。
            //   原来无条件 `_ = RefreshMiyousheAsync(status, ...)`：页面每次重建都多发一次
            //   读取，回来又各自改一次 Text —— 闪烁的直接来源。
            if (!cached || !view!.Ready) _ = RefreshMiyousheAsync(st.UserName);
            return border;
        }


        /// <summary>
        /// t59：把角色列表填进 ComboBox。有多个角色才显示，只有一个角色时隐藏（没什么可切换的）。
        /// ★ 填充时先解绑事件，避免程序化选中触发「设为默认」请求。
        /// </summary>
        private void FillRoleBox(ComboBox box, List<MiyousheRole> roles, string defaultRole)
        {
            try
            {
                box.Items.Clear();
                if (roles == null || roles.Count <= 1)
                {
                    box.Visibility = Visibility.Collapsed;   // 只有 0~1 个角色，不需要切换
                    return;
                }

                ComboBoxItem? pick = null;
                foreach (var r in roles)
                {
                    string label = r.Nickname + "  Lv." + r.Level + "  " + r.Region + "  " + r.GameUid;
                    var item = new ComboBoxItem { Content = label, Tag = r.GameUid };
                    box.Items.Add(item);
                    if (!string.IsNullOrEmpty(defaultRole)
                        && string.Equals(r.GameUid, defaultRole, StringComparison.OrdinalIgnoreCase))
                        pick = item;
                }
                // 未设默认时选第一个（只是显示，不触发写入）
                box.SelectedItem = pick ?? box.Items[0];
                box.Visibility = Visibility.Visible;
            }
            catch { }
        }

        /// <summary>
        /// t59：异步读角色并填充。不整页重建（沿用 t2 的不闪烁原则）。
        /// 同一账号只允许一次在途读取。
        /// </summary>
        private async Task RefreshMiyousheRolesAsync(string account, ComboBox box)
        {
            if (string.IsNullOrWhiteSpace(account)) return;
            if (!_miyousheRolesLoading.Add(account)) return;   // 在途去重
            try
            {
                var (roles, reason) = await MiyousheService.GetRolesForUiAsync(account, default).ConfigureAwait(true);
                _miyousheRoles[account] = (roles, MiyousheService.GetDefaultRole(account), reason);
                FillRoleBox(box, roles, MiyousheService.GetDefaultRole(account));
            }
            catch { }
            finally { _miyousheRolesLoading.Remove(account); }
        }

        /// <summary>
        /// 读一次米游社登录状态并写进缓存。
        ///
        /// ★ t2：签名从 (TextBlock, account) 改成 (account, force)。
        ///   旧签名把「改哪个控件」当成参数传进来，导致异步结果只能写死到
        ///   建它时那个 TextBlock —— 页面一重建，那个块已经不在树上了，
        ///   新块又画回占位，于是每重建一次就闪一次。
        ///   现在只更新缓存，然后找**当前**在树上的块改文字；值没变就什么都不做。
        /// </summary>
        private async Task RefreshMiyousheAsync(string account, bool force = false)
        {
            if (string.IsNullOrWhiteSpace(account)) return;
            if (!force && _miyousheLoading.Contains(account)) return;   // 同一账号只允许一次在途读取
            _miyousheLoading.Add(account);
            try
            {
                var st = await MiyousheService.LoginStatusAsync(account, default).ConfigureAwait(true);
                string stateText = st.State switch
                {
                    "stored" => "已存储",
                    "needs_login" => "需重扫",
                    _ => "未登录"
                };
                string uid = st.AidMasked.Length > 0
                    ? st.AidMasked
                    : (st.Uid.Length > 0 ? MiyousheCredentialStore.Mask(st.Uid) : "");
                ApplyMiyousheView(account, stateText + (uid.Length > 0 ? " · " + uid : ""),
                    st.State == "stored", ready: true);
            }
            catch
            {
                // ★ 读取失败**不**覆盖已缓存的好值：网络抖动不该把「已存储」变成「读取失败」，
                //   那同样是一次可见的闪烁。只有从来没读到过才显示失败。
                if (!_miyoushe.TryGetValue(account, out var cachedView) || cachedView == null || !cachedView.Ready)
                    ApplyMiyousheView(account, "状态读取失败", false, ready: true);
            }
            finally { _miyousheLoading.Remove(account); }
        }

        /// <summary>
        /// 把一次读取结果落到缓存，并**只在值变化时**改当前在树上的那个 TextBlock。
        /// ★ 刻意不调用 Rebuild()：整页重建本身是一次全量重绘，正是闪烁的表现形式。
        /// </summary>
        private void ApplyMiyousheView(string account, string text, bool stored, bool ready)
        {
            if (_miyoushe.TryGetValue(account, out var prev) && prev != null
                && prev.Text == text && prev.Stored == stored && prev.Ready == ready)
                return;                                     // 值没变：连缓存都不动

            _miyoushe[account] = new MiyousheView { Text = text, Stored = stored, Ready = ready };

            TextBlock? live = null;
            try { _miyousheBlocks.TryGetValue(account, out live); } catch { }
            if (live == null) return;                       // 卡片不在树上：下次重建自然用缓存
            live.Text = text;
            try
            {
                live.Foreground = stored
                    ? new SolidColorBrush(Color.FromRgb(0x6E, 0xF0, 0xC8))
                    : (Brush)FindResource("TextSecondary");
            }
            catch { }
        }

        private async Task OpenQrAsync(string account)
        {
            try
            {
                var w = new MiyousheQrWindow(account, "cn") { Owner = Window.GetWindow(this) };
                w.ShowDialog();
                // 扫码后凭据可能变了：作废缓存并强制重读（一次性用户动作，不进 1 秒循环）
                _miyoushe.Remove(account);
                _ = RefreshMiyousheAsync(account, force: true);
                Rebuild();
            }
            catch (Exception ex)
            {
                MessageBox.Show("扫码窗口打开失败：" + ex.Message, "米游社", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task ImportAsync(string account)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择 BetterGI 或 ABGI 目录（只读探测）" };
                if (dlg.ShowDialog() != true) return;
                string path = dlg.FolderName;
                var probe = MiyousheImporter.Probe(account, path);
                if (!probe.Ok)
                {
                    MessageBox.Show("未识别到有效凭据：\n" + probe.Detail, "一键导入", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var confirm = MessageBox.Show("探测到 " + probe.OkCount + " 项凭据，是否导入？\n（不会显示 cookie 内容）", "一键导入", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;
                var outcome = await MiyousheImporter.ImportAsync(account, path, true, default).ConfigureAwait(true);
                MessageBox.Show("导入完成：" + outcome.OkCount + " 成功 / " + outcome.FailedCount + " 失败\n" + outcome.Detail, "一键导入", MessageBoxButton.OK, MessageBoxImage.Information);
                // 导入后凭据变了：作废缓存并强制重读
                _miyoushe.Remove(account);
                _ = RefreshMiyousheAsync(account, force: true);
                Rebuild();
            }
            catch (Exception ex)
            {
                MessageBox.Show("导入失败：" + ex.Message, "一键导入", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private UIElement BuildAutomationData(AccountState st)
        {
            var panel = new StackPanel { Margin = new Thickness(0,14,0,0) };
            panel.Children.Add(new TextBlock { Text="自动化使用的数据", FontSize=11.5, FontWeight=FontWeights.SemiBold, Foreground=(Brush)FindResource("TextPrimary"), Margin=new Thickness(0,0,0,7) });
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(360), MinWidth=280 });
            grid.Children.Add(BuildGroupRuns(st));
            var dogfood = BuildDogfood(st); Grid.SetColumn(dogfood,1); grid.Children.Add(dogfood);
            panel.Children.Add(grid); return panel;
        }

        /// <summary>
        /// t52：异步加载各账号「今日」的 run.db 记录（run 表），不阻塞 UI。
        /// 加载完刷新一次（若页面可见）。SQLite 查询走 RunRecordStore 后台队列。
        /// </summary>
        private async System.Threading.Tasks.Task EnsureSqliteTodayRunsAsync(System.Collections.Generic.IReadOnlyList<AccountState> states)
        {
            if (_sqliteLoading) return;
            _sqliteLoading = true;
            try
            {
                foreach (var st in states)
                {
                    if (string.IsNullOrWhiteSpace(st.UserName)) continue;
                    try
                    {
                        var runs = await SoulLink.Services.RunRecordStore.QueryRunsAsync(st.UserName, 1)
                            .ConfigureAwait(true);
                        long today0 = new DateTimeOffset(DateTime.Now.Date).ToUnixTimeMilliseconds();
                        var todayRuns = runs.Where(r => r.StartAtMs >= today0 || (r.EndAtMs ?? r.StartAtMs) >= today0).ToList();
                        _sqliteTodayRuns[st.UserName] = todayRuns;
                    }
                    catch { _sqliteTodayRuns[st.UserName] = Array.Empty<SoulLink.Services.RunRecord>(); }
                }
                await Dispatcher.InvokeAsync(() => { if (IsVisible) { _lastSignature = ""; Rebuild(); } });
            }
            finally { _sqliteLoading = false; }
        }

        /// <summary>t52：本地 DateTime → epoch ms（幂等合并去重用）。</summary>
        private static long ToMsLocal(DateTime dt)
            => dt == DateTime.MinValue ? 0 : new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local)).ToUnixTimeMilliseconds();

        /// <summary>
        /// t52/t2：把 SQLite 的 RunRecord 映射成页面用的运行视图。
        /// ★ t2 起多带 Picks（用户要求卡片显示「拾取」），并保留 Detail 供 tooltip。
        /// </summary>
        private static GroupRunView ToGroupRunView(SoulLink.Services.RunRecord r)
        {
            var state = r.State switch
            {
                "Running" => ScriptGroupState.Running,
                "Finished" => ScriptGroupState.Finished,
                "Failed" => ScriptGroupState.Failed,
                "Cancelled" => ScriptGroupState.Cancelled,
                "Interrupted" => ScriptGroupState.Interrupted,
                _ => ScriptGroupState.Never,
            };
            return new GroupRunView
            {
                Name = r.GroupName ?? "",
                State = state,
                StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.StartAtMs).LocalDateTime,
                EndedAt = r.EndAtMs.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(r.EndAtMs.Value).LocalDateTime
                    : DateTime.MinValue,
                ScriptCount = r.ScriptCount,
                Picks = r.Picks,
                Detail = r.Detail ?? "",
            };
        }

        private UIElement BuildGroupRuns(AccountState st)
        {
            var panel = new StackPanel();

            // ★ 未绑定 BGI 路径：不读任何文件，明确告诉用户"没绑定"而不是"没数据"。
            if (!st.Bound)
            {
                panel.Children.Add(new TextBlock { Text="配置组", FontSize=11, FontWeight=FontWeights.SemiBold, Foreground=(Brush)FindResource("TextPrimary") });
                panel.Children.Add(new TextBlock
                {
                    Text = "未绑定 BGI 路径 · 不读取日志\n请到「设置 → BetterGI 路径」点一次「从进程取」",
                    FontSize=10.5, TextWrapping=TextWrapping.Wrap,
                    Foreground=(Brush)FindResource("TextMuted"), Margin=new Thickness(0,5,0,0)
                });
                return panel;
            }

            // ★ t52：今日配置组改用 SQLite（run.db）为权威数据源，重启后历史不丢、不扫 36MB 日志。
            //   Tracker 只负责「启动后实时增量」，与 SQLite 历史做幂等合并（同名+同时刻去重）。
            // ★ t2：合并后再**按组名折叠**，同一组只出一张卡片（见 BuildGroupAggregates）。
            var merged = new List<GroupRunView>();
            var seen = new HashSet<(string Name, long StartedMs)>();
            if (_sqliteTodayRuns.TryGetValue(st.UserName, out var sqlRuns) && sqlRuns != null)
            {
                foreach (var r in sqlRuns)
                {
                    var v = ToGroupRunView(r);
                    if (IsBogusGroupName(v.Name)) continue;           // 误识别的英文异常消息不进卡片
                    if (seen.Add((v.Name, ToMsLocal(v.StartedAt)))) merged.Add(v);
                }
            }
            foreach (var tr in st.Groups)
            {
                if (IsBogusGroupName(tr.Name)) continue;
                if (seen.Add((tr.Name, ToMsLocal(tr.StartedAt))))
                    merged.Add(new GroupRunView
                    {
                        Name = tr.Name, State = tr.State, StartedAt = tr.StartedAt,
                        EndedAt = tr.EndedAt, ScriptCount = tr.ScriptCount, Detail = tr.Detail ?? "",
                    });
            }
            var runs = merged.OrderByDescending(x => x.StartedAt).ToList();
            var groups = BuildGroupAggregates(runs);
            string groupsKey = st.UserName;

            // 「当前执行组」：优先取 SQLite 的 Running 记录（重启后 Tracker 白纸也能正确显示）；
            //   SQLite 无 Running 时回退 Tracker 的 CurrentGroup。
            string currentFromSql = "";
            if (_sqliteTodayRuns.TryGetValue(st.UserName, out var sr2))
            {
                var running = sr2?.FirstOrDefault(r => r.State == "Running");
                if (running != null) currentFromSql = running.GroupName;
            }
            string currentGroup = currentFromSql.Length > 0 ? currentFromSql : st.CurrentGroup;
            if (IsBogusGroupName(currentGroup)) currentGroup = "";          // 当前组也可能是误识别值
            // ★ t2：标题按「按组名合并后的卡片数」报，括号里补今日运行总次数，
            //   这样「22 个组 / 79 次运行」一眼可见，不再是 79 张看起来一样的卡片。
            string head = "今日配置组（" + groups.Count + " 个 · " + runs.Count + " 次运行）"
                + (_sqliteTodayRuns.ContainsKey(st.UserName) ? " · SQLite" : "");
            if (currentGroup.Length > 0)
                head += " · 当前：" + currentGroup
                     + (st.CurrentGroupAt == DateTime.MinValue ? "" : "（" + st.CurrentGroupAt.ToString("HH:mm:ss") + "）");
            else if (st.StoppedAt != DateTime.MinValue)
                head += " · 已停止"
                     + (st.LastGroup.Length > 0 ? "（上一个：" + st.LastGroup + "）" : "")
                     + "（" + st.StoppedAt.ToString("HH:mm:ss") + "）";
            panel.Children.Add(new TextBlock { Text=head, FontSize=11, FontWeight=FontWeights.SemiBold, TextWrapping=TextWrapping.Wrap, Foreground=(Brush)FindResource("TextPrimary") });
            panel.Children.Add(new TextBlock { Text="来源：run.db（今日运行记录）" + (st.GroupsSource.Length>0 ? " · 实时增量 " + st.GroupsUpdatedAt.ToString("HH:mm:ss") : ""), FontSize=9.5, TextWrapping=TextWrapping.Wrap, Foreground=(Brush)FindResource("TextMuted"), Margin=new Thickness(0,3,0,5) });
            if (groups.Count == 0)
            { panel.Children.Add(new TextBlock { Text="今日暂无配置组运行记录", FontSize=10.5, Foreground=(Brush)FindResource("TextMuted") }); return panel; }
            const int columns = 3;
            var grid = new Grid();
            for (int i=0;i<columns;i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
            for (int i=0;i<(groups.Count+columns-1)/columns;i++) grid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            for (int i=0;i<groups.Count;i++)
            {
                var item = BuildGroupCard(groups[i], groupsKey);
                Grid.SetRow(item,i/columns); Grid.SetColumn(item,i%columns); grid.Children.Add(item);
            }
            panel.Children.Add(grid); return panel;
        }

        /// <summary>
        /// 把「今日运行列表」按组名折叠：同名只留一张卡片，Runs 按时间倒序（[0] = 最后一次）。
        /// ★ t2 用户诉求：「配置组卡片太多了，能不能合并或者展示最后状态？」
        ///   实测 22 个组 79 次运行（其中一个组跑 44 次），合并后 79 张 → 22 张。
        /// </summary>
        private static List<GroupAggregate> BuildGroupAggregates(List<GroupRunView> runsDesc)
        {
            var map = new Dictionary<string, GroupAggregate>(StringComparer.OrdinalIgnoreCase);
            var order = new List<GroupAggregate>();
            foreach (var run in runsDesc)
            {
                if (string.IsNullOrWhiteSpace(run.Name)) continue;
                if (!map.TryGetValue(run.Name, out var agg))
                {
                    agg = new GroupAggregate { Name = run.Name };
                    map[run.Name] = agg;
                    order.Add(agg);
                }
                agg.Runs.Add(run);
            }
            // 卡片顺序：按「最后一次运行时间」倒序（最活跃/最新的组在最上面）
            order.Sort((a,b) => b.Last.StartedAt.CompareTo(a.Last.StartedAt));
            return order;
        }

        /// <summary>
        /// 过滤被当成配置组名的框架异常消息（**展示层兜底**）。
        /// ★ 采集层 RunRecordCollector.ValidGroup 已经拦了，这里再拦一次是因为
        ///   run.db 里**历史已有**这些行（采集不回头删库）；用户看到的界面不能有它们。
        /// </summary>
        private static bool IsBogusGroupName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;   // 空名交给上层处理（不额外吞掉）
            string n = name.Trim();
            if (n.Contains("Exception", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.Contains("was canceled", StringComparison.OrdinalIgnoreCase)
                || n.Contains("was cancelled", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 单张「按组名合并」的配置组卡片：
        ///   组名 + 今日 N 次 · 最后一次 <状态> / 起止时刻 / 时长 / 脚本数 / 拾取
        ///   + 点击展开该组今日全部运行（可选项，用户要求「展开看历史」）。
        /// </summary>

        /// <summary>
        /// t60：构建「分组卡片的内容」（组名 + 摘要 + 可选的历史列表）。
        /// ★ 单独提出来是为了**只重建这一张卡片**，而不是整页 Rebuild ——
        ///   旧实现点展开时整页 Children.Clear()，把米游社的 ComboBox 也销毁了，
        ///   导致下拉框选不中、视觉上「展开又收回」。
        /// </summary>
        private List<UIElement> BuildGroupCardChildren(GroupAggregate agg, string accountKey)
        {
            var last = agg.Last;
            var (accent, _) = GroupColors(last.State);
            string state = StateText(last.State);
            string times = TimeText(last);
            string meta = "今日 " + agg.Runs.Count + " 次 · 最后一次 " + state
                + (times.Length > 0 ? " · " + times : "")
                + (last.ScriptCount > 0 ? " · " + last.ScriptCount + " 脚本" : "")
                + (last.Duration.HasValue ? " · " + FmtDur(last.Duration.Value) : "")
                + (last.Picks > 0 ? " · 拾取 " + last.Picks : "");

            var list = new List<UIElement>();
            list.Add(new TextBlock
            {
                Text = agg.Name, FontSize = 10.5, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, Foreground = accent
            });
            list.Add(new TextBlock
            {
                Text = meta, FontSize = 9.5, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0), Foreground = (Brush)FindResource("TextSecondary")
            });
            string key = accountKey + "\u001f" + agg.Name;
            if (_expandedGroups.Contains(key) && agg.Runs.Count > 1)
                list.Add(BuildGroupHistory(agg));
            return list;
        }

        private Border BuildGroupCard(GroupAggregate agg, string accountKey)
        {
            var last = agg.Last;
            var (accent, fill) = GroupColors(last.State);

            var stack = new StackPanel();
            foreach (UIElement child in BuildGroupCardChildren(agg, accountKey)) stack.Children.Add(child);

            string key = accountKey + "\u001f" + agg.Name;
            bool expanded = _expandedGroups.Contains(key);
            var card = new Border
            {
                Padding = new Thickness(8,5,8,5), Margin = new Thickness(0,3,7,3),
                Background = fill, BorderBrush = accent, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                ToolTip = last.Detail.Length > 0 ? last.Detail : (agg.Runs.Count > 1 ? "点击展开今日全部运行" : null),
                Child = stack
            };
            card.Cursor = System.Windows.Input.Cursors.Hand;
            // ★★ t60 修复（展开后自动收起 + 无法操作下拉框）：
            //
            //   旧实现：点击 → `_lastSignature = ""` + `Rebuild()`（整页 Children.Clear + 重建）。
            //   后果有两个：
            //     ① 整页重建会销毁**所有**卡片（包括米游社那行的 ComboBox），
            //        所以下拉框刚选完就被销毁重建 → 选不中、闪一下、看似「自动收起」；
            //     ② 重建期间滚动位置/鼠标捕获全部丢失，视觉上就是「点开又收回去了」。
            //
            //   新实现：**只更新这一张卡片自己的 Child**，不碰整页。
            //   展开态仍记在 _expandedGroups 里（跨 Rebuild 保持），但展开动作本身不触发 Rebuild。
            card.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;   // 阻止冒泡到上层（原来可能被父级再处理一次 = 立刻又折回去）
                bool nowExpanded = _expandedGroups.Add(key);
                if (!nowExpanded) _expandedGroups.Remove(key);
                try
                {
                    // 重建这张卡片的内容，而不是整页
                    var fresh = BuildGroupCardChildren(agg, accountKey);
                    if (card.Child is Panel oldPanel)
                    {
                        oldPanel.Children.Clear();
                        foreach (UIElement child in fresh) oldPanel.Children.Add(child);
                    }
                }
                catch
                {
                    // 万一卡片结构异常，退回整页重建（保证至少能用）
                    _lastSignature = "";
                    Rebuild();
                }
            };

            if (expanded && agg.Runs.Count > 1)
                stack.Children.Add(BuildGroupHistory(agg));

            if (last.State == ScriptGroupState.Running)
            {
                card.RenderTransformOrigin = new Point(0.5,0.5);
                card.RenderTransform = new ScaleTransform(1,1);
                var pulse = new System.Windows.Media.Animation.DoubleAnimation
                { From=0.82, To=1, Duration=TimeSpan.FromMilliseconds(900), AutoReverse=true, RepeatBehavior=System.Windows.Media.Animation.RepeatBehavior.Forever };
                card.BeginAnimation(OpacityProperty,pulse);
            }
            return card;
        }

        /// <summary>展开后的历史列表：该组今日每一次运行一行（新的在上）。</summary>
        private UIElement BuildGroupHistory(GroupAggregate agg)
        {
            var list = new StackPanel { Margin = new Thickness(0,5,0,0) };
            list.Children.Add(new TextBlock
            {
                Text = "今日全部运行（" + agg.Runs.Count + " 次）",
                FontSize = 9.5, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(0,0,0,2)
            });
            int shown = Math.Min(agg.Runs.Count, 40);          // 兜底：单组最多列 40 行
            for (int i = 0; i < shown; i++)
            {
                var r = agg.Runs[i];
                string line = TimeText(r) + " · " + StateText(r.State)
                    + (r.Duration.HasValue ? " · " + FmtDur(r.Duration.Value) : "")
                    + (r.Picks > 0 ? " · 拾取 " + r.Picks : "");
                list.Children.Add(new TextBlock
                {
                    Text = line, FontSize = 9.5, TextWrapping = TextWrapping.Wrap,
                    Foreground = GroupColors(r.State).accent
                });
            }
            if (agg.Runs.Count > shown)
                list.Children.Add(new TextBlock
                {
                    Text = "… 另有 " + (agg.Runs.Count - shown) + " 次未列出",
                    FontSize = 9.5, Foreground = (Brush)FindResource("TextMuted")
                });
            return list;
        }

        private static string StateText(ScriptGroupState state) => state switch
        {
            ScriptGroupState.Running => "执行中",
            ScriptGroupState.Finished => "已完成",
            ScriptGroupState.Failed => "失败",
            ScriptGroupState.Cancelled => "已取消",
            ScriptGroupState.Interrupted => "已中断",
            _ => "未运行",
        };

        private static string TimeText(GroupRunView run)
        {
            string start = run.StartedAt == DateTime.MinValue ? "" : run.StartedAt.ToString("HH:mm:ss");
            string end = run.EndedAt == DateTime.MinValue ? "" : run.EndedAt.ToString("HH:mm:ss");
            if (start.Length == 0) return end;
            return end.Length > 0 ? start + " → " + end : start;
        }

        private static string FmtDur(TimeSpan d)
            => d.TotalHours >= 1 ? ((int)d.TotalHours) + ":" + d.ToString(@"mm\:ss") : d.ToString(@"mm\:ss");

        private static (Brush accent, Brush fill) GroupColors(ScriptGroupState state)
        {
            return state switch
            {
                ScriptGroupState.Running => (new SolidColorBrush(Color.FromRgb(0x4F,0xE3,0xFF)),new SolidColorBrush(Color.FromArgb(0x18,0x4F,0xE3,0xFF))),
                ScriptGroupState.Finished => (new SolidColorBrush(Color.FromRgb(0x6E,0xF0,0xC8)),new SolidColorBrush(Color.FromArgb(0x14,0x41,0xF0,0xC0))),
                ScriptGroupState.Failed => (new SolidColorBrush(Color.FromRgb(0xFF,0x9B,0x9B)),new SolidColorBrush(Color.FromArgb(0x1C,0xFF,0x7B,0x7B))),
                ScriptGroupState.Cancelled or ScriptGroupState.Interrupted => (new SolidColorBrush(Color.FromRgb(0xE7,0xC6,0x76)),new SolidColorBrush(Color.FromArgb(0x12,0xE7,0xC6,0x76))),
                _ => (new SolidColorBrush(Color.FromRgb(0x7A,0x8C,0xB4)),new SolidColorBrush(Color.FromArgb(0x10,0xFF,0xFF,0xFF)))
            };
        }

        private UIElement BuildDogfood(AccountState st)
        {
            var panel = new StackPanel { Margin=new Thickness(20,0,0,0) };
            // ★ 狗粮保持 04:00 业务日（它读的是独立 records 文件，不随日志滚动）；
            //   原来这里还取了一份 00:00 视图做对照，现在日志侧统一 00:00、
            //   狗粮侧统一 04:00，双视图已无意义，去掉后少一份缓存。
            var rec = DogfoodRecordReader.ForDay(
                st.DogfoodRecords.FirstOrDefault(r => DogfoodRecordReader.ForDay(r, DateTime.Now, 4).IsValid)
                    ?? st.DogfoodRecords.FirstOrDefault() ?? new DogfoodRecord(),
                DateTime.Now, 4);
            panel.Children.Add(new TextBlock { Text="狗粮记录 · 日界 04:00", FontSize=11, FontWeight=FontWeights.SemiBold, Foreground=(Brush)FindResource("TextPrimary") });
            string route = rec.Route switch { DogfoodEndingRoute.EndingA=>"A：走联机",DogfoodEndingRoute.EndingB=>"B：自己跑",_=>"无有效记录" };
            string dayKey = rec.BusinessDay == DateTime.MinValue ? "—" : rec.BusinessDay.ToString("yyyy-MM-dd");
            panel.Children.Add(new TextBlock
            {
                Text = "业务日 " + dayKey + "：" + (rec.IsValid ? "有效" : "不可用") + " · " + route
                     + "\n日期：" + (rec.Date.Length == 0 ? "—" : rec.Date)
                     + "\n经验：" + rec.Exp.ToString("N0") + " · 摩拉：" + rec.Mora.ToString("N0")
                     + "\n" + (rec.Reason.Length == 0 ? "记录读取正常" : rec.Reason)
                     + "\n今日日志统计（00:00 日界）：经验 " + st.Today.DogfoodExp.ToString("N0")
                     + " / 摩拉 " + st.Today.DogfoodMora.ToString("N0")
                     + " / 拾取 " + st.Today.PickTotal.ToString("N0") + " 件"
                     + "\n读取：" + (st.DogfoodReadAt == DateTime.MinValue ? "—" : st.DogfoodReadAt.ToString("HH:mm:ss"))
                     + "\n" + st.DogfoodRecordPath,
                FontSize = 10.5, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextSecondary"), Margin = new Thickness(0, 5, 0, 0)
            });
            return panel;
        }

        private UIElement BuildHotkeyTable(BgiAccountData data)
        {
            var panel = new StackPanel { Margin = new Thickness(20,0,0,0) };
            panel.Children.Add(new TextBlock { Text="快捷键", FontSize=11, FontWeight=FontWeights.SemiBold, Foreground=(Brush)FindResource("TextPrimary"), Margin=new Thickness(0,0,0,5) });
            var entries = data.DisplayHotkeys.Where(x => x.Name.Length > 0 && x.Key.Length > 0).OrderBy(x => x.Name,StringComparer.OrdinalIgnoreCase).ToList();
            if (entries.Count == 0)
            {
                panel.Children.Add(new TextBlock { Text="没有已设置的快捷键", FontSize=10.5, Foreground=(Brush)FindResource("TextMuted") });
                return panel;
            }
            var table = new Grid();
            table.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(150) });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
            for (int i=0;i<entries.Count;i++)
            {
                table.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
                var name = new TextBlock { Text=entries[i].Name, FontSize=10.5, Foreground=(Brush)FindResource("TextSecondary"), Margin=new Thickness(0,3,8,3) };
                var key = new TextBlock { Text=entries[i].Key, FontSize=11, FontWeight=FontWeights.SemiBold, Foreground=(Brush)FindResource("TextPrimary"), Margin=new Thickness(0,3,0,3) };
                Grid.SetRow(name,i); Grid.SetRow(key,i); Grid.SetColumn(key,1);
                table.Children.Add(name); table.Children.Add(key);
            }
            panel.Children.Add(table); return panel;
        }

        private static string HotkeyName(string key) => key switch
        {
            "cancelTaskHotkey" => "停止当前脚本",
            "bgiEnabledHotkey" => "启动或暂停 BGI",
            "autoPickEnabledHotkey" => "自动拾取",
            "autoSkipEnabledHotkey" => "自动跳过剧情",
            "autoSkipHangoutEnabledHotkey" => "自动跳过邀约",
            "autoWoodHotkey" => "自动伐木",
            "enhanceArtifactHotkey" => "强化圣遗物",
            "quickBuyHotkey" => "快捷购买",
            "oneKeyClaimRewardHotkey" => "一键领取奖励",
            "quickSereniteaPotHotkey" => "快捷尘歌壶",
            "quickTeleportEnabledHotkey" => "快捷传送",
            "quickTeleportTickHotkey" => "连续传送",
            "skillCdEnabledHotkey" => "技能冷却提示",
            "takeScreenshotHotkey" => "截图",
            "recognitionTemplateEditorHotkey" => "识别模板编辑器",
            "turnAroundHotkey" => "原地转圈",
            "clickGenshinConfirmButtonHotkey" => "点击确认按钮",
            "clickGenshinCancelButtonHotkey" => "点击取消按钮",
            "oneKeyFightHotkey" => "一键战斗",
            "mapPosRecordHotkey" => "记录地图位置",
            "autoMusicGameHotkey" => "自动音游",
            "autoFishingGameHotkey" => "自动钓鱼",
            "autoCookGameHotkey" => "自动烹饪",
            _ => "其它功能"
        };

        /// <summary>
        /// 从日志原文里取出最后一条"看起来像任务启动"的行。
        /// ★ 只做展示，不做语义归类 —— BGI 的任务格式随配置组作者和版本变化，
        ///   归类必然出错。真正需要判断的地方请自行匹配 RecentLog。
        /// </summary>
        private static string ExtractTaskLine(AccountState st)
        {
            try
            {
                var log = st.RecentLog;
                if (string.IsNullOrEmpty(log)) return "—";

                // 常见锚点（不追求穷举，只是取一行给人看）
                string[] anchors =
                {
                    "开始执行地图追踪任务", "选用配队", "开始执行JS脚本",
                    "锄地一条龙任务启动", "配对界面切换角色任务启动",
                    "开始执行独立任务", "配置组 \"", "开始执行路线", "开始第"
                };

                var lines = log.Split('\n');
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string ln = lines[i].Trim();
                    if (ln.Length == 0) continue;
                    foreach (var a in anchors)
                        if (ln.IndexOf(a, StringComparison.Ordinal) >= 0)
                            return TrimNoise(ln);
                }
                return "—";
            }
            catch { return "—"; }
        }

        /// <summary>去掉日志行前缀（时间戳、级别、命名空间），只留正文。</summary>
        private static string TrimNoise(string line)
        {
            try
            {
                // [21:52:52.484] [INF] [Primary:S2:P...] Namespace.Class
                int cut = line.LastIndexOf(']');
                if (cut >= 0 && cut + 1 < line.Length)
                {
                    string rest = line.Substring(cut + 1).Trim();
                    // 有些行后面还跟着命名空间，去掉它
                    int sp = rest.LastIndexOf("BetterGenshinImpact", StringComparison.Ordinal);
                    if (sp >= 0 && sp + 20 < rest.Length) rest = rest.Substring(sp + 20).Trim();
                    if (rest.Length > 0) return rest.Length > 110 ? rest.Substring(0, 110) + "…" : rest;
                }
                return line.Length > 110 ? line.Substring(0, 110) + "…" : line;
            }
            catch { return line; }
        }

        private static int CountLines(string? s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int n = 0;
            foreach (var c in s) if (c == '\n') n++;
            return n + 1;
        }

        /// <summary>把字节数格式化成易读形式。</summary>
        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.00") + " MB";
        }

        private void AddRow(Grid grid, int rowIdx, string label, string value, bool mono = false)
        {
            var lab = new TextBlock
            {
                Text = label,
                FontSize = 10.5,
                Margin = new Thickness(0, 2, 0, 2),
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = (Brush)FindResource("TextSecondary")
            };
            Grid.SetRow(lab, rowIdx);
            Grid.SetColumn(lab, 0);
            grid.Children.Add(lab);

            var val = new TextBlock
            {
                Text = value,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 2),
                VerticalAlignment = VerticalAlignment.Top,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextPrimary")
            };
            if (mono) val.FontFamily = (FontFamily)FindResource("MonoFont");
            Grid.SetRow(val, rowIdx);
            Grid.SetColumn(val, 1);
            grid.Children.Add(val);
        }
    }
}
