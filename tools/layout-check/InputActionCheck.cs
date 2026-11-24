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

        // ── 4. FIX-2026-11-24 KEYBOARD-TOGGLE-SYNC：扩展键判定 ──
        // 依据：Set1 扫描码里方向键与小键盘数字**共用同一扫描码**，只能靠 E0 扩展位区分。
        // 旧注入只给 wVk（wScan=0、无 EXTENDEDKEY），目标程序收到的 lParam 扫描码恒 0、
        // 扩展位恒 0 —— 已用低级键盘钩子 + 窗口消息双路实测坐实（见 FIX 报告）。
        Check(InputKeys.IsExtendedVk(0x25), "VK_LEFT 是扩展键");
        Check(InputKeys.IsExtendedVk(0x26), "VK_UP 是扩展键");
        Check(InputKeys.IsExtendedVk(0x27), "VK_RIGHT 是扩展键");
        Check(InputKeys.IsExtendedVk(0x28), "VK_DOWN 是扩展键");
        Check(!InputKeys.IsExtendedVk(0x64), "VK_NUMPAD4 不是扩展键（与 LEFT 同码 0x4B）");
        Check(!InputKeys.IsExtendedVk(0x68), "VK_NUMPAD8 不是扩展键（与 UP 同码 0x48）");
        Check(InputKeys.IsExtendedVk(0x24) && !InputKeys.IsExtendedVk(0x67), "HOME 扩展 / NUMPAD7 不扩展（同码 0x47）");
        Check(InputKeys.IsExtendedVk(0x2D) && !InputKeys.IsExtendedVk(0x60), "INSERT 扩展 / NUMPAD0 不扩展（同码 0x52）");
        Check(InputKeys.IsExtendedVk(0xA3) && !InputKeys.IsExtendedVk(0xA2), "右 Ctrl 扩展 / 左 Ctrl 不扩展");
        Check(InputKeys.IsExtendedVk(0x6F) && !InputKeys.IsExtendedVk(0x6A), "小键盘 / 扩展 / 小键盘 * 不扩展");
        Check(!InputKeys.IsExtendedVk(0x41), "普通字母不扩展（不误伤普通键）");

        // ── 5. 新增键名（小键盘运算符 / toggle）原本全部解析失败 ──
        Check(InputKeys.ParseVk("NumpadDivide") == 0x6F, "ParseVk(NumpadDivide)=0x6F");
        Check(InputKeys.ParseVk("Numpad*") == 0x6A, "ParseVk(Numpad*)=0x6A");
        Check(InputKeys.ParseVk("Numpad-") == 0x6D, "ParseVk(Numpad-)=0x6D");
        Check(InputKeys.ParseVk("Numpad.") == 0x6E, "ParseVk(Numpad.)=0x6E");
        Check(InputKeys.ParseVk("CapsLock") == 0x14, "ParseVk(CapsLock)=0x14");
        Check(InputKeys.ParseVk("NumLock") == 0x90, "ParseVk(NumLock)=0x90");
        Check(InputKeys.ParseVk("ScrollLock") == 0x91, "ParseVk(ScrollLock)=0x91");

        // ── 6. ToggleKeys：只在目标≠当前时才按（防「按一下翻反向」） ──
        var cur = new SoulLink.Services.ToggleKeys.State(false, true, false);
        Check(SoulLink.Services.ToggleKeys.Diff(cur, new SoulLink.Services.ToggleKeys.State(false, true, false)).Count == 0,
            "状态一致 → 0 次按键");
        var d1 = SoulLink.Services.ToggleKeys.Diff(cur, new SoulLink.Services.ToggleKeys.State(true, true, false));
        Check(d1.Count == 1 && d1[0] == SoulLink.Services.ToggleKeys.VkCapsLock, "只差 Caps → 只按 CapsLock");
        var d2 = SoulLink.Services.ToggleKeys.Diff(cur, new SoulLink.Services.ToggleKeys.State(true, false, true));
        Check(d2.Count == 3 && d2[0] == SoulLink.Services.ToggleKeys.VkCapsLock
            && d2[1] == SoulLink.Services.ToggleKeys.VkNumLock && d2[2] == SoulLink.Services.ToggleKeys.VkScrollLock,
            "三个都差 → 顺序固定 Caps/Num/Scroll");
        Check(SoulLink.Services.ToggleKeys.ToMask(new SoulLink.Services.ToggleKeys.State(true, false, true)) == 5, "掩码 Caps+Scroll=5");
        Check(SoulLink.Services.ToggleKeys.FromMask(5) is { Caps: true, Num: false, Scroll: true }, "掩码 5 反解正确");
        SoulLink.Services.ToggleKeys.State parsed;
        Check(SoulLink.Services.ToggleKeys.TryParse("caps=1,num=0,scroll=1", out parsed) && parsed.Caps && !parsed.Num && parsed.Scroll,
            "TryParse 合法串");
        Check(!SoulLink.Services.ToggleKeys.TryParse("caps=1,num=0", out parsed), "TryParse 缺字段 → false（不猜）");
        Check(!SoulLink.Services.ToggleKeys.TryParse("caps=maybe,num=0,scroll=0", out parsed), "TryParse 非法值 → false");

        Console.WriteLine(fails == 0 ? "ALL PASSED" : "FAILURES=" + fails);
        return fails;
    }
}
