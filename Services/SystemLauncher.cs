using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>
    /// 跨会话进程投送（SYSTEM 通道，免安装、无外部依赖）。
    ///
    /// 为什么必须走真 SYSTEM（HANDOFF 3.2，实测结论，别再试别的）：
    ///   - 管理员令牌 + WTSQueryUserToken            → 1314（不是 SYSTEM 就不行）
    ///   - 管理员 + 窃取 SYSTEM 令牌 + 冒充           → 1314
    ///   - CreateProcessWithTokenW                    → 假成功，进程落在调用者会话
    ///   - 真 SYSTEM（服务或计划任务）+ CreateProcessAsUser → ✅ 26ms，落点正确
    ///
    /// 所以这里的做法是：
    ///   1. 管理员进程拿到 SeDebugPrivilege，从会话 0 的 winlogon 偷一个 SYSTEM 令牌；
    ///   2. 用 schtasks /ru SYSTEM 拉起"引导进程"（免安装，用完即删，无服务残留）；
    ///   3. 引导进程自己是 SYSTEM，这时 WTSQueryUserToken 才合法，
    ///      再 CreateProcessAsUser 把目标程序投进目标会话的桌面。
    ///
    /// 引导进程就是 SoulLink.exe 自己（--system-run 模式），不需要额外打包任何东西。
    /// </summary>
    public static class SystemLauncher
    {
        // ── 公共结果 ─────────────────────────────────────────────────────────

        public sealed class Result
        {
            public bool Ok { get; set; }
            public int Pid { get; set; }
            public string Message { get; set; } = "";
            public long ElapsedMs { get; set; }
            public bool SessionMismatch { get; set; }

            public static Result Fail(string msg) => new Result { Ok = false, Message = msg };
        }

        /// <summary>投送请求（写进临时 job 文件，交给 SYSTEM 引导进程读）。</summary>
        public sealed class Job
        {
            public int SessionId { get; set; }
            public string ExePath { get; set; } = "";
            public string Arguments { get; set; } = "";
            public string WorkingDir { get; set; } = "";
            /// <summary>true = 在用户交互桌面（winsta0\default）上启动，false = 后台</summary>
            public bool Interactive { get; set; } = true;
            public string ResultPath { get; set; } = "";
            public bool RequireElevated { get; set; } = true;
            public string Nonce { get; set; } = "";

            /// <summary>
            /// 追加给目标进程的环境变量（会覆盖同名项）。
            /// ★ 3.2.8：录制回放要写主程序目录，而代理运行在 Program Files，
            ///   自己推不出主程序位置，必须由投送方显式告知。
            /// </summary>
            public Dictionary<string, string> Env { get; set; } = new();
        }

        // ── 特权 ─────────────────────────────────────────────────────────────

        private static bool _privsReady;

        /// <summary>启用当前进程的特权（管理员才能成功）。</summary>
        public static void EnablePrivileges()
        {
            if (_privsReady) return;
            foreach (var name in new[]
            {
                "SeDebugPrivilege", "SeAssignPrimaryTokenPrivilege",
                "SeIncreaseQuotaPrivilege", "SeTcbPrivilege", "SeBackupPrivilege"
            })
            {
                TryEnable(name);
            }
            _privsReady = true;
        }

        private static void TryEnable(string privilege)
        {
            IntPtr hToken = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out hToken))
                    return;
                if (!LookupPrivilegeValue(null, privilege, out LUID luid)) return;

                var tp = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED
                };
                AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
            finally { if (hToken != IntPtr.Zero) CloseHandle(hToken); }
        }

        public static bool IsElevated
        {
            get
            {
                try
                {
                    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out IntPtr h)) return false;
                    try
                    {
                        var elevation = new TOKEN_ELEVATION();
                        int size = Marshal.SizeOf(elevation);
                        IntPtr p = Marshal.AllocHGlobal(size);
                        try
                        {
                            if (GetTokenInformation(h, TOKEN_INFORMATION_CLASS.TokenElevation, p, size, out int ret))
                                return Marshal.PtrToStructure<TOKEN_ELEVATION>(p).TokenIsElevated != 0;
                        }
                        finally { Marshal.FreeHGlobal(p); }
                    }
                    finally { CloseHandle(h); }
                }
                catch { }
                return false;
            }
        }

        // ── 投送 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 把 exePath 投进 sessionId 对应的用户桌面。全程免安装，用完不留痕。
        /// </summary>
        /// <param name="bootstrapExe">
        /// 承担 SYSTEM 引导身份的 SoulLink.exe 路径。
        /// ★ 必须显式传入：Environment.ProcessPath 在别的宿主（测试程序、命令行工具）里会指向那个程序，
        ///   而不是 SoulLink.exe，导致引导进程根本不是我们的 --system-run 实现。
        /// </param>
        /// <summary>
        /// 投送串行化闸门。
        ///
        /// ★ 必须串行：每次投送都会创建/运行/删除一个 SYSTEM 计划任务，
        ///   多个账号同时投送时 schtasks 会并发操作同一批任务名与临时文件，
        ///   表现为互相阻塞甚至整体卡死（用户实测：多账号自动投送时界面冻住）。
        ///   这里用信号量保证同一时刻只有一个投送在进行。
        /// </summary>
        private static readonly System.Threading.SemaphoreSlim LaunchGate =
            new System.Threading.SemaphoreSlim(1, 1);

        public static async Task<Result> LaunchAsync(int sessionId, string exePath, string arguments,
            string workingDir = "", bool interactive = true, string? bootstrapExe = null,
            CancellationToken ct = default, Dictionary<string, string>? env = null)
        {
            if (AccountScope.OnlyAutomationAccounts())
            {
                var sessions = await SessionService.QueryAsync().ConfigureAwait(false);
                var target = sessions.FirstOrDefault(s => s.Id == sessionId);
                if (target != null && !AccountScope.Allows(target.UserName)) return Result.Fail("主用户已被全局挂机账号模式排除");
            }
            if (Path.GetFileName(exePath).Equals("BetterGI.exe", StringComparison.OrdinalIgnoreCase))
            {
                var guard = await BgiStartGuard.BeforeStartAsync(sessionId, exePath, ct).ConfigureAwait(false);
                if (!guard.Ok) return Result.Fail(guard.Message);
            }
            await LaunchGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await LaunchCoreAsync(sessionId, exePath, arguments, workingDir,
                    interactive, bootstrapExe, ct, env).ConfigureAwait(false);
            }
            finally { LaunchGate.Release(); }
        }

        private static async Task<Result> LaunchCoreAsync(int sessionId, string exePath, string arguments,
            string workingDir, bool interactive, string? bootstrapExe, CancellationToken ct,
            Dictionary<string, string>? env = null)
        {
            var sw = Stopwatch.StartNew();

            if (!IsElevated)
                return Result.Fail("需要管理员权限。请右键 SoulLink.exe →「以管理员身份运行」。");

            if (sessionId <= 0)
                return Result.Fail("无效的会话 ID：" + sessionId);

            exePath = (exePath ?? "").Trim().Trim('"');
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                return Result.Fail("目标程序不存在：" + exePath);

            // 1) 准备 job 文件
            string stamp = DateTime.Now.ToString("yyyyMMddHHmmssfff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string dir = Path.Combine(AppIdentity.TempRoot, "jobs");
            Directory.CreateDirectory(dir);

            string jobPath = Path.Combine(dir, "job_" + stamp + ".json");
            string resultPath = Path.Combine(dir, "res_" + stamp + ".json");

            var job = new Job
            {
                SessionId = sessionId,
                ExePath = exePath,
                Arguments = arguments ?? "",
                WorkingDir = string.IsNullOrWhiteSpace(workingDir)
                    ? (Path.GetDirectoryName(exePath) ?? "")
                    : workingDir,
                Interactive = interactive,
                RequireElevated = true,
                ResultPath = resultPath,
                Env = env ?? new Dictionary<string, string>()
            };

            try
            {
                File.WriteAllText(jobPath,
                    JsonSerializer.Serialize(job, new JsonSerializerOptions { WriteIndented = false }),
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                return Result.Fail("无法写入 job 文件：" + ex.Message);
            }

            // 2) 用一个一次性的计划任务把它拉成 SYSTEM
            string taskName = AppIdentity.TaskPrefix + Guid.NewGuid().ToString("N").Substring(0, 12);
            bool taskCreated = false;
            try
            {
                // /st 必须给一个"还没到"的时间，否则 schtasks 会拒绝排程。
                // 我们用 /run 手动触发，这个时间只是占位，取 10 分钟后最稳。
                string st = DateTime.Now.AddMinutes(10).ToString("HH:mm");
                string bootCmd;
                try { bootCmd = BuildBootstrapCmd(jobPath, bootstrapExe); }
                catch (Exception ex) { return Result.Fail(ex.Message); }

                var (createOk, createMsg) = await RunToolAsync("schtasks.exe",
                    "/create /tn " + Q(taskName) + " /tr " + Q(bootCmd) +
                    " /sc once /st " + st + " /ru SYSTEM /rl HIGHEST /f", 30000, ct).ConfigureAwait(false);

                if (!createOk)
                {
                    TryDelete(resultPath);
                    TryDelete(jobPath);
                    return Result.Fail("创建 SYSTEM 计划任务失败：" + Shorten(createMsg));
                }
                taskCreated = true;

                var (runOk, runMsg) = await RunToolAsync("schtasks.exe",
                    "/run /tn " + Q(taskName), 15000, ct).ConfigureAwait(false);
                if (!runOk)
                {
                    return Result.Fail("启动 SYSTEM 计划任务失败：" + Shorten(runMsg));
                }

                // 3) 等引导进程写回结果
                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(120, ct).ConfigureAwait(false);
                    if (!File.Exists(resultPath)) continue;

                    Job? res = null;
                    try
                    {
                        // 引导进程可能还在写，读失败就下一轮再试
                        string json = File.ReadAllText(resultPath, Encoding.UTF8);
                        res = JsonSerializer.Deserialize<Job>(json);
                    }
                    catch { continue; }

                    if (res == null) continue;

                    // 约定：Arguments 字段回填 "OK|<pid>|说明"，ExePath 回填错误文本
                    string arg = res.Arguments ?? "";
                    if (arg.StartsWith("OK|", StringComparison.Ordinal))
                    {
                        var parts = arg.Split('|');
                        int pid = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : 0;
                        string note = parts.Length > 2 ? parts[2] : "";
                        sw.Stop();
                        return new Result { Ok = true, Pid = pid, Message = note, ElapsedMs = sw.ElapsedMilliseconds };
                    }

                    sw.Stop();
                    return new Result
                    {
                        Ok = false,
                        Message = string.IsNullOrWhiteSpace(res.ExePath) ? "SYSTEM 引导进程报告失败（无详情）" : res.ExePath,
                        ElapsedMs = sw.ElapsedMilliseconds
                    };
                }

                sw.Stop();
                return new Result
                {
                    Ok = false,
                    Message = "等待 SYSTEM 引导进程超时（45 秒）。可能是杀软拦截了计划任务，或 schtasks 被策略限制。",
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }
            finally
            {
                if (taskCreated)
                {
                    try
                    {
                        await RunToolAsync("schtasks.exe", "/delete /tn " + Q(taskName) + " /f", 10000, ct)
                            .ConfigureAwait(false);
                    }
                    catch { }
                }
                TryDelete(resultPath);
                TryDelete(jobPath);
            }
        }

        /// <summary>引导命令行：无 UAC 清单的宿主 --system-run &lt;job 文件&gt;</summary>
        private static string BuildBootstrapCmd(string jobPath, string? bootstrapExe)
        {
            string self = !string.IsNullOrWhiteSpace(bootstrapExe)
                ? bootstrapExe!
                : ResolveSelfExe();

            if (string.IsNullOrEmpty(self) || !File.Exists(self))
                throw new InvalidOperationException("找不到 SoulLink.exe 本体，无法建立 SYSTEM 引导：" + self);

            // 引导进程就是本程序：它以 SYSTEM 身份运行，只需要 WTSQueryUserToken，
            // 不会触发 UAC。真正会被 740 拦住的是它随后用普通用户令牌启动的子进程，
            // 所以那个子进程必须使用不带管理员清单的代理本体（见 ProxyHost）。
            return Q(self) + " --system-run " + Q(jobPath);
        }

        /// <summary>
        /// 生成/复用不含 requireAdministrator 清单的引导宿主。
        /// 引导进程只做 WTSQueryUserToken + CreateProcessAsUser，本来就以 SYSTEM 运行，
        /// 不需要也不会触发 UAC。
        /// </summary>
        private static string EnsureBootstrapHost(string self)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(self) + ".bootstraphost.exe";
                string dir = Path.Combine(AppIdentity.TempRoot, "boot");
                Directory.CreateDirectory(dir);
                string host = Path.Combine(dir, name);

                var src = new FileInfo(self);
                var dst = new FileInfo(host);
                if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc >= src.LastWriteTimeUtc)
                    return host;

                // 换掉嵌入清单：把 requestedExecutionLevel 改成 asInvoker。
                string manifestPath = Path.Combine(dir, "bootstraphost.manifest");
                File.WriteAllText(manifestPath, BootstrapManifest, new UTF8Encoding(false));

                if (dst.Exists) { try { dst.IsReadOnly = false; dst.Delete(); } catch { } }

                string mt = FindMtExe();
                if (mt.Length > 0)
                {
                    var psi = new ProcessStartInfo(mt,
                        "-nologo -manifest " + Q(manifestPath) + " -outputresource:" + Q(host) + ";#1 " + Q(self))
                    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                    using var p = Process.Start(psi);
                    p?.StandardOutput.ReadToEnd();
                    p?.StandardError.ReadToEnd();
                    p?.WaitForExit(30000);
                }

                if (!File.Exists(host) || new FileInfo(host).Length != src.Length)
                {
                    File.Copy(self, host, true);
                    // 没有 mt.exe 时直接改 PE 内嵌清单；失败则不能用作 SYSTEM 引导（会 740）。
                    if (!ManifestPatcher.MakeAsInvoker(host)) throw new InvalidOperationException("引导宿主清单改写失败");
                }
                return host;
            }
            catch
            {
                return self;
            }
        }

        private static string FindMtExe()
        {
            try
            {
                // Windows SDK 的 mt.exe 不一定装了；找不到就退回复制（复制后仍是原清单）
                string[] roots =
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "bin"),
                };
                foreach (var root in roots)
                {
                    if (!Directory.Exists(root)) continue;
                    var hit = Directory.GetFiles(root, "mt.exe", SearchOption.AllDirectories)
                        .OrderByDescending(f => f).FirstOrDefault();
                    if (!string.IsNullOrEmpty(hit) && File.Exists(hit)) return hit;
                }
            }
            catch { }
            return "";
        }

        private static readonly string BootstrapManifest = string.Join(Environment.NewLine, new[]
        {
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
            "<assembly manifestVersion=\"1.0\" xmlns=\"urn:schemas-microsoft-com:asm.v1\">",
            "  <assemblyIdentity version=\"1.0.0.0\" name=\"SoulLink.bootstrap\" />",
            "  <trustInfo xmlns=\"urn:schemas-microsoft-com:asm.v2\">",
            "    <security>",
            "      <requestedPrivileges xmlns=\"urn:schemas-microsoft-com:asm.v3\">",
            "        <requestedExecutionLevel level=\"asInvoker\" uiAccess=\"false\" />",
            "      </requestedPrivileges>",
            "    </security>",
            "  </trustInfo>",
            "</assembly>"
        });

        /// <summary>
        /// 定位自身 exe：优先当前进程。
        /// ★ 不能只认 "SoulLink" 前缀：挂机验证版叫 SoulLink-Stable.exe，
        ///   写死前缀会退回错误的 exe，导致 SYSTEM 引导进程身份错乱。
        /// </summary>
        public static string ResolveSelfExe()
        {
            string path = Environment.ProcessPath ?? "";
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                string file = Path.GetFileNameWithoutExtension(path);
                // 认当前身份名，也兼容任何 SoulLink* 变体
                if (string.Equals(file, AppIdentity.Name, StringComparison.OrdinalIgnoreCase) ||
                    file.StartsWith("SoulLink", StringComparison.OrdinalIgnoreCase))
                    return path;
            }

            // 被别的宿主（测试程序）调用时，退回当前身份对应的产物位置
            string baseDir = AppContext.BaseDirectory;
            string candidate = Path.Combine(baseDir, AppIdentity.Name + ".exe");
            if (File.Exists(candidate)) return candidate;

            try
            {
                var self = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (File.Exists(self)) return self;
            }
            catch { }

            return path;
        }

        public static Result LaunchAdminAgentFromBroker(int sessionId, string pipeName, string nonce,
            Dictionary<string, string>? env = null)
        {
            string exe = BrokerInstall.ServiceExe, agent = BrokerInstall.AgentExe;
            if (!IsElevated || !BrokerInstall.IsFixedTestPath(ResolveSelfExe()) || !BrokerInstall.ValidAgent(exe, agent)) return Result.Fail("Broker 身份或固定代理路径无效");
            if (BrokerSecurity.InspectProcess(Environment.ProcessId).Sid != "S-1-5-18") return Result.Fail("Broker 不是 LocalSystem");

            // WTSQueryUserToken / SetTokenInformation(TokenSessionId) 都需要 SeTcbPrivilege，
            // 服务启动的 SYSTEM 令牌里这些特权默认是关闭的，这里显式打开再投送。
            EnablePrivileges();

            // ★ 必须把代理参数带全：--relay <管道> 决定代理连回哪条管道，
            //   --require-elevated + --deploy-nonce 决定代理是否做管理员/会话/桌面自检。
            //   以前这里没有带参数，代理就以宿主模式启动、根本不连管道，
            //   即使认证通过也只会"启动了但没连回来"。
            if (!BrokerProtocol.ValidPipeName(pipeName)) return Result.Fail("代理管道名不合法：" + pipeName);
            string arguments = "--relay " + pipeName;
            if (!string.IsNullOrWhiteSpace(nonce)) arguments = BrokerArguments.AddElevated(arguments, nonce);

            var job = new Job
            {
                SessionId = sessionId,
                ExePath = agent,
                Arguments = arguments,
                RequireElevated = true,
                Nonce = nonce,
                WorkingDir = BrokerInstall.Root,
                Interactive = false,
                Env = env ?? new Dictionary<string, string>()
            };
            var r = LaunchAsUser(job); return new Result { Ok = r.ok, Pid = r.pid, Message = r.message };
        }

        /// <summary>
        /// 目标会话是否在线：返回 null 表示 Active 且用户匹配；否则返回可直接展示的失败原因。
        ///
        /// ★ 语义以 Windows 会话状态为准（WTSQuerySessionInformation）：
        ///   A2/A3 断开（Disc）时不应该投送——代理会落在没人看的桌面上。
        ///   查询本身失败时按“在线”处理，避免把平台差异当成离线而误拒。
        /// </summary>
        public static string? DescribeSessionOnline(int sessionId, string userName)
        {
            if (sessionId <= 0) return "无效的目标会话";
            string expected = SessionService.BareName(userName ?? "");
            int state = WtsSessionState(sessionId);
            string hookUser = WtsUserName(sessionId);
            string stateText = state switch
            {
                WTS_ACTIVE => "在线",
                WTS_CONNECTED => "已连接但未登录",
                WTS_IDLE => "空闲",
                3 => "正在连接",
                WTS_DISCONNECTED => "断开",
                5 => "正在初始化",
                6 => "正在注销",
                _ => "状态 " + state
            };

            if (state < 0)
            {
                // WTS 查询失败（少见）：退回 query user，不因为查询问题一刀切拒绝。
                var listed = SessionService.QueryAsync().GetAwaiter().GetResult().FirstOrDefault(s => s.Id == sessionId);
                if (listed == null) return "会话 " + sessionId + " 不存在或已注销，需要先登录该账号";
                if (string.Equals(listed.State, "Active", StringComparison.OrdinalIgnoreCase)) return null;
                return "会话 " + sessionId + " 当前不在线（" + listed.State + "），需要先连上才能投送";
            }
            if (state == WTS_DISCONNECTED) return "会话 " + sessionId + " 已断开（Disc），需要先用 RDP 连上才能投送";
            if (state != WTS_ACTIVE && state != WTS_CONNECTED) return "会话 " + sessionId + " 当前" + stateText + "，需要在线才能投送";
            if (hookUser.Length > 0 && expected.Length > 0
                && !string.Equals(SessionService.BareName(hookUser), expected, StringComparison.OrdinalIgnoreCase))
                return "会话 " + sessionId + " 当前登录用户是 " + hookUser + "，不是 " + expected;
            return null;
        }

        private const int WTS_ACTIVE = 0;
        private const int WTS_CONNECTED = 1;
        private const int WTS_IDLE = 2;
        private const int WTS_DISCONNECTED = 4;
        private const int WTS_INFO_STATE = 8;
        private const int WTS_INFO_USERNAME = 5;

        private static int WtsSessionState(int sessionId)
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, WTS_INFO_STATE, out buffer, out int bytes) || buffer == IntPtr.Zero)
                    return -1;
                return Marshal.ReadInt32(buffer);
            }
            catch { return -1; }
            finally { if (buffer != IntPtr.Zero) WTSFreeMemory(buffer); }
        }

        private static string WtsUserName(int sessionId)
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, WTS_INFO_USERNAME, out buffer, out int bytes) || buffer == IntPtr.Zero)
                    return "";
                return Marshal.PtrToStringUni(buffer) ?? "";
            }
            catch { return ""; }
            finally { if (buffer != IntPtr.Zero) WTSFreeMemory(buffer); }
        }

        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);
        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr memory);

        // ── 引导进程（以 SYSTEM 运行） ───────────────────────────────────────

        /// <summary>
        /// 由计划任务以 SYSTEM 启动。此时代码运行在 SYSTEM 令牌下，
        /// WTSQueryUserToken 才合法，也才能 CreateProcessAsUser 到别人的会话。
        /// </summary>
        public static int RunBootstrap(string[] args)
        {
            // args[0] = --system-run, args[1] = job 文件
            if (args.Length < 2) return 2;

            string jobPath = args[1];
            Job? job = null;
            try
            {
                job = JsonSerializer.Deserialize<Job>(File.ReadAllText(jobPath, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                // 读不到 job 就没法知道 ResultPath，只能写给附近一个固定名字，方便排查
                string fallback = Path.Combine(Path.GetDirectoryName(jobPath) ?? Path.GetTempPath(),
                    "res_" + Path.GetFileNameWithoutExtension(jobPath) + ".fail.json");
                WriteResult(fallback, "读取 job 失败：" + ex.Message, -1);
                return 3;
            }
            if (job == null) return 4;

            try
            {
                var (ok, pid, message) = LaunchAsUser(job);
                WriteResult(job.ResultPath, ok ? ("OK|" + pid + "|" + message) : message, pid);
                return ok ? 0 : 5;
            }
            catch (Exception ex)
            {
                WriteResult(job.ResultPath, "SYSTEM 引导异常：" + ex.Message, -1);
                return 6;
            }
        }

        private static void WriteResult(string path, string payload, int pid)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                // 复用 Job 结构传结果：Arguments = 状态串，ExePath = 错误文本
                var res = new Job();
                if (payload.StartsWith("OK|", StringComparison.Ordinal)) res.Arguments = payload;
                else res.ExePath = payload;
                res.SessionId = pid;

                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(res), new UTF8Encoding(false));
                try { File.Move(tmp, path, true); } catch { File.Copy(tmp, path, true); }
            }
            catch { }
        }

        /// <summary>
        /// 取该用户令牌的“提权副本”。
        /// · UAC 关闭：登录令牌本身已是完整管理员令牌，直接返回 0（沿用原令牌）。
        /// · UAC 开启：登录令牌是 TokenElevationTypeLimited，用 TokenLinkedToken 取管理员令牌。
        /// 取不到（例如该用户不是管理员）返回 0，调用方按标准令牌继续，不报错。
        /// </summary>
        private static IntPtr TryGetElevatedToken(IntPtr token)
        {
            try
            {
                int size = Marshal.SizeOf<int>();
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    if (!GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenElevationType, buf, size, out _))
                        return IntPtr.Zero;
                    int type = Marshal.ReadInt32(buf);
                    // TokenElevationTypeLimited=3 才查询 linked elevated token。
                    if (type != 3) return IntPtr.Zero;
                }
                finally { Marshal.FreeHGlobal(buf); }

                if (!GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenLinkedToken,
                        out IntPtr linked, IntPtr.Size, out _)) return IntPtr.Zero;
                return linked;
            }
            catch { return IntPtr.Zero; }
        }

        /// <summary>
        /// 在已有的 ENVIRONMENT_BLOCK 上追加/覆盖变量。
        /// 环境块格式：一串 "NAME=VALUE\0"，以双 \0 结尾。
        /// </summary>
        private static IntPtr MergeEnvironment(IntPtr src, Dictionary<string, string> extra)
        {
            var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (src != IntPtr.Zero)
                {
                    IntPtr p = src;
                    while (true)
                    {
                        string? s = Marshal.PtrToStringUni(p);
                        if (string.IsNullOrEmpty(s)) break;
                        int eq = s.IndexOf('=');
                        if (eq > 0) items[s.Substring(0, eq)] = s.Substring(eq + 1);
                        p = IntPtr.Add(p, (s.Length + 1) * 2);
                    }
                }
            }
            catch { }

            foreach (var kv in extra) items[kv.Key] = kv.Value;

            var sb = new System.Text.StringBuilder();
            foreach (var kv in items) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
            sb.Append('\0');

            // 转成 UTF-16 双零结尾的块
            string block = sb.ToString();
            IntPtr mem = Marshal.AllocHGlobal((block.Length + 1) * 2);
            var chars = block.ToCharArray();
            Marshal.Copy(chars, 0, mem, chars.Length);
            Marshal.WriteInt16(mem, chars.Length * 2, 0);

            if (src != IntPtr.Zero) { try { DestroyEnvironmentBlock(src); } catch { } }
            return mem;
        }

        /// <summary>核心：以目标会话用户身份创建进程。</summary>
        private static (bool ok, int pid, string message) LaunchAsUser(Job job)
        {
            IntPtr userToken = IntPtr.Zero;
            IntPtr primary = IntPtr.Zero;
            IntPtr env = IntPtr.Zero;
            IntPtr cmdLine = IntPtr.Zero;
            IntPtr workDir = IntPtr.Zero;
            IntPtr desktop = IntPtr.Zero;

            try
            {
                // ① 拿目标会话的用户令牌 —— 只有 SYSTEM 调用才会成功
                if (!WTSQueryUserToken((uint)job.SessionId, out userToken))
                {
                    int err = Marshal.GetLastWin32Error();
                    return (false, 0,
                        "WTSQueryUserToken 失败（错误 " + err + "）。会话 " + job.SessionId + " 可能没有已登录的用户。");
                }

                // ② 优先取“提权令牌”：UAC 开启时，登录令牌是标准令牌，
                //    它的 linked token 才是管理员令牌。这样即使 UAC 开着，
                //    启动的目标程序也能拿到管理员权限。
                // ★ 只有 UAC 开启时才有 linked token。EnableLUA=0 的机器上，
                //   会话令牌本身已是完整管理员令牌（ElevationTypeDefault），
                //   以前要求"必须拿到 linked token"会把每次投送都拒掉。
                //   现在改为：拿得到 linked token 就用它，拿不到就校验登录令牌本身；
                //   校验不通过仍然拒绝，管理员强制要求没有放宽。
                IntPtr elevated = TryGetElevatedToken(userToken);
                IntPtr source = elevated != IntPtr.Zero ? elevated : userToken;
                if (job.RequireElevated)
                {
                    var sourceInfo = BrokerSecurity.InspectToken(source);
                    if (!sourceInfo.Elevated || !sourceInfo.Administrator)
                    {
                        if (elevated != IntPtr.Zero) CloseHandle(elevated);
                        return (false, 0, "目标会话令牌不具备管理员权限（" + sourceInfo.User
                            + " elevated=" + sourceInfo.Elevated + " admin=" + sourceInfo.Administrator
                            + "）；已拒绝启动，未降级。");
                    }
                }

                if (!DuplicateTokenEx(source, TOKEN_ALL_ACCESS, IntPtr.Zero,
                        SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation, TOKEN_TYPE.TokenPrimary, out primary))
                {
                    int dupErr = Marshal.GetLastWin32Error();
                    if (elevated != IntPtr.Zero) CloseHandle(elevated);
                    return (false, 0, "DuplicateTokenEx 失败（错误 " + dupErr + "）。");
                }
                if (elevated != IntPtr.Zero) CloseHandle(elevated);
                if (job.RequireElevated)
                {
                    var primaryInfo = BrokerSecurity.InspectToken(primary);
                    if (!primaryInfo.Elevated || !primaryInfo.Administrator) return (false, 0, "目标程序令牌未通过管理员校验");
                }
                if (job.RequireElevated)
                {
                    int sid = job.SessionId;
                    if (!SetTokenInformation(primary, TokenSessionId, ref sid, sizeof(int))) return (false, 0, "无法固定目标会话");
                }

                // ③ 用户环境变量（不建的话会继承 SYSTEM 的 PATH / TEMP，程序可能找不到依赖）
                if (!CreateEnvironmentBlock(out env, primary, false)) return (false, 0, "无法创建用户环境");

                // ★ 3.2.8：把调用方指定的环境变量并进去（如录制回放目录）。
                //   CreateEnvironmentBlock 只给用户默认环境，需要额外追加。
                if (job.Env.Count > 0)
                {
                    try { env = MergeEnvironment(env, job.Env); }
                    catch { /* 合并失败不阻断启动，代理会回退到自己推导路径 */ }
                }

                // ④ 组装命令行
                string cmd = QuoteArg(job.ExePath);
                if (!string.IsNullOrWhiteSpace(job.Arguments)) cmd += " " + job.Arguments.Trim();

                cmdLine = Marshal.StringToHGlobalUni(cmd);
                if (!string.IsNullOrWhiteSpace(job.WorkingDir)) workDir = Marshal.StringToHGlobalUni(job.WorkingDir);
                desktop = Marshal.StringToHGlobalUni(@"winsta0\default");

                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf<STARTUPINFO>();
                // 交互式：让程序显示在目标用户桌面上；否则不给桌面
                si.lpDesktop = desktop;

                var flags = CREATE_UNICODE_ENVIRONMENT;
                if (job.Interactive) flags |= CREATE_NEW_CONSOLE;

                bool ok = CreateProcessAsUser(primary,
                    null, cmdLine,
                    IntPtr.Zero, IntPtr.Zero,
                    false,
                    flags,
                    env,
                    workDir,
                    ref si,
                    out PROCESS_INFORMATION pi);

                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    string hint = err switch
                    {
                        1314 => "（1314 = 缺少权限，说明当前不是真 SYSTEM）",
                        5 => "（拒绝访问）",
                        2 => "（找不到文件）",
                        267 => "（目标路径无效）",
                        _ => ""
                    };
                    return (false, 0, "CreateProcessAsUser 失败，错误 " + err + hint);
                }

                int newPid = pi.dwProcessId;
                CloseHandle(pi.hThread);
                if (job.RequireElevated)
                {
                    try
                    {
                        var target = BrokerSecurity.InspectProcess(newPid);
                        if (target.SessionId != job.SessionId || !target.Elevated || !target.Administrator)
                        { TerminateProcess(pi.hProcess, 5); CloseHandle(pi.hProcess); return (false, newPid, "目标程序未通过管理员、会话或身份校验，已终止。"); }
                    }
                    catch (Exception ex)
                    { TerminateProcess(pi.hProcess, 5); CloseHandle(pi.hProcess); return (false, newPid, "目标程序身份校验失败，已终止：" + ex.Message); }
                }
                CloseHandle(pi.hProcess);

                // ⑤ 复核落点：进程真的在目标会话里吗？
                try
                {
                    using var p = Process.GetProcessById(newPid);
                    if (p.SessionId != job.SessionId)
                    {
                        return (true, newPid,
                            "已启动 PID " + newPid + "，但落点是会话 " + p.SessionId +
                            "，不是目标会话 " + job.SessionId);
                    }
                    return (true, newPid, "已投送到会话 " + job.SessionId + "，PID " + newPid);
                }
                catch
                {
                    return (true, newPid, "已投送 PID " + newPid);
                }
            }
            finally
            {
                if (userToken != IntPtr.Zero) CloseHandle(userToken);
                if (primary != IntPtr.Zero) CloseHandle(primary);
                if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
                if (cmdLine != IntPtr.Zero) Marshal.FreeHGlobal(cmdLine);
                if (workDir != IntPtr.Zero) Marshal.FreeHGlobal(workDir);
                if (desktop != IntPtr.Zero) Marshal.FreeHGlobal(desktop);
            }
        }

        // ── 结束进程（管理员 + SeDebugPrivilege 即可跨会话） ─────────────────

        public static (bool ok, string message) Kill(int pid)
        {
            EnablePrivileges();
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(true);
                p.WaitForExit(5000);
                return (true, "已结束 PID " + pid + "（会话 " + p.SessionId + "）");
            }
            catch (ArgumentException)
            {
                return (false, "进程 " + pid + " 不存在");
            }
            catch (Exception ex)
            {
                return (false, "结束 PID " + pid + " 失败：" + ex.Message);
            }
        }

        // ── 小工具 ───────────────────────────────────────────────────────────

        private static async Task<(bool ok, string message)> RunToolAsync(string exe, string args, int timeoutMs,
            CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = ConsoleEncoding(),
                    StandardErrorEncoding = ConsoleEncoding()
                };
                using var p = new Process { StartInfo = psi };
                p.Start();
                var outTask = p.StandardOutput.ReadToEndAsync(ct);
                var errTask = p.StandardError.ReadToEndAsync(ct);
                var waitTask = p.WaitForExitAsync(ct);
                var done = await Task.WhenAny(waitTask, Task.Delay(timeoutMs, ct)).ConfigureAwait(false);
                if (done != waitTask)
                {
                    try { p.Kill(true); } catch { }
                    return (false, "执行超时：" + exe);
                }
                string o = (await outTask.ConfigureAwait(false)).Trim();
                string e = (await errTask.ConfigureAwait(false)).Trim();
                bool ok = p.ExitCode == 0;
                return (ok, string.IsNullOrWhiteSpace(e) ? o : (o + " " + e).Trim());
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// 控制台工具（query user / tasklist 等）的输出编码。
        ///
        /// ★ 必须用**系统 ANSI/OEM 代码页**解码，不能用 Encoding.Default：
        ///   .NET Core 里 Encoding.Default 恒为 UTF-8，而中文系统的 query user
        ///   输出是 GBK(936)。实测错配后状态字段变成乱码（“运行中” → “������”），
        ///   NormalizeState 无法识别 → 所有会话被丢弃 → 会话号变成 0
        ///   → 注入代理投送失败「无效的会话 ID：0」（用户实测）。
        ///
        /// 取值顺序：GetACP（ANSI 代码页，最可靠）→ OEMCodePage → 936 兜底。
        /// </summary>
        public static System.Text.Encoding ConsoleEncoding()
        {
            try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); }
            catch { }

            foreach (int cp in new[] { GetACP(), 936 })
            {
                try
                {
                    if (cp <= 0) continue;
                    // 只接受 ANSI/OEM 代码页（936/1252/932...），排除 65001
                    if (cp == 65001) continue;
                    return System.Text.Encoding.GetEncoding(cp);
                }
                catch { }
            }
            try { return System.Text.Encoding.GetEncoding(936); } catch { }
            return System.Text.Encoding.UTF8;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern int GetACP();

        private static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\\\"") + "\"";

        private static string QuoteArg(string s) => "\"" + (s ?? "").Trim('"') + "\"";

        private static string Shorten(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "（无输出）";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 260 ? s.Substring(0, 260) + "…" : s;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // ── Win32 ────────────────────────────────────────────────────────────

        private const uint TOKEN_QUERY = 0x0008;
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_ALL_ACCESS = 0xF01FF;
        private const uint SE_PRIVILEGE_ENABLED = 0x0002;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x0400;
        private const uint CREATE_NEW_CONSOLE = 0x0010;

        private enum SECURITY_IMPERSONATION_LEVEL { SecurityAnonymous, SecurityIdentification, SecurityImpersonation, SecurityDelegation }
        private enum TOKEN_TYPE { TokenPrimary = 1, TokenImpersonation = 2 }
        private enum TOKEN_INFORMATION_CLASS { TokenElevation = 20, TokenElevationType = 18, TokenLinkedToken = 19 }
        private const TOKEN_INFORMATION_CLASS TokenSessionId = (TOKEN_INFORMATION_CLASS)12;

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, TOKEN_INFORMATION_CLASS cls, out IntPtr info, int len, out int ret);

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_ELEVATION { public int TokenIsElevated; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr hProcess, uint access, out IntPtr hToken);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr hToken, bool disableAll, ref TOKEN_PRIVILEGES newState,
            int bufferLength, IntPtr prevState, IntPtr returnLength);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr hToken, TOKEN_INFORMATION_CLASS cls, IntPtr info,
            int length, out int retLength);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(IntPtr hExisting, uint access, IntPtr attrs,
            SECURITY_IMPERSONATION_LEVEL level, TOKEN_TYPE type, out IntPtr phNewToken);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessAsUser(IntPtr hToken, string? appName, IntPtr cmdLine,
            IntPtr procAttrs, IntPtr threadAttrs, bool inherit, uint flags, IntPtr env, IntPtr currentDir,
            ref STARTUPINFO si, out PROCESS_INFORMATION pi);
        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr env);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(IntPtr token, TOKEN_INFORMATION_CLASS cls, ref int value, int length);
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    }
}
