using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>
    /// 执行一条龙 / 配置组前的"清场"准备。
    ///
    /// 背景（用户实测）：BGI 正在跑任务时，再执行 --startOneDragon 会被拒绝，
    /// 必须先让它停下来。而停止的方式取决于该账号 BGI 里的快捷键设置：
    ///   · cancelTaskHotkey  —— 「停止当前脚本/独立任务」（如 F12 / NumPad9）
    ///   · 这个值是**每个账号独立配置**的，不能写死
    ///
    /// ★ 铁律：所有操作只针对**目标会话**（按 SessionId 过滤进程），
    ///   绝不碰其他用户的 BGI / 原神。
    /// </summary>
    public static class BgiTaskPreparer
    {
        /// <summary>一次准备的结果。</summary>
        public sealed class Result
        {
            public bool Ok = true;
            public string Message = "";
            /// <summary>实际用了哪种方式停止：hotkey / kill / none。</summary>
            public string StopMethod = "";
        }

        /// <summary>
        /// 清场：停 BGI（可选关原神），等进程真正退出，再额外等 extraWait 秒。
        /// </summary>
        public static async Task<Result> PrepareAsync(
            int sessionId, string bgiExePath, bool stopBgi, bool closeGenshin,
            int extraWaitSeconds, CancellationToken ct = default)
        {
            var log = new StringBuilder();
            string method = "none";

            try
            {
                // ── ① 停止 BGI ─────────────────────────────────────────────
                if (stopBgi)
                {
                    var bgis = FindProcessesInSession("BetterGI", sessionId);
                    if (bgis.Count == 0)
                    {
                        log.Append("BGI 未在运行，无需停止；");
                        method = "none";
                    }
                    else
                    {
                        // 先试快捷键（BGI 会自己优雅收尾）
                        string hotkey = ReadCancelHotkey(bgiExePath);
                        if (!string.IsNullOrEmpty(hotkey))
                        {
                            log.Append("发送停止快捷键 " + hotkey + "；");
                            SendHotkeyToSession(bgis[0].Id, hotkey);
                            method = "hotkey";

                            // 等 3 秒看它是否自己退出
                            if (await WaitAllExitAsync("BetterGI", sessionId, 3000, ct).ConfigureAwait(false))
                            {
                                log.Append("BGI 已响应快捷键退出；");
                            }
                            else
                            {
                                log.Append("快捷键未让它退出，改为结束进程；");
                                KillAllInSession("BetterGI", sessionId);
                                method = "kill";
                            }
                        }
                        else
                        {
                            // 没配快捷键 → 直接关进程
                            log.Append("该账号未配置「停止当前脚本」快捷键，直接结束 BGI 进程；");
                            KillAllInSession("BetterGI", sessionId);
                            method = "kill";
                        }

                        await WaitAllExitAsync("BetterGI", sessionId, 15000, ct).ConfigureAwait(false);
                    }
                }

                // ── ② 关闭原神（可选）─────────────────────────────────────
                if (closeGenshin)
                {
                    var games = FindProcessesInSession("YuanShen", sessionId);
                    if (games.Count == 0)
                    {
                        log.Append("原神未在运行；");
                    }
                    else
                    {
                        log.Append("结束原神进程 " + games.Count + " 个；");
                        KillAllInSession("YuanShen", sessionId);
                        await WaitAllExitAsync("YuanShen", sessionId, 15000, ct).ConfigureAwait(false);
                    }
                }

                // ── ③ 额外缓冲 ────────────────────────────────────────────
                if (extraWaitSeconds > 0)
                {
                    log.Append("额外等待 " + extraWaitSeconds + " 秒；");
                    await Task.Delay(TimeSpan.FromSeconds(extraWaitSeconds), ct).ConfigureAwait(false);
                }

                return new Result
                {
                    Ok = true,
                    Message = log.ToString().TrimEnd('；'),
                    StopMethod = method
                };
            }
            catch (OperationCanceledException) { return new Result { Ok = false, Message = "已取消" }; }
            catch (Exception ex)
            {
                return new Result { Ok = false, Message = "清场失败：" + ex.Message, StopMethod = method };
            }
        }

        // ── 进程操作（全部按会话过滤）────────────────────────────────────

        /// <summary>只返回属于该会话的进程（同名进程在其他会话里的一律排除）。</summary>
        private static List<Process> FindProcessesInSession(string name, int sessionId)
        {
            var list = new List<Process>();
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (p.SessionId == sessionId) list.Add(p);
                        else p.Dispose();
                    }
                    catch { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
            return list;
        }

        /// <summary>结束该会话里所有同名进程。</summary>
        private static void KillAllInSession(string name, int sessionId)
        {
            foreach (var p in FindProcessesInSession(name, sessionId))
            {
                try { p.Kill(); } catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }

        /// <summary>等该会话里同名进程全部消失；超时返回 false。</summary>
        private static async Task<bool> WaitAllExitAsync(string name, int sessionId, int timeoutMs, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (ct.IsCancellationRequested) return false;

                var procs = FindProcessesInSession(name, sessionId);
                bool any = procs.Count > 0;
                foreach (var p in procs) { try { p.Dispose(); } catch { } }
                if (!any) return true;

                try { await Task.Delay(300, ct).ConfigureAwait(false); } catch { return false; }
            }
            return false;
        }

        // ── 读取 BGI 的「停止当前脚本」快捷键 ────────────────────────────

        private static readonly Regex RxCancel =
            new Regex(@"""cancelTaskHotkey""\s*:\s*""(?<v>[^""]*)""", RegexOptions.Compiled);

        private static readonly Regex RxCancelType =
            new Regex(@"""cancelTaskHotkeyType""\s*:\s*""(?<v>[^""]*)""", RegexOptions.Compiled);

        /// <summary>
        /// 从该账号的 BGI 配置里读 cancelTaskHotkey（停止当前脚本）。
        /// 配置文件在 &lt;BGI 目录&gt;\User\config.json。
        /// 读不到就返回空字符串，调用方会改为直接杀进程。
        /// </summary>
        public static string ReadCancelHotkey(string bgiExePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(bgiExePath)) return "";
                string? dir = Path.GetDirectoryName(bgiExePath);
                if (string.IsNullOrEmpty(dir)) return "";

                string cfg = Path.Combine(dir, "User", "config.json");
                if (!File.Exists(cfg)) return "";

                // ★ 用 FileShare.ReadWrite：BGI 可能正在写这个文件
                string text;
                using (var fs = new FileStream(cfg, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    text = sr.ReadToEnd();
                }

                var m = RxCancel.Match(text);
                if (!m.Success) return "";
                string key = m.Groups["v"].Value.Trim();

                // 空字符串 = 用户没设置，返回空让调用方走杀进程
                if (key.Length == 0 || key.Equals("< None >", StringComparison.OrdinalIgnoreCase)) return "";

                // 只支持键盘热键；类型不是 KeyboardMonitor 也照样尝试发送
                return key;
            }
            catch { return ""; }
        }

        // ── 往目标会话发送按键 ──────────────────────────────────────────

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;

        /// <summary>
        /// 把快捷键名（如 "F12" / "NumPad9"）发给指定进程。
        ///
        /// ★ 用 PostMessage 定点投递到那个进程的主窗口，
        ///   不依赖前台焦点、不影响其他用户 —— 这是"只针对目标用户"的关键。
        /// </summary>
        private static void SendHotkeyToSession(int pid, string keyName)
        {
            try
            {
                var spec = HotkeySpec.Parse(keyName);
                if (spec == null) return;

                var target = FindMainWindow(pid);
                if (target == IntPtr.Zero) return;

                uint vk = spec.Key;
                if (vk == 0) return;

                // lParam: 重复次数(1) | 扫描码 | 扩展位
                IntPtr down = (IntPtr)(1 | (MapScan(vk) << 16));
                IntPtr up = (IntPtr)(1 | (MapScan(vk) << 16) | (1 << 30) | (unchecked((int)0x80000000)));

                PostMessage(target, WM_KEYDOWN, (IntPtr)vk, down);
                System.Threading.Thread.Sleep(60);
                PostMessage(target, WM_KEYUP, (IntPtr)vk, up);
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        private static int MapScan(uint vk) => (int)(MapVirtualKey(vk, 0) & 0xFF);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        /// <summary>找属于该进程的第一个带标题的可见窗口（BGI 主窗口）。</summary>
        private static IntPtr FindMainWindow(int pid)
        {
            IntPtr found = IntPtr.Zero;
            try
            {
                EnumWindows((h, l) =>
                {
                    GetWindowThreadProcessId(h, out uint p);
                    if (p == (uint)pid && IsWindowVisible(h) && GetWindowTextLength(h) > 0)
                    {
                        found = h;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }
    }
}
