using System;using System.IO.Pipes;using System.Threading;using System.Threading.Tasks;using SoulLink.Services;
public static class LiveChannelCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 public static int Main()
 {
  int sid=97;
  string pipe="SoulLink_Relay_"+sid+"_deadbeef";
  // 1) 没有任何代理时不应报告就绪
  var none=BgiLogWatcher.DiscoverLiveAgentSessions();
  Check(!none.Contains(sid),"没有活代理时不算通道就绪");
  // 2) 建一个可连接的服务器管道，模拟活代理
  var server=new NamedPipeServerStream(pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
  var accept=Task.Run(()=>{try{server.WaitForConnection();}catch{}});
  Thread.Sleep(200);
  var live=BgiLogWatcher.DiscoverLiveAgentSessions();
  Check(live.Contains(sid),"可连通的代理被识别为通道就绪");
  // 3) 关掉后应立即不再报告
  try{server.Dispose();}catch{}
  Thread.Sleep(300);
  var after=BgiLogWatcher.DiscoverLiveAgentSessions();
  Check(!after.Contains(sid),"代理退出后不再报告通道就绪");
  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
 }
}