using System;

namespace SoulLink.Services
{
    /// <summary>
    /// 热键事件判定。宿主和 RelayAgent 共用，不依赖 WPF。
    /// 只在调用方明确要求时吞键；识别失败一律放行普通输入。
    /// </summary>
    public sealed class HotkeyMatcher
    {
        private HotkeySpec? _spec;
        private uint _modifiers;
        private bool _keyDown;
        private bool _matchedHotkey;
        private bool _matchedModifiers;

        public HotkeyMatcher(HotkeySpec? spec) => Spec = spec;
        public uint Modifiers => _modifiers;

        public HotkeySpec? Spec
        {
            get => _spec;
            set
            {
                _spec = value;
                _keyDown = false;
                _matchedHotkey = false;
                _matchedModifiers = false;
            }
        }

        /// <summary>安装钩子或模式切换时同步已按下的物理修饰键。</summary>
        public void SynchronizeModifiers(uint modifiers)
        {
            _modifiers = modifiers;
            _keyDown = false;
            _matchedHotkey = false;
            _matchedModifiers = false;
        }

        public static uint NormalizeModifier(uint vk) => vk switch
        {
            0xA0 or 0xA1 => 0x10,
            0xA2 or 0xA3 => 0x11,
            0xA4 or 0xA5 => 0x12,
            _ => vk
        };

        public static bool IsModifier(uint vk)
            => vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x5B || vk == 0x5C;

        public static uint ModifierBit(uint vk) => vk switch
        {
            0x10 => HotkeySpec.ShiftMask,
            0x11 => HotkeySpec.CtrlMask,
            0x12 => HotkeySpec.AltMask,
            0x5B or 0x5C => HotkeySpec.WinMask,
            _ => 0
        };

        /// <summary>返回 true 表示吞掉该事件，matched 表示本次完整组合首次按下。</summary>
        public bool Key(uint vk, bool down, bool injected, bool canTrigger,
            bool suppressModifiers, out bool matched)
        {
            matched = false;
            vk = NormalizeModifier(vk);
            if (injected || vk == 0 || vk == 0xFF) return false;

            uint bit = ModifierBit(vk);
            if (bit != 0)
            {
                if (down) _modifiers |= bit;
                else _modifiers &= ~bit;
            }

            var spec = Spec;
            if (down && IsModifier(vk))
                _matchedModifiers = spec != null && !spec.IsMouse
                    && (bit & spec.ToModifiers()) != 0;

            if (vk == spec?.Key && !down)
            {
                bool swallow = _matchedHotkey;
                _keyDown = false;
                _matchedHotkey = false;
                return swallow;
            }

            if (IsModifier(vk))
            {
                bool swallow = _matchedModifiers && !down && suppressModifiers;
                if (!down) _matchedModifiers = false;
                return swallow;
            }

            if (spec == null || spec.IsMouse || spec.Key == 0 || vk != spec.Key || !down)
                return false;
            if (_keyDown && _matchedHotkey) return true;
            _keyDown = true;
            _matchedHotkey = false;

            if (_modifiers != spec.ToModifiers() || !canTrigger) return false;
            _matchedHotkey = matched = true;
            return true;
        }
    }
}
