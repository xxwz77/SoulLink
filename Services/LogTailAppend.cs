using System;

namespace SoulLink.Services
{
    /// <summary>
    /// 日志面板的「增量追加」判定。
    ///
    /// ★★ 为什么需要它（2026-11-22 性能修复）★★
    ///
    ///   LaunchPage 的 BGI 日志面板原来每次 BgiLogCenter.Changed 都把
    ///   最多 400 行（6 万字符）整块赋给 TextBlock.Text，再 ScrollToEnd()。
    ///   BGI 满速写日志时这个事件每秒触发 —— 于是**每秒一次整窗文本重排**。
    ///
    ///   实测指纹（docs/research/PERF-3.3.4-20261122.md §3.2）：
    ///     AutomationPeer.UpdateSubtree      1.75%
    ///     Visual.PrecomputeRecursive        2.34%
    ///     TextBlock.OnRender → SimpleTextLine.Create
    ///
    ///   改成增量追加后，只有"真正新增的那几行"会触发排版。
    ///
    /// ★★ 关键难点：快照是滑动尾窗 ★★
    ///
    ///   BgiLogCenter 的快照最多 800 行 / 20 万字符，老行会从**头部**被丢掉。
    ///   所以 `next.StartsWith(prev)` 这种判断在窗口滑动后永远不成立，
    ///   增量会退化成每秒整块替换 —— 等于白改。
    ///
    ///   正确做法：拿 prev 的**尾部**当锚点，到 next 里找它最后出现的位置，
    ///   那个位置就是「已经显示过的内容」与「新增内容」的分界。
    ///
    /// ★★ 两个必须守住的边界（都踩过）★★
    ///
    ///   ① 锚点结束若正好在换行符后（prev 以完整行结尾），不能再往后吃一行，
    ///      否则最新那一行会被算成"已显示"，永远不上屏。
    ///   ② 必须用 LastIndexOf 而不是 IndexOf：BGI 日志有大量完全重复的行
    ///      （同样的进度提示每秒刷一遍），用首次命中会把中间一大段重复行
    ///      当成新增再追加一遍，界面出现成片重复。
    /// </summary>
    public static class LogTailAppend
    {
        /// <summary>锚点长度。太短容易误命中，太长在短日志上失效。</summary>
        private const int Anchor = 48;

        /// <summary>重叠小于这个长度就不算"同一段"，宁可整块替换也别拼错。</summary>
        private const int MinUsefulOverlap = Anchor;

        /// <summary>
        /// 计算 next 的前多少个字符是 prev 已经显示过的。
        ///
        /// 返回值语义：调用方应当只追加 <c>next.Substring(返回值)</c>。
        /// 返回 0 表示无法可靠增量（切换账号、日志轮转、内容完全对不上），
        /// 调用方应退回整块替换。
        /// </summary>
        public static int Overlap(string prev, string next)
        {
            if (string.IsNullOrEmpty(prev) || string.IsNullOrEmpty(next)) return 0;
            if (prev.Length < Anchor || next.Length < Anchor) return 0;

            try
            {
                // 用 prev 的尾部当锚点（内容上一定出现在 next 里，除非日志被重建）。
                string anchor = prev.Substring(prev.Length - Anchor);

                // ★ 必须取**最后**一次出现：重复行很常见，取首次会把重复段当新增。
                int at = next.LastIndexOf(anchor, StringComparison.Ordinal);
                if (at < 0) return 0;

                int overlap = at + Anchor;

                // ★ 锚点结束正好在行边界 → 已经是精确分界，不能再往后吃一行。
                if (next[at + Anchor - 1] == '\n') return overlap;

                // 否则推到下一行行首，避免把半行拼进界面。
                int nl = next.IndexOf('\n', overlap);
                if (nl >= 0) overlap = nl + 1;

                return overlap >= MinUsefulOverlap ? overlap : 0;
            }
            catch { return 0; }
        }
    }
}
