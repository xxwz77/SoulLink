using System;using System.Collections.Generic;using System.Linq;using System.Reflection;using SoulLink.Services;
public static class AgentNameCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 public static int Main(string[] args)
 {
  if(args.Length>0){ // 以 --identity=SoulLink 运行，走真实身份
    var live=BgiLogWatcher.DiscoverLiveAgentSessions();
    Console.WriteLine("  identity="+AppIdentity.Name+" live=["+string.Join(",",live)+"]");
    var pipes=System.IO.Directory.GetFiles(@"\\.\pipe\").Select(System.IO.Path.GetFileName)
        .Where(n=>n!=null&&n.StartsWith(AppIdentity.RelayPipePrefix)&&!n!.EndsWith("_Events")).ToList();
    Console.WriteLine("  ourPipes=["+string.Join(",",pipes)+"]");
    Check(pipes.Count>0,"存在本版本代理管道");
    Check(live.Count>0,"识别出运行中的代理（含 SoulLink.proxy 名字）");
    Check(!live.Contains(2)&&!live.Contains(3),"不把 SoulLink-Stable 的会话算进来");
    Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
  }
  Console.WriteLine("SKIP: 需要 --identity=SoulLink 才能验证真实身份");
  return 0;
 }
}