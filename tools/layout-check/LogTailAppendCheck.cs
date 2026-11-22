using System;
using SoulLink.Services;

/// <summary>
/// 回归：日志面板的增量追加判定（LogTailAppend.Overlap）。
///
/// ★ 为什么值得单独测：这个方法决定"界面上会不会重复刷行 / 漏掉新行"。
///   两个边界都真实踩过：
///     · 锚点落在换行符上却继续往后吃一行 → 最新一行永远不上屏
///     · 用 IndexOf 而非 LastIndexOf → 重复行被当成新增，界面成片重复
///   详见 Services/LogTailAppend.cs 的注释。
/// </summary>
public static class LogTailAppendCheck
{
    static int fails;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
        if (!ok) fails++;
    }

    /// <summary>返回"应当被追加的部分"，方便直接比对。</summary>
    static string WouldAppend(string prev, string next)
    {
        int ov = LogTailAppend.Overlap(prev, next);
        return next.Substring(ov);
    }

    static string Line(int i) =>
        "[2026-11-22 15:00:00] 这是一行日志内容，长度够长用于测试重叠检测 number " + i + "\n";

    public static int Main()
    {
        // ── 基本用例 ────────────────────────────────────────────────
        var lines = new string[300];
        for (int i = 0; i < lines.Length; i++) lines[i] = Line(i);
        string prev = string.Concat(lines);

        Check(WouldAppend(prev, prev) == "", "内容没变 → 一个字符都不追加");
        Check(WouldAppend(prev, prev + Line(300) + Line(301)) == Line(300) + Line(301),
              "纯追加 → 只追加新增的两行");
        Check(WouldAppend("", prev) == prev, "首次填充 → 全量追加");
        Check(WouldAppend(prev, "完全无关的短内容") == "完全无关的短内容",
              "内容对不上 → 退回整块替换（overlap=0）");

        // ── 滑动尾窗：快照丢掉了最老的 100 行 ──────────────────────
        var sb = new System.Text.StringBuilder();
        for (int i = 100; i < 300; i++) sb.Append(Line(i));
        sb.Append(Line(300));
        string slid = sb.ToString();
        Check(WouldAppend(prev, slid) == Line(300),
              "滑动尾窗（老行被丢）→ 仍只追加真正的 1 行新内容");

        // ── 重复行：BGI 日志非常常见 ───────────────────────────────
        var rep = new System.Text.StringBuilder();
        for (int i = 0; i < 200; i++) rep.Append("AAAA same line content here padding padding\n");
        string repeated = rep.ToString();
        string repeatedPlus = repeated + "BBBB brand new line\n";
        Check(WouldAppend(repeated, repeatedPlus) == "BBBB brand new line\n",
              "大量重复行 → 不把重复段当新增（必须用 LastIndexOf）");

        // 重复行 + 同时滑动
        var sb2 = new System.Text.StringBuilder();
        for (int i = 0; i < 150; i++) sb2.Append("AAAA same line content here padding padding\n");
        sb2.Append("BBBB brand new line\n");
        Check(WouldAppend(repeated, sb2.ToString()) == "BBBB brand new line\n",
              "重复行 + 滑动尾窗 → 仍只追加新行");

        // ── 边界：最新一行不被吞掉 ─────────────────────────────────
        // 这是上一版真实存在的 bug：overlap 多算了 55 个字符，第 300 行上不了屏。
        int ov = LogTailAppend.Overlap(prev, prev + Line(300));
        Check(ov == prev.Length, "overlap 精确等于旧长度（不多吃一行）");
        Check((prev + Line(300)).Substring(ov).StartsWith("["), "追加内容从行首开始");

        // ── 过短内容不做增量 ───────────────────────────────────────
        Check(LogTailAppend.Overlap("short", "short") == 0, "过短文本不增量（避免误判）");
        Check(LogTailAppend.Overlap(null, "x") == 0, "null 安全");
        Check(LogTailAppend.Overlap("", "") == 0, "空串安全");

        Console.WriteLine(fails == 0 ? "ALL PASSED" : "FAILURES=" + fails);
        return fails;
    }
}
