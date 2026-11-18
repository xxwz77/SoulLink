using System;using System.Diagnostics;using System.IO;using System.Linq;using System.Security.Principal;using SoulLink.Services;
public static class BootstrapHostCheck
{
    static int fails;
    static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
    public static int Main()
    {
        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        Console.WriteLine("  admin=" + admin);
        var self = SystemLauncher.ResolveSelfExe();
        Console.WriteLine("  self=" + self);
        var m = typeof(SystemLauncher).GetMethod("EnsureBootstrapHost",
            System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static);
        Check(m != null, "存在引导宿主生成方法");
        if (m == null) return 1;
        string host = (string)m.Invoke(null, new object[]{ self })!;
        Console.WriteLine("  host=" + host);
        Check(File.Exists(host), "引导宿主已生成");
        // 用 mt 校验清单：有 requestedExecutionLevel 且非 requireAdministrator
        string mt = (string)typeof(SystemLauncher).GetMethod("FindMtExe",
            System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!.Invoke(null,null)!;
        Console.WriteLine("  mt=" + (mt.Length==0?"(none)":mt));
        if (mt.Length > 0)
        {
            var psi = new ProcessStartInfo(mt, "-nologo -inputresource:" + host + ";#1 -out:" + Path.Combine(Path.GetTempPath(),"host.manifest"))
                { UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            Console.WriteLine("  mt-exit=" + p.ExitCode);
            string mf = Path.Combine(Path.GetTempPath(),"host.manifest");
            if (File.Exists(mf))
            {
                string text = File.ReadAllText(mf);
                Check(text.Contains("asInvoker"), "引导宿主清单为 asInvoker（不会触发 740）");
                Check(!text.Contains("requireAdministrator"), "引导宿主不再要求管理员提升");
            }
            else Check(false, "无法导出引导宿主清单：" + o);
        }
        Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);
        return fails;
    }
}
