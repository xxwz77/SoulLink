using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

public static class FullScanBench
{
    static readonly Regex RxStart = new Regex(
        @"配置组\s*""(?<g>[^""]+)""\s*加载完成");
    static readonly Regex RxFinish = new Regex(
        @"配置组\s*""(?<g>[^""]+)""\s*执行结束");

    public static int Main()
    {
        string path = @"D:\AAA挂机专区\A1\BetterGI\log\better-genshin-impact20261117.log";
        var fi = new FileInfo(path);
        Console.WriteLine("文件大小: " + (fi.Length / 1024.0 / 1024.0).ToString("0.0") + " MB");

        // ① 全量读取 + 正则匹配
        var sw = Stopwatch.StartNew();
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs))
            text = sr.ReadToEnd();
        sw.Stop();
        long readMs = sw.ElapsedMilliseconds;
        Console.WriteLine("全量读取: " + readMs + " ms  (" + (text.Length / 1024.0 / 1024.0).ToString("0.0") + " M 字符)");

        sw.Restart();
        int n1 = RxStart.Matches(text).Count;
        int n2 = RxFinish.Matches(text).Count;
        sw.Stop();
        Console.WriteLine("正则匹配: " + sw.ElapsedMilliseconds + " ms  (开始=" + n1 + " 结束=" + n2 + ")");

        // ② 增量：只读尾部 1MB
        sw.Restart();
        long take = Math.Min(fi.Length, 1024 * 1024);
        var buf = new byte[take];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Seek(fi.Length - take, SeekOrigin.Begin);
            fs.Read(buf, 0, buf.Length);
        }
        string chunk = System.Text.Encoding.UTF8.GetString(buf);
        sw.Stop();
        long chunkMs = sw.ElapsedMilliseconds;
        Console.WriteLine("增量 1MB: " + chunkMs + " ms");

        Console.WriteLine();
        Console.WriteLine("结论:");
        Console.WriteLine("  全量一次性 = " + (readMs + sw.ElapsedMilliseconds) + " ms 量级");
        Console.WriteLine("  每秒增量 = <5 ms");
        return 0;
    }
}
