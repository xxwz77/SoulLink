using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SoulLink.Services
{
    /// <summary>
    /// 用 Windows 原生 API（WTSEnumerateSessions）枚举会话。
    ///
    /// ★★ 为什么要替换 query.exe / qwinsta ★★
    ///
    ///   原来的做法每轮起两个子进程。实测每次 Process.Start 会在
    ///   .NET 运行时里留下约 0.6 个 \\.\pipe\dotnet_&lt;guid&gt; 命名管道
    ///   （.NET 用命名管道实现内部的跨进程同步对象），而且这些管道
    ///   在 GC.Collect() 之后依然残留：
    ///
    ///     重定向后读流   60 次 → +36 个管道
    ///     完全不重定向   60 次 → +38 个管道
    ///
    ///   也就是说：只要还起子进程，就会漏，跟读写方式无关。
    ///   状态中心在启动窗口是 1 Hz，等于 1.2 个管道/秒，
    ///   一天就是 10 万个 —— 而第三方软件泄漏让系统管道总数到了 3.8 万时，
    ///   任何遍历 \\.\pipe\ 的代码都会慢到 6 秒（界面卡死）。
    ///
    ///   WTSEnumerateSessions 是纯 API 调用：
    ///     · 不创建进程 → 零泄漏
    ///     · 单次约 0.1 ms（子进程约 50 ms，差 500 倍）
    ///     · 直接给出会话号、用户名、状态、登录时间，不需要解析文本
    ///
    /// ★ 保留 query.exe 路径作为兜底：WTS API 在极少数环境
    ///   （如被裁剪的 Server Core、权限异常）可能失败，此时退回原逻辑。
    /// </summary>
    internal static class WtsSessionQuery
    {
        // ── P/Invoke ──────────────────────────────────────────────────

        // ★ 必须显式布局：int(4) + 填充(4) + 指针(8) + int(4) + 填充(4) = 24 字节。
        //   用 Sequential 时 WPF 的 marshaler 会按 12 字节算，
        //   导致 pWinStationName 读偏、站名变成乱码（实测）。
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct WTS_SESSION_INFO
        {
            [FieldOffset(0)] public int SessionId;
            [FieldOffset(8)] public IntPtr pWinStationName;
            [FieldOffset(16)] public int State;         // WTS_CONNECTSTATE_CLASS
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSEnumerateSessions(
            IntPtr hServer, int Reserved, int Version,
            out IntPtr ppSessionInfo, out int pCount);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr pMemory);

        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool WTSQuerySessionInformation(
            IntPtr hServer, int sessionId, int wtsInfoClass,
            out IntPtr ppBuffer, out int pBytesReturned);

        // WTS_INFO_CLASS
        private const int WTSUserName = 5;
        private const int WTSDomainName = 7;
        private const int WTSConnectState = 8;
        private const int WTSClientName = 10;

        // WTS_CONNECTSTATE_CLASS → 界面用词
        private static string StateText(int state) => state switch
        {
            0 => "Active",      // WTSActive
            1 => "Disc",        // WTSConnected（已连接但未登录）
            2 => "Conn",        // WTSConnectQuery
            3 => "Conn",        // WTSShadow
            4 => "Disc",        // WTSDisconnected
            5 => "Idle",        // WTSIdle
            6 => "Listen",      // WTSListen
            7 => "Reset",       // WTSReset
            8 => "Down",        // WTSDown
            9 => "Init",        // WTSInit
            _ => "Unknown",
        };

        /// <summary>单个会话的原始信息。</summary>
        internal sealed class RawSession
        {
            public int Id { get; set; }
            public string UserName { get; set; } = "";
            public string State { get; set; } = "";
            public string Station { get; set; } = "";
        }

        /// <summary>
        /// 枚举本机全部交互式会话。失败返回 null（调用方据此退回 query.exe）。
        /// </summary>
        internal static List<RawSession>? Enumerate()
        {
            var result = new List<RawSession>();
            IntPtr buf = IntPtr.Zero;
            try
            {
                if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out buf, out int count) || buf == IntPtr.Zero)
                    return null;

                int size = Marshal.SizeOf<WTS_SESSION_INFO>();
                for (int i = 0; i < count; i++)
                {
                    IntPtr p = (IntPtr)(buf.ToInt64() + i * size);
                    var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(p);

                    // 只要真实会话：Listen（65536）和 services（0）不是用户桌面
                    if (info.SessionId <= 0) continue;

                    string user = QueryString(info.SessionId, WTSUserName);
                    if (user.Length == 0) continue;      // 没人登录的会话列表里也用不上

                    result.Add(new RawSession
                    {
                        Id = info.SessionId,
                        UserName = user,
                        State = StateText(info.State),
                        // ★★ 必须用 Ansi 读，不能用 Unicode ★★
                        //
                        //   WTS_SESSION_INFO.pWinStationName 指向的是
                        //   **窄字符（ANSI）** 字符串，不是 UTF-16。
                        //   用 PtrToStringUni 读会把两个字节当一个字符，
                        //   于是 "Console" 变成 "潃獮汯e"（每字节序都被反转）。
                        //
                        //   后果非常隐蔽：IsConsoleSession 靠 StartsWith("console")
                        //   判断主会话，站名乱码 → 永远判 false → 左栏永远进
                        //   "单账号状态"分支，**总览面板不显示**。
                        //   而 query user 兜底路径是文本解析，站名是对的，
                        //   于是"有时正常有时不正常"，极难定位。
                        //
                        //   实测对比：
                        //     PtrToStringUni  → [潃獮汯e䑒ⵐ捔⍰0...]
                        //     PtrToStringAnsi → [Console]
                        Station = info.pWinStationName == IntPtr.Zero
                                  ? "" : (Marshal.PtrToStringAnsi(info.pWinStationName) ?? ""),
                    });
                }
                return result;
            }
            catch
            {
                return null;      // 交给调用方兜底
            }
            finally
            {
                if (buf != IntPtr.Zero) { try { WTSFreeMemory(buf); } catch { } }
            }
        }

        private static string QueryString(int sessionId, int infoClass)
        {
            IntPtr p = IntPtr.Zero;
            try
            {
                if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out p, out int bytes)
                    || p == IntPtr.Zero)
                    return "";
                return Marshal.PtrToStringUni(p) ?? "";
            }
            catch { return ""; }
            finally
            {
                if (p != IntPtr.Zero) { try { WTSFreeMemory(p); } catch { } }
            }
        }
    }
}
