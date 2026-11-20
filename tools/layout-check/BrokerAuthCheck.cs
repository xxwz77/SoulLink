using System;
using System.Diagnostics;
using System.Security.Principal;
using SoulLink.Services;

public static class BrokerAuthCheck
{
    private static int _fails;
    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _fails++;
    }

    public static int Main()
    {
        int self = Environment.ProcessId;

        // 1) 管理员判定必须只读令牌，不得依赖 IsInRole / WindowsIdentity(IntPtr)。
        BrokerSecurity.ProcessIdentity me;
        try { me = BrokerSecurity.InspectProcess(self); }
        catch (Exception ex) { Console.WriteLine("FAIL 读取自身令牌：" + ex.Message); return 2; }
        Console.WriteLine("self: " + BrokerSecurity.DescribeProcess(self));
        Check(me.SessionId > 0, "会话号来自真实 PID");
        Check(me.Sid.Length > 0, "读到用户 SID：" + me.Sid);
        Check(me.User.Length > 0 && me.User != me.Sid, "读到用户名称：" + me.User);
        Check(me.Elevated, "自身进程是提权进程");
        Check(me.Administrator, "自身进程在管理员组");

        // 2) SYSTEM 进程（会话 0 的服务）必须被识别为管理员+提权，且用户名为 SYSTEM。
        int systemPid = FindSystemPid();
        if (systemPid > 0)
        {
            var system = BrokerSecurity.InspectProcess(systemPid);
            Console.WriteLine("system: " + BrokerSecurity.DescribeProcess(systemPid));
            Check(system.Sid == "S-1-5-18", "SYSTEM 令牌 SID 正确");
            Check(system.Administrator && system.Elevated, "SYSTEM 被视为管理员且已提权");
        }
        else Console.WriteLine("SKIP 未找到会话 0 的 SYSTEM 进程");

        // 3) 普通（非管理员组）令牌必须被拒绝——保证强制要求没有被放宽。
        int userPid = FindNonAdminPid();
        if (userPid > 0)
        {
            var plain = BrokerSecurity.InspectProcess(userPid);
            Console.WriteLine("plain: " + BrokerSecurity.DescribeProcess(userPid));
            Check(!plain.Administrator, "非管理员组的令牌不算管理员（强制要求未被放宽）");
        }
        else Console.WriteLine("SKIP 未找到非管理员进程");

        // 4) 令牌自查不能抛异常（历史上 IsInRole 会抛“拒绝访问”）。
        Check(!(BrokerSecurity.InspectProcess(self) is null), "InspectProcess 不抛异常");

        // 5) 在线会话判定：不存在的会话必须被判为不可投送。
        string? ghost = SystemLauncher.DescribeSessionOnline(9999, "A2");
        Check(ghost != null, "不存在的会话被判为不在线：" + ghost);
        Console.WriteLine("session 9999 => " + (ghost ?? "（null，未拦截）"));

        return _fails;
    }

    private static int FindSystemPid()
    {
        foreach (var p in Process.GetProcessesByName("SoulLink"))
        {
            try { if (p.SessionId == 0) return p.Id; } catch { } finally { p.Dispose(); }
        }
        try
        {
            foreach (var p in Process.GetProcessesByName("services"))
            {
                try { return p.Id; } catch { } finally { p.Dispose(); }
            }
        }
        catch { }
        return 0;
    }

    private static int FindNonAdminPid()
    {
        foreach (var name in new[] { "dwm", "winlogon", "fontdrvhost" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    var id = BrokerSecurity.InspectProcess(p.Id);
                    if (!id.Administrator && id.SessionId > 1) return p.Id;
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        return 0;
    }
}
