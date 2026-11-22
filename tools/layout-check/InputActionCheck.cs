using System;
using System.Collections.Generic;
using SoulLink.Services;

/// <summary>
/// 回归：/api/input 新增四动作的纯逻辑部分。
/// 覆盖：text 代理对拆分、长度上限、keydown/keyup 的键码映射（不改变原 key 行为）。
/// 真实 SendInput 到游戏的时序无法在离线单测里验证（见主报告）。
/// </summary>
public static class InputActionCheck
{
    static int fails;
    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
        if (!ok) fails++;
    }

    public static int Main()
    {
        // ── 1. UnicodeInput 代理对拆分 ──
        // "A中😀B" = A(1) + 中(1) + 😀(代理对=2) + B(1)，共 4 个标量值。
        var units = UnicodeInput.SplitUtf16("A中😀B");
        Check(units.Count == 4, "代理对拆分：A中😀B → 4 个标量值");
        Check(units[0] == "A", "第 1 个标量 = A");
        Check(units[1] == "中", "第 2 个标量 = 中");
        Check(units[2] == "😀", "第 3 个标量 = 😀（代理对成对保留）");
        Check(units[3] == "B", "第 4 个标量 = B");

        // 纯 ASCII / 纯 BMP 不产生代理对
        var ascii = UnicodeInput.SplitUtf16("abc");
        Check(ascii.Count == 3 && ascii[0] == "a" && ascii[1] == "b" && ascii[2] == "c",
            "纯 ASCII 逐字符拆分");

        // 每个标量值的 UTF-16 长度：普通字符 1，代理对 2
        Check(units[2].Length == 2, "emoji 标量值占 2 个 UTF-16 码元（成对，不拆开）");
        Check(units[0].Length == 1, "普通字符标量值占 1 个码元");

        // ── 2. UnicodeInput 长度上限 ──
        string err;
        Check(!UnicodeInput.TryValidate(null, out err) && err.Length > 0, "null 文本被拒绝");
        Check(!UnicodeInput.TryValidate("", out err) && err.Length > 0, "空文本被拒绝");

        string okText = new string('a', UnicodeInput.MaxLength);
        Check(UnicodeInput.TryValidate(okText, out err), "恰好在 1000 上限内 → 放行");

        string overText = new string('a', UnicodeInput.MaxLength + 1);
        Check(!UnicodeInput.TryValidate(overText, out err) && err.Contains("过长"), "超出 1000 上限 → 拒绝");

        // ── 3. InputKeys.ParseVk 与 keydown/keyup 共用同一映射（key 行为不变） ──
        Check(InputKeys.ParseVk("W") == (ushort)'W', "ParseVk(W) = 0x57（keydown 按下 W 走同一键码）");
        Check(InputKeys.ParseVk("A") == (ushort)'A', "ParseVk(A) = 0x41");
        Check(InputKeys.ParseVk("CTRL") == 0x11, "ParseVk(CTRL) = 0x11");
        Check(InputKeys.ParseVk("ctrl") == 0x11, "ParseVk 大小写不敏感（ctrl）");
        Check(InputKeys.ParseVk("Space") == 0x20, "ParseVk(Space) = 0x20");
        Check(InputKeys.ParseVk("F12") == 0x7B, "ParseVk(F12) = 0x7B");
        Check(InputKeys.ParseVk("Numpad7") == 0x67, "ParseVk(Numpad7) = 0x67");
        Check(InputKeys.ParseVk("W+D") == 0, "组合串 'W+D' 整体解析失败（应由调用方拆分，与 key 一致）");
        Check(InputKeys.ParseVk("不认识的键") == 0, "未知键名 → 0（调用方报错，与 key 一致）");
        Check(InputKeys.ParseVk("") == 0, "空键名 → 0");
        Check(InputKeys.ParseVk("  shift  ") == 0x10, "键名带空格会被 Trim");

        // 符号键映射保持与历史一致（key 动作依赖这些键码，绝不能漂移）
        Check(InputKeys.ParseVk("-") == 0xBD, "ParseVk('-') = 0xBD (VK_OEM_MINUS)");
        Check(InputKeys.ParseVk(".") == 0xBE, "ParseVk('.') = 0xBE (VK_OEM_PERIOD)");
        Check(InputKeys.ParseVk("/") == 0xBF, "ParseVk('/') = 0xBF (VK_OEM_2)");

        Console.WriteLine(fails == 0 ? "ALL PASSED" : "FAILURES=" + fails);
        return fails;
    }
}
