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
        /// <summary>
        /// 发「停止当前脚本」快捷键**之前**打的时间戳，用于过滤日志里的历史"执行结束"。
        /// ★ 名字里的 Utc 是历史遗留：实际存的是 DateTime.UtcNow。
        ///   比较时必须先把日志时间戳换算到同一个基准，见 HasScriptStopEvidence。
        /// </summary>
        internal static DateTime _stopBaselineUtc = DateTime.MinValue;

        /// <summary>
        /// 「脚本已停」的日志判定词（实测自 20261120 真实日志）。
        ///
        /// ★★ 3.1.11 修复：原来这里**没有**「按下快捷键」那条。★★
        ///   BGI 收到「停止当前脚本」快捷键后，真正写进日志的是：
        ///     检测到您配置的停止快捷键"F12"按下，停止当前执行任务
        ///   而旧词表里只有「执行结束 / 取消执行 / 停止当前脚本 / 已停止 /
        ///   任务已停止 / 任务结束 / 未成功完成」，**一条都对不上**。
        ///   后果：界面（BgiLogCenter.RxStopped / BgiStatusParser）能看到脚本停了，
        ///   但启动守卫等满 10 秒也判定不出「停了」，于是每次清场都报
        ///   「停止当前脚本未确认」——用户看到的就是"按了快捷键却还是失败"。
        /// </summary>
        internal static string[] ScriptStopMarkers = {
            "执行结束", "取消执行", "停止当前脚本", "已停止", "任务已停止", "任务结束", "未成功完成",
            "停止快捷键",          // 检测到您配置的停止快捷键"F12"按下，停止当前执行任务
            "停止当前执行任务",
            "中断执行", "已停止运行"
        };
        /// <summary>
        /// 日志里是否出现了**这个快捷键名** —— 停止判定的主判据。
        ///
        /// ★★ 3.1.11 按用户要求简化 ★★
        ///   用户原话：「日志只需要出现相同的按键名字就可以判断按下了」。
        ///
        ///   BGI 收到「停止当前脚本」快捷键后写的原文是：
        ///     检测到您配置的停止快捷键"F12"按下，停止当前执行任务
        ///   所以在「发键之后新增的日志」里看到 **F12 这个按键名**，就说明它按下了。
        ///
        ///   为什么不用整句匹配：
        ///     · 整句里包含全角引号、空格位置也可能随版本变化，
        ///       一旦 BGI 改字就判不出来（历史上正因为词表对不上而永远超时）；
        ///     · 而按键名（F12 / NumPad9 / Ctrl+Shift+K）是我们自己发出去的，
        ///       BGI 把它原样写进日志，匹配它最稳。
        ///
        ///   仍然保留整句锚点作为补充：有些 BGI 版本可能只写
        ///     「取消执行配置组: "X"」「任务已停止」
        ///   这类不含按键名的停止行。
        /// </summary>
        internal static bool HasHotkeyPressEvidence(string log, DateTime baselineLocal, string hotkey)
        {
            if (string.IsNullOrEmpty(log) || string.IsNullOrWhiteSpace(hotkey)) return false;

            // ★★ 3.1.11：判定用的键名**全部来自用户自己的 BGI 配置**（cancelTaskHotkey），
            //   不是写死 F12。ReadCancelHotkeyFor(sessionId, exePath) 读到的就是它，
            //   不同账号可以各不相同（实测有 F12 / F11 / NumPad9 混用）。
            //
            //   还要准备"写法变体"，因为 BGI 写日志时用的名字可能和配置里不完全一致：
            //     配置 NumPad9  → 日志可能写 NumPad9 / Numpad9 / 数字小键盘 9 / Num9
            //     配置 F12      → 日志可能写 F12 / F 12
            //   这里一次性展开所有变体，任一命中即算按下。
            var names = new List<string>();
            void Add(string s)
            {
                s = (s ?? "").Trim();
                if (s.Length == 0) return;
                if (!names.Contains(s, StringComparer.OrdinalIgnoreCase)) names.Add(s);
                foreach (var v in KeyNameVariants(s))
                    if (v.Length > 0 && !names.Contains(v, StringComparer.OrdinalIgnoreCase)) names.Add(v);
            }

            Add(hotkey);
            // 组合键：整串之外，单个主键名也要认（BGI 可能只写主键）
            foreach (var part in hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries)) Add(part);

            foreach (var raw in log.Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                // 只认 baseline 之后新增的行，避免把历史「按下 F12」当成这一次
                var ts = System.Text.RegularExpressions.Regex.Match(line,
                    @"\[(?<d>\d{4}-\d{2}-\d{2}[ T])?(?<t>\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)\]");
                if (ts.Success)
                {
                    string stamp = (ts.Groups["d"].Success ? ts.Groups["d"].Value.Trim() + " "
                                                           : DateTime.Now.ToString("yyyy-MM-dd") + " ")
                                   + ts.Groups["t"].Value;
                    if (DateTime.TryParse(stamp, out var at))
                    {
                        DateTime atLocal = at.Kind == DateTimeKind.Utc ? at.ToLocalTime() : at;
                        if (atLocal < baselineLocal) continue;   // 早于本次发键，不算
                    }
                }

                foreach (var n in names)
                    if (line.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// 按键名的常见写法变体（大小写之外）。
        /// ★ 只做"同义写法"展开，不做模糊猜测 —— 宁可判不出也不能把别的键当成停止键。
        /// </summary>
        internal static IEnumerable<string> KeyNameVariants(string name)
        {
            var list = new List<string>();
            string n = (name ?? "").Trim();
            if (n.Length == 0) return list;

            // NumPad9 / Numpad9 / Keypad9 / Num9 —— BGI 日志里这几种都可能出现
            if (n.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) && n.Length > 6)
            {
                string digit = n.Substring(6);
                list.Add("Numpad" + digit);
                list.Add("Num" + digit);
                list.Add("Keypad" + digit);
                list.Add("小键盘" + digit);
                list.Add("数字小键盘 " + digit);
            }
            // F 12 → F12（中间允许有空格）
            if (n.Length >= 2 && (n[0] == 'F' || n[0] == 'f') && int.TryParse(n.Substring(1), out int fk) && fk >= 1 && fk <= 24)
            {
                list.Add("F" + fk);
                list.Add("F " + fk);
            }
            // 方向键/特殊键的常见别名
            switch (n.ToUpperInvariant())
            {
                case "ESCAPE": list.Add("Esc"); break;
                case "RETURN": list.Add("Enter"); break;
                case "SPACE": list.Add("Space"); break;
                case "PRIOR": list.Add("PageUp"); break;
                case "NEXT": list.Add("PageDown"); break;
            }
            return list;
        }

        /// <summary>BGI 收到停止快捷键后写的原文；与 BgiLogCenter.RxStopped 保持同一套锚点。</summary>
        [GeneratedRegex(@"停止快捷键[^\n]*?按下|停止当前执行任务|停止当前脚本|取消执行配置组")]
        private static partial Regex RxStopAnchor();

        internal static bool HasScriptStopEvidence(string log, DateTime baselineUtc)
        {
            if (string.IsNullOrEmpty(log)) return false;
            foreach (var raw in log.Replace("\r","").Split('\n'))
            {
                var line = raw.Trim();
                if (!ScriptStopMarkers.Any(m => line.Contains(m,StringComparison.OrdinalIgnoreCase))
                    && !RxStopAnchor().IsMatch(line)) continue;
                var m = System.Text.RegularExpressions.Regex.Match(line, @"\[(?<d>\d{4}-\d{2}-\d{2}[ T])?(?<t>\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)\]");
                if (!m.Success) continue;
                // ★★ 3.1.11 修复时区比较 ★★
                //
                //   日志里的 "[2026-11-21 23:30:15.123]" 是**本地时间**（BGI 用 LocalTime 写日志），
                //   而 baselineUtc 存的是 DateTime.UtcNow。原代码写的是：
                //       at.ToUniversalTime() >= baselineUtc
                //   at 虽然是"未指定 Kind"，ToUniversalTime() 按本地时间换算 → 在 UTC+8 下
                //   本地 23:30 变成 UTC 15:30，而 baseline 是 UTC 23:30 左右 —— **永远小于**，
                //   于是任何停止日志都判不出来，"等脚本停止"必然等满 10 秒后超时。
                //   这也解释了为什么这套"按快捷键再等日志"的逻辑从来没真正生效过。
                //
                //   现在统一换算到**本地时间**再比，避免依赖机器时区是否正确。
                // 没有日期前缀时补今天的日期（BGI 通常带日期，兜底用）
                string stamp = (m.Groups["d"].Success ? m.Groups["d"].Value.Trim() + " "
                                                      : DateTime.Now.ToString("yyyy-MM-dd") + " ")
                               + m.Groups["t"].Value;
                if (!DateTime.TryParse(stamp, out var at)) continue;
                DateTime atLocal = at.Kind == DateTimeKind.Utc ? at.ToLocalTime() : at;
                DateTime baseLocal = baselineUtc.Kind == DateTimeKind.Utc
                    ? baselineUtc.ToLocalTime() : baselineUtc;
                if (atLocal >= baseLocal) return true;
            }
            return false;
        }

        /// <summary>
        /// 等「脚本已停」的日志证据。
        ///
        /// ★★ 3.1.11：判据改成「**日志出现这个按键名** 即算按下」，整句锚点只作补充。
        ///   用户原话：「日志只需要出现相同的按键名字就可以判断按下了」。
        /// </summary>
        public static async Task<bool> WaitForScriptStoppedAsync(int sessionId, TimeSpan timeout, CancellationToken ct,
            string hotkey = "")
        {
            // 基准统一到**本地时间**（日志里的时间戳是本地时间）
            DateTime baseLocal = _stopBaselineUtc.Kind == DateTimeKind.Utc
                ? _stopBaselineUtc.ToLocalTime() : _stopBaselineUtc;

            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                ct.ThrowIfCancellationRequested();
                if (BgiLogWatcher.Discover().TryGetValue(sessionId, out var inst) && inst != null)
                {
                    string text = BgiLogWatcher.TailLines(inst.LogFile, 250).text;
                    // ① 按键名出现 → 按下（主判据）
                    if (HasHotkeyPressEvidence(text, baseLocal, hotkey)) return true;
                    // ② 整句停止锚点 → 也算停（兜底，覆盖不写按键名的版本）
                    if (HasScriptStopEvidence(text, _stopBaselineUtc)) return true;
                }
                try { await Task.Delay(250, ct).ConfigureAwait(false); } catch { return false; }
            }
            return false;
        }

        /// <summary>
        /// 按下「停止当前脚本」快捷键 —— **投送通道优先**。
        ///
        /// ★★ 3.1.11 按用户要求重写 ★★
        ///   用户原话：「按键就是通过我们的投送通道模拟按键」「肯定是投送通道优先啊」。
        ///
        ///   两条通道的差别：
        ///     · **投送通道**（RelayClient.KeyAsync → RelayAgent.SendKeyCombo → SendInput）
        ///       —— 用的是 RelayAgent 所在的**目标会话**注入真实按键事件，
        ///          与用户在那个会话里亲手按 F12 等价。
        ///       ★ 实测（2026-11-21，用户用「模拟按键」节点验证）：BGI 能收到。
        ///         BGI 的 cancelTaskHotkeyType 是 KeyboardMonitor，监听的是会话级按键流，
        ///         **与前台窗口是谁无关** —— 前台是原神、BGI 在后台，照样收得到。
        ///         （曾经误以为"必须 BGI 在前台"，那是错的：SendInput 注入的是
        ///           输入事件流，不是往某个 HWND 投递消息。）
        ///       ★ 也**不要**拿 docs/research/HANDOFF.md 里那条
        ///         「SendInput 不会被低级键盘钩子接收」来否定这条路 ——
        ///         那是 SoulLink 主程序自身热键在特定时刻的结论，不适用于 BGI 的
        ///         KeyboardMonitor。现场实测优先于历史推论。
        ///     · **PostMessage 直投**（兜底）
        ///       —— 定向投到 BGI 主窗口，不依赖投送状态；但绕过了输入队列，
        ///          部分场合（程序有模态弹窗、UI 框架拦截）BGI 收不到。
        ///
        ///   所以顺序是：先走投送通道；通道不通/不接受时退回 PostMessage；
        ///   两条都发过还没有日志证据，调用方会按用户要求直接杀进程与游戏。
        ///
        ///   ★ 不做"等通道就绪"：调用点都是启动/更新 BGI 之前，
        ///     那时投送早已建立（用户原话：「谁会一打开就关闭」）。
        ///
        /// 返回 (是否至少发出一次, 实际用了哪条, 说明)
        /// </summary>
        public static async Task<(bool Sent, string Via, string Note)> SendStopHotkeyAsync(
            int sessionId, int pid, string hotkey, CancellationToken ct)
        {
            // ① 投送通道优先
            try
            {
                var relay = RelayClient.FindBySession(sessionId);
                if (relay != null && relay.IsConnected)
                {
                    var r = await relay.KeyAsync(hotkey, 1).ConfigureAwait(false);
                    if (r != null && r.Ok)
                        return (true, "relay", "已通过投送通道发送「" + hotkey + "」");
                    // 通道在但这次没被接受，继续走兜底
                }
            }
            catch { /* 通道异常不中断，走兜底 */ }

            // ② 兜底：PostMessage 直投到 BGI 主窗口（要求真实 pid）
            if (pid > 0)
            {
                try
                {
                    SendHotkeyToSessionPublic(pid, hotkey);
                    return (true, "postmessage", "投送通道不可用，已改用窗口消息投递「" + hotkey + "」");
                }
                catch (Exception ex) { return (false, "none", "两种方式都没能发送快捷键：" + ex.Message); }
            }

            return (false, "none", "投送通道不可用且找不到 BGI 进程，无法发送快捷键");
        }

        /// <summary>
        /// 取该会话里 BetterGI 的进程 Id（找不到返回 0）。
        ///
        /// ★★ 3.1.11：发「停止当前脚本」快捷键必须投到**真实 pid**。★★
        ///   原来 BgiStartGuard 把 sessionId 当 pid 传进 SendHotkeyToSession(pid, ...)，
        ///   而内部 FindMainWindow(pid) 是拿 pid 去 EnumWindows 匹配窗口所属进程的 ——
        ///   sessionId 和 pid 数值毫无关系，永远匹配不到窗口，
        ///   函数在 target == IntPtr.Zero 处**静默 return，快捷键根本没发出去**。
        ///   再叠加"日志判定词缺失"，每次清场必然失败。
        /// </summary>
        public static int FindBgiPid(int sessionId, string exePath)
        {
            try
            {
                string wanted = BgiPathResolver.Normalize(exePath);
                foreach (var p in Process.GetProcessesByName("BetterGI"))
                using (p)
                {
                    try
                    {
                        if (p.SessionId != sessionId) continue;
                        if (wanted.Length == 0) return p.Id;
                        string actual = BgiPathResolver.Normalize(p.MainModule?.FileName ?? "");
                        if (actual.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return p.Id;
                    }
                    catch { }
                }
            }
            catch { }
            return 0;
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

        /// <summary>
        /// 关闭该会话里**属于目标 exePath** 的 BGI 进程，并等它全部退出。
        ///
        /// ★★ 3.1.11：原来这里只按进程名杀（KillAllInSession("BetterGI", sid)），
        ///   而"是否在运行"的判定是**会话 + 真实 exe 路径**双条件
        ///   （HasRunningProcess(sessionId, exePath)）。
        ///   进入条件严、清理条件松 —— 同会话里若还跑着另一个路径的 BetterGI
        ///   （更新页刚解压的新版、旧版残留），会被一并误杀，
        ///   而用户完全看不出是谁杀的。
        ///   现在统一按 (sessionId, exePath) 精确匹配。
        /// </summary>
        public static async Task<bool> CloseProcessesAsync(int sessionId, string exePath, CancellationToken ct)
        {
            try { KillBgiInSession(sessionId, exePath); }
            catch { return false; }
            return await WaitBgiExitAsync(sessionId, exePath, 15000, ct).ConfigureAwait(false);
        }

        /// <summary>结束该会话中匹配 exePath 的 BetterGI（exePath 为空时退化为整会话同名进程）。</summary>
        private static void KillBgiInSession(int sessionId, string exePath)
        {
            string wanted = BgiPathResolver.Normalize(exePath);
            foreach (var p in Process.GetProcessesByName("BetterGI"))
            {
                try
                {
                    if (p.SessionId != sessionId) continue;
                    if (wanted.Length > 0)
                    {
                        string actual;
                        try { actual = BgiPathResolver.Normalize(p.MainModule?.FileName ?? ""); }
                        catch { continue; }
                        if (!actual.Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    try { p.Kill(); } catch { }
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }

        /// <summary>等该会话中匹配 exePath 的 BetterGI 全部消失；超时返回 false。</summary>
        private static async Task<bool> WaitBgiExitAsync(int sessionId, string exePath, int timeoutMs, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (ct.IsCancellationRequested) return false;
                if (!HasRunningProcess(sessionId, exePath)) return true;
                try { await Task.Delay(300, ct).ConfigureAwait(false); } catch { return false; }
            }
            return false;
        }

        public static Task<bool> ConfirmAllExitedAsync(int sessionId, string exePath, TimeSpan timeout, CancellationToken ct)
            => WaitBgiExitAsync(sessionId, exePath, (int)timeout.TotalMilliseconds, ct);

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
