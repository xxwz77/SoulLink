using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Controls
{
    public class FlowCanvas : Canvas
    {
        private AutomationFlow? _flow;
        private FlowNode? _selected;

        public event Action<FlowNode?>? NodeSelected;
        public event Action? Changed;
        public event Action<FlowNode>? NodeDeleteRequested;
        /// <summary>连线操作的人话提示（建立成功 / 被拒绝的原因）。</summary>
        public event Action<string>? Notice;
        /// <summary>画笔开关、连续连接的当前起点及结束原因。</summary>
        public event Action<string>? PaintStatusChanged;

        private FlowNode? _paintFrom;
        private bool _linkClickStarted;
        private readonly Dictionary<string, PlcNodeState> _liveOverrides = new(StringComparer.OrdinalIgnoreCase);

        public static readonly DependencyProperty PaintModeProperty =
            DependencyProperty.Register(nameof(PaintMode), typeof(bool), typeof(FlowCanvas),
                new FrameworkPropertyMetadata(false, OnPaintModeChanged));

        /// <summary>
        /// 画笔模式：按住左键从下方出口拖到目标，成功后自动以目标继续。
        /// 空白处松开结束本次串联；Esc 或关闭画笔结束全部连线状态。
        /// </summary>
        public bool PaintMode
        {
            get => (bool)GetValue(PaintModeProperty);
            set => SetValue(PaintModeProperty, value);
        }

        private static void OnPaintModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var canvas = (FlowCanvas)d;
            bool enabled = (bool)e.NewValue;
            canvas.CancelLink();
            canvas._paintFrom = null;
            canvas.Cursor = enabled ? Cursors.Cross : Cursors.Arrow;
            canvas._linkClickStarted = false;
            canvas.Redraw();
            if (enabled) canvas.Focus();
            canvas.ReportPaintStatus(enabled
                ? "画笔已开启：依次点击模块即可连线；也可从下方圆点拖出。Esc 取消"
                : "画笔已关闭");
        }

        private void ReportPaintStatus(string text)
        {
            // 画笔状态只更新画布内的浮层，绝不覆盖底部“能否运行”的校验提示。
            _paintHint = text ?? "";
            PaintStatusChanged?.Invoke(_paintHint);
            ShowCanvasHint();
        }
        private string _paintHint = "";
        private Border? _hintBubble;
        private void ShowCanvasHint()
        {
            try
            {
                if (string.IsNullOrEmpty(_paintHint)) { if (_hintBubble != null) _hintBubble.Visibility = Visibility.Collapsed; return; }
                if (_hintBubble == null)
                {
                    _hintBubble = new Border
                    {
                        CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7),
                        Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x18, 0x30)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0xE3, 0xFF)), BorderThickness = new Thickness(1),
                        IsHitTestVisible = false
                    };
                    _hintBubble.Child = new TextBlock
                    { FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 420,
                      Foreground = new SolidColorBrush(Color.FromRgb(0xBF, 0xEF, 0xFF)) };
                    Panel.SetZIndex(_hintBubble, 50);
                    Children.Add(_hintBubble);
                }
                ((TextBlock)_hintBubble.Child).Text = _paintHint;
                _hintBubble.Visibility = Visibility.Visible;
                _hintBubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double left = Math.Max(8, (ActualWidth - _hintBubble.DesiredSize.Width) / 2);
                SetLeft(_hintBubble, left); SetTop(_hintBubble, 12);
            }
            catch { }
        }

        // ★ 加宽加高：原来 178x62 太窄，执行器的参数摘要几乎全被截断
        //   （"执行快捷动作 · 动作：xxxx" 只能看到前几个字）。
        private const double NodeW = 228;
        private const double NodeH = 70;
        private readonly Dictionary<string, (Border Box, TextBlock Status, Ellipse? Out)> _statusViews = new();
        private readonly List<(FlowEdge Edge, Path Path, Polygon Arrow, Ellipse Dot)> _edgeViews = new();
        private DispatcherTimer? _statusTimer;

        private readonly Dictionary<string, Border> _nodeViews = new();

        // ★ 交互状态改成「显式模式位 + 一个连线上下文对象」。
        //
        //   原来用的是 _dragNode / _dragLine / _linkFrom / _linkBranch 四个互相独立的
        //   可空字段：任何一条路径漏清其中一个，下一次操作就会串味。
        //   "从『否』出口拉出来的线被记成『是』分支"就是这套结构埋下的隐患 ——
        //   现在同一时刻只可能处于一种模式，分支标签跟着拖拽对象走，不存在残留。
        private enum CanvasMode { Idle, DragNode, DragLink }
        private CanvasMode _mode = CanvasMode.Idle;

        /// <summary>一次连线拖拽的全部上下文（起点节点 + 分支 + 预览线）。</summary>
        private sealed class LinkDrag
        {
            public FlowNode From = null!;
            public string Branch = "";
            public Line Preview = null!;
            /// <summary>本次移动已经处理过的目标，避免移动和松开重复建线。</summary>
            public string ProcessedTargetId = "";
        }
        private LinkDrag? _link;

        public FlowCanvas()
        {
            Background = Brushes.Transparent;
            ClipToBounds = true;
            Focusable = true;
            PreviewMouseLeftButtonDown += (_, __) => Focus();
            // 名称框等子控件取得焦点时，Esc 仍能关闭画笔；使用 AddHandler
            // 以便在普通文本框自己的按键处理之后仍能收到 Escape。
            AddHandler(Keyboard.PreviewKeyDownEvent,
                new KeyEventHandler((_, e) =>
                {
                    if (e.Key != Key.Escape || !PaintMode) return;
                    PaintMode = false;
                    e.Handled = true;
                }), true);
            LostMouseCapture += (_, __) =>
            {
                // 另一个窗口抢走鼠标时取消预览，避免下一次点击沿用陈旧起点。
                if (_mode == CanvasMode.DragLink)
                {
                    CancelLink();
                    _paintFrom = null;
                }
            };
            IsVisibleChanged += (_, __) =>
            {
                if (!IsVisible)
                {
                    CancelLink();
                    _paintFrom = null;
                }
            };
            Width = 2400;
            Height = 1600;
            Loaded += (_, __) =>
            {
                _statusTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
                _statusTimer.Tick -= RefreshLive; _statusTimer.Tick += RefreshLive;
                _statusTimer.Start();
            };
            Unloaded += (_, __) => _statusTimer?.Stop();
        }

        public FlowNode? SelectedNode => _selected;
        /// <summary>界面只读预览状态；不进入流程持久化状态，不触发任何动作。</summary>
        public void SetLiveStates(IReadOnlyDictionary<string, PlcNodeState> states)
        {
            // 先确保节点视图存在（Redraw 会清空 _statusViews 并重建）
            if (_statusViews.Count == 0 && _flow != null) Redraw();
            // ★ 覆盖值必须在 Redraw 之后再填：Redraw 会重建视图，
            //   先填会被重建过程绕开，表现为灯永远停留在默认颜色。
            _liveOverrides.Clear();
            foreach (var kv in states) _liveOverrides[kv.Key] = new PlcNodeState
            { Value=kv.Value.Value, Input=kv.Value.Input, Output=kv.Value.Output, Available=kv.Value.Available };
            RefreshLive(this, EventArgs.Empty);
        }
        private bool TryLive(string id, out PlcNodeState state)
        {
            if (_liveOverrides.TryGetValue(id, out state!)) return true;
            if (_flow != null && _flow.RuntimeStates.TryGetValue(id, out state!)) return true;
            state = new PlcNodeState(); return false;
        }
        internal string ConnectionStatus => _paintFrom == null ? "" : _paintFrom.Id;

        public void Load(AutomationFlow? flow)
        {
            CancelLink();
            _paintFrom = null;
            _flow = flow;
            _selected = null;
            Redraw();
        }

        public void Select(FlowNode? node)
        {
            _selected = node;
            ApplySelectionVisual();
            NodeSelected?.Invoke(node);
        }

        public void Redraw()
        {
            // Redraw 会清空子元素；连线中的预览线由当前拖拽上下文单独保留。
            if (_link != null) Children.Remove(_link.Preview);
            Children.Clear();
            _nodeViews.Clear();
            _statusViews.Clear();
            _edgeViews.Clear();
            if (_flow == null) return;

            foreach (var e in _flow.Edges) DrawEdge(e);
            foreach (var n in _flow.Nodes) DrawNode(n);
            if (_link != null) Children.Add(_link.Preview);
            if (_hintBubble != null && _paintHint.Length > 0) { Children.Add(_hintBubble); }
            ApplySelectionVisual();
            HighlightPaintSource();
            if (_paintHint.Length > 0) ShowCanvasHint();
        }

        /// <summary>
        /// 各类别的强调色。
        /// 触发器=紫、条件=青、执行器=绿、结束=琥珀（独立颜色，用户要求）。
        /// </summary>
        private static Color AccentOf(FlowNodeKind kind) => kind switch
        {
            FlowNodeKind.Trigger => Color.FromRgb(0xA9, 0x7B, 0xFF),
            FlowNodeKind.Condition => Color.FromRgb(0x4F, 0xE3, 0xFF),
            FlowNodeKind.Terminate => Color.FromRgb(0xFF, 0xB1, 0x4D),   // 琥珀
            _ => Color.FromRgb(0x41, 0xF0, 0xC0)
        };

        private void DrawNode(FlowNode n)
        {
            Color accent = AccentOf(n.Kind);

            // 亮 = 本触点当前成立；是否导通还由上方导线单独表达。
            bool lit = n.IsContact && TryLive(n.Id, out var live) && live.Value;
            if (lit) accent = Color.FromRgb(0x41, 0xF0, 0xC0);

            var border = new Border
            {
                Width = NodeW, Height = NodeH,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1.5),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, accent.R, accent.G, accent.B)),
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x10, 0x18, 0x2E)),
                Cursor = PaintMode ? Cursors.Cross : Cursors.Hand,
                Tag = n,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = accent, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.28 }
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            grid.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(11, 0, 0, 11),
                Background = new SolidColorBrush(Color.FromArgb(0xCC, accent.R, accent.G, accent.B))
            });

            var icon = new TextBlock
            {
                Text = FlowNodeCatalog.GlyphOf(n.Type),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 15,
                Foreground = new SolidColorBrush(accent),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(11, 0, 0, 0)
            };
            Grid.SetColumn(icon, 1);
            grid.Children.Add(icon);

            var textStack = new StackPanel
            { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 10, 0) };
            textStack.Children.Add(new TextBlock
            {
                Text = n.Title,
                FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF5, 0xFF)),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            string summary = SummaryOf(n);
            if (n.IsContact) summary = (lit ? "● 满足 · " : "○ 不满足 · ") + summary;

            var statusView = new TextBlock
            {
                Text = summary, FontSize = 10,
                Foreground = new SolidColorBrush(lit
                    ? Color.FromRgb(0x6E, 0xF0, 0xC8)
                    : Color.FromRgb(0x7A, 0x8C, 0xB4)),
                // ★ 允许两行：参数摘要常常较长（配置组名、一条龙名），
                //   单行省略号会让人看不出到底选了哪个。
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 26,
                Margin = new Thickness(0, 2, 0, 0)
            };
            textStack.Children.Add(statusView);
            _statusViews[n.Id] = (border, statusView, null);
            Grid.SetColumn(textStack, 2);
            grid.Children.Add(textStack);

            border.Child = grid;
            SetLeft(border, n.X);
            SetTop(border, n.Y);
            Children.Add(border);
            _nodeViews[n.Id] = border;

            // 唯一输出口在下缘。END 是终端；单出口允许扇出多条普通导线。
            if (n.Kind != FlowNodeKind.Terminate)
            {
                var outPort = BuildPort(accent);
                SetLeft(outPort, n.X + NodeW / 2 - 12);
                SetTop(outPort, n.Y + NodeH - 12);
                Children.Add(outPort);
                _statusViews[n.Id] = (border, statusView, outPort);
                outPort.PreviewMouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    BeginLink(n, "", e.GetPosition(this));
                    if (PaintMode) _linkClickStarted = true;
                };
                outPort.ToolTip = "从这里连接到下一个模块";
            }

            // ★ 入口放顶边中间（用户要求）：判定节点左右两侧被"是/否"占用，
            //   输入若还在左边会和输出挤在一起，分不清哪条是进哪条是出。
            var inPort = BuildPort(accent, false);
            SetLeft(inPort, n.X + NodeW / 2 - 6);
            SetTop(inPort, n.Y - 6);
            Children.Add(inPort);
            if (n.Kind == FlowNodeKind.Terminate) _statusViews[n.Id] = (border, statusView, null);

            border.MouseLeftButtonDown += (_, e) => BeginDrag(n, border, e);
            border.MouseRightButtonUp += (_, __) => NodeDeleteRequested?.Invoke(n);
        }

        private static Ellipse BuildPort(Color accent, bool filled = true)
        {
            return new Ellipse
            {
                Width = 24, Height = 24,
                Fill = filled ? new SolidColorBrush(accent)
                              : new SolidColorBrush(Color.FromArgb(0x33, 0x10, 0x18, 0x2E)),
                Stroke = new SolidColorBrush(Color.FromArgb(filled ? (byte)0xFF : (byte)0x99, accent.R, accent.G, accent.B)),
                StrokeThickness = 2,
                Cursor = filled ? Cursors.Cross : Cursors.Arrow
            };
        }

        private static string SummaryOf(FlowNode n)
        {
            switch (n.Type)
            {
                case FlowNodeType.TrigSchedule:
                {
                    // 新格式是 time="HH:mm"，老流程是 hour/minute/second
                    string tt = n.Get("time").Trim();
                    if (tt.Length > 0) return tt;
                    return n.GetInt("hour", 4).ToString("00") + ":"
                         + n.GetInt("minute", 0).ToString("00");
                }
                case FlowNodeType.TrigInterval:
                {
                    int sec = n.GetInt("intervalSeconds", 600);
                    return sec % 60 == 0 ? ("每 " + (sec / 60) + " 分钟") : ("每 " + sec + " 秒");
                }
                case FlowNodeType.TrigBgiIdle:
                    return "空闲 " + n.GetInt("idleSeconds", 300) + " 秒";
                case FlowNodeType.TrigLogMatch:
                    return "匹配：" + Trunc(n.Get("pattern"), 16);
                case FlowNodeType.CondTimeRange:
                    return n.Get("from", "00:00") + " - " + n.Get("to", "23:59");
                case FlowNodeType.CondBgiActive:
                    return "最近 " + n.GetInt("withinSeconds", 120) + " 秒";
                case FlowNodeType.CondLogContains:
                case FlowNodeType.CondLogMissing:
                    return Trunc(n.Get("pattern"), 18);
                case FlowNodeType.ActRunAction:
                    return "动作：" + Trunc(n.Get("actionId"), 12);
                case FlowNodeType.ActStartOneDragon:
                    return n.Get("name").Length > 0 ? n.Get("name") : "页面选定的一条龙";
                case FlowNodeType.ActStartGroups:
                    return Trunc(n.Get("groups"), 20);
                case FlowNodeType.ActDelay:
                    return "等待 " + n.GetInt("seconds", 10) + " 秒";
                case FlowNodeType.ActStartBgi:
                    return Trunc(n.Get("args"), 20);
                case FlowNodeType.CondBgiRunning:
                case FlowNodeType.CondGenshinRunning:
                    return n.GetBool("expect", true) ? "期望：在运行" : "期望：未运行";
                default:
                    return FlowNodeCatalog.Get(n.Type)?.Desc ?? "";
            }
        }

        private static string Trunc(string? s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "（未设置）";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        private void DrawEdge(FlowEdge e)
        {
            var from = _flow?.FindNode(e.From);
            var to = _flow?.FindNode(e.To);
            if (from == null || to == null) return;

            // ★ 起点跟着分支端口走（与新布局一致）：
            //   是 = 右边中间，否 = 底边中间，普通（执行器）= 右边中间
            double x1 = from.X + NodeW / 2;
            double y1 = from.Y + NodeH;

            // ★ 终点落在目标节点的顶部入口
            double x2 = to.X + NodeW / 2;
            double y2 = to.Y;

            var path = BuildBezier(x1, y1, x2, y2);

            bool powered = TryLive(from.Id, out var power) && power.Output;
            path.Stroke = new SolidColorBrush(powered ? Color.FromArgb(0xEE, 0x41, 0xF0, 0xC0) : Color.FromArgb(0x88, 0x65, 0x75, 0x91));

            path.Cursor = Cursors.Hand;
            path.ToolTip = "双击或右键删除这条连线";
            path.MouseLeftButtonDown += (_, ev) =>
            {
                if (ev.ClickCount == 2) { RemoveEdge(e); ev.Handled = true; }
            };
            // ★ 右键也能删：双击在细线上不好命中（用户反馈"已连接的线删不掉"）
            path.MouseRightButtonUp += (_, ev) => { RemoveEdge(e); ev.Handled = true; };

            Children.Add(path);

            // ★ 终点补一个小实心点：连线落在输入圆点上时，
            //   覆盖住空心圆，肉眼能确认"已插上"（之前箭头挡在前一格，
            //   看着像没连到节点上）。
            var endDot = new Ellipse
            {
                Width = 8, Height = 8,
                Fill = path.Stroke,
                IsHitTestVisible = false
            };
            SetLeft(endDot, x2 - 4);
            SetTop(endDot, y2 - 4);
            Children.Add(endDot);

            // 箭头也绑同样的删除事件，整条线都能点
            var arrow = BuildArrow(x2, y2, x1, y1);
            // ★ 箭头颜色必须跟线一致，否则分支色只剩半条线有色（用户反馈"没颜色区分"）
            arrow.Fill = path.Stroke;
            arrow.IsHitTestVisible = true;
            arrow.Cursor = Cursors.Hand;
            arrow.ToolTip = "双击或右键删除这条连线";
            arrow.MouseLeftButtonDown += (_, ev) =>
            {
                if (ev.ClickCount == 2) { RemoveEdge(e); ev.Handled = true; }
            };
            arrow.MouseRightButtonUp += (_, ev) => { RemoveEdge(e); ev.Handled = true; };
            Children.Add(arrow);
            _edgeViews.Add((e, path, arrow, endDot));
        }

        private static Path BuildBezier(double x1, double y1, double x2, double y2)
        {
            // ★ 目标在左边时，原来固定用 x1+dx 与 x2-dx 会把曲线拉成"绕一圈"的环
            //   （截图里能看到线从左绕回右）。这里按相对位置调整控制点：
            //   · 正常（目标在右）：左右各撑开一段，形成平滑 S 形
            //   · 反向（目标在左）：控制点改为同向，走一条不过节点的弧
            // ★ 终点在"顶部入口"，所以曲线应当最后一段**竖直向下**进入，
            //   否则会斜着插进节点、看起来像没连上。
            //   起点在右侧 → 先水平出去再拐；起点在底部 → 先竖直下去。
            double dx = Math.Max(50, Math.Abs(x2 - x1) * 0.5);
            double dy = Math.Max(40, Math.Abs(y2 - y1) * 0.5);

            var geo = new PathGeometry();
            var fig = new PathFigure { StartPoint = new Point(x1, y1) };

            // 控制点 1：沿起点法线方向伸出
            var c1 = new Point(x1, y1 + dy);
            // 控制点 2：沿终点法线方向进入（终点法线 = 竖直，所以 y 方向抬高）
            var c2 = new Point(x2, y2 - dy);

            fig.Segments.Add(new BezierSegment(c1, c2, new Point(x2, y2), true));
            geo.Figures.Add(fig);

            return new Path
            {
                Data = geo,
                Stroke = new SolidColorBrush(Color.FromArgb(0x99, 0x4F, 0xE3, 0xFF)),
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
        }

        private static Polygon BuildArrow(double x2, double y2, double x1, double y1)
        {
            double ang = Math.Atan2(y2 - y1, x2 - x1);
            const double len = 9, half = 4.5;
            var p1 = new Point(x2, y2);
            var p2 = new Point(x2 - len * Math.Cos(ang) + half * Math.Sin(ang),
                               y2 - len * Math.Sin(ang) - half * Math.Cos(ang));
            var p3 = new Point(x2 - len * Math.Cos(ang) - half * Math.Sin(ang),
                               y2 - len * Math.Sin(ang) + half * Math.Cos(ang));
            return new Polygon
            {
                Points = new PointCollection { p1, p2, p3 },
                Fill = new SolidColorBrush(Color.FromArgb(0xCC, 0x4F, 0xE3, 0xFF)),
                IsHitTestVisible = false
            };
        }

        /// <summary>
        /// 当前正在被拖动的节点。
        /// ★ 必须是**实例级**状态：原来每个节点各自 += MouseMove，
        ///   一次按下所有节点都挂上了处理器，于是"拖一个带动全部"
        ///   （用户实测：多个条件模块被同时拖动）。
        /// </summary>
        private FlowNode? _dragNode;
        private Point _dragStart;
        // _mode 定义见文件顶部（CanvasMode）
        private double _dragOriginX, _dragOriginY;
        private bool _dragMoved;

        private void BeginDrag(FlowNode n, Border view, MouseButtonEventArgs e)
        {
            Select(n);
            if (e.ClickCount == 2) { e.Handled = true; return; }

            // ★ 同一时刻只允许一种交互：正在拉连线时点节点不启动拖动。
            //   原来两者靠"谁的可空字段不为空"各自判断，没有互斥保证。
            if (PaintMode)
            {
                if (n.Kind != FlowNodeKind.Terminate && (_link == null || _paintFrom == null))
                {
                    BeginLink(n, "", e.GetPosition(this));
                    _linkClickStarted = true;
                    e.Handled = true;
                    return;
                }
                // 上一次成功连接的预览仍跟随鼠标；点下一个目标即可继续，
                // 按住不放还可继续扫过后续目标。绝不允许从节点本体另起新线。
                if (_link != null && _paintFrom != null && _link.From.Id == _paintFrom.Id
                    && n.Id != _link.From.Id)
                    TryPaintLink(n, e.GetPosition(this), true);
                e.Handled = true;
                return;
            }
            if (_mode != CanvasMode.Idle) { e.Handled = true; return; }

            // 记录起点，后续移动统一走画布级处理器
            _dragNode = n;
            _dragStart = e.GetPosition(this);
            _dragOriginX = n.X;
            _dragOriginY = n.Y;
            _dragMoved = false;
            _mode = CanvasMode.DragNode;

            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            // 拖动节点
            if (_mode == CanvasMode.DragNode && _dragNode != null)
            {
                var p = e.GetPosition(this);
                double nx = Math.Max(0, _dragOriginX + (p.X - _dragStart.X));
                double ny = Math.Max(0, _dragOriginY + (p.Y - _dragStart.Y));
                if (Math.Abs(nx - _dragNode.X) < 0.5 && Math.Abs(ny - _dragNode.Y) < 0.5) return;
                _dragNode.X = nx;
                _dragNode.Y = ny;
                _dragMoved = true;
                Redraw();
                return;
            }

            // 拉连线
            if (_mode == CanvasMode.DragLink && _link != null)
                UpdateLink(e.GetPosition(this),
                    e.LeftButton == MouseButtonState.Pressed || (PaintMode && _linkClickStarted));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);

            switch (_mode)
            {
                case CanvasMode.DragNode:
                {
                    bool moved = _dragMoved;
                    _dragNode = null;
                    _mode = CanvasMode.Idle;
                    ReleaseMouseCapture();
                    if (moved) Changed?.Invoke();
                    e.Handled = true;
                    return;
                }

                case CanvasMode.DragLink:
                {
                    if (PaintMode && _linkClickStarted && _paintFrom == _link?.From)
                    {
                        UpdateLink(e.GetPosition(this), true);
                        e.Handled = true;
                        return;
                    }
                    FinishLink(e.GetPosition(this));
                    e.Handled = true;
                    return;
                }
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key != Key.Escape || (!PaintMode && _link == null)) return;
            if (PaintMode) PaintMode = false;
            else CancelLink();
            e.Handled = true;
        }

        private void UpdateLink(Point point, bool buttonDown)
        {
            var link = _link;
            if (link == null) return;
            // 普通模式丢失松开事件时取消；画笔成功后的下一段预览则继续跟随鼠标。
            if (!buttonDown && (!PaintMode || _paintFrom == null))
            {
                CancelLink();
                return;
            }

            var hover = FindNodeAt(point, link.From);
            HighlightTarget(hover);
            if (buttonDown && PaintMode && hover != null && hover.Id != link.ProcessedTargetId)
            {
                // 失败也只提示一次；离开这个目标后可以重试。
                link.ProcessedTargetId = hover.Id;
                // 画笔可按住左键依次扫过多个目标，每次成功立即推进起点。
                TryPaintLink(hover, point, true);
                return;
            }
            if (hover != null)
            {
                link.Preview.X2 = hover.X + NodeW / 2;
                link.Preview.Y2 = hover.Y;
            }
            else
            {
                link.ProcessedTargetId = "";
                link.Preview.X2 = point.X;
                link.Preview.Y2 = point.Y;
            }
        }

        private void FinishLink(Point point)
        {
            var link = _link;
            if (link == null) return;
            // 松开时包含当前起点：一次成功拖动停在原目标上不会被误判为空白。
            var target = FindNodeAt(point, PaintMode ? null : link.From);
            if (target == null)
            {
                if (PaintMode) EndPaintStroke();
                else CancelLink();
                return;
            }

            if (PaintMode)
            {
                // 重复经过同一目标不会再次建线；失败时仍保留原来的起点。
                bool atCurrent = target.Id == link.From.Id || target.Id == link.ProcessedTargetId;
                if (!atCurrent) TryPaintLink(target, point, false);
                if (_link == null) return; // 到达「当天完成」时已结束本次串联。
                if (_paintFrom != null && (atCurrent || _link.From.Id == _paintFrom.Id))
                {
                    // 关键：有效目标上松开并不结束画笔，预览自动从刚连上的目标继续。
                    _link.Preview.X2 = point.X;
                    _link.Preview.Y2 = point.Y;
                    HighlightPaintSource();
                    ReportPaintStatus("当前起点：「" + _paintFrom.Title
                        + "」；继续拖到下一个目标，空白处松开或 Esc 结束");
                }
            }
            else ConnectEdge(link.From, target, "");
            HighlightTarget(null);
        }

        private bool TryPaintLink(FlowNode target, Point pointer, bool keepDragging)
        {
            var link = _link;
            if (link == null) return false;
            var from = link.From;
            if (!ConnectEdge(from, target, "")) return false;

            string fromTitle = from.Title;
            _paintFrom = target;
            link.From = target;
            link.ProcessedTargetId = target.Id;
            link.Preview.X1 = target.X + NodeW / 2;
            link.Preview.Y1 = target.Y + NodeH;
            link.Preview.X2 = keepDragging ? pointer.X : link.Preview.X1;
            link.Preview.Y2 = keepDragging ? pointer.Y : link.Preview.Y1;
            HighlightPaintSource();
            if (target.Kind == FlowNodeKind.Terminate)
            {
                CancelLink();
                _paintFrom = null;
                Redraw();
                ReportPaintStatus("已连接「当天完成」；终点不能继续连出，本次串联结束，画笔仍开启");
            }
            else ReportPaintStatus("已连接「" + fromTitle + "」→「" + target.Title
                + "」；当前起点为「" + target.Title + "」，继续拖动或松开保留起点");
            return true;
        }

        private void BeginLink(FlowNode from, string branch, Point? pointer = null)
        {
            // 画笔只能从下方出口开始，不能通过节点本体绕过终止节点检查。
            if (_mode != CanvasMode.Idle || _flow == null) return;
            if (PaintMode && from.Kind == FlowNodeKind.Terminate)
            {
                Notice?.Invoke("「当天完成」是最后一步，不能继续连出");
                return;
            }

            string b = "";
            var start = new Point(from.X + NodeW / 2, from.Y + NodeH);
            var end = pointer ?? start;
            var preview = new Line
            {
                X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y,
                Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0x4F, 0xE3, 0xFF)),
                StrokeThickness = 2.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                IsHitTestVisible = false
            };
            Children.Add(preview);

            _link = new LinkDrag { From = from, Branch = b, Preview = preview };
            Panel.SetZIndex(preview, 30);
            _mode = CanvasMode.DragLink;
            if (PaintMode)
            {
                _paintFrom = from;
                Mouse.Capture(this, CaptureMode.SubTree);
                ReportPaintStatus("画笔起点：「" + from.Title + "」；拖到目标，空白处松开或 Esc 结束");
            }
            else CaptureMouse();
        }

        private void CancelLink()
        {
            var link = _link;
            _link = null;
            if (_mode == CanvasMode.DragLink) _mode = CanvasMode.Idle;
            if (link != null) Children.Remove(link.Preview);
            if (IsMouseCaptured) ReleaseMouseCapture();
            HighlightTarget(null);
        }

        private void EndPaintStroke()
        {
            CancelLink();
            _paintFrom = null;
            Redraw();
            if (PaintMode)
                ReportPaintStatus("已在空白处结束本次串联；画笔仍开启，可从下方出口重新开始");
        }
        private void RefreshLive(object? sender, EventArgs e)
        {
            if (_flow == null) return;
            foreach (var kv in _statusViews)
            {
                var node = _flow.FindNode(kv.Key); if (node == null) continue;
                bool lit = TryLive(node.Id, out var state) && state.Value;
                bool unknown = node.IsContact && !state.Available;
                string text = node.IsContact ? (unknown ? "⚠ 数据不可用 · " : lit ? "● 满足 · " : "○ 不满足 · ") + SummaryOf(node) : SummaryOf(node);
                kv.Value.Status.Text = text;
                kv.Value.Status.Foreground = new SolidColorBrush(unknown ? Color.FromRgb(0xFF,0xD9,0x8A) : lit ? Color.FromRgb(0x6E,0xF0,0xC8) : Color.FromRgb(0x7A,0x8C,0xB4));
                kv.Value.Box.BorderBrush = new SolidColorBrush(unknown ? Color.FromRgb(0xFF,0xB1,0x4D) : lit ? Color.FromRgb(0x41,0xF0,0xC0) : AccentOf(node.Kind));
                if (kv.Value.Out != null) kv.Value.Out.Fill = new SolidColorBrush(state?.Output == true ? Color.FromRgb(0x41,0xF0,0xC0) : Color.FromRgb(0x65,0x75,0x91));
            }
            foreach (var item in _edgeViews)
            {
                bool power = TryLive(item.Edge.From, out var st) && st.Output;
                item.Path.Stroke = new SolidColorBrush(power ? Color.FromArgb(0xEE,0x41,0xF0,0xC0) : Color.FromArgb(0x88,0x65,0x75,0x91));
                item.Arrow.Fill = item.Path.Stroke; item.Dot.Fill = item.Path.Stroke;
            }
        }

        /// <summary>吸附半径：落点离节点边框在这个距离内就算连上。</summary>
        private const double SnapRadius = 46;

        /// <summary>
        /// 找到落点附近的节点。
        /// ★ 用户要求：连线拖到模块附近就行，不用精确对准节点本体。
        ///   这里用"矩形外扩 SnapRadius"判定，越近的优先。
        ///   同时按类型过滤：不能连回触发器、不能连自己。
        /// </summary>
        private FlowNode? FindNodeAt(Point p, FlowNode? from = null)
        {
            if (_flow == null) return null;

            FlowNode? best = null;
            double bestDist = double.MaxValue;

            foreach (var n in _flow.Nodes)
            {
                // 不能连自己
                if (from != null && n.Id == from.Id) continue;
                // ★ 现在允许连到任何节点（包括原来标为"触发器"的）：
                //   入口改由"谁没有上游连线"自动决定，
                //   给它连一条输入，它就自动退化成普通条件节点。

                // 点到矩形的距离
                double cx = Math.Max(n.X, Math.Min(p.X, n.X + NodeW));
                double cy = Math.Max(n.Y, Math.Min(p.Y, n.Y + NodeH));
                double dx = p.X - cx, dy = p.Y - cy;
                double dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist <= SnapRadius && dist < bestDist)
                {
                    bestDist = dist;
                    best = n;
                }
            }

            return best;
        }

        /// <summary>删除一条连线。</summary>
        private void RemoveEdge(FlowEdge e)
        {
            if (_flow == null) return;
            if (!_flow.Edges.Remove(e)) return;
            Redraw();
            Changed?.Invoke();
        }

        /// <summary>
        /// 建立连线，并按模块类型做合法性校验。
        /// ★ 用户要求：根据模块类型自动判断连线。
        ///
        /// 规则：
        ///   · 触发器 → 任何（条件/执行器）✔
        ///   · 条件   → 条件（串联）✔ / 执行器 ✔
        ///   · 执行器 → 条件（判断执行结果）✔ / 执行器 ✔
        ///   · 任何   → 触发器 ✘（触发器只能有一个，且没有输入）
        ///   · 自己 → 自己 ✘
        ///   · 重复连线 ✘
        /// </summary>
        private bool ConnectEdge(FlowNode from, FlowNode to, string branch)
        {
            if (to.Kind == FlowNodeKind.Terminate && _flow != null && _flow.TargetsOf(to.Id).Count > 0)
            { Notice?.Invoke("「当天完成」是最后一步，不能连接后续模块"); return false; }
            var r = FlowGraphRules.Connect(_flow, from, to, "", out var replaced);

            switch (r)
            {
                case EdgeConnectResult.Duplicate:
                    Notice?.Invoke("「" + PortName(from, branch) + "」已经连着「" + to.Title + "」了");
                    return false;
                case EdgeConnectResult.Self:
                    Notice?.Invoke("不能连到它自己");
                    return false;
                case EdgeConnectResult.Cycle:
                    Notice?.Invoke("这条线会让流程绕成环，已拒绝");
                    return false;
                case EdgeConnectResult.Invalid:
                    Notice?.Invoke("「当天完成」不能连接后续模块");
                    return false;
            }

            // 先结束普通模式的一次性拖拽，再重绘；画笔则保留预览继续下一段。
            if (!PaintMode && _mode == CanvasMode.DragLink) CancelLink();
            Redraw();
            Changed?.Invoke();
            if (r == EdgeConnectResult.Replaced && replaced != null)
            {
                var old = _flow?.FindNode(replaced.To);
                Notice?.Invoke("「" + PortName(from, branch) + "」原来连着「"
                    + (old?.Title ?? "模块") + "」，已换成「" + to.Title + "」");
            }
            return r == EdgeConnectResult.Added || r == EdgeConnectResult.Replaced;
        }

        /// <summary>出口的人话名称，用于提示。</summary>
        private static string PortName(FlowNode from, string branch) => from.Title + " · 输出";

        /// <summary>高亮"将要连上"的目标节点（null = 清除高亮）。</summary>
        private void HighlightTarget(FlowNode? target)
        {
            foreach (var kv in _nodeViews)
            {
                bool isTarget = target != null && kv.Key == target.Id;
                var node = _flow?.FindNode(kv.Key);
                if (node == null) continue;

                Color accent = AccentOf(node.Kind);
                if (_flow != null && node.IsContact && TryLive(node.Id, out var state) && state.Value)
                    accent = Color.FromRgb(0x41, 0xF0, 0xC0);

                kv.Value.BorderBrush = new SolidColorBrush(isTarget
                    ? Color.FromRgb(0x4F, 0xE3, 0xFF)
                    : Color.FromArgb(0xAA, accent.R, accent.G, accent.B));
                kv.Value.BorderThickness = new Thickness(isTarget ? 2.5 : 1.5);
            }
        }

        private void HighlightPaintSource()
        {
            if (!PaintMode || _paintFrom == null) return;
            if (_nodeViews.TryGetValue(_paintFrom.Id, out var box))
            {
                box.BorderBrush = new SolidColorBrush(Color.FromRgb(0x6E, 0xF0, 0xC8));
                box.BorderThickness = new Thickness(2.5);
                box.Opacity = 1;
            }
            if (_statusViews.TryGetValue(_paintFrom.Id, out var status) && status.Out != null)
                status.Out.StrokeThickness = 3;
        }

        private void ApplySelectionVisual()
        {
            foreach (var kv in _nodeViews)
            {
                bool sel = _selected != null && kv.Key == _selected.Id;
                kv.Value.BorderThickness = new Thickness(sel ? 2.5 : 1.5);
                kv.Value.Opacity = sel ? 1.0 : 0.94;
            }
            HighlightPaintSource();
        }
    }
}
