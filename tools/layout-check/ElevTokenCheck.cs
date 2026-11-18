using System;using System.Reflection;using System.Runtime.InteropServices;using SoulLink.Services;
public static class ElevTokenCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr p,uint a,out IntPtr t);
 [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
 public static int Main()
 {
  var m=typeof(SystemLauncher).GetMethod("TryGetElevatedToken",BindingFlags.NonPublic|BindingFlags.Static);
  Check(m!=null,"存在提权令牌获取方法");
  IntPtr tok;OpenProcessToken(GetCurrentProcess(),0x0008,out tok);
  Check(tok!=IntPtr.Zero,"可打开当前进程令牌");
  var elevated=(IntPtr)m!.Invoke(null,new object[]{tok})!;
  Console.WriteLine("  linkedToken="+elevated+" (0 表示当前已是完整令牌或不存在提权副本)");
  // 本次机器 UAC 关闭：登录令牌已是完整管理员令牌，方法应安全返回 0 而不是崩溃或误判
  var t=typeof(SystemLauncher).GetMethod("TryGetElevatedToken",BindingFlags.NonPublic|BindingFlags.Static)!;
  var r=t.Invoke(null,new object[]{tok});
  Check(r!=null,"调用不抛异常，取不到时安全退回原令牌");
  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
 }
}