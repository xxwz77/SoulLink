using System;using SoulLink.Services;
public static class ChannelDiscoveryCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 public static int Main()
 {
  Check(BgiLogWatcher.IsRelayPipeName("SoulLink_Relay_4_12345678"),"测试版代理可识别");
  Check(BgiLogWatcher.IsRelayPipeName("SoulLink-Stable_Relay_5_abcdef12"),"挂机版代理可识别");
  Check(BgiLogWatcher.ParseRelaySessionId(@"C:\\SoulLink-Stable.exe --relay SoulLink-Stable_Relay_5_abcdef12")==5,"挂机版会话号可解析");
  Check(BgiLogWatcher.ParseRelaySessionId(@"C:\\SoulLink.exe --relay SoulLink_Relay_4_12345678")==4,"测试版会话号可解析");
  Check(!BgiLogWatcher.IsRelayPipeName("Other_Relay_4_123")&&BgiLogWatcher.ParseRelaySessionId("Other_Relay_4_123")==-1,"非本应用代理不纳入");
  var current=BgiLogWatcher.DiscoverChannelSessions();Console.WriteLine("当前共享通道会话："+string.Join(",",current));
  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
 }
}