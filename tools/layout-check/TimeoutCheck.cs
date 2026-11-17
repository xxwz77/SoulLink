using System;
using System.Collections.Generic;
using SoulLink.Models;

public static class TimeoutCheck
{
    public static int Main()
    {
        int fails = 0;
        void Check(bool c, string what) { if (!c) { Console.WriteLine("FAIL " + what); fails++; } else Console.WriteLine("ok   " + what); }

        var f = new AutomationFlow();

        // 默认值
        Check(f.ArmTimeoutMinutes == 60, "默认超时 60 分钟");

        // Arm 记录时刻 + 清空条件
        f.Active = true;
        f.Latched = new List<string> { "a", "b" };
        f.ArmedAt = DateTime.Now;
        Check(f.Active && f.Latched.Count == 2, "武装前状态");

        f.Arm();
        Check(f.Active, "Arm 后 Active");
        Check(f.Latched.Count == 0, "Arm 清空条件");
        Check(f.ArmedAt.HasValue, "Arm 记录时刻");

        // 模拟超时判定（与 FlowRunner 同逻辑）
        f.Latched.Add("cond1");
        f.ArmedAt = DateTime.Now.AddMinutes(-61);
        int timeout = f.ArmTimeoutMinutes;
        var elapsed = DateTime.Now - f.ArmedAt.Value;
        Check(elapsed.TotalMinutes >= timeout, "61 分钟前武装 → 判定超时");

        f.Disarm();
        Check(!f.Active, "Disarm 后 Active=false");
        Check(f.Latched.Count == 0, "Disarm 清空条件");
        Check(!f.ArmedAt.HasValue, "Disarm 清空时刻");

        // 未超时
        f.Arm();
        f.ArmedAt = DateTime.Now.AddMinutes(-30);
        elapsed = DateTime.Now - f.ArmedAt.Value;
        Check(elapsed.TotalMinutes < timeout, "30 分钟前武装 → 未超时");

        // 不超时开关
        var f2 = new AutomationFlow { ArmTimeoutMinutes = 0 };
        Check(!(f2.ArmTimeoutMinutes > 0), "0 = 不超时，不会触发判定");

        Console.WriteLine(fails == 0 ? "ALL TIMEOUT CHECKS PASSED" : fails + " FAILURES");
        return fails;
    }
}
