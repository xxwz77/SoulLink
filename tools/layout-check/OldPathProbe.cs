using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

// 复现旧代码路径：WindowsIdentity(token) + WindowsPrincipal.IsInRole，
// 用来确认 .NET 8 下是否同样抛“拒绝访问”（决定客户端是否必须一起更新）。
public static class OldPathProbe
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint a, bool i, int pid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint a, out IntPtr t);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    public static int Main()
    {
        Console.WriteLine("runtime: " + RuntimeInformation.FrameworkDescription);
        foreach (var name in new[] { "SoulLink", "explorer" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    Console.WriteLine(name + " pid=" + p.Id + " session=" + p.SessionId + " => " + Old(p.Id));
                }
                catch (Exception ex) { Console.WriteLine(name + " pid=" + p.Id + " OUTER " + ex.GetType().Name + ":" + ex.Message); }
                finally { p.Dispose(); }
            }
        }
        return 0;
    }

    static string Old(int pid)
    {
        IntPtr proc = OpenProcess(0x1000, false, pid);
        if (proc == IntPtr.Zero) return "OpenProcess err=" + Marshal.GetLastWin32Error();
        try
        {
            if (!OpenProcessToken(proc, 0x0008, out IntPtr token)) return "OpenProcessToken err=" + Marshal.GetLastWin32Error();
            try
            {
                string sid;
                try { sid = new WindowsIdentity(token).User?.Value ?? ""; }
                catch (Exception ex) { return "WindowsIdentity THREW " + ex.GetType().Name + ":" + ex.Message; }
                try
                {
                    using var id = new WindowsIdentity(token);
                    bool admin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                    return "sid=" + sid + " oldAdminCheck=" + admin;
                }
                catch (Exception ex) { return "sid=" + sid + " IsInRole THREW " + ex.GetType().Name + ":" + ex.Message; }
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(proc); }
    }
}
