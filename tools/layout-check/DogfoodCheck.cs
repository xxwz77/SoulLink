using System;
using System.IO;
using System.Text.RegularExpressions;

public static class DogfoodCheck
{
    static readonly Regex RxExp1 = new Regex(
        @"(?<!JS 通知关闭，消息被拦截:\s*)狗粮路线获取狗粮经验\s*[:：]\s*(?<v>\d+)");
    static readonly Regex RxMora1 = new Regex(
        @"(?<!JS 通知关闭，消息被拦截:\s*)狗粮路线获取摩拉\s*[:：]\s*(?<v>\d+)");
    static readonly Regex RxExp2 = new Regex(
        @"(?<!JS 通知关闭，消息被拦截:\s*)[\u4e00-\u9fa5]{1,6}可获得(?<v>\d+)经验");
    static readonly Regex RxMora2 = new Regex(
        @"(?<!JS 通知关闭，消息被拦截:\s*)[\u4e00-\u9fa5]{1,6}获得(?<v>\d+)摩拉(?!鼬)");

    public static int Main(string[] args)
    {
        string path = @"D:\AAA挂机专区\A1\BetterGI\log\better-genshin-impact20261114.log";
        string text = File.ReadAllText(path);

        long exp = 0, mora = 0;
        foreach (Match m in RxExp1.Matches(text)) exp += long.Parse(m.Groups["v"].Value);
        foreach (Match m in RxExp2.Matches(text)) exp += long.Parse(m.Groups["v"].Value);
        foreach (Match m in RxMora1.Matches(text)) mora += long.Parse(m.Groups["v"].Value);
        foreach (Match m in RxMora2.Matches(text)) mora += long.Parse(m.Groups["v"].Value);

        Console.WriteLine("经验累计: " + exp.ToString("N0"));
        Console.WriteLine("摩拉累计: " + mora.ToString("N0"));
        Console.WriteLine("期望:     147,800 / 37,086");
        bool ok = exp == 147800 && mora == 37086;
        Console.WriteLine(ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }
}
