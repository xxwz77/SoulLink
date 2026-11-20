using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using SoulLink;
using SoulLink.Models;
using SoulLink.Services;

/// <summary>
/// 回归：AccountStateCenter.GetByUser 必须能处理界面上的展示名（"A2 · 山客"）。
///
/// ★ 背景（2.17.49）：启动台总览卡片直接拿 row.User（展示名）去查状态，
///   精确比较永远不命中 → 返回 null → 被当成"未绑定" → **已绑定的账号显示「BGI 未绑定」**。
///   现在调用方改用真实用户名，GetByUser 另加一层兜底；两条都要守住。
/// </summary>
public static class BoundLookupCheck
{
    static int fails;
    static void Check(bool ok, string s) { Console.WriteLine((ok ? "ok   " : "FAIL ") + s); if (!ok) fails++; }

    public static int Main()
    {
        var cfg = ConfigStore.Load();
        typeof(App).GetProperty("Config", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, cfg);
        AccountStateCenter.Start(cfg, 1000);
        Thread.Sleep(3500);

        var bound = cfg.Accounts.Where(a => BgiPathResolver.IsPinned(a.UserName)).ToList();
        Console.WriteLine("已绑定账号 = " + string.Join(",", bound.Select(a => a.UserName + "/" + a.DisplayName)));
        if (bound.Count == 0)
        {
            Console.WriteLine("SKIP: 当前身份下没有已绑定账号（用 SOULLINK_CONFIG 指定带绑定的配置再跑）");
            AccountStateCenter.Stop();
            return 0;
        }

        foreach (var acc in bound)
        {
            var byReal = AccountStateCenter.GetByUser(acc.UserName);
            Check(byReal != null && byReal.Bound, acc.UserName + " 用真实用户名查到且 Bound=true");

            if (!string.IsNullOrWhiteSpace(acc.DisplayName))
            {
                foreach (var form in new[] { acc.UserName + " · " + acc.DisplayName, acc.UserName + " " + acc.DisplayName, acc.UserName + "·" + acc.DisplayName })
                {
                    var v = AccountStateCenter.GetByUser(form);
                    Check(v != null && v.Bound, "展示名 '" + form + "' 也能命中（不再误判为未绑定）");
                }
            }
        }
        Check(AccountStateCenter.GetByUser("nobody-xyz") == null, "不存在的账号仍然返回 null");

        AccountStateCenter.Stop();
        Console.WriteLine(fails == 0 ? "ALL PASSED" : "FAILURES=" + fails);
        return fails;
    }
}
