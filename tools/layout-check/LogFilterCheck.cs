using System;
using System.Linq;
using System.Reflection;
using SoulLink;
using SoulLink.Services;

/// <summary>
/// 日志过滤器回归：热键链路的每条判定都必须落盘，
/// 否则"按侧键没反应"只能靠重启带 --verbose-log 才能查（曾经因此误判过排查方向）。
///
/// 同时验证 --verbose-log 不再被当成业务选项导致进程退出。
/// </summary>
public static class LogFilterCheck
{
    private static int _fails;
    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _fails++;
    }

    public static int Main()
    {
        // IsImportant 是 RdpConsole 的私有静态方法，用反射调用（不为此改可见性）。
        var type = typeof(SoulLink.Controls.RdpConsole);
        var method = type.GetMethod("IsImportant", BindingFlags.NonPublic | BindingFlags.Static)
                     ?? type.GetMethod("IsImportant", BindingFlags.Static);
        if (method == null) { Console.WriteLine("FAIL 找不到 IsImportant"); return 2; }
        bool Important(string m) => (bool)method.Invoke(null, new object[] { m })!;

        // ── ① 热键链路：这些曾经全部被丢弃 ──────────────────────
        var mustKeep = new[]
        {
            "收到目标会话热键事件 · UI线程=True connected=True input=True captured=False",
            "丢弃非当前控制台热键事件（非锁定态）",
            "忽略目标会话热键回声（本机刚处理过）",
            "执行热键切换 · guard=0 UI线程=True",
            "热键触发: target=FreeLook IsConnected=True _captured=False",
            "热键切换结果 ok=True message=已进入自由视角 captured=True mode=FreeLook",
            "自由视角中，本机热键鼠标退出：鼠标侧键4",
            "目标热键配置(代理就绪)=配置已排队 · 鼠标侧键4",
            "输入标志为 false，当场重新探测一次",
            "重新探测结果 usable=True · 可交互",
        };
        foreach (var m in mustKeep) Check(Important(m), "落盘：" + m);

        // ── ② 原有异常/跃迁必须继续保留 ──────────────────────────
        var stillKeep = new[]
        {
            "注入代理投送失败：通道未连接",
            "进入自由视角（手动切换）中心=1337,768 焦点=True",
            "连接成功；通道交由会话监控统一投送（会话 6）",
            "通道投送成功 山客",
        };
        foreach (var m in stillKeep) Check(Important(m), "保留原有：" + m);

        // ── ③ 高频噪声必须继续丢弃（避免日志爆炸）─────────────────
        var mustDrop = new[]
        {
            "RDP 宿主窗口移回 (628,305)",
            "键盘焦点 -> RDP 控件 (ax.ContainsFocus=True hwnd=919664)",
            "全局鼠标钩子已安装",
            "会话监控 tick#7 完成",
        };
        foreach (var m in mustDrop) Check(!Important(m), "仍然丢弃噪声：" + m);

        // ── ④ --verbose-log 必须被当作纯开关，不进业务参数 ────────
        var parsed = CommandLine.Parse(new[] { "--verbose-log" });
        Check(parsed.Verb.Length == 0, "--verbose-log 不产生动词");
        var req = CommandLine.ToRequest(parsed);
        // Kind 是非空枚举且默认 Launch，不能用来判断"有无动作"。
        // 真正的判据：纯开关不该带出动作名、目标程序或账号。
        Check(string.IsNullOrEmpty(req.Action), "--verbose-log 不携带动作名");
        Check(string.IsNullOrEmpty(req.Process), "--verbose-log 不携带目标程序");
        Check(string.IsNullOrEmpty(req.Account), "--verbose-log 不携带账号");

        var withPage = CommandLine.Parse(new[] { "--verbose-log", "--page", "console" });
        Check(withPage.Verb.Length == 0 && withPage.Options.ContainsKey("page"),
              "--verbose-log 与 --page 可共存");
        Check(withPage.Options.ContainsKey("verbose-log"), "--verbose-log 仍作为选项可见");

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "RESULT=OK 全部通过" : "RESULT=FAIL 失败 " + _fails + " 项");
        return _fails;
    }
}
