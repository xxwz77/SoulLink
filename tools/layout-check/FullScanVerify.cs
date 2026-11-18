using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

public static class FullScanVerify
{
    static readonly Regex RxStart = new Regex(
        @"配置组\s*""(?<g>[^""]+)""\s*加载完成[，,]\s*共\s*(?<n>\d+)\s*个脚本[，,]\s*开始执行");
    static readonly Regex RxFinish = new Regex(@"配置组\s*""(?<g>[^""]+)""\s*执行结束");
    static readonly Regex RxFail = new Regex(@"配置组\s*""(?<g>[^""]+)""\s*未成功完成");
    static readonly Regex RxCancel = new Regex(@"取消执行配置组\s*[:：]\s*""(?<g>[^""]+)""");

    public static int Main()
    {
        string path = @"D:\AAA挂机专区\A1\BetterGI\log\better-genshin-impact20261117.log";
        var fi = new FileInfo(path);
        Console.WriteLine("日志: " + (fi.Length/1024.0/1024.0).ToString("0.0") + " MB");

        // A. 全量
        var sw = Stopwatch.StartNew();
        string full;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs))
            full = sr.ReadToEnd();
        sw.Stop();
        var fullMs = sw.ElapsedMilliseconds;
        var groupsFull = Parse(full);
        Console.WriteLine("全量: " + fullMs + " ms, 配置组 " + groupsFull.Count + " 个");

        // B. 只读尾部 4000 行（旧做法）
        sw.Restart();
        string tail = TailDeep(path, 4000);
        sw.Stop();
        var groupsTail = Parse(tail);
        Console.WriteLine("尾部4000行: " + sw.ElapsedMilliseconds + " ms, 配置组 " + groupsTail.Count + " 个");

        Console.WriteLine();
        Console.WriteLine("=== 全量能识别、尾部漏掉的配置组 ===");
        int missed = 0;
        foreach (var kv in groupsFull)
            if (!groupsTail.ContainsKey(kv.Key)) { Console.WriteLine("   漏掉: " + kv.Key + " = " + kv.Value); missed++; }
        Console.WriteLine("漏掉数量: " + missed);

        return 0;
    }

    static Dictionary<string,string> Parse(string text)
    {
        var state = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var ev = new List<(int, string, string)>();
        foreach (Match m in RxStart.Matches(text))  ev.Add((m.Index, m.Groups["g"].Value, "Running"));
        foreach (Match m in RxFinish.Matches(text)) ev.Add((m.Index, m.Groups["g"].Value, "Finished"));
        foreach (Match m in RxFail.Matches(text))   ev.Add((m.Index, m.Groups["g"].Value, "Failed"));
        foreach (Match m in RxCancel.Matches(text)) ev.Add((m.Index, m.Groups["g"].Value, "Cancelled"));
        ev.Sort((a,b)=>a.Item1.CompareTo(b.Item1));
        foreach (var e in ev) state[e.Item2] = e.Item3;
        return state;
    }

    static string TailDeep(string path, int maxLines)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long budget = Math.Min(fs.Length, 16L*1024*1024);
        long pos = fs.Length;
        var sb = new StringBuilder();
        int lines = 0;
        while (pos > 0 && lines < maxLines)
        {
            long take = Math.Min(512*1024, Math.Min(pos, budget));
            pos -= take;
            fs.Seek(pos, SeekOrigin.Begin);
            var buf = new byte[take];
            int got = fs.Read(buf, 0, buf.Length);
            string part = Encoding.UTF8.GetString(buf, 0, got);
            sb.Insert(0, part);
            foreach (var c in part) if (c == '\n') lines++;
            if (pos <= fs.Length - budget) break;
        }
        return sb.ToString();
    }
}
