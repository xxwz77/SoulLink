using System;using System.IO;using System.Text;using SoulLink.Services;
public static class ProxyHostCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 public static int Main()
 {
  string self=@"E:\Documents\deepseek-harness\SoulLink\dist\SoulLink.exe";
  var selfText=Encoding.UTF8.GetString(File.ReadAllBytes(self));
  Check(selfText.Contains("requireAdministrator"),"发布版主程序保留管理员清单（引导/RDP 需要）");
  string dst=Path.Combine(Path.GetTempPath(),"agent_"+Guid.NewGuid().ToString("N")+".exe");
  File.Copy(self,dst,true);
  long before=new FileInfo(dst).Length;
  bool ok=ManifestPatcher.MakeAsInvoker(dst);
  var t=Encoding.UTF8.GetString(File.ReadAllBytes(dst));
  Check(ok,"代理副本清单改写成功");
  Check(t.Contains("asInvoker")&&!t.Contains("requireAdministrator"),"代理副本为 asInvoker，普通用户令牌可启动（不再 740）");
  Check(new FileInfo(dst).Length==before,"等长替换，副本大小不变");
  File.Delete(dst);
  string agent=ProxyHost.Resolve();
  Check(!string.IsNullOrEmpty(agent),"ProxyHost.Resolve 返回可用路径");
  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
 }
}