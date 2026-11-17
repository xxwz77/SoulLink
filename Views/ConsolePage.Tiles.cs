using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SoulLink.Controls;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Views
{
    public partial class ConsolePage
    {
        // ═══════════════════════════════════════════════════════════════════
        //  多画面（Tile）容器
        //
        //  ★ 核心约束（0x119 事故结论）：
        //    同一进程里的 ActiveX 绝不能在容器之间反复重挂。
        //    所以这里**只创建一次**控件并放进固定的 Tile，
        //    之后切标签 / 切布局 / 滚动，全部只改 Tile 的尺寸与可见性，
        //    永远不 Children.Clear() + Children.Add()。
        //
        //  ★ 缩放模式在连接时就已确定（SmartSizing / DesktopWidth），
        //    切换布局**不会**改变远端分辨率，也不会重新协商。
        //    只有「跟随窗口」模式在单画面下才按视口改远端分辨率，且已做防抖+串行。
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>一个画面格子：宿主 Border + 里面的 RdpConsole。</summary>
        private sealed class Tile
        {
            public UserRow Row = null!;
            public Border Shell = null!;
            public Border Chrome = null!;
            public Border Header = null!;
            public TextBlock Title = null!;
            public RdpConsole Console = null!;
        }

        /// <summary>
        /// 画面区的尺寸基准（= 可滚动视口本身，不是 Canvas）。
        /// 保留这个属性名是为了让旧代码里的 "挂载/测量/锚定" 判断继续成立：
        /// 语义从「把控件放进这个容器」变成「测量/锚定到该视口」。
        /// </summary>
        private ScrollViewer ViewportHost => ViewportScroll;

        private readonly List<Tile> _tiles = new List<Tile>();
        private Tile? _focusTile;
        private bool _layoutDirty = true;

        private ConsoleLayoutMode CurrentLayoutMode
        {
            get
            {
                int m = App.Config.Settings.RdpLayoutMode;
                return m switch
                {
                    1 => ConsoleLayoutMode.TwoColumn,
                    2 => ConsoleLayoutMode.Grid,
                    _ => ConsoleLayoutMode.Single
                };
            }
        }

        // ── 构建 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 查找或创建该用户的 Tile。已存在就直接返回，**不会重建控件**。
        /// </summary>
        private Tile EnsureTile(UserRow row)
        {
            var existing = _tiles.FirstOrDefault(t => ReferenceEquals(t.Row, row));
            if (existing != null) return existing;

            var console = row.Console ?? throw new InvalidOperationException("Console 未初始化");

            // 外框：负责间距、圆角、焦点高亮
            var chrome = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x04, 0x06, 0x0D)),
                ClipToBounds = true
            };

            // ★ 标题必须是「画面之外」的一行：
            //   RDP 控件是 WindowsFormsHost（独立 HWND），WPF 画不出它上面 ——
            //   任何叠在画面上的 WPF 文字都会被 RDP 的原生窗口盖住（airspace）。
            //   所以标题做成上方独立的 Auto 行，单画面时高度归零。
            var title = new TextBlock
            {
                Text = row.Account.Caption,
                FontSize = 10.5,
                Margin = new Thickness(10, 5, 10, 5),
                Foreground = new SolidColorBrush(Color.FromArgb(0xBB, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var header = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                Cursor = Cursors.Hand,
                Child = title,
                Visibility = Visibility.Collapsed,
                MinHeight = 0
            };

            var stack = new Grid();
            stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(header, 0);
            Grid.SetRow(console, 1);
            stack.Children.Add(header);
            stack.Children.Add(console);
            chrome.Child = stack;

            // 点击格子 = 把焦点切到这个画面（不是切标签）
            chrome.MouseLeftButtonUp += (_, e) =>
            {
                SetFocus(row, reason: "tile-click");
                e.Handled = true;
            };
            chrome.MouseRightButtonUp += (_, e) =>
            {
                // 右键：把该画面单独显示（单画面模式）
                SetFocus(row, reason: "tile-rightclick");
                if (CurrentLayoutMode != ConsoleLayoutMode.Single)
                {
                    App.Config.Settings.RdpLayoutMode = (int)ConsoleLayoutMode.Single;
                    try { ConfigStore.Save(App.Config); } catch { }
                    ApplyLayout();
                    UpdateTileChrome();
                    UpdateLayoutUi();
                }
                e.Handled = true;
            };

            // ★ 画面本体不参与命中测试：点击由 chrome 捕获，
            //   否则 WindowsFormsHost 会把点击吞掉，无法"点格子换焦点"。
            //   真正接管键鼠仍然只通过保护层（首次点击不透传）。
            console.IsHitTestVisible = false;

            var tile = new Tile
            {
                Row = row,
                Shell = chrome,
                Chrome = chrome,
                Header = header,
                Title = title,
                Console = console
            };
            _tiles.Add(tile);
            TileHost.Children.Add(chrome);
            RememberTileOrder();
            return tile;
        }

        /// <summary>该用户是否已经有 Tile（= 画面已打开过）。</summary>
        private Tile? FindTile(UserRow row)
            => _tiles.FirstOrDefault(t => ReferenceEquals(t.Row, row));

        /// <summary>
        /// 记住画面在流式布局里的顺序（账号 Id 列表）。
        /// 只存顺序，不存坐标 —— 坐标由布局函数现算，避免窗口尺寸变化后失效。
        /// </summary>
        private void RememberTileOrder()
        {
            try
            {
                var order = App.Config.Settings.RdpViewOrder;
                order.Clear();
                foreach (var t in _tiles) order.Add(t.Row.Account.Id);
                ConfigStore.Save(App.Config);
            }
            catch { }
        }

        /// <summary>
        /// 打开画面时恢复上次的顺序：新账号追加到末尾，已有账号保持原位。
        /// </summary>
        private static int OrderIndex(List<string> order, string accountId)
        {
            int i = order.IndexOf(accountId);
            if (i >= 0) return i;
            order.Add(accountId);
            return order.Count - 1;
        }

        // ── 焦点 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 把焦点切到某个画面。
        /// 单画面布局下等同于「切标签」，多画面布局下只换焦点格子，布局不动。
        /// </summary>
        private void SetFocus(UserRow row, string reason)
        {
            if (row.Console == null) return;
            var tile = EnsureTile(row);
            if (ReferenceEquals(_focusTile, tile) && ReferenceEquals(_viewing, row)) return;

            _focusTile = tile;
            _viewing = row;
            _showGeneralSettings = false;

            foreach (var t in _tiles)
                t.Console.SetForegroundActive(ReferenceEquals(t, tile));

            // 布局模式决定要不要铺满
            if (CurrentLayoutMode == ConsoleLayoutMode.Single)
            {
                ApplyLayout();
            }
            else
            {
                // 多画面：只更新高亮，不动格子尺寸
                UpdateTileChrome();
                // 让焦点格子尽量滚到可见区域
                ScrollTileIntoView(tile);
            }

            // ★ 切换焦点**不**重新上锁。
            //   保护层现在覆盖整块画布，重新上锁的唯一判据是「光标离开画布范围」
            //   （见 RdpConsole.CanvasScreenRectProvider → WatchPointerCapture）。
            //   在这里无条件 _shieldReleased = false 会让"点一下格子就又被锁住"，
            //   多画面下每换一次焦点都要再点一下，非常难用。
            //   只有"画面本身变了"（还没连上 / 新开的标签）才需要重新挂保护层。
            bool consoleChanged = ReferenceEquals(_shieldWatchedConsole, row.Console) == false;
            _shieldPendingAfterBusy = !(row.Console?.IsConnected ?? false);
            if (consoleChanged || row.Console?.IsConnected != true) ArmShieldForNewTab(row);
            UpdateShield();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateShield();
                SyncShieldSize();
                _shieldOverlayWindow?.SyncToAnchor();
            }), System.Windows.Threading.DispatcherPriority.Loaded);

            row.Console.EnsureInputRestored();
            RenderTabs();
            Debug("画面焦点 → " + row.Account.Caption + "（" + reason + "）");
        }

        private void ScrollTileIntoView(Tile tile)
        {
            try
            {
                if (ViewportScroll.ScrollableHeight <= 0) return;
                var top = Canvas.GetTop(tile.Chrome);
                var h = tile.Chrome.ActualHeight;
                if (double.IsNaN(top)) return;
                double viewTop = ViewportScroll.VerticalOffset;
                double viewBottom = viewTop + ViewportScroll.ViewportHeight;
                if (top < viewTop) ViewportScroll.ScrollToVerticalOffset(top);
                else if (top + h > viewBottom)
                    ViewportScroll.ScrollToVerticalOffset(top + h - ViewportScroll.ViewportHeight);
            }
            catch { }
        }

        // ── 布局 ─────────────────────────────────────────────────────────

        /// <summary>按当前模式重排所有格子。只改几何与可见性，不增删控件。</summary>
        private bool _applyingLayout;

        private void ApplyLayout()
        {
            if (!_initialized) return;
            if (_applyingLayout) return;   // 防止 SizeChanged 回调递归
            _applyingLayout = true;
            try { ApplyLayoutCore(); }
            finally { _applyingLayout = false; }
        }

        private void ApplyLayoutCore()
        {

            var mode = CurrentLayoutMode;
            int count = _tiles.Count;

            // 单画面：只留焦点格子可见，铺满；但控件仍在树里。
            if (mode == ConsoleLayoutMode.Single)
            {
                int vw = (int)Math.Round(ViewportHostWidth());
                int vh = (int)Math.Round(ViewportHostHeight());
                if (vw < 1 || vh < 1) return;

                foreach (var t in _tiles)
                {
                    // ★ 未连接的花面不显示：WindowsFormsHost 一旦可见就会露出
                    //   黑底+转圈的启动画面，并且会盖住所有 WPF 元素。
                    //   连接成功后 ShowViewPageAsync 会重新调用 ApplyLayout。
                    // ★ 判据只能是 IsConnected：Console 已经常驻在 Tile 里，
                    //   它的 WPF IsVisible 会随父级可见性一起变，用来判断是循环的。
                    bool on = ReferenceEquals(t, _focusTile) && t.Console?.IsConnected == true;
                    t.Chrome.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                    if (on)
                    {
                        Canvas.SetLeft(t.Chrome, 0);
                        Canvas.SetTop(t.Chrome, 0);
                        t.Chrome.Width = vw;
                        t.Chrome.Height = vh;
                    }
                    t.Header.Visibility = Visibility.Collapsed;
                }
                TileHost.Width = vw;
                TileHost.Height = vh;
                ViewportScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                _layoutDirty = false;
                SyncNativeVisibility();
                return;
            }

            int w = (int)Math.Round(ViewportHostWidth());
            int h = (int)Math.Round(ViewportHostHeight());
            if (w < 1 || h < 1) return;

            // ★ 只给"能显示的画面"分配格子。
            //   未连接的画面如果也占位，会出现一块黑底板，看起来像加载失败。
            var ordered = OrderedTiles();
            var shown = ordered.Where(t => t.Console?.IsConnected == true).ToList();
            // 只在"有画面被隐藏"时记一条，避免每次布局都刷日志
            if (shown.Count != _tiles.Count)
                Debug("布局：可见 " + shown.Count + "/" + _tiles.Count + "（未连接的画面不占格子）");
            var hidden = ordered.Where(t => t.Console?.IsConnected != true).ToList();
            foreach (var t in hidden) t.Chrome.Visibility = Visibility.Collapsed;

            if (shown.Count == 0)
            {
                TileHost.Width = w;
                TileHost.Height = h;
                ViewportScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                UpdateTileChrome();
                _layoutDirty = false;
                SyncNativeVisibility();
                return;
            }

            var result = ViewLayout.Compute(w, h, mode, shown.Count);

            for (int i = 0; i < shown.Count; i++)
            {
                var t = shown[i];
                var r = result.Tiles[i];
                t.Chrome.Visibility = Visibility.Visible;
                Canvas.SetLeft(t.Chrome, r.X);
                Canvas.SetTop(t.Chrome, r.Y);
                t.Chrome.Width = r.Width;
                t.Chrome.Height = r.Height;
                t.Header.Visibility = Visibility.Visible;
            }

            TileHost.Width = w;
            TileHost.Height = Math.Max(h, result.ContentHeight);
            ViewportScroll.VerticalScrollBarVisibility =
                result.NeedsScroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;

            UpdateTileChrome();
            _layoutDirty = false;

            // ★ 布局算完后，把"该显示的"原生窗口显式打开。
            //   WPF 的 Visibility 管不到 WindowsFormsHost 的独立 HWND，
            //   显式同步一次，切页/最小化恢复/布局切换都不会留下隐藏的窗口。
            SyncNativeVisibility();
        }

        /// <summary>按当前可见性显式同步每个画面的原生窗口。</summary>
        private void SyncNativeVisibility()
        {
            if (_pageHidden) return;
            if (!IsVisible) return;   // 本页整体不可见时由 SetHiddenForPageSwitch 负责
            try
            {
                var win = Window.GetWindow(this);
                if (win != null && win.WindowState == WindowState.Minimized) return;
            }
            catch { }

            bool visibleNow = false;
            foreach (var t in _tiles)
            {
                try
                {
                    bool show = t.Chrome.Visibility == Visibility.Visible;
                    if (show) visibleNow = true;
                    t.Console?.SetNativeVisible(show);
                }
                catch { }
            }

            // ★ 自检：本页可见、有画面应显示，但实际操作完成后仍有原生窗口不可见，
            //   说明位置没恢复（通常是宿主被移到屏幕外后坐标算错）。再补一次。
            if (visibleNow)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        foreach (var t in _tiles)
                        {
                            if (t.Chrome.Visibility != Visibility.Visible) continue;
                            t.Console?.SetNativeVisible(true);
                        }
                    }
                    catch { }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        /// <summary>
        /// 格子顺序：跟随 RdpViewOrder（打开顺序 / 标签顺序）。
        /// ★ 焦点不改变格子位置 —— 否则每次点一下就整片重排，
        ///   用户会晕，而且画面会在格子里跳来跳去。
        /// </summary>
        private List<Tile> OrderedTiles()
        {
            var order = App.Config.Settings.RdpViewOrder;
            var list = new List<Tile>(_tiles);
            list.Sort((a, b) =>
            {
                int ia = order.IndexOf(a.Row.Account.Id);
                int ib = order.IndexOf(b.Row.Account.Id);
                if (ia < 0) ia = int.MaxValue;
                if (ib < 0) ib = int.MaxValue;
                return ia.CompareTo(ib);
            });
            return list;
        }

        private void UpdateTileChrome()
        {
            bool multi = CurrentLayoutMode != ConsoleLayoutMode.Single;
            foreach (var t in _tiles)
            {
                bool focus = ReferenceEquals(t, _focusTile);
                if (t.Chrome == null) continue;
                t.Chrome.BorderBrush = new SolidColorBrush(focus
                    ? Color.FromArgb(0xAA, 0x7C, 0x5C, 0xFF)
                    : Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
                t.Chrome.BorderThickness = new Thickness(focus && multi ? 2 : 1);
                t.Header.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 计算布局用的宽度：多画面模式**总是预留滚动条宽度**。
        ///
        /// ★ 必须这样，否则会出现死循环：
        ///   内容超高 → 出现竖滚动条 → 可用宽变窄 → 格子变矮 → 内容不再超高
        ///   → 滚动条消失 → 可用宽变宽 → 又超高 …… 画面会不停闪。
        ///   预留之后同一尺寸永远算出同一结果，一次收敛。
        /// </summary>
        /// <summary>
        /// 保护层 / 连接动画的锚点：**画面外框的内边**，不是 ScrollViewer。
        ///
        /// ★ 为什么不能用 ScrollViewer 当锚点（实测漂移）：
        ///   ScrollViewer.ActualWidth 含滚动条宽度，而 PointToScreen 取的是它的左上角；
        ///   两者一混用，保护层就会比外框宽出「滚动条 + 边框」，显示成跑到窗口外面。
        ///   用一个尺寸由外框内边决定的 Grid 当锚点，几何永远一致。
        /// </summary>
        private FrameworkElement OverlayAnchor =>
            (FrameworkElement?)OverlayAnchorLayer ?? (FrameworkElement)ViewportScroll;

        /// <summary>
        /// 整块画面区（画布）的屏幕矩形，物理像素。
        /// 用它作为「移出范围才重新上锁」的判据。
        /// </summary>
        private (int left, int top, int width, int height)? CanvasScreenRect()
        {
            try
            {
                var anchor = OverlayAnchor;
                if (anchor == null || !anchor.IsVisible) return null;
                if (anchor.ActualWidth < 4 || anchor.ActualHeight < 4) return null;

                var win = Window.GetWindow(this);
                if (win == null || win.WindowState == WindowState.Minimized) return null;

                var tl = anchor.PointToScreen(new Point(0, 0));
                var br = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
                int left = (int)Math.Round(tl.X);
                int top = (int)Math.Round(tl.Y);
                int w = (int)Math.Round(br.X - tl.X);
                int h = (int)Math.Round(br.Y - tl.Y);
                if (left < -20000 || top < -20000 || w < 4 || h < 4) return null;
                return (left, top, w, h);
            }
            catch { return null; }
        }

        /// <summary>某用户画面格子的屏幕矩形（物理像素）。格子是纯 WPF 元素，从不会被移出屏幕。</summary>
        private (int left, int top, int width, int height)? TileScreenRect(UserRow row)
        {
            try
            {
                var tile = FindTile(row);
                if (tile == null) return null;
                var el = tile.Chrome;
                if (el == null || !el.IsVisible) return null;
                if (el.ActualWidth < 4 || el.ActualHeight < 4) return null;

                var win = Window.GetWindow(this);
                if (win == null || win.WindowState == WindowState.Minimized) return null;

                var tl = el.PointToScreen(new Point(0, 0));
                var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
                int left = (int)Math.Round(tl.X);
                int top = (int)Math.Round(tl.Y);
                int w = (int)Math.Round(br.X - tl.X);
                int h = (int)Math.Round(br.Y - tl.Y);
                if (left < -20000 || top < -20000 || w < 4 || h < 4) return null;
                return (left, top, w, h);
            }
            catch { return null; }
        }

        private double OverlayAnchorWidth()
            => Math.Max(1, OverlayAnchor.ActualWidth);

        private double OverlayAnchorHeight()
            => Math.Max(1, OverlayAnchor.ActualHeight);

        private double ViewportHostWidth()
        {
            // 内容区宽度 = 视口宽 - 当前实际占用的滚动条宽。
            // ScrollViewer.ComputedVerticalScrollBarVisibility 只有在滚动条真的显示时才为 Visible，
            // 用它判断可以避免「Disabled 状态也去减一根滚动条」。
            double w = ViewportScroll.ActualWidth;
            if (ViewportScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible)
                w -= SystemParameters.VerticalScrollBarWidth;
            return Math.Max(1, w);
        }

        private double ViewportHostHeight()
            => Math.Max(1, ViewportScroll.ActualHeight);

        /// <summary>视口尺寸变化：多画面要重排，单画面也要跟着铺满。</summary>
        private void OnViewportResized()
        {
            if (!_initialized) return;
            _layoutDirty = true;
            ApplyLayout();
        }

        // ── 整页隐藏 / 恢复（切到别的导航页时）─────────────────────────────

        /// <summary>本页当前是否被导航切走（隐藏态）。</summary>
        private bool _pageHidden;
        private DateTime _pageHiddenAt;

        /// <summary>
        /// 切走后把每个画面的**原生窗口**藏起来，切回来再恢复。
        ///
        /// ★ 这是必须的：RdpConsole 内部是 WindowsFormsHost，是独立 HWND，
        ///   WPF 的 Visibility.Collapsed 管不到它。不显式 SW_HIDE，RDP 就会
        ///   一直浮在最上层，盖住左侧导航和其它页面（用户实测过）。
        ///
        /// ★ 这里绝不摘控件、绝不 Dispose：ActiveX 和连接全程保留，
        ///   避免"ActiveX 在容器之间反复进出"（0x119 的高风险形态）。
        /// </summary>
        private void SetHiddenForPageSwitch(bool hidden)
        {
            // ★ 幂等：最小化与"页面不可见"会同时触发，重复执行没有意义，
            //   而且会造成隐藏/恢复互相打断（实测表现为画面偶尔全空）。
            if (_pageHidden == hidden && _initialized) { SyncNativeVisibility(); return; }

            _pageHidden = hidden;
            if (hidden) { _pageHiddenAt = DateTime.UtcNow; }
            if (!_initialized) { SyncNativeVisibility(); return; }

            foreach (var t in _tiles)
            {
                try
                {
                    if (hidden)
                    {
                        t.Chrome.Visibility = Visibility.Collapsed;
                        t.Console?.SetNativeVisible(false);
                    }
                    else
                    {
                        // 恢复时交给 ApplyLayout 决定谁该可见，
                        // 再由 SetNativeVisible(true) 把原生窗口显示回来。
                        t.Console?.SetNativeVisible(
                            t.Console.IsConnected && ReferenceEquals(t, _focusTile));
                    }
                }
                catch { }
            }

            if (!hidden)
            {
                ApplyLayout();
                // 恢复后再同步一次，确保"布局说可见"与"原生真可见"一致
                SyncNativeVisibility();
            }
        }

        // ── 移除 ─────────────────────────────────────────────────────────

        private void RemoveTile(UserRow row)
        {
            var tile = FindTile(row);
            if (tile == null) return;

            try { TileHost.Children.Remove(tile.Shell); } catch { }
            _tiles.Remove(tile);
            RememberTileOrder();

            if (ReferenceEquals(_focusTile, tile))
            {
                _focusTile = _tiles.Count > 0 ? _tiles[0] : null;
                _viewing = _focusTile?.Row;
                if (_focusTile != null)
                {
                    foreach (var t in _tiles)
                        t.Console.SetForegroundActive(ReferenceEquals(t, _focusTile));
                }
            }
            ApplyLayout();
            UpdateTileChrome();
        }

        /// <summary>布局按钮：单个 → 双列 → 网格 → 单个。</summary>
        private void BtnLayout_Click(object sender, RoutedEventArgs e)
        {
            var next = CurrentLayoutMode switch
            {
                ConsoleLayoutMode.Single => ConsoleLayoutMode.TwoColumn,
                ConsoleLayoutMode.TwoColumn => ConsoleLayoutMode.Grid,
                _ => ConsoleLayoutMode.Single
            };
            App.Config.Settings.RdpLayoutMode = (int)next;
            try { ConfigStore.Save(App.Config); } catch { }

            if (next == ConsoleLayoutMode.Single && _focusTile == null && _tiles.Count > 0)
                _focusTile = _tiles[0];

            ApplyLayout();
            UpdateTileChrome();
            UpdateLayoutUi();
            Debug("画面布局 → " + next);
        }

        private void UpdateLayoutUi()
        {
            if (LayoutLabel == null) return;
            switch (CurrentLayoutMode)
            {
                case ConsoleLayoutMode.TwoColumn:
                    LayoutLabel.Text = "双列";
                    LayoutGlyph.Text = "\uE8A9";
                    break;
                case ConsoleLayoutMode.Grid:
                    LayoutLabel.Text = "网格";
                    LayoutGlyph.Text = "\uF0E2";
                    break;
                default:
                    LayoutLabel.Text = "单个";
                    LayoutGlyph.Text = "\uE7F4";
                    break;
            }
        }
    }
}
