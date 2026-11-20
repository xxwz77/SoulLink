using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        /// <summary>内嵌资源名 → 释放到目标目录时的文件名。</summary>
        private static readonly (string Resource, string FileName)[] EmbeddedFiles =
        {
            ("SoulLink.Assets.RdpWrap.TermWrap.dll",  "TermWrap.dll"),
            ("SoulLink.Assets.RdpWrap.Zydis.dll",     "Zydis.dll"),
            ("SoulLink.Assets.RdpWrap.UmWrap.dll",    "UmWrap.dll"),
            ("SoulLink.Assets.RdpWrap.EndpWrap.dll",  "EndpWrap.dll"),
        };

        /// <summary>
        /// 把内嵌的 TermWrap 相关 DLL 释放到目标目录（已存在且内容相同则跳过）。
        ///
        /// ★ 为什么要内嵌而不是让用户自己放：
        ///   用户原话「新机器是不是应该也携带这个文件夹里的全部文件」
        ///   「越简单越好，我每次都要去找文件，然后还得重启」。
        ///
        /// ★ 为什么用"内容比对"而不是"存在即跳过"：
        ///   以后升级 TermWrap 时，旧文件留在目标目录会导致新版本不生效。
        ///   有了比对，内容不同就覆盖。
        ///
        /// 返回 (成功的文件数, 说明)。
        /// </summary>
        public static (int copied, string message) DeployEmbeddedFiles(string targetDir)
        {
            if (string.IsNullOrWhiteSpace(targetDir))
                return (0, "未指定目标目录");

            var asm = typeof(RdpWrapService).Assembly;
            var names = new HashSet<string>(asm.GetManifestResourceNames(), StringComparer.Ordinal);

            int copied = 0;
            int skipped = 0;
            var problems = new List<string>();

            try
            {
                Directory.CreateDirectory(targetDir);
            }
            catch (Exception ex)
            {
                return (0, "无法创建目录 " + targetDir + "：" + ex.Message);
            }

            foreach (var (resource, fileName) in EmbeddedFiles)
            {
                try
                {
                    if (!names.Contains(resource))
                    {
                        problems.Add(fileName + "（未内嵌）");
                        continue;
                    }

                    using var stream = asm.GetManifestResourceStream(resource);
                    if (stream == null) { problems.Add(fileName + "（读取失败）"); continue; }

                    byte[] data;
                    using (var ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        data = ms.ToArray();
                    }

                    string dest = Path.Combine(targetDir, fileName);

                    // 已存在且字节完全相同 → 跳过（不触碰正在被 svchost 加载的 dll）
                    if (File.Exists(dest))
                    {
                        try
                        {
                            var old = File.ReadAllBytes(dest);
                            if (old.Length == data.Length && SameBytes(old, data)) { skipped++; continue; }
                        }
                        catch { }
                    }

                    // 先写临时文件再替换：正在被加载的 DLL 直接覆盖会失败，
                    // 用 Move 可以在文件被占用时给出明确异常而不是写坏。
                    string tmp = dest + ".tmp";
                    File.WriteAllBytes(tmp, data);

                    try
                    {
                        if (File.Exists(dest)) File.Replace(tmp, dest, null);
                        else File.Move(tmp, dest);
                    }
                    catch
                    {
                        // File.Replace 在目标被占用时会失败；退回直接覆盖
                        try { File.Copy(tmp, dest, overwrite: true); File.Delete(tmp); }
                        catch (Exception ex)
                        {
                            try { File.Delete(tmp); } catch { }
                            problems.Add(fileName + "（被占用：" + ex.Message + "）");
                            continue;
                        }
                    }

                    copied++;
                }
                catch (Exception ex)
                {
                    problems.Add(fileName + "（" + ex.Message + "）");
                }
            }

            string msg = copied > 0
                ? "已释放 " + copied + " 个文件到 " + targetDir
                : (skipped > 0 ? "文件已是最新，无需释放" : "没有可释放的文件");

            if (problems.Count > 0)
                msg += "；未完成：" + string.Join("、", problems);

            return (copied, msg);
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>
        /// 自动探测 RDP Wrap 目录（用户不想每次手动找）。
        ///
        /// 顺序：默认目录 → Program Files 下名字相近的目录 → 其它盘常见位置。
        /// 只认**含 TermWrap.dll** 的目录。
        /// </summary>
        public static string? FindWrapDir()
        {
            var candidates = new List<string> { DefaultWrapDir };

            try
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (Directory.Exists(pf))
                {
                    foreach (var d in Directory.GetDirectories(pf))
                    {
                        string n = Path.GetFileName(d);
                        if (n.IndexOf("RDP", StringComparison.OrdinalIgnoreCase) >= 0)
                            candidates.Add(d);
                    }
                }
            }
            catch { }

            // 其它盘上也可能放着（用户常把工具放 D:\AAA挂机专区 之类）
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    string root = drive.RootDirectory.FullName;
                    if (string.Equals(root, Path.GetPathRoot(DefaultWrapDir), StringComparison.OrdinalIgnoreCase))
                        continue;   // 系统盘已经在上面扫过

                    foreach (var d in SafeDirs(root, 2))
                    {
                        string n = Path.GetFileName(d);
                        if (n.IndexOf("RDP", StringComparison.OrdinalIgnoreCase) >= 0
                            && n.IndexOf("Wrap", StringComparison.OrdinalIgnoreCase) >= 0)
                            candidates.Add(d);
                    }
                }
            }
            catch { }

            foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(Path.Combine(c, "TermWrap.dll"))) return c;
                }
                catch { }
            }
            return null;
        }

        /// <summary>浅层列目录，异常一律吞掉（遍历磁盘时随时可能遇到拒绝访问）。</summary>
        private static IEnumerable<string> SafeDirs(string root, int depth)
        {
            if (depth <= 0) yield break;

            string[] dirs;
            try { dirs = Directory.GetDirectories(root); }
            catch { yield break; }

            foreach (var d in dirs)
            {
                yield return d;

                // 名字不像工具目录的不再深挖，避免全盘遍历太慢
                string n = Path.GetFileName(d);
                if (n.IndexOf("RDP", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("挂机", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("工具", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Tool", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    foreach (var sub in SafeDirs(d, depth - 1)) yield return sub;
                }
            }
        }

        /// <summary>
        /// 统计当前活动的远程会话数（用于提示"重启会断开这些会话"）。
        /// ★ 只读 query user，不做任何连接/断开操作。
        /// </summary>
        public static int CountActiveRdpSessions()
        {
            try
            {
                var psi = new ProcessStartInfo("query.exe", "user")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return 0;

                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);

                int n = 0;
                foreach (var line in o.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    // RDP 会话名形如 rdp-tcp#0；console 不算
                    if (line.IndexOf("rdp-tcp", StringComparison.OrdinalIgnoreCase) >= 0) n++;
                }
                return n;
            }
            catch { return 0; }
        }

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

            // ★ 先把内嵌的运行时文件释放过去（新机器上目录可能根本不存在）。
            //   这样用户不需要自己找文件，也不依赖任何 .reg。
            var (copied, deployMsg) = DeployEmbeddedFiles(wrapDir);

            string dll = Path.Combine(wrapDir, "TermWrap.dll");
            if (!File.Exists(dll))
                return (false, "目录里没有 TermWrap.dll，且内嵌释放失败：" + deployMsg);

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

            // ★★ 同时写实际的 ControlSet00x，和官方 0-Install_termwrap_merged.reg 一致。★★
            //
            //   为什么不能只写 CurrentControlSet：
            //   CurrentControlSet 是别名，由 HKLMSYSTEMSelectCurrent 决定指向哪个
            //   ControlSet00x。系统更新或异常恢复后这个指向**会变**（例如 001 → 002），
            //   那时只写别名的配置会跟着丢失，表现就是"更新系统后多用户失效、要重新导 reg"。
            //   官方 reg 两个都写，正是为了扛住这种切换。
            string controlSetNote = "";
            try
            {
                int current = ReadCurrentControlSetIndex();
                if (current > 0)
                {
                    string realPath = @"SYSTEM\ControlSet" + current.ToString("000")
                                      + @"\Services\TermService\Parameters";
                    using var real = Registry.LocalMachine.CreateSubKey(realPath, writable: true);
                    if (real != null)
                    {
                        real.SetValue("ServiceDll", dll, RegistryValueKind.ExpandString);
                        real.SetValue("ServiceDllUnloadOnStop", 1, RegistryValueKind.DWord);
                        controlSetNote = "，并写入 ControlSet" + current.ToString("000");
                    }
                }
            }
            catch (Exception ex)
            {
                // 写实际 ControlSet 失败不算致命：CurrentControlSet 那份已经生效
                controlSetNote = "（注意：写入实际 ControlSet 失败 " + ex.Message + "）";
            }

            if (!autoRestart)
                return (true, "已写入注册表。需要重启 TermService 才能生效。");

            var (ok, msg) = await RestartTermServiceAsync();
            string deployNote = copied > 0 ? "（已释放 " + copied + " 个文件）" : "";

            if (!ok)
                return (false, "注册表已写入，但重启 TermService 失败：" + msg
                          + "。\n\n注册表已经写好了，下次重启电脑或手动重启"
                          + "「远程桌面服务」后即会生效。");

            // ★★ 重启返回成功 ≠ DLL 真的加载了。★★
            //
            //   用户实测截图：状态栏显示「已配置，但 DLL 尚未加载（需重启服务）」，
            //   而操作日志却写「已生效」—— 两者矛盾，日志在撒谎。
            //
            //   原因：sc start 成功只代表服务进程起来了，
            //   不代表它加载的是我们指过去的那份 TermWrap.dll
            //   （可能加载失败、被 WOW64 重定向、或被 svchost 复用了旧模块）。
            //
            //   这里必须**实测 DLL 是否已加载进 svchost** 才算成功。
            bool loaded = await WaitForWrapDllLoadedAsync(15000);

            return loaded
                ? (true, "已安装并重启服务" + controlSetNote + deployNote + "，TermWrap 已加载生效")
                : (false, "注册表已写入、服务也重启了，但 TermWrap.dll 尚未加载进远程桌面服务。" + controlSetNote + deployNote
                          + "\n\n这是最稳妥的解决方式：重启电脑后即会生效。"
                          + "\n如果重启后仍然不行，说明当前系统版本的 termsrv.dll 结构与 TermWrap 不匹配。");
        }

        /// <summary>读取 HKLM\SYSTEM\Select\Current，得到实际生效的 ControlSet 编号。</summary>
        private static int ReadCurrentControlSetIndex()
        {
            try
            {
                using var sel = Registry.LocalMachine.OpenSubKey(@"SYSTEM\Select");
                object? v = sel?.GetValue("Current");
                return v is int i ? i : 0;
            }
            catch { return 0; }
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

            // ★★ 关键是"等它真的停下"，不是睡固定时长。★★
            //
            //   原实现：sc stop → 固定等 1.5 秒 → sc start。
            //   sc stop 只是**发出停止请求就返回**，而 TermService 挂着所有 RDP 会话，
            //   停稳往往要几秒。1.5 秒时它还处于 STOP_PENDING，
            //   此时 sc start 必然返回 1056（ERROR_SERVICE_ALREADY_RUNNING），
            //   于是界面报"重启服务失败"——但服务其实一直好好跑着，用户被误导。
            //
            //   改成轮询状态，最多等 30 秒；stop 失败（本来就没在跑）也照常继续。

            // ★★ 必须先停依赖者 UmRdpService，再停 TermService。★★
            //
            //   这是"装了却要重启电脑才生效"的真正根因（用户实测反馈）。
            //
            //   UmRdpService（Remote Desktop Services UserMode Port Redirector）
            //   依赖 TermService，且平时处于 RUNNING。
            //   Windows 的规则：只要还有运行中的依赖者，父服务就**拒绝停止**
            //   （sc stop 返回 1051 ERROR_DEPENDENT_SERVICES_RUNNING）。
            //
            //   旧代码只看"最后服务还在跑"就当成功，于是：
            //     TermService 根本没停 → 它加载的仍是内存里那份旧 termsrv.dll
            //     → TermWrap.dll 从未被重新加载 → 多用户能力没生效
            //     → 用户只能重启电脑。
            //
            //   正确顺序：停 UmRdpService → 停 TermService → 启动 TermService → 启动 UmRdpService。

            const string dependent = "UmRdpService";
            bool dependentWasRunning = IsServiceRunning(dependent);

            if (dependentWasRunning)
            {
                await RunAsync("sc.exe", "stop " + dependent, 60000);
                var (depStopped, depWait) = await WaitForServiceStateAsync(dependent, "STOPPED", 20000);
                if (!depStopped)
                    return (false, "无法停止 " + dependent + "（" + depWait + "），"
                                 + "它是 TermService 的依赖服务，不停它就无法重启 TermService。\n\n"
                                 + "请手动执行：services.msc → 停止\"Remote Desktop Services UserMode Port Redirector\"，"
                                 + "再点一次本按钮；或直接重启电脑。");
            }

            await RunAsync("sc.exe", "stop TermService", 60000);

            var (stopped, stopWait) = await WaitForServiceStateAsync("TermService", "STOPPED", 30000);
            if (!stopped)
            {
                // 停不下来就是失败 —— 绝不能像旧代码那样谎报成功。
                // 没停下 = DLL 没卸载 = TermWrap 没加载 = 多用户不生效。
                try { if (dependentWasRunning) await RunAsync("sc.exe", "start " + dependent, 60000); } catch { }
                return (false, "TermService 没能停下来（" + stopWait + "）。"
                             + "\n\n服务没停，TermWrap.dll 就不会重新加载，多用户不会生效。"
                             + "\n请重启电脑后生效。");
            }

            var (ok2, out2) = await RunAsync("sc.exe", "start TermService", 60000);

            if (!ok2)
            {
                // 1056 = 服务已在运行。这不是失败，再确认状态即可。
                string txt = (out2 ?? "") + " " + (FirstLine(out2) ?? "");
                if (txt.IndexOf("1056", StringComparison.Ordinal) < 0)
                {
                    try { if (dependentWasRunning) await RunAsync("sc.exe", "start " + dependent, 60000); } catch { }
                    return (false, FirstLine(out2) ?? "启动 TermService 失败");
                }
            }

            // 等它真的进 RUNNING
            var (running, runWait) = await WaitForServiceStateAsync("TermService", "RUNNING", 30000);
            if (!running)
            {
                try { if (dependentWasRunning) await RunAsync("sc.exe", "start " + dependent, 60000); } catch { }
                return (false, "TermService 启动超时：" + runWait);
            }

            // 恢复依赖者
            if (dependentWasRunning)
            {
                await RunAsync("sc.exe", "start " + dependent, 60000);
                var (depBack, depBackWait) = await WaitForServiceStateAsync(dependent, "RUNNING", 20000);
                if (!depBack)
                    return (true, "TermService 已重启，但 " + dependent + " 未能恢复（" + depBackWait + "）。"
                                + "\n请手动在 services.msc 里启动它，否则 RDP 端口重定向不可用。");
            }

            return (true, "TermService 已重启（依赖服务已按顺序恢复）");
        }

        /// <summary>
        /// 轮询等待 TermWrap.dll 真正加载进 svchost。
        /// ★ 这是"安装是否生效"的唯一可信判据：
        ///   sc start 成功只说明服务进程起来了，不说明它加载了我们指过去的 DLL。
        /// </summary>
        public static async Task<bool> WaitForWrapDllLoadedAsync(int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (IsWrapDllLoaded()) return true;
                await Task.Delay(500).ConfigureAwait(false);
            }
            return IsWrapDllLoaded();
        }

        /// <summary>
        /// 轮询等待服务进入指定状态。返回 (是否达成, 最后一次状态描述)。
        /// ★ 用 sc query 的状态行判断，不依赖固定延迟。
        /// </summary>
        private static async Task<(bool ok, string state)> WaitForServiceStateAsync(
            string serviceName, string wanted, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string last = "";

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                last = QueryServiceState(serviceName);
                if (string.Equals(last, wanted, StringComparison.OrdinalIgnoreCase))
                    return (true, last);

                // 目标状态不可能到达时提前退出（例如服务被禁用）
                if (last.Length == 0) return (false, "服务不存在");

                await Task.Delay(400).ConfigureAwait(false);
            }

            return (false, last + "（等待 " + (timeoutMs / 1000) + " 秒）");
        }

        /// <summary>读取服务当前状态行，例如 RUNNING / STOPPED / STOP_PENDING。</summary>
        private static string QueryServiceState(string serviceName)
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
                if (p == null) return "";

                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);

                // 取 STATE 那一行的状态词，例如 "4  RUNNING"
                foreach (var line in o.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string t = line.Trim();
                    if (!t.StartsWith("STATE", StringComparison.OrdinalIgnoreCase)) continue;

                    int colon = t.IndexOf(':');
                    if (colon < 0) continue;

                    var parts = t.Substring(colon + 1).Trim()
                                 .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    // parts[0] = 数字状态码，parts[1] = 状态词
                    if (parts.Length >= 2) return parts[1].ToUpperInvariant();
                    if (parts.Length == 1) return parts[0].ToUpperInvariant();
                }
                return "";
            }
            catch { return ""; }
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
