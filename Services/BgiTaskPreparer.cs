using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    public static partial class BgiTaskPreparer
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
                // 所有 stopBgi 调用都走全局启动守卫，不再把停止脚本快捷键当成关闭进程快捷键。
                if (stopBgi)
                {
                    var guard = await BgiStartGuard.BeforeStartAsync(sessionId, bgiExePath, ct).ConfigureAwait(false);
                    if (!guard.Ok) return guard;
                    log.Append(guard.Message + "；"); method = guard.StopMethod;
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
                        if (!await WaitAllExitAsync("YuanShen", sessionId, 15000, ct).ConfigureAwait(false))
                            return new Result { Ok=false, Message="原神进程未全部退出，已阻止继续", StopMethod=method };
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

        /// <summary>守卫调用的停止脚本确认接口：根据目标会话日志/状态确认，不用进程退出冒充脚本停止。</summary>
        internal static DateTime _stopBaselineUtc;
        internal static string[] ScriptStopMarkers = { "执行结束", "取消执行", "停止当前脚本", "已停止", "任务已停止", "任务结束", "未成功完成" };
        internal static bool HasScriptStopEvidence(string log, DateTime baselineUtc)
        {
            if (string.IsNullOrEmpty(log)) return false;
            foreach (var raw in log.Replace("\r","").Split('\n'))
            {
                var line = raw.Trim();
                if (!ScriptStopMarkers.Any(m => line.Contains(m,StringComparison.OrdinalIgnoreCase))) continue;
                var m = System.Text.RegularExpressions.Regex.Match(line, @"\[(?<d>\d{4}-\d{2}-\d{2}[ T])?(?<t>\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)\]");
                if (!m.Success) continue;
                string stamp = (m.Groups["d"].Success ? m.Groups["d"].Value.Trim()+" " : DateTime.UtcNow.ToString("yyyy-MM-dd")+" ") + m.Groups["t"].Value;
                if (DateTime.TryParse(stamp, out var at) && at.ToUniversalTime() >= baselineUtc) return true;
            }
            return false;
        }

        public static async Task<bool> WaitForScriptStoppedAsync(int sessionId, TimeSpan timeout, CancellationToken ct)
        {
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                ct.ThrowIfCancellationRequested();
                if (BgiLogWatcher.Discover().TryGetValue(sessionId, out var inst) && inst != null
                    && HasScriptStopEvidence(BgiLogWatcher.TailLines(inst.LogFile, 250).text, _stopBaselineUtc)) return true;
                try { await Task.Delay(250, ct).ConfigureAwait(false); } catch { return false; }
            }
            return false;
        }

        internal static bool HasRunningProcess(int sessionId)
        {
            var processes = FindProcessesInSession("BetterGI", sessionId);
            bool any = processes.Count > 0;
            foreach (var p in processes) p.Dispose();
            return any;
        }

        internal static bool HasRunningProcess(int sessionId, string exePath)
        {
            // 必须是当前会话、且真实 exe 等于目标 BGI；不会把其它账号或其它程序算成 BGI。
            try
            {
                string wanted = BgiPathResolver.Normalize(exePath);
                if (wanted.Length == 0) return HasRunningProcess(sessionId);
                foreach (var p in Process.GetProcessesByName("BetterGI"))
                using (p)
                {
                    try
                    {
                        if (p.SessionId != sessionId) continue;
                        string actual = BgiPathResolver.Normalize(p.MainModule?.FileName ?? "");
                        if (actual.Equals(wanted,StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        public static async Task<bool> CloseProcessesAsync(int sessionId, CancellationToken ct)
        {
            try { KillAllInSession("BetterGI", sessionId); }
            catch { return false; }
            return await WaitAllExitAsync("BetterGI", sessionId, 15000, ct).ConfigureAwait(false);
        }

        public static Task<bool> ConfirmAllExitedAsync(int sessionId, TimeSpan timeout, CancellationToken ct)
            => WaitAllExitAsync("BetterGI", sessionId, (int)timeout.TotalMilliseconds, ct);

        // ── 读取 BGI 的「停止当前脚本」快捷键 ────────────────────────────

        [GeneratedRegex(@"""cancelTaskHotkey""\s*:\s*""(?<v>[^""]*)""", RegexOptions.Compiled)]
        private static partial Regex RxCancel();

        [GeneratedRegex(@"""cancelTaskHotkeyType""\s*:\s*""(?<v>[^""]*)""", RegexOptions.Compiled)]
        private static partial Regex RxCancelType();

        /// <summary>
        /// 从该账号的 BGI 配置里读 cancelTaskHotkey（停止当前脚本）。
        /// 配置文件在 &lt;BGI 目录&gt;\User\config.json。
        /// 读不到就返回空字符串，调用方会改为直接杀进程。
        /// </summary>
        /// <summary>诊断用：记录快捷键读取的决策路径（只在真的读失败时才有排查价值）。</summary>
        private static void HkLog(string msg)
        {
            try { App.AppendErrorLog("ReadCancelHotkey: " + msg, null); } catch { }
        }

        /// <summary>
        /// 按**会话 ID** 取「停止当前脚本」快捷键 —— 首选路径。
        ///
        /// ★★ 2.17.47：用户要求「直接从数据中心读取」。★★
        ///
        ///   数据中心（BgiDataCenter）已经按账号解析好 config.json，
        ///   界面上显示的就是它读出来的 F12。这里直接复用那一份结果，
        ///   不再自己从 exePath 反推目录和正则匹配：
        ///
        ///     · 会话ID → 账号（SessionService / AccountStateCenter 都可查）
        ///     · 账号 → BgiDataCenter.Refresh(账号).CancelTaskHotkey
        ///
        ///   只有「查不到会话对应的账号」时才退回按 exePath 读文件。
        /// </summary>
        public static string ReadCancelHotkeyFor(int sessionId, string bgiExePath)
        {
            try
            {
                // 1) 会话 ID → 账号：AccountStateCenter 每个账号都记着 SessionId
                string user = "";
                try
                {
                    foreach (var st in AccountStateCenter.All())
                        if (st.SessionId == sessionId && (st.UserName ?? "").Length > 0)
                        { user = st.UserName!; break; }
                }
                catch { }

                if (user.Length > 0)
                {
                    // 2) 账号 → 数据中心快照里的 CancelTaskHotkey
                    foreach (var a in App.Config.Accounts)
                    {
                        if (!string.Equals(BgiPathResolver.Normalize(a.UserName),
                                           BgiPathResolver.Normalize(user),
                                           StringComparison.OrdinalIgnoreCase)) continue;

                        string hk = BgiDataCenter.Refresh(a.UserName).CancelTaskHotkey;
                        if (hk.Length > 0)
                        {
                            HkLog("数据中心命中 " + a.UserName + " => " + hk + "（会话 " + sessionId + "）");
                            return hk;
                        }
                        HkLog("数据中心读到空 " + a.UserName + "，转用 exePath");
                        break;
                    }
                }
                else HkLog("会话 " + sessionId + " 找不到对应用户，转用 exePath");
            }
            catch (Exception ex) { HkLog("按会话取快捷键失败: " + ex.Message); }

            // 3) 兜底：按 exePath 读该 BGI 目录下的 User\config.json
            return ReadCancelHotkey(bgiExePath);
        }

        public static string ReadCancelHotkey(string bgiExePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(bgiExePath)) { HkLog("输入路径为空"); return ""; }
                // ★★ 2.17.47：修致命 bug —— 未绑定账号的空路径会匹配一切空输入。★★
                //
                //   现场：日志出现「BGI 未设置停止快捷键，已直接关闭并确认退出」，
                //   但 D:\AAA挂机专区\A1\BetterGI\User\config.json 里明明是 "F12"。
                //
                //   根因：下面这个匹配循环里，未绑定 BGI 的账号 GetPath() 返回 ""，
                //   而 Normalize("") 仍是 ""。只要传入的 bgiExePath 归一化后也是空，
                //   "" == "" 成立 → 命中该账号 → 返回它（空的）CancelTaskHotkey，
                //   直接 return，永远走不到后面按 exePath 推导目录的正确分支。
                foreach (var a in App.Config.Accounts)
                {
                    string accountPath = BgiPathResolver.Normalize(BgiPathResolver.GetPath(a.UserName));
                    if (accountPath.Length == 0) continue;   // ★ 空路径不参与匹配
                    if (string.Equals(accountPath, BgiPathResolver.Normalize(bgiExePath), StringComparison.OrdinalIgnoreCase))
                    {
                        string hk = BgiDataCenter.Refresh(a.UserName).CancelTaskHotkey;
                        if (hk.Length > 0) { HkLog("账号匹配命中 " + a.UserName + " => " + hk); return hk; }
                        // 账号匹配到了但快捷键为空：不要直接返回，继续按 exePath 兜底
                        HkLog("账号匹配 " + a.UserName + " 但快捷键为空，转用配置目录");
                        break;
                    }
                }
                string? dir = Path.GetDirectoryName(bgiExePath);
                if (string.IsNullOrEmpty(dir)) { HkLog("无法从 exePath 取目录: " + bgiExePath); return ""; }

                string cfg = Path.Combine(dir, "User", "config.json");
                if (!File.Exists(cfg)) { HkLog("配置文件不存在: " + cfg); return ""; }

                // ★ 用 FileShare.ReadWrite：BGI 可能正在写这个文件
                string text;
                using (var fs = new FileStream(cfg, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    text = sr.ReadToEnd();
                }

                var m = RxCancel().Match(text);
                if (!m.Success) { HkLog("配置里没有 cancelTaskHotkey: " + cfg); return ""; }
                string key = m.Groups["v"].Value.Trim();

                // 空字符串 = 用户没设置，返回空让调用方走杀进程
                if (key.Length == 0 || key.Equals("< None >", StringComparison.OrdinalIgnoreCase))
                { HkLog("cancelTaskHotkey 为空或 <None>: " + cfg); return ""; }

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
        /// <summary>
        /// 对外暴露的快捷键投递（BgiUpdater 停 BGI 时用）。
        /// 只发到指定进程，不抢焦点、不影响其他用户。
        /// </summary>
        public static void SendHotkeyToSessionPublic(int pid, string keyName)
            => SendHotkeyToSession(pid, keyName);

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
