using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using SoulLink;
using SoulLink.Services;

/// <summary>
/// 手动代理回归：核心是"自动找到宿主管道"。
/// 代理在目标会话里启动，宿主管道名形如 SoulLink_Relay_&lt;会话号&gt;_&lt;8位hex&gt;。
/// 找不到或找错都会让手动方案失效，所以这里逐条验证匹配规则。
/// </summary>
public static class ManualAgentCheck
{
    private static int _fails;
    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _fails++;
    }

    public static int Main(string[] args)
    {
        int sessionId = Process.GetCurrentProcess().SessionId;
        Console.WriteLine("当前会话号 = " + sessionId);

        // ── ① 无候选时：必须返回 null 且给出可读原因，不能抛异常 ──
        string? found = ManualAgent.FindHostPipe(9999, out string detail);
        Console.WriteLine("会话 9999 => " + (found ?? "(null)") + " · " + detail);
        Check(found == null, "不存在的会话返回 null");
        Check(!string.IsNullOrWhiteSpace(detail), "不存在的会话给出原因：" + detail);

        Check(ManualAgent.FindHostPipe(0, out _) == null, "会话号 0 返回 null");
        Check(ManualAgent.FindHostPipe(-1, out _) == null, "负数会话号返回 null");

        // ── ② 真实建一条管道，验证能被发现 ──────────────────────
        string pipeName = AppIdentity.RelayPipePrefix + sessionId + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        Console.WriteLine("创建测试管道 = " + pipeName);
        using (var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                   PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        {
            var discover = ManualAgent.FindHostPipe(sessionId, out string d2);
            Console.WriteLine("发现结果 = " + (discover ?? "(null)") + " · " + d2);
            Check(discover == pipeName, "能发现本会话的宿主管道");
            Check(!string.IsNullOrWhiteSpace(d2) && d2.Contains(pipeName), "发现结果附带管道名说明");
        }

        // ── ③ 其他会话的管道不能被当成本会话的 ────────────────────
        string otherPipe = AppIdentity.RelayPipePrefix + (sessionId + 100) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        using (var other = new NamedPipeServerStream(otherPipe, PipeDirection.InOut, 1,
                   PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        {
            var d3 = ManualAgent.FindHostPipe(sessionId, out string d3detail);
            Check(d3 == null || !d3.Equals(otherPipe, StringComparison.OrdinalIgnoreCase),
                  "不会误连其他会话的管道");
            Console.WriteLine("其他会话管道存在时的发现结果 = " + (d3 ?? "(null)") + " · " + d3detail);
        }

        // ── ④ _Events 后缀不能被当成主通道 ──────────────────────
        string evName = AppIdentity.RelayPipePrefix + sessionId + "_" + Guid.NewGuid().ToString("N").Substring(0, 8)
                        + AppIdentity.RelayEventSuffix;
        using (var evPipe = new NamedPipeServerStream(evName, PipeDirection.Out, 1,
                   PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        {
            var d4 = ManualAgent.FindHostPipe(sessionId, out string d4detail);
            Check(d4 == null || !d4.EndsWith(AppIdentity.RelayEventSuffix, StringComparison.OrdinalIgnoreCase),
                  "不会把 _Events 事件管道当成主通道");
            Console.WriteLine("仅有事件管道时的发现结果 = " + (d4 ?? "(null)") + " · " + d4detail);
        }

        // ── ⑤ 非法后缀（非 8 位 hex）必须被拒绝 ───────────────────
        string badPipe = AppIdentity.RelayPipePrefix + sessionId + "_zzzz";
        using (var bad = new NamedPipeServerStream(badPipe, PipeDirection.InOut, 1,
                   PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        {
            var d5 = ManualAgent.FindHostPipe(sessionId, out _);
            Check(d5 == null, "拒绝非法后缀的管道（" + badPipe + "）");
        }

        // ── ⑥ 导出功能：应生成 asInvoker 的 SoulLink.Agent.exe ────
        string outDir = Path.Combine(Path.GetTempPath(), "soullink-agent-export-test");
        try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
        var (ok, msg) = ManualAgent.Export(outDir);
        Console.WriteLine("导出结果 = " + ok + " · " + msg);
        Check(ok, "能导出手动代理");
        string exported = Path.Combine(outDir, "SoulLink.Agent.exe");
        Check(File.Exists(exported), "导出的文件存在：" + exported);
        if (File.Exists(exported))
        {
            var bytes = File.ReadAllBytes(exported);
            Check(System.Text.Encoding.ASCII.GetString(bytes).Contains("level=\"asInvoker\""),
                  "导出的代理已改成 asInvoker（普通权限可启动）");
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "RESULT=OK 全部通过" : "RESULT=FAIL 失败 " + _fails + " 项");
        return _fails;
    }
}
