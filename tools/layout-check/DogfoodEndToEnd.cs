using System;
using System.IO;
using System.Text.RegularExpressions;

// 端到端：模拟 FlowRunner 的解析链路（BGI exe → accountName → records → 路线）
public static class DogfoodEndToEnd
{
    static readonly Regex RxDaily = new Regex(
        @"日期\s*[:：]\s*(?<date>\d{4}/\d{2}/\d{2})\s*[，,]\s*运行收尾路线\s*(?<route>收尾额外[AB])\s*[，,]\s*狗粮经验\s*(?<exp>-?\d+)\s*[，,]\s*摩拉\s*(?<mora>-?\\d+)");

    public static int Main()
    {
        int fails = 0;
        string[] bgiDirs = {
            @"D:\AAA挂机专区\A1\BetterGI",
            @"D:\AAA挂机专区\A2\BetterGI",
            @"D:\AAA挂机专区\A3\BetterGI",
        };

        foreach (var dir in bgiDirs)
        {
            Console.WriteLine("--- " + dir);
            if (!Directory.Exists(dir)) { Console.WriteLine("    (不存在，跳过)"); continue; }

            // 1) 找 accountName
            string groupFile = Path.Combine(dir, "User", "ScriptGroup", "狗粮批发.json");
            string account = "";
            if (File.Exists(groupFile))
            {
                var t = File.ReadAllText(groupFile);
                var m = Regex.Match(t, @"""accountName""\s*:\s*""(?<n>[^""]*)""");
                if (m.Success) account = m.Groups["n"].Value.Trim();
            }
            Console.WriteLine("    accountName = '" + account + "'");

            // 2) 拼 records 路径
            string rec = Path.Combine(dir, "User", "JsScript", "AAA-Artifacts-Bulk-Supply",
                                      "records", (account.Length > 0 ? account : "默认账户") + ".txt");
            Console.WriteLine("    records     = " + (File.Exists(rec) ? "找到" : "缺失"));
            if (!File.Exists(rec)) continue;

            // 3) 解析
            string head;
            using (var fs = new FileStream(rec, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int take = (int)Math.Min(fs.Length, 65536);
                var buf = new byte[take];
                int got = fs.Read(buf, 0, take);
                head = System.Text.Encoding.UTF8.GetString(buf, 0, got);
            }
            var mm = RxDaily.Matches(head);
            if (mm.Count == 0) { Console.WriteLine("    FAIL 无匹配"); fails++; continue; }

            var m0 = mm[0];
            string date = m0.Groups["date"].Value;
            string route = m0.Groups["route"].Value;
            bool isA = route == "收尾额外A";
            bool expectA = false;   // 触发"跳转"的配置
            bool triggered = expectA ? isA : !isA;

            Console.WriteLine("    日期=" + date + "  路线=" + route);
            Console.WriteLine("    触发(期望B) = " + triggered + "  ->  " + (triggered ? "跳转别的一条龙" : "不跳转，走联机"));

            // 4) 日期必须是今天或昨天
            if (DateTime.TryParseExact(date, "yyyy/MM/dd", null,
                    System.Globalization.DateTimeStyles.None, out var day))
            {
                bool fresh = day == DateTime.Today || day == DateTime.Today.AddDays(-1);
                Console.WriteLine("    日期有效 = " + fresh);
                if (!fresh) { Console.WriteLine("    ! 过期记录，触发应为 false"); }
            }
        }

        Console.WriteLine(fails == 0 ? "OK" : "FAILURES=" + fails);
        return fails;
    }
}
