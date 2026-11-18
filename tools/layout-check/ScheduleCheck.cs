using System;

public static class ScheduleCheck
{
    static bool ShouldFire(DateTime now, int h, int m, int s, DateTime? lastRun, int catchUpHours)
    {
        var target = now.Date.AddHours(h).AddMinutes(m).AddSeconds(s);
        if (now < target) return false;
        if (lastRun.HasValue && lastRun.Value >= target) return false;
        if (catchUpHours > 0 && (now - target).TotalHours > catchUpHours) return false;
        return true;
    }

    public static int Main()
    {
        int fails = 0;
        void Check(bool ok, string what) { Console.WriteLine((ok?"ok   ":"FAIL ")+what); if(!ok) fails++; }

        var day = new DateTime(2026, 11, 18);

        Check(!ShouldFire(day.AddHours(3), 4, 0, 0, null, 12), "03:00 未到点 -> 不触发");
        Check(ShouldFire(day.AddHours(4), 4, 0, 0, null, 12), "04:00 到点 -> 触发");
        Check(ShouldFire(day.AddHours(4).AddMinutes(30), 4, 0, 0, null, 12), "04:30 晚到 -> 补触发");
        Check(ShouldFire(day.AddHours(10), 4, 0, 0, null, 12), "10:00 晚6小时 -> 补触发");
        Check(!ShouldFire(day.AddHours(5), 4, 0, 0, day.AddHours(4), 12), "今天已跑过 -> 不重复");
        Check(ShouldFire(day.AddHours(4), 4, 0, 0, day.AddDays(-1).AddHours(4), 12), "昨天跑过、今天到点 -> 触发");
        Check(!ShouldFire(day.AddHours(20), 4, 0, 0, null, 12), "20:00 超12小时窗口 -> 不触发");
        Check(ShouldFire(day.AddHours(4).AddSeconds(8), 4, 0, 0, null, 12), "卡顿8秒后仍触发（旧5秒窗口法会漏）");

        Console.WriteLine(fails == 0 ? "ALL PASSED" : "FAILURES=" + fails);
        return fails;
    }
}
