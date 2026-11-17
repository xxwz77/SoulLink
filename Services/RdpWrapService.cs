using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SoulLink.Services
{
    /// <summary>RDP Wrapper（TermWrap）当前状态。</summary>
    public sealed class RdpWrapStatus
    {
        /// <summary>配置里记录的目录。</summary>
        public string WrapDir = "";
        /// <summary>该目录下 TermWrap.dll 是否存在。</summary>
        public bool DllExists;
        /// <summary>TermService 的 ServiceDll 当前值。</summary>
        public string CurrentServiceDll = "";
        /// <summary>当前 ServiceDll 是否指向本目录的 TermWrap.dll。</summary>
        public bool Installed;
        /// <summary>TermService 是否在运行。</summary>
        public bool ServiceRunning;
        /// <summary>TermWrap.dll 是否已加载进 svchost（真正生效的标志）。</summary>
        public bool DllLoaded;

        public string Summary
        {
            get
            {
                if (!DllExists) return "目录无效：找不到 TermWrap.dll";
                if (!Installed) return "未安装：TermService 未指向本目录";
                if (!ServiceRunning) return "已配置，但服务未运行";
                if (!DllLoaded) return "已配置，但 DLL 尚未加载（需重启服务）";
                return "已安装并生效";
            }
        }
    }

    /// <summary>
    /// RDP Wrapper / TermWrap 管理。
    ///
    /// ★ 为什么动态生成而不是内置 .reg 文件（用户明确要求）：
    ///   现成的 reg 把 "C:\Program Files\RDP Wrap\" 写死在 hex(2) 里，
    ///   换台机器或换个安装位置就失效。这里改成按目录现算路径再写注册表。
    ///
    /// ★ 为什么必须重启服务：
    ///   ServiceDll 只在服务启动时读取。ServiceDllUnloadOnStop=1 意味着停止服务会卸载 DLL，
    ///   所以 Restart-Service TermService 就能让新路径生效，**不需要重启整台电脑**。
    ///   代价是所有 RDP 会话会断开重连 —— 使用该功能时应确保没有正在挂机的会话。
    /// </summary>
    public static class RdpWrapService
    {
        private const string TermServiceParams =
            @"SYSTEM\CurrentControlSet\Services\TermService\Parameters";

        private const string DefaultServiceDll = @"%SystemRoot%\System32\termsrv.dll";

        /// <summary>默认安装目录。</summary>
        public static string DefaultWrapDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RDP Wrap");

        /// <summary>读取当前状态。</summary>
        public static RdpWrapStatus GetStatus(string wrapDir)
        {
            var st = new RdpWrapStatus { WrapDir = wrapDir ?? "" };

            try
            {
                st.DllExists = !string.IsNullOrWhiteSpace(wrapDir)
                               && File.Exists(Path.Combine(wrapDir, "TermWrap.dll"));
            }
            catch { st.DllExists = false; }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(TermServiceParams);
                st.CurrentServiceDll = key?.GetValue("ServiceDll") as string ?? "";
            }
            catch { }

            try
            {
                string expected = Path.Combine(wrapDir, "TermWrap.dll");
                st.Installed = !string.IsNullOrWhiteSpace(st.CurrentServiceDll)
                               && string.Equals(Path.GetFullPath(st.CurrentServiceDll),
                                                Path.GetFullPath(expected),
                                                StringComparison.OrdinalIgnoreCase);
            }
            catch { st.Installed = false; }

            st.ServiceRunning = IsServiceRunning("TermService");
            st.DllLoaded = IsWrapDllLoaded();
            return st;
        }

        /// <summary>
        /// 写入注册表，把 TermService 指向指定目录的 TermWrap.dll。
        /// 只写 CurrentControlSet（它是 ControlSet00x 的别名），
        /// 不再像原始 .reg 那样两份都写 —— 两份写反而容易在 ControlSet 切换后不一致。
        /// </summary>
        public static async Task<(bool ok, string message)> InstallAsync(string wrapDir, bool autoRestart)
        {
            if (!LocalUserService.IsElevated()) return (false, "需要管理员权限");
            if (string.IsNullOrWhiteSpace(wrapDir)) return (false, "未指定 RDP Wrap 目录");

            string dll = Path.Combine(wrapDir, "TermWrap.dll");
            if (!File.Exists(dll)) return (false, "目录里没有 TermWrap.dll：" + wrapDir);

            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(TermServiceParams, writable: true);
                if (key == null) return (false, "无法打开注册表项（权限不足）");

                // ★ 必须先备份当前真实值，再覆盖；否则"恢复"只能靠猜系统默认路径
                EnsureBackup(key);

                key.SetValue("ServiceDll", dll, RegistryValueKind.ExpandString);
                key.SetValue("ServiceDllUnloadOnStop", 1, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                return (false, "写注册表失败：" + ex.Message);
            }

            if (!autoRestart)
                return (true, "已写入注册表。需要重启 TermService 才能生效。");

            var (ok, msg) = await RestartTermServiceAsync();
            return ok ? (true, "已写入注册表并重启服务，TermWrap 应已生效")
                      : (true, "注册表已写入，但重启服务失败：" + msg);
        }

        // ── 备份 / 恢复 ───────────────────────────────────────────────

        private const string BackupValueName = "SoulLink_Backup_ServiceDll";
        private const string BackupUnloadName = "SoulLink_Backup_ServiceDllUnloadOnStop";

        /// <summary>是否已经记录过原始值。</summary>
        public static bool HasBackup()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(TermServiceParams);
                return key?.GetValue(BackupValueName) is string;
            }
            catch { return false; }
        }

        /// <summary>读取备份的原始 ServiceDll。</summary>
        public static string? BackedUpServiceDll()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(TermServiceParams);
                return key?.GetValue(BackupValueName) as string;
            }
            catch { return null; }
        }

        /// <summary>
        /// 首次安装前把**当前真实的 ServiceDll** 存进备份值。
        /// ★ 只备份一次：重复安装（比如换目录再装）不能把"已改成 TermWrap"的值当成原始值。
        /// ★ 直接写注册表备份值，而不是另存文件：注册表项本身就是"原位"，
        ///   即使应用被卸载、配置丢失，恢复路径仍然能读到原始值。
        /// </summary>
        private static void EnsureBackup(RegistryKey key)
        {
            try
            {
                if (key.GetValue(BackupValueName) is string) return;   // 已有备份，绝不覆盖

                var cur = key.GetValue("ServiceDll") as string ?? DefaultServiceDll;
                key.SetValue(BackupValueName, cur, RegistryValueKind.String);

                var unload = key.GetValue("ServiceDllUnloadOnStop");
                int unloadVal = unload is int i ? i : 1;
                key.SetValue(BackupUnloadName, unloadVal, RegistryValueKind.DWord);
            }
            catch { }
        }

        /// <summary>恢复到**安装前记录的原始值**（没有备份时退回系统默认 termsrv.dll）。</summary>
        public static async Task<(bool ok, string message)> RestoreAsync(bool autoRestart)
        {
            if (!LocalUserService.IsElevated()) return (false, "需要管理员权限");

            string target;
            int unload = 1;
            bool fromBackup = false;
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(TermServiceParams, writable: true);
                if (key == null) return (false, "无法打开注册表项（权限不足）");

                if (key.GetValue(BackupValueName) is string bak && !string.IsNullOrWhiteSpace(bak))
                {
                    target = bak;
                    if (key.GetValue(BackupUnloadName) is int u) unload = u;
                    fromBackup = true;
                }
                else
                {
                    // 从未安装过就没有备份，退回系统默认
                    target = DefaultServiceDll;
                }

                key.SetValue("ServiceDll", target, RegistryValueKind.ExpandString);
                key.SetValue("ServiceDllUnloadOnStop", unload, RegistryValueKind.DWord);
            }
            catch (Exception ex) { return (false, "写注册表失败：" + ex.Message); }

            string how = fromBackup ? "已恢复安装前的原始配置" : "没有备份记录，已恢复为系统默认 termsrv.dll";
            if (!autoRestart) return (true, how + "。重启 TermService 后生效。");

            var (ok, msg) = await RestartTermServiceAsync();
            return ok ? (true, how + "，并已重启服务")
                      : (true, how + "，但重启服务失败：" + msg);
        }

        /// <summary>
        /// 重启 TermService。
        /// ★ 这是唯一能让 ServiceDll 变更生效的办法；会断开所有现有 RDP 会话。
        /// </summary>
        public static async Task<(bool ok, string message)> RestartTermServiceAsync()
        {
            if (!LocalUserService.IsElevated()) return (false, "需要管理员权限");

            var (ok1, out1) = await RunAsync("sc.exe", "stop TermService", 60000);
            // 没有在跑时 sc stop 会失败，忽略，继续启动
            await Task.Delay(1500);

            var (ok2, out2) = await RunAsync("sc.exe", "start TermService", 60000);
            if (!ok2) return (false, FirstLine(out2) ?? FirstLine(out1) ?? "启动 TermService 失败");

            await Task.Delay(1200);
            return (true, "TermService 已重启");
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
                    CreateNoWindow = true
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
                return (p.ExitCode == 0, (o + e).Trim());
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        private static string? FirstLine(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            foreach (var l in s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (l.Trim().Length > 0) return l.Trim();
            return null;
        }

        /// <summary>
        /// 查询服务是否在运行。
        /// ★ 用 sc query 而不是 ServiceController：后者需要额外的
        ///   System.ServiceProcess.ServiceController 程序集，单文件发布时还要带上，
        ///   而我们只需要一个"RUNNING"判断，命令行足够且零依赖。
        /// </summary>
        public static bool IsServiceRunning(string serviceName)
        {
            try
            {
                var psi = new ProcessStartInfo("sc.exe", "query " + serviceName)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                return o.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0
                    || o.IndexOf("运行中", StringComparison.Ordinal) >= 0;
            }
            catch { return false; }
        }

        /// <summary>检测 termwrap.dll 是否已加载进任何 svchost 进程。</summary>
        public static bool IsWrapDllLoaded()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("svchost"))
                {
                    try
                    {
                        foreach (ProcessModule m in p.Modules)
                        {
                            if (m.ModuleName.Equals("termwrap.dll", StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
            return false;
        }
    }
}
