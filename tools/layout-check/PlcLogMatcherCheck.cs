using System;
using System.Globalization;
using SoulLink.Services;

/// <summary>
/// PlcLogMatcher 的无副作用回归测试。
/// 项目文件直接编译生产源码，避免在测试里复刻一套匹配逻辑。
/// </summary>
public static class PlcLogMatcherCheck
{
    private static int _count;
    private static int _failures;

    private static void Check(bool condition, string name)
    {
        _count++;
        Console.WriteLine((condition ? "ok   " : "FAIL ") + name);
        if (!condition) _failures++;
    }

    private static string At(DateTime time, string level, string message)
        => "[" + time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
           + "] [" + level + "] " + message;

    public static int Main()
    {
        var now = new DateTime(2026, 11, 18, 12, 0, 30, 500);
        string fresh = At(now.AddSeconds(-3), "INF", "任务结束 Alpha");
        string boundary = At(now.AddSeconds(-10), "INF", "边界命中");
        string old = At(now.AddSeconds(-10.001), "INF", "过期命中");
        string future = At(now.AddSeconds(3), "INF", "未来命中");

        Check(PlcLogMatcher.Contains(fresh, "任务结束", false, "", 10, now),
              "完整日期时间：窗口内关键字命中");
        Check(PlcLogMatcher.Contains(boundary, "边界命中", false, "", 10, now),
              "完整日期时间：窗口起点包含");
        Check(!PlcLogMatcher.Contains(old, "过期命中", false, "", 10, now),
              "完整日期时间：窗口外旧行不算新日志");
        Check(!PlcLogMatcher.Contains(future, "未来命中", false, "", 10, now),
              "完整日期时间：明显未来行不命中");
        Check(PlcLogMatcher.Contains(fresh + "\n" + old, "过期命中", false, "", 10, now) == false,
              "旧行不会因附近存在新行而续命");

        var afterMidnight = new DateTime(2026, 11, 18, 0, 0, 3);
        Check(PlcLogMatcher.Contains("[2026-11-17 23:59:58.123] [INF] 跨日完整", "跨日完整", false, "", 10, afterMidnight),
              "跨零点：完整日期保留前一天");
        Check(PlcLogMatcher.Contains("[23:59:58.123] [INF] 跨日时分秒", "跨日时分秒", false, "", 10, afterMidnight),
              "跨零点：仅时分秒归属最近的过去");
        Check(!PlcLogMatcher.Contains("[23:59:40] [INF] 跨日过旧", "跨日过旧", false, "", 10, afterMidnight),
              "跨零点：前一日过旧行被过滤");
        Check(!PlcLogMatcher.Contains("[12:00:35] [INF] 当日未来", "当日未来", false, "", 10, now),
              "仅时分秒：同日未来行不命中");

        string wrn = At(now.AddSeconds(-1), "WRN", "共同关键字");
        string err = At(now.AddSeconds(-1), "ERR", "共同关键字");
        Check(PlcLogMatcher.Contains(wrn + "\n" + err, "共同关键字", false, "err", 10, now),
              "级别：过滤不区分大小写并选中 ERR");
        Check(!PlcLogMatcher.Contains(wrn, "共同关键字", false, "INF", 10, now),
              "级别：不匹配的级别被排除");
        Check(!PlcLogMatcher.Contains(At(now.AddSeconds(-1), "INF", "只有 INF"), "只有 INF", false, "I", 10, now),
              "级别：不是前缀子串匹配");

        string regexText = At(now.AddSeconds(-2), "INF", "Route=A-17, result=Finished");
        Check(PlcLogMatcher.Contains(regexText, @"route=a-\d+.*finished", true, "INF", 10, now),
              "正则：忽略大小写并匹配真实内容");
        Check(!PlcLogMatcher.Contains(regexText, @"route=B-\d+", true, "", 10, now),
              "正则：不匹配时返回 false");
        Check(!PlcLogMatcher.Contains(regexText, "[", true, "", 10, now),
              "正则：非法表达式安全返回 false");
        Check(PlcLogMatcher.Contains(At(now.AddSeconds(-1), "INF", "AbCdEf"), "abcde", false, "", 10, now),
              "关键字：忽略大小写");

        Check(!PlcLogMatcher.Contains("任务结束，没有时间戳", "任务结束", false, "", 10, now),
              "无时间戳：不能当作新日志");
        Check(!PlcLogMatcher.Contains("[INF] 只有级别没有时间", "只有级别", false, "", 10, now),
              "无时间戳：只有级别也不能当作新日志");
        Check(!PlcLogMatcher.Contains("[not-a-time] [INF] 非法时间", "非法时间", false, "", 10, now),
              "无时间戳：非法时间文本被排除");
        Check(!PlcLogMatcher.Contains("普通旧行\n任务结束", "任务结束", false, "", 10, now),
              "无时间戳：旧行不会继承其它行的时间");

        string[] errors =
        {
            At(now.AddSeconds(-1), "ERR", "普通错误"),
            At(now.AddSeconds(-1), "INF", "执行失败：样例"),
            At(now.AddSeconds(-1), "INF", "配置组未成功完成"),
            At(now.AddSeconds(-1), "INF", "发生异常：样例")
        };
        foreach (string line in errors)
            Check(PlcLogMatcher.HasError(line, 10, now), "错误匹配：" + line);
        Check(!PlcLogMatcher.HasError(At(now.AddSeconds(-11), "ERR", "旧错误"), 10, now),
              "错误匹配：窗口外旧错误不命中");
        Check(!PlcLogMatcher.HasError("[ERR] 无时间戳错误", 10, now),
              "错误匹配：无时间戳错误不当作新错误");
        Check(!PlcLogMatcher.HasError(At(now.AddSeconds(-1), "INF", "正常完成"), 10, now),
              "错误匹配：正常行不误报");

        Console.WriteLine(_failures == 0
            ? "ALL PASSED (" + _count + ")"
            : "FAILURES=" + _failures + "/" + _count);
        return _failures == 0 ? 0 : 1;
    }
}
