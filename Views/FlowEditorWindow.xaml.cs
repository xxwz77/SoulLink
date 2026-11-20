using System;
using System.Collections.Generic;
using System.IO;
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
    public partial class FlowEditorWindow : Window
    {
        private readonly AutomationFlow _flow;
        private readonly Account _account;
        private readonly AppConfig _cfg;
        private readonly bool _isNew;
        private bool _ready;
        private bool _dirty;
        private readonly System.Windows.Threading.DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private bool _previewBusy;
        private bool _suppressTodayToggle;
        private int _previewVersion;

        public FlowEditorWindow(AutomationFlow flow, Account account, AppConfig cfg,
                                bool isNew = false)
        {
            InitializeComponent();
            _isNew = isNew;
            _flow = flow;
            _account = account;
            _cfg = cfg;

            HeaderText.Text = account.Caption + " 的自动化";
            SubHeader.Text = "条件满足时执行动作；连接方式决定多个条件如何组合";
            RefreshDailyState();
            NameBox.Text = flow.Name;
            EnabledCheck.IsChecked = flow.Enabled;
            _flow.EnsurePlcModel();
            if (_flow.MigrationNotes.Count > 0) { OnCanvasNotice(string.Join("；", _flow.MigrationNotes)); _dirty = true; }

            BuildLibrary();
            BuildHelp();

            Canvas.NodeSelected += OnNodeSelected;
            Canvas.Changed += () =>
            {
                _dirty = true;
                _previewVersion++;
                RefreshCondModeRow();
                RefreshValidateHint();
                _ = RefreshConditionPreview();
            };
            Canvas.NodeDeleteRequested += OnNodeDelete;
            BgiDataCenter.AccountDataChanged += OnSharedBgiDataChanged;
            Closed += (_, __) => BgiDataCenter.AccountDataChanged -= OnSharedBgiDataChanged;
            Canvas.Notice += OnCanvasNotice;
            Canvas.PaintStatusChanged += PaintStatus_Changed;
            FlowRunner.Scanned += OnFlowScanned;
            Closed += (_, __) => FlowRunner.Scanned -= OnFlowScanned;
            _previewTimer.Tick += async (_, __) => await RefreshConditionPreview();
            Loaded += async (_, __) => { _previewTimer.Start(); await RefreshConditionPreview(); };
            Unloaded += (_, __) => _previewTimer.Stop();
            Closed += (_, __) => _previewTimer.Stop();
            Closed += (_, __) => EndGuide();

            RefreshCondModeRow();

            _ready = true;

            // ★★ 2.17.25：新建流程默认给一个**能自己触发**的触点 ★★
            //
            //   原来新建的空流程什么都不种，用户在画布上手动加的第一个触点
            //   往往就是「手动」——于是"新建之后必须点一下才动"（用户实测反馈）。
            //   用户要求："新建跟重启就要自动跑，除非用户暂停或者删除"。
            //
            //   这里给一个**「定时·间隔」**触点打底（不连任何动作，不产生副作用）。
            //
            //   ★ 为什么不用「会话在线」：它依赖 AccountStateCenter 的会话快照，
            //     账号没登录 / 快照还没起来时恒为 false，新建流程会"看起来又不动了"
            //     （探针实测：只有触发条件为 false，动作永远不执行）。
            //     而「定时·间隔」是纯本地计时，只要窗口开着就一定会周期触发，
            //     不依赖会话/BGI/日志任何外部状态 —— 这才是"新建就能自己跑"的可靠底座。
            //
            //   用户要改类型可以直接在画布里换，不想让它跑就点「自动运行」关掉或删除流程。
            if (_isNew && flow.Nodes.Count == 0)
            {
                var seed = new FlowNode
                {
                    Kind = FlowNodeKind.Condition,
                    Type = FlowNodeType.TrigInterval,
                    Logic = FlowLogicOp.LD,
                    X = 260, Y = 150
                };
                seed.Set("user", account.UserName ?? "");
                seed.Set("intervalSeconds", "5");
                flow.Nodes.Add(seed);
                _dirty = true;
            }

            Canvas.Load(flow);
            UpdateEmptyHint();

            // 首次打开时播放引导；用户可勾选“不再弹出”，之后仍可从使用说明重播。
            Loaded += (_, __) => ShowFlowGuide(force: false);

        }

        /// <summary>
        /// 连线操作反馈。
        /// ★ 用户要求"改完要说清楚发生了什么"：连线被顶替、被拒绝都要看得见，
        ///   否则用户盯着一条没反应的线，会以为是画布坏了。
        /// </summary>
        private string _operationNotice = "";
        /// <summary>画笔状态显示在画布浮层，不占用底部校验提示行。</summary>
        private void PaintStatus_Changed(string text)
        {
            if (PaintBar == null) return;
            PaintBar.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
            if (PaintBarText != null) PaintBarText.Text = text ?? "";
        }
        private void OnCanvasNotice(string text)
        {
            _operationNotice = text ?? "";
            ShowHintText();
            _noticeTimer?.Stop();
            _noticeTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _noticeTimer.Tick -= NoticeTick;
            _noticeTimer.Tick += NoticeTick;
            _noticeTimer.Start();
        }
        private System.Windows.Threading.DispatcherTimer? _noticeTimer;
        private void NoticeTick(object? sender, EventArgs e)
        {
            _noticeTimer?.Stop(); _operationNotice = ""; RefreshValidateHint();
        }
        private void ShowHintText()
        {
            if (ValidateHint == null) return;
            var view = ValidateHint.Content as TextBlock ?? new TextBlock();
            ValidateHint.Content = view;
            if (_operationNotice.Length == 0) return;
            ValidateHint.Visibility = Visibility.Visible;
            view.Text = _operationNotice;
            ValidateHint.Foreground = new SolidColorBrush(Color.FromRgb(0x4F,0xE3,0xFF));
            ValidateHint.ToolTip = _operationNotice;
        }

        /// <summary>条件聚合方式那一行：只有在有 ≥2 个条件时才显示。</summary>
        private void RefreshCondModeRow() { } // 每个条件自行选择全部满足或任一满足。

        private void UpdateEmptyHint()
        {
            EmptyHint.Visibility = _flow.Nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshValidateHint();
        }

        private FlowValidator.Issue? _hintIssue;
        private Button? _bgiPickerButton;
        private readonly Dictionary<string, FrameworkElement> _fieldTargets = new(StringComparer.OrdinalIgnoreCase);
        private FrameworkElement? _highlightedField;
        private readonly Brush _normalFieldBorder = new SolidColorBrush(Color.FromArgb(0x30,0xFF,0xFF,0xFF));
        private static readonly Brush ErrorFieldBorder = new SolidColorBrush(Color.FromRgb(0xFF,0x9B,0x9B));

        private void RefreshValidateHint()
        {
            if (ValidateHint == null) return;
            try
            {
                var report = FlowValidator.Validate(_flow, _cfg);
                var text = ValidateHint.Content as TextBlock ?? new TextBlock();
                ValidateHint.Content = text;
                text.TextWrapping = TextWrapping.Wrap;
                if (_flow.Nodes.Count == 0) { ValidateHint.Visibility = Visibility.Collapsed; _hintIssue = null; return; }
                if (_operationNotice.Length > 0) { ShowHintText(); return; }
                ValidateHint.Visibility = Visibility.Visible;
                var issue = report.Issues.FirstOrDefault(i => i.IsError) ?? report.Issues.FirstOrDefault();
                _hintIssue = issue;
                // 左下角同时给出“能否运行”和“今天会不会跑”两个结论。
                var today = _flow.TodayRunState();
                string todayTip = "\n今日：" + today.Text;
                if (report.HasError)
                {
                    text.Text = "✕ " + (issue?.Message ?? "流程不能运行") + "（点击前往）" + todayTip;
                    ValidateHint.Foreground = new SolidColorBrush(Color.FromRgb(0xFF,0x9B,0x9B));
                    ValidateHint.Cursor = issue?.Node != null ? Cursors.Hand : Cursors.Arrow;
                    ValidateHint.ToolTip = report.ToText() + "\n\n点击定位到相关模块" + todayTip;
                }
                else if (issue != null)
                {
                    text.Text = "提醒：" + issue.Message + "（点击前往）" + todayTip;
                    ValidateHint.Foreground = new SolidColorBrush(Color.FromRgb(0xFF,0xD9,0x8A));
                    ValidateHint.Cursor = issue.Node != null ? Cursors.Hand : Cursors.Arrow;
                    ValidateHint.ToolTip = report.ToText() + todayTip;
                }
                else
                {
                    text.Text = "✔ 可以运行" + todayTip;
                    ValidateHint.Foreground = new SolidColorBrush(today.WillRun
                        ? Color.FromRgb(0x6E,0xF0,0xC8) : Color.FromRgb(0xFF,0xB1,0x4D));
                    ValidateHint.Cursor = Cursors.Arrow; ValidateHint.ToolTip = today.Text;
                }
            }
            catch { }
        }

        private void ValidateHint_Click(object sender, RoutedEventArgs e)
        {
            if (_operationNotice.Length > 0) { _operationNotice = ""; _noticeTimer?.Stop(); RefreshValidateHint(); return; }
            if (_hintIssue?.Node != null)
            {
                Canvas.Select(_hintIssue.Node);
                HighlightIssue(_hintIssue);
            }
        }
        private void HighlightIssue(FlowValidator.Issue issue)
        {
            var target = _fieldTargets.TryGetValue(issue.ParamKey, out var field) ? field : null;
            if (issue.Target == "bgi") target = _bgiPickerButton;
            if (target == null) return;
            _highlightedField = target;
            if (target is Control control) { control.BorderBrush = ErrorFieldBorder; control.BorderThickness = new Thickness(2); }
            target.BringIntoView(); target.Focus();
        }
        private void ClearFieldHighlights()
        {
            if (_highlightedField != null)
            { if (_highlightedField is Control control) { control.BorderBrush = _normalFieldBorder; control.BorderThickness = new Thickness(1); } }
            _highlightedField = null;
        }
        private void RegisterField(string key, FrameworkElement target)
        {
            _fieldTargets[key] = target;
            target.Tag = key;
        }
        private void RefreshFieldHighlight()
        {
            ClearFieldHighlights();
            if (_hintIssue != null && Canvas.SelectedNode == _hintIssue.Node) HighlightIssue(_hintIssue);
        }
        // ══════════ 左侧模块库 ══════════

        /// <summary>
        /// 模块库：只列"类别"，点一下在**右侧展开菜单**选具体类型。
        /// ★ 用户要求：模块只需要类型，点击后在右侧菜单中选择。
        ///   这样左栏只占 3 行，不会铺满整个侧边。
        /// </summary>


        /// <summary>在点击的类别卡片右侧弹出具体类型菜单。</summary>


        /// <summary>
        /// 模块库：显示 4 个**类别卡片**（触发器/条件/执行器/结束）。
        /// ★ 用户要求：恢复成类别卡片，结束独立成一类（琥珀色）。
        ///   点卡片后在右侧弹出该类别的具体节点。
        /// </summary>
        private void BuildLibrary()
        {
            LibPanel.Children.Clear();
            _allCards.Clear();   // 防止重复累积

            // ★ 触发器与条件已合并为一个「判定」块：
            //   两者本质是同一套判定逻辑，只是位置不同 ——
            //   放第一个（无上游连线）就是触发器，放后面就是条件。
            //   这样模块库不再有"触发器/条件"之分，用户不必先想清楚"这算哪个"。
            var judgeCount = FlowNodeCatalog.ByKind(FlowNodeKind.Condition).Count;
            if (judgeCount > 0) LibPanel.Children.Add(BuildJudgeCard(judgeCount));

            foreach (var kind in new[] { FlowNodeKind.Action, FlowNodeKind.Terminate })
            {
                var defs = FlowNodeCatalog.ByKind(kind);
                if (defs.Count == 0) continue;
                LibPanel.Children.Add(BuildKindCard(kind, defs.Count));
            }

        }

        private static string KindName(FlowNodeKind kind) => kind switch
        {
            FlowNodeKind.Trigger => "条件",
            FlowNodeKind.Condition => "条件",
            FlowNodeKind.Terminate => "当天完成",
            _ => "动作"
        };

        private static string KindDesc(FlowNodeKind kind) => kind switch
        {
            FlowNodeKind.Trigger => "判断当前状态，满足就继续",
            FlowNodeKind.Condition => "判断当前状态，满足就继续",
            FlowNodeKind.Terminate => "到达后今天不再重复执行",
            _ => "条件满足后执行实际操作"
        };

        private static Color AccentOf(FlowNodeKind kind) => kind switch
        {
            FlowNodeKind.Trigger => Color.FromRgb(0xA9, 0x7B, 0xFF),
            FlowNodeKind.Condition => Color.FromRgb(0x4F, 0xE3, 0xFF),
            FlowNodeKind.Terminate => Color.FromRgb(0xFF, 0xB1, 0x4D),
            _ => Color.FromRgb(0x41, 0xF0, 0xC0)
        };

        /// <summary>一个类别卡片：标题 + 数量 + 说明，点一下弹出该类节点清单。</summary>
        /// <summary>
        /// 「判定」卡片：包含原来的触发器 + 条件（合并显示）。
        /// ★ 去重原则：同一件事不再同时出现"触发器版"和"条件版"，
        ///   只保留一个模块，由它在图中的位置决定语义。
        /// </summary>
        private Border BuildJudgeCard(int count)
        {
            var accent = Color.FromRgb(0xA9, 0x7B, 0xFF);

            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = "条件",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(accent)
            });
            sp.Children.Add(new TextBlock
            {
                Text = count + " 个模块",
                FontSize = 10,
                Foreground = (Brush)FindResource("TextMuted"),
                Margin = new Thickness(0, 3, 0, 0)
            });
            sp.Children.Add(new TextBlock
            {
                Text = "满足条件后，继续下一步",
                FontSize = 10,
                Foreground = (Brush)FindResource("TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 0),
                LineHeight = 15
            });

            var card = new Border
            {
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(12, 11, 12, 11),
                Margin = new Thickness(0, 0, 0, 9),
                Background = new SolidColorBrush(Color.FromArgb(0x1A, accent.R, accent.G, accent.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = sp
            };

            _allCards.Add((FlowNodeKind.Trigger, card));

            card.MouseEnter += (_, __) =>
            {
                card.Background = new SolidColorBrush(Color.FromArgb(0x30, accent.R, accent.G, accent.B));
                CancelCloseMenu();
                ShowJudgeMenu(card);
            };
            card.MouseLeave += (_, __) =>
            {
                card.Background = new SolidColorBrush(Color.FromArgb(0x1A, accent.R, accent.G, accent.B));
                ScheduleCloseMenu();
            };

            return card;
        }

        /// <summary>判定卡片弹出的清单：触发器 + 条件合并，同一模块只出现一次。</summary>
        private void ShowJudgeMenu(FrameworkElement anchor)
        {
            var seen = new HashSet<FlowNodeType>();
            var merged = new List<FlowNodeDef>();
            foreach (var d in FlowNodeCatalog.ByKind(FlowNodeKind.Trigger))
                if (seen.Add(d.Type)) merged.Add(d);
            foreach (var d in FlowNodeCatalog.ByKind(FlowNodeKind.Condition))
                if (seen.Add(d.Type)) merged.Add(d);

            ShowMergedMenu(merged, Color.FromRgb(0xA9, 0x7B, 0xFF), anchor);
        }

        private Border BuildKindCard(FlowNodeKind kind, int count)
        {
            Color accent = AccentOf(kind);

            var sp = new StackPanel();

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock
            {
                Text = KindName(kind),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(accent)
            });
            head.Children.Add(new TextBlock
            {
                Text = "  " + count,
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, accent.R, accent.G, accent.B)),
                VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(head);

            sp.Children.Add(new TextBlock
            {
                Text = KindDesc(kind),
                FontSize = 10.5,
                Foreground = (Brush)FindResource("TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 0),
                LineHeight = 15
            });

            var card = new Border
            {
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(12, 11, 12, 11),
                Margin = new Thickness(0, 0, 0, 9),
                Background = new SolidColorBrush(Color.FromArgb(0x1A, accent.R, accent.G, accent.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = sp
            };

            // ══════════ 悬停展开（纯事件驱动，不用坐标）══════════
            //
            //   ★ 用户指出：既然 MouseEnter 能把卡片点亮，
            //     说明事件本身是可靠的，直接用它开菜单就行，
            //     没必要去算鼠标坐标（那套东西被 DPI 缩放坑了两次）。
            //
            //   逻辑：
            //     MouseEnter 卡片  → 立刻开菜单（对应类别）
            //     MouseLeave 卡片  → 0.3 秒后关，但如果鼠标进了菜单就不关
            //     MouseEnter 菜单  → 取消关闭
            //     MouseLeave 菜单  → 0.3 秒后关
            _allCards.Add((kind, card));

            card.MouseEnter += (_, __) =>
            {
                card.Background = new SolidColorBrush(Color.FromArgb(0x30, accent.R, accent.G, accent.B));

                // 单节点类别（如"结束"）不弹菜单
                if (FlowNodeCatalog.ByKind(kind).Count <= 1) return;

                CancelCloseMenu();
                ShowKindMenu(kind, card);
            };

            card.MouseLeave += (_, __) =>
            {
                card.Background = new SolidColorBrush(Color.FromArgb(0x1A, accent.R, accent.G, accent.B));
                ScheduleCloseMenu();
            };

            // ★ 该类只有一个节点（如"结束"）→ 不需要菜单，直接拖出来用
            if (count == 1)
            {
                var only = FlowNodeCatalog.ByKind(kind)[0];

                Point pressPos = default;
                bool dragging = false;

                card.MouseLeftButtonDown += (_, ev) =>
                {
                    pressPos = ev.GetPosition(card);
                    dragging = false;
                    card.CaptureMouse();
                    ev.Handled = true;
                };

                card.MouseMove += (_, ev) =>
                {
                    if (!card.IsMouseCaptured) return;
                    var cur = ev.GetPosition(card);
                    if (!dragging &&
                        (Math.Abs(cur.X - pressPos.X) > 6 || Math.Abs(cur.Y - pressPos.Y) > 6))
                    {
                        dragging = true;
                        BeginDragFromLibrary(only);
                    }
                };

                card.MouseLeftButtonUp += (_, ev) =>
                {
                    card.ReleaseMouseCapture();
                    if (!dragging)
                    {
                        // 单击也直接加
                        var center = new Point(
                            Math.Max(20, CanvasScroll.ActualWidth / 2 - 89 + CanvasScroll.HorizontalOffset),
                            Math.Max(20, CanvasScroll.ActualHeight / 2 - 31 + CanvasScroll.VerticalOffset));
                        AddNode(only, center);
                    }
                    ev.Handled = true;
                };

                card.ToolTip = "按住拖到画布即可添加「" + only.Title + "」";
                return card;
            }

            // 点击卡片也打开（有些用户习惯点）
            card.MouseLeftButtonUp += (_, __) =>
            {
                _activeCard = card;
                _activeKind = kind;
                _menuLeftAt = null;
                ShowKindMenu(kind, card);
            };

            return card;
        }

        /// <summary>在点击的类别卡片右侧弹出该类别的具体节点清单。</summary>
        private void ShowKindMenu(FlowNodeKind kind, FrameworkElement anchor)
            => ShowMergedMenu(FlowNodeCatalog.ByKind(kind), AccentOf(kind), anchor);

        /// <summary>
        /// 构建并显示节点清单（供各类别与"判定"合并卡片共用）。
        /// ★ 同一个 def 只会渲染一次，因此调用方无需自己去重。
        /// </summary>
        private void ShowMergedMenu(List<FlowNodeDef> defs, Color accent, FrameworkElement anchor)
        {
            KindMenu.Children.Clear();

            foreach (var d in defs)
            {
                var def = d;

                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock
                {
                    Text = def.Glyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                    Width = 20,
                    Foreground = new SolidColorBrush(accent),
                    VerticalAlignment = VerticalAlignment.Center
                });

                var col = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
                col.Children.Add(new TextBlock
                {
                    Text = def.Title,
                    FontSize = 12,
                    Foreground = (Brush)FindResource("TextPrimary")
                });
                col.Children.Add(new TextBlock
                {
                    Text = def.Desc,
                    FontSize = 9.5,
                    Foreground = (Brush)FindResource("TextMuted"),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 220
                });
                row.Children.Add(col);

                var btn = new Border
                {
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(8, 7, 8, 7),
                    Margin = new Thickness(0, 0, 0, 2),
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    Child = row
                };
                btn.MouseEnter += (_, __) =>
                    btn.Background = new SolidColorBrush(Color.FromArgb(0x20, accent.R, accent.G, accent.B));
                btn.MouseLeave += (_, __) => btn.Background = Brushes.Transparent;

                // ★ 用户要求：**长按拖出**模块，不要点击就自动放一个位置。
                //   做法：按下时记录起点，移动超过阈值就开始拖拽预览，
                //   松手时落到鼠标所在位置（画布坐标）。
                Point pressPos = default;
                bool dragging = false;

                btn.MouseLeftButtonDown += (_, ev) =>
                {
                    pressPos = ev.GetPosition(btn);
                    dragging = false;
                    btn.CaptureMouse();
                    ev.Handled = true;
                };

                btn.MouseMove += (_, ev) =>
                {
                    if (!btn.IsMouseCaptured) return;
                    var cur = ev.GetPosition(btn);
                    if (!dragging &&
                        (Math.Abs(cur.X - pressPos.X) > 6 || Math.Abs(cur.Y - pressPos.Y) > 6))
                    {
                        dragging = true;
                        BeginDragFromLibrary(def);
                    }
                };

                btn.MouseLeftButtonUp += (_, ev) =>
                {
                    btn.ReleaseMouseCapture();

                    // 拖动中：松手由画布事件处理（Canvas_DragUp）
                    // 未拖动（单击）：放到画布可见区域中央附近
                    if (!dragging)
                    {
                        MenuLayer.Visibility = Visibility.Collapsed;
                        var center = new Point(
                            Math.Max(20, CanvasScroll.ActualWidth / 2 - 89 + CanvasScroll.HorizontalOffset),
                            Math.Max(20, CanvasScroll.ActualHeight / 2 - 31 + CanvasScroll.VerticalOffset));
                        AddNode(def, center);
                    }
                    ev.Handled = true;
                };

                KindMenu.Children.Add(btn);
            }

            // ★ 定位：在窗口内的覆盖层上绝对定位（不用 Popup）。
            //   位置 = 卡片右侧，纵向与画布顶部对齐，高度与画布等高。
            try
            {
                // 卡片在 MenuLayer 坐标系里的位置
                var p = anchor.TranslatePoint(new Point(anchor.ActualWidth + 6, 0), MenuLayer);

                double canvasH = CanvasScroll != null ? CanvasScroll.ActualHeight : 0;
                if (canvasH < 200) canvasH = Math.Max(200, ActualHeight - 220);

                // 让菜单顶部与画布顶部对齐
                double top = p.Y;
                if (CanvasScroll != null)
                {
                    var canvasTopInLayer = CanvasScroll.TranslatePoint(new Point(0, 0), MenuLayer).Y;
                    top = canvasTopInLayer;
                }

                KindMenuScroll.MaxHeight = canvasH;

                System.Windows.Controls.Canvas.SetLeft(KindMenuCard, p.X);
                System.Windows.Controls.Canvas.SetTop(KindMenuCard, top);
            }
            catch { }

            MenuLayer.Visibility = Visibility.Visible;
        }

        // ── 首次使用引导：半透明遮罩 + 分步高亮 + 自绘示例（不依赖真实状态） ──
        //
        //   ★ 设计要点（按反馈修正）：
        //     1. 遮罩盖住整个操作区，只挖空当前要讲解的区域，视线自然被引过去；
        //     2. 示例节点/状态灯由本层自己画“假数据”，不受真实流程与预览影响，
        //        演什么就一定是什么；
        //     3. 不再有弹窗：说明文字与按钮直接放在遮罩层里，按钮固定在右下角。

        private FlowGuideOverlay? _guide;
        private bool _guideShown;

        /// <summary>只显示操作演示，不创建节点、不执行动作、不修改配置。</summary>
        private void ShowFlowGuide(bool force)
        {
            try
            {
                if (_guide != null) return;
                if (!force && (_guideShown || !_cfg.Settings.ShowFlowGuide)) return;
                _guideShown = true;
                MenuLayer.Visibility = Visibility.Collapsed;
                GuideLayer.Children.Clear();
                GuideLayer.Visibility = Visibility.Visible;
                _guide = new FlowGuideOverlay(
                    FlowGuideOverlay.BuildSteps(LibPanel, CanvasScroll, PropPanel, ValidateHint),
                    EndGuide,
                    () => !_cfg.Settings.ShowFlowGuide,
                    checkedValue => { _cfg.Settings.ShowFlowGuide = !checkedValue; _dirty = true; });
                _guide.SetSceneReferences(CanvasScroll, PropPanel);
                GuideLayer.Children.Add(_guide);
                GuideLayer.SizeChanged += (_, _) => _guide?.SyncHostSize();
                _guide.SyncHostSize();
                _guide.Start();
            }
            catch (Exception ex)
            {
                EndGuide();
                ThemedMessage.Info(this, "引导", "引导没能显示：" + ex.Message);
            }
        }

        private void EndGuide()
        {
            try
            {
                _guide?.Close();
                _guide = null;
                GuideLayer.Children.Clear();
                GuideLayer.Visibility = Visibility.Collapsed;
                ValidateHint.IsHitTestVisible = true;
                RefreshValidateHint();
                _ = RefreshConditionPreview();
            }
            catch { }
        }

        private void CloseGuide() => EndGuide();


        private void BuildHelp()
        {
            HelpPanel.Children.Clear();

            void Add(string title, string desc)
            {
                HelpPanel.Children.Add(new TextBlock
                {
                    Text = title,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("TextPrimary"),
                    Margin = new Thickness(0, 10, 0, 3),
                    TextWrapping = TextWrapping.Wrap
                });
                HelpPanel.Children.Add(new TextBlock
                {
                    Text = desc,
                    FontSize = 10,
                    Foreground = (Brush)FindResource("TextMuted"),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 16
                });
            }

            Add("① 从哪里开始",
                "启动台选择账号 →「新建自动化」。左侧添加条件、动作或「每天完成」；拖到画布，单击具体条目也可添加。");
            Add("② 正确连线",
                "从下方出口拖到目标。点击起点再点目标仅用于「画笔模式」；Esc 或再次点击开关退出。");
            Add("③ 状态灯不是执行结果",
                "绿色＝此刻满足，灰色＝不满足，琥珀＝数据不可用。约每秒只读预览；不执行动作，也不表示已运行。");
            Add("④ 条件怎样组合",
                "有上游的条件可选「全部满足」或「任一满足」；取反只改变本条件。动作和结束的入线没有全部/任一选项。");
            Add("⑤ 支路与动作",
                "普通扇出选择全部支路，但动作依次执行，并非同时。「选择分支」只走第一条满足的支路。");
            Add("⑥ 信号保留到本轮结束",
                "日志命中、定时、手动等瞬时信号在本轮等待中保留，不跨轮永久保存；进程/会话等实时状态逐轮读取。");
            Add("⑦ 启动不等于任务完成",
                "启动 BGI / 一条龙 / 配置组成功，仅表示请求被接受。继续接「配置组状态」或日志条件，确认真实完成。");
            Add("⑧ 等待与失败",
                "等待期间输入断开，会清除并重新计时。动作失败后先排查；输入断开后再次成立才允许重试。");
            Add("⑨ 每天完成与日界",
                "所选动作全部成功才写入当天标记；停止本轮执行，但仍做只读预览。日界 00:00 / 04:00 到点开启新一轮。");
            Add("⑩ 没有每天完成",
                "一轮完成后清除本轮状态，条件重新满足即可再次执行，不设置日界。");
            Add("⑪ 今天运行这个流程",
                "在「每天完成」属性中设置：勾选恢复本业务日，取消则今日不运行；保存后生效。恢复后的当轮不保证只再跑一次。");
            Add("⑫ 自检与立即运行",
                "自检定位结构问题，不试运行。立即运行会执行真实动作，但仍需条件导通，并受启用、暂停和今日状态限制。");
            var replay = new Button
            {
                Content = "▶ 重新播放引导动画",
                Style = (Style)FindResource("GhostButton"),
                Margin = new Thickness(0, 14, 0, 4),
                Padding = new Thickness(10, 6, 10, 6),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            replay.Click += (_, __) => ShowFlowGuide(force: true);
            HelpPanel.Children.Add(replay);
        }

        // ── 从模块库拖拽节点到画布 ──────────────────────────────

        private FlowNodeDef? _draggingDef;
        private Border? _dragGhost;

        /// <summary>开始从模块库拖拽：在画布上显示一个半透明预览块。</summary>
        private void BeginDragFromLibrary(FlowNodeDef def)
        {
            try
            {
                _draggingDef = def;
                MenuLayer.Visibility = Visibility.Collapsed;

                if (_dragGhost == null)
                {
                    _dragGhost = new Border
                    {
                        Width = 178,
                        Height = 62,
                        CornerRadius = new CornerRadius(12),
                        BorderThickness = new Thickness(2),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0x4F, 0xE3, 0xFF)),
                        Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x18, 0x2E)),
                        IsHitTestVisible = false,
                        Child = new TextBlock
                        {
                            Text = def.Title,
                            FontSize = 12.5,
                            Foreground = (Brush)FindResource("TextPrimary"),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    };
                    Canvas.Children.Add(_dragGhost);
                }

                Canvas.CaptureMouse();
                Canvas.MouseMove += Canvas_DragMove;
                Canvas.MouseLeftButtonUp += Canvas_DragUp;
            }
            catch { }
        }

        private void Canvas_DragMove(object sender, MouseEventArgs e)
        {
            if (_dragGhost == null) return;
            try
            {
                var p = e.GetPosition(Canvas);
                System.Windows.Controls.Canvas.SetLeft(_dragGhost, Math.Max(0, p.X - 89));
                System.Windows.Controls.Canvas.SetTop(_dragGhost, Math.Max(0, p.Y - 31));
            }
            catch { }
        }

        private void Canvas_DragUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                Canvas.MouseMove -= Canvas_DragMove;
                Canvas.MouseLeftButtonUp -= Canvas_DragUp;
                Canvas.ReleaseMouseCapture();

                if (_dragGhost != null)
                {
                    Canvas.Children.Remove(_dragGhost);
                    _dragGhost = null;
                }

                var def = _draggingDef;
                _draggingDef = null;
                if (def == null) return;

                var p = e.GetPosition(Canvas);
                AddNode(def, new Point(p.X - 89, p.Y - 31));
            }
            catch { }
        }

        /// <summary>点模块库里的项 → 在画布上添加一个节点。</summary>
        private void AddNode(FlowNodeDef def, Point? at = null)
        {
            try
            {
                // ★ 不再限制"只能有一个触发器"：
                //   多个没有上游连线的判定节点都是入口，任一命中即可触发流程。
                //   触发器与条件已经合并，用户拖几次都合理。

                var node = new FlowNode
                {
                    Kind = def.Kind,
                    Type = def.Type,
                    X = 40,
                    Y = 40
                };

                // 默认参数
                foreach (var p in def.Params)
                    node.Set(p.Key, p.Default);

                // 新建模块默认当前流程账号；已有节点保持自己保存的目标用户。
                if (node.Params.TryGetValue("user", out var defaultUser) && defaultUser.Length == 0)
                    node.Set("user", _account.UserName);

                // ★ 不要自动把"目标用户"填成别的账号。
                //
                //   原来这里为了让下拉有内容，会把目标用户设成"第一个有 BGI 路径的账号"，
                //   结果本账号 A2 的流程会指向 A1 的 BGI —— 切换账号后路径完全不同步，
                //   而且执行时会在错误的会话里跑（用户实测反馈"更改账号没用同步更改 bgi 位置"）。
                //
                //   正确做法：目标用户留空 = 本流程绑定的账号。
                //   列表为空就让它空着，由用户自己在该账号下配 BGI 路径。
                if (UsesBgi(def.Type) && node.Get("user").Length == 0)
                {
                    // 明确写空，避免沿用旧默认值
                    node.Set("user", "");
                }

                // 位置：拖动落点优先；没有落点（单击）时错开摆放
                if (at.HasValue)
                {
                    node.X = Math.Max(0, at.Value.X);
                    node.Y = Math.Max(0, at.Value.Y);
                }
                else
                {
                    // 找一个没被任何已有节点占用的空格，避免新节点叠在一起看不见。
                    double gapX = 24, gapY = 30, stepX = 260, stepY = 110;
                    for (int row = 0; row < 40; row++)
                    {
                        for (int col = 0; col < 8; col++)
                        {
                            double x = 40 + col * stepX, y = 40 + row * stepY;
                            bool free = !_flow.Nodes.Any(n =>
                                Math.Abs(n.X - x) < stepX - gapX && Math.Abs(n.Y - y) < stepY - gapY);
                            if (!free) continue;
                            node.X = x; node.Y = y; row = 40; break;
                        }
                    }
                }

                _flow.Nodes.Add(node);
                _dirty = true;
                Canvas.Load(_flow);
                // 新节点放到视口中间附近，避免落在滚动区外面看不见。
                Canvas.UpdateLayout();
                Canvas.BringIntoView(new Rect(node.X, node.Y, 228, 70));
                Canvas.Select(node);
                UpdateEmptyHint();
                RefreshCondModeRow();
            }
            catch (Exception ex)
            {
                ThemedMessage.Info(this, "提示", "添加节点失败：" + ex.Message);
            }
        }

        private void OnNodeDelete(FlowNode node)
        {
            try
            {
                if (node.Kind == FlowNodeKind.Trigger)
                {
                    if (!ThemedMessage.Confirm(this, "确认", "删除触发器后整个流程就不会触发了，确定吗？")) return;
                }

                _flow.Nodes.RemoveAll(n => n.Id == node.Id);
                _flow.Edges.RemoveAll(e => e.From == node.Id || e.To == node.Id);
                _dirty = true;
                Canvas.Load(_flow);
                ShowPropPanel(null);
                UpdateEmptyHint();
                RefreshCondModeRow();
            }
            catch { }
        }

        private void ClearPropPanel()
        {
            _propUi.Clear();
        }

        /// <summary>当前属性面板上动态创建的控件（切换节点时要清掉事件）。</summary>
        private readonly List<object> _propUi = new List<object>();

        // ══════════ 右侧属性面板 ══════════

        private void OnNodeSelected(FlowNode? node) => ShowPropPanel(node);

        private void ShowPropPanel(FlowNode? node)
        {
            PropPanel.Children.Clear();
            ClearPropPanel();
            _fieldTargets.Clear(); ClearFieldHighlights();

            if (node == null)
            {
                PropPanel.Children.Add(new TextBlock
                {
                    Text = "点击画布上的节点来编辑它的参数",
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)FindResource("TextMuted")
                });
                return;
            }

            var def = FlowNodeCatalog.Get(node.Type);

            // ★ 节点类型：可直接切换，不用删了重建
            //   （用户要求：红框处无法修改类型，后期修改很麻烦）
            PropPanel.Children.Add(new TextBlock
            {
                Text = "节点类型",
                FontSize = 10.5,
                Foreground = (Brush)FindResource("TextMuted"),
                Margin = new Thickness(0, 0, 0, 0)
            });

            var typeCombo = new ComboBox
            {
                Height = 36,
                Margin = new Thickness(0, 5, 0, 0),
                Padding = new Thickness(10, 0, 6, 0),
                FontSize = 12.5,
                Foreground = (Brush)FindResource("TextPrimary"),
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1)
            };

            // 原触发器和条件已统一成触点，可在同一池里切换数据源。
            foreach (var cand in FlowNodeCatalog.ByKind(node.IsContact ? FlowNodeKind.Condition : node.Kind))
                typeCombo.Items.Add(new ComboBoxItem { Content = cand.Title, Tag = cand.Type });

            foreach (ComboBoxItem it in typeCombo.Items)
                if (it.Tag is FlowNodeType t && t == node.Type) { it.IsSelected = true; break; }

            typeCombo.SelectionChanged += (_, __) =>
            {
                if (typeCombo.SelectedItem is not ComboBoxItem it) return;
                if (it.Tag is not FlowNodeType newType) return;
                if (newType == node.Type) return;

                // 切换类型：保留位置、连线、以及「目标用户」
                // ★ 原来直接清空 Params，把 user 也丢了，
                //   导致"选了 A2 但取不到 A2 的 BGI 路径"（用户实测）。
                string keepUser = node.Get("user");

                node.Type = newType;
                node.Params = new Dictionary<string, string>();

                var nd = FlowNodeCatalog.Get(newType);
                if (nd != null)
                {
                    foreach (var p in nd.Params)
                        node.Set(p.Key, p.Default);
                }

                // 把目标用户还回去
                if (keepUser.Length > 0) node.Set("user", keepUser);

                MarkDirty();
                Canvas.Redraw();

                // 重建属性面板（参数列表变了）
                Dispatcher.InvokeAsync(() => ShowPropPanel(node));
            };

            PropPanel.Children.Add(typeCombo);
            _propUi.Add(typeCombo);

            if (node.IsContact)
            {
                var op = new ComboBox { Margin = new Thickness(0,12,0,0), Height = 32 };
                op.Items.Add(new ComboBoxItem { Content = "全部满足才继续", Tag = "AND" });
                op.Items.Add(new ComboBoxItem { Content = "任一满足就继续", Tag = "OR" });
                foreach (ComboBoxItem item in op.Items) if ((string)item.Tag == (node.Logic == FlowLogicOp.OR ? "OR" : "AND")) item.IsSelected = true;
                op.SelectionChanged += (_,__) => { node.Logic = op.SelectedIndex == 1 ? FlowLogicOp.OR : FlowLogicOp.AND; MarkDirty(); Canvas.Redraw(); };
                PropPanel.Children.Add(op);
                var neg = new CheckBox { Content = "条件不满足时继续", IsChecked = node.Negated, Style = (Style)FindResource("GlassCheckBox"), Margin = new Thickness(0,8,0,0) };
                neg.Checked += (_,__) => { node.Negated = true; MarkDirty(); Canvas.Redraw(); };
                neg.Unchecked += (_,__) => { node.Negated = false; MarkDirty(); Canvas.Redraw(); };
                PropPanel.Children.Add(neg);
            }

            // 标题 + 说明
            PropPanel.Children.Add(new TextBlock
            {
                Text = node.Title,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0)
            });

            if (def != null && def.Desc.Length > 0)
            {
                PropPanel.Children.Add(new TextBlock
                {
                    Text = def.Desc,
                    FontSize = 10.5,
                    Foreground = (Brush)FindResource("TextMuted"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }

            // 参数
            if (def != null)
            {
                foreach (var p in def.Params)
                    AddParamEditor(node, p);
            }

            // ★ BGI 相关节点：显示将使用的 BGI 路径，让用户能确认/修改
            if (UsesBgi(node.Type))
                AddBgiPathEditor(node);

            // ★ 日界只属于 END 节点；无 END 的流程是循环执行。
            if (node.Kind == FlowNodeKind.Terminate)
                AddEndEditors(node);

            // 删除按钮
            var del = new Button
            {
                Content = "删除这个节点",
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, 16, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                FontSize = 11.5
            };
            del.Click += (_, __) => OnNodeDelete(node);
            PropPanel.Children.Add(del);
        }

        private void AddParamEditor(FlowNode node, FlowParamDef p)
        {
            var block = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };

            block.Children.Add(new TextBlock
            {
                Text = p.Label,
                FontSize = 10.5,
                Foreground = (Brush)FindResource("TextMuted"),
                TextWrapping = TextWrapping.Wrap
            });

            switch (p.Kind)
            {
                case FlowParamKind.Bool:
                {
                    var cb = new CheckBox
                    {
                        Content = "是",
                        IsChecked = node.GetBool(p.Key, p.Default == "true"),
                        Style = (Style)FindResource("GlassCheckBox"),
                        Margin = new Thickness(0, 6, 0, 0),
                        FontSize = 11.5
                    };
                    cb.Checked += (_, __) => { node.Set(p.Key, "true"); MarkDirty(); };
                    cb.Unchecked += (_, __) => { node.Set(p.Key, "false"); MarkDirty(); };
                    block.Children.Add(cb);
                    _propUi.Add(cb);
                    break;
                }

                case FlowParamKind.KeyCapture:
                {
                    // ★★ 2.17.34：按键**捕获**，不是文本输入 ★★
                    //
                    //   用户原话："按键录入不应该是文本框，用户按什么键就是什么键，
                    //   也可以组合键录入，这样才对。"
                    //
                    //   做法：一个只读样式的框（显示已录的按键），点它进入"监听"状态，
                    //   期间吞掉按键事件、把按下的修饰键 + 主键拼成 "CTRL+A" / "F12"，
                    //   抬起主键后结束并写回节点参数。Esc 取消，Backspace 清空。
                    // ★★ 2.17.35：改用 **TextBox** 而不是 Border ★★
                    //
                    //   用户实测："光标放上去会变手指，点击无反应，无法录入"。
                    //   根因：Border 是 Decorator，不是 Control —— 虽然设了
                    //   Focusable=true，`Focus()` 往往拿不到**键盘焦点**，
                    //   于是 PreviewKeyDown 根本不会触发（光标变了、点击也进来了，
                    //   但按键全被别的元素吃掉）。
                    //
                    //   TextBox 天生可聚焦、可收键盘输入，用它当"录入框"最稳。
                    //   设为只读 + 隐藏插入符，让它看起来像普通显示框；
                    //   真正的按键处理走 PreviewKeyDown（在只读 TextBox 上照样触发）。
                    var keyBox = new TextBox
                    {
                        MinHeight = 34,
                        Margin = new Thickness(0, 6, 0, 0),
                        Style = (Style)FindResource("InlineTextBox"),
                        // ★ 不用 IsReadOnly：只读 TextBox 在部分场景下不参与点击聚焦
                        //   （表现为"光标是手、点了没反应"）。改为可编辑但**拦截所有输入**：
                        //   PreviewTextInput / PreviewKeyDown 里一律 Handled，
                        //   所以用户永远改不了文字，但焦点与按键事件都能正常到达。
                        IsReadOnly = false,
                        Cursor = Cursors.IBeam,
                        FontSize = 12,
                        TextAlignment = TextAlignment.Left,
                        VerticalContentAlignment = VerticalAlignment.Center,
                        Text = "",
                        ToolTip = "点一下，然后直接按下要发送的按键；可单按 CTRL/SHIFT/ALT，也可组合键"
                    };
                    // 用 TextBox 的文字来显示状态，颜色靠 Foreground 区分。
                    // （原来用 Border+TextBlock，改 TextBox 后这两个 helper 也跟着改。）

                    bool listening = false;
                    var pressedMods = new List<Key>();

                    void Render()
                    {
                        string cur = node.Get(p.Key, p.Default);
                        if (listening)
                        {
                            keyBox.Text = "请按下要发送的按键…（可单按 CTRL/SHIFT/ALT，或按「取消」退出）";
                            keyBox.Foreground = new SolidColorBrush(Color.FromRgb(0x4F, 0xE3, 0xFF));
                            keyBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0xE3, 0xFF));
                        }
                        else if (string.IsNullOrEmpty(cur))
                        {
                            keyBox.Text = "点击这里，然后按下按键（可组合键）";
                            keyBox.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x9C, 0xC8));
                            keyBox.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
                        }
                        else
                        {
                            keyBox.Text = cur;
                            keyBox.Foreground = (Brush)FindResource("TextPrimary");
                            keyBox.BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x41, 0xF0, 0xC0));
                        }
                        // 只读框不能被编辑；把插入符藏到末尾也不显示
                        keyBox.CaretIndex = keyBox.Text?.Length ?? 0;
                    }

                    string ModName(Key k) => k switch
                    {
                        Key.LeftCtrl or Key.RightCtrl => "CTRL",
                        Key.LeftAlt or Key.RightAlt => "ALT",
                        Key.LeftShift or Key.RightShift => "SHIFT",
                        Key.LWin or Key.RWin => "WIN",
                        _ => ""
                    };

                    void StopListen()
                    {
                        listening = false;
                        pressedMods.Clear();
                        Render();
                    }

                    void StartListen()
                    {
                        listening = true;
                        pressedMods.Clear();
                        // TextBox 必须先把键盘焦点真正拿过来，PreviewKeyDown 才会触发。
                        keyBox.Focus();
                        Keyboard.Focus(keyBox);
                        Render();
                    }

                    // 用 PreviewMouseLeftButtonDown：TextBox 内部可能把普通
                    // MouseLeftButtonDown 标记为已处理（用于定位插入符），冒到这里就没了。
                    keyBox.PreviewMouseLeftButtonDown += (_, ev) =>
                    {
                        StartListen();
                        ev.Handled = true;   // 别让 TextBox 自己处理（会尝试进入编辑态）
                    };
                    keyBox.GotKeyboardFocus += (_, __) => { if (!listening) StartListen(); };
                    keyBox.LostKeyboardFocus += (_, __) => { if (listening) StopListen(); };

                    // 拦掉一切"打字"来源，保证框里的内容只能由捕获逻辑写入
                    keyBox.PreviewTextInput += (_, ev) => { ev.Handled = true; };
                    // CommandManager 上挂：粘贴/剪切也不能污染捕获框
                    keyBox.AddHandler(System.Windows.Input.CommandManager.PreviewExecutedEvent,
                        new System.Windows.Input.ExecutedRoutedEventHandler((_, ev) =>
                        {
                            if (ev.Command == System.Windows.Input.ApplicationCommands.Paste
                                || ev.Command == System.Windows.Input.ApplicationCommands.Cut)
                                ev.Handled = true;
                        }));
                    // 本次监听里是否已经按过"主键"（决定修饰键松开时怎么处理）
                    bool sawMainKey = false;
                    DateTime lastModDownAt = DateTime.MinValue;

                    keyBox.PreviewKeyDown += (_, ev) =>
                    {
                        if (!listening) { ev.Handled = true; return; }   // 未在监听：吞掉，别让画布响应
                        ev.Handled = true;

                        // ★★ 2.17.36：Esc 不再承担"取消"，改成可正常录入的按键 ★★
                        //   用户原话："esc 需要增加一个单独按钮，因为现在 esc 是退出"。
                        //   取消改由旁边的「取消」按钮负责（见下方）。

                        // 退格 = 清空已录按键
                        if (ev.Key == Key.Back)
                        {
                            node.Set(p.Key, ""); MarkDirty(); StopListen(); return;
                        }

                        string mod = ModName(ev.Key);
                        if (mod.Length > 0)
                        {
                            if (!pressedMods.Contains(ev.Key)) pressedMods.Add(ev.Key);
                            lastModDownAt = DateTime.Now;
                            // 只按了修饰键：实时预览 "CTRL +"
                            keyBox.Text = "请按下要发送的按键…（当前 " + string.Join("+", pressedMods.Select(ModName))
                                + (ev.Key == Key.LeftCtrl || ev.Key == Key.RightCtrl
                                    ? "，松开即可只录 CTRL）" : "）");
                            return;
                        }

                        // 主键：拼成 CTRL+ALT+X 这类串，交给 RelayAgent.ParseVk 解析
                        sawMainKey = true;
                        var parts = new List<string>();
                        foreach (var k in pressedMods) parts.Add(ModName(k));
                        parts.Add(KeyNameOf(ev.Key, ev));
                        string spec = string.Join("+", parts.Where(s => !string.IsNullOrEmpty(s)));
                        if (spec.Length == 0) return;
                        node.Set(p.Key, spec);
                        MarkDirty();
                        StopListen();
                    };

                    // ★★ 修饰键**单独录入**：松开时若没按过任何主键，就把这个修饰键本身录进去 ★★
                    //   用户原话："shift ctrl 无法单独录入"。
                    //   原来只按修饰键永远只是"预览"，必须再按一个主键才能结束 ——
                    //   于是根本录不进单独的 SHIFT / CTRL / ALT。
                    keyBox.PreviewKeyUp += (_, ev) =>
                    {
                        if (!listening) { ev.Handled = true; return; }
                        ev.Handled = true;

                        string mod = ModName(ev.Key);
                        if (mod.Length == 0) return;
                        if (sawMainKey) return;                     // 组合键已在 KeyDown 里处理
                        if (pressedMods.Count != 1) return;         // 同时按住多个修饰键时不算（避免误录）
                        if ((DateTime.Now - lastModDownAt).TotalMilliseconds < 30) return;  // 与组合键的快速按下区分

                        node.Set(p.Key, mod);
                        MarkDirty();
                        StopListen();
                    };

                    Render();
                    block.Children.Add(keyBox);

                    // ★ 2.17.36：独立的「取消 / 清空」按钮 ★
                    //   原来靠 Esc 退出监听，等于把 Esc 这个按键"占用"了，
                    //   用户就没法把 Esc 本身录进去（而 Esc 恰恰是常用的关闭键）。
                    //   现在取消交给按钮，Esc 变成可以正常录入的按键。
                    var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                    var btnCancel = new Button
                    {
                        Style = (Style)FindResource("PillButton"),
                        Height = 24, MinWidth = 60, Padding = new Thickness(9, 0, 9, 0),
                        Content = "取消录入",
                        Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                        ToolTip = "退出按键监听（不改变已录内容）"
                    };
                    btnCancel.Click += (_, __) => StopListen();
                    btnRow.Children.Add(btnCancel);

                    var btnClear = new Button
                    {
                        Style = (Style)FindResource("PillButton"),
                        Height = 24, MinWidth = 60, Padding = new Thickness(9, 0, 9, 0),
                        Margin = new Thickness(6, 0, 0, 0),
                        Content = "清空",
                        Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                        ToolTip = "清掉已录的按键"
                    };
                    btnClear.Click += (_, __) =>
                    {
                        node.Set(p.Key, "");
                        MarkDirty();
                        listening = false;
                        pressedMods.Clear();
                        Render();
                    };
                    btnRow.Children.Add(btnClear);
                    block.Children.Add(btnRow);

                    _propUi.Add(keyBox);
                    break;
                }

                case FlowParamKind.FilePath:
                {
                    // 程序路径：可直接输入，也可用「浏览…」从磁盘选择
                    var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var box = new TextBox
                    {
                        Text = node.Get(p.Key, p.Default), MinHeight = 32,
                        Style = (Style)FindResource("InlineTextBox"),
                        VerticalContentAlignment = VerticalAlignment.Center
                    };
                    box.TextChanged += (_, __) => { node.Set(p.Key, box.Text ?? ""); MarkDirty(); };
                    Grid.SetColumn(box, 0);
                    var browse = new Button
                    {
                        Content = "浏览…", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 5, 10, 5),
                        Style = (Style)FindResource("GhostButton"), FontSize = 11
                    };
                    browse.Click += (_, __) =>
                    {
                        var dlg = new Microsoft.Win32.OpenFileDialog
                        {
                            Title = "选择要打开的程序",
                            Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                            CheckFileExists = true
                        };
                        try
                        {
                            if (dlg.ShowDialog(this) == true)
                            {
                                box.Text = dlg.FileName;
                                node.Set(p.Key, dlg.FileName);
                                MarkDirty();
                            }
                        }
                        catch { }
                    };
                    Grid.SetColumn(browse, 1);
                    row.Children.Add(box); row.Children.Add(browse);
                    block.Children.Add(row);
                    _propUi.Add(box); _propUi.Add(browse);
                    break;
                }

                case FlowParamKind.ProcessName:
                {
                    // 进程名：可从当前运行中的程序里挑，也可手填
                    var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var box = new TextBox
                    {
                        Text = node.Get(p.Key, p.Default), MinHeight = 32,
                        Style = (Style)FindResource("InlineTextBox"),
                        VerticalContentAlignment = VerticalAlignment.Center
                    };
                    box.TextChanged += (_, __) => { node.Set(p.Key, box.Text ?? ""); MarkDirty(); };
                    Grid.SetColumn(box, 0);
                    var pick = new Button
                    {
                        Content = "选程序…", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 5, 10, 5),
                        Style = (Style)FindResource("GhostButton"), FontSize = 11,
                        ToolTip = "从当前运行的进程中挑选"
                    };
                    pick.Click += (_, __) =>
                    {
                        try
                        {
                            // 用目标账号会话里的真实程序列表，避免手打错进程名
                            var target = node.Get("user");
                            if (target.Length == 0) target = _account.UserName;
                            var picker = new ProcessPickerWindow(target) { Owner = this };
                            if (picker.ShowDialog() == true && picker.Selected != null)
                            {
                                box.Text = picker.Selected.Name;
                                node.Set(p.Key, picker.Selected.Name);
                                MarkDirty();
                            }
                        }
                        catch (Exception ex) { ThemedMessage.Info(this, "提示", "选择程序失败：" + ex.Message); }
                    };
                    Grid.SetColumn(pick, 1);
                    row.Children.Add(box); row.Children.Add(pick);
                    block.Children.Add(row);
                    _propUi.Add(box); _propUi.Add(pick);
                    break;
                }

                case FlowParamKind.Select:
                {
                    // ★ 固定选项下拉：值域是枚举的参数不该让用户手打
                    //   （手打容易写错：finished / Finished / 完成 都可能）。
                    var combo = new ComboBox
                    {
                        Height = 36,
                        Margin = new Thickness(0, 6, 0, 0),
                        Padding = new Thickness(10, 0, 6, 0),
                        FontSize = 12.5,
                        Foreground = (Brush)FindResource("TextPrimary"),
                        Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                        BorderThickness = new Thickness(1)
                    };

                    string curVal = node.Get(p.Key, p.Default);
                    var opts = p.Options != null && p.Options.Count > 0
                        ? p.Options : new System.Collections.Generic.List<string>();

                    bool matched = false;
                    foreach (var raw in opts)
                    {
                        string text = raw, val = raw;
                        int bar = raw.IndexOf('|');
                        if (bar >= 0) { text = raw.Substring(0, bar); val = raw.Substring(bar + 1); }

                        var item = new ComboBoxItem { Content = text, Tag = val };
                        combo.Items.Add(item);
                        if (!matched && string.Equals(val, curVal, StringComparison.OrdinalIgnoreCase))
                        { item.IsSelected = true; matched = true; }
                    }

                    // 当前值不在选项里（老数据/手填过）→ 补一项，避免静默改值
                    if (!matched)
                    {
                        var extra = new ComboBoxItem
                        {
                            Content = string.IsNullOrEmpty(curVal) ? "（未选择）" : curVal,
                            Tag = curVal
                        };
                        combo.Items.Add(extra);
                        extra.IsSelected = true;
                    }

                    combo.SelectionChanged += (_, __) =>
                    {
                        if (combo.SelectedItem is ComboBoxItem it)
                        {
                            node.Set(p.Key, it.Tag as string ?? "");
                            MarkDirty();
                            Canvas.Redraw();
                        }
                    };
                    block.Children.Add(combo);
                    _propUi.Add(combo);
                    break;
                }

                case FlowParamKind.UserPicker:
                {
                    var combo = new ComboBox
                    {
                        Height = 36,
                        Margin = new Thickness(0, 6, 0, 0),
                        Padding = new Thickness(10, 0, 6, 0),
                        FontSize = 12.5,
                        Foreground = (Brush)FindResource("TextPrimary"),
                        Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                        BorderThickness = new Thickness(1)
                    };
                    combo.Items.Add(new ComboBoxItem { Content = "（本账号）", Tag = "" });
                    foreach (var a in AccountScope.Accounts(_cfg))
                        combo.Items.Add(new ComboBoxItem
                        {
                            Content = a.UserName + (string.IsNullOrWhiteSpace(a.DisplayName) ? "" : "　" + a.DisplayName),
                            Tag = a.UserName
                        });

                    string cur = node.Get(p.Key);
                    foreach (ComboBoxItem it in combo.Items)
                        if ((it.Tag as string) == cur) { it.IsSelected = true; break; }
                    if (combo.SelectedIndex < 0 && combo.Items.Count > 0) combo.SelectedIndex = 0;

                    combo.SelectionChanged += (_, __) =>
                    {
                        if (combo.SelectedItem is ComboBoxItem it)
                        {
                            node.Set(p.Key, it.Tag as string ?? "");
                            MarkDirty(); Canvas.Redraw();
                            Dispatcher.InvokeAsync(() => ShowPropPanel(node));
                        }
                    };
                    block.Children.Add(combo);
                    _propUi.Add(combo);
                    break;
                }

                case FlowParamKind.OneDragonPicker:
                {
                    var combo = new ComboBox
                    {
                        Height = 36,
                        Margin = new Thickness(0, 6, 0, 0),
                        Padding = new Thickness(10, 0, 6, 0),
                        FontSize = 12.5,
                        Foreground = (Brush)FindResource("TextPrimary"),
                        Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                        BorderThickness = new Thickness(1)
                    };
                    combo.Items.Add(new ComboBoxItem { Content = "（页面选定的那条）", Tag = "" });
                    foreach (var n in ListBgiConfigs("OneDragon", node.Get("user")))
                        combo.Items.Add(new ComboBoxItem { Content = n, Tag = n });

                    string cur = node.Get(p.Key);
                    foreach (ComboBoxItem it in combo.Items)
                        if ((it.Tag as string) == cur) { it.IsSelected = true; break; }
                    if (combo.SelectedIndex < 0) combo.SelectedIndex = 0;

                    combo.SelectionChanged += (_, __) =>
                    {
                        if (combo.SelectedItem is ComboBoxItem it)
                        {
                            node.Set(p.Key, it.Tag as string ?? "");
                            MarkDirty();
                            Canvas.Redraw();
                        }
                    };
                    block.Children.Add(combo);
                    _propUi.Add(combo);
                    break;
                }

                case FlowParamKind.ScriptGroupPicker:
                {
                    var combo = new ComboBox
                    {
                        Height = 36,
                        Margin = new Thickness(0, 6, 0, 0),
                        Padding = new Thickness(10, 0, 6, 0),
                        FontSize = 12.5,
                        Foreground = (Brush)FindResource("TextPrimary"),
                        Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                        BorderThickness = new Thickness(1)
                    };
                    combo.Items.Add(new ComboBoxItem { Content = "（未选择）", Tag = "" });
                    string savedGroup = node.Get(p.Key).Trim().Trim('"');
                    var groupNames = ListBgiConfigs("ScriptGroup", node.Get("user")).ToList();
                    if (savedGroup.Length > 0 && !groupNames.Contains(savedGroup, StringComparer.OrdinalIgnoreCase)) groupNames.Insert(0, savedGroup);
                    foreach (var n in groupNames)
                        combo.Items.Add(new ComboBoxItem { Content = n, Tag = n });

                    string cur = node.Get(p.Key);
                    foreach (ComboBoxItem it in combo.Items)
                        if ((it.Tag as string) == cur) { it.IsSelected = true; break; }
                    // 旧配置已选择时必须选中，不能因为目录未读取就清空。
                    if (combo.SelectedIndex < 0 && cur.Length > 0)
                    { var saved = new ComboBoxItem { Content = cur, Tag = cur }; combo.Items.Add(saved); saved.IsSelected = true; }

                    combo.SelectionChanged += (_, __) =>
                    {
                        if (combo.SelectedItem is ComboBoxItem it)
                        {
                            node.Set(p.Key, it.Tag as string ?? "");
                            MarkDirty(); Canvas.Redraw();
                        }
                    };
                    block.Children.Add(combo);
                    _propUi.Add(combo);
                    break;
                }

                case FlowParamKind.ActionPicker:
                {
                    var combo = new ComboBox
                    {
                        Height = 36,
                        Margin = new Thickness(0, 6, 0, 0),
                        Padding = new Thickness(10, 0, 6, 0),
                        FontSize = 12.5,
                        Foreground = (Brush)FindResource("TextPrimary"),
                        Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                        BorderThickness = new Thickness(1)
                    };
                    var none = new ComboBoxItem { Content = "（未选择）", Tag = "" };
                    combo.Items.Add(none);
                    foreach (var a in _cfg.Actions)
                        combo.Items.Add(new ComboBoxItem { Content = a.Name, Tag = a.Id });

                    string cur = node.Get(p.Key);
                    foreach (ComboBoxItem it in combo.Items)
                        if ((it.Tag as string) == cur) { it.IsSelected = true; break; }

                    combo.SelectionChanged += (_, __) =>
                    {
                        if (combo.SelectedItem is ComboBoxItem it)
                        {
                            node.Set(p.Key, it.Tag as string ?? "");
                            MarkDirty();
                            Canvas.Redraw();
                        }
                    };
                    block.Children.Add(combo);
                    _propUi.Add(combo);
                    break;
                }

                default: // Text / Number
                {
                    // ★ 高度 34 + GlassTextBox 的 Padding(12,9) 会把内容区压到只剩 16px，
                    //   文字几乎看不见（用户实测"输入框是隐形的"）。
                    //   这里减小内边距并显式设前景色，保证清晰可读。
                    var tb = new TextBox
                    {
                        Text = node.Get(p.Key, p.Default),
                        Height = 36,
                        Margin = new Thickness(0, 6, 0, 0),
                        Style = (Style)FindResource("GlassTextBox"),
                        Padding = new Thickness(10, 0, 10, 0),
                        VerticalContentAlignment = VerticalAlignment.Center,
                        FontSize = 12.5,
                        Foreground = (Brush)FindResource("TextPrimary"),
                        CaretBrush = (Brush)FindResource("CyanBrush"),
                        Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                        BorderThickness = new Thickness(1)
                    };
                    tb.TextChanged += (_, __) =>
                    {
                        node.Set(p.Key, tb.Text ?? "");
                        MarkDirty();
                        Canvas.Redraw();
                    };
                    block.Children.Add(tb);
                    _propUi.Add(tb);
                    break;
                }
            }

            if (!string.IsNullOrEmpty(p.Hint))
            {
                block.Children.Add(new TextBlock
                {
                    Text = p.Hint,
                    FontSize = 10,
                    Foreground = (Brush)FindResource("TextMuted"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }

            PropPanel.Children.Add(block);
        }

        // ── hover 菜单的单例定时器（避免多定时器叠加导致闪烁）──
        // ── 悬停菜单：单一心跳定时器轮询鼠标位置 ──
        private FrameworkElement? _activeCard;
        private FlowNodeKind _activeKind;

        /// <summary>所有类别卡片（kind + 元素），用于按鼠标位置反查。</summary>
        private readonly List<(FlowNodeKind kind, FrameworkElement card)> _allCards
            = new List<(FlowNodeKind, FrameworkElement)>();
        private DateTime? _menuLeftAt;          // 鼠标离开"卡片∪菜单"的时刻
        private DateTime _lastActivate = DateTime.MinValue;
        private int _dbgTick;

        /// <summary>调试：记录菜单开关，用于判断是否反复抖动。</summary>
        private void LogMenu(string action)
        {
            try
            {
                string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  [菜单] " + action + Environment.NewLine;
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoulLink");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "flow-menu.log"), line);
            }
            catch { }
        }



        // ══════════ 菜单开关（纯事件驱动，不用坐标）══════════

        private System.Windows.Threading.DispatcherTimer? _closeMenuTimer;

        /// <summary>取消待关闭（鼠标进入菜单或另一张卡片时调用）。</summary>
        private void CancelCloseMenu() => _closeMenuTimer?.Stop();

        /// <summary>安排 0.3 秒后关闭；期间鼠标回到菜单/卡片则取消。</summary>
        private void ScheduleCloseMenu()
        {
            if (MenuLayer.Visibility != Visibility.Visible) return;

            _closeMenuTimer ??= new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };

            _closeMenuTimer.Stop();
            _closeMenuTimer.Tick -= CloseMenuTick;
            _closeMenuTimer.Tick += CloseMenuTick;
            _closeMenuTimer.Start();
        }

        /// <summary>鼠标进入菜单 → 保持打开。</summary>
        private void KindMenuCard_MouseEnter(object sender, MouseEventArgs e)
        {
            CancelCloseMenu();
        }

        /// <summary>鼠标离开菜单 → 0.3 秒后关闭。</summary>
        private void KindMenuCard_MouseLeave(object sender, MouseEventArgs e)
        {
            ScheduleCloseMenu();
        }

        private void CloseMenuTick(object? sender, EventArgs e)
        {
            try
            {
                _closeMenuTimer?.Stop();
                if (KindMenuCard.IsMouseOver) return;   // 鼠标已移到菜单上
                MenuLayer.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        /// <summary>点击菜单外的空白区域 → 收起菜单。</summary>
        private void MenuLayer_MouseDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                // 点在卡片内部就不关
                if (e.OriginalSource is DependencyObject d && FindAncestor<Border>(d) == KindMenuCard)
                    return;
                MenuLayer.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d != null) { if (d is T found) return found; d = VisualTreeHelper.GetParent(d); }
            return null;
        }

        private void TitleRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { if (e.OriginalSource is DependencyObject d && FindAncestor<TextBox>(d) != null) return; DragMove(); } catch { }
        }

        /// <summary>这些执行器要启动 BGI，需要知道 BGI 在哪。</summary>
        private static bool UsesBgi(FlowNodeType t) => t switch
        {
            FlowNodeType.ActStartBgi => true,
            FlowNodeType.ActStartOneDragon => true,
            FlowNodeType.ActStartGroups => true,
            FlowNodeType.ActStopBgi => true,
            _ => false
        };

        /// <summary>
        /// 显示并允许修改 BGI 程序路径。
        /// ★ 用户要求：路径必须展示出来让用户确认是否有误。
        /// 解析顺序：该节点的用户 → 流程绑定账号 → 第一个有路径的账号。
        /// </summary>
        private void AddBgiPathEditor(FlowNode node)
        {
            var block = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };

            block.Children.Add(new TextBlock
            {
                Text = "BGI 程序路径",
                FontSize = 10.5,
                Foreground = (Brush)FindResource("TextMuted"),
                TextWrapping = TextWrapping.Wrap
            });

            // 当前生效的路径
            string user = node.Get("user").Trim();
            if (user.Length == 0) user = _account.UserName;

            // ★ 统一走解析器
            string path = BgiPathResolver.GetPath(user);
            // 只读当前目标账号的路径，不使用其它账号兜底。
            if (!string.IsNullOrEmpty(path) && !File.Exists(path)) path = "";

            var box = new TextBox
            {
                Text = path,
                IsReadOnly = true,
                Height = 58,
                Margin = new Thickness(0, 5, 0, 0),
                Style = (Style)FindResource("GlassTextBox"),
                Padding = new Thickness(10, 3, 10, 3),
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(string.IsNullOrEmpty(path)
                    ? Color.FromRgb(0xFF, 0x9B, 0x9B)
                    : Color.FromRgb(0x9F, 0xE8, 0xC8)),
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1)
            };

            if (string.IsNullOrEmpty(path))
                box.Text = "（还没有配置 BGI 路径，点下面的按钮选择）";

            block.Children.Add(box);

            // 浏览按钮
            var btn = new Button
            {
                Content = "选择 BGI 程序…",
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 7, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                FontSize = 11
            };
            btn.Click += (_, __) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "选择 BetterGI.exe",
                    Filter = "BetterGI (BetterGI.exe)|BetterGI.exe|可执行文件 (*.exe)|*.exe",
                    CheckFileExists = true
                };
                if (dlg.ShowDialog() != true) return;

                // ★ 用户明确选择 = 手动指定，走 Pin（自动探测之后不再覆盖）
                BgiDataCenter.SetPath(user, dlg.FileName);

                box.Text = dlg.FileName;
                box.Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xE8, 0xC8));
                MarkDirty();

                // 路径变了，一条龙/配置组列表要重新读
                Dispatcher.InvokeAsync(() => ShowPropPanel(node));
            };
            _bgiPickerButton = btn;
            block.Children.Add(btn);

            // 提示
            block.Children.Add(new TextBlock
            {
                Text = "这个路径按「目标用户」保存，不同账号可以用不同的 BGI。",
                FontSize = 9.5,
                Foreground = (Brush)FindResource("TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 0)
            });

            PropPanel.Children.Add(block);
        }

        private void MarkDirty()
        {
            _dirty = true;
            ClearFieldHighlights();
            RefreshValidateHint();
        }

        private void OnSharedBgiDataChanged(string userName)
        {
            var selected = Canvas.SelectedNode;
            if (selected != null && selected.Get("user").Length > 0 && !selected.Get("user").Equals(userName, StringComparison.OrdinalIgnoreCase)) return;
            if (selected == null && !_account.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase)) return;
            Dispatcher.InvokeAsync(() =>
            {
                if (Canvas.SelectedNode != null) ShowPropPanel(Canvas.SelectedNode);
                RefreshValidateHint();
            });
        }

        private List<string> ListBgiConfigs(string sub, string? userName = null)
        {
            string user = string.IsNullOrWhiteSpace(userName) ? _account.UserName : userName;
            return BgiDataCenter.ListConfigs(user, sub).ToList();
        }

        private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready) return;
            _flow.Name = NameBox.Text ?? "";
            _dirty = true;
        }

        private void RefreshDailyState()
        {
            if (DailyStateText == null) return;
            var today = _flow.TodayRunState();
            DailyStateText.Text = today.Text;
            DailyStateText.ToolTip = _flow.HasEnd
                ? "日界在「当天完成」节点上设置；到达日界自动清除标记并开始新一轮。"
                : "没有「当天完成」节点：每轮结束会重新获取条件，可再次执行。";
            DailyStateText.Foreground = new SolidColorBrush(today.WillRun
                ? Color.FromRgb(0x6E, 0xF0, 0xC8)
                : Color.FromRgb(0xFF, 0xB1, 0x4D));
        }
        private void OnFlowScanned(AutomationFlow flow)
        {
            if (flow.Id != _flow.Id) return;
            Dispatcher.InvokeAsync(() => { RefreshDailyState(); Canvas.SetLiveStates(flow.RuntimeStates); });
        }
        private async Task RefreshConditionPreview()
        {
            if (_previewBusy || !IsLoaded || !IsVisible) return;
            // 引导演示期间不要用真实求值覆盖演示中的状态灯。
            if (_guide != null) return;
            _previewBusy = true;
            int version = ++_previewVersion;
            try
            {
                int sid = AccountStateCenter.Get(_flow.AccountId)?.SessionId ?? 0;
                var states = await FlowPreview.EvaluateAsync(_flow, sid, _cfg).ConfigureAwait(false);
                if (version != _previewVersion) return;
                await Dispatcher.InvokeAsync(() => Canvas.SetLiveStates(states));
            }
            catch { /* 状态预览失败不能影响编辑，也不触发任何动作。 */ }
            finally { _previewBusy = false; }
        }
        internal static bool HasBusinessDaySelector(System.Windows.Controls.ComboBox box)
            => box.Items.Cast<ComboBoxItem>().Any(i => i.Tag as string == "0")
               && box.Items.Cast<ComboBoxItem>().Any(i => i.Tag as string == "4");

        /// <summary>END 节点属性：日界选择 + 今日是否已执行 + 手动清除标记。</summary>
        private void AddEndEditors(FlowNode node)
        {
            PropPanel.Children.Add(new TextBlock
            {
                Text = "日界", FontSize = 10.5, Foreground = (Brush)FindResource("TextMuted"),
                Margin = new Thickness(0, 12, 0, 0)
            });
            var combo = new ComboBox { Height = 32, Margin = new Thickness(0, 6, 0, 0) };
            combo.Items.Add(new ComboBoxItem { Content = "00:00（自然日）", Tag = "0" });
            combo.Items.Add(new ComboBoxItem { Content = "04:00（凌晨四点）", Tag = "4" });
            SelectByTagValue(combo, node.DayBoundaryHour.ToString());
            combo.SelectionChanged += (_, __) =>
            {
                if (combo.SelectedItem is not ComboBoxItem item || !int.TryParse(item.Tag as string, out int hour)) return;
                if (node.DayBoundaryHour == hour) return;
                node.DayBoundaryHour = hour;
                PlcLogic.ClearDailyLatch(_flow);
                _flow.RunComplete = false;
                _dirty = true;
                RefreshDailyState();
                Canvas.Redraw();
            };
            PropPanel.Children.Add(combo);
            _propUi.Add(combo);

            PropPanel.Children.Add(new TextBlock
            {
                Text = "今日状态", FontSize = 10.5, Foreground = (Brush)FindResource("TextMuted"),
                Margin = new Thickness(0, 12, 0, 0)
            });
            var todayState = _flow.TodayRunState();
            var stateText = new TextBlock
            {
                Text = todayState.Text,
                FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
                Foreground = new SolidColorBrush(todayState.WillRun
                    ? Color.FromRgb(0x6E, 0xF0, 0xC8) : Color.FromRgb(0xFF, 0xB1, 0x4D))
            };
            PropPanel.Children.Add(stateText);
            // 双向开关：勾选=今日运行，取消=今日不运行。任何时刻都能改，两个方向都立即生效。
            _suppressTodayToggle = true;
            var todayToggle = new CheckBox
            {
                Content = "今天运行这个流程",
                IsChecked = _flow.TodayRunState().WillRun,
                Style = (Style)FindResource("GlassCheckBox"),
                Margin = new Thickness(0, 8, 0, 0), FontSize = 11.5,
                IsEnabled = _flow.Enabled && !_flow.Paused
            };
            todayToggle.ToolTip = _flow.Enabled && !_flow.Paused
                ? "取消勾选＝今天不再运行；重新勾选＝恢复今天运行。"
                : "流程未启用或已暂停，今日不会运行；请先启用流程。";
            void ApplyTodayWanted(bool want)
            {
                var at = DateTime.Now;
                _flow.ToggleTodayRequested = want;
                _flow.RunResetRequested = false;
                PlcLogic.SetTodayRun(_flow, at, want);   // 立即反映到界面与保存内容
                _dirty = true;
                RefreshDailyState();
                RefreshValidateHint();
                var now = _flow.TodayRunState(at);
                stateText.Text = now.Text;
                stateText.Foreground = new SolidColorBrush(now.WillRun
                    ? Color.FromRgb(0x6E, 0xF0, 0xC8) : Color.FromRgb(0xFF, 0xB1, 0x4D));
            }
            todayToggle.Checked += (_, __) =>
            {
                if (_suppressTodayToggle) return;
                ApplyTodayWanted(true);
            };
            todayToggle.Unchecked += (_, __) =>
            {
                if (_suppressTodayToggle) return;
                ApplyTodayWanted(false);
            };
            _suppressTodayToggle = false;
            PropPanel.Children.Add(todayToggle);
        }

        private void EnabledCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            _flow.Enabled = EnabledCheck.IsChecked == true;
            _dirty = true;
            RefreshDailyState();
            RefreshValidateHint();
        }

        /// <summary>
        /// 把 WPF 的 Key 转成 RelayAgent.ParseVk 认识的名字。
        ///
        /// ★ 必须和 ParseVk 保持同一套词表，否则界面录进去了、发送端解析不出来，
        ///   表现为"录了 F12 但按下去没反应"（这种静默失败最难查）。
        /// </summary>
        private static string KeyNameOf(Key key, KeyEventArgs ev)
        {
            // 字母 / 数字：优先用**实际字符**，这样 Shift+1 录成 "!"、
            // 小键盘/输入法布局也按用户真按的字符走。
            if (key >= Key.A && key <= Key.Z) return key.ToString();
            if (key >= Key.D0 && key <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
            if (key >= Key.NumPad0 && key <= Key.NumPad9) return "NUMPAD" + (key - Key.NumPad0);
            if (key >= Key.F1 && key <= Key.F24) return "F" + (key - Key.F1 + 1);

            return key switch
            {
                Key.Return => "ENTER",
                Key.Escape => "ESC",
                Key.Space => "SPACE",
                Key.Tab => "TAB",
                Key.Back => "BACKSPACE",
                Key.Delete => "DELETE",
                Key.Insert => "INSERT",
                Key.Home => "HOME",
                Key.End => "END",
                Key.PageUp => "PAGEUP",
                Key.PageDown => "PAGEDOWN",
                Key.Up => "UP",
                Key.Down => "DOWN",
                Key.Left => "LEFT",
                Key.Right => "RIGHT",
                Key.OemMinus => "-",
                Key.OemPlus => "=",
                Key.OemComma => ",",
                Key.OemPeriod => ".",
                Key.OemQuestion => "/",
                Key.OemSemicolon => ";",
                Key.OemQuotes => "'",
                Key.OemOpenBrackets => "[",
                Key.OemCloseBrackets => "]",
                Key.OemPipe => "\\",
                Key.OemTilde => "`",
                _ => ""
            };
        }

        /// <summary>把下拉框选中到指定 Tag 值；找不到则选第一项。</summary>
        private static void SelectByTagValue(System.Windows.Controls.ComboBox box, string tag)
        {
            if (box == null) return;
            foreach (var obj in box.Items)
            {
                if (obj is ComboBoxItem it && (it.Tag as string) == tag) { box.SelectedItem = it; return; }
            }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (_dirty)
            {
                var r = ThemedMessage.YesNoCancel(this, "保存", "有未保存的修改，要保存吗？");
                if (r == MessageBoxResult.Cancel) return;
                if (r == MessageBoxResult.Yes && !SaveFlow()) return;
            }
            Close();
        }

        private bool SaveFlow(bool showMessage = true)
        {
            try
            {
                _flow.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "未命名流程" : NameBox.Text.Trim();
                _flow.Enabled = EnabledCheck.IsChecked == true;
                _flow.EnsurePlcModel();
                var report = FlowValidator.Validate(_flow, _cfg);
                if (report.HasError) { if (showMessage) ThemedMessage.Info(this, "流程不完整", report.ToText()); return false; }
                if (_isNew && !_cfg.Automations.Exists(f => f.Id == _flow.Id)) _cfg.Automations.Add(_flow);
                ConfigStore.Save(_cfg); _dirty = false;
                if (showMessage) ThemedMessage.Info(this, "提示", "流程已保存。\n" + (report.Issues.Count > 0 ? report.ToText() : "条件每轮重新判断；从不满足变为满足时执行动作。"));
                return true;
            }
            catch (Exception ex) { if (showMessage) ThemedMessage.Info(this, "保存失败", ex.Message); return false; }
        }

        private void Save_Click(object? sender, RoutedEventArgs e) => SaveFlow();



        private void DeleteFlow_Click(object sender, RoutedEventArgs e)
        {
            if (!ThemedMessage.Confirm(this, "确认", "确定删除整个流程「" + _flow.DisplayName + "」？")) return;

            _cfg.Automations.RemoveAll(f => f.Id == _flow.Id);
            ConfigStore.Save(_cfg);
            DialogResult = true;
            Close();
        }

        private async void RunNow_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 立即运行与关闭共用同一个保存事务，避免新流程未入库。
                if (!SaveFlow(showMessage: false)) return;

                // 运行前同样校验
                var vr = FlowValidator.Validate(_flow, _cfg);
                if (vr.HasError)
                {
                    var errs = new List<string>();
                    foreach (var i in vr.Issues) if (i.IsError) errs.Add("• " + i.Message);
                    ThemedMessage.Info(this, "流程不完整", "这个流程还不能运行，请先修正：\n\n" + string.Join("\n", errs));
                    return;
                }

                var sessions = await SessionService.QueryAsync();
                var s = SessionService.Find(sessions, _account.UserName);
                int sid = s?.Id ?? 0; // 无会话的触点也能判断；真正需要会话的 OUT 会报告明确错误。

                LogBus.Info("手动运行流程：" + _flow.DisplayName);
                var result = await FlowRunner.RunAsync(_flow, sid, _cfg, manual: true);
                Canvas.Redraw();
                RefreshDailyState();

                string detail = string.Join(Environment.NewLine, result.Steps);
                ThemedMessage.Info(this, "执行结果",
                    result.Message + (detail.Length > 0 ? ("\n\n" + detail) : ""));
            }
            catch (Exception ex)
            {
                ThemedMessage.Info(this, "提示", "运行失败：" + ex.Message);
            }
        }
    }
}
