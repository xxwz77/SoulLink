using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using SoulLink;
using SoulLink.Services;

/// <summary>诊断：手动代理进程的命令行和进程名到底是什么样。</summary>
public static class AgentCmdProbe
{
    public static int Main()
    {
        int sessionId = Process.GetCurrentProcess().SessionId;
        string outDir = Path.Combine(Path.GetTempPath(), "soullink-cmdprobe");
        try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
        Directory.CreateDirectory(outDir);

        string src = null;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            string c = Path.Combine(d.FullName, "dist", "SoulLink.exe");
            if (File.Exists(c)) { src = c; break; }
        }
        if (src == null) { Console.WriteLine("no dist"); return 2; }

        string agentExe = Path.Combine(outDir, "SoulLink.Agent.exe");
        File.Copy(src, agentExe, true);

        string hostPipe = AppIdentity.RelayPipePrefix + sessionId + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        using var server = new NamedPipeServerStream(hostPipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var psi = new ProcessStartInfo(agentExe) { UseShellExecute = false, CreateNoWindow = true };
        using var agent = Process.Start(psi);
        Console.WriteLine("started pid=" + agent!.Id);

        Thread.Sleep(2500);
        try
        {
            var p = Process.GetProcessById(agent.Id);
            Console.WriteLine("  ProcessName  = [" + p.ProcessName + "]");
            Console.WriteLine("  SessionId    = " + p.SessionId);
            p.Dispose();
        }
        catch (Exception e) { Console.WriteLine("  process gone: " + e.Message); }

        string cmd = ProcessCommandLine.Read(agent.Id);
        Console.WriteLine("  CommandLine  = [" + cmd + "]");
        Console.WriteLine("  LastError    = " + ProcessCommandLine.LastError);
        Console.WriteLine("  AppIdentity.Name here = " + AppIdentity.Name);
        Console.WriteLine("  HasManualAgent       = " + ManualAgent.HasManualAgent(sessionId, AppIdentity.Name));

        try { if (!agent.HasExited) agent.Kill(); } catch { }
        try { Directory.Delete(outDir, true); } catch { }
        return 0;
    }
}
