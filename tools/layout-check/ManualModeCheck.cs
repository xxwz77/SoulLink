using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SoulLink;
using SoulLink.Models;
using SoulLink.Services;

/// <summary>
/// 手动代理模式回归：
///   开关关闭时行为不变；打开后不自动投送、只准备管道；
///   用户启动代理后能接入；关闭后恢复。
/// </summary>
public static class ManualModeCheck
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
        Console.WriteLine("会话号 = " + sessionId + " · 身份 = " + AppIdentity.Name);

        bool original = App.Config.Settings.ManualAgentMode;
        Console.WriteLine("初始开关 = " + original);

        // ① 关闭状态下不等待
        App.Config.Settings.ManualAgentMode = false;
        Check(!ManualAgentMode.Enabled, "关闭时 Enabled=false");
        Check(!ManualAgentMode.IsWaiting(sessionId), "关闭时没有等待中的会话");

        // ② 打开开关（不落盘，直接改内存态测试行为）
        App.Config.Settings.ManualAgentMode = true;
        Check(ManualAgentMode.Enabled, "打开时 Enabled=true");

        // ③ 为当前会话建立等待
        bool started = await ManualAgentMode.StartWaitingAsync(sessionId, Environment.UserName);
        Check(started, "能为会话建立等待管道");
        Check(ManualAgentMode.IsWaiting(sessionId), "会话出现在等待列表");
        Console.WriteLine("等待中的会话 = " + string.Join(",", ManualAgentMode.WaitingSessions()));

        // ④ 重复调用不应重复建立
        bool again = await ManualAgentMode.StartWaitingAsync(sessionId, Environment.UserName);
        Check(!again, "重复建立被拒绝（不会重复占管道）");

        // ⑤ 真实启动代理，验证能接入
        string outDir = Path.Combine(Path.GetTempPath(), "soullink-mode-test");
        try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
        Directory.CreateDirectory(outDir);
        string? src = FindDistExe();
        if (src == null) { Console.WriteLine("FAIL 找不到 dist"); return 2; }
        string agentExe = Path.Combine(outDir, "SoulLink.Agent.exe");
        File.Copy(src, agentExe, true);

        var agent = Process.Start(new ProcessStartInfo(agentExe) { UseShellExecute = false, CreateNoWindow = true })!;
        Console.WriteLine("手动代理 pid = " + agent.Id);

        var deadline = DateTime.UtcNow.AddSeconds(25);
        bool connected = false;
        while (DateTime.UtcNow < deadline)
        {
            var live = RelayClient.FindBySession(sessionId);
            if (live != null && live.IsConnected) { connected = true; break; }
            await Task.Delay(400);
        }
        Check(connected, "用户启动代理后自动接入（无需任何参数）");

        if (connected)
        {
            var relay = RelayClient.FindBySession(sessionId)!;
            Console.WriteLine("接入信息 = " + relay.AgentInfo + " · 屏幕 " + relay.ScreenWidth + "x" + relay.ScreenHeight);
            var pong = await relay.SendAsync(new { Cmd = "ping" }, 5000);
            Check(pong != null && pong.Ok, "接入后握手正常");
        }

        // ⑥ 关闭开关应停止等待并释放通道
        App.Config.Settings.ManualAgentMode = false;
        await ManualAgentMode.StopAllAsync();
        Check(!ManualAgentMode.IsWaiting(sessionId), "关闭后等待列表已清空");
        await Task.Delay(800);
        Check(RelayClient.FindBySession(sessionId) == null, "关闭后通道已注销");

        // 恢复
        try { if (!agent.HasExited) agent.Kill(); } catch { }
        try { agent.WaitForExit(5000); } catch { }
        try { Directory.Delete(outDir, true); } catch { }
        App.Config.Settings.ManualAgentMode = original;

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
