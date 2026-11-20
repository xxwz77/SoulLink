using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;

public static class Program
{
    private const string ServiceName = "SoulLink.DevBroker";
    private const string InstallRoot = "C:\\Program Files\\SoulLinkDev";
    private const uint ScmAll = 0xF003F, ServiceAll = 0xF01FF, ServiceChange = 0x0002;
    private const uint OwnProcess = 0x10, AutoStart = 0x2, ErrorNormal = 0x1;
    private static string LogPath => Path.Combine(Path.GetTempPath(), "SoulLink-Broker-install-current.log");
    private static void Log(string text) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("O") + " " + text + Environment.NewLine, new UTF8Encoding(false)); } catch { } }

    public static int Main(string[] args)
    {
        try
        {
            File.WriteAllText(LogPath, "", new UTF8Encoding(false));
            if (args.Length == 1 && args[0] == "--uninstall") return Uninstall();
            if (args.Length == 1 && args[0] == "--uninstall") return Uninstall();
            if (args.Length != 2 || args[0] != "--install") return 2;
            string source = Path.GetFullPath(args[1]);

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
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
            StopExisting();
            Directory.CreateDirectory(InstallRoot);
            // 文件锁由旧服务释放；不在此写死等待。
            SecureDirectory();
            string service = Path.Combine(InstallRoot, "SoulLink.exe");
            string agent = Path.Combine(InstallRoot, "SoulLink.Agent.exe");
            CopyFile(source, service); Log("SERVICE_COPIED");
            // 主程序 → 服务本体（requireAdministrator），再复制一份改成 asInvoker 作为代理。
            // 代理清单是判定"代理是否就绪"的依据，必须每次都校验，不能因为内容相同就跳过。
            if (!File.Exists(agent) || !PatchManifest(agent, source)) { CopyFile(source, agent); PatchManifest(agent, null); }
            Log("AGENT_READY");
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
