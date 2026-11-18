using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Collections.Generic;

public static class GroupTrackerCheck
{
    static readonly Regex RxStart = new Regex(
        @"配置组\s*""(?<g>[^""]+)""\s*加载完成[，,]\s*共\s*(?<n>\d+)\s*个脚本[，,]\s*开始执行");
    static readonly Regex RxFinish = new Regex(
        @"配置组\s*""(?<g>[^""]+)""\s*执行结束");
    static readonly Regex RxFail = new Regex(
        @"配置组\s*""(?<g>[^""]+)""\s*未成功完成[，,]?\s*结果\s*[:：]\s*(?<r>\S*)");
    static readonly Regex RxCancel = new Regex(
        @"取消执行配置组\s*[:：]\s*""(?<g>[^""]+)""");

    public static int Main()
    {
        int fails = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
            if (!ok) fails++;
        }

        string path = @"D:\AAA挂机专区\A1\BetterGI\log\better-genshin-impact20261117.log";
        string text;
        using (var fs2 = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs2))
            text = sr.ReadToEnd();

        // 模拟 tracker：按位置顺序应用事件
        var state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var events = new List<(int pos, string g, string s)>();
        foreach (Match m in RxStart.Matches(text))  events.Add((m.Index, m.Groups["g"].Value, "Running"));
        foreach (Match m in RxFinish.Matches(text)) events.Add((m.Index, m.Groups["g"].Value, "Finished"));
        foreach (Match m in RxFail.Matches(text))   events.Add((m.Index, m.Groups["g"].Value, "Failed"));
        foreach (Match m in RxCancel.Matches(text)) events.Add((m.Index, m.Groups["g"].Value, "Cancelled"));
        events.Sort((a,b) => a.pos.CompareTo(b.pos));
        foreach (var e in events) state[e.g] = e.s;

        Console.WriteLine("状态统计: " + state.Count + " 个配置组");
        foreach (var kv in state) Console.WriteLine("   " + kv.Key + " = " + kv.Value);

        // 实测预期（来自今天的日志）
        Check(state.ContainsKey("联机锄地-混合精英"), "识别到 联机锄地-混合精英");
        Check(state["联机锄地-混合精英"] == "Finished", "  状态=Finished");
        Check(state.ContainsKey("联机锄地-小怪-队员"), "识别到 联机锄地-小怪-队员");
        Check(state["联机锄地-小怪-队员"] == "Failed", "  状态=Failed");
        Check(state.ContainsKey("CD采集-挖矿"), "识别到 CD采集-挖矿");
        Check(state["CD采集-挖矿"] == "Running", "  状态=Running (最后是开始)");

        Console.WriteLine(fails == 0 ? "ALL PASSED" : "FAILURES=" + fails);
        return fails;
    }
}
