using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SoulLink;
using SoulLink.Services;

/// <summary>
/// 手动代理端到端：模拟"宿主已就绪、用户在目标会话里启动代理"的完整过程。
/// 宿主侧用 RelayClient 的管道命名规则建监听，再用导出的 SoulLink.Agent.exe 真实连回来。
/// </summary>
public static class ManualAgentLiveCheck
{
    private static int _fails;
    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _fails++;
    }

    public static async Task<int> Main()
    {
        int sessionId = Process.GetCurrentProcess().SessionId;
        Console.WriteLine("会话号 = " + sessionId);

        // ① 用宿主同样的命名规则建好两条管道（主通道 + 事件通道）
        string pipeName = AppIdentity.RelayPipePrefix + sessionId + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        Console.WriteLine("宿主管道 = " + pipeName);
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var eventServer = new NamedPipeServerStream(pipeName + "_Events", PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        // ② 准备代理副本。
        //    ★ 不能用 ManualAgent.Export：它取 ResolveSelfExe()，在测试进程里会拿到
        //      测试工具自己，导出来的"代理"根本不是 SoulLink。
        //      这里显式用 dist 里的主程序复制一份并改清单。
        string outDir = Path.Combine(Path.GetTempPath(), "soullink-agent-live");
        try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
        Directory.CreateDirectory(outDir);
        string source = FindDistExe();
        if (source == null) { Console.WriteLine("RESULT=FAIL 找不到 dist\\SoulLink.exe"); return 2; }
        string agentExe = Path.Combine(outDir, "SoulLink.Agent.exe");
        File.Copy(source, agentExe, true);
        Console.WriteLine("代理来源 = " + source);

        // ③ 启动代理（不带任何参数，走自动发现）
        Console.WriteLine("启动代理（无参数，自动发现管道）…");
        var psi = new ProcessStartInfo(agentExe) { UseShellExecute = false, CreateNoWindow = true };
        using var agent = Process.Start(psi);
        if (agent == null) { Console.WriteLine("RESULT=FAIL 代理启动失败"); return 2; }
        Console.WriteLine("代理 PID = " + agent.Id);

        // ④ 宿主侧接受连接
        // 代理启动后要枚举管道并连接，给它充足时间（默认等待宿主 60 秒）。
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        bool connected = false;
        try
        {
            await server.WaitForConnectionAsync(cts.Token);
            connected = true;
        }
        catch (OperationCanceledException) { }
        Check(connected, "代理自动发现管道并连回宿主（无需任何参数）");

        if (connected)
        {
            // ⑤ 收发一条指令，确认链路真的通
            var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, true);
            writer.WriteLine("{\"Cmd\":\"ping\"}");
            var readTask = reader.ReadLineAsync();
            var done = await Task.WhenAny(readTask, Task.Delay(6000));
            if (done == readTask)
            {
                string? line = await readTask;
                Console.WriteLine("ping 响应：" + (line ?? "(null)"));
                Check(line != null && line.Contains("pong"), "代理能正常响应 ping");
            }
            else Check(false, "代理响应 ping（超时）");
        }

        // ⑥ 清理
        try { if (!agent.HasExited) agent.Kill(); } catch { }
        try { await agent.WaitForExitAsync(new CancellationTokenSource(5000).Token); } catch { }
        try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "RESULT=OK 全部通过" : "RESULT=FAIL 失败 " + _fails + " 项");
        return _fails;
    }

    /// <summary>从当前目录向上找 dist\\SoulLink.exe。</summary>
    private static string? FindDistExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "dist", "SoulLink.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
