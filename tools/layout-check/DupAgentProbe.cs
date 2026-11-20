using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using SoulLink;
using SoulLink.Services;

/// <summary>诊断重复启动拦截：第二个代理到底退没退、退出码多少、日志说了什么。</summary>
public static class DupAgentProbe
{
    public static int Main()
    {
        int sessionId = Process.GetCurrentProcess().SessionId;
        string outDir = Path.Combine(Path.GetTempPath(), "soullink-dup");
        try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
        Directory.CreateDirectory(outDir);

        string src = null;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            string c = Path.Combine(d.FullName, "dist", "SoulLink.exe");
            if (File.Exists(c)) { src = c; break; }
        }
        string agentExe = Path.Combine(outDir, "SoulLink.Agent.exe");
        File.Copy(src!, agentExe, true);

        string hostPipe = AppIdentity.RelayPipePrefix + sessionId + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        using var server = new NamedPipeServerStream(hostPipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Console.WriteLine("--- 第一个代理 ---");
        var a1 = Process.Start(new ProcessStartInfo(agentExe) { UseShellExecute = false, CreateNoWindow = true })!;
        Thread.Sleep(3000);
        Console.WriteLine("a1 pid=" + a1.Id + " alive=" + !a1.HasExited);
        Console.WriteLine("HasManualAgent=" + ManualAgent.HasManualAgent(sessionId, AppIdentity.Name));
        foreach (var m in ManualAgent.FindManualAgents(AppIdentity.Name))
            Console.WriteLine("   manual: pid=" + m.Pid + " exe=" + m.Exe);

        Console.WriteLine("--- 第二个代理（应被拦下）---");
        var a2 = Process.Start(new ProcessStartInfo(agentExe) { UseShellExecute = false, CreateNoWindow = true })!;
        bool exited = a2.WaitForExit(20000);
        Console.WriteLine("a2 pid=" + a2.Id + " exited=" + exited + (exited ? " code=" + a2.ExitCode : " (still running)"));

        Console.WriteLine("--- 第二个代理日志 ---");
        string log2 = Path.Combine(Path.GetTempPath(), "soullink-agent-" + a2.Id + ".log");
        Console.WriteLine(File.Exists(log2) ? File.ReadAllText(log2) : "(no log)");
        string log1 = Path.Combine(Path.GetTempPath(), "soullink-agent-" + a1.Id + ".log");
        Console.WriteLine("--- 第一个代理日志 ---");
        Console.WriteLine(File.Exists(log1) ? File.ReadAllText(log1) : "(no log)");

        try { if (!a2.HasExited) a2.Kill(); } catch { }
        try { if (!a1.HasExited) a1.Kill(); } catch { }
        try { Directory.Delete(outDir, true); } catch { }
        return 0;
    }
}
