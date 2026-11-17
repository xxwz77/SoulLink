using System;
using System.Collections.Generic;
using SoulLink.Services;

// 纯函数验证：不依赖 WPF/RDP，直接跑布局数学。
public static class LayoutCheck
{
    public static int Main()
    {
        int fails = 0;
        void Check(bool cond, string what) { if (!cond) { Console.WriteLine("FAIL " + what); fails++; } }

        // 1) Single
        var r = ViewLayout.Compute(2560, 1440, (ConsoleLayoutMode)0, 1);
        Check(r.Tiles.Count == 1 && r.Tiles[0].Width == 2560 && r.Tiles[0].Height == 1440, "single full");

        // 2) Two column, 4 tiles: 2 cols x 2 rows, 16:9, no overlap
        r = ViewLayout.Compute(2560, 1440, (ConsoleLayoutMode)1, 4);
        Check(r.Tiles.Count == 4, "two-col count");
        for (int i = 1; i < 4; i++)
            Check(r.Tiles[i].X + r.Tiles[i].Width <= 2560, "two-col fits width #" + i);
        double ar = (double)r.Tiles[0].Width / r.Tiles[0].Height;
        Check(Math.Abs(ar - 16.0 / 9.0) < 0.02, "two-col aspect " + ar);

        // 3) Two column, 9 tiles: content taller than viewport -> needs scroll
        r = ViewLayout.Compute(2560, 1440, (ConsoleLayoutMode)1, 9);
        Check(r.Tiles.Count == 9, "nine count");
        Check(r.NeedsScroll, "nine needs scroll (content=" + r.ContentHeight + ")");
        Check(r.Tiles[8].Y > r.Tiles[0].Y, "nine ordered downward");

        // 4) Grid, 4 tiles: 2x2 equal
        r = ViewLayout.Compute(2560, 1440, (ConsoleLayoutMode)2, 4);
        Check(r.Tiles.Count == 4, "grid count");
        Check(r.Tiles[0].Width == r.Tiles[3].Width, "grid equal width");
        Check(!r.NeedsScroll, "grid no scroll");

        // 5) Grid, 2 tiles side by side
        r = ViewLayout.Compute(2560, 1440, (ConsoleLayoutMode)2, 2);
        Check(r.Tiles[0].Y == r.Tiles[1].Y, "grid2 same row");
        Check(r.Tiles[0].X < r.Tiles[1].X, "grid2 left to right");

        // 6) Degenerate: zero count / tiny viewport
        Check(ViewLayout.Compute(2560, 1440, (ConsoleLayoutMode)1, 0).Tiles.Count == 0, "zero count");
        r = ViewLayout.Compute(1, 1, (ConsoleLayoutMode)1, 3);
        Check(r.Tiles.Count == 3 && r.Tiles[0].Width >= 1 && r.Tiles[0].Height >= 1, "tiny viewport");

        // 7) No overlaps in two-column for 6
        r = ViewLayout.Compute(2560, 1440, (ConsoleLayoutMode)1, 6);
        for (int i = 0; i < r.Tiles.Count; i++)
            for (int j = i + 1; j < r.Tiles.Count; j++)
            {
                var a = r.Tiles[i]; var b = r.Tiles[j];
                bool overlap = a.X < b.X + b.Width && b.X < a.X + a.Width
                            && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
                Check(!overlap, "overlap " + i + "/" + j);
            }

        Console.WriteLine(fails == 0 ? "ALL LAYOUT CHECKS PASSED" : fails + " FAILURES");
        return fails;
    }
}
