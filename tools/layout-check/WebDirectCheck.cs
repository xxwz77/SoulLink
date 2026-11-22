using System;
using SoulLink.Services;

/// <summary>回归：账号 → 网页直连端口分配。</summary>
public static class WebDirectCheck
{
    static int fails;
    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
        if (!ok) fails++;
    }

    public static int Main()
    {
        // 基础规则：basePort + 序号 + 1
        Check(WebDirect.PortFor(10800, 0) == 10801, "第 0 个账号 → base+1");
        Check(WebDirect.PortFor(10800, 1) == 10802, "第 1 个账号 → base+2");
        Check(WebDirect.PortFor(10800, 4) == 10805, "第 4 个账号 → base+5");

        // 同一账号永远同一个端口（这是"按配置顺序而不是会话号"的意义）
        Check(WebDirect.PortFor(10800, 2) == WebDirect.PortFor(10800, 2), "分配是确定性的");

        // 每个账号端口互不相同
        var seen = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < 10; i++) seen.Add(WebDirect.PortFor(10800, i));
        Check(seen.Count == 10, "10 个账号拿到 10 个不同端口");

        // 边界保护
        Check(WebDirect.PortFor(0, 0) == 0, "未启用网页（base=0）→ 0");
        Check(WebDirect.PortFor(10800, -1) == 0, "负序号 → 0");
        Check(WebDirect.PortFor(10800, 99) == 0, "序号过大 → 0（不无限外扩）");
        Check(WebDirect.PortFor(65535, 0) == 0, "越过 65535 → 0");

        // 路径白名单：代理不是通用文件服务器
        Check(WebDirect.IsAllowedPath("/live"), "放行 /live");
        Check(WebDirect.IsAllowedPath("/api/input"), "放行 /api/input");
        Check(WebDirect.IsAllowedPath("/api/info"), "放行 /api/info");
        Check(!WebDirect.IsAllowedPath("/file/a.mp4"), "不放行 /file/（历史录像走主程序读盘）");
        Check(!WebDirect.IsAllowedPath("/"), "不放行 /");
        Check(!WebDirect.IsAllowedPath("/api/status"), "不放行主程序专属接口");
        Check(!WebDirect.IsAllowedPath("/..\\..\\windows\\win.ini"), "不放行穿越路径");
        Check(!WebDirect.IsAllowedPath(""), "不放行空路径");

        Console.WriteLine(fails == 0 ? "ALL PASSED" : "FAILURES=" + fails);
        return fails;
    }
}
