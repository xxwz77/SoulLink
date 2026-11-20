using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using SoulLink;
using SoulLink.Services;

/// <summary>
/// 三种通道模式的互斥验证（基于命名互斥体，不依赖命令行解析）：
///   1. 没有手动代理时不误报
///   2. 手动代理启动后能被宿主识别（手动优先依据）
///   3. 重复启动会拿到退出码 4
///   4. 手动代理退出后标记自动释放
///   5. 命名管道天然单实例
/// </summary>
public static class AgentMutexCheck
{
    private static int _fails;
    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _fails++;
    }

    public static int Main()
    {
        int sessionId = Process.GetCurrentProcess().SessionId;
        string identity = AppIdentity.Name;
        Console.WriteLine("会话号 = " + sessionId + " · 身份 = " + identity);

        // ① 没有手动代理时不误报（注意：Broker 服务/客户端同进程名，不能被算进去）
        bool before = ManualAgent.HasManualAgent(sessionId, identity);
        Console.WriteLine("启动前 HasManualAgent = " + before);
        Check(!before, "没有手动代理时不误报（宿主进程不会被当成代理）");

        // ② 准备代理副本
        string outDir = Path.Combine(Path.GetTempPath(), "soullink-mutex-test");
        try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
        Directory.CreateDirectory(outDir);
        string? src = FindDistExe();
        if (src == null) { Console.WriteLine("FAIL 找不到 dist\\SoulLink.exe"); return 2; }
        string agentExe = Path.Combine(outDir, "SoulLink.Agent.exe");
        File.Copy(src, agentExe, true);

        string hostPipe = AppIdentity.RelayPipePrefix + sessionId + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        using var server = new NamedPipeServerStream(hostPipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var a1 = Process.Start(new ProcessStartInfo(agentExe) { UseShellExecute = false, CreateNoWindow = true })!;
        Console.WriteLine("第一个代理 pid = " + a1.Id);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        bool detected = false;
        while (DateTime.UtcNow < deadline)
        {
            if (ManualAgent.HasManualAgent(sessionId, identity)) { detected = true; break; }
            Thread.Sleep(300);
        }
        Check(detected, "手动代理启动后能被识别（手动优先依据）");

        if (detected) Check(a1.HasExited == false, "此时第一个代理仍在运行");

        // ③ 重复启动必须被拦下（退出码 4）
        var a2 = Process.Start(new ProcessStartInfo(agentExe) { UseShellExecute = false, CreateNoWindow = true })!;
        bool exited = a2.WaitForExit(20000);
        Console.WriteLine("第二个代理 exited=" + exited + (exited ? " code=" + a2.ExitCode : " (仍在运行)"));
        Check(exited, "重复启动的手动代理会退出");
        if (exited) Check(a2.ExitCode == 4, "重复启动返回专用退出码 4");
        try { if (!a2.HasExited) a2.Kill(); } catch { }

        // ④ 第一个代理退出后标记必须释放
        try { if (!a1.HasExited) a1.Kill(); } catch { }
        try { a1.WaitForExit(8000); } catch { }
        Thread.Sleep(1500);
        bool after = ManualAgent.HasManualAgent(sessionId, identity);
        Console.WriteLine("代理退出后 HasManualAgent = " + after);
        Check(!after, "手动代理退出后标记自动释放（不会永久卡住自动投送）");

        // ⑤ 命名管道单实例
        string pn = AppIdentity.RelayPipePrefix + sessionId + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        using (var f1 = new NamedPipeServerStream(pn, PipeDirection.InOut, 1,
                   PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        {
            bool failed = false;
            try { using var f2 = new NamedPipeServerStream(pn, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous); }
            catch (IOException) { failed = true; }
            Check(failed, "同一管道名不能有两个服务器（天然互斥）");
        }

        try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "RESULT=OK 全部通过" : "RESULT=FAIL 失败 " + _fails + " 项");
        return _fails;
    }

    private static string? FindDistExe()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            string c = Path.Combine(d.FullName, "dist", "SoulLink.exe");
            if (File.Exists(c)) return c;
        }
        return null;
    }
}
