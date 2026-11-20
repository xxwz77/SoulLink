using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using SoulLink;
using SoulLink.Controls;
using SoulLink.Services;

/// <summary>
/// 热键准入回归：判据必须基于"RDP 画面控件是否有焦点"，
/// 而不是"整个窗口是否处于激活状态"。
/// 后者会让用户在主窗口其它区域按键时被静默丢弃（实测 2026-11-19）。
/// </summary>
public static class HotkeyFocusCheck
{
    private static int _fails;
    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _fails++;
    }

    public static int Main()
    {
        // 反射检查源码级契约：CanUseToggleHotkey 不得再依赖 _ownerWindow.IsActive
        string source = FindSource("Controls", "RdpConsole.cs");
        if (source == null) { Console.WriteLine("FAIL 找不到 RdpConsole.cs"); return 2; }

        var type = typeof(RdpConsole);
        var prop = type.GetProperty("CanUseToggleHotkey");
        Check(prop != null, "存在 CanUseToggleHotkey 属性");
        Check(type.GetProperty("RdpSurfaceHasFocus") != null, "存在 RdpSurfaceHasFocus（按控件焦点判断）");

        // 关键：CanUseToggleHotkey 的 getter 内部不能再出现 _ownerWindow.IsActive
        var getter = prop!.GetGetMethod(true);
        Check(getter != null, "CanUseToggleHotkey 可读");

        // 用源码确认判据已切换（IL 检查成本高，源码契约足够）
        int iface = source.IndexOf("public bool CanUseToggleHotkey", StringComparison.Ordinal);
        Check(iface > 0, "源码中找到 CanUseToggleHotkey 定义");
        if (iface > 0)
        {
            // 取该属性到下一个成员声明之间的片段
            int end = source.IndexOf("public bool RdpSurfaceHasFocus", iface, StringComparison.Ordinal);
            Check(end > iface, "源码中找到 RdpSurfaceHasFocus 定义");
            string block = end > iface ? source.Substring(iface, end - iface) : source.Substring(iface, Math.Min(2200, source.Length - iface));

            Check(!block.Contains("_ownerWindow.IsActive"),
                  "CanUseToggleHotkey 不再依赖 _ownerWindow.IsActive（原来的误判来源）");
            Check(block.Contains("RdpSurfaceHasFocus"),
                  "CanUseToggleHotkey 改为依赖 RdpSurfaceHasFocus");
            Check(block.Contains("_captured"), "退出自由视角仍然无条件放行");
        }

        // 焦点判断要有 Win32 兜底（AxHost.ContainsFocus 对 RDP 不可靠）
        int focusIdx = source.IndexOf("public bool RdpSurfaceHasFocus", StringComparison.Ordinal);
        if (focusIdx > 0)
        {
            string focusBlock = source.Substring(focusIdx, Math.Min(1400, source.Length - focusIdx));
            Check(focusBlock.Contains("IsChild"), "焦点判断含 IsChild 兜底（子窗口焦点）");
            Check(focusBlock.Contains("GetFocus"), "焦点判断使用 Win32 GetFocus");
        }

        // 丢弃日志必须带原因，避免以后再靠猜
        Check(source.Contains("DescribeHotkeyBlock"), "丢弃日志会写明具体原因");

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "RESULT=OK 全部通过" : "RESULT=FAIL 失败 " + _fails + " 项");
        return _fails;
    }

    private static string? FindSource(params string[] parts)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            string p = Path.Combine(new[] { d.FullName }.Concat(parts).ToArray());
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        return null;
    }
}
