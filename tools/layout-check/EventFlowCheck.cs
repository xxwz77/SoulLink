using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SoulLink;
using SoulLink.Services;

/// <summary>
/// 事件管道单向验证：模拟代理往 _Events 管道写一条热键，
/// 确认宿主侧 RelayClient 的读取循环真的会把它派发出来。
/// 不依赖真实鼠标，纯协议层。
/// </summary>
public static class EventFlowCheck
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

        // 用真实 RelayClient 建等待（它会启动主通道 + 事件通道两个服务器）
        var relay = new RelayClient(Environment.UserName, sessionId);
        Console.WriteLine("管道 = " + relay.PipeName);

        var listenTask = relay.ListenAsync(20000);

        // 等两条服务器起来
        await Task.Delay(1200);

        int fired = 0;
        string last = "";
        relay.HotkeyPressed += (_, key) => { fired++; last = key; Console.WriteLine("  >>> 宿主收到：" + key); };

        // 模拟代理：连主通道
        using var main = new NamedPipeClientStream(".", relay.PipeName, PipeDirection.InOut);
        try { main.Connect(5000); Console.WriteLine("主通道已连接"); }
        catch (Exception ex) { Console.WriteLine("FAIL 主通道连接失败：" + ex.Message); return 2; }

        // 模拟代理：连事件通道
        using var evt = new NamedPipeClientStream(".", relay.PipeName + "_Events", PipeDirection.Out);
        try { evt.Connect(5000); Console.WriteLine("事件通道已连接"); }
        catch (Exception ex) { Console.WriteLine("FAIL 事件通道连接失败：" + ex.Message); return 2; }

        var listenDone = await Task.WhenAny(listenTask, Task.Delay(8000)) == listenTask;
        Console.WriteLine("ListenAsync 完成 = " + listenDone);

        // 等读者就位
        await Task.Delay(1200);

        // 写一条热键事件
        var writer = new StreamWriter(evt, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        writer.WriteLine("{\"Event\":\"hotkey\",\"Value\":\"toggle\"}");
        Console.WriteLine("已写入一条热键事件");

        var dl = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < dl && fired == 0) await Task.Delay(200);

        Check(fired > 0, "宿主收到模拟代理的热键事件（收到 " + fired + " 条，内容 " + last + "）");

        try { relay.Dispose(); } catch { }
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "RESULT=OK 全部通过" : "RESULT=FAIL 失败 " + _fails + " 项");
        return _fails;
    }
}
