using System;
using System.Diagnostics;
using System.IO;
using SoulLink;
using SoulLink.Services;

/// <summary>
/// 挂机版身份隔离回归：
/// SoulLink-Stable.exe 必须得到独立身份 SoulLink-Stable，
/// 从而与测试版（SoulLink）在管道 / 互斥体 / 配置目录 / 任务名上完全隔离。
/// </summary>
public static class StableIdentityCheck
{
    private static int _fails;
    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _fails++;
    }

    public static int Main(string[] args)
    {
        Console.WriteLine("本进程身份 = " + AppIdentity.Name);
        Console.WriteLine("  ConfigDir   = " + AppIdentity.ConfigDir);
        Console.WriteLine("  RelayPrefix = " + AppIdentity.RelayPipePrefix);
        Console.WriteLine("  MutexName   = " + AppIdentity.MutexName);
        Console.WriteLine("  TaskPrefix  = " + AppIdentity.TaskPrefix);

        // 以 --identity=SoulLink-Stable 模拟挂机版身份
        Check(AppIdentity.Name == "SoulLink-Stable", "身份为 SoulLink-Stable（与测试版隔离）");
        Check(AppIdentity.RelayPipePrefix == "SoulLink-Stable_Relay_",
              "relay 管道前缀独立：" + AppIdentity.RelayPipePrefix);
        Check(AppIdentity.MutexName.Contains("SoulLink-Stable"), "单实例互斥体独立");
        Check(AppIdentity.ConfigDir.EndsWith("SoulLink-Stable", StringComparison.OrdinalIgnoreCase),
              "配置目录独立：" + AppIdentity.ConfigDir);
        Check(AppIdentity.TaskPrefix == "SoulLink-StableRun_", "计划任务前缀独立");

        // 与测试版不冲突
        Check(AppIdentity.RelayPipePrefix != "SoulLink_Relay_", "不会与测试版抢同一批管道");

        // 挂机版 exe 应存在且版本正确
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        string? stable = null;
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            string c = Path.Combine(d.FullName, "dist", "stable", "SoulLink-Stable.exe");
            if (File.Exists(c)) { stable = c; break; }
        }
        Check(stable != null, "找到 dist\\stable\\SoulLink-Stable.exe");
        if (stable != null)
        {
            var vi = FileVersionInfo.GetVersionInfo(stable);
            Console.WriteLine("stable exe ver = " + vi.FileVersion);
            Check(vi.FileVersion == "2.10.2.0", "挂机版版本为 2.10.2.0");
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "RESULT=OK 全部通过" : "RESULT=FAIL 失败 " + _fails + " 项");
        return _fails;
    }
}
