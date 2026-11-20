using System;
using System.Diagnostics;
using System.Linq;
using SoulLink;
using SoulLink.Services;

public static class WhyHostProbe
{
    public static int Main()
    {
        Console.WriteLine("identity = " + AppIdentity.Name);
        Console.WriteLine("--- 自身 ---");
        Console.WriteLine("self cmd=[" + ProcessCommandLine.Read(Environment.ProcessId) + "]");
        Console.WriteLine("--- 本应用进程 ---");
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string n = p.ProcessName;
                if (!n.Equals("SoulLink", StringComparison.OrdinalIgnoreCase)
                    && !n.Equals("SoulLink.Agent", StringComparison.OrdinalIgnoreCase)) continue;
                Console.WriteLine("pid=" + p.Id + " sess=" + p.SessionId + " name=" + n);
                Console.WriteLine("   cmd=[" + ProcessCommandLine.Read(p.Id) + "]");
            }
            catch { }
            finally { p.Dispose(); }
        }
        return 0;
    }
}
