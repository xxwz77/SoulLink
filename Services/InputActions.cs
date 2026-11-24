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

                // ★★ 2026-11-24（FIX-KEYBOARD-TOGGLE-SYNC）：补小键盘运算符 + toggle 键名。
                //   原来只有 Numpad0-9，小键盘的 * + - . / 全部解析失败 → 报「无法解析按键名」。
                //   数值全部按 WinUser.h 的 VK_NUMPAD* / VK_MULTIPLY… 常量，不做换算。
                case "NUMPADMULTIPLY": case "NUMPAD*": return 0x6A;   // VK_MULTIPLY
                case "NUMPADADD":      case "NUMPAD+": return 0x6B;   // VK_ADD
                case "NUMPADSUBTRACT": case "NUMPAD-": return 0x6D;   // VK_SUBTRACT
                case "NUMPADDECIMAL":  case "NUMPAD.": return 0x6E;   // VK_DECIMAL
                case "NUMPADDIVIDE":   case "NUMPAD/": return 0x6F;   // VK_DIVIDE
                case "NUMPADENTER": return 0x0D;                      // 小键盘回车与主回车同 VK
                case "CAPSLOCK":   case "CAPS": return 0x14;          // VK_CAPITAL
                case "NUMLOCK":    case "NUM":  return 0x90;          // VK_NUMLOCK
                case "SCROLLLOCK": case "SCROLL": return 0x91;        // VK_SCROLL
                case "PRINTSCREEN": case "PRTSC": return 0x2C;        // VK_SNAPSHOT
                case "PAUSE": return 0x13;                            // VK_PAUSE
                case "APPS": case "MENU": return 0x5D;                // VK_APPS
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

        /// <summary>
        /// 该虚拟键在 Set1 扫描码表里是否属于**扩展键**（E0 前缀）。
        ///
        /// ★★ 2026-11-24（FIX-KEYBOARD-TOGGLE-SYNC）新增。为什么必须有：
        ///   Set1 扫描码里**大量键共用同一扫描码**，只靠 E0 扩展位区分：
        ///     · 方向键 ←↑→↓ 与 小键盘 4/8/6/2 同为 4B/48/4D/50
        ///     · Home/End/PgUp/PgDn 与 小键盘 7/1/9/3 同为 47/4F/49/51
        ///     · Insert/Delete 与 小键盘 0/. 同为 52/53
        ///     · 右 Ctrl 与 左 Ctrl 同为 1D；右 Alt 与 左 Alt 同为 38
        ///     · 小键盘 / 与主键盘 / 同为 35
        ///   注入时如果只给 wVk 不给 wScan/扩展位（旧实现），目标程序拿到的
        ///   lParam 里 **扫描码恒为 0、扩展位恒为 0**（本会话用低级键盘钩子 +
        ///   窗口消息双路实测坐实，见 FIX 报告 §复现），于是所有依赖「扫描码 +
        ///   扩展位」判键的程序（DirectInput/Raw Input 游戏、部分终端、某些
        ///   IME）都会把方向键当成小键盘数字、把右 Ctrl 当成左 Ctrl。
        ///
        /// ★ 本方法只做**查表**，不调用任何 Win32 API，因此可离线单测。
        ///   调用方（RelayAgent）负责把扩展位与 wScan 一起塞进 KEYBDINPUT。
        /// </summary>
        public static bool IsExtendedVk(ushort vk)
        {
            switch (vk)
            {
                // 编辑键簇（E0 4x / 5x）
                case 0x21:   // VK_PRIOR  (PageUp)
                case 0x22:   // VK_NEXT   (PageDown)
                case 0x23:   // VK_END
                case 0x24:   // VK_HOME
                case 0x25:   // VK_LEFT
                case 0x26:   // VK_UP
                case 0x27:   // VK_RIGHT
                case 0x28:   // VK_DOWN
                case 0x2D:   // VK_INSERT
                case 0x2E:   // VK_DELETE
                // 右侧修饰键（E0 1D / E0 38）
                case 0xA3:   // VK_RCONTROL
                case 0xA5:   // VK_RMENU (右 Alt)
                // 小键盘运算符（E0 35 / E0 1C）
                case 0x6F:   // VK_DIVIDE
                // Win 键（E0 5B / E0 5C）
                case 0x5B:   // VK_LWIN
                case 0x5C:   // VK_RWIN
                // 应用键菜单（E0 5D）
                case 0x5D:   // VK_APPS
                // 截屏 / 暂停（E0 2A / E0 1D 45）
                case 0x2C:   // VK_SNAPSHOT
                case 0x13:   // VK_PAUSE
                // NumLock（E0 45）—— 它的小键盘归属由扩展位表达
                case 0x90:   // VK_NUMLOCK
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// 键盘 toggle 状态（CapsLock / NumLock / ScrollLock）的同步契约。
    ///
    /// ★★ 2026-11-24（FIX-KEYBOARD-TOGGLE-SYNC）新增。为什么必须有：
    ///   三个锁是**会话级/桌面级状态**，不是按键事件。观看端（浏览器/手机）与宿主
    ///   各自有独立的锁状态：宿主 NumLock=ON 而观看端 OFF 时，观看端按小键盘 4
    ///   期望得到 "4"，远端却按自己的 NumLock 解释。更糟的是**只按「切换键」会把
    ///   远端状态翻到相反值**——所以不能"按一下 NumLock 试试"，必须先读再算。
    ///
    /// 本类只做纯计算（可离线单测），真正的注入由 RelayAgent 执行。
    /// </summary>
    public static class ToggleKeys
    {
        public const ushort VkCapsLock = 0x14;
        public const ushort VkNumLock = 0x90;
        public const ushort VkScrollLock = 0x91;

        /// <summary>三个锁的状态快照（true = ON）。</summary>
        public readonly struct State
        {
            public readonly bool Caps, Num, Scroll;
            public State(bool caps, bool num, bool scroll) { Caps = caps; Num = num; Scroll = scroll; }
            public override string ToString()
                => "Caps=" + (Caps ? "ON" : "off") + " Num=" + (Num ? "ON" : "off") + " Scroll=" + (Scroll ? "ON" : "off");
        }

        /// <summary>
        /// 把「目标状态」与「当前状态」算成「需要对哪几个键各按一次」。
        ///
        /// 语义：toggle 键按一次翻转一次，所以**只有目标 ≠ 当前**才需要按，按一次即可。
        /// 返回值按 Caps → Num → Scroll 顺序，调用方逐个小写。空列表 = 已经一致，不用动。
        /// </summary>
        public static IReadOnlyList<ushort> Diff(State current, State target)
        {
            var list = new List<ushort>(3);
            if (current.Caps != target.Caps) list.Add(VkCapsLock);
            if (current.Num != target.Num) list.Add(VkNumLock);
            if (current.Scroll != target.Scroll) list.Add(VkScrollLock);
            return list;
        }

        /// <summary>状态 → 位掩码（1=Caps, 2=Num, 4=Scroll，与前端约定一致）。</summary>
        public static int ToMask(State s)
            => (s.Caps ? 1 : 0) | (s.Num ? 2 : 0) | (s.Scroll ? 4 : 0);

        /// <summary>位掩码 → 状态。掩码里的非法高位被忽略。</summary>
        public static State FromMask(int mask)
            => new State((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);

        /// <summary>
        /// 解析 "caps=1,num=0,scroll=1" 这类紧凑串（网页/手机端传过来的形态）。
        /// 解析失败返回 false，调用方按「不同步」处理（宁可不做，也不能猜错状态）。
        /// </summary>
        public static bool TryParse(string? text, out State state)
        {
            state = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            bool? caps = null, num = null, scroll = null;
            foreach (var piece in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = piece.Split('=', 2);
                if (kv.Length != 2) return false;
                string k = kv[0].Trim().ToLowerInvariant();
                string v = kv[1].Trim();
                bool on;
                if (v == "1" || v.Equals("on", StringComparison.OrdinalIgnoreCase) || v.Equals("true", StringComparison.OrdinalIgnoreCase)) on = true;
                else if (v == "0" || v.Equals("off", StringComparison.OrdinalIgnoreCase) || v.Equals("false", StringComparison.OrdinalIgnoreCase)) on = false;
                else return false;

                switch (k)
                {
                    case "caps": case "capslock": caps = on; break;
                    case "num": case "numlock": num = on; break;
                    case "scroll": case "scrolllock": scroll = on; break;
                    default: return false;
                }
            }
            if (caps == null || num == null || scroll == null) return false;
            state = new State(caps.Value, num.Value, scroll.Value);
            return true;
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
