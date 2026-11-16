using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

        public FlowEditorWindow(AutomationFlow flow, Account account, AppConfig cfg,
                                bool isNew = false)
        {
            InitializeComponent();
            _isNew = isNew;
            _flow = flow;
            _account = account;
            _cfg = cfg;

            HeaderText.Text = account.Caption + " 的自动化";
            SubHeader.Text = "流程名可自定义；每个账号的流程互相独立，不会互相影响";
            NameBox.Text = flow.Name;
            EnabledCheck.IsChecked = flow.Enabled;
            PersistCheck.IsChecked = flow.PersistLatched;

            BuildLibrary();
            BuildHelp();

            Canvas.NodeSelected += OnNodeSelected;
            Canvas.Changed += () =>
            {
                _dirty = true;
                RefreshCondModeRow();
                RefreshValidateHint();
            };
            Canvas.NodeDeleteRequested += OnNodeDelete;

            RefreshCondModeRow();

            _ready = true;
            Canvas.Load(flow);
            UpdateEmptyHint();

        }

        /// <summary>条件聚合方式那一行：只有在有 ≥2 个条件时才显示。</summary>
        private void RefreshCondModeRow()
        {
            try
            {
                int condCount = 0;
                foreach (var n in _flow.Nodes)
                    if (n.Kind == FlowNodeKind.Condition) condCount++;

                CondModeRow.Visibility = condCount >= 2 ? Visibility.Visible : Visibility.Collapsed;

                foreach (ComboBoxItem it in CondModeCombo.Items)
                {
                    bool match = (it.Tag as string) == _flow.ConditionMode.ToString();
                    if (match && it.IsSelected != true) it.IsSelected = true;
                }
            }
            catch { }
        }

        private void UpdateEmptyHint()
        {
            EmptyHint.Visibility = _flow.Nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshValidateHint();
        }

        /// <summary>
        /// 实时显示流程是否可运行。
        /// ★ 用户要求：保存时提醒，所以编辑过程中也要让用户看到当前状态。
        /// </summary>
        private void RefreshValidateHint()
        {
            try
            {
                if (ValidateHint == null) return;

                var rep = FlowValidator.Validate(_flow, _cfg);
                if (_flow.Nodes.Count == 0)
                {
                    ValidateHint.Visibility = Visibility.Collapsed;
                    return;
                }

                ValidateHint.Visibility = Visibility.Visible;

                if (rep.HasError)
                {
                    int errs = 0;
                    string first = "";
                    foreach (var i in rep.Issues)
                    {
                        if (!i.IsError) continue;
                        errs++;
                        if (first.Length == 0) first = i.Message;
                    }

                    // ★ 直接把**第一条问题**显示出来，不让用户去悬停猜
                    ValidateHint.Text = "✕ " + first + (errs > 1 ? "（还有 " + (errs - 1) + " 个）" : "");
                    ValidateHint.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9B, 0x9B));
                    ValidateHint.ToolTip = rep.ToText();
                    ValidateHint.MaxWidth = 520;
                }
                else if (rep.Issues.Count > 0)
                {
                    ValidateHint.Text = "⚠ 可以运行（" + rep.Issues.Count + " 条提醒）";
                    ValidateHint.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD9, 0x8A));
                    ValidateHint.ToolTip = rep.ToText();
                }
                else
                {
                    ValidateHint.Text = "✔ 结构完整，可以运行";
                    ValidateHint.Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0xF0, 0xC8));
                    ValidateHint.ToolTip = null;
                }
            }
            catch { }
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

            foreach (var kind in new[]
            {
                FlowNodeKind.Trigger,
                FlowNodeKind.Condition,
                FlowNodeKind.Action,
                FlowNodeKind.Terminate
            })
            {
                var defs = FlowNodeCatalog.ByKind(kind);
                if (defs.Count == 0) continue;
                LibPanel.Children.Add(BuildKindCard(kind, defs.Count));
            }
        }

        private static string KindName(FlowNodeKind kind) => kind switch
        {
            FlowNodeKind.Trigger => "触发器",
            FlowNodeKind.Condition => "条件",
            FlowNodeKind.Terminate => "结束",
            _ => "执行器"
        };

        private static string KindDesc(FlowNodeKind kind) => kind switch
        {
            FlowNodeKind.Trigger => "只能有一个，决定流程什么时候开始",
            FlowNodeKind.Condition => "满足才继续往下执行，可以多个（AND/OR）",
            FlowNodeKind.Terminate => "立刻结束本次流程，条件同时清零",
            _ => "真正干活的部分，执行完可以再接条件"
        };

        private static Color AccentOf(FlowNodeKind kind) => kind switch
        {
            FlowNodeKind.Trigger => Color.FromRgb(0xA9, 0x7B, 0xFF),
            FlowNodeKind.Condition => Color.FromRgb(0x4F, 0xE3, 0xFF),
            FlowNodeKind.Terminate => Color.FromRgb(0xFF, 0xB1, 0x4D),
            _ => Color.FromRgb(0x41, 0xF0, 0xC0)
        };

        /// <summary>一个类别卡片：标题 + 数量 + 说明，点一下弹出该类节点清单。</summary>
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
        {
            KindMenu.Children.Clear();
            Color accent = AccentOf(kind);

            foreach (var d in FlowNodeCatalog.ByKind(kind))
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

            Add("添加节点", "点左栏「节点库」里的任一类型，它会出现在画布左上角，然后拖到合适位置。");
            Add("移动节点", "按住节点拖动。只有正在拖的那个会动。");
            Add("建立连线", "从节点右侧的圆点按住，拖到目标节点附近放开即可（不用精确对准，46px 内自动吸附）。");
            Add("删除连线", "双击连线，或在连线上点右键，都可以删除。");
            Add("删除节点", "右键节点，或在左栏属性里点「删除这个节点」。");
            Add("编辑参数", "点节点，左栏下方会显示它的所有参数。");
            Add("条件状态", "条件满足后会变绿并显示「✔ 已满足」。只有流程结束时才清零。");
            Add("保存校验", "链路不完整无法保存。底部会实时显示是否能运行。");
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
                // 触发器只能有一个
                if (def.Kind == FlowNodeKind.Trigger)
                {
                    var exist = _flow.Trigger;
                    if (exist != null)
                    {
                        ThemedMessage.Info(this, "提示", "一个流程只能有一个触发器。\n如需更换，请先右键删除原来的触发器。");
                        return;
                    }
                }

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

                // ★ 用到 BGI 的节点：如果本账号没配 BGI 路径，
                //   自动把目标用户设成第一个有路径的账号，
                //   避免一新建就显示"没有配置 BGI 路径"（用户实测）。
                if (UsesBgi(def.Type))
                {
                    var map = _cfg.Settings.BgiPaths;
                    bool meHas = map != null && map.TryGetValue(_account.UserName, out var mePath)
                                 && !string.IsNullOrEmpty(mePath) && File.Exists(mePath);
                    if (!meHas && map != null)
                    {
                        foreach (var kv in map)
                        {
                            if (!string.IsNullOrWhiteSpace(kv.Value) && File.Exists(kv.Value))
                            {
                                node.Set("user", kv.Key);
                                break;
                            }
                        }
                    }
                }

                // 位置：拖动落点优先；没有落点（单击）时错开摆放
                if (at.HasValue)
                {
                    node.X = Math.Max(0, at.Value.X);
                    node.Y = Math.Max(0, at.Value.Y);
                }
                else
                {
                    int sameKind = 0;
                    foreach (var n in _flow.Nodes) if (n.Kind == def.Kind) sameKind++;
                    node.X = 40 + Math.Max(0, sameKind % 3) * 210;
                    node.Y = 40 + Math.Max(0, sameKind / 3) * 100;
                }

                _flow.Nodes.Add(node);
                _dirty = true;
                Canvas.Load(_flow);
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

            // 只列出**同类**节点（触发器只能换触发器，条件换条件……）
            foreach (var cand in FlowNodeCatalog.ByKind(node.Kind))
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
                    foreach (var a in _cfg.Accounts)
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
                            MarkDirty();
                            Canvas.Redraw();
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
                    foreach (var n in ListBgiConfigs("OneDragon"))
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
                    foreach (var n in ListBgiConfigs("ScriptGroup"))
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

            string path = "";
            var paths = _cfg.Settings.BgiPaths;
            if (paths != null)
            {
                paths.TryGetValue(user, out path);
                if (string.IsNullOrEmpty(path)) paths.TryGetValue(_account.UserName, out path);
            }
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

                var map = _cfg.Settings.BgiPaths
                          ?? (_cfg.Settings.BgiPaths = new Dictionary<string, string>());
                map[user] = dlg.FileName;

                box.Text = dlg.FileName;
                box.Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xE8, 0xC8));
                MarkDirty();

                // 路径变了，一条龙/配置组列表要重新读
                Dispatcher.InvokeAsync(() => ShowPropPanel(node));
            };
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

        private void MarkDirty() => _dirty = true;

        /// <summary>
        /// 列出本账号 BGI 目录下指定子目录里的配置名。
        /// 目录从账号保存的 BGI 路径推出：&lt;exe目录&gt;\User\&lt;sub&gt;。
        /// </summary>
        private List<string> ListBgiConfigs(string sub)
        {
            var list = new List<string>();
            try
            {
                string exe = "";
                var paths = _cfg.Settings.BgiPaths;
                if (paths != null) paths.TryGetValue(_account.UserName, out exe);
                if (string.IsNullOrEmpty(exe))
                {
                    // 退回：看该账号有没有在跑 BGI
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName("BetterGI"))
                    {
                        try
                        {
                            string? f = p.MainModule?.FileName;
                            if (string.IsNullOrEmpty(f)) continue;
                            exe = f;
                            break;
                        }
                        catch { }
                    }
                }
                if (string.IsNullOrEmpty(exe)) return list;

                string? dir = Path.GetDirectoryName(exe);
                if (string.IsNullOrEmpty(dir)) return list;

                string target = Path.Combine(dir, "User", sub);
                if (!Directory.Exists(target)) return list;

                foreach (var f in Directory.GetFiles(target, "*.json"))
                {
                    string n = Path.GetFileNameWithoutExtension(f);
                    if (!string.IsNullOrWhiteSpace(n)) list.Add(n);
                }
                list.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return list;
        }

        // ══════════ 事件 ══════════

        private void TitleRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (e.OriginalSource is DependencyObject d && FindAncestor<TextBox>(d) != null) return;
                DragMove();
            }
            catch { }
        }

        private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready) return;
            _flow.Name = NameBox.Text ?? "";
            _dirty = true;
        }

        private void EnabledCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            _flow.Enabled = EnabledCheck.IsChecked == true;
            _dirty = true;
        }

        /// <summary>「记住条件」开关：决定条件锁存是否写进配置文件。</summary>
        private void PersistCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            _flow.PersistLatched = PersistCheck.IsChecked == true;
            _dirty = true;
        }

        private void CondMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            if (CondModeCombo.SelectedItem is ComboBoxItem it && (it.Tag as string) == "Any")
                _flow.ConditionMode = FlowConditionMode.Any;
            else
                _flow.ConditionMode = FlowConditionMode.All;
            _dirty = true;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (_dirty)
            {
                var r = ThemedMessage.YesNoCancel(this, "保存", "有未保存的修改，要保存吗？");
                if (r == MessageBoxResult.Cancel) return;
                if (r == MessageBoxResult.Yes) Save_Click(null, null!);
            }
            Close();
        }

        private void Save_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                _flow.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "未命名流程" : NameBox.Text.Trim();
                _flow.Enabled = EnabledCheck.IsChecked == true;
                _flow.PersistLatched = PersistCheck.IsChecked == true;

                // ★ 保存前校验：链路不完整 / 结构非法 / 数据缺失 → 不让保存
                var report = FlowValidator.Validate(_flow, _cfg);
                if (report.HasError)
                {
                    var errs = new List<string>();
                    var warns = new List<string>();
                    foreach (var i in report.Issues)
                        (i.IsError ? errs : warns).Add((i.IsError ? "• " : "⚠ ") + i.Message);

                    string text = "这个流程还不能运行，请先修正下面的问题：\n\n"
                                + string.Join("\n", errs);
                    if (warns.Count > 0)
                        text += "\n\n另外：\n" + string.Join("\n", warns);

                    ThemedMessage.Info(this, "流程不完整", text);

                    // 选中第一个出问题的节点，方便直接定位
                    foreach (var i in report.Issues)
                        if (i.IsError && i.Node != null) { Canvas.Select(i.Node); break; }

                    return;
                }

                // ★ 新建的流程在**保存时**才真正加入配置，
                //   避免主页出现空白流程（用户要求）。
                if (_isNew && !_cfg.Automations.Exists(f => f.Id == _flow.Id))
                    _cfg.Automations.Add(_flow);

                ConfigStore.Save(_cfg);
                _dirty = false;
                LogBus.Ok("已保存自动化流程：" + _flow.DisplayName);

                string okText = "已保存。\n\n流程「" + _flow.DisplayName + "」会在满足触发器时自动执行。";
                if (report.Issues.Count > 0)
                {
                    var warns = new List<string>();
                    foreach (var i in report.Issues) warns.Add("⚠ " + i.Message);
                    okText += "\n\n提醒：\n" + string.Join("\n", warns);
                }
                ThemedMessage.Info(this, "提示", okText);
            }
            catch (Exception ex)
            {
                ThemedMessage.Info(this, "提示", "保存失败：" + ex.Message);
            }
        }

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
                // 保存当前编辑
                _flow.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "未命名流程" : NameBox.Text.Trim();

                // 运行前同样校验
                var vr = FlowValidator.Validate(_flow, _cfg);
                if (vr.HasError)
                {
                    var errs = new List<string>();
                    foreach (var i in vr.Issues) if (i.IsError) errs.Add("• " + i.Message);
                    ThemedMessage.Info(this, "流程不完整", "这个流程还不能运行，请先修正：\n\n" + string.Join("\n", errs));
                    return;
                }

                ConfigStore.Save(_cfg);
                _dirty = false;

                var sessions = await SessionService.QueryAsync();
                var s = SessionService.Find(sessions, _account.UserName);
                if (s == null)
                {
                    ThemedMessage.Info(this, "提示", "账号 " + _account.UserName + " 当前没有会话，无法执行。");
                    return;
                }

                LogBus.Info("手动运行流程：" + _flow.DisplayName);
                var result = await FlowRunner.RunAsync(_flow, s.Id, _cfg, manual: true);

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
