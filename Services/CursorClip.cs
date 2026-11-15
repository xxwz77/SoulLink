using System;
using System.Runtime.InteropServices;

namespace SoulLink.Services
{
    /// <summary>
    /// 把系统光标限制在一个屏幕矩形内（ClipCursor）。
    ///
    /// ★ 这是"自由视角"真正该用的模型，和"钉中心 + 相对注入"完全不同：
    ///
    ///   钉中心模型：光标隐藏并锁死在画面中心，靠 mouse_event 相对位移转视角。
    ///              拖拽时远端光标被相对位移推着走、本地光标却每帧被拽回中心，
    ///              两个位置互相打架 —— 拖地图就是一顿一顿的（用户实测反馈）。
    ///
    ///   ClipCursor：光标**真实可见地移动**，只是不许离开画面矩形。
    ///              移动走 RDP 原生通道（绝对坐标），落点、拖拽、点击全是真实位置，
    ///              手感和"游戏无边框窗口化"完全一致；到边上也不会跑到别的窗口。
    ///
    /// 这就是用户要的效果：鼠标不会从 RDP 窗口出来，但在窗口内跟真鼠标一样。
    /// </summary>
    public static class CursorClip
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClipCursor(ref RECT rect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClipCursor(IntPtr rect);

        /// <summary>当前是否处于限制状态（用于幂等与兜底恢复）。</summary>
        public static bool IsClipped { get; private set; }

        /// <summary>
        /// 把光标限制在 (left,top)-(right,bottom) 屏幕矩形内。
        /// right/bottom 是**开区间**的右下角（ClipCursor 语义），内部会 -1 让光标能到达最后一行/列。
        /// </summary>
        public static bool Apply(int left, int top, int width, int height)
        {
            try
            {
                if (width <= 0 || height <= 0) return false;
                var r = new RECT
                {
                    Left = left,
                    Top = top,
                    Right = left + width - 1,
                    Bottom = top + height - 1
                };
                if (ClipCursor(ref r))
                {
                    IsClipped = true;
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>解除限制。任何退出路径都必须调用，否则用户鼠标会被困住。</summary>
        public static void Release()
        {
            try { ClipCursor(IntPtr.Zero); } catch { }
            IsClipped = false;
        }

        /// <summary>兜底：进程退出/异常时强制松开。</summary>
        public static void ForceRelease() => Release();
    }
}
