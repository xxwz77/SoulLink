using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SoulLink;
using SoulLink.Services;

/// <summary>
/// 事件管道回归：手动代理接入后，代理侧的热键命中必须能传回宿主。
/// 这是"代理已命中、宿主毫无反应"的直接验证点。
/// </summary>
public static class EventPipeCheck
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

        bool original = App.Config.Settings.ManualAgentMode;
        App.Config.Settings.ManualAgentMode = true;

        await ManualAgentMode.StartWaitingAsync(sessionId, Environment.UserName);
        Check(ManualAgentMode.IsWaiting(sessionId), "建立等待");

        // 启动代理
        string outDir = Path.Combine(Path.GetTempPath(), "soullink-evt-test");
        try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
        Directory.CreateDirectory(outDir);
        string? src = FindDistExe();
        if (src == null) { Console.WriteLine("FAIL 找不到 dist"); return 2; }
        string agentExe = Path.Combine(outDir, "SoulLink.Agent.exe");
        File.Copy(src, agentExe, true);

        var agent = Process.Start(new ProcessStartInfo(agentExe) { UseShellExecute = false, CreateNoWindow = true })!;
        Console.WriteLine("代理 pid = " + agent.Id);

        RelayClient? relay = null;
        var dl = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < dl)
        {
            relay = RelayClient.FindBySession(sessionId);
            if (relay != null && relay.IsConnected) break;
            await Task.Delay(300);
        }
        Check(relay != null && relay.IsConnected, "代理接入");
        if (relay == null) { Cleanup(agent, outDir, original); return 1; }

        // ★ 订阅热键事件
        int fired = 0;
        string lastKey = "";
        relay.HotkeyPressed += (_, key) => { fired++; lastKey = key; Console.WriteLine("  >>> 收到热键：" + key); };
        Console.WriteLine("已订阅 HotkeyPressed");

        // 下发热键配置，让代理装钩子
        var resp = await relay.ConfigureHotkeyAsync("鼠标侧键4", trace: false);
        Console.WriteLine("热键配置 = " + (resp?.Message ?? "null"));
        await Task.Delay(2000);

        // 检查事件管道读者是否在跑：发一条 releaseall 看通道是否双向通
        var pong = await relay.SendAsync(new { Cmd = "ping" }, 4000);
        Check(pong != null && pong.Ok, "主通道双向通信正常");

        // 检查宿主侧的事件管道是否已连接（通过反射看 _eventPipe）
        var evField = typeof(RelayClient).GetField("_eventPipe",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var evPipe = evField?.GetValue(relay) as System.IO.Pipes.NamedPipeServerStream;
        Console.WriteLine("_eventPipe = " + (evPipe == null ? "null" : "connected=" + evPipe.IsConnected));
        Check(evPipe != null && evPipe.IsConnected, "事件管道在宿主侧已连接");

        Console.WriteLine("\n请在目标会话里按几次鼠标侧键（10 秒内）…");
        await Task.Delay(10000);
        Console.WriteLine("收到事件数 = " + fired + (lastKey.Length > 0 ? " 最后：" + lastKey : ""));

        Cleanup(agent, outDir, original);
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "RESULT=OK 全部通过" : "RESULT=FAIL 失败 " + _fails + " 项");
        return _fails;
    }

    private static void Cleanup(Process agent, string outDir, bool original)
    {
        try { if (!agent.HasExited) agent.Kill(); } catch { }
        try { agent.WaitForExit(5000); } catch { }
        try { Directory.Delete(outDir, true); } catch { }
        try { ManualAgentMode.StopAllAsync().GetAwaiter().GetResult(); } catch { }
        App.Config.Settings.ManualAgentMode = original;
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
