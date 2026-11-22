using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>
    /// 系统优化：可关服务白名单（本次停止，不改启动类型）+ Defender 目录排除。
    ///
    /// ★★ 安全边界（务必遵守）：★★
    ///   1. <b>服务白名单制</b>：只停下方明确列出的 6 项服务，其余一律不碰。
    ///   2. <b>只做「本次停止」</b>：用 sc stop / sc start，绝不调用 sc config 改启动类型，
    ///      重启后全部自动恢复默认，不留下任何永久改动。
    ///   3. <b>停止需要管理员权限</b>：UI 层做权限判断 + 二次确认 + 明确提示影响范围。
    ///   4. <b>Defender 排除只增删目录路径</b>：绝不关闭实时保护、不改其它 Defender 策略。
    ///   5. 本类不引用 System.Management / System.ServiceProcess（单文件发布省体积），
    ///      服务状态走 sc.exe，Defender 走 powershell.exe 内置 cmdlet。
    /// </summary>
    public static class SystemOptimizer
    {
        // ══════════ 服务白名单 ══════════

        public sealed class ServiceEntry
        {
            /// <summary>sc.exe 用的服务名（不是显示名）。</summary>
            public string ServiceName { get; init; } = "";
            /// <summary>中文显示名。</summary>
            public string DisplayName { get; init; } = "";
            /// <summary>为什么可以安全停（一句话，展示给用户）。</summary>
            public string Reason { get; init; } = "";
            /// <summary>停掉后如果按需重启是否安全（true = 系统会自动拉起，可放心停）。</summary>
            public bool RestartsOnDemand { get; init; }
        }

        /// <summary>
        /// 可安全停止的 6 项服务。
        ///
        /// ★ 选入标准：① 不依赖 TermService / RDP / 登录链路；② 停掉不破坏桌面/游戏/挂机；
        ///   ③ 重启即恢复；④ 与挂机游戏机的「内存/磁盘/后台」开销直接相关。
        /// 每一项的「为什么安全」见 Reason。
        /// </summary>
        public static readonly ServiceEntry[] ServiceWhitelist =
        {
            new ServiceEntry { ServiceName = "SysMain",  DisplayName = "内存预取（Superfetch）",        Reason = "挂机时持续预读磁盘，纯浪费 I/O；停掉不影响任何已运行程序。如果经常启动新程序，可能感觉略慢", RestartsOnDemand = false },
            new ServiceEntry { ServiceName = "DiagTrack", DisplayName = "遥测（Connected User Experiences）", Reason = "只上传遥测数据，停掉不影响系统功能",               RestartsOnDemand = false },
            new ServiceEntry { ServiceName = "WSearch",  DisplayName = "Windows 搜索索引",               Reason = "后台持续索引磁盘（44 MB）；停掉后文件管理器搜索会明显变慢（退化为逐文件扫描），开始菜单搜程序不受影响", RestartsOnDemand = false },
            new ServiceEntry { ServiceName = "FontCache",DisplayName = "字体缓存（Font Cache）",          Reason = "字体缓存；停掉后字体枚举变慢（冷启动首次加载时逐文件扫描）。无自动重启，重启系统后恢复", RestartsOnDemand = false },
            new ServiceEntry { ServiceName = "BITS",     DisplayName = "后台智能传输（BITS）",           Reason = "Windows 更新下载用；停掉只是推迟更新下载（按需启动，更新需要时会自动拉起）", RestartsOnDemand = true  },
            new ServiceEntry { ServiceName = "Spooler",  DisplayName = "打印后台（Print Spooler）",      Reason = "挂机用不到打印；停掉更安全（规避打印漏洞）。极少数安装程序会检查此服务，如遇异常请恢复", RestartsOnDemand = false },
        };

        /// <summary>查询某一项服务的当前状态快照。</summary>
        public sealed class ServiceState
        {
            public string ServiceName { get; init; } = "";
            public string DisplayName { get; init; } = "";
            public string Reason { get; init; } = "";
            public bool RestartsOnDemand { get; init; }
            /// <summary>是否正在运行。</summary>
            public bool Running { get; init; }
            /// <summary>状态词（RUNNING / STOPPED / 不存在 / 未知）。</summary>
            public string StateText { get; init; } = "";
        }

        /// <summary>批量查询白名单服务的运行状态。</summary>
        public static List<ServiceState> QueryServices()
        {
            var list = new List<ServiceState>();
            foreach (var e in ServiceWhitelist)
            {
                string state = QueryServiceState(e.ServiceName);
                bool running = string.Equals(state, "RUNNING", StringComparison.OrdinalIgnoreCase);
                list.Add(new ServiceState
                {
                    ServiceName = e.ServiceName,
                    DisplayName = e.DisplayName,
                    Reason = e.Reason,
                    RestartsOnDemand = e.RestartsOnDemand,
                    Running = running,
                    StateText = state.Length == 0 ? "不存在" : state,
                });
            }
            return list;
        }

        /// <summary>停止一个服务（本次停止，不改启动类型）。需要管理员权限。</summary>
        public static async Task<(bool ok, string message)> StopServiceAsync(string serviceName)
        {
            if (!SystemLauncher.IsElevated)
                return (false, "需要管理员权限。请以管理员身份运行 SoulLink 后再停止服务。");

            var (stopOk, stopOut) = await RunAsync("sc.exe", "stop " + serviceName, 60000);
            // sc stop 对「已停止」返回 1062（服务尚未启动），不是失败
            if (!stopOk && !ContainsIgnoreCase(stopOut, "1062"))
            {
                // 服务本来就没在跑时，sc stop 也可能直接失败，此时确认一下状态即可
                if (!string.Equals(QueryServiceState(serviceName), "STOPPED", StringComparison.OrdinalIgnoreCase))
                    return (false, "停止 " + serviceName + " 失败：" + Shorten(stopOut));
            }

            var (stopped, state) = await WaitForServiceStateAsync(serviceName, "STOPPED", 20000);
            if (!stopped)
                return (false, serviceName + " 未能停到 STOPPED（当前 " + state + "）。");
            return (true, "已停止 " + serviceName + "（仅本次，重启后自动恢复）");
        }

        /// <summary>启动（恢复）一个服务。需要管理员权限。</summary>
        public static async Task<(bool ok, string message)> StartServiceAsync(string serviceName)
        {
            if (!SystemLauncher.IsElevated)
                return (false, "需要管理员权限。请以管理员身份运行 SoulLink 后再恢复服务。");

            var (startOk, startOut) = await RunAsync("sc.exe", "start " + serviceName, 60000);
            if (!startOk)
            {
                // 1056 = 已在运行，不是失败
                if (!ContainsIgnoreCase(startOut, "1056"))
                    return (false, "启动 " + serviceName + " 失败：" + Shorten(startOut));
            }

            var (running, state) = await WaitForServiceStateAsync(serviceName, "RUNNING", 20000);
            if (!running)
                return (false, serviceName + " 未能进到 RUNNING（当前 " + state + "）。");
            return (true, "已恢复 " + serviceName);
        }

        // ══════════ Defender 目录排除 ══════════

        /// <summary>读取当前 Defender 的目录排除列表。</summary>
        public static (bool ok, string message, List<string> paths) GetDefenderExclusions()
        {
            // Get-MpPreference 只在 Defender 是活动杀软时可用；
            // 装了第三方杀软（火绒/360 等）时该 cmdlet 可能报错，属于正常情况。
            var (ok, output) = RunPowerShellSync(
                "try { $e=(Get-MpPreference -ErrorAction Stop).ExclusionPath; if($e){$e -join [char]10} ; exit 0 } catch { Write-Error $_; exit 1 }",
                30000);

            var paths = new List<string>();
            if (!ok) return (false, output.Length == 0 ? "无法读取 Defender 排除项（可能未启用 Defender 或装了第三方杀软）" : Shorten(output), paths);

            foreach (var raw in (output ?? "").Split('\n'))
            {
                string p = raw.Trim().TrimEnd('\r');
                if (p.Length > 0) paths.Add(p);
            }
            return (true, "", paths);
        }

        /// <summary>把一个目录加入 Defender 排除。需要管理员权限。</summary>
        public static (bool ok, string message) AddDefenderExclusion(string path)
        {
            path = (path ?? "").Trim().Trim('"');
            if (path.Length == 0) return (false, "目录不能为空。");
            if (!Directory.Exists(path) && !File.Exists(path))
                return (false, "目录不存在：" + path);
            if (!SystemLauncher.IsElevated)
                return (false, "需要管理员权限。请以管理员身份运行 SoulLink 后再添加排除。");

            string esc = path.Replace("'", "''");
            var (ok, output) = RunPowerShellSync(
                "try { Add-MpPreference -ExclusionPath '" + esc + "' -ErrorAction Stop; exit 0 } catch { Write-Error $_; exit 1 }",
                30000);
            return ok
                ? (true, "已加入 Defender 排除：" + path)
                : (false, "加入排除失败：" + Shorten(output));
        }

        /// <summary>把一个目录从 Defender 排除中移除。需要管理员权限。</summary>
        public static (bool ok, string message) RemoveDefenderExclusion(string path)
        {
            path = (path ?? "").Trim().Trim('"');
            if (path.Length == 0) return (false, "目录不能为空。");
            if (!SystemLauncher.IsElevated)
                return (false, "需要管理员权限。请以管理员身份运行 SoulLink 后再移除排除。");

            string esc = path.Replace("'", "''");
            var (ok, output) = RunPowerShellSync(
                "try { Remove-MpPreference -ExclusionPath '" + esc + "' -ErrorAction Stop; exit 0 } catch { Write-Error $_; exit 1 }",
                30000);
            return ok
                ? (true, "已移除 Defender 排除：" + path)
                : (false, "移除排除失败：" + Shorten(output));
        }

        // ══════════ 底层：sc.exe / powershell ══════════

        /// <summary>读取服务当前状态词，例如 RUNNING / STOPPED / STOP_PENDING。</summary>
        private static string QueryServiceState(string serviceName)
        {
            try
            {
                var psi = new ProcessStartInfo("sc.exe", "query " + serviceName)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = SystemLauncher.ConsoleEncoding(),
                };
                using var p = Process.Start(psi);
                if (p == null) return "";

                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);

                foreach (var line in o.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string t = line.Trim();
                    if (!t.StartsWith("STATE", StringComparison.OrdinalIgnoreCase)) continue;
                    int colon = t.IndexOf(':');
                    if (colon < 0) continue;
                    var parts = t.Substring(colon + 1).Trim()
                                 .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2) return parts[1].ToUpperInvariant();
                    if (parts.Length == 1) return parts[0].ToUpperInvariant();
                }
                return "";
            }
            catch { return ""; }
        }

        private static async Task<(bool ok, string state)> WaitForServiceStateAsync(
            string serviceName, string wanted, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            string last = "";
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                last = QueryServiceState(serviceName);
                if (string.Equals(last, wanted, StringComparison.OrdinalIgnoreCase))
                    return (true, last);
                if (last.Length == 0) return (false, "服务不存在");
                await Task.Delay(400).ConfigureAwait(false);
            }
            return (false, last + "（等待 " + (timeoutMs / 1000) + " 秒）");
        }

        private static async Task<(bool ok, string output)> RunAsync(string file, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = SystemLauncher.ConsoleEncoding(),
                    StandardErrorEncoding = SystemLauncher.ConsoleEncoding(),
                };
                using var p = Process.Start(psi);
                if (p == null) return (false, "进程启动失败");

                string o = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string e = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);

                using var cts = new CancellationTokenSource(timeoutMs);
                try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    try { p.Kill(true); } catch { }
                    return (false, "命令超时");
                }
                return (p.ExitCode == 0, (o + "\n" + e).Trim());
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        private static (bool ok, string output) RunPowerShellSync(string script, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add("$OutputEncoding=[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + script);

                using var p = Process.Start(psi);
                if (p == null) return (false, "PowerShell 启动失败");

                string o = p.StandardOutput.ReadToEnd();
                string e = p.StandardError.ReadToEnd();

                // 同步等待 + 超时兜底（Process 的同步 WaitForExit 只有 int 毫秒重载）
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(true); } catch { }
                    return (false, "PowerShell 超时");
                }
                return (p.ExitCode == 0, (o + "\n" + e).Trim());
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        private static bool ContainsIgnoreCase(string? s, string needle)
            => s != null && s.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private static string Shorten(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "（无输出）";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 240 ? s.Substring(0, 240) + "…" : s;
        }
    }
}
