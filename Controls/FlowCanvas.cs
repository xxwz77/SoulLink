using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SoulLink.Models;

namespace SoulLink.Controls
{
    public class FlowCanvas : Canvas
    {
        private AutomationFlow? _flow;
        private FlowNode? _selected;

        public event Action<FlowNode?>? NodeSelected;
        public event Action? Changed;
        public event Action<FlowNode>? NodeDeleteRequested;

        private const double NodeW = 178;
        private const double NodeH = 62;

        private readonly Dictionary<string, Border> _nodeViews = new();
        private Line? _dragLine;
        private FlowNode? _linkFrom;

        public FlowCanvas()
        {
            Background = Brushes.Transparent;
            ClipToBounds = true;
            Width = 2400;
            Height = 1600;
        }

        public FlowNode? SelectedNode => _selected;

        public void Load(AutomationFlow? flow)
        {
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
            Children.Clear();
            _nodeViews.Clear();
            if (_flow == null) return;

            foreach (var e in _flow.Edges) DrawEdge(e);
            foreach (var n in _flow.Nodes) DrawNode(n);
            ApplySelectionVisual();
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

            // ★ 条件节点：显示锁存状态
            //   已满足 = 绿色边框 + 勾
            //   等待中 = 原色
            bool latched = _flow != null && n.Kind == FlowNodeKind.Condition
                           && _flow.IsLatched(n.Id);
            if (latched) accent = Color.FromRgb(0x41, 0xF0, 0xC0);

            var border = new Border
            {
                Width = NodeW, Height = NodeH,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1.5),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, accent.R, accent.G, accent.B)),
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x10, 0x18, 0x2E)),
                Cursor = Cursors.Hand,
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
                Text = n.Title, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF5, 0xFF)),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            string summary = SummaryOf(n);
            if (latched) summary = "✔ 已满足 · " + summary;

            textStack.Children.Add(new TextBlock
            {
                Text = summary, FontSize = 10,
                Foreground = new SolidColorBrush(latched
                    ? Color.FromRgb(0x6E, 0xF0, 0xC8)
                    : Color.FromRgb(0x7A, 0x8C, 0xB4)),
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(textStack, 2);
            grid.Children.Add(textStack);

            border.Child = grid;
            SetLeft(border, n.X);
            SetTop(border, n.Y);
            Children.Add(border);
            _nodeViews[n.Id] = border;

            var outPort = BuildPort(accent);
            SetLeft(outPort, n.X + NodeW - 6);
            SetTop(outPort, n.Y + NodeH / 2 - 6);
            Children.Add(outPort);
            outPort.MouseLeftButtonDown += (_, e) => BeginLink(n, e);
            outPort.ToolTip = "从这里拖到另一个节点建立连线";

            var inPort = BuildPort(accent, false);
            SetLeft(inPort, n.X - 6);
            SetTop(inPort, n.Y + NodeH / 2 - 6);
            Children.Add(inPort);

            border.MouseLeftButtonDown += (_, e) => BeginDrag(n, border, e);
            border.MouseRightButtonUp += (_, __) => NodeDeleteRequested?.Invoke(n);
        }

        private static Ellipse BuildPort(Color accent, bool filled = true)
        {
            return new Ellipse
            {
                Width = 12, Height = 12,
                Fill = filled ? new SolidColorBrush(accent)
                              : new SolidColorBrush(Color.FromArgb(0x33, 0x10, 0x18, 0x2E)),
                Stroke = new SolidColorBrush(Color.FromArgb(filled ? (byte)0xFF : (byte)0x99, accent.R, accent.G, accent.B)),
                StrokeThickness = 1.5,
                Cursor = filled ? Cursors.Cross : Cursors.Arrow
            };
        }

        private static string SummaryOf(FlowNode n)
        {
            switch (n.Type)
            {
                case FlowNodeType.TrigSchedule:
                    return n.GetInt("hour", 4).ToString("00") + ":"
                         + n.GetInt("minute", 0).ToString("00") + ":"
                         + n.GetInt("second", 0).ToString("00");
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

            double x1 = from.X + NodeW, y1 = from.Y + NodeH / 2;
            double x2 = to.X, y2 = to.Y + NodeH / 2;

            var path = BuildBezier(x1, y1, x2, y2);
            path.Cursor = Cursors.Hand;
            path.ToolTip = "双击或右键删除这条连线";
            path.MouseLeftButtonDown += (_, ev) =>
            {
                if (ev.ClickCount == 2) { RemoveEdge(e); ev.Handled = true; }
            };
            // ★ 右键也能删：双击在细线上不好命中（用户反馈"已连接的线删不掉"）
            path.MouseRightButtonUp += (_, ev) => { RemoveEdge(e); ev.Handled = true; };

            Children.Add(path);

            // 箭头也绑同样的删除事件，整条线都能点
            var arrow = BuildArrow(x2, y2, x1, y1);
            arrow.IsHitTestVisible = true;
            arrow.Cursor = Cursors.Hand;
            arrow.ToolTip = "双击或右键删除这条连线";
            arrow.MouseLeftButtonDown += (_, ev) =>
            {
                if (ev.ClickCount == 2) { RemoveEdge(e); ev.Handled = true; }
            };
            arrow.MouseRightButtonUp += (_, ev) => { RemoveEdge(e); ev.Handled = true; };
            Children.Add(arrow);
        }

        private static Path BuildBezier(double x1, double y1, double x2, double y2)
        {
            double dx = Math.Max(40, Math.Abs(x2 - x1) * 0.45);
            var geo = new PathGeometry();
            var fig = new PathFigure { StartPoint = new Point(x1, y1) };
            fig.Segments.Add(new BezierSegment(
                new Point(x1 + dx, y1), new Point(x2 - dx, y2), new Point(x2, y2), true));
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
        private double _dragOriginX, _dragOriginY;
        private bool _dragMoved;

        private void BeginDrag(FlowNode n, Border view, MouseButtonEventArgs e)
        {
            Select(n);
            if (e.ClickCount == 2) { e.Handled = true; return; }

            // 记录起点，后续移动统一走画布级处理器
            _dragNode = n;
            _dragStart = e.GetPosition(this);
            _dragOriginX = n.X;
            _dragOriginY = n.Y;
            _dragMoved = false;

            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            // 拖动节点
            if (_dragNode != null)
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
            if (_dragLine != null && _linkFrom != null)
            {
                var p = e.GetPosition(this);
                _dragLine.X2 = p.X;
                _dragLine.Y2 = p.Y;

                // ★ 实时高亮：鼠标附近有可连接的模块就描边发亮，
                //   让用户知道"松手就连上了"（吸附半径 46px 内都算）。
                var hover = FindNodeAt(p, _linkFrom);
                HighlightTarget(hover);
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);

            // 结束拖动
            if (_dragNode != null)
            {
                var node = _dragNode;
                bool moved = _dragMoved;
                _dragNode = null;
                ReleaseMouseCapture();
                if (moved) Changed?.Invoke();
                e.Handled = true;
                return;
            }

            // 结束连线
            if (_dragLine != null && _linkFrom != null)
            {
                var from = _linkFrom;
                _linkFrom = null;
                Children.Remove(_dragLine);
                _dragLine = null;
                ReleaseMouseCapture();

                var target = FindNodeAt(e.GetPosition(this), from);
                if (target != null) AddEdge(from, target);
                HighlightTarget(null);   // 清除高亮
                e.Handled = true;
            }
        }

        private void BeginLink(FlowNode from, MouseButtonEventArgs e)
        {
            _linkFrom = from;
            var start = new Point(from.X + NodeW, from.Y + NodeH / 2);

            _dragLine = new Line
            {
                X1 = start.X, Y1 = start.Y, X2 = start.X, Y2 = start.Y,
                Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0x4F, 0xE3, 0xFF)),
                StrokeThickness = 2.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                IsHitTestVisible = false
            };
            Children.Add(_dragLine);

            CaptureMouse();
            e.Handled = true;
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
                // 不能连到触发器（触发器没有输入）
                if (n.Kind == FlowNodeKind.Trigger) continue;

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
        private void AddEdge(FlowNode from, FlowNode to)
        {
            if (_flow == null) return;

            // 不能连到触发器
            if (to.Kind == FlowNodeKind.Trigger) return;
            // 不能连自己
            if (from.Id == to.Id) return;
            // 不能重复
            foreach (var e in _flow.Edges)
                if (e.From == from.Id && e.To == to.Id) return;

            // 防环：如果 to 已经能到达 from，就连线会形成环
            if (CanReach(to.Id, from.Id)) return;

            _flow.Edges.Add(new FlowEdge { From = from.Id, To = to.Id });
            Redraw();
            Changed?.Invoke();
        }

        /// <summary>判断从 start 出发能否走到 target（用于防环）。</summary>
        private bool CanReach(string start, string target)
        {
            if (_flow == null) return false;
            var seen = new HashSet<string>();
            var stack = new Stack<string>();
            stack.Push(start);

            while (stack.Count > 0)
            {
                string cur = stack.Pop();
                if (cur == target) return true;
                if (!seen.Add(cur)) continue;
                foreach (var e in _flow.Edges)
                    if (e.From == cur) stack.Push(e.To);
            }
            return false;
        }

        /// <summary>高亮"将要连上"的目标节点（null = 清除高亮）。</summary>
        private void HighlightTarget(FlowNode? target)
        {
            foreach (var kv in _nodeViews)
            {
                bool isTarget = target != null && kv.Key == target.Id;
                var node = _flow?.FindNode(kv.Key);
                if (node == null) continue;

                Color accent = AccentOf(node.Kind);
                if (_flow != null && node.Kind == FlowNodeKind.Condition && _flow.IsLatched(node.Id))
                    accent = Color.FromRgb(0x41, 0xF0, 0xC0);

                kv.Value.BorderBrush = new SolidColorBrush(isTarget
                    ? Color.FromRgb(0x4F, 0xE3, 0xFF)
                    : Color.FromArgb(0xAA, accent.R, accent.G, accent.B));
                kv.Value.BorderThickness = new Thickness(isTarget ? 2.5 : 1.5);
            }
        }

        private void ApplySelectionVisual()
        {
            foreach (var kv in _nodeViews)
            {
                bool sel = _selected != null && kv.Key == _selected.Id;
                kv.Value.BorderThickness = new Thickness(sel ? 2.5 : 1.5);
                kv.Value.Opacity = sel ? 1.0 : 0.94;
            }
        }
    }
}
