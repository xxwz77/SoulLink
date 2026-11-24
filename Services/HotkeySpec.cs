using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SoulLink.Services
{
    /// <summary>
    /// 一个热键描述：修饰键 + 键盘键 或 鼠标键。
    /// 由界面「录制」得到（用户按什么就记什么）。
    /// </summary>
    public sealed class HotkeySpec
    {
        public bool Ctrl { get; set; }
        public bool Alt { get; set; }
        public bool Shift { get; set; }
        public bool Win { get; set; }

        /// <summary>虚拟键码（键盘热键）</summary>
        public uint Key { get; set; }

        /// <summary>鼠标按钮：Left/Right/Middle/X1/X2。空表示键盘热键。</summary>
        public string MouseButton { get; set; } = "";

        public bool IsMouse => !string.IsNullOrEmpty(MouseButton);

        public override string ToString()
        {
            var sb = new StringBuilder();
            if (Ctrl) sb.Append("Ctrl+");
            if (Alt) sb.Append("Alt+");
            if (Shift) sb.Append("Shift+");
            if (Win) sb.Append("Win+");
            sb.Append(IsMouse ? MouseName(MouseButton) : KeyName(Key));
            return sb.ToString();
        }

        private static string MouseName(string b) => b switch
        {
            "Left" => "鼠标左键",
            "Right" => "鼠标右键",
            "Middle" => "鼠标中键",
            "X1" => "鼠标侧键4",
            "X2" => "鼠标侧键5",
            _ => b
        };

        public uint ToModifiers()
        {
            uint m = 0;
            if (Ctrl) m |= 0x0002;
            if (Alt) m |= 0x0001;
            if (Shift) m |= 0x0004;
            if (Win) m |= 0x0008;
            return m;
        }

        public const uint AltMask = 0x0001;
        public const uint CtrlMask = 0x0002;
        public const uint ShiftMask = 0x0004;
        public const uint WinMask = 0x0008;

        /// <summary>键盘和鼠标钩子共用同一份物理修饰键状态，不在回调里读异步状态。</summary>
        public bool ModifiersMatch()
        {
#if SOULLINK_AGENT
            // 注入代理不装本机键盘钩子（无物理修饰键状态）；该判定仅宿主 UI 使用。
            return ToModifiers() == 0;
#else
            return LowLevelKeyboardHook.CurrentModifiers == ToModifiers();
#endif
        }

        public HotkeySpec Clone() => new HotkeySpec
        {
            Ctrl = Ctrl, Alt = Alt, Shift = Shift, Win = Win, Key = Key, MouseButton = MouseButton
        };

        public bool MatchesMouse(MouseHookEvent ev) => MouseButton switch
        {
            "Left" => ev.IsLeftDown,
            "Right" => ev.IsRightDown,
            "Middle" => ev.IsMiddleDown,
            "X1" => ev.IsXButton1Down,
            "X2" => ev.IsXButton2Down,
            _ => false
        };

        private static readonly Dictionary<uint, string> VkNames = Build();

        private static Dictionary<uint, string> Build()
        {
            var d = new Dictionary<uint, string>();
            for (uint i = 0x41; i <= 0x5A; i++) d[i] = ((char)i).ToString();
            for (uint i = 0x30; i <= 0x39; i++) d[i] = ((char)i).ToString();
            for (uint i = 1; i <= 12; i++) d[(uint)(0x6F + i)] = "F" + i;
            d[0x20] = "Space"; d[0x09] = "Tab"; d[0x0D] = "Enter"; d[0x1B] = "Escape";
            d[0x25] = "Left"; d[0x26] = "Up"; d[0x27] = "Right"; d[0x28] = "Down";
            d[0x24] = "Home"; d[0x23] = "End"; d[0x21] = "PageUp"; d[0x22] = "PageDown";
            d[0x2D] = "Insert"; d[0x2E] = "Delete";
            d[0xC0] = "`"; d[0xBD] = "-"; d[0xBB] = "=";
            d[0xDB] = "["; d[0xDD] = "]"; d[0xDC] = "\\";
            d[0xBA] = ";"; d[0xDE] = "'"; d[0xBC] = ","; d[0xBE] = "."; d[0xBF] = "/";
            return d;
        }

        public static string KeyName(uint vk)
            => VkNames.TryGetValue(vk, out var n) ? n : ("VK_" + vk.ToString("X2"));

        public static HotkeySpec? Parse(string? spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;
            var r = new HotkeySpec();
            foreach (var raw in spec.Split(new[] { '+', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;
                switch (p.ToLowerInvariant())
                {
                    case "ctrl": case "control": r.Ctrl = true; continue;
                    case "alt": r.Alt = true; continue;
                    case "shift": r.Shift = true; continue;
                    case "win": case "windows": r.Win = true; continue;
                    case "mouseleft": r.MouseButton = "Left"; continue;
                    case "mouseright": r.MouseButton = "Right"; continue;
                    case "mousemiddle": r.MouseButton = "Middle"; continue;
                    case "mouse4": case "x1": r.MouseButton = "X1"; continue;
                    case "mouse5": case "x2": r.MouseButton = "X2"; continue;

                    // ★ 必须同时认 ToString() 产生的中文名。
                    //   ToString 输出「鼠标侧键4」，而这里原来只认 "mouse4"/"x1"，
                    //   于是 ConfigStore 存下去的中文名读回来 Parse 结果为 null —— 热键配置整个丢失。
                    case "鼠标左键": r.MouseButton = "Left"; continue;
                    case "鼠标右键": r.MouseButton = "Right"; continue;
                    case "鼠标中键": r.MouseButton = "Middle"; continue;
                    case "鼠标侧键4": case "鼠标键4": r.MouseButton = "X1"; continue;
                    case "鼠标侧键5": case "鼠标键5": r.MouseButton = "X2"; continue;
                }
                foreach (var kv in VkNames)
                    if (string.Equals(kv.Value, p, StringComparison.OrdinalIgnoreCase)) { r.Key = kv.Key; break; }
            }
            if (r.IsMouse) return r;
            return r.Key != 0 ? r : null;
        }
    }
}
