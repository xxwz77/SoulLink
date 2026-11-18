using System;
using System.IO;
using System.Text.RegularExpressions;

public static class DogfoodRecordCheck
{
    static readonly Regex RxDaily = new Regex(
        @"日期\s*[:：]\s*(?<date>\d{4}/\d{2}/\d{2})\s*[，,]\s*运行收尾路线\s*(?<route>收尾额外[AB])\s*[，,]\s*狗粮经验\s*(?<exp>-?\d+)\s*[，,]\s*摩拉\s*(?<mora>-?\d+)");

    public static int Main()
    {
        int fails = 0;
        string path = @"D:\AAA挂机专区\A1\BetterGI\User\JsScript\AAA-Artifacts-Bulk-Supply\records\思.txt";

        string head;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            int take = (int)Math.Min(fs.Length, 64 * 1024);
            var buf = new byte[take];
            int got = fs.Read(buf, 0, take);
            head = System.Text.Encoding.UTF8.GetString(buf, 0, got);
        }

        var m = RxDaily.Matches(head);
        Console.WriteLine("匹配条数: " + m.Count);
        if (m.Count == 0) { Console.WriteLine("FAIL 无匹配"); return 1; }

        var m0 = m[0];
        Console.WriteLine("最新日期: " + m0.Groups["date"].Value);
        Console.WriteLine("路线    : " + m0.Groups["route"].Value);
        Console.WriteLine("经验    : " + m0.Groups["exp"].Value);
        Console.WriteLine("摩拉    : " + m0.Groups["mora"].Value);

        bool ok = m0.Groups["date"].Value == "2026/11/17"
               && m0.Groups["route"].Value == "收尾额外A"
               && m0.Groups["exp"].Value == "142480"
               && m0.Groups["mora"].Value == "38957";
        Console.WriteLine(ok ? "PASS 与实测数据一致" : "FAIL 数据不符");
        return ok ? 0 : 1;
    }
}
