using System;
using System.Collections.Generic;

namespace SoulLink.Services
{
    /// <summary>
    /// 键盘注入的共享契约：把「键名 → 虚拟键码」和「Unicode 文本拆分/校验」这两块
    /// 纯逻辑从 RelayAgent 里抽出来，让 tools/layout-check 能离线单测，
    /// 而不必真的调用 SendInput（那只能在本会话内、对着真实桌面生效）。
    /// </summary>

    /// <summary>键名 → 虚拟键码。原 RelayAgent.ParseVk 原样迁出，映射表一字未改。</summary>
    public static class InputKeys
    {
        /// <summary>按键名 → 虚拟键码。解析失败返回 0。</summary>
        public static ushort ParseVk(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;
            string u = name.Trim().ToUpperInvariant();

            switch (u)
            {
                case "CTRL": case "CONTROL": return 0x11;
                case "ALT": return 0x12;
                case "SHIFT": return 0x10;
                case "WIN": return 0x5B;
                case "ENTER": case "RETURN": return 0x0D;
                case "ESC": case "ESCAPE": return 0x1B;
                case "SPACE": return 0x20;
                case "TAB": return 0x09;
                case "BACKSPACE": return 0x08;
                case "DELETE": case "DEL": return 0x2E;
                case "INSERT": case "INS": return 0x2D;
                case "HOME": return 0x24;
                case "END": return 0x23;
                case "PAGEUP": return 0x21;
                case "PAGEDOWN": return 0x22;
                case "UP": return 0x26;
                case "DOWN": return 0x28;
                case "LEFT": return 0x25;
                case "RIGHT": return 0x27;

                // 符号键（与英文键盘布局一致的 OemKey 虚拟键码）。
                case "-": return 0xBD;   // VK_OEM_MINUS
                case "=": return 0xBB;   // VK_OEM_PLUS
                case ",": return 0xBC;   // VK_OEM_COMMA
                case ".": return 0xBE;   // VK_OEM_PERIOD
                case "/": return 0xBF;   // VK_OEM_2
                case ";": return 0xBA;   // VK_OEM_1
                case "'": return 0xDE;   // VK_OEM_7
                case "[": return 0xDB;   // VK_OEM_4
                case "]": return 0xDD;   // VK_OEM_6
                case "\\": return 0xDC;  // VK_OEM_5
                case "`": return 0xC0;   // VK_OEM_3
            }

            if (u.Length >= 2 && u[0] == 'F' && int.TryParse(u.Substring(1), out int fn)
                && fn >= 1 && fn <= 24)
                return (ushort)(0x6F + fn);

            if (u.Length == 1 && u[0] >= '0' && u[0] <= '9') return (ushort)u[0];
            if (u.Length == 1 && u[0] >= 'A' && u[0] <= 'Z') return (ushort)u[0];

            if (u.StartsWith("NUMPAD") && int.TryParse(u.Substring(6), out int np)
                && np >= 0 && np <= 9)
                return (ushort)(0x60 + np);

            return 0;
        }
    }

    /// <summary>Unicode 文本注入的纯逻辑（拆分 / 校验），与 SendInput 解耦。</summary>
    public static class UnicodeInput
    {
        /// <summary>单次 text 动作允许的最大 UTF-16 码元数（string.Length 口径）。</summary>
        public const int MaxLength = 1000;

        /// <summary>
        /// 校验 text 动作的输入。不通过时返回 false 并给出面向调用者的错误文案。
        /// </summary>
        public static bool TryValidate(string text, out string error)
        {
            error = "";
            if (string.IsNullOrEmpty(text)) { error = "缺少文本"; return false; }
            if (text.Length > MaxLength) { error = "文本过长（上限 " + MaxLength + " 字符）"; return false; }
            return true;
        }

        /// <summary>
        /// 把字符串拆成「Unicode 标量值」序列：每个元素是 1 个或 2 个 UTF-16 码元。
        ///
        /// ★ 代理对（surrogate pair，如 emoji）必须成对保留，绝不能拆成两个孤立码元：
        ///   单独发送高位/低位代理的 KEYEVENTF_UNICODE 会打出乱码或替换符。
        /// </summary>
        public static IReadOnlyList<string> SplitUtf16(string text)
        {
            var list = new List<string>(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    list.Add(text.Substring(i, 2));
                    i++; // 跳过低位代理
                }
                else
                {
                    list.Add(c.ToString());
                }
            }
            return list;
        }
    }
}
