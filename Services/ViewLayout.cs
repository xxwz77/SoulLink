using System;
using System.Collections.Generic;
using System.Windows;

namespace SoulLink.Services
{
    /// <summary>中控台画面区的排列模式。</summary>
    public enum ConsoleLayoutMode
    {
        /// <summary>单个：只显示焦点画面，铺满视口（原有行为）。</summary>
        Single = 0,
        /// <summary>双列：两列纵向流式排布，可滚动浏览，画面保持 16:9。</summary>
        TwoColumn = 1,
        /// <summary>网格：等分铺满，不滚动（适合 2~4 个）。</summary>
        Grid = 2,
        /// <summary>单列：一列纵向流式排布，可滚动浏览，画面保持 16:9（画面多时最清晰）。</summary>
        OneColumn = 3
    }

    /// <summary>一个画面格子在视口里的位置（全部是物理像素）。</summary>
    public readonly struct TileRect
    {
        public TileRect(int index, int x, int y, int width, int height)
        {
            Index = index; X = x; Y = y; Width = width; Height = height;
        }

        public int Index { get; }
        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }

        public override string ToString() => "#" + Index + " " + X + "," + Y + " " + Width + "x" + Height;
    }

    /// <summary>一次布局计算的结果。</summary>
    public sealed class ViewLayoutResult
    {
        public ViewLayoutResult(IReadOnlyList<TileRect> tiles, int contentHeight, int viewportHeight = 0)
        {
            Tiles = tiles;
            ContentHeight = contentHeight;
            NeedsScroll = contentHeight > viewportHeight;
        }

        /// <summary>每个画面格子的矩形（物理像素，相对视口左上角）。</summary>
        public IReadOnlyList<TileRect> Tiles { get; }

        /// <summary>内容总高度；大于视口高度时说明需要滚动。</summary>
        public int ContentHeight { get; }

        /// <summary>内容是否超出视口，需要滚动浏览。</summary>
        public bool NeedsScroll { get; }
    }

    /// <summary>
    /// 画面区布局计算。**纯函数，不碰任何控件**，因此可以脱离 RDP 直接单元测试。
    ///
    /// ★ 为什么单独抽出来：
    ///   分屏事故（0x119）之后，任何多画面布局都必须先在「没有真实 RDP」的条件下
    ///   把矩形算对。把布局做成纯函数，可以让 4/6/9 个格子的分配完全可验证，
    ///   而不用启动 ActiveX。
    ///
    /// 三种模式：
    ///   · Single      —— 1 个格子铺满（原有行为）
    ///   · TwoColumn   —— 两列纵向流式，保持 16:9，超出视口就滚动（网格/双列上线后都走这里）
    ///   · Grid        —— 等分铺满，不滚动（2~4 个时最省屏幕）
    /// </summary>
    public static class ViewLayout
    {
        /// <summary>格子之间的间距（物理像素）。</summary>
        public const int Gap = 8;

        /// <summary>画面宽高比（16:9）。</summary>
        public const double Aspect = 16.0 / 9.0;

        /// <summary>
        /// 计算布局。
        /// </summary>
        /// <param name="viewportWidth">视口宽（物理像素）</param>
        /// <param name="viewportHeight">视口高（物理像素）</param>
        /// <param name="mode">排列模式</param>
        /// <param name="count">画面个数</param>
        public static ViewLayoutResult Compute(int viewportWidth, int viewportHeight,
            ConsoleLayoutMode mode, int count)
        {
            if (viewportWidth < 1) viewportWidth = 1;
            if (viewportHeight < 1) viewportHeight = 1;
            if (count < 0) count = 0;

            var tiles = new List<TileRect>(count);
            if (count == 0)
                return new ViewLayoutResult(tiles, 0);

            switch (mode)
            {
                case ConsoleLayoutMode.Single:
                    tiles.Add(new TileRect(0, 0, 0, viewportWidth, viewportHeight));
                    return new ViewLayoutResult(tiles, viewportHeight, viewportHeight);

                case ConsoleLayoutMode.Grid:
                    return ComputeGrid(viewportWidth, viewportHeight, count);

                case ConsoleLayoutMode.OneColumn:
                    return ComputeOneColumn(viewportWidth, viewportHeight, count);

                case ConsoleLayoutMode.TwoColumn:
                default:
                    return ComputeTwoColumn(viewportWidth, viewportHeight, count);
            }
        }

        /// <summary>网格：按接近正方形的行列数等分，铺满视口，不滚动。</summary>
        private static ViewLayoutResult ComputeGrid(int vw, int vh, int count)
        {
            int cols = (int)Math.Ceiling(Math.Sqrt(count));
            if (cols < 1) cols = 1;
            // 4 个按 2x2，2 个按 1x2（左右并排），3 个按 2x2（最后一格留空）
            if (count == 2) cols = 2;
            int rows = (int)Math.Ceiling((double)count / cols);
            if (rows < 1) rows = 1;

            int cellW = Math.Max(1, (vw - Gap * (cols + 1)) / cols);
            int cellH = Math.Max(1, (vh - Gap * (rows + 1)) / rows);

            var tiles = new List<TileRect>(count);
            for (int i = 0; i < count; i++)
            {
                int r = i / cols;
                int c = i % cols;
                int x = Gap + c * (cellW + Gap);
                int y = Gap + r * (cellH + Gap);
                tiles.Add(new TileRect(i, x, y, cellW, cellH));
            }
            return new ViewLayoutResult(tiles, vh, vh);
        }

        /// <summary>
        /// 单列纵向流：一列铺满宽度，每格保持 16:9，从上往下排；内容超高就滚动。
        /// 画面数量多、或需要在单屏里看清某一路时用这个模式。
        /// </summary>
        private static ViewLayoutResult ComputeOneColumn(int vw, int vh, int count)
        {
            int cellW = Math.Max(1, vw - Gap * 2);
            // 保持 16:9；但一列时高度往往受视口限制，最多占视口的 ~92%，
            // 保证至少能看到下一格的顶部，让"可以往下滚"这件事可见。
            int cellH = Math.Max(1, (int)Math.Round(cellW / Aspect));
            int maxCellH = Math.Max(1, (int)Math.Round(vh * 0.92));
            if (cellH > maxCellH)
            {
                cellH = maxCellH;
                cellW = Math.Max(1, (int)Math.Round(cellH * Aspect));
            }

            var tiles = new List<TileRect>(count);
            for (int i = 0; i < count; i++)
                tiles.Add(new TileRect(i, Gap, Gap + i * (cellH + Gap), cellW, cellH));

            int contentHeight = Gap + count * (cellH + Gap);
            return new ViewLayoutResult(tiles, contentHeight, vh);
        }

        /// <summary>
        /// 双列纵向流：两列等宽，每格保持 16:9，按行往下排；内容超高就滚动。
        /// 这是「4/6/9 个都有」的主力模式——格子多时靠滚动浏览，而不是把每格压扁。
        /// </summary>
        private static ViewLayoutResult ComputeTwoColumn(int vw, int vh, int count)
        {
            const int cols = 2;

            // 视口很窄时退化成单列，避免每格小到不可用。
            if (vw < 480)
            {
                int cellW0 = Math.Max(1, vw - Gap * 2);
                int cellH0 = Math.Max(1, (int)Math.Round(cellW0 / Aspect));
                var single = new List<TileRect>(count);
                for (int i = 0; i < count; i++)
                    single.Add(new TileRect(i, Gap, Gap + i * (cellH0 + Gap), cellW0, cellH0));
                int total0 = Gap + count * (cellH0 + Gap);
                return new ViewLayoutResult(single, total0, vh);
            }

            // ★★ 2.17.40：格子要**填满视口**，不能只按宽度推高度 ★★
            //
            //   用户实测（双列、2 个画面、视口约 1500x500）：
            //     格子只有 430x242，高/视口 = 48% —— 下半截全空着，
            //     看起来就是"双列模式没有自适应窗口大小"。
            //
            //   根因：只用了 `cellH = cellW / 16*9` 这一条式子。
            //   宽屏时宽度富余、高度按比例算出来自然只有一半，
            //   而下面的 maxCellH 只在"高度放不下"时才介入（缩），从不放大。
            //
            //   现在同时用两个约束取**较大**的那个（但都不超过可用空间）：
            //     · 按宽度铺满：cellW = (vw - 间距) / 列数，cellH = cellW / 16*9
            //     · 按高度铺满：cellH = (vh - 间距) / 行数，cellW = cellH * 16*9
            //   取能填满更多区域的那一套，从而真正随窗口自适应。
            int rowsTotal = Math.Max(1, (count + cols - 1) / cols);
            int availW = Math.Max(1, (vw - Gap * (cols + 1)) / cols);
            int availH = Math.Max(1, (vh - Gap * (rowsTotal + 1)) / rowsTotal);

            // ★ 关键：格子**宽不能超过 availW、高不能超过 availH**，
            //   在这两个上限内尽量大（保持 16:9）。
            //
            //   先取"按宽度铺满"得到的高度；若高度超了 → 说明高度是瓶颈，
            //   按高度反推宽度（此时宽度必然 ≤ availW）。
            //   两种情况的最终宽高都**不会**越界 —— 越界会导致两列横向溢出，
            //   那比"只占半屏"更糟（画面会被裁掉）。
            int cellW = availW;
            int cellH = Math.Max(1, (int)Math.Round(cellW / Aspect));
            if (cellH > availH)
            {
                // 高度是瓶颈：按高度铺满，反推宽度
                cellH = availH;
                cellW = Math.Max(1, (int)Math.Round(cellH * Aspect));
            }
            // （若 cellH 本来就没超过 availH，说明宽度是瓶颈，
            //   当前 cellW 已经是 availW 上限，不能再放大 —— 保持原样即可。）

            var tiles = new List<TileRect>(count);
            for (int i = 0; i < count; i++)
            {
                int r = i / cols;
                int c = i % cols;
                int x = Gap + c * (cellW + Gap);
                int y = Gap + r * (cellH + Gap);
                tiles.Add(new TileRect(i, x, y, cellW, cellH));
            }

            int rows = (count + cols - 1) / cols;
            int contentHeight = Gap + rows * (cellH + Gap);
            return new ViewLayoutResult(tiles, contentHeight, vh);
        }
    }
}
