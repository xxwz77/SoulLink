using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
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
            for (int i = 0; i < 11; i++)
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
            AddRow(grid, row++, "配置组", shared.ScriptGroups.Count + " 个：" + (shared.ScriptGroups.Count == 0 ? "未发现" : string.Join("、", shared.ScriptGroups)));
            AddRow(grid, row++, "一条龙", shared.OneDragonConfigs.Count + " 个：" + (shared.OneDragonConfigs.Count == 0 ? "未发现" : string.Join("、", shared.OneDragonConfigs)));
            AddRow(grid, row++, "停止快捷键", shared.HotkeyText);
            AddRow(grid, row++, "配置读取", shared.Error.Length > 0 ? shared.Error : (shared.ConfigExists ? "正常" : "尚未读取") + " · " + shared.ReadAt.ToString("HH:mm:ss"));
            AddRow(grid, row++, "日志文件", string.IsNullOrWhiteSpace(st.BgiLogFile) ? "—"
                : (System.IO.Path.GetFileName(st.BgiLogFile) + "  " + st.BgiLogWrite.ToString("HH:mm:ss")), mono: true);
            AddRow(grid, row++, "任务原文", ExtractTaskLine(st), mono: true);

            // ── 数据中心占用（回归哨兵：改造后应稳定在百 KB 级）──
            //   ★ 2.17.45 起不再保留日志原文，这里改成数据中心常驻量；
            //     如果这个数字开始涨到 MB 级，说明尾窗预算失守。
            long centerChars = BgiLogCenter.ResidentChars();
            long centerBytes = centerChars * 2L;
            AddRow(grid, row++, "数据中心占用",
                (st.Bound ? "已绑定" : "未绑定（不读日志）")
                + "  ·  " + FormatSize(centerBytes) + "（全部账号）"
                + "  ·  尾窗 " + CountLines(st.RecentLog) + " 行"
                + "  ·  " + (st.RecentLogAt == DateTime.MinValue
                    ? "未采集"
                    : st.RecentLogAt.ToString("HH:mm:ss") + " 更新"),
                mono: true);

            columns.Children.Add(grid);
            var hotkeys = BuildHotkeyTable(shared);
            Grid.SetColumn(hotkeys,1);
            columns.Children.Add(hotkeys);
            root.Children.Add(columns);
            var automation = BuildAutomationData(st);
            Grid.SetRow(automation,2);
            root.Children.Add(automation);
            card.Child = root;
            return card;
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

            // ★ 日志按天滚动，日界硬性 00:00（用户确认；狗粮仍保留 04:00）。
            var runs = st.Groups.OrderByDescending(x => x.StartedAt).ToList();
            string head = "今日配置组（" + runs.Count + " 个）";
            if (st.CurrentGroup.Length > 0)
                head += " · 当前：" + st.CurrentGroup
                     + (st.CurrentGroupAt == DateTime.MinValue ? "" : "（" + st.CurrentGroupAt.ToString("HH:mm:ss") + "）");
            else if (st.StoppedAt != DateTime.MinValue)
                // ★ 日志里已经出现停止锚点（执行结束 / 未成功完成 / 取消 /
                //   停止快捷键）→ 明确显示"已停止"，不再挂着上一个组名。
                head += " · 已停止"
                     + (st.LastGroup.Length > 0 ? "（上一个：" + st.LastGroup + "）" : "")
                     + "（" + st.StoppedAt.ToString("HH:mm:ss") + "）";
            panel.Children.Add(new TextBlock { Text=head, FontSize=11, FontWeight=FontWeights.SemiBold, TextWrapping=TextWrapping.Wrap, Foreground=(Brush)FindResource("TextPrimary") });
            panel.Children.Add(new TextBlock { Text=st.GroupsSource.Length==0?"尚未读取日志":"来源："+st.GroupsSource+" · 更新 "+st.GroupsUpdatedAt.ToString("HH:mm:ss"), FontSize=9.5, TextWrapping=TextWrapping.Wrap, Foreground=(Brush)FindResource("TextMuted"), Margin=new Thickness(0,3,0,5) });
            if (runs.Count == 0)
            { panel.Children.Add(new TextBlock { Text=st.GroupsReadError.Length>0?st.GroupsReadError:"今日日志中尚未识别到配置组", FontSize=10.5, Foreground=(Brush)FindResource("TextMuted") }); return panel; }
            const int columns = 3;
            var grid = new Grid();
            for (int i=0;i<columns;i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
            for (int i=0;i<(runs.Count+columns-1)/columns;i++) grid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            for (int i=0;i<runs.Count;i++)
            {
                var run = runs[i];
                string state = run.State switch { ScriptGroupState.Running=>"执行中",ScriptGroupState.Finished=>"已完成",ScriptGroupState.Failed=>"失败",ScriptGroupState.Cancelled=>"已取消",ScriptGroupState.Interrupted=>"已中断",_=>"未运行" };
                string times = (run.StartedAt==DateTime.MinValue?"":run.StartedAt.ToString("HH:mm:ss")) + (run.EndedAt==DateTime.MinValue?"":" → "+run.EndedAt.ToString("HH:mm:ss"));
                string detail = state + (times.Length>0?" · "+times:"") + (run.ScriptCount>0?" · "+run.ScriptCount+" 脚本":"") + (run.Duration.HasValue?" · "+run.Duration.Value.ToString(@"hh\:mm\:ss"):"");
                var (accent, fill) = GroupColors(run.State);
                var item = new Border { Padding=new Thickness(8,5,8,5), Margin=new Thickness(0,3,7,3), Background=fill, BorderBrush=accent, BorderThickness=new Thickness(1), CornerRadius=new CornerRadius(6), ToolTip=run.Detail.Length>0?run.Detail:null };
                item.Child = new TextBlock { Text=run.Name+"\n"+detail, FontSize=10, TextWrapping=TextWrapping.Wrap, Foreground=accent };
                if (run.State == ScriptGroupState.Running)
                {
                    item.RenderTransformOrigin = new Point(0.5,0.5);
                    item.RenderTransform = new ScaleTransform(1,1);
                    var pulse = new System.Windows.Media.Animation.DoubleAnimation
                    { From=0.82, To=1, Duration=TimeSpan.FromMilliseconds(900), AutoReverse=true, RepeatBehavior=System.Windows.Media.Animation.RepeatBehavior.Forever };
                    item.BeginAnimation(OpacityProperty,pulse);
                }
                Grid.SetRow(item,i/columns); Grid.SetColumn(item,i%columns); grid.Children.Add(item);
            }
            panel.Children.Add(grid); return panel;
        }

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
