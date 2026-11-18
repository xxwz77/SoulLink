using System;using System.IO;using System.Text;using SoulLink.Services;
public static class ProxyFreshCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 public static int Main()
 {
  string dir=Path.Combine(AppIdentity.TempRoot,"proxy");
  Directory.CreateDirectory(dir);
  string host=Path.Combine(dir,Path.GetFileNameWithoutExtension(SystemLauncher.ResolveSelfExe())+".proxy.exe");
  // 造一个“过期但大小相同”的副本：内容不同、时间戳更新——正是线上踩到的情形
  string self=SystemLauncher.ResolveSelfExe();
  var src=File.ReadAllBytes(self);
  var fake=(byte[])src.Clone();
  for(int i=1000;i<1400;i++) fake[i]=(byte)(fake[i]^0xFF);  // 改 400 字节，模拟旧版本
  File.WriteAllBytes(host,fake);
  File.SetLastWriteTimeUtc(host,DateTime.UtcNow.AddMinutes(5));
  ProxyHost.Invalidate();
  string got=ProxyHost.Resolve();
  Check(!string.IsNullOrEmpty(got)&&File.Exists(got),"解析到可用的代理副本");
  var now=File.ReadAllBytes(got);
  int diff=0;for(int i=0;i<Math.Min(now.Length,src.Length);i++) if(now[i]!=src[i]) diff++;
  Check(diff<=64,"过期副本被识别并重建（差异应仅为清单改写）");
  Console.WriteLine("  remainingDiff="+diff);
  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
 }
}