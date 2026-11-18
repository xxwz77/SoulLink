using System;using System.IO;using SoulLink.Services;
public static class SweeperCheck
{
 static int fails;static void Check(bool ok,string s){Console.WriteLine((ok?"ok   ":"FAIL ")+s);if(!ok)fails++;}
 public static int Main()
 {
  var root=AppIdentity.TempRoot;
  var job=Path.Combine(root,"jobs");var boot=Path.Combine(root,"boot");
  Directory.CreateDirectory(job);Directory.CreateDirectory(boot);
  var f1=Path.Combine(job,"job_stale.json");File.WriteAllText(f1,"{}");
  var f2=Path.Combine(boot,"stale.exe");File.WriteAllText(f2,"x");
  File.SetLastWriteTimeUtc(f1,DateTime.UtcNow.AddHours(-2));
  File.SetLastWriteTimeUtc(f2,DateTime.UtcNow.AddHours(-2));
  Console.WriteLine("  tempRoot="+root);
  Check(File.Exists(f1)&&File.Exists(f2),"准备过期残留文件");
  LaunchSweeper.Sweep();
  Check(!File.Exists(f1),"清理过期 job 文件");
  Check(!File.Exists(f2),"清理过期引导宿主");
  var fresh=Path.Combine(job,"job_fresh.json");File.WriteAllText(fresh,"{}");
  LaunchSweeper.Sweep();
  Check(File.Exists(fresh),"新鲜文件保留，不影响正在进行的投送");
  try{File.Delete(fresh);}catch{}
  Console.WriteLine(fails==0?"ALL PASSED":"FAILURES="+fails);return fails;
 }
}