using System;
using System.IO;
using System.Text.RegularExpressions;

public static class ReaderCheck
{
    static readonly Regex RxDaily = new Regex(
        @"日期\s*[:：]\s*(?<date>\d{4}/\d{2}/\d{2})\s*[，,]\s*运行收尾路线\s*(?<route>收尾额外[AB])");
    static readonly Regex RxExp = new Regex(@"狗粮经验\s*(?<exp>-?\d+)");

    public static int Main()
    {
        int fails = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
            if (!ok) fails++;
        }

        // 业务日边界
        Check(BizDay(new DateTime(2026,11,17, 3,59,0)) == new DateTime(2026,11,16), "03:59 -> 前一天");
        Check(BizDay(new DateTime(2026,11,17, 4,0,0))  == new DateTime(2026,11,17), "04:00 -> 当天");
        Check(BizDay(new DateTime(2026,11,17,22,0,0))  == new DateTime(2026,11,17), "22:00 -> 当天");

        // 真实文件
        string path = @"D:\AAA挂机专区\A1\BetterGI\User\JsScript\AAA-Artifacts-Bulk-Supply\records\思.txt";
        string head;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            int take = (int)Math.Min(fs.Length, 65536);
            var buf = new byte[take];
            int got = fs.Read(buf, 0, take);
            head = System.Text.Encoding.UTF8.GetString(buf, 0, got);
        }
        var m = RxDaily.Matches(head);
        Check(m.Count > 0, "解析出记录条目 (" + m.Count + ")");

        var m0 = m[0];
        Console.WriteLine("      最新: " + m0.Groups["date"].Value + " " + m0.Groups["route"].Value);

        // 经验提取（按行取，不跨行）
        int pos = m0.Index;
        int nl = head.IndexOf('\n', pos);
        string line = nl > pos ? head.Substring(pos, nl - pos) : head.Substring(pos);
        var me = RxExp.Match(line);
        Check(me.Success, "经验提取到: " + (me.Success ? me.Groups["exp"].Value : "无"));

        // 上限判断（A=13万, B=5万）
        long exp = me.Success ? long.Parse(me.Groups["exp"].Value) : 0;
        bool isA = m0.Groups["route"].Value == "收尾额外A";
        long limit = isA ? 130000 : 50000;
        Console.WriteLine("      路线=" + (isA?"A":"B") + "  经验=" + exp + "  上限=" + limit + "  已达上限=" + (exp >= limit));

        Console.WriteLine(fails == 0 ? "ALL PASSED" : "FAILURES=" + fails);
        return fails;
    }

    static DateTime BizDay(DateTime n) => n.Hour < 4 ? n.Date.AddDays(-1) : n.Date;
}
