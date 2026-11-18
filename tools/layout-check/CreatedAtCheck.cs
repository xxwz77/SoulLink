using System;

public static class CreatedAtCheck
{
    static bool ShouldFire(DateTime now, int h, int m, int s, DateTime? lastRun,
                          int catchUpHours, DateTime? createdAt)
    {
        var target = now.Date.AddHours(h).AddMinutes(m).AddSeconds(s);
        if (now < target) return false;
        if (lastRun.HasValue && lastRun.Value >= target) return false;
        if (catchUpHours > 0 && (now - target).TotalHours > catchUpHours) return false;
        if (createdAt.HasValue && target < createdAt.Value.Date) return false;
        if (createdAt.HasValue && target < createdAt.Value && target.Date == createdAt.Value.Date) return false;
        return true;
    }

    public static int Main()
    {
        int fails = 0;
        void Check(bool ok, string what) { Console.WriteLine((ok?"ok   ":"FAIL ")+what); if(!ok) fails++; }

        var day = new DateTime(2026, 11, 18);

        // ★ 核心场景：10:00 新建 04:00 的流程 -> 当天不补跑
        Check(!ShouldFire(day.AddHours(10), 4, 0, 0, null, 12, day.AddHours(10)),
              "10:00 创建 04:00 流程 -> 当天不触发");

        // 第二天 04:00 正常触发
        Check(ShouldFire(day.AddDays(1).AddHours(4), 4, 0, 0, null, 12, day.AddHours(10)),
              "次日 04:00 -> 正常触发");

        // 02:00 创建 04:00 流程 -> 当天正常触发（创建时间早于目标）
        Check(ShouldFire(day.AddHours(4), 4, 0, 0, null, 12, day.AddHours(2)),
              "02:00 创建 04:00 流程 -> 当天 04:00 触发");

        // 老流程（createdAt = null）保持原行为
        Check(ShouldFire(day.AddHours(10), 4, 0, 0, null, 12, null),
              "老流程无 CreatedAt -> 保持补跑行为");

        // 昨天创建的流程，今天早晨补跑仍可
        Check(ShouldFire(day.AddHours(6), 4, 0, 0, null, 12, day.AddDays(-1).AddHours(20)),
              "昨天创建 -> 今天 06:00 仍可补跑");

        Console.WriteLine(fails == 0 ? "ALL PASSED" : "FAILURES=" + fails);
        return fails;
    }
}
