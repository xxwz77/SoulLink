using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using SoulLink.Services;

public static class Program
{
    private const string ServiceName = EngineComponentVersions.BrokerServiceName;
    private static string InstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SoulLinkDev");
    private const uint ScmAll = 0xF003F, ServiceAll = 0xF01FF, ServiceChange = 0x0002;
    private const uint OwnProcess = 0x10, AutoStart = 0x2, ErrorNormal = 0x1;
    private static string LogPath => Path.Combine(Path.GetTempPath(), "SoulLink-Broker-install-current.log");
    private static void Log(string text) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("O") + " " + text + Environment.NewLine, new UTF8Encoding(false)); } catch { } }

    public static int Main(string[] args)
    {
        try
        {
            File.WriteAllText(LogPath, "", new UTF8Encoding(false));
            // 干跑验证模式：只把源目录的旁挂文件同步到目标目录，不动服务/ACL/固定安装目录。
            // 用于在临时目录里验证复制逻辑与幂等性（不触碰生产环境）。
            if (args.Length == 3 && args[0] == "--sync-runtime")
            {
                SyncRuntimeFiles(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
                return 0;
            }
            if (args.Length == 1 && args[0] == "--uninstall") return Uninstall();
            if (args.Length != 2 || args[0] is not ("--install" or "--update")) return 2;
            string source = Path.GetFullPath(args[1]);
            if (args[0] == "--update")
            {
                if (!IsAdministrator()) return 5;
                return UpdateExisting(Path.GetDirectoryName(source) ?? "");
            }

            // ★ 2.17.45：不再写死 "SoulLink.exe"。
            //
            //   本项目有两个身份共用同一份二进制：
            //     · SoulLink.exe        → 测试版（身份 "SoulLink"）
            //     · SoulLink-Stable.exe → 挂机验证版（身份 "SoulLink-Stable"）
            //   改名前只认 "SoulLink.exe"，于是挂机版点「安装 / 修复服务」
            //   会直接 exit 3，用户完全看不出原因。
            //
            //   服务本体统一装成 SoulLink.exe（路径固定，服务名/ACL 都按它建），
            //   所以这里只校验"是不是本项目的可执行文件"，不再校验文件名。
            if (!File.Exists(source)) return 3;
            string baseName = Path.GetFileNameWithoutExtension(source);
            if (!baseName.Equals("SoulLink", StringComparison.OrdinalIgnoreCase)
                && !baseName.Equals("SoulLink-Stable", StringComparison.OrdinalIgnoreCase)) return 3;
            if (!IsAdministrator()) return 5;
            string sourceDir = Path.GetDirectoryName(source) ?? "";
            string standaloneBroker = Path.Combine(sourceDir, "SoulLink.Broker.exe");
            string standaloneAgent = Path.Combine(sourceDir, "SoulLink.Agent.exe");
            if (EngineComponentVersions.Read(standaloneBroker, EngineComponentVersions.BrokerFile) == null
                || EngineComponentVersions.Read(standaloneAgent, EngineComponentVersions.AgentFile) == null)
            {
                Log("COMPONENTS_MISSING_OR_INVALID");
                return 3;
            }
            Directory.CreateDirectory(InstallRoot);
            using var updateLock = LockInstallation();
            StopExisting();
            // 文件锁由旧服务释放；不在此写死等待。
            SecureDirectory();
            string service = Path.Combine(InstallRoot, "SoulLink.exe");
            string agent = Path.Combine(InstallRoot, "SoulLink.Agent.exe");
            // Keep the registered path compatible with existing installations.
            CopyFile(standaloneBroker, service); Log("SERVICE_COPIED");
            CopyFile(standaloneAgent, agent);
            if (!PatchManifest(agent, null)) Log("AGENT_MANIFEST_UNVERIFIED " + agent);
            Log("AGENT_READY");
            // Both components are standalone. The installed Broker needs only the version markers beside them.
            foreach (string marker in new[] { "SoulLink.Agent.version", "SoulLink.Agent.required.version" })
                CopyFile(Path.Combine(sourceDir, marker), Path.Combine(InstallRoot, marker));
            EnsureService(service); Log("SERVICE_CREATED");
            using (var controller = new ServiceController(ServiceName))
            {
                controller.Start(); controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                Log("SERVICE_RUNNING");
            }
            return 0;
        }
        catch (Exception ex) { Log("FAILED " + ex); return 1; }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static FileStream LockInstallation() => new(Path.Combine(InstallRoot, ".component-update.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

    private static int UpdateExisting(string sourceDirectory)
    {
        string service = Path.Combine(InstallRoot, "SoulLink.exe");
        string agent = Path.Combine(InstallRoot, EngineComponentVersions.AgentFile);
        if (!Directory.Exists(InstallRoot)) return 8;
        using var updateLock = LockInstallation();
        if (!EngineComponentVersions.IsRegisteredBroker(service)) return 8;

        string sourceService = Path.Combine(sourceDirectory, EngineComponentVersions.BrokerFile);
        string sourceAgent = Path.Combine(sourceDirectory, EngineComponentVersions.AgentFile);
        var brokerVersion = EngineComponentVersions.Read(sourceService, EngineComponentVersions.BrokerFile);
        var agentVersion = EngineComponentVersions.Read(sourceAgent, EngineComponentVersions.AgentFile);
        bool updateBroker = brokerVersion != null && (EngineComponentVersions.ReadInstalledBroker(service) is not { } installedBroker
            || installedBroker < brokerVersion);
        string required = Path.Combine(InstallRoot, "SoulLink.Agent.required.version");
        var minimum = EngineComponentVersions.ReadMarker(required);
        var installedAgent = EngineComponentVersions.Read(agent, EngineComponentVersions.AgentFile);
        bool updateAgent = agentVersion != null && (minimum == null || agentVersion >= minimum)
            && (installedAgent == null || installedAgent < agentVersion);
        string targetMarker = Path.Combine(InstallRoot, "SoulLink.Agent.version");
        string sourceMarker = Path.Combine(sourceDirectory, "SoulLink.Agent.version");
        bool repairMarker = !updateAgent && installedAgent != null
            && EngineComponentVersions.ReadMarker(targetMarker) != installedAgent;
        if (updateAgent && EngineComponentVersions.ReadMarker(sourceMarker) != agentVersion) return 7;

        using var controller = new ServiceController(ServiceName);
        bool waitForBroker = false;
        bool running = false;
        if (updateBroker)
        {
            var status = controller.Status;
            running = status == ServiceControllerStatus.Running;
            waitForBroker = AgentInUse(agent) || (!running && status != ServiceControllerStatus.Stopped);
            updateBroker = !waitForBroker;
        }
        if (!updateBroker && !updateAgent && !repairMarker) return waitForBroker ? 6 : 0;

        var changed = new List<(string Target, string? Backup)>();
        string nonce = Guid.NewGuid().ToString("N");
        string markerDraft = targetMarker + ".repair-" + nonce;
        try
        {
            if (updateBroker && running)
            {
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
            if (updateBroker) Replace(sourceService, service, nonce, changed);
            if (updateAgent)
            {
                // 运行中的进程保留旧映像，后续投送从原路径启动新版。
                Replace(sourceAgent, agent, nonce, changed, moveRunningImage: true);
                Replace(sourceMarker, targetMarker, nonce, changed);
                string sourceMinimum = Path.Combine(sourceDirectory, "SoulLink.Agent.required.version");
                if (EngineComponentVersions.ReadMarker(sourceMinimum) is { } requested
                    && requested <= agentVersion && (minimum == null || requested > minimum))
                    Replace(sourceMinimum, required, nonce, changed);
            }
            else if (repairMarker && installedAgent != null)
            {
                File.WriteAllText(markerDraft, installedAgent.ToString(3) + Environment.NewLine);
                Replace(markerDraft, targetMarker, nonce, changed);
            }
            if (updateBroker && running) StartAndWait(controller);
        }
        catch
        {
            if (updateBroker && running)
                try { if (controller.Status != ServiceControllerStatus.Stopped) { controller.Stop(); controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15)); } } catch { }
            for (int i = changed.Count - 1; i >= 0; i--)
            {
                var (target, backup) = changed[i];
                try { File.Delete(target); if (backup != null) File.Move(backup, target); }
                catch (Exception ex) { Log("ROLLBACK_FAILED " + ex.Message); }
            }
            if (updateBroker && running)
                try { StartAndWait(controller); } catch (Exception ex) { Log("RESTART_FAILED " + ex.Message); }
            throw;
        }
        finally { try { File.Delete(markerDraft); } catch { } }
        foreach (var (_, backup) in changed) if (backup != null) try { File.Delete(backup); } catch { }
        Log("COMPONENTS_UPDATED broker=" + updateBroker + " agent=" + updateAgent);
        return waitForBroker ? 6 : 0;
    }

    private static bool AgentInUse(string target)
    {
        bool active = false;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target)))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited && (process.MainModule?.FileName is not { } path
                        || string.Equals(path, target, StringComparison.OrdinalIgnoreCase))) active = true;
                }
                catch (InvalidOperationException) { }
                catch { active = true; }
            }
        }
        return active;
    }

    private static void Replace(string source, string target, string nonce,
        List<(string Target, string? Backup)> changed, bool moveRunningImage = false)
    {
        string staged = target + ".new-" + nonce;
        try
        {
            File.Copy(source, staged);
            string? backup = File.Exists(target) ? target + ".old-" + nonce : null;
            if (moveRunningImage && backup != null)
            {
                File.Move(target, backup);
                changed.Add((target, backup));
                File.Move(staged, target);
                return;
            }
            if (backup == null) File.Move(staged, target);
            else File.Replace(staged, target, backup);
            changed.Add((target, backup));
        }
        finally { try { File.Delete(staged); } catch { } }
    }

    private static void StartAndWait(ServiceController service)
    {
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Stopped) service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
    }

    private static void StopExisting()
    {
        using var controller = new ServiceController(ServiceName);
        try
        {
            if (controller.Status != ServiceControllerStatus.Stopped)
            {
                controller.Stop(true); controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
            controller.Refresh();
            Log("EXISTING_STOPPED");
        }
        catch (InvalidOperationException) { Log("NO_EXISTING_SERVICE"); }
    }

    private static void CopyFile(string source, string destination)
    {
        // ★ 判据必须是内容，不是版本号：同版本重新编译出来的 exe 版本号完全一样，
        //   以前 "版本相同就跳过"，结果装完还是旧二进制，排查时误以为改动没生效。
        if (File.Exists(destination) && SameContent(source, destination)) { Log("SAME_CONTENT " + destination); return; }
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try { File.Copy(source, destination, true); Log("COPIED " + destination); return; }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(600);
                if (attempt >= 2 && File.Exists(destination))
                {
                    // 旧文件仍被句柄占用：改名让开路径，再把新文件放到目标位置。
                    string old = destination + ".old";
                    try { File.Delete(old); } catch { }
                    try { File.Move(destination, old); } catch { }
                    try { File.Copy(source, destination, false); Log("COPIED_AFTER_MOVE " + destination); return; } catch { }
                }
            }
        }
        throw new IOException("无法替换文件（旧服务可能仍占用）：" + destination);
    }

    /// <summary>
    /// ★ 部署链路修复（2026-11-23）：
    ///   单文件 exe 把 Vortice.* / SharpGen.Runtime* / WinRT 投影 DLL 外置
    ///   （见 SoulLink.csproj 的 ExcludeWinRTRuntimePacksFromSingleFile），
    ///   网页界面、7za、tools\ 也都是 exe 旁的独立文件。
    ///   安装时只复制 exe 会缺运行库，真实 Agent 的 /stream 因加载不到
    ///   Vortice.MediaFoundation 直接类型加载失败（连接重置）。
    ///   这里把 exe 同目录的旁挂文件一并同步到安装目录。
    ///   幂等：按内容比对，相同跳过；单个文件复制失败（旧进程仍占用）只记日志，不中断安装。
    /// </summary>
    private static void SyncRuntimeFiles(string sourceDir, string root)
    {
        if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir)) { Log("SYNC_NO_SRC " + sourceDir); return; }
        Directory.CreateDirectory(root);

        // 1) 网页界面：主程序优先读 exe 同目录的 web-index.html（改样式不用重启）。
        SyncFile(Path.Combine(sourceDir, "web-index.html"), Path.Combine(root, "web-index.html"));

        // 2) 7za.exe：BGI 更新包解压依赖，必须随 exe 一起。
        SyncFile(Path.Combine(sourceDir, "7za.exe"), Path.Combine(root, "7za.exe"));

        // 2b) P0-1（3.3.40）：随包 version 标记文件（版本号单行）。agent 由 source 复制而来，
        //      version 必须跟着走，否则单文件发布的 agent 读不到版本 → 投送全线 AGENT_INVALID。
        SyncFile(Path.Combine(sourceDir, "SoulLink.Agent.version"), Path.Combine(root, "SoulLink.Agent.version"));
        // 2c) t7a（3.3.40）：主程序要求的 agent 最低版本标记，跟着服务本体 SoulLink.exe 走，
        //      ReadRequiredAgentVersion 依赖它，缺失会让 ValidAgent 一律放行。
        SyncFile(Path.Combine(sourceDir, "SoulLink.Agent.required.version"), Path.Combine(root, "SoulLink.Agent.required.version"));


        // 3) 旁挂运行时 DLL：单文件外置的 Vortice / SharpGen / WinRT 投影。
        foreach (var pattern in new[] { "Vortice.*.dll", "SharpGen.Runtime*.dll", "WinRT.Runtime.dll", "Microsoft.Windows.SDK.NET.dll", "Microsoft.Windows.UI.Xaml.dll" })
        {
            string[] hits;
            try { hits = Directory.GetFiles(sourceDir, pattern); } catch { hits = new string[0]; }
            foreach (var src in hits) SyncFile(src, Path.Combine(root, Path.GetFileName(src)));
        }

        // 4) tools\ 子目录：broker-*.ps1 与独立安装器，安装目录自身要能再次安装/卸载。
        string toolsSrc = Path.Combine(sourceDir, "tools");
        if (Directory.Exists(toolsSrc))
        {
            string toolsDst = Path.Combine(root, "tools");
            Directory.CreateDirectory(toolsDst);
            var list = new System.Collections.Generic.List<string>();
            try
            {
                list.AddRange(Directory.GetFiles(toolsSrc, "broker-*.ps1"));
                list.AddRange(Directory.GetFiles(toolsSrc, "SoulLink.BrokerSetup.exe"));
            }
            catch { }
            foreach (var src in list) SyncFile(src, Path.Combine(toolsDst, Path.GetFileName(src)));
        }
    }

    private static void SyncFile(string source, string destination)
    {
        if (string.IsNullOrEmpty(source) || !File.Exists(source)) { Log("SYNC_MISSING " + source); return; }
        try
        {
            if (File.Exists(destination) && SameContent(source, destination)) { Log("SYNC_SAME " + destination); return; }
            File.Copy(source, destination, true);
            Log("SYNC_COPIED " + destination);
        }
        catch (Exception ex) { Log("SYNC_FAIL " + destination + " :: " + ex.Message); }
    }

    private static bool SameContent(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            using var sa = File.OpenRead(a); using var sb = File.OpenRead(b);
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] ha = sha.ComputeHash(sa);
            byte[] hb = sha.ComputeHash(sb);
            for (int i = 0; i < ha.Length; i++) if (ha[i] != hb[i]) return false;
            return true;
        }
        catch { return false; }
    }

    private static void WaitForFileRelease(string path)
    {
        if (!File.Exists(path)) return;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            try { using var s = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return; }
            catch (IOException) { Thread.Sleep(250); }
        }
        throw new IOException("旧服务文件未释放：" + path);
    }

    private static void SecureDirectory()
    {
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(InstallRoot).SetAccessControl(acl);
    }

    /// <summary>
    /// 把代理副本改成 asInvoker。已改过的返回 true（无需重做）。
    /// source 为 null 表示不比对来源内容。
    /// </summary>
    private static bool PatchManifest(string file, string? source)
    {
        if (!File.Exists(file)) return false;
        // ★ 先比内容：已经改成 asInvoker 的旧版本也会命中下面的早退，
        //   导致换了新主程序却还在用旧代理（实测踩过，排查时以为改动没生效）。
        if (source != null && !SameContent(source, file)) return false;
        byte[] data = File.ReadAllBytes(file);
        byte[] oldValue = Encoding.ASCII.GetBytes("level=\"requireAdministrator\"");
        byte[] newValue = Encoding.ASCII.GetBytes("level=\"asInvoker\"           ");
        int count = 0;
        for (int i = 0; i <= data.Length - oldValue.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < oldValue.Length; j++) if (data[i+j] != oldValue[j]) { match = false; break; }
            if (!match) continue;
            Array.Copy(newValue, 0, data, i, newValue.Length); count++;
        }
        if (count == 0 && Encoding.ASCII.GetString(data).Contains("level=\"asInvoker\"")) return true;
        if (count != 1) return false;
        File.WriteAllBytes(file, data);
        return true;
    }

    private static void EnsureService(string exe)
    {
        IntPtr scm = OpenSCManager(null, null, ScmAll);
        if (scm == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        try
        {
            string command = "\"" + exe + "\" --broker";
            IntPtr service = OpenService(scm, ServiceName, ServiceChange | ServiceAll);
            if (service == IntPtr.Zero)
                service = CreateService(scm, ServiceName, "SoulLink Dev Broker", ServiceAll, OwnProcess, AutoStart, ErrorNormal, command, null, IntPtr.Zero, null, null, null);
            if (service == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            CloseServiceHandle(service);
        }
        finally { CloseServiceHandle(scm); }
    }

    private static int Uninstall()
    {
        StopExisting();
        IntPtr scm = OpenSCManager(null, null, ScmAll);
        if (scm == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        try
        {
            IntPtr service = OpenService(scm, ServiceName, ServiceAll);
            if (service != IntPtr.Zero)
            {
                if (!DeleteService(service)) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                CloseServiceHandle(service);
            }
        }
        finally { CloseServiceHandle(scm); }
        Thread.Sleep(800);
        if (Directory.Exists(InstallRoot))
            foreach (var p in Directory.GetFiles(InstallRoot)) try { File.Delete(p); } catch { }
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError=true)] private static extern bool DeleteService(IntPtr service);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode, SetLastError=true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode, SetLastError=true)] private static extern IntPtr CreateService(IntPtr scm, string name, string display, uint access, uint type, uint start, uint error, string binary, string? group, IntPtr tag, string? dependencies, string? account, string? password);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode, SetLastError=true)] private static extern IntPtr OpenService(IntPtr scm, string name, uint access);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError=true)] private static extern bool CloseServiceHandle(IntPtr handle);
}
