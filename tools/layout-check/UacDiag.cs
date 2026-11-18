using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
public static class UacDiag
{
 [DllImport("kernel32.dll")] static extern IntPtr WTSGetActiveConsoleSessionId();
 [DllImport("wtsapi32.dll", SetLastError=true)] static extern bool WTSQueryUserToken(uint sid, out IntPtr token);
 [DllImport("advapi32.dll", SetLastError=true)] static extern bool GetTokenInformation(IntPtr t,int cls,IntPtr buf,int len,out int ret);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
 static string ElevType(int t)=> t switch {1=>"Limited(标准令牌,有提权副本)",2=>"Full(已提权)",3=>"Default(无UAC/不受限)",_=>"未知("+t+")"};
 public static int Main()
 {
  var sb=new System.Text.StringBuilder();
  var lua=Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System","EnableLUA",-1);
  sb.AppendLine("EnableLUA="+lua+"  ("+(Convert.ToInt32(lua)==1?"UAC 开启":"UAC 关闭")+")");
  bool admin=new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
  sb.AppendLine("当前进程管理员="+admin+"  提权="+Environment.IsPrivilegedProcess);
  foreach(var sid in new uint[]{1,2,3,4,5})
  {
   if(!WTSQueryUserToken(sid,out IntPtr tok)){ sb.AppendLine("  会话"+sid+": 无登录用户或查询失败("+Marshal.GetLastWin32Error()+")"); continue; }
   try{
    IntPtr buf=Marshal.AllocHGlobal(4); int got;
    string elev="?";
    if(GetTokenInformation(tok,18,buf,4,out got)) elev=ElevType(Marshal.ReadInt32(buf));
    Marshal.FreeHGlobal(buf);
    IntPtr lb=Marshal.AllocHGlobal(IntPtr.Size);
    bool hasLinked=GetTokenInformation(tok,19,lb,IntPtr.Size,out got) && Marshal.ReadIntPtr(lb)!=IntPtr.Zero;
    Marshal.FreeHGlobal(lb);
    sb.AppendLine("  会话"+sid+": ElevationType="+elev+"  hasLinkedElevated="+hasLinked);
   } finally { CloseHandle(tok); }
  }
  System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"uacdiag.txt"),sb.ToString());
  return 0;
 }
}