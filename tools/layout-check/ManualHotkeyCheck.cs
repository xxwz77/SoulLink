using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SoulLink;
using SoulLink.Models;
using SoulLink.Services;

/// <summary>
/// 手动代理挂接回归：
/// 接入后必须能收到热键配置、并能把按键事件送回宿主。
/// 这是"显示接入成功但侧键没反应"的直接成因。
/// </summary>
public static class ManualHotkeyCheck
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

        // 建立等待
        bool started = await ManualAgentMode.StartWaitingAsync(sessionId, Environment.UserName);
        Check(started, "建立等待管道");

        // 启动代理
        string outDir = Path.Combine(Path.GetTempPath(), "soullink-hotkey-test");
        try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
        Directory.CreateDirectory(outDir);
        string? src = FindDistExe();
        if (src == null) { Console.WriteLine("FAIL 找不到 dist"); return 2; }
        string agentExe = Path.Combine(outDir, "SoulLink.Agent.exe");
        File.Copy(src, agentExe, true);

        var agent = Process.Start(new ProcessStartInfo(agentExe) { UseShellExecute = false, CreateNoWindow = true })!;
        Console.WriteLine("代理 pid = " + agent.Id);

        // 等接入
        RelayClient? relay = null;
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            relay = RelayClient.FindBySession(sessionId);
            if (relay != null && relay.IsConnected) break;
            await Task.Delay(400);
        }
        Check(relay != null && relay.IsConnected, "代理接入");
        if (relay == null) { Cleanup(agent, outDir, original); return 1; }

        // ★ 核心断言：接入后必须能下发热键配置
        var resp = await relay.ConfigureHotkeyAsync("鼠标侧键4", trace: false);
        Console.WriteLine("热键配置响应 = " + (resp?.Message ?? "(null)") + " ok=" + (resp?.Ok));
        Check(resp != null, "代理能响应热键配置指令");
        Check(resp?.Ok == true, "热键配置被代理接受（安装成功）");

        // 验证热键事件管道：订阅后应能收到代理侧的按键（这里只能验证管道活着）
        bool hotkeyFired = false;
        relay.HotkeyPressed += (_, key) => { hotkeyFired = true; Console.WriteLine("收到热键事件：" + key); };

        // 等一小会儿看代理日志是否出现"安装=True"
        await Task.Delay(1500);
        Check(!hotkeyFired, "（无按键时不误触发）");

        // 检查代理侧日志确认安装
        string logPath = Path.Combine(Path.GetTempPath(), "soullink-relay-" + agent.Id + ".log");
        string logText = File.Exists(logPath) ? File.ReadAllText(logPath) : "";
        Console.WriteLine("代理日志：\n" + logText);
        Check(logText.Contains("热键接收线程启动"), "代理热键线程已启动");
        Check(logText.Contains("热键安装=True") || logText.Contains("安装=True"),
              "代理已安装鼠标热键（侧键可用）");

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
