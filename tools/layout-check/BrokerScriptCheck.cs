using System;
using System.IO;
using SoulLink.Services;
public static class BrokerScriptCheck
{
 public static int Main()
 {
  string script=BrokerInstall.FindScript("install");
  Console.WriteLine("SCRIPT="+script);
  if(!File.Exists(script)){Console.Error.WriteLine("脚本不存在");return 1;}
  string text=File.ReadAllText(script);
  if(!text.Contains("SoulLink.DevBroker")||!text.Contains("SoulLink.Agent.exe"))return 2;
  Console.WriteLine("ok 单文件发布版从真实exe旁找到安装脚本");return 0;
 }
}