using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SoulLink.Controls
{
    /// <summary>
    /// 自动化编辑器的嵌入式引导。所有图形都是演示，不读写流程、不调用动作。
    /// 同一个渲染器用于主程序、独立预览和逐帧导出，避免三套效果不一致。
    /// </summary>
    public sealed class FlowGuideOverlay : Canvas
    {
        public sealed class Step
        {
            public string Title = "";
            public string[] Lines = Array.Empty<string>();
            public string Tip = "";
            public Func<FrameworkElement?> Target = () => null;
        }

        private sealed class DemoNode
        {
            public Rect Bounds;
            public Border Card = null!;
            public TextBlock State = null!;
            public Ellipse Port = null!;
            public TextBlock Summary = null!;
        }

        private readonly IReadOnlyList<Step> _steps;
        private readonly Action _close;
        private readonly Func<bool> _noMore;
        private readonly Action<bool> _toggleNoMore;
        private readonly DispatcherTimer _timer;
        private readonly Stopwatch _clock = new();
        private readonly List<Action<double>> _animations = new();
        private readonly List<Rect> _demoBounds = new();
        private int _index;
        private bool _active;
        private Action<double>? _frame;

        /// <summary>Canvas 不会给子元素分配尺寸；引导必须显式跟随宿主。</summary>
        public void SyncHostSize()
        {
            if (Parent is not FrameworkElement host) return;
            if (host.ActualWidth > 1) Width = host.ActualWidth;
            if (host.ActualHeight > 1) Height = host.ActualHeight;
        }

        public int CurrentIndex => _index;
        public int StepCount => _steps.Count;
        public string DescribeTargets() => string.Join(" | ", _steps.Select(s =>
        {
            var e = s.Target();
            return (e == null ? "null" : e.GetType().Name + " " + e.IsVisible + " " + e.ActualWidth.ToString("0") + "x" + e.ActualHeight.ToString("0") + " " + TargetRect(e));
        }));

        public FlowGuideOverlay(IReadOnlyList<Step> steps, Action close,
            Func<bool> noMore, Action<bool> toggleNoMore)
        {
            _steps = steps; _close = close; _noMore = noMore; _toggleNoMore = toggleNoMore;
            Visibility = Visibility.Collapsed;
            Background = Brushes.Transparent;
            ClipToBounds = true;
            IsHitTestVisible = true;
            Loaded += (_, _) => SyncHostSize();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (_, _) => _frame?.Invoke(_clock.Elapsed.TotalSeconds);
            SizeChanged += (_, _) =>
            {
                if (!_active || ActualWidth < 2 || ActualHeight < 2) return;
                double time = _clock.IsRunning ? _clock.Elapsed.TotalSeconds : 0;
                Render(false, time);
            };
        }

        public static List<Step> BuildSteps(FrameworkElement library, FrameworkElement canvas,
            FrameworkElement properties, FrameworkElement validation) => new()
        {
            new Step
            {
                Title = "从条件开始",
                Lines = new[]
                {
                    "从左侧「条件」添加「会话在线」。",
                    "拖到画布；单击具体条目也可添加。",
                    "选中节点，在右侧确认目标账号与状态。"
                },
                Tip = "条件只判断当前状态，不会自己执行动作。",
                Target = () => library
            },
            new Step
            {
                Title = "把模块连成流程",
                Lines = new[]
                {
                    "从模块下方圆点拖到下一模块。",
                    "点击连线前，先开启左上「画笔模式」。",
                    "普通多出口选择全部支路，动作依次执行。"
                },
                Tip = "绿色连线只表示当前导通，不表示动作已完成。",
                Target = () => canvas
            },
            new Step
            {
                Title = "看懂实时状态灯",
                Lines = new[]
                {
                    "绿色＝此刻满足；灰色＝此刻不满足。",
                    "琥珀＝数据不可用，不会当作条件满足。",
                    "状态灯约每秒刷新，但不代表执行结果。"
                },
                Tip = "预览只读取状态；未保存的修改不会因此执行动作。",
                Target = () => canvas
            },
            new Step
            {
                Title = "等待真正的任务结果",
                Lines = new[]
                {
                    "「启动一条龙」成功，仅表示请求已接受。",
                    "接「配置组状态」或「日志包含」确认完成。",
                    "等待的输入断开，会重新计时。"
                },
                Tip = "动作失败先排查；输入断开后再次成立才允许重试。",
                Target = () => canvas
            },
            new Step
            {
                Title = "设置「每天完成」",
                Lines = new[]
                {
                    "所选动作全部成功后，写入当天完成标记。",
                    "日界设 00:00 或 04:00，到点开始新一轮。",
                    "没有「每天完成」，一轮结束后可再次执行。"
                },
                Tip = "右侧「今天运行这个流程」控制本业务日；修改后请保存。",
                Target = () => properties
            },
            new Step
            {
                Title = "自检、保存，再运行",
                Lines = new[]
                {
                    "底部提示结构问题，点击可定位节点。",
                    "「可以运行」只表示结构通过，不是任务成功。",
                    "保存并启用；「立即运行」仍按条件执行。"
                },
                Tip = "立即运行会执行真实动作，也受暂停和今日状态限制。",
                Target = () => validation
            }
        };

        public void Start()
        {
            _index = 0;
            ShowStep(true);
        }

        public void Next()
        {
            if (_index >= _steps.Count - 1) { Close(); _close(); return; }
            _index++;
            ShowStep(true);
        }

        public void Previous()
        {
            _index = Math.Max(0, _index - 1);
            ShowStep(true);
        }

        /// <summary>供独立实验台生成确定性验收截图；不启动实时计时器。</summary>
        public void ShowFrame(int index, double seconds)
        {
            _timer.Stop(); _clock.Reset();
            _index = Math.Clamp(index, 0, _steps.Count - 1);
            Render(false, Math.Max(0, seconds));
        }

        private void ShowStep(bool animate)
        {
            _timer.Stop(); _clock.Reset();
            Render(animate, 0);
            if (animate) { _clock.Start(); _timer.Start(); }
        }

        private void Render(bool animate, double time)
        {
            _timer.Stop();
            Children.Clear();
            _animations.Clear(); _demoBounds.Clear(); _frame = null;
            Visibility = Visibility.Visible; _active = true;
            SyncHostSize();
            _canvasReference?.UpdateLayout();
            _propertiesReference?.UpdateLayout();
            double w = ActualWidth > 1 ? ActualWidth : 1180;
            double h = ActualHeight > 1 ? ActualHeight : 820;
            var step = _steps[_index];
            var hole = TargetRect(step.Target());
            var canvasRect = TargetRect(FindCanvasTarget(step));
            if (canvasRect.IsEmpty) canvasRect = new Rect(w * .23, 94, w * .49, h - 218);

            Add(new Rectangle
            {
                Width = w, Height = h, Fill = Brushes.Transparent,
                IsHitTestVisible = true
            }, 0, 0);

            // 真正的镂空遮罩：即使与主题窗口背景叠加，也只保留目标原亮度。
            var maskGeometry = new GeometryGroup { FillRule = FillRule.Nonzero };
            maskGeometry.Children.Add(new RectangleGeometry(new Rect(0, 0, w, h)));
            if (!hole.IsEmpty)
            {
                hole = ClipRect(hole, w, h, 6);
                if (!hole.IsEmpty) maskGeometry.Children.Add(new RectangleGeometry(hole));
            }
            var mask = new Path
            {
                Data = maskGeometry,
                Fill = new SolidColorBrush(Color.FromArgb(194, 4, 10, 22)),
                IsHitTestVisible = true
            };
            Children.Add(mask);

            if (!hole.IsEmpty)
            {
                var ring = new Rectangle
                {
                    Width = hole.Width, Height = hole.Height,
                    RadiusX = 12, RadiusY = 12,
                    Stroke = new SolidColorBrush(Color.FromArgb(220, 79, 227, 255)),
                    StrokeThickness = 1.6, IsHitTestVisible = false
                };
                Add(ring, hole.X, hole.Y);
                _animations.Add(t => ring.Opacity = .82 + .12 * Math.Sin(t * 2.2));
            }

            var demo = new Canvas { Width = w, Height = h, IsHitTestVisible = false };
            Children.Add(demo);
            Rect focus = DrawDemo(_index, demo, canvasRect, TargetRect(FindPropertiesTarget(step)), w, h);
            if (_index == 4 && !hole.IsEmpty)
            {
                // 第五步明确露出右侧属性栏，不受示例卡片指向逻辑影响。
                hole = ClipRect(new Rect(hole.X - 6, hole.Y + 24, hole.Width + 12, 286), w, h, 0);
            }
            if (focus.IsEmpty) focus = hole;

            var panel = BuildPanel(step, _index);
            Size panelSize = new Size(340, 318);
            panel.Measure(panelSize);
            panel.Arrange(new Rect(new Point(0, 0), panelSize));
            panel.UpdateLayout();
            Point pos = PickPanel(panelSize, w, h, hole);
            AddConnector(pos, panelSize, focus, hole);
            Add(panel, pos.X, pos.Y);
            Panel.SetZIndex(panel, 20);
            AnimateIn(panel, 0, 10);

            BuildProgress(w);
            BuildNavigation(w, h);
            AddStepLabel(w, h);

            _frame = t =>
            {
                foreach (var animation in _animations) animation(t);
            };
            _frame(time);
        }

        private FrameworkElement? FindCanvasTarget(Step step)
        {
            // 取 BuildSteps 中画布目标的引用；其它步骤也统一在真实画布中演示。
            return _canvasReference;
        }

        private FrameworkElement? FindPropertiesTarget(Step step) => _propertiesReference;
        private FrameworkElement? _canvasReference;
        private FrameworkElement? _propertiesReference;

        public void SetSceneReferences(FrameworkElement canvas, FrameworkElement properties)
        {
            _canvasReference = canvas; _propertiesReference = properties;
        }

        private Rect TargetRect(FrameworkElement? element)
        {
            if (element == null || !element.IsVisible || element.ActualWidth < 1 || element.ActualHeight < 1)
                return Rect.Empty;
            try
            {
                // 目标与引导层通常是兄弟节点，TransformToAncestor(this) 必然失败。
                // 转换到公共视觉树中的引导层坐标，才能正确得到高亮区域。
                return element.TransformToVisual(this).TransformBounds(
                    new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            }
            catch { return Rect.Empty; }
        }

        private static Rect ClipRect(Rect rect, double w, double h, double inflate)
        {
            rect.Inflate(inflate, inflate);
            rect.Intersect(new Rect(0, 0, w, h));
            return rect.IsEmpty || rect.Width < 2 || rect.Height < 2 ? Rect.Empty : rect;
        }

        private Border BuildPanel(Step step, int index)
        {
            var panel = new Border
            {
                Width = 340, Height = 318, CornerRadius = new CornerRadius(15),
                Padding = new Thickness(21, 17, 21, 16),
                Background = new SolidColorBrush(Color.FromRgb(15, 26, 45)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(175, 79, 227, 255)),
                BorderThickness = new Thickness(1), Tag = "GuidePanel",
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, BlurRadius = 25, Opacity = .5, ShadowDepth = 0 }
            };
            var stack = new StackPanel();
            stack.Children.Add(Text("STEP " + (index + 1).ToString("00") + "  /  06",
                10.5, CyanBrush, FontWeights.SemiBold));
            stack.Children.Add(new TextBlock
            {
                Text = step.Title, FontSize = 20, FontWeight = FontWeights.SemiBold,
                Foreground = BrushOf(White), Margin = new Thickness(0, 8, 0, 10),
                TextWrapping = TextWrapping.Wrap
            });
            foreach (string line in step.Lines)
            {
                var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(17) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new Ellipse
                {
                    Width = 5, Height = 5, Fill = BrushOf(CyanBrush),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 8, 0, 0)
                });
                var text = Text(line, 13, Color.FromRgb(218, 231, 247));
                text.LineHeight = 22; Grid.SetColumn(text, 1); row.Children.Add(text);
                stack.Children.Add(row);
            }
            var tip = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8),
                Background = new SolidColorBrush(Color.FromArgb(24, 79, 227, 255)),
                Margin = new Thickness(0, 12, 0, 10)
            };
            var tipText = Text("记住 · " + step.Tip, 11.2, Color.FromRgb(159, 190, 219));
            tipText.LineHeight = 18; tip.Child = tipText; stack.Children.Add(tip);
            var noMore = new CheckBox
            {
                IsChecked = _noMore(), FontSize = 10.5,
                Foreground = ColorBrush(135, 160, 190),
                Content = new TextBlock
                {
                    Text = "以后不再自动显示（可在左侧「使用说明」重播）",
                    TextWrapping = TextWrapping.Wrap, Width = 276
                }
            };
            if (TryFindResource("GlassCheckBox") is Style style) noMore.Style = style;
            noMore.Checked += (_, _) => _toggleNoMore(true);
            noMore.Unchecked += (_, _) => _toggleNoMore(false);
            stack.Children.Add(noMore);
            panel.Child = stack;
            return panel;
        }

        private Point PickPanel(Size size, double w, double h, Rect hole)
        {
            double pw = size.Width, ph = size.Height;
            var preferred = _index == 4
                ? new Point(24, 96)
                : _index == 5
                    ? new Point(w - pw - 26, h - ph - 126)
                    : new Point(w - pw - 26, 108);
            var candidates = new List<Point>
            {
                preferred,
                new Point(w - pw - 24, 96),
                new Point(24, 96),
                new Point((w - pw) / 2, 76),
                new Point(w - pw - 24, h - ph - 120),
                new Point(24, h - ph - 120)
            };
            Point best = preferred; double bestScore = double.MaxValue;
            foreach (var candidate in candidates)
            {
                var p = new Point(
                    Math.Clamp(candidate.X, 18, Math.Max(18, w - pw - 18)),
                    Math.Clamp(candidate.Y, 70, Math.Max(70, h - ph - 108)));
                var rect = new Rect(p, size);
                double score = Math.Abs(p.X - preferred.X) * .012 + Math.Abs(p.Y - preferred.Y) * .012;
                foreach (var demo in _demoBounds)
                    score += Overlap(rect, demo) * 80;
                // 整幅画布的高亮洞允许放说明卡，但须给示例留足空间。
                if (score < bestScore) { bestScore = score; best = p; }
            }
            return best;
        }

        private static double Overlap(Rect a, Rect b)
        {
            a.Intersect(b);
            return a.IsEmpty ? 0 : a.Width * a.Height;
        }

        private void AddConnector(Point pos, Size size, Rect focus, Rect hole)
        {
            var anchor = !focus.IsEmpty ? focus : hole;
            if (anchor.IsEmpty) return;
            var target = new Point(anchor.X + anchor.Width / 2, anchor.Y + anchor.Height / 2);
            if (_index == 4 && anchor.Width > 100)
                target = new Point(anchor.X + 22, Math.Min(anchor.Bottom - 30, anchor.Y + 150));
            if (_index == 5) target = new Point(anchor.X + Math.Min(anchor.Width / 2, 220), anchor.Y + anchor.Height / 2);
            bool panelLeft = pos.X + size.Width < target.X;
            bool panelRight = pos.X > target.X;
            var from = panelLeft ? new Point(pos.X + size.Width, pos.Y + 70)
                : panelRight ? new Point(pos.X, pos.Y + 70)
                : new Point(pos.X + size.Width / 2, target.Y > pos.Y ? pos.Y + size.Height : pos.Y);
            var c1 = new Point(from.X + (panelLeft ? 42 : panelRight ? -42 : 0), from.Y);
            var c2 = new Point(target.X + (panelLeft ? -45 : panelRight ? 45 : 0), target.Y);
            var path = new Path
            {
                Data = Curve(from, c1, c2, target),
                Stroke = new SolidColorBrush(Color.FromArgb(220, 79, 227, 255)),
                StrokeThickness = 1.8, StrokeDashArray = new DoubleCollection { 4, 4 },
                IsHitTestVisible = false
            };
            Children.Add(path);
            var dot = new Ellipse
            {
                Width = 11, Height = 11, Fill = BrushOf(CyanBrush),
                Stroke = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), StrokeThickness = 3,
                IsHitTestVisible = false
            };
            Add(dot, target.X - 5.5, target.Y - 5.5);
            _animations.Add(t =>
            {
                path.StrokeDashOffset = -t * 5;
                dot.Opacity = .78 + .22 * Math.Sin(t * 2.8);
            });
        }

        private void BuildProgress(double w)
        {
            var group = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
            group.Children.Add(Text((_index + 1).ToString("00") + " / 06", 11, White, FontWeights.SemiBold));
            for (int i = 0; i < _steps.Count; i++)
            {
                group.Children.Add(new Border
                {
                    Width = i == _index ? 30 : 18, Height = 3, CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(i == 0 ? 14 : 6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = i <= _index ? BrushOf(CyanBrush) : ColorBrush(49, 68, 92)
                });
            }
            group.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Add(group, 27, 25);
        }

        private void BuildNavigation(double w, double h)
        {
            var nav = new StackPanel { Orientation = Orientation.Horizontal, Tag = "GuideNavigation" };
            if (_index > 0) nav.Children.Add(Button("← 上一步", Previous, false));
            nav.Children.Add(Button("跳过引导", () => { Close(); _close(); }, false));
            var next = Button(_index == _steps.Count - 1 ? "开始使用 ✓" : "下一步 →", Next, true);
            next.Margin = new Thickness(10, 0, 0, 0); nav.Children.Add(next);
            nav.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Add(nav, Math.Max(20, w - nav.DesiredSize.Width - 26),
                Math.Max(20, h - nav.DesiredSize.Height - 24));
            Panel.SetZIndex(nav, 30);
        }

        private void AddStepLabel(double w, double h)
        {
            var b = new Border
            {
                Background = ColorBrush(13, 25, 43), CornerRadius = new CornerRadius(7),
                Padding = new Thickness(9, 5, 9, 5), IsHitTestVisible = false
            };
            b.Child = Text("交互演示 · 不执行真实动作", 10, Color.FromRgb(153, 182, 210));
            b.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Add(b, w - b.DesiredSize.Width - 26, 18);
        }

        private Button Button(string text, Action action, bool primary)
        {
            var b = new Button
            {
                Content = text, MinWidth = 98, MinHeight = 38,
                Padding = new Thickness(17, 8, 17, 8), FontSize = 12,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            string key = primary ? "PrimaryButton" : "GhostButton";
            if (TryFindResource(key) is Style style) b.Style = style;
            else { b.Background = primary ? BrushOf(CyanBrush) : ColorBrush(23, 37, 59); b.Foreground = primary ? ColorBrush(4, 20, 34) : BrushOf(White); }
            b.Click += (_, _) => action(); return b;
        }

        private Rect DrawDemo(int index, Canvas layer, Rect c, Rect p, double w, double h)
        {
            if (c.IsEmpty) c = new Rect(w * .22, 95, w * .49, h - 220);
            double x = c.X, right = c.Right, top = c.Y + 66;
            var nodes = new List<DemoNode>();
            DemoNode Node(double nx, double ny, string kind, string title, string summary,
                Color accent, string state = "")
            {
                var n = DrawNode(layer, nx, ny, kind, title, summary, accent, state);
                nodes.Add(n); _demoBounds.Add(n.Bounds); return n;
            }
            void Link(DemoNode a, DemoNode b, bool animated = false)
            {
                DrawLink(layer, a.Bounds, b.Bounds, animated, animated ? .75 : 0);
            }
            void Badge(string text, double bx, double by, Color color, double width = 250)
            {
                var border = new Border
                {
                    Width = width, MinHeight = 32, CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(Color.FromArgb(245, 16, 32, 51)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(100, color.R, color.G, color.B)),
                    BorderThickness = new Thickness(1), Padding = new Thickness(9, 6, 9, 6)
                };
                border.Child = Text(text, 11, color, FontWeights.SemiBold, HorizontalAlignment.Center);
                Add(border, bx, by, layer); _demoBounds.Add(new Rect(bx, by, width, 36));
            }

            if (index == 0)
            {
                double nx = c.X + c.Width / 2 - 125;
                var n = Node(nx, top + 122, "条件", "会话在线", "目标账号的 Windows 会话处于活动状态", CyanBrushColor, "● 满足");
                Badge("① 左侧选择「条件」   →   ② 添加「会话在线」", nx - 25, top + 24, CyanBrushColor, 300);
                Badge("③ 在右侧选择目标账号", nx - 10, top + 242, Color.FromRgb(165, 184, 213), 270);
                DrawPointer(layer, new Point(Math.Max(45, c.X - 85), top + 38),
                    new Point(nx + 112, top + 145), 0, 1.5);
                return TargetRect(_steps[0].Target());
            }
            if (index == 1)
            {
                var a = Node(x + 32, top + 66, "条件", "会话在线", "目标账号的会话当前在线", CyanBrushColor, "● 满足");
                var b = Node(right - 282, top + 280, "动作", "执行一条龙", "启动指定的一条龙配置", GreenBrush, "请求已接受");
                Link(a, b, true);
                Badge("下方出口  →  下一模块", c.X + c.Width / 2 - 118, top + 214, CyanBrushColor, 236);
                return b.Bounds;
            }
            if (index == 2)
            {
                bool wide = c.Width > 520;
                double ax = wide ? c.X + (c.Width - 524) / 2 : c.X + c.Width / 2 - 125;
                var a = Node(ax, top + 80, "条件", "会话在线", "读取到当前会话状态", CyanBrushColor, "● 满足");
                var b = Node(wide ? ax + 274 : ax, top + (wide ? 80 : 198), "条件", "BGI 是否运行", "目标账号的 BGI 进程未运行", CyanBrushColor, "○ 不满足");
                Badge("⚠ 数据不可用 ≠ 不满足；不会按满足处理",
                    c.X + c.Width / 2 - 185, top + (wide ? 236 : 326), AmberBrush, 370);
                Badge("绿色只表示此刻成立，不表示已经执行",
                    c.X + c.Width / 2 - 165, top + (wide ? 292 : 380), Color.FromRgb(161, 183, 211), 330);
                return Rect.Union(a.Bounds, b.Bounds);
            }
            if (index == 3)
            {
                var a = Node(x + 24, top + 44, "动作", "执行一条龙", "启动请求已接受", GreenBrush, "请求已接受");
                var wait = Node(Math.Min(right - 274, w - 640), top + 186, "动作", "等待 5 秒", "输入断开后会重新计时", GreenBrush, "等待中");
                var done = Node(x + 90, top + 330, "条件", "配置组状态", "选择要观察的配置组", CyanBrushColor, "执行中");
                Link(a, wait); Link(wait, done);
                Badge("启动成功 ≠ 任务完成；继续读取真实结果",
                    c.X + c.Width / 2 - 185, top + 438, GreenBrush, 370);
                _animations.Add(t =>
                {
                    bool finished = t >= 2.4;
                    SetNodeState(done, finished ? "● 已完成" : "执行中", finished);
                    done.Summary.Text = finished ? "所选配置组已完成" : "选择要观察的配置组";
                });
                return done.Bounds;
            }
            if (index == 4)
            {
                double ny = Math.Max(top + 260, c.Bottom - 204);
                double nx = c.X + (c.Width - 524) / 2;
                var a = Node(Math.Max(c.X + 8, nx), ny, "动作", "配置组状态确认", "等待实际任务完成", GreenBrush, "已完成");
                var b = Node(Math.Max(c.X + 8, nx) + 274, ny, "结束", "每天完成", "所选动作全部成功后锁存", AmberBrush, "今天已完成");
                Link(a, b);
                if (!p.IsEmpty)
                {
                    double px = p.X + 14, py = p.Y + 36, pw = Math.Max(180, p.Width - 28);
                    var panel = new Border
                    {
                        Width = pw, Height = 260, CornerRadius = new CornerRadius(11),
                        Background = ColorBrush(16, 30, 50),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 177, 77)),
                        BorderThickness = new Thickness(1), Padding = new Thickness(16)
                    };
                    var s = new StackPanel();
                    s.Children.Add(Text("每天完成 · 节点属性（示例）", 13, AmberBrush, FontWeights.SemiBold));
                    s.Children.Add(Text("日界", 11, Color.FromRgb(151, 175, 205), margin: new Thickness(0, 21, 0, 6)));
                    var day = new Border { Height = 38, CornerRadius = new CornerRadius(7), Background = ColorBrush(27, 43, 66), Padding = new Thickness(10, 7, 10, 7) };
                    day.Child = Text("04:00                                         ⌄", 12, White); s.Children.Add(day);
                    var check = new CheckBox
                    {
                        Content = "今天运行这个流程", IsChecked = false, FontSize = 12,
                        Foreground = BrushOf(White), Margin = new Thickness(0, 20, 0, 9)
                    };
                    if (TryFindResource("GlassCheckBox") is Style style) check.Style = style;
                    s.Children.Add(check);
                    s.Children.Add(Text("取消勾选：今天不再运行\n到日界后自动开始新一轮", 10.5,
                        Color.FromRgb(158, 181, 207), margin: new Thickness(0, 8, 0, 0)));
                    panel.Child = s; Add(panel, px, py, layer);
                    _demoBounds.Add(new Rect(px, py, pw, 260));
                    p = new Rect(px, py, pw, 260);
                }
                return p.IsEmpty ? Rect.Union(a.Bounds, b.Bounds) : p;
            }
            double fx = c.X + c.Width / 2 - 125;
            var n1 = Node(fx, top + 8, "条件", "会话在线", "此刻满足", CyanBrushColor, "● 满足");
            var n2 = Node(fx, top + 116, "动作", "执行一条龙", "启动请求已接受", GreenBrush, "已执行");
            var n3 = Node(fx, top + 224, "条件", "配置组状态", "确认任务实际完成", CyanBrushColor, "● 已完成");
            var n4 = Node(fx, top + 332, "结束", "每天完成", "到日界自动开始新一轮", AmberBrush, "✓ 可以运行");
            Link(n1, n2); Link(n2, n3); Link(n3, n4);
            return n4.Bounds;
        }

        private DemoNode DrawNode(Canvas layer, double x, double y, string kind,
            string title, string summary, Color accent, string state)
        {
            const double nw = 250, nh = 86;
            var card = new Border
            {
                Width = nw, Height = nh, CornerRadius = new CornerRadius(12),
                Background = ColorBrush(18, 33, 54),
                BorderBrush = new SolidColorBrush(Color.FromArgb(175, accent.R, accent.G, accent.B)),
                BorderThickness = new Thickness(1.2), Tag = "DemoNode"
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12, 0, 0, 12),
                Background = new SolidColorBrush(accent)
            });
            var stack = new StackPanel { Margin = new Thickness(14, 10, 10, 8) };
            stack.Children.Add(Text(kind + "  /  " + title, 14, White, FontWeights.SemiBold));
            var summaryText = Text(summary, 10.5, Color.FromRgb(148, 173, 203), margin: new Thickness(0, 9, 0, 0));
            summaryText.MaxWidth = 205; summaryText.TextWrapping = TextWrapping.Wrap;
            stack.Children.Add(summaryText);
            Grid.SetColumn(stack, 1); grid.Children.Add(stack); card.Child = grid;
            Add(card, x, y, layer);
            var status = Text(state, 9.5, accent, FontWeights.SemiBold);
            status.Width = 96; status.TextAlignment = TextAlignment.Right;
            if (state.Length > 0) Add(status, x + nw - 108, y + nh - 22, layer);
            var port = new Ellipse
            {
                Width = 13, Height = 13, Fill = new SolidColorBrush(accent),
                Stroke = ColorBrush(214, 243, 255), StrokeThickness = 1.5
            };
            Add(port, x + nw / 2 - 6.5, y + nh - 6.5, layer);
            var node = new DemoNode
            {
                Bounds = new Rect(x, y, nw, nh), Card = card,
                State = status, Port = port, Summary = summaryText
            };
            AnimateIn(card, 0, 8);
            return node;
        }

        private static void SetNodeState(DemoNode node, string state, bool success)
        {
            node.State.Text = state;
            var color = success ? GreenBrush : Color.FromRgb(148, 173, 203);
            node.State.Foreground = new SolidColorBrush(color);
            node.Port.Fill = new SolidColorBrush(color);
            node.Card.BorderBrush = new SolidColorBrush(success ? GreenBrush : Color.FromRgb(79, 227, 255));
        }

        private void DrawLink(Canvas layer, Rect a, Rect b, bool animated, double delay)
        {
            var from = new Point(a.X + a.Width / 2, a.Bottom);
            var to = new Point(b.X + b.Width / 2, b.Y + 3);
            double bend = Math.Max(35, Math.Abs(to.Y - from.Y) * .45);
            var c1 = new Point(from.X + (to.X - from.X) * .28, from.Y + bend);
            var c2 = new Point(to.X - (to.X - from.X) * .22, to.Y - bend);
            var geometry = Curve(from, c1, c2, to);
            double length = GeometryLength(geometry);
            var path = new Path
            {
                Data = geometry, Stroke = BrushOf(GreenBrush),
                StrokeThickness = 2.2, StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            if (animated) path.StrokeDashArray = new DoubleCollection { length, length };
            layer.Children.Insert(0, path);
            _animations.Add(t =>
            {
                double progress = animated ? Clamp((t - delay) / 1.25) : 1;
                path.StrokeDashOffset = length * (1 - progress);
                path.Opacity = .45 + .55 * progress;
            });
            if (animated) DrawPointer(layer, from, to, delay, 1.25, c1, c2);
        }

        private void DrawPointer(Canvas layer, Point from, Point to, double delay, double duration,
            Point? c1 = null, Point? c2 = null)
        {
            var pointer = new Canvas { Width = 30, Height = 35, IsHitTestVisible = false };
            pointer.Children.Add(new Path
            {
                Data = PathGeometry.Parse("M 2,2 L 2,25 L 8,19 L 14,31 L 19,28 L 13,17 L 24,16 Z"),
                Fill = BrushOf(White), Stroke = ColorBrush(17, 35, 55), StrokeThickness = 1.5
            });
            layer.Children.Add(pointer);
            _animations.Add(t =>
            {
                double u = Clamp((t - delay) / duration);
                u = u * u * (3 - 2 * u);
                Point p = c1.HasValue && c2.HasValue
                    ? Bezier(from, c1.Value, c2.Value, to, u)
                    : new Point(from.X + (to.X - from.X) * u, from.Y + (to.Y - from.Y) * u);
                SetLeft(pointer, p.X - 2); SetTop(pointer, p.Y - 2);
                pointer.Opacity = t < delay ? 0 : t > delay + duration + .65 ? 0 : 1;
            });
        }

        private static PathGeometry Curve(Point a, Point b, Point c, Point d)
        {
            var g = new PathGeometry();
            var f = new PathFigure { StartPoint = a, IsClosed = false };
            f.Segments.Add(new BezierSegment(b, c, d, true)); g.Figures.Add(f); return g;
        }

        private static double GeometryLength(PathGeometry geometry)
        {
            double total = 0;
            foreach (var f in geometry.GetFlattenedPathGeometry(.01, ToleranceType.Absolute).Figures)
            {
                var last = f.StartPoint;
                foreach (var segment in f.Segments.OfType<PolyLineSegment>())
                    foreach (var p in segment.Points) { total += (p - last).Length; last = p; }
            }
            return Math.Max(1, total);
        }

        private static Point Bezier(Point a, Point b, Point c, Point d, double t)
        {
            double u = 1 - t;
            return new Point(
                u * u * u * a.X + 3 * u * u * t * b.X + 3 * u * t * t * c.X + t * t * t * d.X,
                u * u * u * a.Y + 3 * u * u * t * b.Y + 3 * u * t * t * c.Y + t * t * t * d.Y);
        }

        private static double Clamp(double value) => Math.Max(0, Math.Min(1, value));

        private void AnimateIn(FrameworkElement element, double delay, double offset)
        {
            var transform = new TranslateTransform(0, offset);
            element.RenderTransform = transform;
            element.Opacity = 0;
            _animations.Add(t =>
            {
                double u = Clamp((t - delay) / .36);
                u = 1 - Math.Pow(1 - u, 3);
                element.Opacity = u;
                transform.Y = offset * (1 - u);
            });
        }

        public void Close()
        {
            _active = false; _timer.Stop(); _clock.Reset();
            _frame = null; _animations.Clear(); Children.Clear(); Visibility = Visibility.Collapsed;
        }

        private void Add(UIElement element, double x, double y, Canvas? parent = null)
        {
            SetLeft(element, x); SetTop(element, y); (parent ?? this).Children.Add(element);
        }

        private static TextBlock Text(string text, double size, Color color,
            FontWeight? weight = null, HorizontalAlignment alignment = HorizontalAlignment.Left,
            Thickness? margin = null) => new()
        {
            Text = text, FontSize = size, Foreground = new SolidColorBrush(color),
            FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = alignment, Margin = margin ?? new Thickness(0)
        };

        private static readonly Color CyanBrush = Color.FromRgb(79, 227, 255);
        private static readonly Color White = Color.FromRgb(239, 246, 255);
        private static readonly Color GreenBrush = Color.FromRgb(65, 240, 192);
        private static readonly Color AmberBrush = Color.FromRgb(255, 177, 77);
        private static readonly Color CyanBrushColor = CyanBrush;
        private static readonly Color GreenColor = GreenBrush;
        private static readonly Color AmberColor = AmberBrush;
        private static Brush BrushOf(Color color) => new SolidColorBrush(color);
        private static SolidColorBrush ColorBrush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
    }
}