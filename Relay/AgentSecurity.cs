using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SoulLink.Relay
{
    /// <summary>管理员代理启动自检；不提供提升操作。</summary>
    internal static class AgentSecurity
    {
        internal static string? Validate(int session, string? nonce)
        {
            if (!SystemLauncherSessionIsElevated()) return "代理未获得管理员提权令牌";
            if (Process.GetCurrentProcess().SessionId != session) return "代理不在目标会话";
            if (string.IsNullOrWhiteSpace(nonce) || nonce.Length != 32) return "代理缺少部署 nonce";
            string desktop = CurrentDesktop();
            // 桌面名就是 "Default"（winsta0\default）；取不到名字时不要误判为失败，
            // 真正的判据是下面的 OpenInputDesktop。
            if (desktop.Length > 0 && !desktop.Equals("Default", StringComparison.OrdinalIgnoreCase)
                && !desktop.EndsWith("\\default", StringComparison.OrdinalIgnoreCase))
                return "代理桌面错误：" + desktop;
            IntPtr input = OpenInputDesktop(0, false, GENERIC_READ);
            if (input == IntPtr.Zero) return "无法打开目标输入桌面：" + Marshal.GetLastWin32Error();
            CloseDesktop(input); return null;
        }

        private static bool SystemLauncherSessionIsElevated() => Environment.IsPrivilegedProcess;

        /// <summary>
        /// 当前线程所在桌面的名字（不是窗口站名字）。
        /// ★ 旧实现用 GetProcessWindowStation 取到的是 "WinSta0"，再拿去和 "\default" 比，
        ///   永远不相等；由于普通通道从不传 --require-elevated，这个检查一直没被执行过，
        ///   Broker 通道第一次带上 --require-elevated 就把代理判成"桌面错误"并自杀。
        /// </summary>
        private static string CurrentDesktop()
        {
            IntPtr desktop = GetThreadDesktop(GetCurrentThreadId());
            if (desktop == IntPtr.Zero) return "";
            int needed = 0;
            GetUserObjectInformation(desktop, UOI_NAME, null, 0, out needed);
            var name = new StringBuilder(Math.Max(needed / 2 + 2, 256));
            if (!GetUserObjectInformation(desktop, UOI_NAME, name, name.Capacity, out _)) return "";
            return name.ToString();
        }
        private const int UOI_NAME = 1;
        private const uint GENERIC_READ = 0x80000000;
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetThreadDesktop(uint threadId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr obj, int index, StringBuilder info, int length, out int needed);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    }
}
