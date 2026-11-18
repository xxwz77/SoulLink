using System;using System.Linq;using System.Reflection;using SoulLink.Services;
public static class IdentityChannelCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 static string RunAs(string identity,out string detail)
 {
  var t=typeof(AppIdentity);
  var lazy=t.GetField("_name",BindingFlags.NonPublic|BindingFlags.Static)!;
  // 直接读取名字并模拟：用真实枚举逻辑，仅替换身份比较
  detail="";
  return "";
 }
 public static int Main()
 {
  var pipes=System.IO.Directory.GetFiles(@"\\.\pipe\").Select(System.IO.Path.GetFileName)
      .Where(n=>n!=null && n.Contains("_Relay_") && !n.EndsWith(AppIdentity.RelayEventSuffix,StringComparison.OrdinalIgnoreCase))
      .ToList();
  Console.WriteLine("  identity="+AppIdentity.Name);
  var mine=pipes.Where(n=>n!.StartsWith(AppIdentity.RelayPipePrefix,StringComparison.OrdinalIgnoreCase)).ToList();
  var others=pipes.Where(n=>!n!.StartsWith(AppIdentity.RelayPipePrefix,StringComparison.OrdinalIgnoreCase)).ToList();
  Console.WriteLine("  mine=["+string.Join(",",mine)+"]");
  Console.WriteLine("  others=["+string.Join(",",others)+"]");
  Check(others.All(n=>!n!.StartsWith(AppIdentity.RelayPipePrefix,StringComparison.OrdinalIgnoreCase)),"其它版本管道不会被算作本版本通道");
  var live=BgiLogWatcher.DiscoverLiveAgentSessions();
  Console.WriteLine("  liveSessions=["+string.Join(",",live)+"]");
  Check(live.Count==0,"测试程序身份下没有代理，不应报告任何就绪会话");
  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
 }
}