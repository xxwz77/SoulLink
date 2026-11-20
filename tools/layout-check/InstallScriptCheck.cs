using System;
using System.IO;
public static class InstallScriptCheck
{
 public static int Main()
 {
  string script=Path.Combine("dist","tools","broker-install.ps1");
  if(!File.Exists(script)){Console.Error.WriteLine("发布脚本不存在");return 1;}
  string s=File.ReadAllText(script);
  if(s.Contains("@('SY','BA')")||s.Contains("'BU'")){Console.Error.WriteLine("仍有账户别名");return 2;}
  if(!s.Contains("S-1-5-18")||!s.Contains("S-1-5-32-544")||!s.Contains("S-1-5-32-545")){Console.Error.WriteLine("固定SID不全");return 3;}
  Console.WriteLine("ok 发布安装脚本仅使用固定SID");return 0;
 }
}