using System;using System.IO.Pipes;using System.Threading;using SoulLink.Services;
public static class LiveChannelCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 public static int Main()
 {
  int sid=9000+new Random().Next(1,800);
  string pipe="SoulLink_Relay_"+sid+"_probe";
  // 只有管道没有进程：不能算就绪（避免残留误报），但也绝不能去连接它
  var server=new NamedPipeServerStream(pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
  var live=BgiLogWatcher.DiscoverLiveAgentSessions();
  Check(!live.Contains(sid),"只有管道、没有代理进程时不算就绪");
  // 关键：探测不能占用连接，服务器必须仍可正常接受连接
  bool canStillAccept=true;
  try{
    var accept=server.WaitForConnectionAsync();
    using var client=new NamedPipeClientStream(".",pipe,PipeDirection.InOut);
    client.Connect(500);
    canStillAccept=accept.Wait(500);
  }catch{canStillAccept=false;}
  Check(canStillAccept,"状态探测不会占用代理管道（这是绿一下就消失的原因）");
  try{server.Dispose();}catch{}
  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
 }
}